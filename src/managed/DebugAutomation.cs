using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity.UseObject;
using VRageMath;
using VRageRender;
using VRageRender.ExternalApp;

namespace SpaceEngineers.RTHWAccel
{
    // [Debug] options for unattended test runs through the shared queue:
    // load a world, run the phase 0b inspector sequence, quit.
    internal static class DebugAutomation
    {
        public static void Run(Settings settings)
        {
            try
            {
                if (!string.IsNullOrEmpty(settings.AutoLoadWorld))
                {
                    string world = FindWorld(settings.AutoLoadWorld);
                    if (world == null)
                    {
                        Log.Error("Debug: world not found: " + settings.AutoLoadWorld);
                        return;
                    }
                    Log.Info("Debug: loading world " + world);
                    MySandboxGame.Static.Invoke(() => MySessionLoader.LoadSingleplayerSession(world), Plugin.Name);
                    if (!Plugin.WaitUntil(() => MySession.Static != null && MySession.Static.Ready, TimeSpan.FromMinutes(5)))
                        Log.Warning("Debug: the world did not finish loading in time.");
                    else
                        Log.Info("Debug: world ready.");
                    // Let streaming settle (textures, voxel LODs) before measuring.
                    Thread.Sleep(TimeSpan.FromSeconds(10));
                    if (settings.SeatInCockpit)
                        SeatInCockpit();
                    if (settings.FreeCamera && settings.AutoQuitSeconds > 0)
                    {
                        CheckFreeCamera(settings);
                        if (settings.TracingToggleKey != 0)
                            CheckTracingToggle(settings);
                    }
                    if (settings.InspectorSequence)
                        InspectorSequence(settings);
                }
                if (settings.AutoQuitSeconds > 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(settings.AutoQuitSeconds));
                    Log.Info("Debug: quitting.");
                    NativeApi.RtShutdown();
                    MySandboxGame.ExitThreadSafe();
                    // A test run must end by itself; the game's exit can stall
                    // with its window in the background (HighFPSFix).
                    Thread.Sleep(TimeSpan.FromSeconds(20));
                    Log.Warning("Debug: the game did not exit within 20 s; ending the process.");
                    Process.GetCurrentProcess().Kill();
                }
            }
            catch (Exception exception)
            {
                Log.Error("Debug automation failed", exception);
            }
        }

        // Phase 2: the game's own frame and its G-buffer normals, then primary
        // rays through the scene mirror (parts, depth mismatch against the
        // G-buffer, faces, clay, materials and how objects reach the mirror;
        // the material views log the [Debug] Probes pixels) with screenshots
        // from the same camera, the mismatch again while the spectator orbits
        // the player, and timing windows; the native log reports each window's
        // statistics when the next one starts.
        private static void InspectorSequence(Settings settings)
        {
            Log.Info("Inspector: sequence starts.");
            if (settings.Probes.Length >= 2)
                NativeApi.RtSetProbes(settings.Probes);
            Step(GateMode.Off, null, 6, "game");
            Step(GateMode.Replace, DebugView.Normals, 6, "normals");
            Step(GateMode.Replace, DebugView.Parts, 8, "parts");
            Step(GateMode.Replace, DebugView.Mismatch, 8, "mismatch");
            Step(GateMode.Replace, DebugView.Faces, 6, "faces");
            Step(GateMode.Replace, DebugView.Clay, 8, "clay");
            Step(GateMode.Replace, DebugView.BaseColor, 6, "basecolor");
            Step(GateMode.Replace, DebugView.Material, 8, "material");
            Step(GateMode.Replace, DebugView.MaterialCompare, 8, "materialcompare");
            Step(GateMode.Replace, DebugView.Paths, 8, "paths");
            // P2: traced light replaces the game's on its surfaces (the patched sun pass blends it in by alpha;
            // the game keeps its sky, fog and post-processing); averaged over time through reprojection.
            Step(GateMode.Overlay, DebugView.LitRaw, 8, "lit-raw");
            // The lit view as the Denoiser setting has it (auto: DLSS Ray Reconstruction where available), then the
            // same light with our filter.
            Step(GateMode.Overlay, DebugView.Lit, 12, "lit");
            Step(GateMode.Overlay, DebugView.LitFilter, 12, "lit-filter");
            // Full coverage (replace): the traced light on every surface pixel, the primary rays jittered (no depth
            // test against the game's G-buffer); Ray Reconstruction (as the setting resolves), then our filter.
            Step(GateMode.Replace, DebugView.Lit, 12, "lit-replace");
            Step(GateMode.Replace, DebugView.LitFilter, 12, "lit-filter-replace");
            // Emissive surfaces as lights alone (replace: without the game's local lights on top), then the same
            // without reusing light samples: the unbiased reference the reuse must match on average (the log's
            // sampled diffuse light, and the accumulated images).
            Step(GateMode.Replace, DebugView.Emitters, 8, "emitters");
            Step(GateMode.Replace, DebugView.EmittersReference, 8, "emitters-reference");
            // The same with P2p's light grid (lights drawn by power alone): the cells' lists' A/B (the log's
            // difference from the history per pixel and per 8x4 tile).
            Step(GateMode.Replace, DebugView.EmittersPowerGrid, 8, "emitters-power-grid");
            // The game's own frame again, a minute later: screens draw their content some time after loading.
            Step(GateMode.Off, null, 6, "game-late");
            // Skinned meshes: the player character seen from the front, posed on the GPU (the mismatch view
            // compares its depth with the game's G-buffer), then its traced light and the game's own frame.
            FaceTheCharacter(2.5);
            Step(GateMode.Replace, DebugView.Mismatch, 8, "mismatch-character");
            Step(GateMode.Replace, DebugView.Clay, 6, "clay-character");
            // Its motion: the idle animation's (the previous pose's positions), small and smooth.
            Step(GateMode.Replace, DebugView.Motion, 6, "motion-character");
            Step(GateMode.Overlay, DebugView.Lit, 10, "lit-character");
            Step(GateMode.Replace, DebugView.Lit, 10, "lit-replace-character");
            Step(GateMode.Replace, DebugView.Emitters, 8, "emitters-character");
            Step(GateMode.Replace, DebugView.EmittersReference, 8, "emitters-reference-character");
            Step(GateMode.Replace, DebugView.EmittersPowerGrid, 8, "emitters-power-grid-character");
            Step(GateMode.Off, null, 6, "game-character");
            StartOrbit(10.0, 15.0);
            Step(GateMode.Replace, DebugView.Mismatch, 12, "mismatch-moving");
            Step(GateMode.Replace, DebugView.Parts, 10, "parts-moving");
            Step(GateMode.Replace, DebugView.Clay, 10, "clay-moving");
            Step(GateMode.Overlay, DebugView.LitRaw, 8, "lit-raw-moving");
            Step(GateMode.Overlay, DebugView.Lit, 10, "lit-moving");
            Step(GateMode.Overlay, DebugView.LitFilter, 10, "lit-filter-moving");
            Step(GateMode.Replace, DebugView.Lit, 10, "lit-replace-moving");
            Step(GateMode.Off, null, 10, "off-moving");
            // Object motion: back in the player's own view on the ship, the ship drifts and turns (the cockpit
            // case: its interior moves with the camera while the world streams past); the light history follows
            // its surfaces through their previous transforms. Then the game's own frame, still moving.
            s_orbit = false;
            ReturnToPlayerView();
            Thread.Sleep(2000);
            StartGridMotion(new Vector3(1, 0, 0), new Vector3(0, 0.05f, 0));
            Step(GateMode.Overlay, DebugView.LitRaw, 12, "lit-raw-grid-moving");
            Step(GateMode.Replace, DebugView.Motion, 6, "motion-grid-moving");
            Step(GateMode.Off, null, 6, "off-grid-moving");
            StopGridMotion();
            Step(GateMode.RoundTrip, null, 10, null);
            Step(GateMode.Replace, DebugView.Stripes, 10, null);
            s_orbit = false;
            NativeApi.RtSetDebugView(settings.DebugView);
            NativeApi.RtSetGateMode(settings.GateMode);
            Thread.Sleep(1000);
            Log.Info("Inspector: sequence done.");

            void Step(GateMode mode, DebugView? view, int seconds, string capture)
            {
                Log.Info($"Inspector: gate mode {mode}{(view != null ? ", debug view " + view : "")} for {seconds} s.");
                if (view != null)
                    NativeApi.RtSetDebugView(view.Value);
                NativeApi.RtSetGateMode(mode);
                Thread.Sleep(TimeSpan.FromSeconds(seconds - 2));
                if (capture != null)
                    Capture(settings, capture);
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }

        private static volatile bool s_orbit;

        // The spectator camera circles the player once per `period` seconds at
        // `radius` metres (HighFPSFix's automation), driven once per simulation
        // step on the update thread: a moving camera for the reprojection check.
        private static void StartOrbit(double period, double radius)
        {
            s_orbit = true;
            Vector3D center = Vector3D.Zero;
            long step = 0;
            MySandboxGame.Static.Invoke(() =>
            {
                MySession session = MySession.Static;
                center = session?.ControlledEntity?.Entity?.PositionComp?.GetPosition() ?? Vector3D.Zero;
                session?.SetCameraController(MyCameraControllerEnum.Spectator, null,
                    center + new Vector3D(radius, radius * 0.2, 0));
                Log.Info($"Inspector: spectator orbit, radius {radius} m, {period} s per turn.");
            }, Plugin.Name);
            MySandboxGame.Static.Invoke(() =>
            {
                MySpectatorCameraController spectator = MySpectatorCameraController.Static;
                if (!s_orbit || spectator == null)
                    return;
                double angle = 2.0 * Math.PI * step++ / (60.0 * period);
                spectator.Position = center + new Vector3D(Math.Cos(angle) * radius, radius * 0.2, Math.Sin(angle) * radius);
                spectator.SetTarget(center, Vector3D.Up);
            }, Plugin.Name, -1, int.MaxValue);
        }

        private static volatile bool s_gridMotion;

        // The spectator camera `distance` metres in front of the player character, looking at its chest.
        private static void FaceTheCharacter(double distance)
        {
            MySandboxGame.Static.Invoke(() =>
            {
                MySession session = MySession.Static;
                MyCharacter character = session?.LocalCharacter;
                if (character == null)
                {
                    Log.Info("Inspector: no player character.");
                    return;
                }
                MatrixD world = character.WorldMatrix;
                Vector3D chest = world.Translation + world.Up * 1.2;
                Vector3D eye = chest + world.Forward * distance + world.Up * 0.3;
                session.SetCameraController(MyCameraControllerEnum.Spectator, null, eye);
                MySpectatorCameraController spectator = MySpectatorCameraController.Static;
                if (spectator != null)
                {
                    spectator.Position = eye;
                    spectator.SetTarget(chest, world.Up);
                }
                Log.Info($"Inspector: spectator {distance} m in front of the player character" +
                         $"{(character.IsSitting ? " (seated)" : "")}.");
            }, Plugin.Name);
        }

        // [Debug] SeatInCockpit: the player character sits in the nearest free cockpit (the game's own use
        // action, as pressing F at it; the mod API's attach if that does not seat it), so that the first views
        // look out through its canopy, whose glass the rays must pass.
        private static void SeatInCockpit()
        {
            MyCockpit cockpit = null;
            MyCharacter character = null;
            bool searched = false;
            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    character = MySession.Static?.LocalCharacter;
                    if (character == null)
                        return;
                    Vector3D position = character.PositionComp.GetPosition();
                    double nearest = double.MaxValue;
                    foreach (var entity in MyEntities.GetEntities())
                    {
                        if (!(entity is MyCubeGrid grid))
                            continue;
                        foreach (var block in grid.GetFatBlocks())
                        {
                            double distance = Vector3D.DistanceSquared(block.PositionComp.GetPosition(), position);
                            if (block is MyCockpit candidate && candidate.Pilot == null && distance < nearest)
                            {
                                nearest = distance;
                                cockpit = candidate;
                            }
                        }
                    }
                    cockpit?.RequestUse(UseActionEnum.Manipulate, character);
                }
                finally
                {
                    searched = true;
                }
            }, Plugin.Name);
            if (!Plugin.WaitUntil(() => searched, TimeSpan.FromSeconds(10)) || character == null || cockpit == null)
            {
                Log.Warning(character == null ? "Inspector: no player character to seat." : "Inspector: no free cockpit.");
                return;
            }
            if (!Plugin.WaitUntil(() => cockpit.Pilot == character, TimeSpan.FromSeconds(5)))
            {
                MySandboxGame.Static.Invoke(() => ((Sandbox.ModAPI.IMyCockpit)cockpit).AttachPilot(character), Plugin.Name);
                Plugin.WaitUntil(() => cockpit.Pilot == character, TimeSpan.FromSeconds(5));
            }
            Log.Info(cockpit.Pilot == character
                ? $"Inspector: seated in {cockpit.BlockDefinition.Id.SubtypeName} of {cockpit.CubeGrid.DisplayName}."
                : "Inspector: the player character did not take the cockpit.");
            // The camera's move into the seat (the sit animation) settles.
            Thread.Sleep(TimeSpan.FromSeconds(3));
        }

        // [Debug] FreeCamera in an unattended run: what the world alone allows, then the free spectator key's own
        // handler (F8: MyGuiScreenGamePlay.SetSpectatorFree) and where it left the camera, a shot from there as the
        // screenshot key takes it (when one is set); back on the player after.
        private static void CheckFreeCamera(Settings settings)
        {
            bool done = false;
            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    MySession session = MySession.Static;
                    if (session == null)
                        return;
                    bool gameAllows = FreeCamera.GameAllows();
                    MyCameraControllerEnum before = session.GetCameraControllerEnum();
                    Sandbox.Game.Gui.MyGuiScreenGamePlay.SetSpectatorFree();
                    MyCameraControllerEnum after = session.GetCameraControllerEnum();
                    Log.Info($"Free camera check: the world alone allows the spectator: {gameAllows} (survival " +
                             $"{session.SurvivalMode}, its spectator setting {session.Settings.EnableSpectator}); the " +
                             $"F8 handler moved the camera from {before} to {after}.");
                    if (after == MyCameraControllerEnum.Spectator)
                    {
                        float fov = MySector.MainCamera?.FieldOfView ?? 0;
                        float zoomed = FreeCamera.ZoomNow(240);
                        Log.Info($"Free camera check: two wheel notches forward zoom from {MathHelper.ToDegrees(fov):F1} " +
                                 $"to {MathHelper.ToDegrees(zoomed):F1} degrees.");
                    }
                }
                catch (Exception exception)
                {
                    Log.Error("Free camera check failed", exception);
                }
                finally
                {
                    done = true;
                }
            }, Plugin.Name);
            Plugin.WaitUntil(() => done, TimeSpan.FromSeconds(10));
            Thread.Sleep(TimeSpan.FromSeconds(2));
            if (settings.ScreenshotKey != 0)
                CheckScreenshotKey(settings);
            ReturnToPlayerView();
            Thread.Sleep(TimeSpan.FromSeconds(1));
            MySandboxGame.Static.Invoke(() =>
                Log.Info($"Free camera check: back on the player, the field of view is " +
                         $"{MathHelper.ToDegrees(MySector.MainCamera?.FieldOfView ?? 0):F1} degrees (the options' " +
                         $"{MathHelper.ToDegrees(MySandboxGame.Config.FieldOfView):F1})."), Plugin.Name);
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }

        // [Debug] ScreenshotKey in an unattended run: a shot as the key takes it and the PNG it wrote.
        private static void CheckScreenshotKey(Settings settings) => KeepShot(settings, "screenshot-key");

        // [Debug] TracingToggleKey in an unattended run, from the player's view: a shot with the traced light
        // switched off as the key does it (the game's own look), and one after switching it on again.
        private static void CheckTracingToggle(Settings settings)
        {
            GateMode off = Hotkeys.ToggleNow();
            Thread.Sleep(TimeSpan.FromSeconds(2));
            string offShot = settings.ScreenshotKey != 0 ? KeepShot(settings, "toggle-off") : null;
            GateMode on = Hotkeys.ToggleNow();
            Thread.Sleep(TimeSpan.FromSeconds(4));  // the traced light's history fills again
            string onShot = settings.ScreenshotKey != 0 ? KeepShot(settings, "toggle-on") : null;
            Log.Info($"Tracing toggle check: switched to {off} (shot {offShot ?? "none"}), then to {on} (shot " +
                     $"{onShot ?? "none"}).");
        }

        // A shot as the screenshot key takes it; the PNG it wrote is moved into the run's captures as <name>.png,
        // so that the game folder's Screenshots keeps only the player's own. The kept path, or null.
        private static string KeepShot(Settings settings, string name)
        {
            string path = Hotkeys.TakeNow();
            if (path == null)
            {
                Log.Warning($"Screenshot key check ({name}): no shot taken.");
                return null;
            }
            if (!Plugin.WaitUntil(() => File.Exists(path) && new FileInfo(path).Length > 0, TimeSpan.FromSeconds(10)))
            {
                Log.Warning($"Screenshot key check ({name}): {path} was not written within 10 s.");
                return null;
            }
            Thread.Sleep(500);  // the render thread finishes the file
            long size = new FileInfo(path).Length;
            string kept = path;
            if (!string.IsNullOrEmpty(settings.CapturePath))
            {
                Directory.CreateDirectory(settings.CapturePath);
                kept = Path.Combine(settings.CapturePath, name + ".png");
                File.Copy(path, kept, true);
                File.Delete(path);
            }
            Log.Info($"Screenshot key check ({name}): a {size / 1024} KB PNG written in {Path.GetDirectoryName(path)}, " +
                     $"kept as {kept}.");
            return kept;
        }

        // The camera back on the controlled entity (the player's first-person view).
        private static void ReturnToPlayerView()
        {
            MySandboxGame.Static.Invoke(() =>
            {
                MySession session = MySession.Static;
                var entity = session?.ControlledEntity?.Entity;
                if (entity != null)
                    session.SetCameraController(MyCameraControllerEnum.Entity, entity);
                Log.Info(entity != null ? "Inspector: camera back on the player." : "Inspector: no controlled entity.");
            }, Plugin.Name);
        }

        // The largest movable grid within 100 m of the player drifts and turns (m/s, rad/s), set again every
        // simulation step (against its dampeners) until StopGridMotion: moving objects for the history's
        // motion vectors. The test world is a fresh copy for every run (run_session.ps1).
        private static void StartGridMotion(Vector3 linear, Vector3 angular)
        {
            s_gridMotion = true;
            MyCubeGrid grid = null;
            MySandboxGame.Static.Invoke(() =>
            {
                Vector3D? center = MySession.Static?.ControlledEntity?.Entity?.PositionComp?.GetPosition();
                if (center == null)
                    return;
                foreach (var entity in MyEntities.GetEntities())
                {
                    if (entity is MyCubeGrid candidate && candidate.Physics != null && !candidate.IsStatic &&
                        Vector3D.Distance(candidate.PositionComp.WorldAABB.Center, center.Value) < 100 &&
                        (grid == null || candidate.BlocksCount > grid.BlocksCount))
                        grid = candidate;
                }
                Log.Info(grid != null
                    ? $"Inspector: grid {grid.DisplayName} ({grid.BlocksCount} blocks) moving at {linear} m/s, turning at {angular} rad/s."
                    : "Inspector: no movable grid near the player.");
            }, Plugin.Name);
            MySandboxGame.Static.Invoke(() =>
            {
                if (!s_gridMotion || grid?.Physics == null)
                    return;
                grid.Physics.LinearVelocity = linear;
                grid.Physics.AngularVelocity = angular;
            }, Plugin.Name, -1, int.MaxValue);
        }

        private static void StopGridMotion()
        {
            s_gridMotion = false;
            MySandboxGame.Static.Invoke(() =>
            {
                Vector3D? center = MySession.Static?.ControlledEntity?.Entity?.PositionComp?.GetPosition();
                if (center == null)
                    return;
                foreach (var entity in MyEntities.GetEntities())
                {
                    if (entity is MyCubeGrid grid && grid.Physics != null && !grid.IsStatic &&
                        Vector3D.Distance(grid.PositionComp.WorldAABB.Center, center.Value) < 200)
                    {
                        grid.Physics.LinearVelocity = Vector3.Zero;
                        grid.Physics.AngularVelocity = Vector3.Zero;
                    }
                }
            }, Plugin.Name);
        }

        // The game's own screenshot (no HUD), written by its render thread.
        private static void Capture(Settings settings, string name)
        {
            if (string.IsNullOrEmpty(settings.CapturePath))
                return;
            Directory.CreateDirectory(settings.CapturePath);
            string path = Path.Combine(settings.CapturePath, name + ".png");
            MySandboxGame.Static.Invoke(() => MyRenderProxy.TakeScreenshot(Vector2.One, path, false, true, false),
                Plugin.Name);
            Log.Info("Inspector: screenshot requested: " + path);
        }

        // Runs `apply` (which posts a shader reload) and reports the longest
        // render frame of the next 15 seconds: the reload's hitch.
        public static void MeasureReload(Func<bool> apply)
        {
            MyRenderThread thread = MyRenderProxy.RenderThread;
            var clock = Stopwatch.StartNew();
            long last = 0;
            long longest = 0;
            Action onDraw = () =>
            {
                long now = clock.ElapsedTicks;
                if (last != 0)
                    longest = Math.Max(longest, now - last);
                last = now;
            };
            if (thread != null)
                thread.BeforeDraw += onDraw;
            try
            {
                if (!apply())
                    return;
                Thread.Sleep(TimeSpan.FromSeconds(15));
            }
            finally
            {
                if (thread != null)
                    thread.BeforeDraw -= onDraw;
            }
            Log.Info($"Shader reload: the longest render frame in the 15 s after the request took " +
                     $"{longest * 1000.0 / Stopwatch.Frequency:F0} ms.");
        }

        // A full path, or a world folder name under Saves\<user id>\.
        private static string FindWorld(string world)
        {
            if (Path.IsPathRooted(world))
                return System.IO.Directory.Exists(world) ? world : null;
            string saves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", "Saves");
            if (!System.IO.Directory.Exists(saves))
                return null;
            foreach (string user in System.IO.Directory.GetDirectories(saves))
            {
                string candidate = Path.Combine(user, world);
                if (System.IO.Directory.Exists(candidate))
                    return candidate;
            }
            return null;
        }
    }
}
