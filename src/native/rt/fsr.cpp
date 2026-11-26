#include "fsr.h"

#include "../logger.h"
#include "common.h"

#if defined(SERT_FSR)
#pragma warning(push, 0)
#include <ffx_api.h>
#include <ffx_api_types.h>
#include <dx12/ffx_api_dx12.h>
#include <dx12/ffx_api_framegeneration_dx12.h>
#include <ffx_framegeneration.h>
#include <ffx_upscale.h>
#pragma warning(pop)
#include <softpub.h>
#include <wincrypt.h>
#include <wintrust.h>
#endif

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cwctype>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

namespace sert::rt::fsr {

const wchar_t* QualityName(const int quality) {
    switch (quality) {
    case 1:
        return L"quality";
    case 2:
        return L"balanced";
    case 3:
        return L"performance";
    case 4:
        return L"ultra performance";
    default:
        return L"native AA";
    }
}

#if defined(SERT_FSR)

namespace {

// AMD's loader and the effect DLLs this plugin uses, from its own folder (the loader takes a module of an effect's
// name already loaded: these, checked).
constexpr const wchar_t* kLoader = L"amd_fidelityfx_loader_dx12.dll";
constexpr const wchar_t* kEffects[] = {L"amd_fidelityfx_upscaler_dx12.dll", L"amd_fidelityfx_framegeneration_dx12.dll"};
// FSR's messages logged: each distinct one once (its debug checking repeats them every frame), up to this many.
constexpr std::size_t kDistinctMessages = 64;

struct Api {
    PfnFfxCreateContext createContext{};
    PfnFfxDestroyContext destroyContext{};
    PfnFfxConfigure configure{};
    PfnFfxQuery query{};
    PfnFfxDispatch dispatch{};
};

struct Version {
    std::uint64_t id = 0;
    std::wstring name;
};

struct State {
    std::mutex initializeLock;  // Initialize, and Status's reads of what it writes
    std::mutex versionLock;     // contextVersion: written by Prepare, read by Status from any thread
    bool tried = false;
    std::atomic<bool> available{false};
    bool diagnostics = false;
    Api api;
    ID3D12Device* device = nullptr;
    std::wstring unavailable;              // why FSR cannot run (empty: it can, or not tried yet)
    std::vector<Version> upscalers;        // offered on this device, as the loader lists them
    std::vector<Version> frameGenerators;
    bool pinned = false;                   // [RayTracing] FsrVersion picked one of the upscalers
    std::uint64_t pinnedId = 0;
    std::wstring pinnedName;
    // The prepared context and its sizes.
    ffxContext context = nullptr;
    unsigned width = 0;
    unsigned height = 0;
    unsigned targetWidth = 0;
    unsigned targetHeight = 0;
    int quality = 0;
    int phaseCount = 8;
    std::wstring contextVersion;
    bool preparedFailed = false;           // the last Prepare failed (logged once until one succeeds)
    unsigned failedWidth = 0;              // the sizes it failed at: not tried again until they change
    unsigned failedHeight = 0;
    unsigned failedTargetWidth = 0;
    unsigned failedTargetHeight = 0;
    bool loggedDispatchFailure = false;
    std::vector<std::pair<ffxContext, unsigned long long>> retired;
    // RenderSize's last answer.
    unsigned sizeTargetWidth = 0;
    unsigned sizeTargetHeight = 0;
    int sizeQuality = -1;
    unsigned sizeWidth = 0;
    unsigned sizeHeight = 0;
    std::mutex messageLock;                 // FSR may call OnMessage from its own threads
    std::vector<std::wstring> messagesSeen;
    // Frame generation: the swap chain's context (its chain held by the presenter) and the generation context.
    ffxContext swapChainContext = nullptr;
    ffxContext frameGenContext = nullptr;
    IDXGISwapChain4* chain = nullptr;       // not owned
    unsigned fgWidth = 0;
    unsigned fgHeight = 0;
    DXGI_FORMAT fgFormat = DXGI_FORMAT_UNKNOWN;
    std::wstring frameGenVersion;
    bool fgPrepareFailureLogged = false;
    bool fgConfigureFailureLogged = false;
    bool originSet = false;                 // cameraPosition's origin (floats keep their precision near it)
    double origin[3]{};
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
    return wide;
}

void OnMessage(const uint32_t type, const wchar_t* message) {
    if (message == nullptr) {
        return;
    }
    std::wstring text = message;
    while (!text.empty() && (text.back() == L'\n' || text.back() == L'\r')) {
        text.pop_back();
    }
    {
        std::lock_guard<std::mutex> lock(s.messageLock);
        if (s.messagesSeen.size() >= kDistinctMessages ||
            std::find(s.messagesSeen.begin(), s.messagesSeen.end(), text) != s.messagesSeen.end()) {
            return;
        }
        s.messagesSeen.push_back(text);
    }
    if (type == FFX_API_MESSAGE_TYPE_ERROR) {
        Error(L"FSR: " + text + L" (logged once)");
    } else {
        Info(L"(warning) FSR: " + text + L" (logged once)");
    }
}

// Whether `path` carries a valid Authenticode signature of AMD's (`signer` gets the signer's name). No network:
// revocation is not checked.
bool SignedByAmd(const std::wstring& path, std::wstring& signer) {
    WINTRUST_FILE_INFO file{};
    file.cbStruct = sizeof(file);
    file.pcwszFilePath = path.c_str();
    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    WINTRUST_DATA data{};
    data.cbStruct = sizeof(data);
    data.dwUIChoice = WTD_UI_NONE;
    data.fdwRevocationChecks = WTD_REVOKE_NONE;
    data.dwUnionChoice = WTD_CHOICE_FILE;
    data.pFile = &file;
    data.dwStateAction = WTD_STATEACTION_VERIFY;
    data.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;
    const LONG status = WinVerifyTrust(static_cast<HWND>(INVALID_HANDLE_VALUE), &action, &data);
    bool amd = false;
    if (status == ERROR_SUCCESS) {
        CRYPT_PROVIDER_DATA* provider = WTHelperProvDataFromStateData(data.hWVTStateData);
        CRYPT_PROVIDER_SGNR* sgnr = provider != nullptr ? WTHelperGetProvSignerFromChain(provider, 0, FALSE, 0) : nullptr;
        CRYPT_PROVIDER_CERT* cert = sgnr != nullptr ? WTHelperGetProvCertFromChain(sgnr, 0) : nullptr;
        if (cert != nullptr && cert->pCert != nullptr) {
            wchar_t name[256]{};
            CertGetNameStringW(cert->pCert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, nullptr, name, 256);
            signer = name;
            amd = signer.find(L"Advanced Micro Devices") != std::wstring::npos;
        }
    } else {
        signer = Format(L"no valid signature (0x%08lX)", static_cast<unsigned long>(status));
    }
    data.dwStateAction = WTD_STATEACTION_CLOSE;
    WinVerifyTrust(static_cast<HWND>(INVALID_HANDLE_VALUE), &action, &data);
    return amd;
}

// The backend description every call without a context needs (it routes the query to the device's providers,
// the driver's included), with the pinned version after it.
struct Backend {
    ffxCreateBackendDX12Desc backend{};
    ffxOverrideVersion override{};

    Backend() {
        backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
        backend.device = s.device;
        if (s.pinned) {
            override.header.type = FFX_API_DESC_TYPE_OVERRIDE_VERSION;
            override.versionId = s.pinnedId;
            backend.header.pNext = &override.header;
        }
    }
};

std::vector<Version> Versions(const std::uint64_t createDescType) {
    ffxQueryDescGetVersions query{};
    query.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
    query.createDescType = createDescType;
    query.device = s.device;
    std::uint64_t count = 0;
    query.outputCount = &count;
    if (s.api.query(nullptr, &query.header) != FFX_API_RETURN_OK || count == 0) {
        return {};
    }
    std::vector<std::uint64_t> ids(count);
    std::vector<const char*> names(count);
    query.versionIds = ids.data();
    query.versionNames = names.data();
    if (s.api.query(nullptr, &query.header) != FFX_API_RETURN_OK) {
        return {};
    }
    std::vector<Version> versions;
    for (std::uint64_t i = 0; i < count && i < ids.size(); ++i) {
        versions.push_back({ids[i], Widen(names[i])});  // copied: the names may live in memory a later query reuses
    }
    return versions;
}

std::wstring Names(const std::vector<Version>& versions) {
    std::wstring text;
    for (const Version& version : versions) {
        text += (text.empty() ? L"" : L", ") + version.name;
    }
    return text.empty() ? L"none" : text;
}

std::wstring ReadPin(const std::wstring& iniPath) {
    wchar_t value[64]{};
    GetPrivateProfileStringW(L"RayTracing", L"FsrVersion", L"", value, 64, iniPath.c_str());
    std::wstring text = value;
    const std::size_t comment = text.find(L';');
    if (comment != std::wstring::npos) {
        text.resize(comment);
    }
    while (!text.empty() && std::iswspace(text.back())) {
        text.pop_back();
    }
    while (!text.empty() && std::iswspace(text.front())) {
        text.erase(text.begin());
    }
    return text;
}

void DestroyContext(ffxContext& context) {
    if (context != nullptr) {
        s.api.destroyContext(&context, nullptr);
        context = nullptr;
    }
}

} // namespace

bool Built() {
    return true;
}

bool Initialize(ID3D12Device* device, const std::wstring& directory, const std::wstring& iniPath,
                const bool diagnostics) {
    std::lock_guard<std::mutex> lock(s.initializeLock);
    if (s.tried) {
        return s.available.load();
    }
    s.tried = true;
    s.device = device;
    s.diagnostics = diagnostics;
    LARGE_INTEGER start{};
    LARGE_INTEGER frequency{};
    QueryPerformanceCounter(&start);
    QueryPerformanceFrequency(&frequency);
    // The effect DLLs first (the loader reuses modules of their names already loaded), each checked for AMD's
    // signature before it is loaded.
    std::vector<std::wstring> files;
    for (const wchar_t* effect : kEffects) {
        files.push_back(effect);
    }
    files.push_back(kLoader);
    HMODULE loader = nullptr;
    for (const std::wstring& file : files) {
        const std::wstring path = directory + L"\\" + file;
        if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) {
            s.unavailable = L"AMD FSR's files are not in the plugin's folder (" + file + L" and the rest)";
            Info(L"FSR: " + s.unavailable + L"; unavailable.");
            return false;
        }
        std::wstring signer;
        if (!SignedByAmd(path, signer)) {
            s.unavailable = file + L" is not signed by AMD (" + signer + L")";
            Error(L"FSR: " + s.unavailable + L"; unavailable.");
            return false;
        }
        HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == nullptr) {
            s.unavailable = Format(L"%s could not be loaded (error %lu)", file.c_str(), GetLastError());
            Error(L"FSR: " + s.unavailable + L"; unavailable.");
            return false;
        }
        if (file == kLoader) {
            loader = module;
        }
    }
    s.api.createContext = reinterpret_cast<PfnFfxCreateContext>(GetProcAddress(loader, "ffxCreateContext"));
    s.api.destroyContext = reinterpret_cast<PfnFfxDestroyContext>(GetProcAddress(loader, "ffxDestroyContext"));
    s.api.configure = reinterpret_cast<PfnFfxConfigure>(GetProcAddress(loader, "ffxConfigure"));
    s.api.query = reinterpret_cast<PfnFfxQuery>(GetProcAddress(loader, "ffxQuery"));
    s.api.dispatch = reinterpret_cast<PfnFfxDispatch>(GetProcAddress(loader, "ffxDispatch"));
    if (s.api.createContext == nullptr || s.api.destroyContext == nullptr || s.api.configure == nullptr ||
        s.api.query == nullptr || s.api.dispatch == nullptr) {
        s.unavailable = L"amd_fidelityfx_loader_dx12.dll lacks the FFX API's functions (an unexpected version)";
        Error(L"FSR: " + s.unavailable + L"; unavailable.");
        return false;
    }
    if (diagnostics) {
        // FSR's messages (the effects' own checks, with the contexts' debug flags) in our log.
        ffxConfigureDescGlobalDebug debug{};
        debug.header.type = FFX_API_CONFIGURE_DESC_TYPE_GLOBALDEBUG;
        debug.fpMessage = &OnMessage;
        debug.debugLevel = FFX_API_CONFIGURE_GLOBALDEBUG_LEVEL_WARNINGS;
        for (const std::uint64_t effect : {static_cast<std::uint64_t>(FFX_API_EFFECT_ID_UPSCALE),
                                           static_cast<std::uint64_t>(FFX_API_EFFECT_ID_FRAMEGENERATION)}) {
            debug.effectId = effect;
            s.api.configure(nullptr, &debug.header);
        }
    }
    s.upscalers = Versions(FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE);
    s.frameGenerators = Versions(FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION);
    const std::wstring pin = ReadPin(iniPath);
    if (!pin.empty() && _wcsicmp(pin.c_str(), L"auto") != 0) {
        // The newest listed version whose name starts with "FSR <pin>" (the list's order is the loader's; a
        // driver-provided one is taken only when nothing else matches).
        const std::wstring prefix = L"FSR " + pin;
        const Version* best = nullptr;
        for (const Version& version : s.upscalers) {
            if (version.name.compare(0, prefix.size(), prefix) == 0 &&
                (best == nullptr || (version.name.back() != L'*' && best->name.back() == L'*'))) {
                best = &version;
            }
        }
        if (best != nullptr) {
            s.pinned = true;
            s.pinnedId = best->id;
            s.pinnedName = best->name;
        } else {
            Info(L"FSR: no upscaler version matches FsrVersion = " + pin + L"; the loader picks one.");
        }
    }
    if (s.upscalers.empty()) {
        s.unavailable = L"AMD FSR offers no upscaler on this GPU";
        Info(L"FSR: " + s.unavailable + L"; unavailable.");
        return false;
    }
    s.available.store(true);
    LARGE_INTEGER end{};
    QueryPerformanceCounter(&end);
    const double ms = static_cast<double>(end.QuadPart - start.QuadPart) * 1000.0 /
                      static_cast<double>(frequency.QuadPart);
    Info(L"FSR: AMD's loader and effects loaded from " + directory + L" (signed by AMD; " + Format(L"%.0f ms", ms) +
         L"); upscalers offered: " + Names(s.upscalers) + L"; frame generation: " + Names(s.frameGenerators) +
         (s.pinned ? L"; pinned by FsrVersion: " + s.pinnedName : std::wstring(L"; the loader picks the newest")) +
         L".");
    return true;
}

bool Available() {
    return s.available.load();
}

std::wstring Status() {
    std::unique_lock<std::mutex> lock(s.initializeLock, std::try_to_lock);
    if (!lock.owns_lock()) {
        return L"AMD FSR is loading";
    }
    if (!s.tried) {
        return L"AMD FSR is not loaded yet";
    }
    if (!s.available.load()) {
        return s.unavailable.empty() ? L"AMD FSR is unavailable (see the log)" : s.unavailable;
    }
    std::wstring inUse;
    {
        std::lock_guard<std::mutex> versionLock(s.versionLock);
        inUse = s.contextVersion;
    }
    return L"upscalers " + Names(s.upscalers) + L"; frame generation " + Names(s.frameGenerators) +
           (inUse.empty() ? L"" : L"; in use: " + inUse);
}

void RenderSize(const unsigned targetWidth, const unsigned targetHeight, const int quality, unsigned& width,
                unsigned& height) {
    width = targetWidth;
    height = targetHeight;
    if (!s.available.load() || quality <= 0 || quality > 4 || targetWidth == 0 || targetHeight == 0) {
        return;
    }
    if (s.sizeTargetWidth == targetWidth && s.sizeTargetHeight == targetHeight && s.sizeQuality == quality) {
        width = s.sizeWidth;
        height = s.sizeHeight;
        return;
    }
    std::uint32_t renderWidth = 0;
    std::uint32_t renderHeight = 0;
    ffxQueryDescUpscaleGetRenderResolutionFromQualityMode query{};
    query.header.type = FFX_API_QUERY_DESC_TYPE_UPSCALE_GETRENDERRESOLUTIONFROMQUALITYMODE;
    query.displayWidth = targetWidth;
    query.displayHeight = targetHeight;
    query.qualityMode = static_cast<std::uint32_t>(quality);
    query.pOutRenderWidth = &renderWidth;
    query.pOutRenderHeight = &renderHeight;
    Backend backend;
    query.header.pNext = &backend.backend.header;
    const ffxReturnCode_t result = s.api.query(nullptr, &query.header);
    if (result != FFX_API_RETURN_OK || renderWidth == 0 || renderHeight == 0 || renderWidth > targetWidth ||
        renderHeight > targetHeight) {
        // AMD's published ratios per axis.
        static constexpr double kRatio[5] = {1.0, 1.5, 1.7, 2.0, 3.0};
        renderWidth = std::max(1u, static_cast<unsigned>(std::lround(targetWidth / kRatio[quality])));
        renderHeight = std::max(1u, static_cast<unsigned>(std::lround(targetHeight / kRatio[quality])));
        Info(Format(L"FSR: no render size from the API for %s at %ux%u (result %u); %ux%u by AMD's ratio.",
                    QualityName(quality), targetWidth, targetHeight, static_cast<unsigned>(result), renderWidth,
                    renderHeight));
    }
    s.sizeTargetWidth = targetWidth;
    s.sizeTargetHeight = targetHeight;
    s.sizeQuality = quality;
    s.sizeWidth = renderWidth;
    s.sizeHeight = renderHeight;
    width = renderWidth;
    height = renderHeight;
}

bool Prepare(const unsigned width, const unsigned height, const unsigned targetWidth, const unsigned targetHeight,
             const int quality, const unsigned long long retireFence) {
    if (!s.available.load() || width == 0 || height == 0 || targetWidth < width || targetHeight < height) {
        return false;
    }
    if (s.context != nullptr && s.width == width && s.height == height && s.targetWidth == targetWidth &&
        s.targetHeight == targetHeight) {
        s.quality = quality;
        return true;
    }
    if (s.preparedFailed && s.failedWidth == width && s.failedHeight == height && s.failedTargetWidth == targetWidth &&
        s.failedTargetHeight == targetHeight) {
        return false;  // failed at these sizes already (logged)
    }
    if (s.context != nullptr) {
        s.retired.emplace_back(s.context, retireFence);  // frames still on the GPU may use it
        s.context = nullptr;
        std::lock_guard<std::mutex> versionLock(s.versionLock);
        s.contextVersion.clear();
    }
    ffxCreateContextDescUpscale create{};
    create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    // Linear HDR light; the game's reverse-Z depth with an infinite far plane; FSR's own exposure (as Ray
    // Reconstruction's); its checks and debug view with diagnostics.
    create.flags = static_cast<uint32_t>(FFX_UPSCALE_ENABLE_HIGH_DYNAMIC_RANGE | FFX_UPSCALE_ENABLE_DEPTH_INVERTED |
                                         FFX_UPSCALE_ENABLE_DEPTH_INFINITE | FFX_UPSCALE_ENABLE_AUTO_EXPOSURE);
    if (s.diagnostics) {
        create.flags |= static_cast<uint32_t>(FFX_UPSCALE_ENABLE_DEBUG_CHECKING | FFX_UPSCALE_ENABLE_DEBUG_VISUALIZATION);
    }
    create.maxRenderSize = {width, height};
    create.maxUpscaleSize = {targetWidth, targetHeight};
    create.fpMessage = s.diagnostics ? &OnMessage : nullptr;
    ffxCreateContextDescUpscaleVersion version{};
    version.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE_VERSION;
    version.version = FFX_UPSCALER_VERSION;
    Backend backend;
    create.header.pNext = &version.header;
    version.header.pNext = &backend.backend.header;
    ffxContext context = nullptr;
    const ffxReturnCode_t result = s.api.createContext(&context, &create.header, nullptr);
    if (result != FFX_API_RETURN_OK || context == nullptr) {
        s.preparedFailed = true;
        s.failedWidth = width;
        s.failedHeight = height;
        s.failedTargetWidth = targetWidth;
        s.failedTargetHeight = targetHeight;
        Error(Format(L"FSR: the upscaler could not be created at %ux%u to %ux%u (result %u); the lit view uses our "
                     L"filter alone at these sizes.",
                     width, height, targetWidth, targetHeight, static_cast<unsigned>(result)));
        return false;
    }
    s.preparedFailed = false;
    s.context = context;
    s.width = width;
    s.height = height;
    s.targetWidth = targetWidth;
    s.targetHeight = targetHeight;
    s.quality = quality;
    ffxQueryGetProviderVersion provider{};
    provider.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
    {
        std::lock_guard<std::mutex> versionLock(s.versionLock);
        s.contextVersion = s.api.query(&s.context, &provider.header) == FFX_API_RETURN_OK &&
                                   provider.versionName != nullptr
                               ? Widen(provider.versionName)
                               : std::wstring(L"FSR (version unknown)");
    }
    std::int32_t phases = 0;
    ffxQueryDescUpscaleGetJitterPhaseCount phaseQuery{};
    phaseQuery.header.type = FFX_API_QUERY_DESC_TYPE_UPSCALE_GETJITTERPHASECOUNT;
    phaseQuery.renderWidth = width;
    phaseQuery.displayWidth = targetWidth;
    phaseQuery.pOutPhaseCount = &phases;
    s.phaseCount = s.api.query(&s.context, &phaseQuery.header) == FFX_API_RETURN_OK && phases > 0 ? phases : 8;
    FfxApiEffectMemoryUsage memory{};
    ffxQueryDescUpscaleGetGPUMemoryUsage memoryQuery{};
    memoryQuery.header.type = FFX_API_QUERY_DESC_TYPE_UPSCALE_GPU_MEMORY_USAGE;
    memoryQuery.gpuMemoryUsageUpscaler = &memory;
    const bool memoryKnown = s.api.query(&s.context, &memoryQuery.header) == FFX_API_RETURN_OK;
    Info(Format(L"FSR: %s created: traced at %ux%u, out at %ux%u (%s; %d jitter phases; %s).",
                s.contextVersion.c_str(), width, height, targetWidth, targetHeight, QualityName(quality),
                s.phaseCount,
                memoryKnown ? Format(L"%.0f MB of GPU memory",
                                     static_cast<double>(memory.totalUsageInBytes) / 1048576.0).c_str()
                            : L"its memory unknown"));
    return true;
}

void JitterOffset(const unsigned long long frame, float& x, float& y) {
    x = 0.0f;
    y = 0.0f;
    if (s.context == nullptr) {
        return;
    }
    ffxQueryDescUpscaleGetJitterOffset query{};
    query.header.type = FFX_API_QUERY_DESC_TYPE_UPSCALE_GETJITTEROFFSET;
    query.index = static_cast<std::int32_t>(frame % static_cast<unsigned long long>(s.phaseCount));
    query.phaseCount = s.phaseCount;
    query.pOutX = &x;
    query.pOutY = &y;
    if (s.api.query(&s.context, &query.header) != FFX_API_RETURN_OK) {
        x = 0.0f;
        y = 0.0f;
    }
}

bool Evaluate(ID3D12GraphicsCommandList* list, const Inputs& in) {
    if (s.context == nullptr || list == nullptr || in.width != s.width || in.height != s.height ||
        in.targetWidth != s.targetWidth || in.targetHeight != s.targetHeight) {
        return false;
    }
    ffxDispatchDescUpscale dispatch{};
    dispatch.header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
    dispatch.commandList = list;
    dispatch.color = ffxApiGetResourceDX12(in.color, FFX_API_RESOURCE_STATE_COMPUTE_READ);
    dispatch.depth = ffxApiGetResourceDX12(in.depth, FFX_API_RESOURCE_STATE_COMPUTE_READ);
    dispatch.motionVectors = ffxApiGetResourceDX12(in.motion, FFX_API_RESOURCE_STATE_COMPUTE_READ);
    dispatch.output = ffxApiGetResourceDX12(in.output, FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
    dispatch.jitterOffset = {in.jitterX, in.jitterY};
    dispatch.motionVectorScale = {1.0f, 1.0f};  // pixels already
    dispatch.renderSize = {in.width, in.height};
    dispatch.upscaleSize = {in.targetWidth, in.targetHeight};
    dispatch.enableSharpening = in.sharpness > 0.0f;
    dispatch.sharpness = std::clamp(in.sharpness, 0.0f, 1.0f);
    dispatch.frameTimeDelta = std::clamp(in.frameTimeMs, 1.0f, 100.0f);
    dispatch.preExposure = 1.0f;
    dispatch.reset = in.reset;
    dispatch.cameraNear = in.cameraNear;
    dispatch.cameraFar = in.cameraFar;
    dispatch.cameraFovAngleVertical = in.fovY;
    dispatch.viewSpaceToMetersFactor = 1.0f;  // the game's units are metres
    dispatch.flags = in.debugView && s.diagnostics ? static_cast<uint32_t>(FFX_UPSCALE_FLAG_DRAW_DEBUG_VIEW) : 0u;
    const ffxReturnCode_t result = s.api.dispatch(&s.context, &dispatch.header);
    if (result != FFX_API_RETURN_OK) {
        if (!s.loggedDispatchFailure) {
            s.loggedDispatchFailure = true;
            Error(Format(L"FSR: the upscaler's dispatch failed (result %u).", static_cast<unsigned>(result)));
        }
        return false;
    }
    return true;
}

void ReleaseRetired(const unsigned long long completed) {
    s.retired.erase(std::remove_if(s.retired.begin(), s.retired.end(),
                                   [completed](std::pair<ffxContext, unsigned long long>& entry) {
                                       if (entry.second > completed) {
                                           return false;
                                       }
                                       DestroyContext(entry.first);
                                       return true;
                                   }),
                    s.retired.end());
}

const std::wstring& ContextVersion() {
    return s.contextVersion;
}

namespace {

// FSR's swap chain records the generation through this when it presents (the documented path: the driver's frame
// pacing needs it).
ffxReturnCode_t DispatchFrameGeneration(ffxDispatchDescFrameGeneration* params, void* context) {
    return s.api.dispatch(static_cast<ffxContext*>(context), &params->header);
}

// The generation context for a back buffer of `width` x `height` in `format` (and the frame before the HUD in the
// same format), the render size up to it.
bool CreateFrameGenContext(const unsigned width, const unsigned height, const DXGI_FORMAT format) {
    ffxCreateContextDescFrameGeneration create{};
    create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
    // The game's reverse-Z depth with an infinite far plane; the back buffer is sRGB-encoded 8-bit (not HDR).
    create.flags = static_cast<uint32_t>(FFX_FRAMEGENERATION_ENABLE_DEPTH_INVERTED |
                                         FFX_FRAMEGENERATION_ENABLE_DEPTH_INFINITE);
    if (s.diagnostics) {
        create.flags |= static_cast<uint32_t>(FFX_FRAMEGENERATION_ENABLE_DEBUG_CHECKING);
    }
    create.displaySize = {width, height};
    create.maxRenderSize = {width, height};
    create.backBufferFormat = ffxApiGetSurfaceFormatDX12(format);
    ffxCreateContextDescFrameGenerationHudless hudless{};
    hudless.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_HUDLESS;
    hudless.hudlessBackBufferFormat = ffxApiGetSurfaceFormatDX12(format);
    ffxCreateContextDescFrameGenerationVersion version{};
    version.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_VERSION;
    version.version = FFX_FRAMEGENERATION_VERSION;
    ffxCreateBackendDX12Desc backend{};
    backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backend.device = s.device;
    create.header.pNext = &hudless.header;
    hudless.header.pNext = &version.header;
    version.header.pNext = &backend.header;
    const ffxReturnCode_t result = s.api.createContext(&s.frameGenContext, &create.header, nullptr);
    if (result != FFX_API_RETURN_OK || s.frameGenContext == nullptr) {
        s.frameGenContext = nullptr;
        Error(Format(L"FSR: the frame generation context could not be created for %ux%u (format %d; result %u).",
                     width, height, static_cast<int>(format), static_cast<unsigned>(result)));
        return false;
    }
    s.fgWidth = width;
    s.fgHeight = height;
    s.fgFormat = format;
    ffxQueryGetProviderVersion provider{};
    provider.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
    s.frameGenVersion = s.api.query(&s.frameGenContext, &provider.header) == FFX_API_RETURN_OK &&
                                provider.versionName != nullptr
                            ? Widen(provider.versionName)
                            : std::wstring(L"version unknown");
    FfxApiEffectMemoryUsage memory{};
    ffxQueryDescFrameGenerationGetGPUMemoryUsage memoryQuery{};
    memoryQuery.header.type = FFX_API_QUERY_DESC_TYPE_FRAMEGENERATION_GPU_MEMORY_USAGE;
    memoryQuery.gpuMemoryUsageFrameGeneration = &memory;
    const bool memoryKnown = s.api.query(&s.frameGenContext, &memoryQuery.header) == FFX_API_RETURN_OK;
    Info(Format(L"FSR: frame generation %s created for %ux%u (format %d; %s).", s.frameGenVersion.c_str(), width,
                height, static_cast<int>(format),
                memoryKnown ? Format(L"%.0f MB of GPU memory",
                                     static_cast<double>(memory.totalUsageInBytes) / 1048576.0).c_str()
                            : L"its memory unknown"));
    return true;
}

void DestroyFrameGenContext() {
    if (s.frameGenContext == nullptr) {
        return;
    }
    // Off first (AMD's order), then destroyed.
    ffxConfigureDescFrameGeneration off{};
    off.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
    off.swapChain = s.chain;
    off.frameGenerationEnabled = false;
    off.generationRect = {0, 0, static_cast<int32_t>(s.fgWidth), static_cast<int32_t>(s.fgHeight)};
    s.api.configure(&s.frameGenContext, &off.header);
    s.api.destroyContext(&s.frameGenContext, nullptr);
    s.frameGenContext = nullptr;
}

} // namespace

bool FrameGenAvailable() {
    return s.available.load() && !s.frameGenerators.empty();
}

IDXGISwapChain4* FrameGenCreate(const HWND window, const DXGI_SWAP_CHAIN_DESC1& desc, IDXGIFactory* factory,
                                ID3D12CommandQueue* queue) {
    if (!FrameGenAvailable() || window == nullptr || factory == nullptr || queue == nullptr) {
        return nullptr;
    }
    FrameGenDestroy();
    IDXGISwapChain4* chain = nullptr;
    DXGI_SWAP_CHAIN_DESC1 chainDesc = desc;
    chainDesc.BufferCount = 2;  // AMD: double buffering is enough (FSR's chain keeps its own buffers too)
    ffxCreateContextDescFrameGenerationSwapChainForHwndDX12 create{};
    create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_FOR_HWND_DX12;
    create.swapchain = &chain;
    create.hwnd = window;
    create.desc = &chainDesc;
    create.fullscreenDesc = nullptr;  // windowed (our child window)
    create.dxgiFactory = factory;
    create.gameQueue = queue;
    ffxCreateContextDescFrameGenerationSwapChainVersionDX12 version{};
    version.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_VERSION_DX12;
    version.version = FFX_FRAMEGENERATION_SWAPCHAIN_DX12_VERSION;
    create.header.pNext = &version.header;
    const ffxReturnCode_t result = s.api.createContext(&s.swapChainContext, &create.header, nullptr);
    if (result != FFX_API_RETURN_OK || s.swapChainContext == nullptr || chain == nullptr) {
        Error(Format(L"FSR: its frame generation swap chain could not be made on window %p (result %u).",
                     static_cast<void*>(window), static_cast<unsigned>(result)));
        if (s.swapChainContext != nullptr) {
            s.api.destroyContext(&s.swapChainContext, nullptr);
            s.swapChainContext = nullptr;
        }
        if (chain != nullptr) {
            chain->Release();
        }
        return nullptr;
    }
    s.chain = chain;
    if (!CreateFrameGenContext(chainDesc.Width, chainDesc.Height, chainDesc.Format)) {
        s.api.destroyContext(&s.swapChainContext, nullptr);
        s.swapChainContext = nullptr;
        s.chain = nullptr;
        chain->Release();
        return nullptr;
    }
    ffxQueryGetProviderVersion provider{};
    provider.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
    const std::wstring chainVersion = s.api.query(&s.swapChainContext, &provider.header) == FFX_API_RETURN_OK &&
                                              provider.versionName != nullptr
                                          ? Widen(provider.versionName)
                                          : std::wstring(L"version unknown");
    Info(Format(L"FSR: frame generation swap chain %s on window %p (%ux%u, format %d, 2 buffers).",
                chainVersion.c_str(), static_cast<void*>(window), chainDesc.Width, chainDesc.Height,
                static_cast<int>(chainDesc.Format)));
    return chain;
}

bool FrameGenResize(const unsigned width, const unsigned height, const DXGI_FORMAT format) {
    if (s.swapChainContext == nullptr) {
        return false;
    }
    if (s.frameGenContext != nullptr && width == s.fgWidth && height == s.fgHeight && format == s.fgFormat) {
        return true;
    }
    DestroyFrameGenContext();
    return CreateFrameGenContext(width, height, format);
}

void FrameGenDestroy() {
    if (s.swapChainContext != nullptr) {
        // FSR's own presents of the last frames done first (its present thread).
        ffxDispatchDescFrameGenerationSwapChainWaitForPresentsDX12 wait{};
        wait.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_WAIT_FOR_PRESENTS_DX12;
        s.api.dispatch(&s.swapChainContext, &wait.header);
    }
    DestroyFrameGenContext();
    if (s.swapChainContext != nullptr) {
        s.api.destroyContext(&s.swapChainContext, nullptr);
        s.swapChainContext = nullptr;
    }
    s.chain = nullptr;
    s.frameGenVersion.clear();
}

bool FrameGenPrepare(ID3D12GraphicsCommandList* list, const FrameGenInputs& in, const unsigned long long frameId) {
    if (s.frameGenContext == nullptr || list == nullptr || in.depth == nullptr || in.motion == nullptr) {
        return false;
    }
    // The camera's position from an origin of this session's: floats keep their precision near it; a new origin
    // (10 km away) resets generation once.
    bool reset = in.reset;
    double offset = 0.0;
    for (int axis = 0; axis < 3 && s.originSet; ++axis) {
        offset = std::max(offset, std::abs(in.cameraPosition[axis] - s.origin[axis]));
    }
    if (!s.originSet || offset > 10000.0) {
        for (int axis = 0; axis < 3; ++axis) {
            s.origin[axis] = in.cameraPosition[axis];
        }
        s.originSet = true;
        reset = true;
    }
    ffxDispatchDescFrameGenerationPrepareV2 prepare{};
    prepare.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_V2;
    prepare.frameID = frameId;
    prepare.flags = 0;
    prepare.commandList = list;
    prepare.renderSize = {in.width, in.height};
    prepare.jitterOffset = {in.jitterX, in.jitterY};
    prepare.motionVectorScale = {1.0f, 1.0f};  // pixels already
    prepare.frameTimeDelta = std::clamp(in.frameTimeMs, 1.0f, 100.0f);
    prepare.reset = reset;
    prepare.cameraNear = in.cameraNear;
    prepare.cameraFar = in.cameraFar;
    prepare.cameraFovAngleVertical = in.fovY;
    prepare.viewSpaceToMetersFactor = 1.0f;
    prepare.depth = ffxApiGetResourceDX12(in.depth, FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
    prepare.motionVectors = ffxApiGetResourceDX12(in.motion, FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
    for (int axis = 0; axis < 3; ++axis) {
        prepare.cameraPosition[axis] = static_cast<float>(in.cameraPosition[axis] - s.origin[axis]);
        prepare.cameraUp[axis] = in.cameraUp[axis];
        prepare.cameraRight[axis] = in.cameraRight[axis];
        prepare.cameraForward[axis] = in.cameraForward[axis];
    }
    const ffxReturnCode_t result = s.api.dispatch(&s.frameGenContext, &prepare.header);
    if (result != FFX_API_RETURN_OK) {
        if (!s.fgPrepareFailureLogged) {
            s.fgPrepareFailureLogged = true;
            Error(Format(L"FSR: frame generation's prepare failed (result %u).", static_cast<unsigned>(result)));
        }
        return false;
    }
    return true;
}

bool FrameGenConfigure(const bool enabled, ID3D12Resource* hudless, const unsigned width, const unsigned height,
                       const unsigned long long frameId) {
    if (s.frameGenContext == nullptr || s.chain == nullptr) {
        return false;
    }
    ffxConfigureDescFrameGeneration configure{};
    configure.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
    configure.swapChain = s.chain;
    configure.presentCallback = nullptr;  // the HUD comes from the frame before it (HUDLessColor)
    configure.frameGenerationCallback = &DispatchFrameGeneration;
    configure.frameGenerationCallbackUserContext = &s.frameGenContext;
    configure.frameGenerationEnabled = enabled;
    configure.allowAsyncWorkloads = false;
    if (hudless != nullptr) {
        configure.HUDLessColor = ffxApiGetResourceDX12(hudless, FFX_API_RESOURCE_STATE_COMMON);
    }
    configure.flags = 0;
    configure.onlyPresentGenerated = false;
    configure.generationRect = {0, 0, static_cast<int32_t>(width), static_cast<int32_t>(height)};
    configure.frameID = frameId;
    const ffxReturnCode_t result = s.api.configure(&s.frameGenContext, &configure.header);
    if (result != FFX_API_RETURN_OK) {
        if (!s.fgConfigureFailureLogged) {
            s.fgConfigureFailureLogged = true;
            Error(Format(L"FSR: frame generation's configuration failed (result %u).", static_cast<unsigned>(result)));
        }
        return false;
    }
    return true;
}

const std::wstring& FrameGenVersion() {
    return s.frameGenVersion;
}

#else

bool Built() {
    return false;
}

bool Initialize(ID3D12Device*, const std::wstring&, const std::wstring&, bool) {
    return false;
}

bool Available() {
    return false;
}

std::wstring Status() {
    return L"this build of the plugin has no AMD FSR SDK (scripts/fetch_fsr.ps1)";
}

void RenderSize(const unsigned targetWidth, const unsigned targetHeight, int, unsigned& width, unsigned& height) {
    width = targetWidth;
    height = targetHeight;
}

bool Prepare(unsigned, unsigned, unsigned, unsigned, int, unsigned long long) {
    return false;
}

void JitterOffset(unsigned long long, float& x, float& y) {
    x = 0.0f;
    y = 0.0f;
}

bool Evaluate(ID3D12GraphicsCommandList*, const Inputs&) {
    return false;
}

void ReleaseRetired(unsigned long long) {}

const std::wstring& ContextVersion() {
    static const std::wstring kNone;
    return kNone;
}

bool FrameGenAvailable() {
    return false;
}

IDXGISwapChain4* FrameGenCreate(HWND, const DXGI_SWAP_CHAIN_DESC1&, IDXGIFactory*, ID3D12CommandQueue*) {
    return nullptr;
}

bool FrameGenResize(unsigned, unsigned, DXGI_FORMAT) {
    return false;
}

void FrameGenDestroy() {}

bool FrameGenPrepare(ID3D12GraphicsCommandList*, const FrameGenInputs&, unsigned long long) {
    return false;
}

bool FrameGenConfigure(bool, ID3D12Resource*, unsigned, unsigned, unsigned long long) {
    return false;
}

const std::wstring& FrameGenVersion() {
    static const std::wstring kNone;
    return kNone;
}

#endif

} // namespace sert::rt::fsr
