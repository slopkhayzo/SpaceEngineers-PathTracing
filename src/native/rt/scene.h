#pragma once

#include "common.h"

#include <cstddef>
#include <cstdint>
#include <string>

namespace sert::rt {

// The scene mirror (research/docs/space-engineers-rt-plan.md, §5.6): the
// game's meshes copied into D3D12 with one BLAS each (built inside the gate),
// and a TLAS per frame in camera-relative space. The structs below come from
// the managed side and are mirrored in src/managed/NativeApi.cs.

// One draw range of a mesh: one BLAS geometry, one geometry record.
struct RtMeshPart {
    std::uint32_t indexStart;
    std::uint32_t indexCount;
    std::int32_t baseVertex;  // added to every index of the part
    std::uint32_t flags;      // kPartAlphaMasked, kPartDoubleSided, kPartHideable
    std::uint32_t material;   // SceneCreateMaterial handle; 0: the game's defaults
};
static_assert(sizeof(RtMeshPart) == 20);

constexpr std::uint32_t kPartAlphaMasked = 1u;  // alpha tested by the game: non-opaque, tested by the rays
constexpr std::uint32_t kPartDoubleSided = 2u;  // drawn without back-face culling
constexpr std::uint32_t kPartHideable = 4u;  // the game hides it at times: non-opaque, tested by the rays

// A material: the files of its four texture slots (absolute paths, as the
// game's MyFileTexture.m_path; null: the game's default for the slot), or a
// texture the game renders into (an LCD screen's content: its
// ID3D11Resource*, mirrored every gate; takes precedence over the path).
struct RtMaterialDesc {
    std::uint32_t size;
    std::uint32_t flags;           // kMaterial*
    const wchar_t* textures[4];    // ColorMetal, NormalGloss, Extensions (add maps), Alphamask
    void* rendered[4];             // ID3D11Resource* of a game-rendered texture, or null
};
static_assert(sizeof(RtMaterialDesc) == 72);

constexpr std::uint32_t kMaterialAlphaMasked = 1u;
constexpr std::uint32_t kMaterialMetalnessColorable = 2u;  // paint also on metal (METALNESS_COLORABLE)

enum class PositionFormat : std::uint32_t {
    packedHalf4 = 0,  // half4 whose w scales xyz (PositionPacker, VertexTransformations.hlsli)
    float3 = 1,       // voxels
};

enum class MeshKind : std::uint32_t {
    model = 0,        // GeometryStage2 MyLod
    oldPipeline = 1,  // MyMeshes LOD mesh (cube parts, old-pipeline models)
    voxelCell = 2,
};

// A mesh on the game's device: its buffers are copied at the next gate.
struct RtMeshDesc {
    std::uint32_t size;
    std::uint32_t positionFormat;   // PositionFormat; the position sits at byte 0 of each VB0 vertex
    std::uint32_t vertexStride;     // VB0
    std::uint32_t vertexCount;
    void* vertexBuffer;             // ID3D11Buffer* VB0
    void* attributeBuffer;          // ID3D11Buffer* VB1 (normal, tangent, ...), or null
    std::uint32_t attributeStride;  // VB1
    std::uint32_t indexFormat;      // DXGI_FORMAT_R16_UINT or DXGI_FORMAT_R32_UINT
    void* indexBuffer;              // ID3D11Buffer*
    std::uint32_t indexCount;       // indices copied from the buffer's start
    std::uint32_t partCount;
    const RtMeshPart* parts;
    std::uint32_t kind;             // MeshKind
    std::uint32_t flags;            // kMesh*
};
static_assert(sizeof(RtMeshDesc) == 72);

// The vertex layout has bones (MyVertexLayoutInfo.HasBonesInfo): drawn from
// cube-block records, the game deforms the mesh and shifts its UVs by the
// record's pattern offset (USE_DEFORMED_CUBE_INSTANCING).
constexpr std::uint32_t kMeshBones = 1u;
static_assert(offsetof(RtMeshDesc, vertexBuffer) == 16);
static_assert(offsetof(RtMeshDesc, indexBuffer) == 40);
static_assert(offsetof(RtMeshDesc, parts) == 56);

// An object of this frame: a mesh at an actor's world matrix (VRageMath, row
// vectors), split into a float rotation-scale and a double translation.
struct RtInstance {
    std::uint32_t mesh;
    std::uint32_t mask;      // kMask*
    std::uint32_t flags;     // kInstanceMetalnessColorable, kInstanceHiddenParts
    std::uint32_t id;        // the game's actor id
    double translation[3];   // M41..M43
    float rotation[9];       // M11..M13, M21..M23, M31..M33
    std::uint32_t overrides; // 1 + its first entry in the frame's part overrides (SceneSubmit); 0: none
    float keyColor[4];       // paint, an HSV offset (MyInstance.KeyColor, MyRenderableComponent.m_keyColor)
};
static_assert(sizeof(RtInstance) == 96);

constexpr std::uint32_t kInstanceMetalnessColorable = 1u;
// Some of its parts the game hides in the main view (a negative emissivity in
// their overrides; registered kPartHideable): the TLAS instance's
// InstanceContributionToHitGroupIndex is 1, so that the main view's rays look
// the overrides up (scene_common.hlsli AlphaSolid).
constexpr std::uint32_t kInstanceHiddenParts = 2u;
// For a voxel cell, keyColor carries its VoxelOffset (xyz, metres, the cell's
// corner in the voxel map) and VoxelScale (w, 2^LOD): voxels have no paint.

// A lamp of this frame: the point or spot light of a MyLightComponent (its
// UpdateRenderLightData layout, placed by its actor). Mirrored in NativeApi.cs.
struct RtLight {
    std::uint32_t flags;         // kLightSpot
    std::uint32_t id;            // the light actor's id
    std::uint32_t maskMaterial;  // spot: a material handle with the reflector mask as its alpha mask (0: none)
    std::uint32_t reserved;
    double position[3];          // world
    float direction[3];          // spot: the cone's axis
    float apertureCos;           // spot: the cone's cosine
    float up[3];                 // spot: the mask's up
    float range;
    float color[3];              // intensity included
    float falloff;
    float glossFactor;
    float diffuseFactor;
    float reserved2[2];
};
static_assert(sizeof(RtLight) == 104);

constexpr std::uint32_t kLightSpot = 1u;
constexpr std::uint32_t kMaxLamps = 8192;

// A skinned object of this frame (a character): an instance of SceneSubmit's
// list whose mesh the GPU poses with bone matrices, as the game's vertex
// skinning does (USE_SKINNING). The mesh's VB0 holds the bone weights and
// indices (MyVertexFormatPositionSkinning, kMeshBones); a vertex's bone index
// selects within its part's palette. Mirrored in NativeApi.cs.
struct RtSkin {
    std::uint32_t instance;      // index into the frame's instances (SceneSubmit)
    std::uint32_t firstBone;     // into the frame's bones (SceneSubmitSkins)
    std::uint32_t boneCount;
    std::uint32_t firstPalette;  // into the frame's palettes: per part of the mesh description, in its
                                 // order, the part's first bone (relative to firstBone)
    std::uint32_t paletteCount;  // parts
    std::uint32_t reserved;
};
static_assert(sizeof(RtSkin) == 24);

// A bone: the first three columns of its VRageMath matrix (row vectors),
// x' = dot(column0, (p, 1)): M11 M21 M31 M41, M12 M22 M32 M42, M13 M23 M33 M43.
constexpr std::uint32_t kBoneFloats = 12;

// Instances of one model under one renderable: records [start, start + count)
// of an instancing buffer, placed by the renderable's world matrix: cube
// blocks of a render cell (64-byte MyVertexFormatCubeInstance records) or
// generic instances such as trees (32-byte MyVertexFormatGenericInstance
// records). Expanded into TLAS instances on the GPU.
struct RtBucket {
    std::uint32_t mesh;
    std::uint32_t instancing;
    std::uint32_t start;
    std::uint32_t count;
    double translation[3];
    float rotation[9];
    std::uint32_t mask;
};
static_assert(sizeof(RtBucket) == 80);

constexpr std::uint32_t kMaskPrimary = 0x01;  // drawn in the main view
constexpr std::uint32_t kMaskShadow = 0x02;   // casts shadows

constexpr std::uint32_t kMaxGeometryPages = 16;  // SRVs in the caller's heap: 15 pages of meshes (256 MB
                                                 // each), then the posed skinned meshes'

// One entry of the game's voxel material table, by the index voxel vertices
// carry (MyVoxelMaterials.Table; the constants as its triplanar shaders get
// them, MyVoxelMaterialEntry.StandardTilingSetup and Far3Color). Mirrored in
// NativeApi.cs.
struct RtVoxelMaterial {
    float distanceAndScale[4];  // initial scale, initial distance, scale multiplier, distance multiplier
    float far1[4];              // scale, distance (0: off), texture index; TilingScale
    float far2[4];              // scale, distance, texture index; unused
    float far3[4];              // scale, distance, texture index; unused
    float colorFar3[4];         // Far3Color (beyond the far textures); w: 1 for an entry in use
    // Absolute texture files by component (ColorMetal, NormalGloss, Extensions)
    // * 6 + distance (near, far1, far2) * 2 + axis (XZnY, Y); null or empty: the
    // component's default.
    const wchar_t* textures[18];
};
static_assert(sizeof(RtVoxelMaterial) == 224);

constexpr std::uint32_t kMaxVoxelMaterials = 128;  // MAX_VOXEL_MATERIALS of the game's shaders

// GPU bindings of the frame's scene for the trace passes.
struct SceneFrame {
    D3D12_GPU_VIRTUAL_ADDRESS tlas = 0;            // 0: no scene this frame
    D3D12_GPU_VIRTUAL_ADDRESS records = 0;         // StructuredBuffer<GeometryRecord>
    D3D12_GPU_VIRTUAL_ADDRESS materials = 0;       // StructuredBuffer<MaterialRecord>
    D3D12_GPU_VIRTUAL_ADDRESS instanceData = 0;    // StructuredBuffer<float4>, by TLAS instance index
    D3D12_GPU_VIRTUAL_ADDRESS voxelMaterials = 0;  // StructuredBuffer<VoxelMaterial>, always valid
    std::uint32_t voxelMaterialCount = 0;          // 0: no table yet
    std::uint32_t voxelRandom = 0;                 // descriptor slot of the random texture
    std::uint32_t instances = 0;
    D3D12_GPU_VIRTUAL_ADDRESS instanceDescs = 0;   // the TLAS's D3D12_RAYTRACING_INSTANCE_DESCs (NON_PIXEL_SHADER_RESOURCE)
    D3D12_GPU_VIRTUAL_ADDRESS partOverrides = 0;   // StructuredBuffer<float4>, per record of the objects with overrides
    D3D12_GPU_VIRTUAL_ADDRESS lamps = 0;           // StructuredBuffer<Lamp> (lights.hlsli), at least one entry
    D3D12_GPU_VIRTUAL_ADDRESS previousTransforms = 0;  // per TLAS instance, 3 float4: its transform in the previous
                                                       // recorded frame, around that frame's camera
    std::uint32_t lampCount = 0;
    std::uint32_t materialCount = 0;               // entries of the material table
    std::uint64_t materialsVersion = 0;            // changes whenever the material table is written
};

// `pageDescriptors` are kMaxGeometryPages CBV/SRV/UAV descriptors of a
// shader-visible heap: the geometry pages' raw SRVs (ByteAddressBuffer[]).
// `lampIntensity` scales the lamps' colours (the LampIntensity setting): their
// traced light, the light list's weights and the grid's alike.
[[nodiscard]] bool SceneInitialize(const Devices& devices, D3D12_CPU_DESCRIPTOR_HANDLE pageDescriptors,
                                   UINT descriptorSize, float lampIntensity);

// Any thread. Handles are never 0.
[[nodiscard]] std::uint32_t SceneCreateMesh(const RtMeshDesc& desc);
// Equal descriptions return the same handle; materials are never released.
[[nodiscard]] std::uint32_t SceneCreateMaterial(const RtMaterialDesc& desc);
// A variant of `mesh` (a SceneCreateMesh handle) with other materials, as an
// object with a skin needs: the same geometry and BLAS, its own geometry
// records. `materials` has one entry per part of the mesh's description, in
// its order. Usable wherever a mesh handle is; released with SceneReleaseMesh,
// and with its mesh. 0 = refused.
[[nodiscard]] std::uint32_t SceneCreateVariant(std::uint32_t mesh, const std::uint32_t* materials,
                                               std::uint32_t count);
void SceneReleaseMesh(std::uint32_t mesh);
// Replaces the records of an instancing buffer (handle 0: a new one); stride
// 64 (cube blocks) or 32 (generic instances).
[[nodiscard]] std::uint32_t SceneUpdateInstancing(std::uint32_t instancing, const void* records,
                                                  std::uint32_t count, std::uint32_t stride);
void SceneReleaseInstancing(std::uint32_t instancing);
// The objects of the next frame (copied). `overrides`: float4 entries, per
// part of an instance's mesh description in its order, from the instance's
// `overrides` - 1: the color multiplier (xyz) and emissivity (w) the game sets
// per object and material (MyRenderProxy.UpdateColorEmissivity).
void SceneSubmit(const RtInstance* instances, std::uint32_t instanceCount, const RtBucket* buckets,
                 std::uint32_t bucketCount, const float* overrides, std::uint32_t overrideCount);
// The lamps of the next frame (copied; at most kMaxLamps are used).
void SceneSubmitLights(const RtLight* lights, std::uint32_t count);
// The skinned objects of the next frame (copied), their bones (kBoneFloats
// floats each) and palettes; goes with the next SceneSubmit. An instance with
// a skin is drawn posed, or not at all while its pose cannot be made.
void SceneSubmitSkins(const RtSkin* skins, std::uint32_t skinCount, const float* bones, std::uint32_t boneCount,
                      const std::uint32_t* palettes, std::uint32_t paletteCount);

// Render thread, at the gate before the ready signal: copies the geometry
// registered since the last gate into the transport buffer (D3D11 immediate
// context). Every call must be followed by SceneRecord in the same gate.
void SceneCopyIn(ID3D11DeviceContext* immediate);

// Render thread, inside the gate's D3D12 window: geometry upload, position
// decode and BLAS builds of what SceneCopyIn copied, block expansion and the
// TLAS around `camera`. `slot` selects the frame's upload memory (free when
// called); `retireAfter` is a fence value after which nothing recorded so far
// is in use.
// `previousCamera`: the previous recorded frame's camera (null: none), around
// which the instances' transforms of then are placed (motion vectors).
[[nodiscard]] SceneFrame SceneRecord(ID3D12GraphicsCommandList4* list, std::size_t slot, const double camera[3],
                                     const double* previousCamera, UINT64 retireAfter);

// Frees memory retired before `completed` (the D3D12 fence value).
void SceneCollect(UINT64 completed);

// Statistics since the last report, into the log.
void SceneReport();

// A material's texture files (folder and file name per slot), for the log.
[[nodiscard]] std::wstring SceneMaterialName(std::uint32_t material);

// Any thread. Replaces the voxel material table (`count` entries, at most
// kMaxVoxelMaterials) and the random texture the game's tiled triplanar
// sampling reads (MyGeneratedTextureManager.RandomTex: mirrored like a
// screen, TextureRequestDynamic).
void SceneSetVoxelMaterials(const RtVoxelMaterial* materials, std::uint32_t count, ID3D11Resource* random);

} // namespace sert::rt
