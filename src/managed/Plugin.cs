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
                settings.LampIntensity, settings.EmissiveIntensity, settings.DecalLights);
            if (status != 0)
            {
                Log.Error($"The ray-tracing core did not start (step {status}, see above); the game renders as usual.");
                return false;
            }
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
