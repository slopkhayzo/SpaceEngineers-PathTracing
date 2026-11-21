#include "native_api.h"

#include "logger.h"
#include "plugin_version.h"
#include "rt/common.h"
#include "rt/rt_core.h"
#include "rt/scene.h"
#include "rt/textures.h"

#include <MinHook.h>

namespace sert {
namespace {

int __cdecl CreateHook(void* target, void* detour, void** original) {
    if (target == nullptr || detour == nullptr || original == nullptr) {
        return MH_ERROR_NOT_EXECUTABLE;
    }
    MH_STATUS status = MH_CreateHook(target, detour, original);
    if (status != MH_OK) {
        return status;
    }
    status = MH_QueueEnableHook(target);
    if (status != MH_OK) {
        MH_RemoveHook(target);
    }
    return status;
}

int __cdecl ApplyQueued() {
    return MH_ApplyQueued();
}

int __cdecl RemoveHook(void* target) {
    const MH_STATUS disabled = MH_DisableHook(target);
    const MH_STATUS removed = MH_RemoveHook(target);
    return disabled != MH_OK ? disabled : removed;
}

void __cdecl Log(const int level, const wchar_t* message) {
    if (message == nullptr) {
        return;
    }
    log::Level mapped = log::Level::info;
    switch (level) {
    case 0:
        mapped = log::Level::debug;
        break;
    case 2:
        mapped = log::Level::warning;
        break;
    case 3:
        mapped = log::Level::error;
        break;
    default:
        break;
    }
    log::Write(mapped, message);
}

int __cdecl DiagnosticsEnabled() {
    return log::DiagnosticsEnabled() ? 1 : 0;
}

// The managed side calls these from the game's render and recording threads:
// nothing may escape as a C++ exception.

int __cdecl RtInitialize(void* d3d11Device, const RtOptions* options) {
    if (d3d11Device == nullptr || options == nullptr || options->size != sizeof(RtOptions)) {
        return -1;
    }
    try {
        rt::Options parsed;
        parsed.gateMode = static_cast<rt::GateMode>(options->gateMode);
        parsed.patternStrength = options->patternStrength;
        parsed.debugView = static_cast<rt::DebugView>(options->debugView);
        parsed.denoiser = static_cast<rt::Denoiser>(options->denoiser);
        parsed.characterLights = options->characterLights != 0;
        parsed.lampIntensity = options->lampIntensity;
        parsed.emissiveIntensity = options->emissiveIntensity;
        parsed.decalLights = options->decalLights != 0;
        parsed.tracePath = options->tracePath != nullptr ? options->tracePath : L"";
        return rt::Initialize(static_cast<ID3D11Device*>(d3d11Device), parsed);
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception during initialization.");
        return -2;
    }
}

int __cdecl RtInstallGate() {
    try {
        return rt::InstallGate() ? 0 : 1;
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while installing the gate.");
        return -2;
    }
}

void __cdecl RtSetLightingList(void* commandList) {
    rt::SetLightingList(static_cast<ID3D11CommandList*>(commandList));
}

void __cdecl RtBindLighting(void* deferredContext) {
    try {
        rt::BindLighting(static_cast<ID3D11DeviceContext*>(deferredContext));
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while binding the lighting inputs.");
    }
}

void __cdecl RtSunPassRecorded(void* deferredContext) {
    try {
        rt::SunPassRecorded(static_cast<ID3D11DeviceContext*>(deferredContext));
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception after the sun pass was recorded.");
    }
}

void __cdecl RtSetGateMode(const int mode) {
    rt::SetGateMode(static_cast<rt::GateMode>(mode));
}

void __cdecl RtShutdown() {
    rt::Shutdown();
}

void __cdecl RtSetFrame(const void* frame) {
    if (frame != nullptr) {
        rt::SetFrame(*static_cast<const rt::RtFrame*>(frame));
    }
}

void __cdecl RtSetDebugView(const int view) {
    rt::SetDebugView(static_cast<rt::DebugView>(view));
}

std::uint32_t __cdecl RtMeshCreate(const void* desc) {
    if (desc == nullptr) {
        return 0;
    }
    try {
        return rt::SceneCreateMesh(*static_cast<const rt::RtMeshDesc*>(desc));
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while registering a mesh.");
        return 0;
    }
}

void __cdecl RtMeshRelease(const std::uint32_t mesh) {
    try {
        rt::SceneReleaseMesh(mesh);
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while releasing a mesh.");
    }
}

std::uint32_t __cdecl RtInstancingUpdate(const std::uint32_t instancing, const void* records,
                                         const std::uint32_t count, const std::uint32_t stride) {
    try {
        return rt::SceneUpdateInstancing(instancing, records, count, stride);
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while updating block records.");
        return 0;
    }
}

void __cdecl RtInstancingRelease(const std::uint32_t instancing) {
    try {
        rt::SceneReleaseInstancing(instancing);
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while releasing block records.");
    }
}

std::uint32_t __cdecl RtMaterialCreate(const void* desc) {
    if (desc == nullptr) {
        return 0;
    }
    try {
        return rt::SceneCreateMaterial(*static_cast<const rt::RtMaterialDesc*>(desc));
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while registering a material.");
        return 0;
    }
}

std::uint32_t __cdecl RtMeshVariant(const std::uint32_t mesh, const std::uint32_t* materials,
                                    const std::uint32_t count) {
    try {
        return rt::SceneCreateVariant(mesh, materials, count);
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while registering a material variant.");
        return 0;
    }
}

void __cdecl RtVoxelMaterials(const void* materials, const std::uint32_t count, void* randomTexture) {
    try {
        rt::SceneSetVoxelMaterials(static_cast<const rt::RtVoxelMaterial*>(materials), count,
                                   static_cast<ID3D11Resource*>(randomTexture));
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while setting the voxel materials.");
    }
}

void __cdecl RtSetEnvironment(void* farProbe, void* closeProbe) {
    try {
        rt::SetEnvironment(static_cast<ID3D11Resource*>(farProbe), static_cast<ID3D11Resource*>(closeProbe));
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while setting the environment cubemaps.");
    }
}

void __cdecl RtSetProbes(const std::uint32_t* pixels, const std::uint32_t count) {
    rt::SetProbes(pixels, count);
}

void __cdecl RtSceneSubmit(const void* instances, const std::uint32_t instanceCount, const void* buckets,
                           const std::uint32_t bucketCount, const float* overrides,
                           const std::uint32_t overrideCount) {
    try {
        rt::SceneSubmit(static_cast<const rt::RtInstance*>(instances), instanceCount,
                        static_cast<const rt::RtBucket*>(buckets), bucketCount, overrides, overrideCount);
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while submitting the scene.");
    }
}

void __cdecl RtLightsSubmit(const void* lights, const std::uint32_t count) {
    try {
        rt::SceneSubmitLights(static_cast<const rt::RtLight*>(lights), count);
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while submitting the lamps.");
    }
}

void __cdecl RtSkinsSubmit(const void* skins, const std::uint32_t skinCount, const float* bones,
                           const std::uint32_t boneCount, const std::uint32_t* palettes,
                           const std::uint32_t paletteCount) {
    try {
        rt::SceneSubmitSkins(static_cast<const rt::RtSkin*>(skins), skinCount, bones, boneCount, palettes,
                             paletteCount);
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while submitting the skinned objects.");
    }
}

void __cdecl RtDecalsSubmit(const void* decals, const std::uint32_t count) {
    try {
        rt::SceneSubmitDecals(static_cast<const rt::RtDecal*>(decals), count);
    } catch (...) {
        log::Write(log::Level::error, L"RT core: exception while submitting the projected decals.");
    }
}

void __cdecl RtSetDecalLights(const int on) {
    rt::SetDecalLights(on != 0);
}

const NativeApi g_api{
    sizeof(NativeApi),
    SE_RTHWACCEL_API_VERSION,
    &CreateHook,
    &ApplyQueued,
    &RemoveHook,
    &Log,
    &DiagnosticsEnabled,
    &RtInitialize,
    &RtInstallGate,
    &RtSetLightingList,
    &RtBindLighting,
    &RtSunPassRecorded,
    &RtSetGateMode,
    &RtShutdown,
    &RtSetFrame,
    &RtSetDebugView,
    &RtMeshCreate,
    &RtMeshRelease,
    &RtInstancingUpdate,
    &RtInstancingRelease,
    &RtSceneSubmit,
    &RtMaterialCreate,
    &RtSetProbes,
    &RtMeshVariant,
    &RtVoxelMaterials,
    &RtSetEnvironment,
    &RtLightsSubmit,
    &RtSkinsSubmit,
    &RtDecalsSubmit,
    &RtSetDecalLights,
};

} // namespace

bool InitializeHooking() {
    const MH_STATUS status = MH_Initialize();
    return status == MH_OK || status == MH_ERROR_ALREADY_INITIALIZED;
}

const NativeApi* GetNativeApi() {
    return &g_api;
}

} // namespace sert
