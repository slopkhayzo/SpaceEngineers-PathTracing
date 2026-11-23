using System;
using System.Runtime.InteropServices;

namespace SpaceEngineers.RTHWAccel
{
    // Reads sert::NativeApi (src/native/native_api.h) by explicit offsets:
    //
    //   0  uint32 size        8  createHook     24 removeHook   40 diagnosticsEnabled
    //   4  uint32 version     16 applyQueued    32 log
    //   48 rtInitialize       56 rtInstallGate  64 rtSetLightingList  72 rtBindLighting
    //   80 rtSunPassRecorded  88 rtSetGateMode  96 rtShutdown        104 rtSetFrame
    //   112 rtSetDebugView    120 rtMeshCreate  128 rtMeshRelease    136 rtInstancingUpdate
    //   144 rtInstancingRelease                 152 rtSceneSubmit 160 rtMaterialCreate
    //   168 rtSetProbes       176 rtMeshVariant 184 rtVoxelMaterials 192 rtSetEnvironment
    //   200 rtLightsSubmit
    //
    // A C# struct with function-pointer fields would not do: .NET Framework
    // does not count those fields as blittable and may reorder them.
    internal static unsafe class NativeApi
    {
        public const uint ExpectedVersion = 16;
        public const uint ExpectedSize = 240;

        // sert::RtOptions.
        [StructLayout(LayoutKind.Sequential)]
        private struct RtOptions
        {
            public uint Size;
            public int GateMode;
            public float PatternStrength;
            public int DebugView;
            public IntPtr TracePath;
            public int Denoiser;
            public int CharacterLights;  // 1: characters' glowing suit parts are lights
            public float LampIntensity;      // the lamps' light (traced, and the game's beyond the traced area)
            public float EmissiveIntensity;  // the light emissive surfaces cast (not their own glow)
            public int DecalLights;          // 1: glowing decals are lights
            public int LightTree;            // 1: lamps and emitters traced at any distance (the light tree)
        }

        private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, int> s_createHook;
        private static delegate* unmanaged[Cdecl]<int> s_applyQueued;
        private static delegate* unmanaged[Cdecl]<IntPtr, int> s_removeHook;
        private static delegate* unmanaged[Cdecl]<int, char*, void> s_log;
        private static delegate* unmanaged[Cdecl]<int> s_diagnosticsEnabled;
        private static delegate* unmanaged[Cdecl]<IntPtr, RtOptions*, int> s_rtInitialize;
        private static delegate* unmanaged[Cdecl]<int> s_rtInstallGate;
        private static delegate* unmanaged[Cdecl]<IntPtr, void> s_rtSetLightingList;
        private static delegate* unmanaged[Cdecl]<IntPtr, void> s_rtBindLighting;
        private static delegate* unmanaged[Cdecl]<IntPtr, void> s_rtSunPassRecorded;
        private static delegate* unmanaged[Cdecl]<int, void> s_rtSetGateMode;
        private static delegate* unmanaged[Cdecl]<void> s_rtShutdown;
        private static delegate* unmanaged[Cdecl]<RtFrame*, void> s_rtSetFrame;
        private static delegate* unmanaged[Cdecl]<int, void> s_rtSetDebugView;
        private static delegate* unmanaged[Cdecl]<RtMeshDesc*, uint> s_rtMeshCreate;
        private static delegate* unmanaged[Cdecl]<uint, void> s_rtMeshRelease;
        private static delegate* unmanaged[Cdecl]<uint, byte*, uint, uint, uint> s_rtInstancingUpdate;
        private static delegate* unmanaged[Cdecl]<uint, void> s_rtInstancingRelease;
        private static delegate* unmanaged[Cdecl]<RtInstance*, uint, RtBucket*, uint, float*, uint, void> s_rtSceneSubmit;
        private static delegate* unmanaged[Cdecl]<RtMaterialDesc*, uint> s_rtMaterialCreate;
        private static delegate* unmanaged[Cdecl]<uint*, uint, void> s_rtSetProbes;
        private static delegate* unmanaged[Cdecl]<uint, uint*, uint, uint> s_rtMeshVariant;
        private static delegate* unmanaged[Cdecl]<RtVoxelMaterial*, uint, IntPtr, void> s_rtVoxelMaterials;
        private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_rtSetEnvironment;
        private static delegate* unmanaged[Cdecl]<RtLight*, uint, void> s_rtLightsSubmit;
        private static delegate* unmanaged[Cdecl]<RtSkin*, uint, float*, uint, uint*, uint, void> s_rtSkinsSubmit;
        private static delegate* unmanaged[Cdecl]<RtDecal*, uint, void> s_rtDecalsSubmit;
        private static delegate* unmanaged[Cdecl]<int, void> s_rtSetDecalLights;
        private static delegate* unmanaged[Cdecl]<int, void> s_rtSetLightTree;

        public static bool IsBound => s_log != null;

        public static string Bind(IntPtr table)
        {
            if (table == IntPtr.Zero)
                return "no native API table";
            byte* bytes = (byte*)table;
            uint size = *(uint*)bytes;
            uint version = *(uint*)(bytes + 4);
            if (size != ExpectedSize)
                return $"native API table size {size}, expected {ExpectedSize}";
            if (version != ExpectedVersion)
                return $"native API version {version}, expected {ExpectedVersion}";
            if (sizeof(RtVoxelMaterial) != 224)
                return $"RtVoxelMaterial is {sizeof(RtVoxelMaterial)} bytes, expected 224";
            if (sizeof(RtFrame) != 544)
                return $"RtFrame is {sizeof(RtFrame)} bytes, expected 544";
            if (sizeof(RtLight) != 104)
                return $"RtLight is {sizeof(RtLight)} bytes, expected 104";
            if (sizeof(RtSkin) != 24)
                return $"RtSkin is {sizeof(RtSkin)} bytes, expected 24";
            if (sizeof(RtDecal) != 72)
                return $"RtDecal is {sizeof(RtDecal)} bytes, expected 72";
            if (sizeof(RtMeshPart) != 20 || sizeof(RtMeshDesc) != 72 || sizeof(RtInstance) != 96 || sizeof(RtBucket) != 80 ||
                sizeof(RtMaterialDesc) != 72)
                return $"scene structs are {sizeof(RtMeshPart)}/{sizeof(RtMeshDesc)}/{sizeof(RtInstance)}/{sizeof(RtBucket)}/" +
                       $"{sizeof(RtMaterialDesc)} bytes, expected 20/72/96/80/72";
            s_createHook = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, int>)*(IntPtr*)(bytes + 8);
            s_applyQueued = (delegate* unmanaged[Cdecl]<int>)*(IntPtr*)(bytes + 16);
            s_removeHook = (delegate* unmanaged[Cdecl]<IntPtr, int>)*(IntPtr*)(bytes + 24);
            s_log = (delegate* unmanaged[Cdecl]<int, char*, void>)*(IntPtr*)(bytes + 32);
            s_diagnosticsEnabled = (delegate* unmanaged[Cdecl]<int>)*(IntPtr*)(bytes + 40);
            s_rtInitialize = (delegate* unmanaged[Cdecl]<IntPtr, RtOptions*, int>)*(IntPtr*)(bytes + 48);
            s_rtInstallGate = (delegate* unmanaged[Cdecl]<int>)*(IntPtr*)(bytes + 56);
            s_rtSetLightingList = (delegate* unmanaged[Cdecl]<IntPtr, void>)*(IntPtr*)(bytes + 64);
            s_rtBindLighting = (delegate* unmanaged[Cdecl]<IntPtr, void>)*(IntPtr*)(bytes + 72);
            s_rtSunPassRecorded = (delegate* unmanaged[Cdecl]<IntPtr, void>)*(IntPtr*)(bytes + 80);
            s_rtSetGateMode = (delegate* unmanaged[Cdecl]<int, void>)*(IntPtr*)(bytes + 88);
            s_rtShutdown = (delegate* unmanaged[Cdecl]<void>)*(IntPtr*)(bytes + 96);
            s_rtSetFrame = (delegate* unmanaged[Cdecl]<RtFrame*, void>)*(IntPtr*)(bytes + 104);
            s_rtSetDebugView = (delegate* unmanaged[Cdecl]<int, void>)*(IntPtr*)(bytes + 112);
            s_rtMeshCreate = (delegate* unmanaged[Cdecl]<RtMeshDesc*, uint>)*(IntPtr*)(bytes + 120);
            s_rtMeshRelease = (delegate* unmanaged[Cdecl]<uint, void>)*(IntPtr*)(bytes + 128);
            s_rtInstancingUpdate = (delegate* unmanaged[Cdecl]<uint, byte*, uint, uint, uint>)*(IntPtr*)(bytes + 136);
            s_rtInstancingRelease = (delegate* unmanaged[Cdecl]<uint, void>)*(IntPtr*)(bytes + 144);
            s_rtSceneSubmit = (delegate* unmanaged[Cdecl]<RtInstance*, uint, RtBucket*, uint, float*, uint, void>)*(IntPtr*)(bytes + 152);
            s_rtMaterialCreate = (delegate* unmanaged[Cdecl]<RtMaterialDesc*, uint>)*(IntPtr*)(bytes + 160);
            s_rtSetProbes = (delegate* unmanaged[Cdecl]<uint*, uint, void>)*(IntPtr*)(bytes + 168);
            s_rtMeshVariant = (delegate* unmanaged[Cdecl]<uint, uint*, uint, uint>)*(IntPtr*)(bytes + 176);
            s_rtVoxelMaterials = (delegate* unmanaged[Cdecl]<RtVoxelMaterial*, uint, IntPtr, void>)*(IntPtr*)(bytes + 184);
            s_rtSetEnvironment = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)*(IntPtr*)(bytes + 192);
            s_rtLightsSubmit = (delegate* unmanaged[Cdecl]<RtLight*, uint, void>)*(IntPtr*)(bytes + 200);
            s_rtSkinsSubmit = (delegate* unmanaged[Cdecl]<RtSkin*, uint, float*, uint, uint*, uint, void>)*(IntPtr*)(bytes + 208);
            s_rtDecalsSubmit = (delegate* unmanaged[Cdecl]<RtDecal*, uint, void>)*(IntPtr*)(bytes + 216);
            s_rtSetDecalLights = (delegate* unmanaged[Cdecl]<int, void>)*(IntPtr*)(bytes + 224);
            s_rtSetLightTree = (delegate* unmanaged[Cdecl]<int, void>)*(IntPtr*)(bytes + 232);
            return null;
        }

        // Creates a hook and queues it; ApplyQueued enables everything queued.
        public static int CreateHook(IntPtr target, IntPtr detour, out IntPtr original)
        {
            IntPtr trampoline = IntPtr.Zero;
            int status = s_createHook(target, detour, &trampoline);
            original = trampoline;
            return status;
        }

        public static int ApplyQueued() => s_applyQueued();

        public static int RemoveHook(IntPtr target) => s_removeHook(target);

        public static void Log(int level, string message)
        {
            if (s_log == null || message == null)
                return;
            fixed (char* text = message)
                s_log(level, text);
        }

        public static bool DiagnosticsEnabled => s_diagnosticsEnabled != null && s_diagnosticsEnabled() != 0;

        // Returns 0, or the number of the RT core's step that failed (logged natively).
        public static int RtInitialize(IntPtr d3d11Device, GateMode gateMode, DebugView debugView, float patternStrength,
            string tracePath, Denoiser denoiser, bool characterLights, float lampIntensity, float emissiveIntensity,
            bool decalLights, bool lightTree)
        {
            fixed (char* path = tracePath ?? "")
            {
                var options = new RtOptions
                {
                    Size = (uint)sizeof(RtOptions),
                    GateMode = (int)gateMode,
                    PatternStrength = patternStrength,
                    DebugView = (int)debugView,
                    TracePath = (IntPtr)path,
                    Denoiser = (int)denoiser,
                    CharacterLights = characterLights ? 1 : 0,
                    LampIntensity = lampIntensity,
                    EmissiveIntensity = emissiveIntensity,
                    DecalLights = decalLights ? 1 : 0,
                    LightTree = lightTree ? 1 : 0,
                };
                CurrentGateMode = gateMode;
                return s_rtInitialize(d3d11Device, &options);
            }
        }

        public static int RtInstallGate() => s_rtInstallGate();

        public static void RtSetLightingList(IntPtr commandList) => s_rtSetLightingList(commandList);

        public static void RtBindLighting(IntPtr deferredContext) => s_rtBindLighting(deferredContext);

        public static void RtSunPassRecorded(IntPtr deferredContext) => s_rtSunPassRecorded(deferredContext);

        public static void RtSetGateMode(GateMode mode)
        {
            CurrentGateMode = mode;
            s_rtSetGateMode((int)mode);
        }

        // The mode last requested (RtInitialize, RtSetGateMode).
        public static volatile GateMode CurrentGateMode;

        public static void RtShutdown() => s_rtShutdown();

        public static void RtSetFrame(ref RtFrame frame)
        {
            fixed (RtFrame* pointer = &frame)
                s_rtSetFrame(pointer);
        }

        public static void RtSetDebugView(DebugView view) => s_rtSetDebugView((int)view);

        // Probe pixels (x, y pairs; up to 8): the material views log what their rays found there.
        public static void RtSetProbes(int[] pixels)
        {
            int count = Math.Min((pixels?.Length ?? 0) / 2, 8);
            uint* packed = stackalloc uint[8];
            for (int i = 0; i < count; i++)
                packed[i] = (uint)(pixels[2 * i] & 0xFFFF) | ((uint)(pixels[2 * i + 1] & 0xFFFF) << 16);
            s_rtSetProbes(packed, (uint)count);
        }

        // Scene mirror (src/native/rt/scene.h). Handles are never 0; 0 = refused.
        public static uint RtMeshCreate(ref RtMeshDesc desc, RtMeshPart[] parts, int partCount)
        {
            fixed (RtMeshPart* pointer = parts)
            fixed (RtMeshDesc* descPointer = &desc)
            {
                desc.Parts = (IntPtr)pointer;
                desc.PartCount = (uint)partCount;
                return s_rtMeshCreate(descPointer);
            }
        }

        public static void RtMeshRelease(uint mesh) => s_rtMeshRelease(mesh);

        // The game's environment probe cubemaps (ID3D11Resource*, far and close; zero: none).
        public static void RtSetEnvironment(IntPtr far, IntPtr close) => s_rtSetEnvironment(far, close);

        // The voxel material table; `paths` holds 18 texture files per entry (RtVoxelMaterial order), copied
        // into native strings for the call. `randomTexture`: the game's random texture (ID3D11Resource*) or zero.
        public static void RtVoxelMaterials(RtVoxelMaterial[] materials, string[] paths, int count, IntPtr randomTexture)
        {
            var strings = new IntPtr[count * 18];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    for (int t = 0; t < 18; t++)
                    {
                        string path = paths[i * 18 + t];
                        strings[i * 18 + t] = string.IsNullOrEmpty(path) ? IntPtr.Zero : Marshal.StringToHGlobalUni(path);
                        materials[i].Textures[t] = (long)strings[i * 18 + t];
                    }
                }
                fixed (RtVoxelMaterial* pointer = materials)
                    s_rtVoxelMaterials(pointer, (uint)count, randomTexture);
            }
            finally
            {
                foreach (IntPtr s in strings)
                {
                    if (s != IntPtr.Zero)
                        Marshal.FreeHGlobal(s);
                }
            }
        }

        // A variant of `mesh` with other materials (one per part of its description, in order): the same
        // geometry and BLAS; a mesh handle itself, released with RtMeshRelease or with its mesh. 0 = refused.
        public static uint RtMeshVariant(uint mesh, uint[] materials, int count)
        {
            fixed (uint* pointer = materials)
                return s_rtMeshVariant(mesh, pointer, (uint)count);
        }

        // `records`: the first count * stride bytes are copied.
        public static uint RtInstancingUpdate(uint instancing, byte[] records, int count, int stride)
        {
            fixed (byte* pointer = records)
                return s_rtInstancingUpdate(instancing, pointer, (uint)count, (uint)stride);
        }

        public static void RtInstancingRelease(uint instancing) => s_rtInstancingRelease(instancing);

        // A material from its four texture files (ColorMetal, NormalGloss, Extensions, Alphamask; null: the
        // game's default for the slot) or textures the game renders into (`rendered`, ID3D11Resource* per slot,
        // zero for none). Equal descriptions share a handle; 0 = refused.
        public static uint RtMaterialCreate(uint flags, string colorMetal, string normalGloss, string extensions, string alphamask,
            IntPtr[] rendered)
        {
            fixed (char* cm = colorMetal)
            fixed (char* ng = normalGloss)
            fixed (char* ext = extensions)
            fixed (char* alpha = alphamask)
            {
                var desc = new RtMaterialDesc
                {
                    Size = (uint)sizeof(RtMaterialDesc),
                    Flags = flags,
                    ColorMetal = (IntPtr)cm,
                    NormalGloss = (IntPtr)ng,
                    Extensions = (IntPtr)ext,
                    Alphamask = (IntPtr)alpha,
                    RenderedColorMetal = rendered[0],
                    RenderedNormalGloss = rendered[1],
                    RenderedExtensions = rendered[2],
                    RenderedAlphamask = rendered[3],
                };
                return s_rtMaterialCreate(&desc);
            }
        }

        // `overrides`: four floats per entry (color multiplier xyz, emissivity), see RtInstance.Overrides.
        public static void RtSceneSubmit(RtInstance[] instances, int instanceCount, RtBucket[] buckets, int bucketCount,
            float[] overrides, int overrideCount)
        {
            fixed (RtInstance* instancePointer = instances)
            fixed (RtBucket* bucketPointer = buckets)
            fixed (float* overridePointer = overrides)
                s_rtSceneSubmit(instancePointer, (uint)instanceCount, bucketPointer, (uint)bucketCount, overridePointer,
                    (uint)overrideCount);
        }

        // The lamps of the next frame (copied by the native side).
        public static void RtLightsSubmit(RtLight[] lights, int count)
        {
            fixed (RtLight* pointer = lights)
                s_rtLightsSubmit(pointer, (uint)count);
        }

        // The skinned objects of the frame submitted last (after RtSceneSubmit): `boneCount` bones of 12 floats
        // (RtSkin), the part palettes.
        public static void RtSkinsSubmit(RtSkin[] skins, int skinCount, float[] bones, int boneCount, uint[] palettes,
            int paletteCount)
        {
            fixed (RtSkin* skinPointer = skins)
            fixed (float* bonePointer = bones)
            fixed (uint* palettePointer = palettes)
                s_rtSkinsSubmit(skinPointer, (uint)skinCount, bonePointer, (uint)boneCount, palettePointer, (uint)paletteCount);
        }

        // Whether glowing decals light their surroundings, from the next frame on (a fresh statistics window).
        public static void RtSetDecalLights(bool on) => s_rtSetDecalLights(on ? 1 : 0);

        // Whether the lit views trace lamps and emitters at any distance (the light grid's coarse levels, filled
        // from the light tree) or 32 m around the camera, from the next frame on (the LightTree setting; the
        // inspector's A/B); starts a fresh statistics window.
        public static void RtSetLightTree(bool on) => s_rtSetLightTree(on ? 1 : 0);

        // The projected decals of the next frame, in the game's draw order (copied by the native side).
        public static void RtDecalsSubmit(RtDecal[] decals, int count)
        {
            fixed (RtDecal* pointer = decals)
                s_rtDecalsSubmit(pointer, (uint)count);
        }
    }

    // sert::rt::RtDecal: a projected decal (MyScreenDecals): the unit box placed by its world matrix (VRageMath, row
    // vectors; translation M41..M43, rotation-scale M11..M33), its textures as a material, its maps (1 color-metal,
    // 2 normal-gloss, 4 extensions: the game's DecalType) and its fade.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct RtDecal
    {
        public fixed double Translation[3];
        public fixed float Rotation[9];
        public uint Material;  // RtMaterialCreate handle
        public uint Maps;
        public float Fade;
    }

    // sert::rt::RtLight: a point or spot light of a MyLightComponent, in world space.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct RtLight
    {
        public uint Flags;         // 1: a spot light
        public uint Id;            // the light actor's id
        public uint MaskMaterial;  // spot: an RtMaterialCreate handle with the reflector mask as its alpha mask; 0: none
        public uint Reserved;
        public fixed double Position[3];
        public fixed float Direction[3];  // spot: the cone's axis
        public float ApertureCos;         // spot: the cone's cosine
        public fixed float Up[3];         // spot: the mask's up
        public float Range;
        public fixed float Color[3];      // intensity included
        public float Falloff;
        public float GlossFactor;
        public float DiffuseFactor;
        public fixed float Reserved2[2];
    }

    // sert::rt::RtSkin: a skinned object of the frame (a character), posed on the GPU with bone matrices. A bone
    // is 12 floats, the first three columns of its VRageMath matrix: M11 M21 M31 M41, M12 M22 M32 M42,
    // M13 M23 M33 M43. Per part of the instance's mesh description (in its order), the palettes give the part's
    // first bone, relative to FirstBone; a vertex's bone index selects within its part's palette.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RtSkin
    {
        public uint Instance;      // index into the frame's instances (RtSceneSubmit)
        public uint FirstBone;
        public uint BoneCount;
        public uint FirstPalette;
        public uint PaletteCount;  // parts
        public uint Reserved;
    }

    // sert::rt::RtMeshPart.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RtMeshPart
    {
        public uint IndexStart;
        public uint IndexCount;
        public int BaseVertex;
        public uint Flags;  // 1 alpha masked, 2 double-sided (drawn without back-face culling), 4 hideable
        public uint Material;  // RtMaterialCreate handle; 0: the game's defaults
    }

    // sert::rt::RtMaterialDesc.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RtMaterialDesc
    {
        public uint Size;
        public uint Flags;  // 1 alpha masked, 2 metalness colorable
        public IntPtr ColorMetal;  // const wchar_t*, absolute path or null
        public IntPtr NormalGloss;
        public IntPtr Extensions;
        public IntPtr Alphamask;
        // ID3D11Resource* of a texture the game renders into (screens), per slot, or zero; wins over the path.
        public IntPtr RenderedColorMetal;
        public IntPtr RenderedNormalGloss;
        public IntPtr RenderedExtensions;
        public IntPtr RenderedAlphamask;
    }

    // sert::rt::RtVoxelMaterial: an entry of the game's voxel material table.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct RtVoxelMaterial
    {
        public fixed float DistanceAndScale[4];  // initial scale, initial distance, scale multiplier, distance multiplier
        public fixed float Far1[4];              // scale, distance, texture index; TilingScale
        public fixed float Far2[4];
        public fixed float Far3[4];
        public fixed float ColorFar3[4];         // w: 1 for an entry in use
        public fixed long Textures[18];          // const wchar_t*: component * 6 + distance * 2 + axis
    }

    // sert::rt::RtMeshDesc: a mesh's buffers on the game's device.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RtMeshDesc
    {
        public uint Size;
        public uint PositionFormat;   // 0 packed half4, 1 float3
        public uint VertexStride;
        public uint VertexCount;
        public IntPtr VertexBuffer;   // ID3D11Buffer*
        public IntPtr AttributeBuffer;
        public uint AttributeStride;
        public uint IndexFormat;      // DXGI 57 (R16_UINT) or 42 (R32_UINT)
        public IntPtr IndexBuffer;
        public uint IndexCount;
        public uint PartCount;
        public IntPtr Parts;
        public uint Kind;             // 0 model, 1 old-pipeline mesh, 2 voxel cell
        public uint Flags;            // 1: the vertex layout has bones (deformed when drawn from cube records)
    }

    // sert::rt::RtInstance: a mesh at a world matrix (VRageMath, row vectors).
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct RtInstance
    {
        public uint Mesh;
        public uint Mask;
        public uint Flags;  // 1 metalness colorable, 2 some parts hidden in the main view
        public uint Id;
        public fixed double Translation[3];
        public fixed float Rotation[9];
        // 1 + the first of its parts' entries in the frame's overrides (RtSceneSubmit), in the order of the
        // parts its mesh was registered with; 0: the game's defaults (color multiplier and emissivity 1).
        public uint Overrides;  // a part with emissivity -1: hidden in the main view (Flags 2)
        public fixed float KeyColor[4];  // paint: HSV offset
    }

    // sert::rt::RtBucket: blocks [Start, Start + Count) of an instancing buffer in a cell.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct RtBucket
    {
        public uint Mesh;
        public uint Instancing;
        public uint Start;
        public uint Count;
        public fixed double Translation[3];
        public fixed float Rotation[9];
        public uint Mask;
    }

    // sert::rt::RtFrame (src/native/rt/common.h): the camera of the frame being
    // drawn, from MyRender11.Environment.Matrices (VRageMath matrices: row-major,
    // row vectors).
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct RtFrame
    {
        public uint Size;
        public uint Width;
        public uint Height;
        public uint Reserved;
        public fixed double CameraPosition[3];
        public double Reserved2;
        public fixed float ViewAt0[16];
        public fixed float InvViewAt0[16];
        public fixed float Projection[16];
        public fixed float InvProjection[16];
        public fixed float ViewProjectionAt0[16];
        public fixed float InvViewProjectionAt0[16];
        // MyRender11.Environment.Data.EnvironmentLight.
        public fixed float SunDirection[3];  // the sun's light travels along it
        public float SunDiffuse;
        public fixed float SunColor[3];
        public float Reserved3;
        // Environment.Data.TextureMultipliers: 6 multipliers, 6 shifts (albedo, metalness, gloss, AO,
        // emissive, color mask), then ColorizeHSV.
        public fixed float Multipliers[16];
        // MyCommon.FrameConstantsData (GameFrame.Capture), copied by the native side, or zero.
        public IntPtr GameFrame;
        public uint GameFrameSize;
        public uint Reserved4;
    }

    // sert::rt::Denoiser: what denoises the lit view's traced light.
    internal enum Denoiser
    {
        Auto = 0,               // DLSS Ray Reconstruction where available (NVIDIA RTX), else our filter
        RayReconstruction = 1,  // DLSS Ray Reconstruction (our filter when it is unavailable)
        Filter = 2,             // our filter
    }

    // sert::rt::DebugView.
    internal enum DebugView
    {
        Stripes = 0,
        Normals = 1,
        BaseColor = 2,
        WorldGrid = 3,
        Reprojection = 4,
        Parts = 5,     // primary rays through the scene mirror
        Mismatch = 6,  // their depth against the G-buffer's
        Faces = 7,     // front and back faces
        Clay = 8,      // traced sun shadows and sky occlusion on white surfaces
        Material = 9,         // the traced base color (textures, paint)
        MaterialCompare = 10, // the traced material against the G-buffer's
        Paths = 11,           // how each hit object reaches the mirror
        Lit = 12,             // traced light: emissive, the sun, emissive surfaces as lights, one diffuse and one
                              // specular bounce, denoised
        LitRaw = 13,          // the same before the spatial filter (temporal accumulation only)
        Emitters = 14,        // the light of emissive surfaces alone (accumulated, not filtered)
        Motion = 15,          // each pixel's motion since the previous frame
        LitRR = 16,           // traced light denoised by DLSS Ray Reconstruction (when available)
        LitFilter = 17,       // traced light denoised by our filter
        EmittersReference = 18,  // the emitters view without reusing light samples: what it must match on average
        EmittersPowerGrid = 19,  // the emitters view with P2p's light grid (lights drawn by power alone): the A/B
    }

    // sert::rt::GateMode.
    internal enum GateMode
    {
        Off = 0,
        RoundTrip = 1,
        Overlay = 2,
        Replace = 3,
    }
}
