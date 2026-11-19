using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using VRage.Game;
using VRage.ModAPI;
using VRageMath;

namespace SpaceEngineers.RTHWAccel
{
    // [Debug] FreeCamera: the game's spectator camera as a photo camera, in worlds this machine runs (single player,
    // or hosting), also where the world does not allow it (survival without its "Enable spectator" setting).
    // - Access: the spectator keys (F8 free, F7 following, F9 static, F6 back) switch through
    //   MyGuiScreenGamePlay.SetSpectatorFree / SetSpectatorDelta, which ask MyGuiScreenGamePlay.SpectatorEnabled
    //   (creative mode, creative tools, or the world's setting); the detour adds "this machine is the server". The key
    //   handler's other check, MySession.HasPlayerSpectatorRights, already lets the host in (promote level owner).
    // - Controls in the free mode (PhotoControls): Ctrl down, Shift faster, Alt slower, Q/E roll at a steady rate,
    //   the mouse wheel zooms. MySpectatorCameraController.MoveAndRotate gets the adjusted inputs; the spectator's
    //   speeds, which the wheel changes there with Ctrl, Shift or Alt held, are put back after it.
    // - The zoom is the main camera's field of view, as camera blocks set theirs (MyCameraBlock.SetFov); the
    //   player's own returns when the camera leaves the free spectator (MySession.SetCameraController), as turrets
    //   restore it (MyLargeTurretBase.ExitView).
    // Nothing is written: the world's settings and the game's options stay as they are, and servers joined keep
    // their own rules.
    internal static unsafe class FreeCamera
    {
        private static delegate*<bool> s_spectatorEnabled;
        private static delegate*<object, Vector3, Vector2, float, void> s_moveAndRotate;
        private static delegate*<object, MyCameraControllerEnum, IMyEntity, Vector3D?, void> s_setCameraController;
        private static bool s_zoomed;

        public static void Install()
        {
            MethodInfo getter = typeof(MyGuiScreenGamePlay)
                .GetProperty(nameof(MyGuiScreenGamePlay.SpectatorEnabled), BindingFlags.Public | BindingFlags.Static)
                ?.GetGetMethod();
            MethodInfo move = typeof(MySpectatorCameraController).GetMethod(nameof(MySpectatorCameraController.MoveAndRotate),
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Vector3), typeof(Vector2), typeof(float) }, null);
            MethodInfo controller = typeof(MySession).GetMethod(nameof(MySession.SetCameraController),
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(MyCameraControllerEnum), typeof(IMyEntity), typeof(Vector3D?) }, null);
            if (getter == null || move == null || controller == null)
                throw new DetourException("a spectator method is missing (SpectatorEnabled, MoveAndRotate, SetCameraController)");
            using (var hooks = new HookTransaction())
            {
                s_spectatorEnabled = (delegate*<bool>)hooks.Install(getter, Own(nameof(SpectatorEnabledDetour)));
                s_moveAndRotate = (delegate*<object, Vector3, Vector2, float, void>)hooks.Install(move,
                    Own(nameof(MoveAndRotateDetour)));
                s_setCameraController = (delegate*<object, MyCameraControllerEnum, IMyEntity, Vector3D?, void>)hooks.Install(
                    controller, Own(nameof(SetCameraControllerDetour)));
                hooks.Commit();
            }
            Log.Info("Free camera: the spectator camera (F8 free, F6 back to the character) is available in worlds " +
                     "this machine runs; in it Ctrl flies down, Shift faster, Alt slower, Q/E roll, the mouse wheel zooms.");
        }

        // The game's own answer (the world's rules), for the automated check.
        public static bool GameAllows() => s_spectatorEnabled != null && s_spectatorEnabled();

        // The zoom as the mouse wheel works it, for the automated check (game thread): the new field of view.
        public static float ZoomNow(int wheel) => Zoom(wheel);

        private static MethodInfo Own(string name) =>
            typeof(FreeCamera).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

        // Game thread (the spectator keys' handlers).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SpectatorEnabledDetour() => s_spectatorEnabled() || Sandbox.Game.Multiplayer.Sync.IsServer;

        // Game thread (MyGuiScreenGamePlay.MoveAndRotatePlayerOrCamera), every simulation step with a spectator camera.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void MoveAndRotateDetour(object self, Vector3 move, Vector2 rotation, float roll)
        {
            var spectator = self as MySpectatorCameraController;
            VRage.Input.IMyInput input = VRage.Input.MyInput.Static;  // (VRage.ModAPI has an IMyInput too)
            if (spectator == null || input == null ||
                spectator.SpectatorCameraMovement != VRage.MySpectatorCameraMovementEnum.UserControlled)
            {
                s_moveAndRotate(self, move, rotation, roll);
                return;
            }
            float factor = PhotoControls.SpeedFactor(input.IsAnyShiftKeyPressed(), input.IsAnyAltKeyPressed());
            float linear = spectator.SpeedModeLinear;
            float angular = spectator.SpeedModeAngular;
            s_moveAndRotate(self, PhotoControls.Move(move, input.IsAnyCtrlKeyPressed(), factor), rotation,
                PhotoControls.Roll(roll, linear, factor));
            // The wheel is the zoom's here: the spectator's speeds stay.
            spectator.SpeedModeLinear = linear;
            spectator.SpeedModeAngular = angular;
            int wheel = input.MouseScrollWheelValue() - input.PreviousMouseScrollWheelValue();
            if (wheel != 0)
                Zoom(wheel);
        }

        // Game thread: before the camera leaves the free spectator, the player's own field of view returns.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetCameraControllerDetour(object self, MyCameraControllerEnum controller, IMyEntity entity,
            Vector3D? position)
        {
            if (s_zoomed && controller != MyCameraControllerEnum.Spectator)
            {
                try
                {
                    VRage.Game.Utils.MyCamera camera = MySector.MainCamera;
                    if (camera != null && MySandboxGame.Config != null)
                        camera.FieldOfView = MySandboxGame.Config.FieldOfView;
                }
                catch (Exception exception)
                {
                    Log.Error("Free camera: restoring the field of view", exception);
                }
                s_zoomed = false;
            }
            s_setCameraController(self, controller, entity, position);
        }

        private static float Zoom(int wheel)
        {
            VRage.Game.Utils.MyCamera camera = MySector.MainCamera;
            if (camera == null)
                return 0;
            camera.FieldOfView = PhotoControls.Fov(camera.FieldOfView, wheel);
            s_zoomed = true;
            return camera.FieldOfView;
        }
    }
}
