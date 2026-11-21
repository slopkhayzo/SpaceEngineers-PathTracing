using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using VRage.Generics;
using VRage.Render.Scene;
using VRage.Render.Scene.Components;
using VRageMath;
using VRageMath.PackedVector;

namespace SpaceEngineers.RTHWAccel
{
    // The scene mirror's managed half (research/docs/space-engineers-rt-plan.md
    // §5.6, phase 2). Every frame, at the start of MyRender11.DrawGameScene
    // (render thread; the frame's messages and transform updates are applied,
    // the parallel culling and recording have not started), the render scene
    // is walked and every object goes to the native core with its world
    // matrix:
    //
    //   GeometryStage2 instances (MyManagers.Instances): functional blocks,
    //     subparts, tools; geometry per MyLod.
    //   Old-pipeline renderables (MyComponentFactory's MyRenderableComponent
    //     pool): models, the cube parts of the grids' render cells, whose
    //     blocks are records of a cube instancing buffer, and generic instances
    //     (trees, bushes) of a generic instancing buffer; the records are
    //     expanded into TLAS instances on the GPU; geometry per LOD mesh.
    //   Voxel cells (MyComponentFactory's MyVoxelCellComponent pool).
    //   Lamps: the point and spot lights (LampMirror).
    //   Projected decals: bullet holes, impact marks, trails (DecalMirror).
    //   Skinned renderables (characters): their LOD mesh with the bone
    //     matrices of the frame; the native core poses it on the GPU.
    //
    // A mesh is registered the first time an object uses it (the native core
    // copies the game's buffers at the next gate) and released when no object
    // used it for a while or its buffers changed. Old-pipeline renderables bind
    // their own materials (block skins): one whose materials differ from those
    // its mesh was registered with gets a variant of the mesh (same geometry
    // and BLAS, its own geometry records). A GeometryStage2 instance with a
    // skin (armor panels, interior walls) draws its model instance's textures:
    // a variant of its LOD's mesh likewise. Not mirrored yet: static groups,
    // transparent parts, grass.
    internal static unsafe class SceneMirror
    {
        private const uint MaskPrimary = 1;   // drawn in the main view
        private const uint MaskShadow = 2;    // casts shadows
        private const int SkipInMainView = 0x4;  // MyRenderableProxyFlags
        private const uint InstanceHiddenParts = 2;  // sert::rt::kInstanceHiddenParts
        private const int MetalnessColorableFlag = 0x800;
        private const int EvictAfterFrames = 600;
        private const int ReportFrames = 600;

        // Draw techniques (VRageRender.Import.MyMeshDrawTechnique, a byte: 6-8 the decals, 9 HOLO, 15 GLASS, 18
        // ATMOSPHERE, 19 CLOUD_LAYER, 20-21 the shields).
        private const int TechniqueAlphaMasked = 3;
        private const int TechniqueAlphaMaskedSingleSided = 4;
        private const int TechniqueDecal = 6;
        private const int TechniqueDecalNoPremult = 7;
        private const int TechniqueDecalCutout = 8;
        private const int TechniqueHolo = 9;
        private const int TechniqueGlass = 15;
        private const int TechniqueAtmosphere = 18;
        private const int TechniqueShieldLit = 21;

        private static delegate*<object, ref object, void> s_drawGameScene;
        private static delegate*<int, object, object, int, void> s_updateCube;
        private static delegate*<int, void> s_rebuildGeneric;
        private static volatile bool s_enabled;
        private static int s_errors;

        // The *State structs are filled by the compiled readers (Reader below),
        // which the compiler cannot see.
#pragma warning disable CS0649
        // GeometryStage2.
        internal struct InstanceState
        {
            public object Owner;   // MyInstanceComponent (an MyActorComponent)
            public object Model;   // MyModel
            public int Lod;        // LodStrategy.m_currentLod (-1: not drawn)
            public int ExplicitState;  // MyInstanceLodState: 2 hologram
            public bool Dummy;
            public bool GBuffer;
            public bool Depth;
            public HalfVector4 KeyColor;  // paint: HSV offset
            public bool MetalnessColorable;
        }
        internal delegate void InstanceReader(object instance, ref InstanceState state);
        private static Func<object> s_instances;  // HashSet<MyInstance>
        private static InstanceReader s_readInstance;
        private static Func<object, bool> s_modelValid;
        private static Func<object, IList> s_modelLods;
        private static Func<object, object> s_lodVb0;
        private static Func<object, object> s_lodVb1;
        private static Func<object, object> s_lodIb;
        private static Func<object, IList> s_lodGbufferParts;  // List<MyPreprocessedPart>
        private static Func<object, int> s_partIndexStart;      // boxed MyPreprocessedPart
        private static Func<object, int> s_partIndexCount;
        private static Func<object, int> s_partTechnique;
        private static Func<object, bool> s_partDoubleSided;  // RasterizerState is the no-cull state
        private static Func<object, object[]> s_partTextures; // Material.Binding.Srvs: ColorMetal, NormalGloss, Extensions, Alphamask
        // The color multiplier and emissivity the game sets per object and material name
        // (MyRenderProxy.UpdateColorEmissivity: status lights, screens, lights switched off): a part's
        // offset within its LOD (-1: the defaults), the LOD's offsets into the model's instance materials,
        // an instance's value at a model offset (MyInstance.m_instanceMaterials, packed as half floats).
        private static Func<object, int> s_partMaterialOffset;   // MyPreprocessedPart.InstanceMaterialOffsetWithinLod
        private static Func<object, IList> s_lodMaterialOffsets; // MyLod.GetInstanceMaterialOffsets()
        private static Func<object, int, Vector4> s_instanceMaterial;
        // Skins on GeometryStage2 instances (armor panels, interior walls...: MyInstance.AddTextureChanges): the model
        // instance an instance draws with (MyModel.GetInstance(changes), shared by the instances with the same
        // changes), its texture changes (null or empty: the model's own materials) and per LOD its parts' bindings
        // (MyLodInstance.GBufferParts: the LOD's GBufferParts in their order, the changes applied).
        private static Func<object, object> s_skinnedModelInstance;  // MyInstance.ModelInstance if it has changes, else null
        private static Func<object, object[]> s_modelInstanceLods;    // MyModelInstance.LodInstances
        private static Func<object, int, object[]> s_lodInstanceSrvs;     // MyLodInstance.GBufferParts[i].Srvs, or null
        private static Func<object, int, object> s_lodInstanceAlphamask;  // MyLodInstance.GBufferParts[i].SrvAlphamask, or null
        private static readonly object[] s_boundTextures = new object[4];
        private static Func<int, object[]> s_proxyTextures;   // MyMaterials1.ProxyPool.Data[i].MaterialSrvs.Srvs
        private static Type s_fileTextureType;                // MyFileTexture: what a texture slot holds when it is a file
        private static Func<object, string> s_texturePath;    // MyFileTexture.m_path (absolute)
        private static Type s_generatedTextureType;           // IGeneratedTexture: screens' render targets, the game's defaults
        private static Func<object, SharpDX.Direct3D11.Resource> s_textureResource;  // ITexture.Resource

        // Buffers (IBuffer, IIndexBuffer).
        private static Func<object, SharpDX.Direct3D11.Buffer> s_bufferNative;
        private static Func<object, int> s_bufferElements;
        private static Func<object, int> s_bufferStride;
        private static Func<object, int> s_indexFormat;

        // Old pipeline and voxels.
        internal struct RenderableState
        {
            public bool Standalone;  // m_isRenderedStandalone
            public bool Built;       // CullProxy.Parent != null: proxies exist
            public int Lod;
            public object[] Lods;    // MyRenderLod[]
            public Vector3 KeyColor; // m_keyColor: paint of a renderable without instancing
        }
        internal delegate void RenderableReader(object component, ref RenderableState state);
        internal struct ProxyState
        {
            public int Mesh;  // LodMeshId.Index
            public int Instancing;
            public int InstanceCount;
            public int StartInstance;
            public int Flags;
            public bool Skinned;
            public int Technique;
            public int IndexCount;
            public int StartIndex;
            public int BaseVertex;
            public bool DoubleSided;  // GbufferRasterizerState is the no-cull state
            public int MaterialProxy; // DrawSubmesh.MaterialId.Index: the bound textures, skins included
            public Vector3 ColorMul;  // CommonObjectData: the object's color multiplier and emissivity for the
            public float Emissive;    // part (UpdateColorEmissivity; 1 by default)
        }
        internal delegate void ProxyReader(object proxy, ref ProxyState state);
        // What decides a renderable's materials, read every frame.
        internal struct PartKeyState
        {
            public int Mesh;
            public int Technique;
            public int MaterialProxy;
        }
        internal delegate void PartKeyReader(object proxy, ref PartKeyState state);
        internal struct VoxelState
        {
            public bool Ready;
            public int Mesh;  // MeshId.Index
        }
        internal delegate void VoxelReader(object cell, ref VoxelState state);
#pragma warning restore CS0649
        private static Func<Dictionary<Type, MyRuntimeObjectsPool<MyActorComponent>>> s_pools;
        private static Type s_renderableType;
        private static Type s_voxelCellType;
        private static RenderableReader s_readRenderable;
        private static Func<object, object[]> s_lodProxies;
        private static ProxyReader s_readProxy;
        private static VoxelReader s_readVoxel;
        private static Func<int, int> s_lodMeshOf;     // MeshId.Index -> LodMeshId.Index of LOD 0, or -1
        private static Func<int, object> s_meshVb0;    // MyMeshes.LodMeshBuffers[i].VB0, or null
        private static Func<int, object> s_meshVb1;
        private static Func<int, object> s_meshIb;
        private static Func<int, int> s_meshVertices;  // MyMeshes.LodMeshInfos.Data[i].VerticesNum, or -1
        private static Func<int, int> s_meshIndices;
        private static Func<int, int> s_meshLayout;    // MyMeshes.LodMeshInfos.Data[i].Data.VertexLayout.Index, or -1
        private static Func<int, bool> s_layoutBones;  // MyVertexLayouts.Layouts.Data[i].HasBonesInfo
        private static Func<object, Matrix[]> s_proxySkinning;   // MyRenderableProxy.SkinningMatrices
        private static Func<object, int[]> s_proxyBonesMapping;  // DrawSubmesh.BonesMapping (null: bones 0-59)
        private static PartKeyReader s_readPartKey;

        // Voxel materials: MyVoxelMaterials.Table (texture files, by the index voxel vertices carry) and the
        // constants the game's triplanar shaders get (MyCommon.VoxelMaterialsConstants.m_entries).
        private static Func<int> s_voxelTableLength;
        private static Func<int, string[]>[] s_voxelPaths;  // ColorMetal XZnY, Y; NormalGloss XZnY, Y; Ext XZnY, Y
        private static Func<int, Vector4> s_voxelDistanceAndScale;
        private static Func<int, Vector3> s_voxelFar1;
        private static Func<int, float> s_voxelTilingScale;
        private static Func<int, Vector3> s_voxelFar2;
        private static Func<int, Vector3> s_voxelFar3;
        private static Func<int, Vector3> s_voxelFar3Color;
        private static Func<string, string> s_textureFullPath;  // MyResourceUtils.GetTextureFullPath
        private static Func<object> s_randomTexture;            // MyGeneratedTextureManager.RandomTex
        private static Func<object, Vector3> s_proxyVoxelOffset;  // VoxelCommonObjectData.VoxelOffset
        private static Func<object, Vector3> s_proxyVoxelScale;
        // MyManagers.EnvironmentProbe: the cubemaps the game's ambient light samples.
        private static Func<object> s_environmentProbe;
        private static Func<object, object> s_probeFar;    // FarCubemapFinal
        private static Func<object, object> s_probeClose;  // CloseCubemapFinal
        private static Func<object, SharpDX.Direct3D11.Resource> s_resourceOf;  // IResource.Resource
        private static IntPtr s_environmentFar;
        private static IntPtr s_environmentClose;
        private static Func<int, byte[]> s_instancingData;  // MyInstancing.Instancings.Data[i].Data, or null
        private static Func<int, int> s_instancingCount;    // .VisibleCapacity
        private static Func<int, int> s_instancingStride;

        private sealed class MeshEntry
        {
            public uint Handle;  // 0: the native core refused it
            public object Vb0;   // identity check: the game's vertex buffer object
            public int LastSeen;
            public int Validated;
            public uint[] Materials;                   // old-pipeline meshes: their parts' materials as registered
            public bool[] Hideable;                    // old-pipeline meshes: parts registered non-opaque (hideable)
            public bool Stale;                         // a part was hidden that is not: register anew
            public Dictionary<string, uint> Variants;  // other materials of the parts -> variant handle (0: refused)
        }

        // An old-pipeline renderable: the mesh or variant handle for the material
        // ids its proxies bind (Key).
        private sealed class RenderableEntry
        {
            public uint Mesh;
            public ulong Key;
            public uint Handle;
            public int LastSeen;
        }

        private sealed class InstancingEntry
        {
            public uint Handle;
            public byte[] Data;
            public int Count;
            public int LastSeen;
        }

        // A skinned model instance's LOD: the mesh (handle) it was resolved for, the variant drawn, the LOD's
        // GBufferParts indices of the registered parts and the four textures each was resolved with (the game swaps
        // them as they stream in or out: compared once per frame).
        private sealed class SkinnedLodEntry
        {
            public uint Mesh;
            public uint Handle;
            public int LastSeen;
            public int[] Parts;
            public object[] Bound;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static readonly Dictionary<object, MeshEntry> s_modelMeshes = new Dictionary<object, MeshEntry>(new ReferenceComparer());
        private static readonly Dictionary<int, MeshEntry> s_lodMeshes = new Dictionary<int, MeshEntry>();
        private static readonly Dictionary<object, RenderableEntry> s_renderables =
            new Dictionary<object, RenderableEntry>(new ReferenceComparer());
        private static uint[] s_variantMaterials = new uint[64];
        // Per skinned model instance (by reference), per LOD index; and this frame's last one resolved (instances of
        // one block type and skin often come in a row).
        private static readonly Dictionary<object, SkinnedLodEntry[]> s_skinnedModels =
            new Dictionary<object, SkinnedLodEntry[]>(new ReferenceComparer());
        private static object s_lastSkinned;
        private static int s_lastSkinnedLod;
        private static uint s_lastSkinnedMesh;
        private static uint s_lastSkinnedHandle;
        private static readonly Dictionary<int, InstancingEntry> s_instancings = new Dictionary<int, InstancingEntry>();
        private static readonly HashSet<int> s_dirtyInstancings = new HashSet<int>();
        private static readonly Dictionary<string, uint> s_materials = new Dictionary<string, uint>(StringComparer.Ordinal);
        private static readonly string[] s_paths = new string[4];
        private static readonly IntPtr[] s_rendered = new IntPtr[4];
        private static RtInstance[] s_frameInstances = new RtInstance[4096];
        private static RtBucket[] s_frameBuckets = new RtBucket[1024];
        private static RtMeshPart[] s_parts = new RtMeshPart[64];
        private static float[] s_overrides = new float[4096];  // this frame's part overrides, four floats each
        private static int s_overrideCount;                      // entries
        // This frame's skinned objects (RtSkin), their bones (12 floats each) and part palettes; while one is
        // gathered, its palettes so far (the matrices and mapping each came from, its first bone).
        private static RtSkin[] s_frameSkins = new RtSkin[16];
        private static float[] s_bones = new float[16 * 60 * 12];
        private static uint[] s_palettes = new uint[64];
        private static int s_skinCount;
        private static int s_boneCount;
        private static int s_paletteCount;
        private static Matrix[][] s_paletteMatrices = new Matrix[8][];
        private static int[][] s_paletteMappings = new int[8][];
        private static int[] s_paletteStarts = new int[8];
        private static int s_instanceCount;
        private static int s_bucketCount;
        private static int s_frame;

        // Statistics per report window.
        private static long s_ticks;
        private static long s_maxTicks;
        private static long s_instanceSum;
        private static long s_bucketSum;
        private static long s_blockSum;
        private static int s_registered;
        private static int s_refused;
        private static int s_released;
        private static int s_recordUploads;
        private static int s_skippedSkinned;
        private static long s_skinSum;
        private static long s_skinBoneSum;
        private static int s_materialsCreated;
        private static int s_variantsCreated;
        private static long s_overriddenInstances;
        private static long s_hiddenPartInstances;
        private static long s_skinnedInstances;  // GeometryStage2 instances drawn with their own textures (skins)
        private static int s_skinnedRebinds;     // skinned LODs resolved again: the game swapped a texture
        // Per old-pipeline LOD mesh, the parts ever seen hidden in the main view: registered non-opaque
        // (kPartHideable). Only grows, so that renderables hiding different parts of one mesh agree.
        private static readonly Dictionary<int, HashSet<int>> s_hideableParts = new Dictionary<int, HashSet<int>>();
        private static long s_reregistered;

        // `mirrorTransparent` ([Debug] MirrorTransparent): the transparent pass's parts mirrored as opaque surfaces,
        // as before P2r, for an A/B of leaving them out.
        public static void Install(Assembly render11, bool mirrorTransparent = false)
        {
            s_mirrorTransparent = mirrorTransparent;
            Resolve(render11);
            (MethodInfo Target, MethodInfo Detour)[] hooks = Hooks(render11);
            RenderThread.Run(() =>
            {
                using (var transaction = new HookTransaction())
                {
                    s_updateCube = (delegate*<int, object, object, int, void>)transaction.Install(hooks[0].Target, hooks[0].Detour);
                    s_drawGameScene = (delegate*<object, ref object, void>)transaction.Install(hooks[1].Target, hooks[1].Detour);
                    s_rebuildGeneric = (delegate*<int, void>)transaction.Install(hooks[2].Target, hooks[2].Detour);
                    transaction.Commit();
                }
            });
            s_enabled = true;
            Log.Info("Scene mirror hooks installed (MyRender11.DrawGameScene, MyInstancing.UpdateCube, MyInstancing.RebuildGeneric).");
        }

        // The hooked game methods and their detours, in Install's order (also
        // checked by the game-free tests).
        internal static (MethodInfo Target, MethodInfo Detour)[] Hooks(Assembly render11)
        {
            Type render = GameReflection.Type(render11, "VRageRender.MyRender11");
            Type rtvBindable = GameReflection.Type(render11, "VRage.Render11.Resources.IRtvBindable");
            Type borrowedRtv = GameReflection.Type(render11, "VRage.Render11.Resources.IBorrowedRtvTexture");
            Type instancing = GameReflection.Type(render11, "VRageRender.MyInstancing");
            return new[]
            {
                (instancing.GetMethods(GameReflection.All).Single(m => m.Name == "UpdateCube"), Own(nameof(UpdateCubeDetour))),
                (GameReflection.Method(render, "DrawGameScene", rtvBindable, borrowedRtv.MakeByRefType()), Own(nameof(DrawGameSceneDetour))),
                (instancing.GetMethods(GameReflection.All).Single(m => m.Name == "RebuildGeneric"), Own(nameof(RebuildGenericDetour))),
            };
        }

        private static MethodInfo Own(string name) =>
            typeof(SceneMirror).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

        // Every member the walk uses, compiled once; a miss throws before any
        // hook exists. Also run by the game-free tests (tests/ManagedTests.cs).
        internal static void Resolve(Assembly render11)
        {
            Type managers = GameReflection.Type(render11, "VRage.Render11.Common.MyManagers");
            Type instance = GameReflection.Type(render11, "VRage.Render11.GeometryStage2.Instancing.MyInstance");
            Type model = GameReflection.Type(render11, "VRage.Render11.GeometryStage2.Model.MyModel");
            Type lod = GameReflection.Type(render11, "VRage.Render11.GeometryStage2.Model.MyLod");
            Type part = GameReflection.Type(render11, "VRage.Render11.GeometryStage2.Model.Preprocess.MyPreprocessedPart");
            Type buffer = GameReflection.Type(render11, "VRage.Render11.Resources.IBuffer");
            Type indexBuffer = GameReflection.Type(render11, "VRage.Render11.Resources.IIndexBuffer");
            Type factory = GameReflection.Type(render11, "VRage.Render11.Scene.MyComponentFactory");
            s_renderableType = GameReflection.Type(render11, "VRage.Render11.Scene.Components.MyRenderableComponent");
            s_voxelCellType = GameReflection.Type(render11, "VRageRender.MyVoxelCellComponent");
            Type renderLod = GameReflection.Type(render11, "VRageRender.MyRenderLod");
            Type proxy = GameReflection.Type(render11, "VRageRender.MyRenderableProxy");
            Type meshes = GameReflection.Type(render11, "VRageRender.MyMeshes");
            Type meshId = GameReflection.Type(render11, "VRageRender.MeshId");
            Type instancings = GameReflection.Type(render11, "VRageRender.MyInstancing");
            // Parts the game draws without back-face culling share this state; null means culled.
            Expression noCull = Compiled.StaticMember(
                GameReflection.Type(render11, "VRage.Render11.Resources.MyRasterizerStateManager"), "NocullRasterizerState");

            s_instances = Compiled.StaticGetter<object>(managers, "Instances.m_instances.m_active");
            s_readInstance = Reader<InstanceReader>(instance, typeof(InstanceState), (i, set) => new[]
            {
                set(nameof(InstanceState.Owner), Compiled.Member(i, "Owner")),
                set(nameof(InstanceState.Model), Compiled.Member(i, "Model")),
                set(nameof(InstanceState.Lod), Compiled.Path(i, "LodStrategy.m_currentLod")),
                set(nameof(InstanceState.ExplicitState), Compiled.Path(i, "LodStrategy.m_explicitState")),
                set(nameof(InstanceState.Dummy), Compiled.Member(i, "IsDummyModel")),
                set(nameof(InstanceState.GBuffer), Expression.Call(i, GameReflection.Method(instance, "IsGBufferVisible"))),
                set(nameof(InstanceState.Depth), Expression.Call(i, GameReflection.Method(instance, "IsDepthVisible"))),
                set(nameof(InstanceState.KeyColor), Compiled.Member(i, "KeyColor")),
                set(nameof(InstanceState.MetalnessColorable), Compiled.Member(i, "MetalnessColorable")),
            });
            Type modelInstance = GameReflection.Type(render11, "VRage.Render11.GeometryStage2.Model.MyModelInstance");
            Type lodInstance = GameReflection.Type(render11, "VRage.Render11.GeometryStage2.Model.MyLodInstance");
            // o => { var own = ((MyInstance)o).ModelInstance; return own?.Changes?.Count > 0 ? own : null; }
            ParameterExpression boxedOwner = Expression.Parameter(typeof(object), "o");
            Expression ownValue = Compiled.Member(Expression.Convert(boxedOwner, instance), "ModelInstance");
            ParameterExpression own = Expression.Variable(ownValue.Type, "own");
            Expression changesValue = Compiled.Member(own, "Changes");
            ParameterExpression changes = Expression.Variable(changesValue.Type, "changes");
            s_skinnedModelInstance = Expression.Lambda<Func<object, object>>(Expression.Block(new[] { own, changes },
                Expression.Assign(own, ownValue),
                Expression.Condition(Expression.NotEqual(own, Expression.Constant(null, own.Type)),
                    Expression.Block(
                        Expression.Assign(changes, changesValue),
                        Expression.Condition(Expression.AndAlso(
                                Expression.NotEqual(changes, Expression.Constant(null, changes.Type)),
                                Expression.GreaterThan(Expression.Property(changes, "Count"), Expression.Constant(0))),
                            Expression.Convert(own, typeof(object)), Expression.Constant(null))),
                    Expression.Constant(null))), boxedOwner).Compile();
            s_modelInstanceLods = Compiled.Getter<object[]>(modelInstance, "LodInstances");
            s_lodInstanceSrvs = LodInstanceBinding<object[]>(lodInstance, "Srvs");
            s_lodInstanceAlphamask = LodInstanceBinding<object>(lodInstance, "SrvAlphamask");
            s_modelValid = Compiled.Getter<bool>(model, "IsValid");
            s_modelLods = Compiled.Getter<IList>(model, "m_lods");
            s_lodVb0 = Compiled.Getter<object>(lod, "VB0");
            s_lodVb1 = Compiled.Getter<object>(lod, "VB1");
            s_lodIb = Compiled.Getter<object>(lod, "IB");
            s_lodGbufferParts = Compiled.Getter<IList>(lod, "m_preprocessedParts.GBufferParts");
            s_partIndexStart = Compiled.Getter<int>(part, "IndexStart");
            s_partIndexCount = Compiled.Getter<int>(part, "IndicesCount");
            s_partTechnique = Compiled.Getter<int>(part, "Material.Technique");
            ParameterExpression boxedPart = Expression.Parameter(typeof(object), "o");
            s_partDoubleSided = Expression.Lambda<Func<object, bool>>(Expression.ReferenceEqual(
                Compiled.Member(Expression.Convert(boxedPart, part), "RasterizerState"), noCull), boxedPart).Compile();
            s_partTextures = Compiled.Getter<object[]>(part, "Material.Binding.Srvs");
            s_partMaterialOffset = Compiled.Getter<int>(part, "InstanceMaterialOffsetWithinLod");
            ParameterExpression boxedLod = Expression.Parameter(typeof(object), "o");
            s_lodMaterialOffsets = Expression.Lambda<Func<object, IList>>(Expression.Convert(Expression.Call(
                Expression.Convert(boxedLod, lod), GameReflection.Method(lod, "GetInstanceMaterialOffsets")),
                typeof(IList)), boxedLod).Compile();
            // (instance, offset) => cache != null && 0 <= offset < cache.Length
            //     ? cache[offset].PackedColorMultEmissivity.ToVector4() : Vector4.One
            ParameterExpression boxedInstance = Expression.Parameter(typeof(object), "o");
            ParameterExpression materialOffset = Expression.Parameter(typeof(int), "i");
            Expression cacheValue = Compiled.Path(Expression.Convert(boxedInstance, instance),
                "m_instanceMaterials.m_instanceMaterialsCache");
            ParameterExpression cache = Expression.Variable(cacheValue.Type, "cache");
            Expression packed = Compiled.Member(Expression.ArrayIndex(cache, materialOffset), "PackedColorMultEmissivity");
            s_instanceMaterial = Expression.Lambda<Func<object, int, Vector4>>(Expression.Block(new[] { cache },
                Expression.Assign(cache, cacheValue),
                Expression.Condition(Expression.AndAlso(Expression.NotEqual(cache, Expression.Constant(null, cache.Type)),
                    Expression.AndAlso(Expression.GreaterThanOrEqual(materialOffset, Expression.Constant(0)),
                        Expression.LessThan(materialOffset, Expression.ArrayLength(cache)))),
                    Expression.Call(packed, typeof(HalfVector4).GetMethod(nameof(HalfVector4.ToVector4), Type.EmptyTypes)),
                    Expression.Constant(Vector4.One))), boxedInstance, materialOffset).Compile();
            s_fileTextureType = GameReflection.Type(render11, "VRage.Render11.Resources.Internal.MyFileTexture");
            s_texturePath = Compiled.Getter<string>(s_fileTextureType, "m_path");
            s_generatedTextureType = GameReflection.Type(render11, "VRage.Render11.Resources.IGeneratedTexture");
            s_textureResource = Compiled.Getter<SharpDX.Direct3D11.Resource>(s_generatedTextureType, "Resource");
            s_proxyTextures = Indexed<object[]>(
                Compiled.Path(Compiled.StaticMember(GameReflection.Type(render11, "VRageRender.MyMaterials1"), "ProxyPool"), "Data"),
                "MaterialSrvs.Srvs", null);

            s_bufferNative = Compiled.Getter<SharpDX.Direct3D11.Buffer>(buffer, "Buffer");
            s_bufferElements = Compiled.Getter<int>(buffer, "ElementCount");
            s_bufferStride = Compiled.Getter<int>(buffer, "Description.StructureByteStride");
            s_indexFormat = Compiled.Getter<int>(indexBuffer, "Format");

            s_pools = Compiled.StaticGetter<Dictionary<Type, MyRuntimeObjectsPool<MyActorComponent>>>(factory, "m_pools");
            LampMirror.Resolve(render11);
            DecalMirror.Resolve(render11);
            s_readRenderable = Reader<RenderableReader>(s_renderableType, typeof(RenderableState), (c, set) =>
            {
                Expression cull = Compiled.Member(c, "CullProxy");
                Expression parent = Compiled.Member(cull, "Parent");
                return new[]
                {
                    set(nameof(RenderableState.Standalone), Compiled.Member(c, "m_isRenderedStandalone")),
                    set(nameof(RenderableState.Built), Expression.AndAlso(
                        Expression.NotEqual(cull, Expression.Constant(null, cull.Type)),
                        Expression.NotEqual(parent, Expression.Constant(null, parent.Type)))),
                    set(nameof(RenderableState.Lod), Compiled.Member(c, "CurrentLod")),
                    set(nameof(RenderableState.Lods), Compiled.Member(c, "m_lods")),
                    set(nameof(RenderableState.KeyColor), Compiled.Member(c, "m_keyColor")),
                };
            });
            s_lodProxies = Compiled.Getter<object[]>(renderLod, "RenderableProxies");
            s_readProxy = Reader<ProxyReader>(proxy, typeof(ProxyState), (p, set) => new[]
            {
                set(nameof(ProxyState.Mesh), Compiled.Path(p, "Mesh.Index")),
                set(nameof(ProxyState.Instancing), Compiled.Path(p, "Instancing.Index")),
                set(nameof(ProxyState.InstanceCount), Compiled.Member(p, "InstanceCount")),
                set(nameof(ProxyState.StartInstance), Compiled.Member(p, "StartInstance")),
                set(nameof(ProxyState.Flags), Compiled.Member(p, "Flags")),
                set(nameof(ProxyState.Skinned), Expression.NotEqual(Compiled.Member(p, "SkinningMatrices"),
                    Expression.Constant(null, typeof(Matrix[])))),
                set(nameof(ProxyState.Technique), Compiled.Member(p, "Technique")),
                set(nameof(ProxyState.IndexCount), Compiled.Path(p, "DrawSubmesh.IndexCount")),
                set(nameof(ProxyState.StartIndex), Compiled.Path(p, "DrawSubmesh.StartIndex")),
                set(nameof(ProxyState.BaseVertex), Compiled.Path(p, "DrawSubmesh.BaseVertex")),
                set(nameof(ProxyState.DoubleSided), Expression.ReferenceEqual(Compiled.Member(p, "GbufferRasterizerState"), noCull)),
                set(nameof(ProxyState.MaterialProxy), Compiled.Path(p, "DrawSubmesh.MaterialId.Index")),
                set(nameof(ProxyState.ColorMul), Compiled.Path(p, "CommonObjectData.ColorMul")),
                set(nameof(ProxyState.Emissive), Compiled.Path(p, "CommonObjectData.Emissive")),
            });
            s_proxySkinning = Compiled.Getter<Matrix[]>(proxy, "SkinningMatrices");
            s_proxyBonesMapping = Compiled.Getter<int[]>(proxy, "DrawSubmesh.BonesMapping");
            s_readVoxel = Reader<VoxelReader>(s_voxelCellType, typeof(VoxelState), (v, set) => new[]
            {
                set(nameof(VoxelState.Ready), Compiled.Path(v, "m_voxelMesh.Ready")),
                set(nameof(VoxelState.Mesh), Compiled.Path(v, "m_voxelMesh.m_mesh.Index")),
            });

            // MyMeshes.TryGetLodMesh(new MeshId { Index = i }, 0, out LodMeshId l) ? l.Index : -1
            // (GetLodMesh throws for a mesh that is gone).
            ParameterExpression index = Expression.Parameter(typeof(int), "i");
            MethodInfo tryGetLodMesh = meshes.GetMethods(GameReflection.All).Single(m => m.Name == "TryGetLodMesh");
            Type lodMeshId = tryGetLodMesh.GetParameters()[2].ParameterType.GetElementType();
            ParameterExpression found = Expression.Variable(lodMeshId, "lodMesh");
            Expression mesh = Expression.MemberInit(Expression.New(meshId), Expression.Bind(GameReflection.Field(meshId, "Index"), index));
            s_lodMeshOf = Expression.Lambda<Func<int, int>>(Expression.Block(new[] { found },
                Expression.Condition(Expression.Call(tryGetLodMesh, mesh, Expression.Constant(0), found),
                    Compiled.Member(found, "Index"), Expression.Constant(-1))), index).Compile();
            s_meshVb0 = Indexed<object>(Compiled.StaticMember(meshes, "LodMeshBuffers"), "VB0", null);
            s_meshVb1 = Indexed<object>(Compiled.StaticMember(meshes, "LodMeshBuffers"), "VB1", null);
            s_meshIb = Indexed<object>(Compiled.StaticMember(meshes, "LodMeshBuffers"), "IB", null);
            Expression infos = Compiled.Path(Compiled.StaticMember(meshes, "LodMeshInfos"), "Data");
            s_meshVertices = Indexed<int>(infos, "VerticesNum", -1);
            s_meshIndices = Indexed<int>(infos, "IndicesNum", -1);
            s_meshLayout = Indexed<int>(infos, "Data.VertexLayout.Index", -1);
            s_layoutBones = Indexed<bool>(Compiled.Path(Compiled.StaticMember(
                GameReflection.Type(render11, "VRageRender.MyVertexLayouts"), "Layouts"), "Data"), "HasBonesInfo", false);
            Expression voxelTable = Compiled.StaticMember(GameReflection.Type(render11, "VRageRender.MyVoxelMaterials"), "Table");
            s_voxelTableLength = Expression.Lambda<Func<int>>(Expression.Condition(
                Expression.Equal(voxelTable, Expression.Constant(null, voxelTable.Type)),
                Expression.Constant(0), Expression.ArrayLength(voxelTable))).Compile();
            s_voxelPaths = new[] { "ColorMetalXZnY", "ColorMetalY", "NormalGlossXZnY", "NormalGlossY", "ExtXZnY", "ExtY" }
                .Select(set => Indexed<string[]>(voxelTable, "Resource." + set + "_Filepaths", null)).ToArray();
            Expression voxelEntries = Compiled.Path(Compiled.StaticMember(
                GameReflection.Type(render11, "VRageRender.MyCommon"), "VoxelMaterialsConstants"), "m_entries");
            s_voxelDistanceAndScale = Indexed<Vector4>(voxelEntries, "StandardTilingSetup.DistanceAndScale", default);
            s_voxelFar1 = Indexed<Vector3>(voxelEntries, "StandardTilingSetup.DistanceAndScaleFar", default);
            s_voxelTilingScale = Indexed<float>(voxelEntries, "StandardTilingSetup.TilingScale", 0f);
            s_voxelFar2 = Indexed<Vector3>(voxelEntries, "StandardTilingSetup.DistanceAndScaleFar2", default);
            s_voxelFar3 = Indexed<Vector3>(voxelEntries, "StandardTilingSetup.DistanceAndScaleFar3", default);
            s_voxelFar3Color = Indexed<Vector3>(voxelEntries, "Far3Color", default);
            MethodInfo fullPath = GameReflection.Type(render11, "VRage.Render11.Resources.MyResourceUtils")
                .GetMethods(GameReflection.All).Single(m => m.Name == "GetTextureFullPath");
            ParameterExpression textureName = Expression.Parameter(typeof(string), "name");
            s_textureFullPath = Expression.Lambda<Func<string, string>>(
                Expression.Call(fullPath, textureName, Expression.Constant(null, typeof(string))), textureName).Compile();
            s_randomTexture = Compiled.StaticGetter<object>(
                GameReflection.Type(render11, "VRage.Render11.Resources.MyGeneratedTextureManager"), "RandomTex");
            Type environmentProbe = GameReflection.Type(render11, "VRage.Render11.LightingStage.EnvironmentProbe.MyEnvironmentProbe");
            s_environmentProbe = Compiled.StaticGetter<object>(managers, "EnvironmentProbe");
            s_probeFar = Compiled.Getter<object>(environmentProbe, "FarCubemapFinal");
            s_probeClose = Compiled.Getter<object>(environmentProbe, "CloseCubemapFinal");
            s_resourceOf = Compiled.Getter<SharpDX.Direct3D11.Resource>(
                GameReflection.Type(render11, "VRage.Render11.Resources.IResource"), "Resource");
            s_proxyVoxelOffset = Compiled.Getter<Vector3>(proxy, "VoxelCommonObjectData.VoxelOffset");
            s_proxyVoxelScale = Compiled.Getter<Vector3>(proxy, "VoxelCommonObjectData.VoxelScale");
            s_readPartKey = Reader<PartKeyReader>(proxy, typeof(PartKeyState), (p, set) => new[]
            {
                set(nameof(PartKeyState.Mesh), Compiled.Path(p, "Mesh.Index")),
                set(nameof(PartKeyState.Technique), Compiled.Member(p, "Technique")),
                set(nameof(PartKeyState.MaterialProxy), Compiled.Path(p, "DrawSubmesh.MaterialId.Index")),
            });
            Expression instancingInfos = Compiled.Path(Compiled.StaticMember(instancings, "Instancings"), "Data");
            s_instancingData = Indexed<byte[]>(instancingInfos, "Data", null);
            s_instancingCount = Indexed<int>(instancingInfos, "VisibleCapacity", 0);
            s_instancingStride = Indexed<int>(instancingInfos, "Stride", 0);
        }

        // (lodInstance, i) => parts != null && 0 <= i < parts.Length ? (T)parts[i].member : null, parts being the
        // model instance LOD's GBufferParts (MyRenderMaterialBindings).
        private static Func<object, int, T> LodInstanceBinding<T>(Type lodInstance, string member) where T : class
        {
            ParameterExpression boxed = Expression.Parameter(typeof(object), "o");
            ParameterExpression index = Expression.Parameter(typeof(int), "i");
            Expression value = Compiled.Member(Expression.Convert(boxed, lodInstance), "GBufferParts");
            ParameterExpression parts = Expression.Variable(value.Type, "parts");
            return Expression.Lambda<Func<object, int, T>>(Expression.Block(new[] { parts },
                Expression.Assign(parts, value),
                Expression.Condition(Expression.AndAlso(Expression.NotEqual(parts, Expression.Constant(null, parts.Type)),
                    Expression.AndAlso(Expression.GreaterThanOrEqual(index, Expression.Constant(0)),
                        Expression.LessThan(index, Expression.ArrayLength(parts)))),
                    Compiled.To<T>(Compiled.Member(Expression.ArrayIndex(parts, index), member)),
                    Expression.Constant(null, typeof(T)))), boxed, index).Compile();
        }

        // (object o, ref TState state) => { var x = (type)o; state.A = ...; state.B = ...; }
        internal static TDelegate Reader<TDelegate>(Type type, Type stateType,
            Func<Expression, Func<string, Expression, Expression>, Expression[]> assignments)
        {
            ParameterExpression input = Expression.Parameter(typeof(object), "o");
            ParameterExpression state = Expression.Parameter(stateType.MakeByRefType(), "state");
            ParameterExpression typed = Expression.Variable(type, "x");
            Func<string, Expression, Expression> set = (field, value) =>
            {
                MemberExpression target = Expression.Field(state, field);
                return Expression.Assign(target, value.Type == target.Type ? value : Expression.Convert(value, target.Type));
            };
            var body = new List<Expression> { Expression.Assign(typed, Expression.Convert(input, type)) };
            body.AddRange(assignments(typed, set));
            return Expression.Lambda<TDelegate>(Expression.Block(new[] { typed }, body), input, state).Compile();
        }

        // i => i >= 0 && i < array.Length ? array[i].member : fallback, the array read anew on every call
        // (the game replaces it when it grows).
        private static Func<int, T> Indexed<T>(Expression array, string member, T fallback)
        {
            ParameterExpression index = Expression.Parameter(typeof(int), "i");
            ParameterExpression local = Expression.Variable(array.Type, "a");
            Expression inRange = Expression.AndAlso(
                Expression.GreaterThanOrEqual(index, Expression.Constant(0)),
                Expression.LessThan(index, Expression.ArrayLength(local)));
            Expression value = Compiled.To<T>(Compiled.Path(Expression.ArrayIndex(local, index), member));
            Expression body = Expression.Block(new[] { local },
                Expression.Assign(local, array),
                Expression.Condition(Expression.AndAlso(Expression.NotEqual(local, Expression.Constant(null, array.Type)), inRange),
                    value, Expression.Constant(fallback, typeof(T))));
            return Expression.Lambda<Func<int, T>>(body, index).Compile();
        }

        // Render thread: cube block records changed (an edit in a render cell).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UpdateCubeDetour(int id, object instanceData, object decals, int capacity)
        {
            s_updateCube(id, instanceData, decals, capacity);
            s_dirtyInstancings.Add(id);
        }

        // Render thread: generic instance records rebuilt (trees added, cut or hidden).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RebuildGenericDetour(int id)
        {
            s_rebuildGeneric(id);
            s_dirtyInstancings.Add(id);
        }

        // Render thread, before the frame's scene is drawn.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DrawGameSceneDetour(object renderTarget, ref object debugAmbientOcclusion)
        {
            // Only gates that run D3D12 work consume the mirror.
            if (s_enabled && NativeApi.CurrentGateMode >= GateMode.Overlay)
            {
                try
                {
                    Snapshot();
                }
                catch (Exception exception)
                {
                    if (Interlocked.Increment(ref s_errors) == 1)
                        Log.Error("Scene mirror: walking the render scene failed", exception);
                    if (s_errors >= 10)
                    {
                        s_enabled = false;
                        Log.Error("Scene mirror: switched off after repeated failures.");
                    }
                }
            }
            s_drawGameScene(renderTarget, ref debugAmbientOcclusion);
        }

        private static void Snapshot()
        {
            long start = Stopwatch.GetTimestamp();
            ++s_frame;
            s_lastSkinned = null;
            s_instanceCount = 0;
            s_bucketCount = 0;
            s_overrideCount = 0;
            s_skinCount = 0;
            s_boneCount = 0;
            s_paletteCount = 0;
            long blocks = 0;

            if (s_instances() is IEnumerable instances)
            {
                foreach (object instance in instances)
                    ModelInstance(instance);
            }
            // The factory pools components by their exact type: voxel cells have
            // their own pool (MyComponentFactory<MyVoxelCellComponent>), although
            // their actors file them under the renderable slot.
            Dictionary<Type, MyRuntimeObjectsPool<MyActorComponent>> pools = s_pools();
            if (pools != null && pools.TryGetValue(s_renderableType, out MyRuntimeObjectsPool<MyActorComponent> renderables))
            {
                foreach (MyActorComponent component in renderables.Active)
                {
                    if (component.GetType() == s_renderableType)
                        blocks += Renderable(component);
                }
            }
            if (pools != null && pools.TryGetValue(s_voxelCellType, out MyRuntimeObjectsPool<MyActorComponent> cells))
            {
                bool any = false;
                foreach (MyActorComponent component in cells.Active)
                    any |= VoxelCell(component);
                if (any)
                    VoxelMaterials();
            }
            NativeApi.RtSceneSubmit(s_frameInstances, s_instanceCount, s_frameBuckets, s_bucketCount, s_overrides,
                s_overrideCount);
            NativeApi.RtSkinsSubmit(s_frameSkins, s_skinCount, s_bones, s_boneCount, s_palettes, s_paletteCount);
            LampMirror.Snapshot(pools);
            DecalMirror.Snapshot();
            SendEnvironment();

            if (s_frame % EvictAfterFrames == 0)
                Evict();
            long ticks = Stopwatch.GetTimestamp() - start;
            s_ticks += ticks;
            s_maxTicks = Math.Max(s_maxTicks, ticks);
            s_instanceSum += s_instanceCount;
            s_bucketSum += s_bucketCount;
            s_skinSum += s_skinCount;
            s_skinBoneSum += s_boneCount;
            s_blockSum += blocks;
            if (s_frame % ReportFrames == 0 && NativeApi.DiagnosticsEnabled)
                Report();
        }

        private static void ModelInstance(object instance)
        {
            var state = new InstanceState();
            s_readInstance(instance, ref state);
            if (state.Dummy || state.Model == null || state.ExplicitState == 2 || !(state.Owner is MyActorComponent owner))
                return;
            IMyActor actor = owner.Owner;
            if (actor == null || actor.IsDestroyed || !actor.IsVisible)
                return;
            // A LOD of -1 is not drawn now (fading out, or never in view): a shadow caster only.
            uint mask = (state.GBuffer && state.Lod >= 0 ? MaskPrimary : 0) | (state.Depth ? MaskShadow : 0);
            if (mask == 0 || !s_modelValid(state.Model))
                return;
            IList lods = s_modelLods(state.Model);
            if (lods == null || lods.Count == 0)
                return;
            int lodIndex = Math.Max(0, Math.Min(state.Lod, lods.Count - 1));
            object lod = lods[lodIndex];
            uint mesh = ModelMesh(lod);
            if (mesh != 0)
            {
                // A skin: the instance draws its own model instance's textures.
                object own = s_skinnedModelInstance(instance);
                if (own != null)
                {
                    mesh = SkinnedModelMesh(own, lodIndex, lod, mesh);
                    ++s_skinnedInstances;
                }
                Vector4 key = state.KeyColor.ToVector4();
                AddInstance(mesh, mask, actor.ID, in actor.WorldMatrix, new Vector3(key.X, key.Y, key.Z),
                    state.MetalnessColorable ? 1u : 0u, 0, ModelOverrides(instance, lod));
            }
        }

        // The instance's color multiplier and emissivity per part its LOD's mesh was registered with
        // (ModelMesh's order), as the game's color prepare pass finds them (MyColorPreparePass0: a part's
        // offset within the LOD + 1, slot 0 the defaults): 0 when all are the defaults, else the
        // RtInstance.Overrides of the entries added.
        private static uint ModelOverrides(object instance, object lod)
        {
            IList parts = s_lodGbufferParts(lod);
            if (parts == null)
                return 0;
            IList offsets = null;
            int start = s_overrideCount;
            bool any = false;
            foreach (object part in parts)
            {
                int technique = s_partTechnique(part);
                if (Unmirrored(technique) || s_partIndexCount(part) <= 0 || s_partIndexStart(part) < 0)
                    continue;  // not registered (ModelMesh, AddPart)
                Vector4 value = Vector4.One;
                int withinLod = s_partMaterialOffset(part);
                if (withinLod >= 0)
                {
                    offsets = offsets ?? s_lodMaterialOffsets(lod);
                    if (offsets != null && withinLod < offsets.Count)
                        value = s_instanceMaterial(instance, (int)offsets[withinLod]);
                }
                any |= value != Vector4.One;
                AddOverride(value);
            }
            return EndOverrides(start, any);
        }

        // The same for an old-pipeline renderable, from its registered proxies (LodMesh's order). A part the game
        // hides in the main view (its proxy's SkipInMainView: UpdateModelProperties taking RenderFlags.Visible
        // away, as for a screen without content) gets the emissivity -1, which the trace takes for hidden
        // (scene_common.hlsli AlphaSolid). `visible`, `hidden`: whether any part is drawn in the main view,
        // whether any is not.
        private static uint RenderableOverrides(int meshIndex, object[] proxies, out bool visible, out bool hidden)
        {
            int start = s_overrideCount;
            bool any = false;
            visible = false;
            hidden = false;
            s_lodMeshes.TryGetValue(meshIndex, out MeshEntry entry);
            int index = 0;
            var proxy = new ProxyState();
            foreach (object part in proxies)
            {
                if (part == null)
                    continue;
                s_readProxy(part, ref proxy);
                if (!RegisteredPart(in proxy, meshIndex))
                    continue;
                bool skip = (proxy.Flags & SkipInMainView) != 0;
                visible |= !skip;
                hidden |= skip;
                if (skip && entry != null && (entry.Hideable == null || index >= entry.Hideable.Length || !entry.Hideable[index]))
                {
                    // Registered opaque: the rays could not pass it. Remembered, and the mesh registered anew.
                    if (!s_hideableParts.TryGetValue(meshIndex, out HashSet<int> known))
                        s_hideableParts[meshIndex] = known = new HashSet<int>();
                    known.Add(index);
                    entry.Stale = true;
                }
                ++index;
                var value = new Vector4(proxy.ColorMul, skip ? -1 : proxy.Emissive);
                any |= value != Vector4.One;
                AddOverride(value);
            }
            if (visible && hidden)
                ++s_hiddenPartInstances;
            return EndOverrides(start, any);
        }

        private static void AddOverride(Vector4 value)
        {
            if ((s_overrideCount + 1) * 4 > s_overrides.Length)
                Array.Resize(ref s_overrides, s_overrides.Length * 2);
            int at = s_overrideCount++ * 4;
            s_overrides[at] = value.X;
            s_overrides[at + 1] = value.Y;
            s_overrides[at + 2] = value.Z;
            s_overrides[at + 3] = value.W;
        }

        // Keeps the entries added since `start` if any differs from the defaults.
        private static uint EndOverrides(int start, bool any)
        {
            if (!any)
            {
                s_overrideCount = start;
                return 0;
            }
            ++s_overriddenInstances;
            return (uint)start + 1;
        }

        // Returns the number of cube blocks added.
        private static int Renderable(MyActorComponent component)
        {
            IMyActor actor = component.Owner;
            if (actor == null || actor.IsDestroyed || !actor.IsVisible)
                return 0;
            var state = new RenderableState();
            s_readRenderable(component, ref state);
            if (!state.Standalone || !state.Built || state.Lods == null || state.Lods.Length == 0)
                return 0;
            object[] proxies = s_lodProxies(state.Lods[Math.Max(0, Math.Min(state.Lod, state.Lods.Length - 1))]);
            if (proxies == null || proxies.Length == 0 || proxies[0] == null)
                return 0;
            var proxy = new ProxyState();
            s_readProxy(proxies[0], ref proxy);
            if (proxy.Skinned && proxy.Instancing >= 0 && proxy.InstanceCount > 0)
            {
                ++s_skippedSkinned;  // skinned and instanced: the game makes no such thing
                return 0;
            }
            uint mesh = LodMesh(proxy.Mesh, proxies, voxel: false);
            if (mesh == 0)
                return 0;
            mesh = MaterialVariant(component, mesh, proxy.Mesh, proxies);
            uint mask = (proxy.Flags & SkipInMainView) != 0 ? MaskShadow : MaskPrimary | MaskShadow;
            if (proxy.Instancing >= 0 && proxy.InstanceCount > 0)
            {
                uint instancing = Instancing(proxy.Instancing);
                if (instancing == 0)
                    return 0;
                AddBucket(mesh, instancing, proxy.StartInstance, proxy.InstanceCount, mask, in actor.WorldMatrix);
                return proxy.InstanceCount;
            }
            uint overrides = RenderableOverrides(proxy.Mesh, proxies, out bool visible, out bool hidden);
            if (visible || hidden)
                mask = visible ? MaskPrimary | MaskShadow : MaskShadow;
            AddInstance(mesh, mask, actor.ID, in actor.WorldMatrix, state.KeyColor, visible && hidden ? InstanceHiddenParts : 0,
                0, overrides);
            // A character: posed by the native core with this frame's bones; without any, left out rather than
            // drawn in its bind pose.
            if (proxy.Skinned && !AddSkin(proxy.Mesh, proxies))
            {
                --s_instanceCount;
                ++s_skippedSkinned;
            }
            return 0;
        }

        // The pose of the skinned renderable just added (the frame's last instance): per part its LOD mesh was
        // registered with (LodMesh's order), the bone palette the game gives the part's draw
        // (MyRenderableProxy.UpdateObjectBuffer): the proxy's bone matrices through its BonesMapping, or the first
        // 60 of them without one. Parts with the same matrices and mapping share their palette. False when no
        // part has bones.
        private static bool AddSkin(int meshIndex, object[] proxies)
        {
            int firstBone = s_boneCount;
            int firstPalette = s_paletteCount;
            int known = 0;
            var proxy = new ProxyState();
            foreach (object part in proxies)
            {
                if (part == null)
                    continue;
                s_readProxy(part, ref proxy);
                if (!RegisteredPart(in proxy, meshIndex))
                    continue;
                Matrix[] matrices = s_proxySkinning(part);
                int[] mapping = s_proxyBonesMapping(part);
                int start = -1;
                for (int i = 0; i < known && start < 0; i++)
                {
                    if (ReferenceEquals(s_paletteMatrices[i], matrices) && ReferenceEquals(s_paletteMappings[i], mapping))
                        start = s_paletteStarts[i];
                }
                if (start < 0)
                {
                    start = s_boneCount - firstBone;
                    AppendBones(matrices, mapping);
                    if (known == s_paletteStarts.Length)
                    {
                        Array.Resize(ref s_paletteMatrices, known * 2);
                        Array.Resize(ref s_paletteMappings, known * 2);
                        Array.Resize(ref s_paletteStarts, known * 2);
                    }
                    s_paletteMatrices[known] = matrices;
                    s_paletteMappings[known] = mapping;
                    s_paletteStarts[known++] = start;
                }
                if (s_paletteCount == s_palettes.Length)
                    Array.Resize(ref s_palettes, s_palettes.Length * 2);
                s_palettes[s_paletteCount++] = (uint)start;
            }
            Array.Clear(s_paletteMatrices, 0, known);  // the game's arrays are not kept
            Array.Clear(s_paletteMappings, 0, known);
            int bones = s_boneCount - firstBone;
            if (bones == 0)
            {
                s_boneCount = firstBone;
                s_paletteCount = firstPalette;
                return false;
            }
            if (s_skinCount == s_frameSkins.Length)
                Array.Resize(ref s_frameSkins, s_frameSkins.Length * 2);
            s_frameSkins[s_skinCount++] = new RtSkin
            {
                Instance = (uint)(s_instanceCount - 1),
                FirstBone = (uint)firstBone,
                BoneCount = (uint)bones,
                FirstPalette = (uint)firstPalette,
                PaletteCount = (uint)(s_paletteCount - firstPalette),
            };
            return true;
        }

        // A palette: matrices[mapping[i]] for every entry of the mapping, else the first 60 matrices (the game's
        // bone_matrix[60]). The game keeps them transposed for its column-major constants
        // (MySkinningComponent: Matrix.Transpose(SkinTransform * absolute)): the first three rows of one are the
        // first three columns of the bone's own matrix, which RtSkin wants.
        private static void AppendBones(Matrix[] matrices, int[] mapping)
        {
            if (matrices == null)
                return;
            int count = mapping != null ? mapping.Length : Math.Min(60, matrices.Length);
            if ((s_boneCount + count) * 12 > s_bones.Length)
                Array.Resize(ref s_bones, Math.Max(s_bones.Length * 2, (s_boneCount + count) * 12));
            for (int i = 0; i < count; i++)
            {
                int source = mapping != null ? mapping[i] : i;
                Matrix m = source >= 0 && source < matrices.Length ? matrices[source] : Matrix.Identity;
                int at = s_boneCount++ * 12;
                s_bones[at] = m.M11;
                s_bones[at + 1] = m.M12;
                s_bones[at + 2] = m.M13;
                s_bones[at + 3] = m.M14;
                s_bones[at + 4] = m.M21;
                s_bones[at + 5] = m.M22;
                s_bones[at + 6] = m.M23;
                s_bones[at + 7] = m.M24;
                s_bones[at + 8] = m.M31;
                s_bones[at + 9] = m.M32;
                s_bones[at + 10] = m.M33;
                s_bones[at + 11] = m.M34;
            }
        }

        // Returns whether the cell was added.
        private static bool VoxelCell(MyActorComponent component)
        {
            IMyActor actor = component.Owner;
            if (actor == null || actor.IsDestroyed || !actor.IsVisible)
                return false;
            var state = new VoxelState();
            s_readVoxel(component, ref state);
            if (!state.Ready || state.Mesh < 0)
                return false;
            int lodMesh = s_lodMeshOf(state.Mesh);
            if (lodMesh < 0)
                return false;
            uint mesh = LodMesh(lodMesh, null, voxel: true);
            if (mesh == 0)
                return false;
            // The triplanar coordinates: the cell's corner in the voxel map and its LOD scale
            // (MyObjectDataVoxelCommon of its proxies), carried in the paint slots.
            Vector3 offset = Vector3.Zero;
            float scale = 1;
            var renderable = new RenderableState();
            s_readRenderable(component, ref renderable);
            if (renderable.Lods != null && renderable.Lods.Length > 0 && renderable.Lods[0] != null)
            {
                object[] proxies = s_lodProxies(renderable.Lods[0]);
                if (proxies != null && proxies.Length > 0 && proxies[0] != null)
                {
                    offset = s_proxyVoxelOffset(proxies[0]);
                    scale = s_proxyVoxelScale(proxies[0]).X;
                }
            }
            AddInstance(mesh, MaskPrimary | MaskShadow, actor.ID, in actor.WorldMatrix, offset, 0, scale);
            return true;
        }

        // The game's environment probe cubemaps to the native core when they change.
        private static void SendEnvironment()
        {
            object probe = s_environmentProbe();
            IntPtr far = IntPtr.Zero, close = IntPtr.Zero;
            if (probe != null)
            {
                object farTexture = s_probeFar(probe);
                object closeTexture = s_probeClose(probe);
                far = farTexture != null ? s_resourceOf(farTexture)?.NativePointer ?? IntPtr.Zero : IntPtr.Zero;
                close = closeTexture != null ? s_resourceOf(closeTexture)?.NativePointer ?? IntPtr.Zero : IntPtr.Zero;
            }
            if (far == s_environmentFar && close == s_environmentClose)
                return;
            s_environmentFar = far;
            s_environmentClose = close;
            NativeApi.RtSetEnvironment(far, close);
        }

        private static RtVoxelMaterial[] s_voxelTable = new RtVoxelMaterial[0];
        private static string[] s_voxelTablePaths = new string[0];
        private static long s_voxelSignature;
        private static IntPtr s_voxelRandom;

        // Sends the voxel material table when it changed (checked when voxels are first seen, then every
        // ReportFrames frames: the game fills the constants as its material tiles arrive).
        private static unsafe void VoxelMaterials()
        {
            if (s_voxelSignature != 0 && s_frame % ReportFrames != 0)
                return;
            int count = Math.Min(s_voxelTableLength(), 128);
            if (s_voxelTable.Length != count)
            {
                s_voxelTable = new RtVoxelMaterial[count];
                s_voxelTablePaths = new string[count * 18];
            }
            long signature = count + 1;
            for (int i = 0; i < count; i++)
            {
                ref RtVoxelMaterial m = ref s_voxelTable[i];
                Vector4 das = s_voxelDistanceAndScale(i);
                Vector3 far1 = s_voxelFar1(i), far2 = s_voxelFar2(i), far3 = s_voxelFar3(i), color = s_voxelFar3Color(i);
                float tiling = s_voxelTilingScale(i);
                m.DistanceAndScale[0] = das.X; m.DistanceAndScale[1] = das.Y; m.DistanceAndScale[2] = das.Z; m.DistanceAndScale[3] = das.W;
                m.Far1[0] = far1.X; m.Far1[1] = far1.Y; m.Far1[2] = far1.Z; m.Far1[3] = tiling;
                m.Far2[0] = far2.X; m.Far2[1] = far2.Y; m.Far2[2] = far2.Z; m.Far2[3] = 0;
                m.Far3[0] = far3.X; m.Far3[1] = far3.Y; m.Far3[2] = far3.Z; m.Far3[3] = 0;
                m.ColorFar3[0] = color.X; m.ColorFar3[1] = color.Y; m.ColorFar3[2] = color.Z;
                bool used = false;
                // RtVoxelMaterial order: component (ColorMetal, NormalGloss, Ext) * 6 + distance * 2 + axis (XZnY, Y).
                for (int set = 0; set < 6; set++)
                {
                    string[] files = s_voxelPaths[set](i);
                    for (int distance = 0; distance < 3; distance++)
                    {
                        string file = files != null && distance < files.Length ? files[distance] : null;
                        string path = string.IsNullOrEmpty(file) ? null : s_textureFullPath(file);
                        s_voxelTablePaths[i * 18 + (set / 2) * 6 + distance * 2 + set % 2] = path;
                        used |= !string.IsNullOrEmpty(path);
                        signature = signature * 31 + (path?.GetHashCode() ?? 0);
                    }
                }
                m.ColorFar3[3] = used ? 1 : 0;
                signature = signature * 31 + das.GetHashCode();
                signature = signature * 31 + far1.GetHashCode() + far2.GetHashCode() * 7 + far3.GetHashCode() * 13;
                signature = signature * 31 + tiling.GetHashCode() + color.GetHashCode() * 3;
            }
            object random = s_randomTexture();
            IntPtr randomResource = random != null && s_generatedTextureType.IsInstanceOfType(random)
                ? s_textureResource(random)?.NativePointer ?? IntPtr.Zero : IntPtr.Zero;
            signature = signature * 31 + randomResource.ToInt64();
            if (signature == s_voxelSignature)
                return;
            s_voxelSignature = signature;
            s_voxelRandom = randomResource;
            NativeApi.RtVoxelMaterials(s_voxelTable, s_voxelTablePaths, count, randomResource);
            Log.Info($"Scene mirror: voxel material table sent ({count} entries).");
        }

        // A GeometryStage2 LOD's mesh handle, registered on first use.
        private static uint ModelMesh(object lod)
        {
            object vb0 = null;
            if (s_modelMeshes.TryGetValue(lod, out MeshEntry entry))
            {
                entry.LastSeen = s_frame;
                if (entry.Validated == s_frame)
                    return entry.Handle;
                entry.Validated = s_frame;
                vb0 = s_lodVb0(lod);
                if (ReferenceEquals(vb0, entry.Vb0))
                    return entry.Handle;
                Release(entry);
                s_modelMeshes.Remove(lod);
            }
            vb0 = vb0 ?? s_lodVb0(lod);
            object vb1 = s_lodVb1(lod);
            object ib = s_lodIb(lod);
            SharpDX.Direct3D11.Buffer vertices = vb0 != null ? s_bufferNative(vb0) : null;
            SharpDX.Direct3D11.Buffer indices = ib != null ? s_bufferNative(ib) : null;
            if (vertices == null || indices == null)
                return 0;  // not loaded yet; asked again next frame
            SharpDX.Direct3D11.Buffer attributes = vb1 != null ? s_bufferNative(vb1) : null;
            int partCount = 0;
            var materials = new List<uint>();
            IList parts = s_lodGbufferParts(lod);
            if (parts != null)
            {
                foreach (object part in parts)
                {
                    int technique = s_partTechnique(part);
                    if (!ModelPart(part, technique))
                        continue;
                    uint material = Material(s_partTextures(part), Masked(technique) ? 1u : 0u);
                    AddPart(ref partCount, s_partIndexStart(part), s_partIndexCount(part), 0, technique,
                        s_partDoubleSided(part), material);
                    materials.Add(material);
                }
            }
            var desc = new RtMeshDesc
            {
                Size = (uint)sizeof(RtMeshDesc),
                PositionFormat = 0,
                VertexStride = (uint)s_bufferStride(vb0),
                VertexCount = (uint)s_bufferElements(vb0),
                VertexBuffer = vertices.NativePointer,
                AttributeBuffer = attributes?.NativePointer ?? IntPtr.Zero,
                AttributeStride = attributes != null ? (uint)s_bufferStride(vb1) : 0,
                IndexFormat = (uint)s_indexFormat(ib),
                IndexBuffer = indices.NativePointer,
                IndexCount = (uint)s_bufferElements(ib),
                Kind = 0,
            };
            entry = Register(ref desc, partCount, vb0);
            entry.Materials = materials.ToArray();
            s_modelMeshes[lod] = entry;
            return entry.Handle;
        }

        // A GeometryStage2 LOD part the mesh is registered with (ModelMesh, AddPart).
        private static bool ModelPart(object part, int technique) =>
            !Unmirrored(technique) && s_partIndexCount(part) > 0 && s_partIndexStart(part) >= 0;

        // The mesh a skinned GeometryStage2 instance draws: its LOD's mesh with the model instance's bindings
        // (MyLodInstance.GBufferParts, by the LOD's part index) as the parts' materials, a variant where they differ
        // from the model's own; resolved once per model instance, LOD and mesh.
        private static uint SkinnedModelMesh(object modelInstance, int lodIndex, object lod, uint mesh)
        {
            if (ReferenceEquals(modelInstance, s_lastSkinned) && lodIndex == s_lastSkinnedLod && mesh == s_lastSkinnedMesh)
                return s_lastSkinnedHandle;
            if (!s_skinnedModels.TryGetValue(modelInstance, out SkinnedLodEntry[] lods) || lodIndex >= lods.Length)
            {
                Array.Resize(ref lods, Math.Max(lodIndex + 1, lods?.Length ?? 0));
                s_skinnedModels[modelInstance] = lods;
            }
            SkinnedLodEntry cached = lods[lodIndex];
            if (cached == null || cached.Mesh != mesh || (cached.LastSeen != s_frame && !StillBound(modelInstance, lodIndex, cached)))
            {
                if (cached != null && cached.Mesh == mesh)
                    ++s_skinnedRebinds;
                cached = ResolveSkinnedMesh(modelInstance, lodIndex, lod, mesh);
                lods[lodIndex] = cached;
            }
            cached.LastSeen = s_frame;
            s_lastSkinned = modelInstance;
            s_lastSkinnedLod = lodIndex;
            s_lastSkinnedMesh = mesh;
            s_lastSkinnedHandle = cached.Handle;
            return cached.Handle;
        }

        private static SkinnedLodEntry ResolveSkinnedMesh(object modelInstance, int lodIndex, object lod, uint mesh)
        {
            var resolved = new SkinnedLodEntry { Mesh = mesh, Handle = mesh };
            object own = LodInstance(modelInstance, lodIndex);
            IList parts = s_lodGbufferParts(lod);
            if (own == null || parts == null || !s_modelMeshes.TryGetValue(lod, out MeshEntry entry) || entry.Handle != mesh ||
                entry.Materials == null)
                return resolved;
            var indices = new List<int>();
            var bound = new List<object>();
            int count = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                object part = parts[i];
                int technique = s_partTechnique(part);
                if (!ModelPart(part, technique))
                    continue;
                object[] textures = BoundTextures(own, i);
                indices.Add(i);
                bound.AddRange(textures);
                if (count == s_variantMaterials.Length)
                    Array.Resize(ref s_variantMaterials, count * 2);
                s_variantMaterials[count++] = Material(textures, Masked(technique) ? 1u : 0u);
            }
            resolved.Handle = VariantOf(entry, mesh, count);
            resolved.Parts = indices.ToArray();
            resolved.Bound = bound.ToArray();
            return resolved;
        }

        private static object LodInstance(object modelInstance, int lodIndex)
        {
            object[] lodInstances = s_modelInstanceLods(modelInstance);
            return lodInstances != null && lodIndex < lodInstances.Length ? lodInstances[lodIndex] : null;
        }

        // The four textures a model instance's LOD part draws with now (in s_boundTextures): ColorMetal, NormalGloss,
        // Extensions and the alpha mask (SrvAlphamask, which the game keeps current as textures stream; else Srvs[3]).
        private static object[] BoundTextures(object lodInstance, int part)
        {
            object[] srvs = s_lodInstanceSrvs(lodInstance, part);
            for (int slot = 0; slot < 3; slot++)
                s_boundTextures[slot] = srvs != null && slot < srvs.Length ? srvs[slot] : null;
            s_boundTextures[3] = s_lodInstanceAlphamask(lodInstance, part) ?? (srvs != null && srvs.Length > 3 ? srvs[3] : null);
            return s_boundTextures;
        }

        // Whether a skinned LOD still draws the textures it was resolved with.
        private static bool StillBound(object modelInstance, int lodIndex, SkinnedLodEntry cached)
        {
            if (cached.Parts == null)
                return true;
            object own = LodInstance(modelInstance, lodIndex);
            if (own == null)
                return true;
            for (int k = 0; k < cached.Parts.Length; k++)
            {
                object[] textures = BoundTextures(own, cached.Parts[k]);
                for (int slot = 0; slot < 4; slot++)
                {
                    if (!ReferenceEquals(textures[slot], cached.Bound[k * 4 + slot]))
                        return false;
                }
            }
            return true;
        }

        // An old-pipeline or voxel LOD mesh's handle, registered on first use.
        private static uint LodMesh(int index, object[] proxies, bool voxel)
        {
            object vb0 = null;
            if (s_lodMeshes.TryGetValue(index, out MeshEntry entry))
            {
                entry.LastSeen = s_frame;
                if (!entry.Stale)
                {
                    if (entry.Validated == s_frame)
                        return entry.Handle;
                    entry.Validated = s_frame;
                    vb0 = s_meshVb0(index);
                    if (ReferenceEquals(vb0, entry.Vb0))
                        return entry.Handle;
                }
                else
                {
                    ++s_reregistered;
                }
                Release(entry);
                s_lodMeshes.Remove(index);
            }
            vb0 = vb0 ?? s_meshVb0(index);
            object vb1 = s_meshVb1(index);
            object ib = s_meshIb(index);
            SharpDX.Direct3D11.Buffer vertices = vb0 != null ? s_bufferNative(vb0) : null;
            SharpDX.Direct3D11.Buffer indices = ib != null ? s_bufferNative(ib) : null;
            int vertexCount = s_meshVertices(index);
            int indexCount = s_meshIndices(index);
            if (vertices == null || indices == null || vertexCount <= 0 || indexCount <= 0)
                return 0;  // not uploaded yet (the update batch commits later)
            SharpDX.Direct3D11.Buffer attributes = vb1 != null ? s_bufferNative(vb1) : null;
            int partCount = 0;
            uint[] materials = null;
            bool[] hideable = null;
            if (voxel)
            {
                AddPart(ref partCount, 0, indexCount, 0, 0, false, 0);
            }
            else
            {
                var proxy = new ProxyState();
                var registered = new List<uint>();
                var hideableParts = new List<bool>();
                s_hideableParts.TryGetValue(index, out HashSet<int> known);
                foreach (object part in proxies)
                {
                    if (part == null)
                        continue;
                    s_readProxy(part, ref proxy);
                    if (!RegisteredPart(in proxy, index))
                        continue;
                    uint material = Material(s_proxyTextures(proxy.MaterialProxy), PartMaterialFlags(in proxy));
                    bool canHide = (proxy.Flags & SkipInMainView) != 0 || (known != null && known.Contains(registered.Count));
                    AddPart(ref partCount, proxy.StartIndex, proxy.IndexCount, proxy.BaseVertex, proxy.Technique,
                        proxy.DoubleSided, material, canHide);
                    registered.Add(material);
                    hideableParts.Add(canHide);
                }
                materials = registered.ToArray();
                hideable = hideableParts.ToArray();
            }
            bool bones = !voxel && s_layoutBones(s_meshLayout(index));
            var desc = new RtMeshDesc
            {
                Size = (uint)sizeof(RtMeshDesc),
                PositionFormat = voxel ? 1u : 0u,
                VertexStride = (uint)s_bufferStride(vb0),
                VertexCount = (uint)vertexCount,
                VertexBuffer = vertices.NativePointer,
                AttributeBuffer = attributes?.NativePointer ?? IntPtr.Zero,
                AttributeStride = attributes != null ? (uint)s_bufferStride(vb1) : 0,
                IndexFormat = (uint)s_indexFormat(ib),
                IndexBuffer = indices.NativePointer,
                IndexCount = (uint)indexCount,
                Kind = voxel ? 2u : 1u,
                Flags = bones ? 1u : 0u,
            };
            entry = Register(ref desc, partCount, vb0);
            entry.Materials = materials;
            entry.Hideable = hideable;
            s_lodMeshes[index] = entry;
            return entry.Handle;
        }

        // Parts drawn with these techniques stay out of the mirror: the transparent pass's parts (holograms, glass,
        // atmospheres, cloud layers, shields) are not in the game's G-buffer, so the rays pass them as the game's
        // own lighting does. (An old-pipeline cockpit's canopy is a GLASS part of its block's model: mirrored, it
        // stopped the camera's rays and the bounces on everything seen through it, which then stayed dark.) Decals
        // are mirrored: cutout ones as alpha-tested parts (Masked), blended ones as layers the rays look through
        // and blend into the surface beneath (DecalFlags; scene_common.hlsli TraceClosestDecals).
        private static bool Unmirrored(int technique) =>
            !s_mirrorTransparent && (technique == TechniqueHolo || technique == TechniqueGlass ||
                                     (technique >= TechniqueAtmosphere && technique <= TechniqueShieldLit));

        // Alpha-tested parts (non-opaque, solid where their alpha mask reaches 0.5): the game's alpha-masked
        // techniques and its cutout decals (STATIC_DECAL_CUTOUT: clipped at 0.5, then written over the surface).
        private static bool Masked(int technique) =>
            technique == TechniqueAlphaMasked || technique == TechniqueAlphaMaskedSingleSided ||
            technique == TechniqueDecalCutout;

        // Decal flags (scene.h): the blended decals (STATIC_DECAL: blended into the surface's G-buffer by their alpha
        // mask, premultiplied or not) kPartDecal (8), with kPartDecalPremultiplied (16) for the premultiplied ones;
        // the cutout ones (alpha-tested, Masked) kPartDecalCutout (32), which only the camera's rays see.
        private static uint DecalFlags(int technique) =>
            technique == TechniqueDecal ? 8u | 16u : technique == TechniqueDecalNoPremult ? 8u :
            technique == TechniqueDecalCutout ? 32u : 0u;
        private static bool s_mirrorTransparent;

        // A proxy part an old-pipeline mesh is registered with.
        private static bool RegisteredPart(in ProxyState proxy, int meshIndex) =>
            proxy.Mesh == meshIndex && !Unmirrored(proxy.Technique) && proxy.IndexCount > 0 && proxy.StartIndex >= 0;

        private static uint PartMaterialFlags(in ProxyState proxy)
        {
            return (Masked(proxy.Technique) ? 1u : 0u) | ((proxy.Flags & MetalnessColorableFlag) != 0 ? 2u : 0u);
        }

        // The handle for a renderable's own materials: its LOD mesh is shared by
        // every renderable of the model, the materials (skins) are not.
        private static uint MaterialVariant(object component, uint mesh, int meshIndex, object[] proxies)
        {
            ulong key = MaterialKey(meshIndex, proxies);
            if (!s_renderables.TryGetValue(component, out RenderableEntry cached))
            {
                cached = new RenderableEntry();
                s_renderables[component] = cached;
            }
            cached.LastSeen = s_frame;
            if (cached.Handle != 0 && cached.Mesh == mesh && cached.Key == key)
                return cached.Handle;
            cached.Mesh = mesh;
            cached.Key = key;
            cached.Handle = ResolveVariant(mesh, meshIndex, proxies);
            return cached.Handle;
        }

        // FNV-1a over the material ids the mesh's parts bind (MyMaterials1 proxies: textures, skins included).
        private static ulong MaterialKey(int meshIndex, object[] proxies)
        {
            ulong hash = 14695981039346656037UL;
            var state = new PartKeyState();
            foreach (object part in proxies)
            {
                if (part == null)
                    continue;
                s_readPartKey(part, ref state);
                if (state.Mesh != meshIndex || Unmirrored(state.Technique))
                    continue;
                hash = (hash ^ (uint)state.MaterialProxy) * 1099511628211UL;
            }
            return hash;
        }

        // The mesh when the proxies bind the materials it was registered with,
        // else the variant for theirs (created on first use).
        private static uint ResolveVariant(uint mesh, int meshIndex, object[] proxies)
        {
            if (!s_lodMeshes.TryGetValue(meshIndex, out MeshEntry entry) || entry.Handle != mesh || entry.Materials == null)
                return mesh;
            int count = 0;
            var proxy = new ProxyState();
            foreach (object part in proxies)
            {
                if (part == null)
                    continue;
                s_readProxy(part, ref proxy);
                if (!RegisteredPart(in proxy, meshIndex))
                    continue;
                if (count == s_variantMaterials.Length)
                    Array.Resize(ref s_variantMaterials, count * 2);
                s_variantMaterials[count++] = Material(s_proxyTextures(proxy.MaterialProxy), PartMaterialFlags(in proxy));
            }
            return VariantOf(entry, mesh, count);
        }

        // The mesh itself when the first `count` materials of s_variantMaterials are those it was registered with,
        // else its variant with them (created on first use).
        private static uint VariantOf(MeshEntry entry, uint mesh, int count)
        {
            if (count != entry.Materials.Length)
                return mesh;  // other parts than registered: the mesh as it is
            bool same = true;
            for (int i = 0; i < count && same; i++)
                same = s_variantMaterials[i] == entry.Materials[i];
            if (same)
                return mesh;
            string key = string.Join(",", s_variantMaterials.Take(count));
            entry.Variants = entry.Variants ?? new Dictionary<string, uint>(StringComparer.Ordinal);
            if (!entry.Variants.TryGetValue(key, out uint variant))
            {
                variant = NativeApi.RtMeshVariant(mesh, s_variantMaterials, count);
                entry.Variants[key] = variant;
                ++s_variantsCreated;
            }
            return variant != 0 ? variant : mesh;
        }

        // The native material of a part's bound textures (slots ColorMetal, NormalGloss, Extensions,
        // Alphamask): files are read by the native core, textures the game generates (screens' render
        // targets, its default textures) are mirrored from the game's device; anything else keeps the
        // native default for the slot.
        internal static uint Material(object[] textures, uint flags)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                object texture = textures != null && slot < textures.Length ? textures[slot] : null;
                s_paths[slot] = null;
                s_rendered[slot] = IntPtr.Zero;
                if (texture == null)
                    continue;
                if (s_fileTextureType.IsInstanceOfType(texture))
                    s_paths[slot] = s_texturePath(texture);
                else if (s_generatedTextureType.IsInstanceOfType(texture))
                    s_rendered[slot] = s_textureResource(texture)?.NativePointer ?? IntPtr.Zero;
            }
            string key = flags + "|" + SlotKey(0) + "|" + SlotKey(1) + "|" + SlotKey(2) + "|" + SlotKey(3);
            if (!s_materials.TryGetValue(key, out uint handle))
            {
                handle = NativeApi.RtMaterialCreate(flags, s_paths[0], s_paths[1], s_paths[2], s_paths[3], s_rendered);
                s_materials[key] = handle;
                ++s_materialsCreated;
            }
            return handle;
        }

        private static string SlotKey(int slot) =>
            s_rendered[slot] != IntPtr.Zero ? "#" + s_rendered[slot].ToInt64().ToString("X") : s_paths[slot];

        private static void AddPart(ref int count, int indexStart, int indexCount, int baseVertex, int technique,
            bool doubleSided, uint material, bool hideable = false)
        {
            if (indexCount <= 0 || indexStart < 0)
                return;
            if (count == s_parts.Length)
                Array.Resize(ref s_parts, s_parts.Length * 2);
            s_parts[count++] = new RtMeshPart
            {
                IndexStart = (uint)indexStart,
                IndexCount = (uint)indexCount,
                BaseVertex = baseVertex,
                Flags = (Masked(technique) ? 1u : 0u) | (doubleSided ? 2u : 0u) | (hideable ? 4u : 0u) |
                        DecalFlags(technique),
                Material = material,
            };
        }

        private static MeshEntry Register(ref RtMeshDesc desc, int partCount, object vb0)
        {
            uint handle = partCount > 0 ? NativeApi.RtMeshCreate(ref desc, s_parts, partCount) : 0;
            if (handle != 0)
                ++s_registered;
            else
                ++s_refused;
            return new MeshEntry { Handle = handle, Vb0 = vb0, LastSeen = s_frame, Validated = s_frame };
        }

        private static void Release(MeshEntry entry)
        {
            if (entry.Variants != null)
            {
                foreach (uint variant in entry.Variants.Values)
                {
                    if (variant != 0)
                        NativeApi.RtMeshRelease(variant);
                }
                entry.Variants = null;
            }
            if (entry.Handle != 0)
            {
                NativeApi.RtMeshRelease(entry.Handle);
                ++s_released;
            }
        }

        // The native handle of an instancing buffer's records (cube blocks: 64 bytes, generic instances: 32),
        // uploaded again when the game changed them.
        private static uint Instancing(int index)
        {
            byte[] data = s_instancingData(index);
            int count = s_instancingCount(index);
            int stride = s_instancingStride(index);
            if (data == null || (stride != 64 && stride != 32))
                return 0;
            count = Math.Min(count, data.Length / stride);
            bool edited = s_dirtyInstancings.Remove(index);
            if (!s_instancings.TryGetValue(index, out InstancingEntry entry))
            {
                entry = new InstancingEntry();
                s_instancings[index] = entry;
                edited = true;
            }
            entry.LastSeen = s_frame;
            if (edited || entry.Handle == 0 || !ReferenceEquals(entry.Data, data) || entry.Count != count)
            {
                entry.Handle = NativeApi.RtInstancingUpdate(entry.Handle, data, count, stride);
                entry.Data = data;
                entry.Count = count;
                ++s_recordUploads;
            }
            return entry.Handle;
        }

        private static void AddInstance(uint mesh, uint mask, uint id, in MatrixD world, Vector3 keyColor, uint flags,
            float keyW = 0, uint overrides = 0)
        {
            if (s_instanceCount == s_frameInstances.Length)
                Array.Resize(ref s_frameInstances, s_frameInstances.Length * 2);
            ref RtInstance instance = ref s_frameInstances[s_instanceCount++];
            instance.Mesh = mesh;
            instance.Mask = mask;
            instance.Flags = flags;
            instance.Id = id;
            instance.Overrides = overrides;
            instance.KeyColor[0] = keyColor.X;
            instance.KeyColor[1] = keyColor.Y;
            instance.KeyColor[2] = keyColor.Z;
            instance.KeyColor[3] = keyW;
            instance.Translation[0] = world.M41;
            instance.Translation[1] = world.M42;
            instance.Translation[2] = world.M43;
            instance.Rotation[0] = (float)world.M11;
            instance.Rotation[1] = (float)world.M12;
            instance.Rotation[2] = (float)world.M13;
            instance.Rotation[3] = (float)world.M21;
            instance.Rotation[4] = (float)world.M22;
            instance.Rotation[5] = (float)world.M23;
            instance.Rotation[6] = (float)world.M31;
            instance.Rotation[7] = (float)world.M32;
            instance.Rotation[8] = (float)world.M33;
        }

        private static void AddBucket(uint mesh, uint instancing, int start, int count, uint mask, in MatrixD world)
        {
            if (s_bucketCount == s_frameBuckets.Length)
                Array.Resize(ref s_frameBuckets, s_frameBuckets.Length * 2);
            ref RtBucket bucket = ref s_frameBuckets[s_bucketCount++];
            bucket.Mesh = mesh;
            bucket.Instancing = instancing;
            bucket.Start = (uint)Math.Max(0, start);
            bucket.Count = (uint)count;
            bucket.Mask = mask;
            bucket.Translation[0] = world.M41;
            bucket.Translation[1] = world.M42;
            bucket.Translation[2] = world.M43;
            bucket.Rotation[0] = (float)world.M11;
            bucket.Rotation[1] = (float)world.M12;
            bucket.Rotation[2] = (float)world.M13;
            bucket.Rotation[3] = (float)world.M21;
            bucket.Rotation[4] = (float)world.M22;
            bucket.Rotation[5] = (float)world.M23;
            bucket.Rotation[6] = (float)world.M31;
            bucket.Rotation[7] = (float)world.M32;
            bucket.Rotation[8] = (float)world.M33;
        }

        // Meshes and block records no object used for EvictAfterFrames frames.
        private static void Evict()
        {
            int threshold = s_frame - EvictAfterFrames;
            foreach (object key in s_modelMeshes.Where(pair => pair.Value.LastSeen < threshold).Select(pair => pair.Key).ToList())
            {
                Release(s_modelMeshes[key]);
                s_modelMeshes.Remove(key);
            }
            foreach (int key in s_lodMeshes.Where(pair => pair.Value.LastSeen < threshold).Select(pair => pair.Key).ToList())
            {
                Release(s_lodMeshes[key]);
                s_lodMeshes.Remove(key);
            }
            foreach (object key in s_renderables.Where(pair => pair.Value.LastSeen < threshold).Select(pair => pair.Key).ToList())
                s_renderables.Remove(key);
            foreach (int key in s_instancings.Where(pair => pair.Value.LastSeen < threshold).Select(pair => pair.Key).ToList())
            {
                if (s_instancings[key].Handle != 0)
                    NativeApi.RtInstancingRelease(s_instancings[key].Handle);
                s_instancings.Remove(key);
            }
            // Skinned model instances no instance drew for a while (their variants belong to the meshes' entries).
            foreach (object key in s_skinnedModels.Keys.ToList())
            {
                SkinnedLodEntry[] lods = s_skinnedModels[key];
                bool any = false;
                for (int i = 0; i < lods.Length; i++)
                {
                    if (lods[i] != null && lods[i].LastSeen < threshold)
                        lods[i] = null;
                    any |= lods[i] != null;
                }
                if (!any)
                    s_skinnedModels.Remove(key);
            }
        }

        private static void Report()
        {
            double frames = ReportFrames;
            double toMs = 1000.0 / Stopwatch.Frequency;
            Log.Info($"Scene mirror: walk {s_ticks * toMs / frames:F3} ms avg / {s_maxTicks * toMs:F3} max; per frame " +
                     $"{s_instanceSum / frames:F0} instances, {s_bucketSum / frames:F0} block buckets with {s_blockSum / frames:F0} blocks; " +
                     $"meshes known: {s_modelMeshes.Count} models, {s_lodMeshes.Count} LOD meshes; this window {s_registered} registered ({s_materials.Count} materials, {s_materialsCreated} new; " +
                     $"{s_variantsCreated} new material variants), " +
                     $"{s_refused} refused, {s_released} released, {s_recordUploads} block-record uploads, " +
                     $"{s_skinSum / frames:F1} skinned renderables posed per frame ({s_skinBoneSum / frames:F0} bones; " +
                     $"{s_skippedSkinned} left out this window), " +
                     $"{s_overriddenInstances / frames:F0} instances with their own part colors or emissivity per frame " +
                     $"({s_hiddenPartInstances / frames:F0} with parts hidden in the main view; {s_reregistered} meshes " +
                     $"registered anew for them), {s_skinnedInstances / frames:F0} model instances with a skin's textures " +
                     $"per frame ({s_skinnedModels.Count} skinned model instances known, {s_skinnedRebinds} LODs bound anew as textures " +
                     "streamed).");
            s_ticks = s_maxTicks = s_instanceSum = s_bucketSum = s_blockSum = s_overriddenInstances = 0;
            s_hiddenPartInstances = s_reregistered = s_skinSum = s_skinBoneSum = s_skinnedInstances = 0;
            s_skinnedRebinds = 0;
            s_registered = s_refused = s_released = s_recordUploads = s_skippedSkinned = s_materialsCreated = 0;
            s_variantsCreated = 0;
            LampMirror.Report(frames);
            DecalMirror.Report(frames);
        }
    }
}
