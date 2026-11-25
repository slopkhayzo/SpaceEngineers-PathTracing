using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Sandbox;
using Sandbox.Game.Screens;
using Sandbox.Graphics.GUI;

namespace SpaceEngineers.RTHWAccel
{
    // Managed entry point, run by the native plugin in the game's default
    // AppDomain once the game is running (Start uses no game types; the startup
    // thread waits for the game's assemblies).
    public static class Plugin
    {
        public const string Name = "SpaceEngineers.RTHWAccel";

        internal static Settings Settings { get; private set; }
        internal static string Directory { get; private set; }

        private static int s_started;

        // ICLRRuntimeHost::ExecuteInDefaultAppDomain needs static int(string).
        public static int Start(string argument)
        {
            if (Interlocked.Exchange(ref s_started, 1) != 0)
                return 0;
            Dictionary<string, string> arguments = ParseArguments(argument);
            string bindError = NativeApi.Bind(arguments.TryGetValue("api", out string api)
                ? new IntPtr(long.Parse(api.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                : IntPtr.Zero);
            if (bindError != null)
                return 2;

            try
            {
                Directory = arguments.TryGetValue("dir", out string directory) ? directory : "";
                Settings = Settings.Load(arguments.TryGetValue("ini", out string ini) ? ini : "");
                Log.Diagnostics = Settings.Diagnostics || NativeApi.DiagnosticsEnabled;
                Log.Info($"Managed part running on .NET {Environment.Version}; {Settings}");

                string selfTest = Detour.SelfTest();
                if (selfTest != null)
                {
                    Log.Error("Hooking does not work in this process (" + selfTest + "); the game runs unmodified.");
                    return 3;
                }
                Log.Info("Detour self-test passed.");

                var startup = new Thread(StartupThread) { IsBackground = true, Name = Name + " startup" };
                startup.Start();
                return 0;
            }
            catch (Exception exception)
            {
                Log.Error("Managed start failed", exception);
                return 1;
            }
        }

        private static void StartupThread()
        {
            try
            {
                if (WaitForAssembly("Sandbox.Game", TimeSpan.FromMinutes(5)) == null)
                {
                    Log.Error("Sandbox.Game was never loaded; the game runs unmodified.");
                    return;
                }
                Assembly render11 = WaitForAssembly("VRage.Render11", TimeSpan.FromMinutes(5));
                if (render11 == null)
                {
                    Log.Error("VRage.Render11 was never loaded; the game runs unmodified.");
                    return;
                }
                if (!RayTracing.Start(Settings, render11))
                    return;
                // The options' "Path tracing" button as soon as the core runs (not after the main menu and the shader
                // patch: a player opening the options early found none).
                try
                {
                    OptionsMenu.Install(Settings);
                }
                catch (Exception exception)
                {
                    Log.Error("The \"Path tracing\" options screen could not be added to the game's options", exception);
                }

                // The shader reload is applied at the main menu, before any world loads.
                if (!WaitUntil(() => MySandboxGame.IsGameReady && MySandboxGame.Static != null, TimeSpan.FromMinutes(5)))
                    Log.Warning("The game did not report ready within 5 minutes.");
                if (!WaitUntil(() => MyScreenManager.GetScreenWithFocus() is MyGuiScreenMainMenuBase, TimeSpan.FromMinutes(2)))
                    Log.Warning("The main menu did not come up; continuing.");
                Thread.Sleep(1000);
                if (Settings.PatchShaders)
                    DebugAutomation.MeasureReload(() => ShaderPatcher.Apply(render11));
                if (Settings.FreeCamera)
                {
                    try
                    {
                        FreeCamera.Install();
                    }
                    catch (Exception exception)
                    {
                        Log.Error("The free camera could not be installed", exception);
                    }
                }
                Hotkeys.Start(Settings);

                DebugAutomation.Run(Settings);
            }
            catch (Exception exception)
            {
                Log.Error("Startup failed; features that were not installed stay off", exception);
            }
        }

        internal static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (condition())
                        return true;
                }
                catch (Exception)
                {
                    // Game state read from another thread mid-change.
                }
                Thread.Sleep(100);
            }
            return false;
        }

        private static Assembly WaitForAssembly(string name, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                Assembly found = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
                if (found != null)
                    return found;
                Thread.Sleep(10);
            }
            return null;
        }

        private static Dictionary<string, string> ParseArguments(string argument)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (argument ?? "").Split(';'))
            {
                int equals = part.IndexOf('=');
                if (equals > 0)
                    result[part.Substring(0, equals).Trim()] = part.Substring(equals + 1).Trim();
            }
            return result;
        }
    }

    // Starts the native ray-tracing core on the game's device and hooks the lighting.
    internal static class RayTracing
    {
        // The presenter's window is made on the thread of the game's window (a WinForms form with a thread of its
        // own, not the game's main thread): through the form's BeginInvoke.
        private static void AttachPresenter(IntPtr window)
        {
            System.Windows.Forms.Control form = System.Windows.Forms.Control.FromHandle(window);
            if (form == null)
            {
                Log.Warning("Presenter: the game's window is no WinForms form here; the game's swap chain presents.");
                return;
            }
            form.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!NativeApi.RtPresenterAttach())
                        Log.Warning("Presenter: no window over the game's (see above); the game's swap chain presents.");
                }
                catch (Exception exception)
                {
                    Log.Error("Presenter: making its window failed; the game's swap chain presents", exception);
                }
            }));
        }

        // The game's frames presented through the native core's D3D12 swap chain on the game's window (the base of
        // frame generation): the game's swap chain (MyRender11.m_swapchain) handed to the core, which hooks its
        // Present (the first time) and presents in `mode` (NativeApi.RtSetPresenter). False when not set.
        private static bool StartPresenter(Type render, int mode)
        {
            try
            {
                var swapChain = Compiled.StaticGetter<object>(render, "m_swapchain")() as SharpDX.ComObject;
                if (swapChain == null || swapChain.NativePointer == IntPtr.Zero)
                {
                    Log.Warning("Presenter: the game has no swap chain yet; it presents as usual.");
                    return false;
                }
                if (!NativeApi.RtSetPresenter(swapChain.NativePointer, mode, out IntPtr window))
                {
                    Log.Warning("Presenter: the native core could not take over the presentation (see above).");
                    return false;
                }
                if (!s_presenterStarted)
                    AttachPresenter(window);
                s_presenterStarted = true;
                return true;
            }
            catch (Exception exception)
            {
                Log.Error("Presenter: the game's swap chain could not be read; it presents as usual", exception);
                return false;
            }
        }

        private static Type s_render;            // MyRender11, once the core started
        private static bool s_presenterStarted;  // its Present hooked and our window asked for

        // The Path tracing screen's switch, while playing: DLSS Frame Generation on (the presenter started on first
        // use, through Streamline) or off (the game's swap chain presents again; ours, and DLSS-G's resources, are
        // let go). Returns NativeApi.RtFrameGenStatus's answer (2: it cannot run; `note` says why) or 0.
        internal static int SetFrameGeneration(bool on, out string note)
        {
            note = "";
            if (s_render == null)
            {
                note = "the ray-tracing core is not running";
                return 2;
            }
            if (on || s_presenterStarted)
                StartPresenter(s_render, on ? 3 : 0);
            return on ? NativeApi.RtFrameGenStatus(out note) : 0;
        }

        public static bool Start(Settings settings, Assembly render11)
        {
            Type render = GameReflection.Type(render11, "VRageRender.MyRender11");
            PropertyInfo deviceProperty = GameReflection.Property(render, "DeviceInstance");
            SharpDX.Direct3D11.Device device = null;
            if (!Plugin.WaitUntil(() => (device = deviceProperty.GetValue(null) as SharpDX.Direct3D11.Device) != null,
                    TimeSpan.FromMinutes(5)))
            {
                Log.Error("The game's D3D11 device never appeared; ray tracing stays off.");
                return false;
            }
            int status = NativeApi.RtInitialize(device.NativePointer, settings.GateMode, settings.DebugView,
                settings.PatternStrength, settings.TracePath, settings.Denoiser, settings.CharacterLights,
                settings.LampIntensity, settings.EmissiveIntensity, settings.DecalLights, settings.LightTree,
                settings.DlssMode);
            if (status != 0)
            {
                Log.Error($"The ray-tracing core did not start (step {status}, see above); the game renders as usual.");
                return false;
            }
            if (settings.HandOffOrder.Length != 0)
            {
                int order = settings.HandOffOrder == "after" ? 0 : settings.HandOffOrder == "alternate" ? 2 : 1;
                NativeApi.RtSetOption(RtOption.HandOffOrder, order);
                Log.Info("Gate: D3D12 commands recorded " +
                         $"{(order == 0 ? "after" : order == 1 ? "before" : "by turns before and after")} the hand-off.");
            }
            if (settings.FrameLimit > 0)
                NativeApi.RtSetOption(RtOption.FrameLimit, settings.FrameLimit);
            if (settings.PresentSync >= 0)
                NativeApi.RtSetOption(RtOption.PresentSync, settings.PresentSync);
            if (settings.FrameGenReflex >= 0)
                NativeApi.RtSetOption(RtOption.ReflexMode, settings.FrameGenReflex);
            if (settings.SampleThreads)
                NativeApi.RtSetOption(RtOption.SampleThreads, 1);
            s_render = render;
            if (settings.Presenter == "d3d12" || settings.FrameGeneration != "off")
                StartPresenter(render, settings.FrameGeneration == "on" ? 3 : settings.FrameGeneration == "streamline" ? 2 : 1);
            try
            {
                GameFrame.Resolve(render11);
                if (GameFrame.Size != GameFrame.ExpectedSize)
                    Log.Warning($"The game's frame constants are {GameFrame.Size} bytes, not {GameFrame.ExpectedSize}; traced lighting uses defaults.");
            }
            catch (Exception exception)
            {
                Log.Error("The game's frame constants cannot be read; traced lighting uses defaults", exception);
            }
            LightingHooks.Install(render11);
            if (settings.SceneMirror)
            {
                try
                {
                    SceneMirror.Install(render11, settings.MirrorTransparent);
                    WalkWorkers.Start(settings.WalkWorkers);
                    SceneMirror.AlternateWalk = settings.WalkAlternate;
                    Log.Info($"Scene mirror: the walk reads the game's objects on {WalkWorkers.Count} worker threads " +
                             $"and the render thread{(settings.WalkAlternate ? " by turns with the render thread alone" : "")}.");
                }
                catch (Exception exception)
                {
                    // The gate and the debug views keep working without the mirror.
                    Log.Error("The scene mirror could not be installed; traced views stay empty", exception);
                }
            }
            return true;
        }
    }
}
