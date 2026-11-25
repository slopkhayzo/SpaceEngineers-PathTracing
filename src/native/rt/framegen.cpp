#include "framegen.h"

#include "../logger.h"

#if SERT_STREAMLINE
#pragma warning(push, 0)
#include <sl.h>
#include <sl_dlss_g.h>
#include <sl_pcl.h>
#include <sl_reflex.h>
// Not inline: included here only.
#include <sl_security.h>
#pragma warning(pop)
#endif

#include <algorithm>
#include <atomic>
#include <cmath>
#include <iterator>
#include <string>
#include <string_view>

namespace sert::rt {

#if SERT_STREAMLINE

namespace {

// The NGX project id (as DLSS Ray Reconstruction's, dlss.cpp).
constexpr char kProjectId[] = "5f0c2a0e-8d64-4c7e-9a2b-6b1e3f7d9c41";
// Streamline's lines logged: its first informational ones, its first warnings and errors, then every 1000th of
// those (it can repeat one every frame).
constexpr int kInfoLines = 200;
constexpr int kProblemLines = 100;
constexpr int kProblemEvery = 1000;
// DLSS-G's own lines (sl.dlss_g, its present path), verbose: their own budget, then every 500th.
constexpr int kFrameGenLines = 400;
constexpr int kFrameGenEvery = 500;

struct Api {
    HMODULE module = nullptr;
    PFun_slInit* init{};
    PFun_slSetD3DDevice* setDevice{};
    PFun_slUpgradeInterface* upgrade{};
    PFun_slGetNativeInterface* native{};
    PFun_slIsFeatureSupported* isSupported{};
    PFun_slGetFeatureFunction* getFunction{};
    PFun_slGetNewFrameToken* newFrame{};
    PFun_slSetConstants* setConstants{};
    PFun_slSetTagForFrame* setTag{};
    PFun_slDLSSGSetOptions* dlssgSetOptions{};
    PFun_slDLSSGGetState* dlssgGetState{};
    PFun_slReflexSetOptions* reflexSetOptions{};
    PFun_slReflexSleep* reflexSleep{};
    PFun_slPCLSetMarker* pclSetMarker{};
};

struct State {
    Api api;
    bool tried = false;
    bool loaded = false;
    bool supported = false;            // DLSS-G on this adapter
    std::atomic<bool> wanted{false};   // FrameGenSetEnabled
    std::atomic<float> frameLimit{0.0f};  // FrameGenSetFrameLimit (frames per second; 0: none)
    std::atomic<int> reflexMode{1};       // FrameGenSetReflexMode
    double sleepMsSum = 0.0;              // Reflex's sleep per frame (this window)
    double sleepMsMax = 0.0;
    UINT64 sleeps = 0;
    bool applied = false;              // the mode last set
    bool appliedOnce = false;
    std::wstring unavailable;          // why frame generation cannot run (empty: it can, or not tried yet)
    UINT presentsWithoutInputs = 1000; // DLSS-G on only while traced frames come (not in loading screens, menus)
    bool rtssWarned = false;
    sl::FrameToken* frame = nullptr;   // the frame the game is on (from just after the previous present)
    UINT64 frameNumber = 0;            // counts the tokens taken
    UINT64 tracedFor = 0;              // the frameNumber whose traced inputs were set (0: none)
    UINT64 presents = 0;               // presents of the proxy (this window)
    UINT64 inputs = 0;                 // frames with the camera, depth and motion set
    UINT64 uiInputs = 0;               // of those: with the HUD-less frame and the UI layer too
    UINT64 inputFailures = 0;          // slSetConstants or slSetTagForFrame refused
    std::atomic<int> infoLines{0};
    std::atomic<int> problemLines{0};
    std::atomic<int> frameGenLines{0};
};

State s;

std::wstring Widen(const char* text) {
    if (text == nullptr) {
        return {};
    }
    const int length = MultiByteToWideChar(CP_UTF8, 0, text, -1, nullptr, 0);
    std::wstring wide(length > 0 ? static_cast<std::size_t>(length) : 0, L'\0');
    if (length > 0) {
        MultiByteToWideChar(CP_UTF8, 0, text, -1, wide.data(), length);
        wide.resize(static_cast<std::size_t>(length) - 1);
    }
    while (!wide.empty() && (wide.back() == L'\n' || wide.back() == L'\r')) {
        wide.pop_back();
    }
    return wide;
}

void OnLog(const sl::LogType type, const char* message) {
    if (type == sl::LogType::eInfo) {
        const std::string_view text = message != nullptr ? message : "";
        if (text.find("dlfg") != std::string_view::npos || text.find("dlssg") != std::string_view::npos ||
            text.find("dlss_g") != std::string_view::npos || text.find("DLSSG") != std::string_view::npos ||
            text.find("FrameGeneration") != std::string_view::npos || text.find("pacer") != std::string_view::npos) {
            const int line = s.frameGenLines.fetch_add(1);
            if (line < kFrameGenLines || line % kFrameGenEvery == 0) {
                Info(Format(L"Streamline (DLSS-G line %d): ", line + 1) + Widen(message));
            }
            return;
        }
        if (s.infoLines.fetch_add(1) < kInfoLines) {
            Info(L"Streamline: " + Widen(message));
        }
        return;
    }
    const int line = s.problemLines.fetch_add(1);
    if (line >= kProblemLines && line % kProblemEvery != 0) {
        return;
    }
    const std::wstring text = (line >= kProblemLines ? Format(L"Streamline (problem line %d): ", line + 1)
                                                     : std::wstring(L"Streamline: ")) +
                              Widen(message);
    if (type == sl::LogType::eError) {
        Error(text);
    } else {
        Info(L"(warning) " + text);
    }
}

template <typename T>
bool Export(const char* name, T*& function) {
    function = reinterpret_cast<T*>(GetProcAddress(s.api.module, name));
    if (function == nullptr) {
        s.unavailable = Format(L"Streamline's sl.interposer.dll has no %hs (an unexpected version)", name);
        Error(L"frame generation: " + s.unavailable + L"; unavailable.");
        return false;
    }
    return true;
}

template <typename T>
bool FeatureFunction(const sl::Feature feature, const char* name, T*& function) {
    void* address = nullptr;
    const sl::Result result = s.api.getFunction(feature, name, address);
    function = reinterpret_cast<T*>(address);
    if (result != sl::Result::eOk || function == nullptr) {
        s.unavailable = Format(L"Streamline has no %hs (result %d)", name, static_cast<int>(result));
        Error(L"frame generation: " + s.unavailable + L"; unavailable.");
        return false;
    }
    return true;
}

// Why DLSS-G is not supported, from slIsFeatureSupported's result.
std::wstring UnsupportedReason(const sl::Result result) {
    switch (result) {
    case sl::Result::eErrorOSDisabledHWS:
        return L"hardware-accelerated GPU scheduling is off (Windows Settings, System, Display, Graphics); DLSS Frame "
               L"Generation needs it";
    case sl::Result::eErrorNoSupportedAdapterFound:
    case sl::Result::eErrorAdapterNotSupported:
        return L"DLSS Frame Generation needs an NVIDIA GeForce RTX 40 or 50 series GPU";
    case sl::Result::eErrorDriverOutOfDate:
        return L"the NVIDIA driver is too old for DLSS Frame Generation";
    case sl::Result::eErrorOSOutOfDate:
        return L"Windows is too old for DLSS Frame Generation (Windows 10 20H1 or later)";
    default:
        return Format(L"DLSS Frame Generation is not supported here (Streamline result %d)", static_cast<int>(result));
    }
}

std::wstring PluginFolder() {
    HMODULE self = nullptr;
    wchar_t path[MAX_PATH]{};
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&PluginFolder), &self);
    GetModuleFileNameW(self, path, MAX_PATH);
    std::wstring folder = path;
    const std::size_t slash = folder.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring() : folder.substr(0, slash);
}

// c = a * b (row-major 4x4).
void Multiply(const float a[16], const float b[16], float c[16]) {
    for (int row = 0; row < 4; ++row) {
        for (int column = 0; column < 4; ++column) {
            float sum = 0.0f;
            for (int k = 0; k < 4; ++k) {
                sum += a[row * 4 + k] * b[k * 4 + column];
            }
            c[row * 4 + column] = sum;
        }
    }
}

// The translation by `d` for row vectors.
void Translation(const double d[3], const double sign, float m[16]) {
    for (int i = 0; i < 16; ++i) {
        m[i] = i % 5 == 0 ? 1.0f : 0.0f;
    }
    m[12] = static_cast<float>(sign * d[0]);
    m[13] = static_cast<float>(sign * d[1]);
    m[14] = static_cast<float>(sign * d[2]);
}

void ToMatrix(const float m[16], sl::float4x4& out) {
    for (int row = 0; row < 4; ++row) {
        out.setRow(static_cast<uint32_t>(row),
                   sl::float4(m[row * 4], m[row * 4 + 1], m[row * 4 + 2], m[row * 4 + 3]));
    }
}

// A clip-space position of one frame into the other's: back to the camera-relative world of the first, moved by
// the cameras' offset, through the other's view and projection.
void ClipToClip(const float invViewProjection[16], const double delta[3], const double sign,
                const float viewProjection[16], sl::float4x4& out) {
    float move[16];
    float world[16];
    float result[16];
    Translation(delta, sign, move);
    Multiply(invViewProjection, move, world);
    Multiply(world, viewProjection, result);
    ToMatrix(result, out);
}

// Tags for the current frame, copied by Streamline on `list` now (eOnlyValidNow).
bool Tag(ID3D12GraphicsCommandList* list, const sl::ResourceTag* tags, const uint32_t count) {
    const sl::Result result = s.api.setTag(*s.frame, sl::ViewportHandle(0u), tags, count,
                                           reinterpret_cast<sl::CommandBuffer*>(list));
    if (result != sl::Result::eOk) {
        ++s.inputFailures;
        if (s.inputFailures <= 5) {
            Error(Format(L"frame generation: slSetTagForFrame failed (result %d).", static_cast<int>(result)));
        }
        return false;
    }
    return true;
}

// Reflex: low latency, with the frame limit if any.
void ApplyReflex() {
    sl::ReflexOptions reflex{};
    const int mode = s.reflexMode.load();
    reflex.mode = mode == 0   ? sl::ReflexMode::eOff
                  : mode == 2 ? sl::ReflexMode::eLowLatencyWithBoost
                              : sl::ReflexMode::eLowLatency;
    const float limit = s.frameLimit.load();
    reflex.frameLimitUs = limit > 0.0f ? static_cast<uint32_t>(1.0e6f / limit) : 0;
    const sl::Result result = s.api.reflexSetOptions(reflex);
    if (result != sl::Result::eOk) {
        Error(Format(L"frame generation: slReflexSetOptions failed (result %d).", static_cast<int>(result)));
        return;
    }
    if (limit > 0.0f) {
        Info(Format(L"frame generation: Reflex limits the game's frames to %.1f per second.", limit));
    }
    if (mode != 1) {
        Info(Format(L"frame generation: Reflex %s.", mode == 0 ? L"off" : L"low latency with boost"));
    }
}

// Traced frames stop coming for this many presents (loading screens, the main menu, the game's own lighting):
// DLSS-G off until they come again (its resources kept).
constexpr UINT kIdlePresents = 30;

void ApplyMode() {
    const bool on = s.wanted.load() && s.supported && s.presentsWithoutInputs < kIdlePresents;
    if (s.appliedOnce && on == s.applied) {
        return;
    }
    sl::DLSSGOptions options{};
    options.mode = on ? sl::DLSSGMode::eOn : sl::DLSSGMode::eOff;
    options.numFramesToGenerate = 1;
    // Full-screen menus over the traced frame (the UI layer covers the screen): DLSS-G pauses itself.
    options.flags = sl::DLSSGFlags::eRetainResourcesWhenOff | sl::DLSSGFlags::eEnableFullscreenMenuDetection;
    const sl::Result result = s.api.dlssgSetOptions(sl::ViewportHandle(0u), options);
    if (result != sl::Result::eOk) {
        Error(Format(L"frame generation: slDLSSGSetOptions(%s) failed (result %d).", on ? L"on" : L"off",
                     static_cast<int>(result)));
    }
    s.applied = on;
    s.appliedOnce = true;
    Info(Format(L"frame generation: DLSS-G %s.",
                on                  ? L"on (one generated frame between each two)"
                : s.wanted.load()   ? L"paused (no traced frames: a loading screen, a menu or the game's own lighting)"
                                    : L"off"));
    if (on && !s.rtssWarned && GetModuleHandleW(L"RTSSHooks64.dll") != nullptr) {
        s.rtssWarned = true;
        Info(L"frame generation: RivaTuner Statistics Server is hooking the game. Its on-screen display, drawn into "
             L"every frame DLSS-G presents, slows frame generation down a lot (to a quarter of the frame rate in a "
             L"test); set RivaTuner's application detection level to None for SpaceEngineers.exe while using frame "
             L"generation.");
    }
}

} // namespace

bool FrameGenLoad(ID3D12Device* device) {
    if (s.tried) {
        return s.loaded;
    }
    s.tried = true;
    const std::wstring folder = PluginFolder();
    const std::wstring interposer = folder + L"\\sl.interposer.dll";
    if (GetFileAttributesW(interposer.c_str()) == INVALID_FILE_ATTRIBUTES) {
        s.unavailable = L"NVIDIA Streamline's files are not in the plugin's folder (sl.interposer.dll and the rest)";
        Info(L"frame generation: " + s.unavailable + L"; unavailable.");
        return false;
    }
    if (!sl::security::verifyEmbeddedSignature(interposer.c_str())) {
        s.unavailable = L"sl.interposer.dll is not signed by NVIDIA";
        Error(L"frame generation: " + s.unavailable + L"; unavailable.");
        return false;
    }
    s.api.module = LoadLibraryExW(interposer.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (s.api.module == nullptr) {
        s.unavailable = Format(L"sl.interposer.dll could not be loaded (error %lu)", GetLastError());
        Error(L"frame generation: " + s.unavailable + L"; unavailable.");
        return false;
    }
    if (!Export("slInit", s.api.init) || !Export("slSetD3DDevice", s.api.setDevice) ||
        !Export("slUpgradeInterface", s.api.upgrade) || !Export("slGetNativeInterface", s.api.native) ||
        !Export("slIsFeatureSupported", s.api.isSupported) || !Export("slGetFeatureFunction", s.api.getFunction) ||
        !Export("slGetNewFrameToken", s.api.newFrame) || !Export("slSetConstants", s.api.setConstants) ||
        !Export("slSetTagForFrame", s.api.setTag)) {
        return false;
    }
    const wchar_t* paths[] = {folder.c_str()};
    // With diagnostics on: Streamline's own log, verbose, in %TEMP%\SpaceEngineers.RTHWAccel\streamline (the
    // session script copies it); its problems reach ours either way (OnLog).
    const bool diagnostics = log::DiagnosticsEnabled();
    wchar_t temp[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temp);
    std::wstring logs = std::wstring(temp) + L"SpaceEngineers.RTHWAccel";
    CreateDirectoryW(logs.c_str(), nullptr);
    logs += L"\\streamline";
    CreateDirectoryW(logs.c_str(), nullptr);
    const sl::Feature features[] = {sl::kFeatureDLSS_G, sl::kFeatureReflex, sl::kFeaturePCL};
    sl::Preferences preferences{};
    preferences.logLevel = diagnostics ? sl::LogLevel::eVerbose : sl::LogLevel::eDefault;
    preferences.pathsToPlugins = paths;
    preferences.numPathsToPlugins = 1;
    preferences.pathToLogsAndData = diagnostics ? logs.c_str() : nullptr;
    preferences.logMessageCallback = &OnLog;
    // No downloads: the pinned, signed plugins only.
    preferences.flags = sl::PreferenceFlags::eDisableCLStateTracking | sl::PreferenceFlags::eUseManualHooking |
                        sl::PreferenceFlags::eUseFrameBasedResourceTagging;
    preferences.featuresToLoad = features;
    preferences.numFeaturesToLoad = static_cast<uint32_t>(std::size(features));
    preferences.engine = sl::EngineType::eCustom;
    preferences.engineVersion = "SpaceEngineers.RTHWAccel";
    preferences.projectId = kProjectId;
    preferences.renderAPI = sl::RenderAPI::eD3D12;
    sl::Result result = s.api.init(preferences, sl::kSDKVersion);
    if (result != sl::Result::eOk) {
        s.unavailable = Format(L"NVIDIA Streamline could not start (slInit result %d)", static_cast<int>(result));
        Error(L"frame generation: " + s.unavailable + L"; unavailable.");
        return false;
    }
    result = s.api.setDevice(device);
    if (result != sl::Result::eOk) {
        s.unavailable = Format(L"NVIDIA Streamline refused the device (slSetD3DDevice result %d)",
                               static_cast<int>(result));
        Error(L"frame generation: " + s.unavailable + L"; unavailable.");
        return false;
    }
    if (!FeatureFunction(sl::kFeatureReflex, "slReflexSetOptions", s.api.reflexSetOptions) ||
        !FeatureFunction(sl::kFeatureReflex, "slReflexSleep", s.api.reflexSleep) ||
        !FeatureFunction(sl::kFeaturePCL, "slPCLSetMarker", s.api.pclSetMarker)) {
        return false;
    }
    ApplyReflex();
    LUID luid = device->GetAdapterLuid();
    sl::AdapterInfo adapter{};
    adapter.deviceLUID = reinterpret_cast<uint8_t*>(&luid);
    adapter.deviceLUIDSizeInBytes = sizeof(luid);
    result = s.api.isSupported(sl::kFeatureDLSS_G, adapter);
    s.supported = result == sl::Result::eOk &&
                  FeatureFunction(sl::kFeatureDLSS_G, "slDLSSGSetOptions", s.api.dlssgSetOptions) &&
                  FeatureFunction(sl::kFeatureDLSS_G, "slDLSSGGetState", s.api.dlssgGetState);
    if (result != sl::Result::eOk) {
        s.unavailable = UnsupportedReason(result);
    }
    s.loaded = true;
    Info(Format(L"frame generation: Streamline loaded from %s (manual hooking, Reflex low latency); DLSS-G %s.",
                folder.c_str(),
                s.supported ? L"supported on this adapter"
                            : Format(L"not supported on this adapter (result %d)", static_cast<int>(result)).c_str()));
    return true;
}

bool FrameGenLoaded() {
    return s.loaded;
}

bool FrameGenUpgrade(void** interfacePointer) {
    if (!s.loaded || interfacePointer == nullptr || *interfacePointer == nullptr) {
        return false;
    }
    const sl::Result result = s.api.upgrade(interfacePointer);
    if (result != sl::Result::eOk) {
        Error(Format(L"frame generation: slUpgradeInterface failed (result %d).", static_cast<int>(result)));
        return false;
    }
    return true;
}

IDXGISwapChain* FrameGenNative(IDXGISwapChain* proxy) {
    if (!s.loaded || proxy == nullptr) {
        return nullptr;
    }
    void* native = nullptr;
    return s.api.native(proxy, &native) == sl::Result::eOk ? static_cast<IDXGISwapChain*>(native) : nullptr;
}

bool FrameGenEnabled() {
    return s.loaded && s.supported && s.wanted.load();
}

int FrameGenStatus(std::wstring& text) {
    text.clear();
    if (!s.tried) {
        return 1;
    }
    if (!s.loaded || !s.supported) {
        text = s.unavailable.empty() ? L"DLSS Frame Generation is unavailable (see the log)" : s.unavailable;
        return 2;
    }
    if (GetModuleHandleW(L"RTSSHooks64.dll") != nullptr) {
        text = L"RivaTuner Statistics Server is hooking the game: its on-screen display costs frame generation most "
               L"of its frame rate (set its application detection level to None for SpaceEngineers.exe)";
    }
    return 0;
}

// The next frame's token as the game starts it (after the previous present): Reflex's pacing sleep, then its
// simulation (the game's whole frame up to its present, unmarked within).
void StartFrame() {
    if (s.api.newFrame(s.frame, nullptr) != sl::Result::eOk) {
        s.frame = nullptr;
        return;
    }
    ++s.frameNumber;
    LARGE_INTEGER before{};
    LARGE_INTEGER after{};
    LARGE_INTEGER frequency{};
    QueryPerformanceCounter(&before);
    s.api.reflexSleep(*s.frame);
    QueryPerformanceCounter(&after);
    QueryPerformanceFrequency(&frequency);
    const double ms = static_cast<double>(after.QuadPart - before.QuadPart) * 1000.0 /
                      static_cast<double>(frequency.QuadPart);
    s.sleepMsSum += ms;
    s.sleepMsMax = std::max(s.sleepMsMax, ms);
    ++s.sleeps;
    s.api.pclSetMarker(sl::PCLMarker::eSimulationStart, *s.frame);
}

void FrameGenBeginFrame() {
    if (!s.loaded) {
        return;
    }
    // This frame's traced inputs were set mid-frame (FrameGenSetTracedInputs), before this present.
    const bool inputs = s.frame != nullptr && s.tracedFor == s.frameNumber;
    s.presentsWithoutInputs = inputs ? 0 : std::min(s.presentsWithoutInputs + 1, 1000u);
    if (s.supported) {
        ApplyMode();
    }
    if (s.frame == nullptr) {
        StartFrame();  // the first frame through ours
    }
    if (s.frame != nullptr) {
        s.api.pclSetMarker(sl::PCLMarker::eSimulationEnd, *s.frame);
        s.api.pclSetMarker(sl::PCLMarker::eRenderSubmitStart, *s.frame);
    }
}

bool FrameGenSetTracedInputs(ID3D12GraphicsCommandList* list, const FrameGenFrame& frame) {
    if (!s.loaded || !s.supported || s.frame == nullptr || list == nullptr || frame.hardwareDepth == nullptr ||
        frame.motion == nullptr || frame.width == 0 || frame.height == 0 || s.tracedFor == s.frameNumber) {
        return false;  // (constants once per frame)
    }
    // The game's projection is reverse-Z (rt_core.cpp FillPrimaryConstants): depth = M43 / z - M33, 1 at the near
    // plane, 0 at the far one (none when M33 is 0).
    const float m33 = frame.projection[10];
    const float m43 = frame.projection[14];
    sl::Constants constants{};
    ToMatrix(frame.projection, constants.cameraViewToClip);
    ToMatrix(frame.invProjection, constants.clipToCameraView);
    float identity[16]{};
    for (int i = 0; i < 16; i += 5) {
        identity[i] = 1.0f;
    }
    ToMatrix(identity, constants.clipToLensClip);
    ClipToClip(frame.invViewProjection, frame.cameraDelta, 1.0, frame.previousViewProjection,
               constants.clipToPrevClip);
    ClipToClip(frame.previousInvViewProjection, frame.cameraDelta, -1.0, frame.viewProjection,
               constants.prevClipToClip);
    // The presented frame is the game's (not jittered); the motion is in render pixels, the camera's included.
    constants.jitterOffset = sl::float2(0.0f, 0.0f);
    constants.mvecScale = sl::float2(1.0f / static_cast<float>(frame.width), 1.0f / static_cast<float>(frame.height));
    constants.cameraPinholeOffset = sl::float2(0.0f, 0.0f);
    constants.cameraPos = sl::float3(0.0f, 0.0f, 0.0f);  // camera-relative
    constants.cameraRight = sl::float3(frame.invView[0], frame.invView[1], frame.invView[2]);
    constants.cameraUp = sl::float3(frame.invView[4], frame.invView[5], frame.invView[6]);
    constants.cameraFwd = sl::float3(-frame.invView[8], -frame.invView[9], -frame.invView[10]);
    constants.cameraNear = m43 / (1.0f + m33);
    constants.cameraFar = m33 > 1.0e-9f ? m43 / m33 : 1.0e6f;
    constants.cameraFOV = 2.0f * std::atan(1.0f / frame.projection[5]);
    constants.cameraAspectRatio = frame.projection[5] / frame.projection[0];
    constants.depthInverted = sl::Boolean::eTrue;
    constants.cameraMotionIncluded = sl::Boolean::eTrue;
    constants.motionVectors3D = sl::Boolean::eFalse;
    constants.reset = frame.reset ? sl::Boolean::eTrue : sl::Boolean::eFalse;
    constants.orthographicProjection = sl::Boolean::eFalse;
    constants.motionVectorsDilated = sl::Boolean::eFalse;
    constants.motionVectorsJittered = sl::Boolean::eFalse;
    const sl::ViewportHandle viewport(0u);
    sl::Result result = s.api.setConstants(constants, *s.frame, viewport);
    if (result != sl::Result::eOk) {
        ++s.inputFailures;
        return false;
    }
    // Copied by Streamline now (on the core's list): the next traced frame rewrites them.
    sl::Resource depthResource(sl::ResourceType::eTex2d, frame.hardwareDepth, nullptr, nullptr,
                               static_cast<uint32_t>(D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
    sl::Resource motionResource(sl::ResourceType::eTex2d, frame.motion, nullptr, nullptr,
                                static_cast<uint32_t>(D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
    const sl::Extent renderExtent{0, 0, frame.width, frame.height};
    const sl::ResourceTag tags[] = {
        sl::ResourceTag(&depthResource, sl::kBufferTypeDepth, sl::ResourceLifecycle::eOnlyValidNow, &renderExtent),
        sl::ResourceTag(&motionResource, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eOnlyValidNow,
                        &renderExtent),
    };
    if (!Tag(list, tags, static_cast<uint32_t>(std::size(tags)))) {
        return false;
    }
    s.tracedFor = s.frameNumber;
    ++s.inputs;
    return true;
}

bool FrameGenFrameHasInputs() {
    return s.loaded && s.frame != nullptr && s.tracedFor == s.frameNumber;
}

bool FrameGenSetUiInputs(ID3D12GraphicsCommandList* list, ID3D12Resource* hudless, ID3D12Resource* ui,
                         const UINT outputWidth, const UINT outputHeight) {
    if (!FrameGenFrameHasInputs() || list == nullptr || hudless == nullptr || ui == nullptr) {
        return false;
    }
    sl::Resource hudlessResource(sl::ResourceType::eTex2d, hudless, nullptr, nullptr,
                                 static_cast<uint32_t>(D3D12_RESOURCE_STATE_COMMON));
    sl::Resource uiResource(sl::ResourceType::eTex2d, ui, nullptr, nullptr,
                            static_cast<uint32_t>(D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
    const sl::Extent outputExtent{0, 0, outputWidth, outputHeight};
    const sl::ResourceTag tags[] = {
        sl::ResourceTag(&hudlessResource, sl::kBufferTypeHUDLessColor, sl::ResourceLifecycle::eOnlyValidNow,
                        &outputExtent),
        sl::ResourceTag(&uiResource, sl::kBufferTypeUIColorAndAlpha, sl::ResourceLifecycle::eOnlyValidNow,
                        &outputExtent),
    };
    if (!Tag(list, tags, static_cast<uint32_t>(std::size(tags)))) {
        return false;
    }
    ++s.uiInputs;
    return true;
}

void FrameGenBeforePresent() {
    if (!s.loaded || s.frame == nullptr) {
        return;
    }
    s.api.pclSetMarker(sl::PCLMarker::eRenderSubmitEnd, *s.frame);
    s.api.pclSetMarker(sl::PCLMarker::ePresentStart, *s.frame);
}

void FrameGenAfterPresent() {
    if (!s.loaded) {
        return;
    }
    if (s.frame != nullptr) {
        s.api.pclSetMarker(sl::PCLMarker::ePresentEnd, *s.frame);
        ++s.presents;
    }
    StartFrame();
}

void FrameGenSetEnabled(const bool on) {
    s.wanted.store(on);
}

void FrameGenSetReflexMode(const int mode) {
    s.reflexMode.store(mode);
    if (s.loaded) {
        ApplyReflex();
    }
}

void FrameGenSetFrameLimit(const float framesPerSecond) {
    s.frameLimit.store(framesPerSecond);
    if (s.loaded) {
        ApplyReflex();
    }
}

void FrameGenReport(const double seconds) {
    if (!s.loaded) {
        return;
    }
    std::wstring state = L"not supported";
    if (s.supported) {
        sl::DLSSGState dlssg{};
        const sl::Result result = s.api.dlssgGetState(sl::ViewportHandle(0u), dlssg, nullptr);
        state = result != sl::Result::eOk
                    ? Format(L"state unknown (result %d)", static_cast<int>(result))
                    : Format(L"status %u, %u frames presented per game frame lately, up to %u generated per frame, "
                             L"minimum size %u, VSync %s, about %llu MB",
                             static_cast<unsigned>(dlssg.status), dlssg.numFramesActuallyPresented,
                             dlssg.numFramesToGenerateMax, dlssg.minWidthOrHeight,
                             dlssg.bIsVsyncSupportAvailable == sl::Boolean::eTrue ? L"supported" : L"not supported",
                             static_cast<unsigned long long>(dlssg.estimatedVRAMUsageInBytes >> 20));
    }
    Info(Format(L"frame generation over %.1f s: %llu presents through Streamline; %llu frames with the camera, depth "
                L"and motion (%llu with the HUD-less frame and UI layer too; %llu refused); Reflex slept %.2f ms avg / "
                L"%.1f max per frame; DLSS-G %s (%s).",
                seconds, static_cast<unsigned long long>(s.presents), static_cast<unsigned long long>(s.inputs),
                static_cast<unsigned long long>(s.uiInputs), static_cast<unsigned long long>(s.inputFailures),
                s.sleeps != 0 ? s.sleepMsSum / static_cast<double>(s.sleeps) : 0.0, s.sleepMsMax,
                s.applied ? L"on" : L"off", state.c_str()));
    s.sleepMsSum = 0.0;
    s.sleepMsMax = 0.0;
    s.sleeps = 0;
    s.presents = 0;
    s.inputs = 0;
    s.uiInputs = 0;
    s.inputFailures = 0;
}

#else  // SERT_STREAMLINE

bool FrameGenLoad(ID3D12Device*) {
    Info(L"frame generation: built without Streamline (scripts/fetch_streamline.ps1); unavailable.");
    return false;
}
bool FrameGenLoaded() {
    return false;
}
bool FrameGenEnabled() {
    return false;
}
int FrameGenStatus(std::wstring& text) {
    text = L"this build of the plugin has no NVIDIA Streamline (scripts/fetch_streamline.ps1)";
    return 2;
}
void FrameGenBeginFrame() {}
bool FrameGenSetTracedInputs(ID3D12GraphicsCommandList*, const FrameGenFrame&) {
    return false;
}
bool FrameGenFrameHasInputs() {
    return false;
}
bool FrameGenSetUiInputs(ID3D12GraphicsCommandList*, ID3D12Resource*, ID3D12Resource*, UINT, UINT) {
    return false;
}
bool FrameGenUpgrade(void**) {
    return false;
}
IDXGISwapChain* FrameGenNative(IDXGISwapChain*) {
    return nullptr;
}
void FrameGenBeforePresent() {}
void FrameGenAfterPresent() {}
void FrameGenSetEnabled(bool) {}
void FrameGenSetFrameLimit(float) {}
void FrameGenSetReflexMode(int) {}
void FrameGenReport(double) {}

#endif  // SERT_STREAMLINE

} // namespace sert::rt
