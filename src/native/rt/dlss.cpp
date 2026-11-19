#include "dlss.h"

#include "../logger.h"
#include "../plugin_version.h"
#include "common.h"

#if defined(SERT_DLSS)
#include <nvsdk_ngx.h>
#include <nvsdk_ngx_defs.h>
#include <nvsdk_ngx_helpers.h>
#include <nvsdk_ngx_helpers_dlssd_d3d.h>
#include <nvsdk_ngx_params.h>
#endif

#include <string>

namespace sert::rt::dlss {

#if defined(SERT_DLSS)

namespace {

// A project id of our own (NGX asks custom engines for a GUID-style id).
constexpr char kProjectId[] = "5f0c2a0e-8d64-4c7e-9a2b-6b1e3f7d9c41";

struct State {
    bool initialized = false;
    bool available = false;
    ID3D12Device* device = nullptr;
    NVSDK_NGX_Parameter* parameters = nullptr;
    NVSDK_NGX_Handle* feature = nullptr;
    unsigned width = 0;
    unsigned height = 0;
    bool loggedEvaluateFailure = false;
};
State s;

void NVSDK_CONV LogCallback(const char* message, NVSDK_NGX_Logging_Level, NVSDK_NGX_Feature) {
    if (message == nullptr) {
        return;
    }
    std::wstring text;
    for (const char* c = message; *c != '\0'; ++c) {
        if (*c != '\n' && *c != '\r') {
            text += static_cast<wchar_t>(static_cast<unsigned char>(*c));
        }
    }
    log::Write(log::Level::info, L"NGX: " + text);
}

const wchar_t* ResultName(const NVSDK_NGX_Result result) {
    return GetNGXResultAsString(result);
}

} // namespace

bool Built() {
    return true;
}

bool Initialize(ID3D12Device* device, const wchar_t* runtimeDirectory, const wchar_t* dataDirectory) {
    if (s.initialized) {
        return s.available;
    }
    s.initialized = true;
    s.device = device;
    const wchar_t* paths[] = {runtimeDirectory};
    NVSDK_NGX_FeatureCommonInfo info{};
    info.PathListInfo.Path = paths;
    info.PathListInfo.Length = 1;
    info.LoggingInfo.LoggingCallback = &LogCallback;
    info.LoggingInfo.MinimumLoggingLevel = NVSDK_NGX_LOGGING_LEVEL_ON;
    info.LoggingInfo.DisableOtherLoggingSinks = false;
    NVSDK_NGX_Result result = NVSDK_NGX_D3D12_Init_with_ProjectID(kProjectId, NVSDK_NGX_ENGINE_TYPE_CUSTOM, SE_RTHWACCEL_VERSION_ASCII,
                                                                  dataDirectory, device, &info, NVSDK_NGX_Version_API);
    if (NVSDK_NGX_FAILED(result)) {
        Error(Format(L"DLSS: NGX could not be initialized (%s, 0x%08X); Ray Reconstruction stays off.",
                     ResultName(result), static_cast<unsigned>(result)));
        return false;
    }
    result = NVSDK_NGX_D3D12_GetCapabilityParameters(&s.parameters);
    if (NVSDK_NGX_FAILED(result) || s.parameters == nullptr) {
        Error(Format(L"DLSS: NGX capability parameters unavailable (%s); Ray Reconstruction stays off.",
                     ResultName(result)));
        return false;
    }
    int available = 0;
    int needsDriver = 0;
    unsigned driverMajor = 0;
    unsigned driverMinor = 0;
    NVSDK_NGX_Parameter_GetI(s.parameters, NVSDK_NGX_Parameter_SuperSamplingDenoising_Available, &available);
    NVSDK_NGX_Parameter_GetI(s.parameters, NVSDK_NGX_Parameter_SuperSamplingDenoising_NeedsUpdatedDriver, &needsDriver);
    NVSDK_NGX_Parameter_GetUI(s.parameters, NVSDK_NGX_Parameter_SuperSamplingDenoising_MinDriverVersionMajor,
                              &driverMajor);
    NVSDK_NGX_Parameter_GetUI(s.parameters, NVSDK_NGX_Parameter_SuperSamplingDenoising_MinDriverVersionMinor,
                              &driverMinor);
    if (available == 0) {
        int initResult = 0;
        NVSDK_NGX_Parameter_GetI(s.parameters, NVSDK_NGX_Parameter_SuperSamplingDenoising_FeatureInitResult,
                                 &initResult);
        Error(Format(L"DLSS: Ray Reconstruction is not available (feature init result 0x%08X%s); it stays off.",
                     static_cast<unsigned>(initResult),
                     needsDriver != 0 ? Format(L", needs driver %u.%u", driverMajor, driverMinor).c_str() : L""));
        return false;
    }
    s.available = true;
    Info(Format(L"DLSS: Ray Reconstruction available (runtime from %s).", runtimeDirectory));
    return true;
}

bool Evaluate(ID3D12GraphicsCommandList* list, const Inputs& in) {
    if (!s.available || list == nullptr || in.width == 0 || in.height == 0) {
        return false;
    }
    if (s.feature == nullptr || s.width != in.width || s.height != in.height) {
        if (s.feature != nullptr) {
            NVSDK_NGX_D3D12_ReleaseFeature(s.feature);  // the GPU is past it: the frames before are complete
            s.feature = nullptr;
        }
        NVSDK_NGX_DLSSD_Create_Params create{};
        create.InDenoiseMode = NVSDK_NGX_DLSS_Denoise_Mode_DLUnified;
        create.InRoughnessMode = NVSDK_NGX_DLSS_Roughness_Mode_Packed;
        create.InUseHWDepth = NVSDK_NGX_DLSS_Depth_Type_Linear;
        create.InWidth = in.width;
        create.InHeight = in.height;
        create.InTargetWidth = in.width;
        create.InTargetHeight = in.height;
        create.InPerfQualityValue = NVSDK_NGX_PerfQuality_Value_DLAA;
        create.InFeatureCreateFlags = NVSDK_NGX_DLSS_Feature_Flags_IsHDR | NVSDK_NGX_DLSS_Feature_Flags_MVLowRes |
                                      NVSDK_NGX_DLSS_Feature_Flags_AutoExposure;
        create.InEnableOutputSubrects = false;
        const NVSDK_NGX_Result result = NGX_D3D12_CREATE_DLSSD_EXT(list, 1, 1, &s.feature, s.parameters, &create);
        if (NVSDK_NGX_FAILED(result) || s.feature == nullptr) {
            Error(Format(L"DLSS: Ray Reconstruction could not be created at %ux%u (%s); it stays off.", in.width,
                         in.height, ResultName(result)));
            s.feature = nullptr;
            s.available = false;
            return false;
        }
        s.width = in.width;
        s.height = in.height;
        Info(Format(L"DLSS: Ray Reconstruction created at %ux%u (DLAA, linear depth, roughness in the normals' w).",
                    in.width, in.height));
        return false;  // evaluated from the next frame on
    }
    NVSDK_NGX_D3D12_DLSSD_Eval_Params eval{};
    eval.pInColor = in.color;
    eval.pInOutput = in.output;
    eval.pInDepth = in.depth;
    eval.pInMotionVectors = in.motion;
    eval.pInDiffuseAlbedo = in.diffuseAlbedo;
    eval.pInSpecularAlbedo = in.specularAlbedo;
    eval.pInNormals = in.normalRoughness;
    eval.pInRoughness = in.normalRoughness;
    eval.pInSpecularHitDistance = in.specularHitDistance;
    eval.InRenderSubrectDimensions.Width = in.width;
    eval.InRenderSubrectDimensions.Height = in.height;
    eval.InReset = in.reset ? 1 : 0;
    eval.InJitterOffsetX = in.jitterX;
    eval.InJitterOffsetY = in.jitterY;
    eval.InMVScaleX = 1.0f;
    eval.InMVScaleY = 1.0f;
    eval.pInWorldToViewMatrix = const_cast<float*>(in.worldToView);
    eval.pInViewToClipMatrix = const_cast<float*>(in.viewToClip);
    eval.InFrameTimeDeltaInMsec = in.frameTimeMs;
    const NVSDK_NGX_Result result = NGX_D3D12_EVALUATE_DLSSD_EXT(list, s.feature, s.parameters, &eval);
    if (NVSDK_NGX_FAILED(result)) {
        if (!s.loggedEvaluateFailure) {
            s.loggedEvaluateFailure = true;
            Error(Format(L"DLSS: Ray Reconstruction evaluation failed (%s).", ResultName(result)));
        }
        return false;
    }
    return true;
}

#else

bool Built() {
    return false;
}

bool Initialize(ID3D12Device*, const wchar_t*, const wchar_t*) {
    return false;
}

bool Evaluate(ID3D12GraphicsCommandList*, const Inputs&) {
    return false;
}

#endif

} // namespace sert::rt::dlss
