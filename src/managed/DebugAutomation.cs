using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
                    if (settings.Foreground)
                        BringToFront();
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
                    if (settings.TestDecals)
                        DecalSequence(settings);
                    if (!string.IsNullOrWhiteSpace(settings.LookAtSkins))
                        SkinSequence(settings);
                    if (settings.TestMenu)
                        MenuSequence(settings);
                }
                if (settings.AutoQuitSeconds > 0)
                {
                    if (settings.CaptureWindow)
                    {
                        // Halfway: the window as Windows shows it (the presenter's check).
                        Thread.Sleep(TimeSpan.FromSeconds(settings.AutoQuitSeconds / 2));
                        bool ours = settings.Presenter == "d3d12" || settings.FrameGeneration != "off";
                        CaptureWindow(settings, ours ? "window-d3d12" : "window-game");
                        Thread.Sleep(TimeSpan.FromSeconds(settings.AutoQuitSeconds / 2));
                    }
                    else
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(settings.AutoQuitSeconds));
                    }
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
            // The glowing decals' light, A/B in the same view within the session (the machine's own drift from run
            // to run is as large as its cost): windows with it off and on in turn (the log's "gate mode is now"
            // lines say "glowing decals only glow" for the former), then as the settings have it.
            for (int round = 0; round < 3; ++round)
            {
                NativeApi.RtSetDecalLights(false);
                Step(GateMode.Replace, DebugView.Lit, 7, round == 0 ? "lit-replace-decal-glow-only" : null);
                NativeApi.RtSetDecalLights(true);
                Step(GateMode.Replace, DebugView.Lit, 7, round == 0 ? "lit-replace-decal-lights" : null);
            }
            NativeApi.RtSetDecalLights(settings.DecalLights);
            // The light tree against the light grid (P2z), A/B in the same view within the session: windows with
            // each in turn (the log's "gate mode is now" lines say "light tree" for the former), the emitters' light
            // alone from the tree (against "emitters" below, from the grid), then as the settings have it.
            for (int round = 0; round < 3; ++round)
            {
                NativeApi.RtSetLightTree(true);
                Step(GateMode.Replace, DebugView.Lit, 7, round == 0 ? "lit-replace-tree" : null);
                NativeApi.RtSetLightTree(false);
                Step(GateMode.Replace, DebugView.Lit, 7, round == 0 ? "lit-replace-grid" : null);
            }
            NativeApi.RtSetLightTree(true);
            Step(GateMode.Replace, DebugView.Emitters, 8, "emitters-tree");
            NativeApi.RtSetLightTree(settings.LightTree);
            // Ray Reconstruction's DLSS modes, A/B in the same view within the session: the light traced at the
            // game's size (DLAA) and smaller, upscaled (the log's "DLSS Ray Reconstruction" line of each window
            // names the mode and both sizes), then as the settings have it.
            DlssMode[] dlssModes = { DlssMode.Dlaa, DlssMode.Quality, DlssMode.Balanced, DlssMode.Performance,
                                     DlssMode.UltraPerformance };
            for (int round = 0; round < 2; ++round)
            {
                foreach (DlssMode dlssMode in dlssModes)
                {
                    NativeApi.RtSetDlssMode(dlssMode);
                    Step(GateMode.Replace, DebugView.Lit, 7,
                         round == 0 ? "lit-replace-dlss-" + dlssMode.ToString().ToLowerInvariant() : null);
                }
            }
            NativeApi.RtSetDlssMode(settings.DlssMode);
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
            // From afar (P2z): the spectator 80 m beyond the player's grid, looking back at it; the game's frame, then
            // the traced light with the light grid's coarse levels (lamps and emitters traced at any distance) and
            // without them (beyond 32 m the game's own lamp light), A/B; then as the settings have it.
            ViewFromAfar(80.0);
            Step(GateMode.Off, null, 6, "game-far");
            for (int round = 0; round < 2; ++round)
            {
                NativeApi.RtSetLightTree(true);
                Step(GateMode.Replace, DebugView.Lit, 8, round == 0 ? "lit-replace-far-tree" : null);
                NativeApi.RtSetLightTree(false);
                Step(GateMode.Replace, DebugView.Lit, 8, round == 0 ? "lit-replace-far-grid" : null);
            }
            NativeApi.RtSetLightTree(settings.LightTree);
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

        // [Debug] TestDecals: bullet holes where the player's view meets a grid (the game's own projected decals:
        // MyDecals.HandleAddDecal at the hits of rays from the camera, as a rifle's bullets leave them, in pairs that
        // overlap), then the game's frame with them, the traced base color, its comparison with the G-buffer (which
        // the game's decal pass blends them into: the log's "projected decals" line of each window) and the traced
        // light. Run without the inspector: its views stay as they are.
        private static void DecalSequence(Settings settings)
        {
            int placed = 0;
            bool done = false;
            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    placed = PlaceBulletHoles();
                }
                catch (Exception exception)
                {
                    Log.Error("Decal test: placing the bullet holes failed", exception);
                }
                finally
                {
                    done = true;
                }
            }, Plugin.Name);
            Plugin.WaitUntil(() => done, TimeSpan.FromSeconds(10));
            Log.Info($"Decal test: {placed} bullet holes placed; their textures load.");
            Thread.Sleep(TimeSpan.FromSeconds(4));
            Step(GateMode.Off, null, 6, "decals-game");
            Step(GateMode.Replace, DebugView.BaseColor, 6, "decals-basecolor");
            Step(GateMode.Replace, DebugView.MaterialCompare, 8, "decals-materialcompare");
            Step(GateMode.Replace, DebugView.Lit, 12, "decals-lit-replace");
            Step(GateMode.Off, null, 6, "decals-game-after");
            NativeApi.RtSetDebugView(settings.DebugView);
            NativeApi.RtSetGateMode(settings.GateMode);
            Thread.Sleep(1000);
            Log.Info("Decal test: done.");

            void Step(GateMode mode, DebugView? view, int seconds, string capture)
            {
                Log.Info($"Decal test: gate mode {mode}{(view != null ? ", debug view " + view : "")} for {seconds} s.");
                if (view != null)
                    NativeApi.RtSetDebugView(view.Value);
                NativeApi.RtSetGateMode(mode);
                Thread.Sleep(TimeSpan.FromSeconds(seconds - 2));
                Capture(settings, capture);
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }

        // Update thread: rays from the camera over the middle of its view (11 x 7 directions, 0.06 apart), up to
        // 12 m; at each grid they hit, a rifle bullet's hole and a second one 6 cm beside it. The bullet holes placed.
        private static int PlaceBulletHoles()
        {
            VRage.Game.Utils.MyCamera camera = MySector.MainCamera;
            VRage.Game.ModAPI.IMyPhysics physics = Sandbox.ModAPI.MyAPIGateway.Physics;
            if (camera == null || physics == null)
                return 0;
            MatrixD view = camera.WorldMatrix;
            Vector3D eye = view.Translation;
            VRage.Utils.MyStringHash rifle = VRage.Utils.MyStringHash.GetOrCompute("RifleBullet");
            VRage.Utils.MyStringHash metal = VRage.Utils.MyStringHash.GetOrCompute("Metal");
            int placed = 0;
            int hitGrids = 0;
            double nearest = double.MaxValue;
            double farthest = 0;
            for (int y = -3; y <= 3; ++y)
            {
                for (int x = -5; x <= 5; ++x)
                {
                    Vector3D direction = Vector3D.Normalize(view.Forward + view.Right * (x * 0.06) + view.Up * (y * 0.06));
                    if (!physics.CastRay(eye, eye + direction * 12, out VRage.Game.ModAPI.IHitInfo hit) ||
                        !(hit?.HitEntity is VRage.ModAPI.IMyEntity entity) ||
                        !(entity.GetTopMostParent() is MyCubeGrid))
                        continue;
                    ++hitGrids;
                    double distance = Vector3D.Distance(eye, hit.Position);
                    nearest = Math.Min(nearest, distance);
                    farthest = Math.Max(farthest, distance);
                    Vector3 normal = hit.Normal;
                    Vector3 side = Vector3.Normalize(Vector3.Cross(normal, (Vector3)view.Up));
                    if (!side.IsValid() || side.LengthSquared() < 0.5f)
                        side = Vector3.Normalize(Vector3.Cross(normal, (Vector3)view.Right));
                    for (int pair = 0; pair < 2; ++pair)
                    {
                        var info = new VRage.Game.ModAPI.MyHitInfo
                        {
                            Position = hit.Position + side * (pair * 0.06f),
                            Normal = normal,
                        };
                        Sandbox.Game.MyDecals.HandleAddDecal(entity, info, (Vector3)direction, physicalMaterial: metal,
                            source: rifle);
                        ++placed;
                    }
                }
            }
            Log.Info($"Decal test: rays from the camera met grids {hitGrids} times, " +
                     (hitGrids != 0 ? $"{nearest:F1} to {farthest:F1} m away." : "none within 12 m."));
            return placed;
        }

        // [Debug] LookAtSkins (armor skin subtypes, comma-separated; "block:<subtype>" for a block type;
        // "view:<subtype>@<eye>><target>[|<point>...]" for a view of one, LookFrom): per entry, the spectator 3 m from
        // the matching block nearest to the camera (on the camera's side), then the game's frame, the comparison of the
        // traced material with the G-buffer (the log's "traced material" line of the window) and the traced light,
        // with screenshots.
        private static void SkinSequence(Settings settings)
        {
            foreach (string entry in settings.LookAtSkins.Split(','))
            {
                string skin = entry.Trim();
                if (skin.Length == 0)
                    continue;
                bool view = skin.StartsWith("view:", StringComparison.OrdinalIgnoreCase);
                Vector3D[] points = null;
                bool found = false;
                bool done = false;
                MySandboxGame.Static.Invoke(() =>
                {
                    try
                    {
                        found = view ? LookFrom(skin, out points) : LookAtSkin(skin);
                    }
                    catch (Exception exception)
                    {
                        Log.Error("Skin test: placing the camera failed", exception);
                    }
                    finally
                    {
                        done = true;
                    }
                }, Plugin.Name);
                Plugin.WaitUntil(() => done, TimeSpan.FromSeconds(10));
                if (!found)
                    continue;
                Thread.Sleep(TimeSpan.FromSeconds(3));  // textures stream in
                if (view)
                {
                    // The view's points as probes (the camera has moved by now), through the material views, the
                    // parts view and the traced light.
                    string name = skin.Substring(5, Math.Max(0, skin.IndexOf('@') - 5));
                    int[] pixels = null;
                    done = false;
                    MySandboxGame.Static.Invoke(() =>
                    {
                        try
                        {
                            pixels = ProbePixels(points);
                        }
                        finally
                        {
                            done = true;
                        }
                    }, Plugin.Name);
                    Plugin.WaitUntil(() => done, TimeSpan.FromSeconds(10));
                    if (pixels != null && pixels.Length >= 2)
                        NativeApi.RtSetProbes(pixels);
                    Step(GateMode.Off, null, 5, $"view-{name}-game");
                    Step(GateMode.Replace, DebugView.BaseColor, 5, $"view-{name}-basecolor");
                    Step(GateMode.Replace, DebugView.MaterialCompare, 6, $"view-{name}-materialcompare");
                    Step(GateMode.Replace, DebugView.Parts, 5, $"view-{name}-parts");
                    Step(GateMode.Replace, DebugView.Lit, 8, $"view-{name}-lit-replace");
                    NativeApi.RtSetProbes(settings.Probes);
                    continue;
                }
                string label = skin.Replace(':', '-');  // file names
                // A block type is looked at from above: probes at the middle of the view say what the rays hit there.
                bool byBlock = skin.StartsWith("block:", StringComparison.OrdinalIgnoreCase);
                if (byBlock)
                    NativeApi.RtSetProbes(new[] { 960, 540, 960, 600, 900, 540, 1020, 540, 960, 480 });
                Step(GateMode.Off, null, 5, $"skin-{label}-game");
                Step(GateMode.Replace, DebugView.BaseColor, 5, $"skin-{label}-basecolor");
                Step(GateMode.Replace, DebugView.MaterialCompare, 6, $"skin-{label}-materialcompare");
                Step(GateMode.Replace, DebugView.Lit, 8, $"skin-{label}-lit-replace");
                if (byBlock)
                {
                    // The light's parts apart: the sun and occlusion alone (clay), the traced light before any
                    // denoiser, the emitters' light alone.
                    Step(GateMode.Replace, DebugView.Clay, 5, $"skin-{label}-clay");
                    Step(GateMode.Overlay, DebugView.LitRaw, 8, $"skin-{label}-lit-raw");
                    Step(GateMode.Replace, DebugView.Emitters, 8, $"skin-{label}-emitters");
                    NativeApi.RtSetProbes(settings.Probes);
                }
            }
            NativeApi.RtSetDebugView(settings.DebugView);
            NativeApi.RtSetGateMode(settings.GateMode);
            Thread.Sleep(1000);
            Log.Info("Skin test: done.");

            void Step(GateMode mode, DebugView? view, int seconds, string capture)
            {
                Log.Info($"Skin test: gate mode {mode}{(view != null ? ", debug view " + view : "")} for {seconds} s.");
                if (view != null)
                    NativeApi.RtSetDebugView(view.Value);
                NativeApi.RtSetGateMode(mode);
                Thread.Sleep(TimeSpan.FromSeconds(seconds - 2));
                Capture(settings, capture);
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }

        // Update thread: the spectator 3 m from the centre of the block with `skin` nearest to the camera, on the
        // camera's side, looking at it. Whether one was found.
        private static bool LookAtSkin(string skin)
        {
            MySession session = MySession.Static;
            VRage.Game.Utils.MyCamera camera = MySector.MainCamera;
            if (session == null || camera == null)
                return false;
            // "block:<subtype>": a block type instead of a skin.
            bool byBlock = skin.StartsWith("block:", StringComparison.OrdinalIgnoreCase);
            string blockSubtype = byBlock ? skin.Substring(6) : null;
            VRage.Utils.MyStringHash wanted = VRage.Utils.MyStringHash.GetOrCompute(skin);
            Vector3D eye = camera.Position;
            Sandbox.Game.Entities.Cube.MySlimBlock best = null;
            MyCubeGrid bestGrid = null;
            Vector3D bestCenter = Vector3D.Zero;
            double bestDistance = double.MaxValue;
            int count = 0;
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid))
                    continue;
                foreach (Sandbox.Game.Entities.Cube.MySlimBlock block in grid.GetBlocks())
                {
                    if (byBlock ? block.BlockDefinition.Id.SubtypeName != blockSubtype : block.SkinSubtypeId != wanted)
                        continue;
                    ++count;
                    block.ComputeWorldCenter(out Vector3D center);
                    double distance = Vector3D.DistanceSquared(center, eye);
                    if (distance < bestDistance)
                    {
                        best = block;
                        bestGrid = grid;
                        bestCenter = center;
                        bestDistance = distance;
                    }
                }
            }
            if (best == null)
            {
                Log.Info($"Skin test: no block with the {skin} skin.");
                return false;
            }
            Vector3D toEye = eye - bestCenter;
            Vector3D direction = toEye.LengthSquared() > 1e-6 ? Vector3D.Normalize(toEye) : bestGrid.WorldMatrix.Up;
            Vector3D up = bestGrid.WorldMatrix.Up;
            if (byBlock)
            {
                // From above, by the grid's up (the floor a block is set in), a little toward the grid's front.
                MatrixD gridWorld = bestGrid.WorldMatrix;
                direction = Vector3D.Normalize(gridWorld.Up + gridWorld.Forward * 0.4);
                up = gridWorld.Forward;
            }
            Vector3D position = bestCenter + direction * 3;
            session.SetCameraController(MyCameraControllerEnum.Spectator, null, position);
            MySpectatorCameraController spectator = MySpectatorCameraController.Static;
            if (spectator != null)
            {
                spectator.Position = position;
                spectator.SetTarget(bestCenter, up);
            }
            Log.Info($"Skin test: {count} blocks with the {skin} skin; looking at a {best.BlockDefinition.Id.SubtypeName} " +
                     $"of {bestGrid.DisplayName}, {Math.Sqrt(bestDistance):F1} m from the camera.");
            return true;
        }

        // Update thread. "view:<subtype>@<eye>><target>[|<point>...]" (LookAtSkins): the spectator at <eye> in the
        // model space of the block of that type nearest to the camera (metres, "x y z"), looking at <target>, its up
        // the block's; `points`: the further points (up to 8, the same space) in the world, for probes (ProbePixels).
        // Whether such a block was found.
        private static bool LookFrom(string entry, out Vector3D[] points)
        {
            points = new Vector3D[0];
            MySession session = MySession.Static;
            VRage.Game.Utils.MyCamera camera = MySector.MainCamera;
            if (session == null || camera == null)
                return false;
            string spec = entry.Substring(5);
            int at = spec.IndexOf('@');
            int toward = at > 0 ? spec.IndexOf('>', at + 1) : -1;
            string[] tail = toward > 0 ? spec.Substring(toward + 1).Split('|') : new string[0];
            if (toward < 0 || !TryPoint(spec.Substring(at + 1, toward - at - 1), out Vector3D eyeLocal) ||
                !TryPoint(tail[0], out Vector3D targetLocal))
            {
                Log.Warning($"View: \"{entry}\" is not view:<subtype>@<x y z>><x y z>[|<x y z>...].");
                return false;
            }
            string subtype = spec.Substring(0, at);
            Vector3D eye = camera.Position;
            MyCubeBlock best = null;
            double bestDistance = double.MaxValue;
            int count = 0;
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid))
                    continue;
                foreach (Sandbox.Game.Entities.Cube.MySlimBlock block in grid.GetBlocks())
                {
                    if (block.FatBlock == null || block.BlockDefinition.Id.SubtypeName != subtype)
                        continue;
                    ++count;
                    double distance = Vector3D.DistanceSquared(block.FatBlock.PositionComp.GetPosition(), eye);
                    if (distance < bestDistance)
                    {
                        best = block.FatBlock;
                        bestDistance = distance;
                    }
                }
            }
            if (best == null)
            {
                Log.Info($"View: no {subtype} block.");
                return false;
            }
            MatrixD world = best.WorldMatrix;
            Vector3D position = Vector3D.Transform(eyeLocal, world);
            Vector3D target = Vector3D.Transform(targetLocal, world);
            session.SetCameraController(MyCameraControllerEnum.Spectator, null, position);
            MySpectatorCameraController spectator = MySpectatorCameraController.Static;
            if (spectator != null)
            {
                spectator.Position = position;
                spectator.SetTarget(target, world.Up);
            }
            var list = new System.Collections.Generic.List<Vector3D>();
            for (int i = 1; i < tail.Length && list.Count < 8; i++)
            {
                if (TryPoint(tail[i], out Vector3D local))
                    list.Add(Vector3D.Transform(local, world));
            }
            points = list.ToArray();
            Log.Info($"View: {count} {subtype} blocks; looking at the one of {best.CubeGrid.DisplayName}, " +
                     $"{Math.Sqrt(bestDistance):F1} m from the camera, from ({eyeLocal.X:F2}, {eyeLocal.Y:F2}, " +
                     $"{eyeLocal.Z:F2}) in its model space; {points.Length} probe points.");
            return true;
        }

        // "x y z" (invariant culture).
        private static bool TryPoint(string text, out Vector3D point)
        {
            point = Vector3D.Zero;
            string[] parts = text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.Float;
            if (parts.Length != 3 || !double.TryParse(parts[0], style, culture, out double x) ||
                !double.TryParse(parts[1], style, culture, out double y) ||
                !double.TryParse(parts[2], style, culture, out double z))
                return false;
            point = new Vector3D(x, y, z);
            return true;
        }

        // Update thread: the pixels (x, y pairs) where world points lie in the main camera's view, through its
        // view-projection matrix; points off the screen are left out.
        private static int[] ProbePixels(Vector3D[] points)
        {
            var pixels = new System.Collections.Generic.List<int>();
            VRage.Game.Utils.MyCamera camera = MySector.MainCamera;
            if (camera == null || points == null)
                return pixels.ToArray();
            MatrixD m = camera.ViewProjectionMatrix;
            double width = camera.Viewport.Width;
            double height = camera.Viewport.Height;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3D p = points[i];
                double cx = p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41;
                double cy = p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42;
                double cw = p.X * m.M14 + p.Y * m.M24 + p.Z * m.M34 + m.M44;
                if (cw <= 1e-6)
                {
                    Log.Info($"View: probe point {i} lies behind the camera.");
                    continue;
                }
                int x = (int)Math.Floor((cx / cw * 0.5 + 0.5) * width);
                int y = (int)Math.Floor((0.5 - cy / cw * 0.5) * height);
                if (x < 0 || y < 0 || x >= width || y >= height)
                {
                    Log.Info($"View: probe point {i} lies off the screen ({x}, {y}).");
                    continue;
                }
                Log.Info($"View: probe point {i} at pixel ({x}, {y}) of {width}x{height}.");
                pixels.Add(x);
                pixels.Add(y);
            }
            return pixels.ToArray();
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

        // The spectator camera `distance` metres beyond the bounding sphere of the largest grid within 100 m of the
        // player character (of the character itself without one), across the sun's direction and a little away
        // from it, above (the sun out of view, the grid's shadowed side in it), looking at its centre: where the
        // traced lamps' reach shows.
        private static void ViewFromAfar(double distance)
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
                Vector3D target = world.Translation;
                double radius = 0;
                MyCubeGrid largest = null;
                foreach (var entity in MyEntities.GetEntities())
                {
                    if (entity is MyCubeGrid grid &&
                        Vector3D.Distance(grid.PositionComp.WorldAABB.Center, target) < 100 &&
                        (largest == null || grid.BlocksCount > largest.BlocksCount))
                        largest = grid;
                }
                if (largest != null)
                {
                    target = largest.PositionComp.WorldAABB.Center;
                    radius = largest.PositionComp.WorldVolume.Radius;
                }
                Vector3D toSun = MySector.DirectionToSunNormalized;
                Vector3D across = Vector3D.Cross(toSun, world.Up);
                if (across.LengthSquared() < 1e-6)
                    across = Vector3D.Cross(toSun, world.Forward);
                across.Normalize();
                Vector3D eye = target + Vector3D.Normalize(across - toSun * 0.4 + world.Up * 0.3) * (radius + distance);
                session.SetCameraController(MyCameraControllerEnum.Spectator, null, eye);
                MySpectatorCameraController spectator = MySpectatorCameraController.Static;
                if (spectator != null)
                {
                    spectator.Position = eye;
                    spectator.SetTarget(target, world.Up);
                }
                Log.Info($"Inspector: spectator {radius + distance:F0} m from " +
                         $"{(largest != null ? "the grid " + largest.DisplayName : "the player character")}, afar.");
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
        // [Debug] TestMenu: the game's graphics options with their "Path tracing" button, the plugin's screen over them,
        // the DLSS mode changed through its drop-down (its event applies it as a click would) and back, frame generation
        // switched; screenshots with the GUI.
        private static void MenuSequence(Settings settings)
        {
            Sandbox.Graphics.GUI.MyGuiScreenBase options = null;
            PathTracingScreen screen = null;
            OnGameThread(() =>
            {
                options = OptionsMenu.CreateGraphicsOptionsScreen();
                if (options != null)
                    Sandbox.Graphics.GUI.MyGuiSandbox.AddScreen(options);
            }, "the game's graphics options could not be opened");
            if (options == null)
                return;
            Thread.Sleep(2000);
            string button = "missing";
            OnGameThread(() =>
            {
                foreach (Sandbox.Graphics.GUI.MyGuiControlBase control in options.Controls)
                {
                    if (control is Sandbox.Graphics.GUI.MyGuiControlButton found && found.Name == "PathTracing")
                        button = $"\"{found.Text}\" at ({found.Position.X:F3}, {found.Position.Y:F3})";
                }
            }, "the graphics options' controls could not be read");
            Log.Info($"Menu test: the graphics options' Path tracing button: {button}.");
            CaptureGui(settings, "menu-graphics");
            Thread.Sleep(1500);
            OnGameThread(() =>
            {
                screen = new PathTracingScreen(settings);
                Sandbox.Graphics.GUI.MyGuiSandbox.AddScreen(screen);
            }, "the Path tracing screen could not be opened");
            if (screen != null)
            {
                Thread.Sleep(2000);
                CaptureGui(settings, "menu-path-tracing");
                Thread.Sleep(1500);
                DlssMode before = settings.DlssMode;
                OnGameThread(() => screen.DlssModeCombobox.SelectItemByKey((long)DlssMode.Performance),
                    "the DLSS mode could not be selected");
                Thread.Sleep(4000);
                CaptureGui(settings, "menu-dlss-performance");
                Thread.Sleep(1500);
                OnGameThread(() => screen.DlssModeCombobox.SelectItemByKey((long)before), "the DLSS mode could not be reset");
                // Frame generation switched on as a click would (Streamline loads now when it did not at the start),
                // then back to how it was.
                bool frameGeneration = settings.FrameGenerationOn;
                Thread.Sleep(1500);
                OnGameThread(() => screen.FrameGenerationCheckbox.IsChecked = !frameGeneration,
                    "frame generation could not be switched");
                Log.Info($"Menu test: frame generation switched {(!frameGeneration ? "on" : "off")} " +
                         $"({(settings.FrameGenerationOn != frameGeneration ? "taken" : "refused")}).");
                Thread.Sleep(8000);
                CaptureGui(settings, "menu-frame-generation");
                OnGameThread(() => screen.FrameGenerationCheckbox.IsChecked = frameGeneration,
                    "frame generation could not be switched back");
                Thread.Sleep(3000);
                OnGameThread(() => screen.CloseScreen(), "the Path tracing screen could not be closed");
            }
            OnGameThread(() => options.CloseScreen(), "the graphics options could not be closed");
            Thread.Sleep(1500);
            Log.Info("Menu test: done.");

            void OnGameThread(Action action, string failure)
            {
                bool done = false;
                MySandboxGame.Static.Invoke(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception exception)
                    {
                        Log.Error("Menu test: " + failure, exception);
                    }
                    finally
                    {
                        done = true;
                    }
                }, Plugin.Name);
                Plugin.WaitUntil(() => done, TimeSpan.FromSeconds(10));
            }
        }

        // A screenshot with the GUI drawn (the menu test's).
        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // The game window in the foreground, as when played (Windows lets a thread set the foreground while its
        // input is attached to the foreground window's thread).
        private static void BringToFront()
        {
            IntPtr window = Process.GetCurrentProcess().MainWindowHandle;
            IntPtr foreground = GetForegroundWindow();
            if (window == IntPtr.Zero || window == foreground)
            {
                Log.Info(window == IntPtr.Zero ? "Debug: no game window to bring to the foreground."
                                               : "Debug: the game window is in the foreground already.");
                return;
            }
            uint own = GetCurrentThreadId();
            uint other = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, IntPtr.Zero) : 0;
            bool attached = other != 0 && other != own && AttachThreadInput(own, other, true);
            BringWindowToTop(window);
            bool set = SetForegroundWindow(window);
            if (attached)
                AttachThreadInput(own, other, false);
            Log.Info($"Debug: the game window brought to the foreground ({(set ? "done" : "refused")}; now " +
                     $"{(GetForegroundWindow() == window ? "the game's" : "another")}).");
        }

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out ClientRect rect);

#pragma warning disable CS0649  // filled by GetClientRect
        private struct ClientRect
        {
            public int Left, Top, Right, Bottom;
        }
#pragma warning restore CS0649

        // The game window's client area as Windows composes it (PrintWindow with PW_RENDERFULLCONTENT: the DirectX
        // content of flip-model swap chains included), to see what the presenter puts on screen.
        private static void CaptureWindow(Settings settings, string name)
        {
            const uint clientOnly = 1, renderFullContent = 2;
            if (string.IsNullOrEmpty(settings.CapturePath))
                return;
            IntPtr window = Process.GetCurrentProcess().MainWindowHandle;
            if (window == IntPtr.Zero || !GetClientRect(window, out ClientRect rect) || rect.Right <= rect.Left ||
                rect.Bottom <= rect.Top)
            {
                Log.Warning("Debug: no game window to capture.");
                return;
            }
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            using (var bitmap = new System.Drawing.Bitmap(width, height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                bool printed;
                using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    IntPtr dc = graphics.GetHdc();
                    printed = PrintWindow(window, dc, clientOnly | renderFullContent);
                    graphics.ReleaseHdc(dc);
                }
                if (!printed)
                {
                    Log.Warning("Debug: PrintWindow failed; no window capture.");
                    return;
                }
                Directory.CreateDirectory(settings.CapturePath);
                string path = Path.Combine(settings.CapturePath, name + ".png");
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Log.Info($"Debug: window captured ({width}x{height}) to {path}.");
            }
        }

        private static void CaptureGui(Settings settings, string name)
        {
            if (string.IsNullOrEmpty(settings.CapturePath))
                return;
            Directory.CreateDirectory(settings.CapturePath);
            string path = Path.Combine(settings.CapturePath, name + ".png");
            MySandboxGame.Static.Invoke(() => MyRenderProxy.TakeScreenshot(Vector2.One, path, false, false, false),
                Plugin.Name);
            Log.Info("Menu test: screenshot requested: " + path);
        }

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
