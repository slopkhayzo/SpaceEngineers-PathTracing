using System;
using VRageMath;

namespace SpaceEngineers.RTHWAccel
{
    // The free camera's controls ([Debug] FreeCamera; FreeCamera.cs applies them to the game's free spectator), as
    // plain arithmetic for the tests: Ctrl flies down (W/A/S/D, Space up and C down stay the game's controls), Shift
    // 5x faster, Alt 5x slower, Q/E roll at a steady rate, and the mouse wheel zooms (the field of view) instead of
    // changing the spectator's speeds.
    internal static class PhotoControls
    {
        public const float FastFactor = 5f;
        public const float SlowFactor = 0.2f;
        // Radians of roll a simulation step (60 a second) at the normal speed: about 34 degrees a second.
        public const float RollPerStep = 0.01f;
        // The spectator rolls by its roll input x its linear speed x 0.1, at most 0.02 rad a step
        // (VRage.MySpectator.MoveAndRotate, user-controlled mode).
        public const float BaseRollPerSpeed = 0.1f;
        public const float BaseRollLimit = 0.02f;
        public const float MinFov = 5f * (float)Math.PI / 180f;
        public const float MaxFov = 120f * (float)Math.PI / 180f;
        // The field of view's factor a wheel notch (120 units) forward: a narrower view, closer to the scene.
        public const float FovStep = 0.9f;

        public static float SpeedFactor(bool shift, bool alt) => (shift ? FastFactor : 1f) * (alt ? SlowFactor : 1f);

        // The move input with Ctrl as down, scaled by the speed factor (the spectator moves by input x its speed).
        public static Vector3 Move(Vector3 move, bool ctrl, float factor)
        {
            if (ctrl)
                move.Y = Math.Max(-1f, move.Y - 1f);
            return move * factor;
        }

        // The roll input that rolls the spectator RollPerStep x the speed factor a step (at most its limit) in the
        // input's direction, whatever its linear speed.
        public static float Roll(float roll, float linearSpeed, float factor)
        {
            if (roll == 0)
                return 0;
            float radians = Math.Min(RollPerStep * factor, BaseRollLimit);
            return Math.Sign(roll) * radians / (BaseRollPerSpeed * Math.Max(linearSpeed, 1e-6f));
        }

        // The field of view (radians) after `wheel` units of the mouse wheel (120 a notch; forward zooms in).
        public static float Fov(float fov, int wheel) =>
            MathHelper.Clamp(fov * (float)Math.Pow(FovStep, wheel / 120.0), MinFov, MaxFov);
    }
}
