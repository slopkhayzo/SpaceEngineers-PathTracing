using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using VRage.Render.Scene;
using VRage.Utils;
using VRageMath;
using VRageRender.Messages;

namespace SpaceEngineers.RTHWAccel
{
    // The game's projected decals for the traced view (research/docs/space-engineers-rt-plan.md P2v): bullet holes,
    // impact marks, missile scorches, tire trails (MyScreenDecals), which the game projects onto its G-buffer after
    // the geometry (Decals/Decals.hlsl). Walked with the scene (SceneMirror.Snapshot, render thread, before the
    // frame's culling): the decals the game drew last into the G-buffer (MyScreenDecals.m_materialsToDraw[0]; its
    // Preprocess job fills it anew later in the frame), in its draw order (by material, then as listed), each placed
    // and faded as MyScreenDecals.PrepareMaterialBatches does it now: a World decal at its WorldPosition, the others
    // by their parent actor (MatrixCurrent x the actor's world matrix); the fade from the time left before it goes
    // (DECAL_FADE_DURATION) and from its distance against its render distance (times the quality factor). Its four
    // textures (MyDecalTextures, by material and index) become a native material, the maps it has its DecalType.
    // The decals on transparent surfaces (m_materialsToDraw[1]) stay the game's transparent pass's.
    internal static unsafe class DecalMirror
    {
        private const int FlagWorld = 1;   // MyDecalFlags
        private const int FlagClosed = 4;
        private const uint FadeDuration = 6000;  // MyScreenDecals.DECAL_FADE_DURATION, ms
        private const int MaxLoggedMaterials = 32;

        // The *State structs are filled by compiled readers (SceneMirror.Reader), which the compiler cannot see.
#pragma warning disable CS0649
        internal struct DecalState
        {
            public uint FadeTimestamp;       // when it is gone (GetTimeStampSinceStart, ms); uint.MaxValue: not fading
            public MyDecalTopoData TopoData;
            public uint[] ParentIds;
            public int Flags;                // MyDecalFlags
            public float RenderSqDistance;
        }
        internal delegate void DecalReader(object decal, ref DecalState state);
        internal struct TexturesState
        {
            public int Type;  // MyFileTextureEnum: 1 color-metal, 2 normal-gloss, 4 extensions
            public object ColorMetal;
            public object NormalGloss;
            public object Extensions;
            public object Alphamask;
        }
        internal delegate void TexturesReader(object textures, ref TexturesState state);
#pragma warning restore CS0649

        private static Func<IDictionary> s_toDraw;     // m_materialsToDraw[0]: MyMaterialIdentity -> List<MyScreenDecal>
        private static Func<IDictionary> s_materials;  // m_materials: MyStringId -> List<MyDecalTextures>
        private static Func<object, MyStringId> s_identityMaterial;
        private static Func<object, int> s_identityIndex;
        private static DecalReader s_readDecal;
        private static TexturesReader s_readTextures;
        private static Func<uint> s_now;          // GetTimeStampSinceStart
        private static Func<float> s_quality;     // m_qualityFactor
        private static Func<Vector3D> s_camera;   // MyRender11.Environment.Matrices.CameraPosition

        private sealed class MaterialEntry
        {
            public object ColorMetal;
            public object NormalGloss;
            public object Extensions;
            public object Alphamask;
            public uint Handle;
            public uint Maps;
        }

        private static readonly Dictionary<long, MaterialEntry> s_materialEntries = new Dictionary<long, MaterialEntry>();
        private static readonly object[] s_textures = new object[4];
        private static RtDecal[] s_decals = new RtDecal[256];
        private static int s_count;
        private static long s_decalSum;
        private static int s_loggedMaterials;

        // Every member the walk uses; a miss throws before any hook exists. Also run by the game-free tests.
        internal static void Resolve(Assembly render11)
        {
            Type screenDecals = GameReflection.Type(render11, "VRageRender.MyScreenDecals");
            Type decal = GameReflection.Type(render11, "VRageRender.MyScreenDecals+MyScreenDecal");
            Type textures = GameReflection.Type(render11, "VRageRender.MyScreenDecals+MyDecalTextures");
            Type identity = GameReflection.Type(render11, "VRageRender.MyScreenDecals+MyMaterialIdentity");

            // () => m_materialsToDraw != null && m_materialsToDraw.Length > 0 ? m_materialsToDraw[0] : null
            Expression toDraw = Compiled.StaticMember(screenDecals, "m_materialsToDraw");
            s_toDraw = Expression.Lambda<Func<IDictionary>>(Expression.Condition(
                Expression.AndAlso(Expression.NotEqual(toDraw, Expression.Constant(null, toDraw.Type)),
                    Expression.GreaterThan(Expression.ArrayLength(toDraw), Expression.Constant(0))),
                Expression.Convert(Expression.ArrayIndex(toDraw, Expression.Constant(0)), typeof(IDictionary)),
                Expression.Constant(null, typeof(IDictionary)))).Compile();
            s_materials = Compiled.StaticGetter<IDictionary>(screenDecals, "m_materials");
            s_identityMaterial = Compiled.Getter<MyStringId>(identity, "Material");
            s_identityIndex = Compiled.Getter<int>(identity, "Index");
            s_readDecal = SceneMirror.Reader<DecalReader>(decal, typeof(DecalState), (d, set) => new[]
            {
                set(nameof(DecalState.FadeTimestamp), Compiled.Member(d, "FadeTimestamp")),
                set(nameof(DecalState.TopoData), Compiled.Member(d, "TopoData")),
                set(nameof(DecalState.ParentIds), Compiled.Member(d, "ParentIDs")),
                set(nameof(DecalState.Flags), Compiled.Member(d, "Flags")),
                set(nameof(DecalState.RenderSqDistance), Compiled.Member(d, "RenderSqDistance")),
            });
            s_readTextures = SceneMirror.Reader<TexturesReader>(textures, typeof(TexturesState), (t, set) => new[]
            {
                set(nameof(TexturesState.Type), Compiled.Member(t, "DecalType")),
                set(nameof(TexturesState.ColorMetal), Compiled.Member(t, "ColorMetalTexture")),
                set(nameof(TexturesState.NormalGloss), Compiled.Member(t, "NormalmapTexture")),
                set(nameof(TexturesState.Extensions), Compiled.Member(t, "ExtensionsTexture")),
                set(nameof(TexturesState.Alphamask), Compiled.Member(t, "AlphamaskTexture")),
            });
            s_now = Expression.Lambda<Func<uint>>(
                Expression.Call(GameReflection.Method(screenDecals, "GetTimeStampSinceStart"))).Compile();
            s_quality = Compiled.StaticGetter<float>(screenDecals, "m_qualityFactor");
            s_camera = Compiled.StaticGetter<Vector3D>(GameReflection.Type(render11, "VRageRender.MyRender11"),
                "Environment.Matrices.CameraPosition");
        }

        public static void Snapshot()
        {
            s_count = 0;
            IDictionary toDraw = s_toDraw();
            IDictionary materials = s_materials();
            if (toDraw != null && materials != null && toDraw.Count != 0)
            {
                uint now = s_now();
                float quality = s_quality();
                Vector3D camera = s_camera();
                foreach (DictionaryEntry entry in toDraw)
                {
                    if (!(entry.Value is IList decals) || decals.Count == 0)
                        continue;
                    MaterialEntry material = Material(entry.Key, materials);
                    if (material == null || material.Handle == 0)
                        continue;
                    foreach (object decal in decals)
                        Add(decal, material, now, quality, in camera);
                }
            }
            NativeApi.RtDecalsSubmit(s_decals, s_count);
            s_decalSum += s_count;
        }

        // The native material of a decal material's textures (read anew: the game registers its decal materials again
        // when their definitions reload).
        private static MaterialEntry Material(object identity, IDictionary materials)
        {
            MyStringId id = s_identityMaterial(identity);
            int index = s_identityIndex(identity);
            if (!(materials[id] is IList list) || index < 0 || index >= list.Count || list[index] == null)
                return null;
            var textures = new TexturesState();
            s_readTextures(list[index], ref textures);
            long key = ((long)id.Id << 32) | (uint)index;
            if (s_materialEntries.TryGetValue(key, out MaterialEntry cached) &&
                ReferenceEquals(cached.ColorMetal, textures.ColorMetal) &&
                ReferenceEquals(cached.NormalGloss, textures.NormalGloss) &&
                ReferenceEquals(cached.Extensions, textures.Extensions) &&
                ReferenceEquals(cached.Alphamask, textures.Alphamask))
                return cached;
            s_textures[0] = textures.ColorMetal;
            s_textures[1] = textures.NormalGloss;
            s_textures[2] = textures.Extensions;
            s_textures[3] = textures.Alphamask;
            var entry = new MaterialEntry
            {
                ColorMetal = textures.ColorMetal,
                NormalGloss = textures.NormalGloss,
                Extensions = textures.Extensions,
                Alphamask = textures.Alphamask,
                Handle = SceneMirror.Material(s_textures, 0),
                Maps = (uint)(textures.Type & 7),
            };
            s_materialEntries[key] = entry;
            if (s_loggedMaterials++ < MaxLoggedMaterials)
            {
                Log.Info($"Decal mirror: {id}#{index} -> material {entry.Handle} (maps {entry.Maps}; textures " +
                         $"{TypeName(textures.ColorMetal)}, {TypeName(textures.NormalGloss)}, " +
                         $"{TypeName(textures.Extensions)}, {TypeName(textures.Alphamask)}).");
            }
            return entry;
        }

        private static string TypeName(object texture) => texture?.GetType().Name ?? "none";

        private static void Add(object decal, MaterialEntry material, uint now, float quality, in Vector3D camera)
        {
            var state = new DecalState();
            s_readDecal(decal, ref state);
            if ((state.Flags & FlagClosed) != 0)
                return;
            MatrixD world;
            if ((state.Flags & FlagWorld) != 0)
            {
                world = state.TopoData.MatrixCurrent;
                world.Translation = state.TopoData.WorldPosition;
            }
            else
            {
                if (state.ParentIds == null || state.ParentIds.Length == 0)
                    return;
                MyActor parent = MyIDTracker<MyActor>.FindByID(state.ParentIds[0]);
                if (parent == null)
                    return;
                MatrixD binding = state.TopoData.MatrixCurrent;
                world = binding * parent.WorldMatrix;
            }
            // PrepareMaterialBatches: the last DECAL_FADE_DURATION before it goes, and toward its render distance.
            uint left = unchecked(state.FadeTimestamp - now);
            float fade = left < FadeDuration ? left / (float)FadeDuration : 1f;
            float distance = ((Vector3)(world.Translation - camera)).LengthSquared();
            float reach = state.RenderSqDistance * quality * 0.9f;
            float far = reach > 0 ? Math.Min(Math.Max(distance / reach, 0f), 1f) : 1f;
            fade *= 1 - far;
            if (!(fade > 0))
                return;
            if (s_count == s_decals.Length)
                Array.Resize(ref s_decals, s_decals.Length * 2);
            ref RtDecal entry = ref s_decals[s_count++];
            entry.Translation[0] = world.M41;
            entry.Translation[1] = world.M42;
            entry.Translation[2] = world.M43;
            entry.Rotation[0] = (float)world.M11;
            entry.Rotation[1] = (float)world.M12;
            entry.Rotation[2] = (float)world.M13;
            entry.Rotation[3] = (float)world.M21;
            entry.Rotation[4] = (float)world.M22;
            entry.Rotation[5] = (float)world.M23;
            entry.Rotation[6] = (float)world.M31;
            entry.Rotation[7] = (float)world.M32;
            entry.Rotation[8] = (float)world.M33;
            entry.Material = material.Handle;
            entry.Maps = material.Maps;
            entry.Fade = fade;
        }

        public static void Report(double frames)
        {
            Log.Info($"Decal mirror: {s_decalSum / frames:F1} projected decals per frame; {s_materialEntries.Count} decal " +
                     "materials known.");
            s_decalSum = 0;
        }
    }
}
