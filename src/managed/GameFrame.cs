using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace SpaceEngineers.RTHWAccel
{
    // The game's per-frame shader constants (MyCommon.FrameConstantsData, the
    // struct the game uploads to its cbuffer Frame as it is), copied raw every
    // frame for the traced passes: light, post-processing and fog settings in
    // the exact form the game's shaders read them (src/shaders/game_frame.hlsli).
    internal static class GameFrame
    {
        // FrameConstants of the game's Frame.hlsli.
        public const int ExpectedSize = 1280;

        private static Action<IntPtr> s_copy;
        private static IntPtr s_buffer;

        // The size of the game's struct; ExpectedSize when the layout is the one the shaders mirror.
        public static int Size { get; private set; }

        // Compiles the copier (ldsflda + cpblk: the field is a non-public struct of a non-public type).
        internal static void Resolve(Assembly render11)
        {
            Type common = GameReflection.Type(render11, "VRageRender.MyCommon");
            FieldInfo field = common.GetField("FrameConstantsData", GameReflection.All)
                ?? throw new DetourException("VRageRender.MyCommon::FrameConstantsData not found");
            var sizeOf = new DynamicMethod("SizeOfFrameConstants", typeof(int), Type.EmptyTypes, typeof(GameFrame).Module, true);
            ILGenerator il = sizeOf.GetILGenerator();
            il.Emit(OpCodes.Sizeof, field.FieldType);
            il.Emit(OpCodes.Ret);
            Size = ((Func<int>)sizeOf.CreateDelegate(typeof(Func<int>)))();
            var copy = new DynamicMethod("CopyFrameConstants", null, new[] { typeof(IntPtr) }, typeof(GameFrame).Module, true);
            il = copy.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldsflda, field);
            il.Emit(OpCodes.Ldc_I4, Size);
            il.Emit(OpCodes.Cpblk);
            il.Emit(OpCodes.Ret);
            s_copy = (Action<IntPtr>)copy.CreateDelegate(typeof(Action<IntPtr>));
        }

        // The current frame's constants in an unmanaged buffer of Size bytes (valid until the next call),
        // or zero when the layout differs from the mirrored one.
        public static IntPtr Capture()
        {
            if (s_copy == null || Size != ExpectedSize)
                return IntPtr.Zero;
            if (s_buffer == IntPtr.Zero)
                s_buffer = Marshal.AllocHGlobal(Size);
            s_copy(s_buffer);
            return s_buffer;
        }
    }
}
