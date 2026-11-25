#pragma once

#include <cstddef>
#include <cstdint>

namespace sert {

// Settings for the ray-tracing core, passed by the managed part to
// rtInitialize. Mirrored in src/managed/NativeApi.cs.
struct RtOptions {
    std::uint32_t size;
    // 0 = no gate, 1 = cross-device fence round trip only, 2 = round trip plus
    // the D3D12 test pattern added by the patched sun pass, 3 = the pattern
    // replaces the lit frame (rt::GateMode).
    std::int32_t gateMode;
    float patternStrength;
    // 0 stripes, 1 normals, 2 base color, 3 world grid, 4 reprojection, 5-12
    // traced parts / mismatch / faces / clay / material / material compare /
    // paths / light (rt::DebugView).
    std::int32_t debugView;
    // Directory for gate.csv (per-frame gate timings), or null/empty. Copied.
    const wchar_t* tracePath;
    // What denoises the traced light of the lit view: 0 Ray Reconstruction
    // where available, else our filter; 1 Ray Reconstruction; 2 our filter
    // (rt::Denoiser).
    std::int32_t denoiser;
    // Characters' glowing suit parts light their surroundings (1) or only glow
    // where they are seen (0).
    std::int32_t characterLights;
    // Multipliers of the traced light of the game's lamps (and of the game's
    // own lamp passes beyond the traced area) and of the light emissive
    // surfaces cast (not their own glow); 1 = as the game sets them.
    float lampIntensity;
    float emissiveIntensity;
    // Glowing decals (indicator lights, signs) light their surroundings (1) or
    // only glow where they are seen (0).
    std::int32_t decalLights;
    // Lamps and emitters traced at any distance (1: the light grid's coarse
    // levels, filled from the light tree) or 32 m around the camera (0).
    std::int32_t lightTree;
    // DLSS mode of Ray Reconstruction (rt::DlssMode): 0 DLAA (the light traced
    // at the game's resolution), 1 quality, 2 balanced, 3 performance, 4 ultra
    // performance (traced at a smaller size, Ray Reconstruction upscaling).
    std::int32_t dlssMode;
};

static_assert(sizeof(RtOptions) == 56);
static_assert(offsetof(RtOptions, tracePath) == 16);
static_assert(offsetof(RtOptions, denoiser) == 24);
static_assert(offsetof(RtOptions, characterLights) == 28);
static_assert(offsetof(RtOptions, lampIntensity) == 32);
static_assert(offsetof(RtOptions, emissiveIntensity) == 36);
static_assert(offsetof(RtOptions, decalLights) == 40);
static_assert(offsetof(RtOptions, lightTree) == 44);
static_assert(offsetof(RtOptions, dlssMode) == 48);

// Function table handed to the managed side; its address travels in the
// argument of the managed entry point. The layout is mirrored in
// src/managed/NativeApi.cs: bump SE_RTHWACCEL_API_VERSION when it changes.
struct NativeApi {
    std::uint32_t size;
    std::uint32_t version;
    // Creates a MinHook detour and queues it for enabling; *original receives
    // the trampoline. Returns the MH_STATUS value (0 = MH_OK).
    int(__cdecl* createHook)(void* target, void* detour, void** original);
    // Enables every queued hook with one thread freeze.
    int(__cdecl* applyQueued)();
    int(__cdecl* removeHook)(void* target);
    void(__cdecl* log)(int level, const wchar_t* message);
    int(__cdecl* diagnosticsEnabled)();
    // Ray-tracing core (src/native/rt/rt_core.h).
    int(__cdecl* rtInitialize)(void* d3d11Device, const RtOptions* options);
    int(__cdecl* rtInstallGate)();
    void(__cdecl* rtSetLightingList)(void* commandList);
    void(__cdecl* rtBindLighting)(void* deferredContext);
    void(__cdecl* rtSunPassRecorded)(void* deferredContext);
    void(__cdecl* rtSetGateMode)(int mode);
    void(__cdecl* rtShutdown)();
    // The camera of the frame whose lighting was just recorded (rt::RtFrame).
    void(__cdecl* rtSetFrame)(const void* frame);
    void(__cdecl* rtSetDebugView)(int view);
    // Scene mirror (src/native/rt/scene.h): meshes (rt::RtMeshDesc; returns a
    // handle, 0 = refused), cube-block records, and each frame's objects
    // (rt::RtInstance, rt::RtBucket arrays).
    std::uint32_t(__cdecl* rtMeshCreate)(const void* desc);
    void(__cdecl* rtMeshRelease)(std::uint32_t mesh);
    std::uint32_t(__cdecl* rtInstancingUpdate)(std::uint32_t instancing, const void* records, std::uint32_t count,
                                               std::uint32_t stride);
    void(__cdecl* rtInstancingRelease)(std::uint32_t instancing);
    // ... and the part overrides (float4 each, rt::SceneSubmit).
    void(__cdecl* rtSceneSubmit)(const void* instances, std::uint32_t instanceCount, const void* buckets,
                                 std::uint32_t bucketCount, const float* overrides, std::uint32_t overrideCount);
    // A material (rt::RtMaterialDesc): equal descriptions share a handle; 0 = refused.
    std::uint32_t(__cdecl* rtMaterialCreate)(const void* desc);
    // Up to 8 probe pixels (x | y << 16) whose rays the material views log; count 0 = none.
    void(__cdecl* rtSetProbes)(const std::uint32_t* pixels, std::uint32_t count);
    // A variant of a mesh with other materials (skins): one material handle per
    // part of the mesh's description; a mesh handle itself. 0 = refused.
    std::uint32_t(__cdecl* rtMeshVariant)(std::uint32_t mesh, const std::uint32_t* materials, std::uint32_t count);
    // The voxel material table (rt::RtVoxelMaterial[count]) and the game's random texture (ID3D11Resource*).
    void(__cdecl* rtVoxelMaterials)(const void* materials, std::uint32_t count, void* randomTexture);
    // The game's environment probe cubemaps (ID3D11Resource*, far and close; null: none).
    void(__cdecl* rtSetEnvironment)(void* farProbe, void* closeProbe);
    // The lamps of the next frame (rt::RtLight[count], copied).
    void(__cdecl* rtLightsSubmit)(const void* lights, std::uint32_t count);
    // The skinned objects of the frame submitted last (rt::RtSkin[skinCount]), their bones
    // (rt::kBoneFloats floats each) and part palettes (rt::SceneSubmitSkins; copied).
    void(__cdecl* rtSkinsSubmit)(const void* skins, std::uint32_t skinCount, const float* bones,
                                 std::uint32_t boneCount, const std::uint32_t* palettes, std::uint32_t paletteCount);
    // The projected decals of the next frame (rt::RtDecal[count], copied).
    void(__cdecl* rtDecalsSubmit)(const void* decals, std::uint32_t count);
    // Whether glowing decals light their surroundings (1) or only glow (0),
    // from the next frame on; starts a fresh statistics window.
    void(__cdecl* rtSetDecalLights)(int on);
    // Whether the lit views trace lamps and emitters at any distance (1) or
    // 32 m around the camera (0), from the next frame on; starts a fresh
    // statistics window.
    void(__cdecl* rtSetLightTree)(int on);
    // The DLSS mode of Ray Reconstruction (rt::DlssMode: 0 DLAA ... 4 ultra
    // performance), from the next frame on; starts a fresh statistics window.
    void(__cdecl* rtSetDlssMode)(int mode);
    // One of the settings the game's options screen changes while playing
    // (rt::Option: 0 denoiser, 1 character lights, 2 lamp intensity, 3
    // emissive intensity), from the next frame on.
    void(__cdecl* rtSetOption)(int option, float value);
    // The presenter: 0 the game's swap chain presents, 1 ours (a D3D12
    // flip-model chain over the game's window), 2 ours through Streamline
    // (DLSS Frame Generation off), 3 that with DLSS-G on; the game's
    // IDXGISwapChain (its first call hooks Present); `window` gets the game's
    // window. 0 when set.
    int(__cdecl* rtSetPresenter)(void* swapChain, int mode, void** window);
    // On the thread of the game's window: the presenter's window made over
    // it (until then the game's swap chain presents). 0 when made.
    int(__cdecl* rtPresenterAttach)();
    // Render thread, once the game drew its scene (before its HUD): the frame
    // copied for frame generation (nothing without it).
    void(__cdecl* rtCaptureHudless)();
    // Whether frame generation can run: 0 yes, 1 not known yet (Streamline
    // loads with the presenter), 2 no. `text` (`capacity` characters, ended
    // by a 0) gets why not, or when it can, anything that hurts it.
    int(__cdecl* rtFrameGenStatus)(wchar_t* text, int capacity);
};

// The managed side reads the table by these offsets (NativeApi.cs).
static_assert(sizeof(NativeApi) == 288);
static_assert(offsetof(NativeApi, createHook) == 8);
static_assert(offsetof(NativeApi, applyQueued) == 16);
static_assert(offsetof(NativeApi, removeHook) == 24);
static_assert(offsetof(NativeApi, log) == 32);
static_assert(offsetof(NativeApi, diagnosticsEnabled) == 40);
static_assert(offsetof(NativeApi, rtInitialize) == 48);
static_assert(offsetof(NativeApi, rtInstallGate) == 56);
static_assert(offsetof(NativeApi, rtSetLightingList) == 64);
static_assert(offsetof(NativeApi, rtBindLighting) == 72);
static_assert(offsetof(NativeApi, rtSunPassRecorded) == 80);
static_assert(offsetof(NativeApi, rtSetGateMode) == 88);
static_assert(offsetof(NativeApi, rtShutdown) == 96);
static_assert(offsetof(NativeApi, rtSetFrame) == 104);
static_assert(offsetof(NativeApi, rtSetDebugView) == 112);
static_assert(offsetof(NativeApi, rtMeshCreate) == 120);
static_assert(offsetof(NativeApi, rtMeshRelease) == 128);
static_assert(offsetof(NativeApi, rtInstancingUpdate) == 136);
static_assert(offsetof(NativeApi, rtInstancingRelease) == 144);
static_assert(offsetof(NativeApi, rtSceneSubmit) == 152);
static_assert(offsetof(NativeApi, rtMaterialCreate) == 160);
static_assert(offsetof(NativeApi, rtSetProbes) == 168);
static_assert(offsetof(NativeApi, rtMeshVariant) == 176);
static_assert(offsetof(NativeApi, rtVoxelMaterials) == 184);
static_assert(offsetof(NativeApi, rtSetEnvironment) == 192);
static_assert(offsetof(NativeApi, rtLightsSubmit) == 200);
static_assert(offsetof(NativeApi, rtSkinsSubmit) == 208);
static_assert(offsetof(NativeApi, rtDecalsSubmit) == 216);
static_assert(offsetof(NativeApi, rtSetDecalLights) == 224);
static_assert(offsetof(NativeApi, rtSetLightTree) == 232);
static_assert(offsetof(NativeApi, rtSetDlssMode) == 240);
static_assert(offsetof(NativeApi, rtSetOption) == 248);
static_assert(offsetof(NativeApi, rtSetPresenter) == 256);
static_assert(offsetof(NativeApi, rtPresenterAttach) == 264);
static_assert(offsetof(NativeApi, rtCaptureHudless) == 272);
static_assert(offsetof(NativeApi, rtFrameGenStatus) == 280);

[[nodiscard]] bool InitializeHooking();
[[nodiscard]] const NativeApi* GetNativeApi();

} // namespace sert
