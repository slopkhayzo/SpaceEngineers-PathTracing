using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Sandbox;
using VRageMath;
using VRageRender;

namespace SpaceEngineers.RTHWAccel
{
    // [Debug] keys for screenshots (F12 and F2 in the development INI, single keys that move nothing: the free
    // camera flies with Ctrl, Shift and Alt; the game's other F-keys are its controls or debug screens, and F12 is
    // also Steam's screenshot key):
    // - ScreenshotKey saves the frame without the HUD as a PNG in the game folder's Screenshots (beside Bin64;
    //   %LOCALAPPDATA%\SpaceEngineers.RTHWAccel\Screenshots when that is not writable). The game's own F4 keeps
    //   the HUD and saves to %APPDATA%\SpaceEngineers\Screenshots. The shot is the game's own
    //   (MyRenderProxy.TakeScreenshot on the game thread, written by its render thread), with its "screenshot
    //   saved" note, which the picture leaves out with the rest of the HUD.
    // - TracingToggleKey switches the traced light off (gate off: the game's own look, its lamps unscaled too,
    //   ShaderPatch.LampShare) and on again (the INI's gate mode); a note says which.
    // A background thread polls the keys while one of the game's windows is in front; a key counts when it goes
    // down with exactly its modifiers held.
    internal static class Hotkeys
    {
        private const int PollMilliseconds = 15;
        private const int VkShift = 0x10;
        private const int VkControl = 0x11;
        private const int VkMenu = 0x12;

        private struct Binding
        {
            public int Combo;
            public Action Action;
        }

        private static string s_folder;
        private static GateMode s_traced = GateMode.Replace;

        // The traced mode the toggle key returns to, as the options screen last chose it.
        internal static void SetTracedMode(GateMode mode)
        {
            if (mode != GateMode.Off)
                s_traced = mode;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        public static void Start(Settings settings)
        {
            var bindings = new List<Binding>();
            if (Usable(settings.ScreenshotKeyName, settings.ScreenshotKey, "ScreenshotKey"))
            {
                s_folder = Folder();
                bindings.Add(new Binding { Combo = settings.ScreenshotKey, Action = () => Take(s_folder) });
                Log.Info($"Screenshot key: {settings.ScreenshotKeyName} saves the frame without the HUD as a PNG in " +
                         $"{s_folder}.");
            }
            if (Usable(settings.TracingToggleKeyName, settings.TracingToggleKey, "TracingToggleKey"))
            {
                if (settings.GateMode != GateMode.Off)
                    s_traced = settings.GateMode;
                bindings.Add(new Binding { Combo = settings.TracingToggleKey, Action = () => ToggleTracing() });
                Log.Info($"Tracing toggle key: {settings.TracingToggleKeyName} switches the traced light off (the " +
                         $"game's own look) and on again ({s_traced}).");
            }
            if (bindings.Count == 0)
                return;
            Binding[] all = bindings.ToArray();
            var thread = new Thread(() => Poll(all))
            {
                IsBackground = true,
                Name = Plugin.Name + " hotkeys",
            };
            thread.Start();
        }

        // A shot as the screenshot key takes it, for the automated checks: its path (null without the key, or if
        // it failed).
        public static string TakeNow() => s_folder == null ? null : Take(s_folder);

        // The tracing toggle as its key works it, for the automated checks: the gate mode it switched to.
        public static GateMode ToggleNow() => ToggleTracing();

        private static bool Usable(string name, int combo, string key)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (combo != 0)
                return true;
            Log.Warning($"{key}: '{name}' is not a key (F1-F24, A-Z, 0-9, Pause, ScrollLock, after Ctrl+, Shift+, " +
                        "Alt+ if wanted); no such key.");
            return false;
        }

        // The game folder's Screenshots, or the plugin's folder under %LOCALAPPDATA% when that cannot be written.
        private static string Folder()
        {
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Screenshots")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Plugin.Name,
                    "Screenshots"),
            };
            foreach (string folder in candidates)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    string probe = Path.Combine(folder, ".write-test");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                    return folder;
                }
                catch (Exception)
                {
                    // Not writable: the next candidate.
                }
            }
            return candidates[candidates.Length - 1];
        }

        private static void Poll(Binding[] bindings)
        {
            uint self = (uint)Process.GetCurrentProcess().Id;
            var wasDown = new bool[bindings.Length];
            while (true)
            {
                Thread.Sleep(PollMilliseconds);
                int modifiers = (Down(VkControl) ? Settings.ComboCtrl : 0) | (Down(VkShift) ? Settings.ComboShift : 0) |
                                (Down(VkMenu) ? Settings.ComboAlt : 0);
                for (int i = 0; i < bindings.Length; ++i)
                {
                    bool down = Down(bindings[i].Combo & 0xFF);
                    if (down && !wasDown[i] && (bindings[i].Combo & ~0xFF) == modifiers && InFront(self))
                    {
                        try
                        {
                            bindings[i].Action();
                        }
                        catch (Exception exception)
                        {
                            Log.Error("Hotkey", exception);
                        }
                    }
                    wasDown[i] = down;
                }
            }
        }

        private static bool Down(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        private static bool InFront(uint self)
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
            return process == self;
        }

        private static string Take(string folder)
        {
            try
            {
                string name = "SpaceEngineers_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
                string path = Path.Combine(folder, name + ".png");
                MySandboxGame game = MySandboxGame.Static;
                if (game == null)
                    return null;
                game.Invoke(() => MyRenderProxy.TakeScreenshot(Vector2.One, path, false, true, true), Plugin.Name);
                Log.Info("Screenshot: " + path);
                return path;
            }
            catch (Exception exception)
            {
                Log.Error("Screenshot key", exception);
                return null;
            }
        }

        private static GateMode ToggleTracing()
        {
            GateMode next = NativeApi.CurrentGateMode == GateMode.Off ? s_traced : GateMode.Off;
            NativeApi.RtSetGateMode(next);
            Log.Info($"Tracing toggle: gate mode {next}.");
            Notify(next == GateMode.Off ? "Path tracing off: the game's own lighting" : "Path tracing on");
            return next;
        }

        // A note on the HUD (screenshots leave it out).
        private static void Notify(string text)
        {
            MySandboxGame game = MySandboxGame.Static;
            game?.Invoke(() =>
            {
                try
                {
                    Sandbox.Game.MyVisualScriptLogicProvider.ShowNotification(text, 2000);
                }
                catch (Exception)
                {
                    // No session (the main menu): no note.
                }
            }, Plugin.Name);
        }
    }
}
