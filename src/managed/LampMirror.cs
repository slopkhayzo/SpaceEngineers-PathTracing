using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using VRage.Generics;
using VRage.Render.Scene;
using VRage.Render.Scene.Components;
using VRageMath;
using VRageRender.Messages;

namespace SpaceEngineers.RTHWAccel
{
    // The game's lamps for the traced light (research/docs/space-engineers-rt-plan.md §6.9), walked with the
    // scene (SceneMirror.Snapshot, render thread): every light component (MyComponentFactory's
    // MyLightComponent pool) whose actor is visible, with its point light, its spot light or both on, goes to
    // the native core in world space, as MyLightComponent.WritePointlightConstants and
    // WriteSpotlightConstants place them: a point light at the actor's translation plus PointOffset along its
    // forward, a spot light at the translation along the forward with the actor's up for its mask (the
    // reflector texture, a material of its own). The data is the component's UpdateRenderLightData after
    // UpdateData (intensity in the colours, the falloff already turned into the shaders' factor, lights below
    // the game's intensity floor switched off).
    internal static class LampMirror
    {
        private const uint LightSpot = 1;  // sert::rt::kLightSpot

        private static Type s_lightType;
        private static Func<object, UpdateRenderLightData> s_data;  // MyLightComponent.m_data
        private static Func<string, string> s_textureFullPath;       // MyResourceUtils.GetTextureFullPath
        private static readonly IntPtr[] s_noRendered = new IntPtr[4];
        private static readonly Dictionary<string, uint> s_masks = new Dictionary<string, uint>();
        private static RtLight[] s_lamps = new RtLight[256];
        private static int s_count;
        private static long s_pointSum;
        private static long s_spotSum;
        private static long s_maskedSum;

        // Every member the walk uses; a miss throws before any hook exists. Also run by the game-free tests.
        internal static void Resolve(Assembly render11)
        {
            s_lightType = GameReflection.Type(render11, "VRage.Render11.Scene.Components.MyLightComponent");
            s_data = Compiled.Getter<UpdateRenderLightData>(s_lightType, "m_data");
            MethodInfo fullPath = GameReflection.Type(render11, "VRage.Render11.Resources.MyResourceUtils")
                .GetMethods(GameReflection.All).Single(m => m.Name == "GetTextureFullPath");
            ParameterExpression textureName = Expression.Parameter(typeof(string), "name");
            s_textureFullPath = Expression.Lambda<Func<string, string>>(
                Expression.Call(fullPath, textureName, Expression.Constant(null, typeof(string))), textureName).Compile();
        }

        public static void Snapshot(Dictionary<Type, MyRuntimeObjectsPool<MyActorComponent>> pools)
        {
            s_count = 0;
            if (pools != null && pools.TryGetValue(s_lightType, out MyRuntimeObjectsPool<MyActorComponent> lights))
            {
                foreach (MyActorComponent component in lights.Active)
                    Lamp(component);
            }
            NativeApi.RtLightsSubmit(s_lamps, s_count);
        }

        private static void Lamp(MyActorComponent component)
        {
            IMyActor actor = component.Owner;
            if (actor == null || actor.IsDestroyed || !actor.IsVisible)
                return;
            UpdateRenderLightData data = s_data(component);
            if (!data.PointLightOn && !data.SpotLightOn)
                return;
            MatrixD world = actor.WorldMatrix;
            if (data.PointLightOn)
            {
                Add(0, actor.ID, 0, world.Translation + data.PointOffset * world.Forward, Vector3.Forward, 0,
                    Vector3.Up, in data.PointLight);
                ++s_pointSum;
            }
            if (data.SpotLightOn)
            {
                uint mask = Mask(data.ReflectorTexture);
                Add(LightSpot, actor.ID, mask, world.Translation, (Vector3)world.Forward, data.SpotLight.ApertureCos,
                    (Vector3)world.Up, in data.SpotLight.Light);
                ++s_spotSum;
                if (mask != 0)
                    ++s_maskedSum;
            }
        }

        private static unsafe void Add(uint flags, uint id, uint mask, Vector3D position, Vector3 direction,
            float apertureCos, Vector3 up, in MyLightLayout light)
        {
            if (!(light.Range > 0))
                return;
            if (s_count == s_lamps.Length)
                Array.Resize(ref s_lamps, s_lamps.Length * 2);
            ref RtLight lamp = ref s_lamps[s_count++];
            lamp.Flags = flags;
            lamp.Id = id;
            lamp.MaskMaterial = mask;
            lamp.Position[0] = position.X;
            lamp.Position[1] = position.Y;
            lamp.Position[2] = position.Z;
            lamp.Direction[0] = direction.X;
            lamp.Direction[1] = direction.Y;
            lamp.Direction[2] = direction.Z;
            lamp.ApertureCos = apertureCos;
            lamp.Up[0] = up.X;
            lamp.Up[1] = up.Y;
            lamp.Up[2] = up.Z;
            lamp.Range = light.Range;
            lamp.Color[0] = light.Color.X;
            lamp.Color[1] = light.Color.Y;
            lamp.Color[2] = light.Color.Z;
            lamp.Falloff = light.Falloff;
            lamp.GlossFactor = light.GlossFactor;
            lamp.DiffuseFactor = light.DiffuseFactor;
        }

        // A spot light's reflector mask as a material of its own (its alpha-mask slot), by texture name; 0: none.
        private static uint Mask(string texture)
        {
            if (string.IsNullOrEmpty(texture))
                return 0;
            if (!s_masks.TryGetValue(texture, out uint handle))
            {
                string path = s_textureFullPath(texture);
                handle = string.IsNullOrEmpty(path) ? 0 : NativeApi.RtMaterialCreate(0, null, null, null, path, s_noRendered);
                s_masks[texture] = handle;
                Log.Info($"Lamp mirror: reflector mask {texture} -> {path ?? "(not found)"} (material {handle}).");
            }
            return handle;
        }

        public static void Report(double frames)
        {
            Log.Info($"Lamp mirror: per frame {s_pointSum / frames:F0} point lights and {s_spotSum / frames:F0} spot lights " +
                     $"({s_maskedSum / frames:F0} with a reflector mask); {s_masks.Count} masks known.");
            s_pointSum = s_spotSum = s_maskedSum = 0;
        }
    }
}
