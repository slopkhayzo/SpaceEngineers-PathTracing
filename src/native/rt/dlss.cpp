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

#include <algorithm>
#include <cmath>
#include <string>

namespace sert::rt::dlss {

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
        return L"DLAA";
    }
}

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
    unsigned width = 0;  // the feature's render and target sizes and mode
    unsigned height = 0;
    unsigned targetWidth = 0;
    unsigned targetHeight = 0;
    int quality = 0;
    bool loggedEvaluateFailure = false;
    // RenderSize's last answer.
    unsigned sizeTargetWidth = 0;
    unsigned sizeTargetHeight = 0;
    int sizeQuality = -1;
    unsigned sizeWidth = 0;
    unsigned sizeHeight = 0;
};
State s;

NVSDK_NGX_PerfQuality_Value QualityValue(const int quality) {
    switch (quality) {
    case 1:
        return NVSDK_NGX_PerfQuality_Value_MaxQuality;
    case 2:
        return NVSDK_NGX_PerfQuality_Value_Balanced;
    case 3:
        return NVSDK_NGX_PerfQuality_Value_MaxPerf;
    case 4:
        return NVSDK_NGX_PerfQuality_Value_UltraPerformance;
    default:
        return NVSDK_NGX_PerfQuality_Value_DLAA;
    }
}

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

void RenderSize(const unsigned targetWidth, const unsigned targetHeight, const int quality, unsigned& width,
                unsigned& height) {
    width = targetWidth;
    height = targetHeight;
    if (!s.available || quality <= 0 || quality > 4 || targetWidth == 0 || targetHeight == 0) {
        return;
    }
    if (s.sizeTargetWidth == targetWidth && s.sizeTargetHeight == targetHeight && s.sizeQuality == quality) {
        width = s.sizeWidth;
        height = s.sizeHeight;
        return;
    }
    unsigned optimalWidth = 0;
    unsigned optimalHeight = 0;
    unsigned maxWidth = 0;
    unsigned maxHeight = 0;
    unsigned minWidth = 0;
    unsigned minHeight = 0;
    float sharpness = 0.0f;
    const NVSDK_NGX_Result result =
        NGX_DLSSD_GET_OPTIMAL_SETTINGS(s.parameters, targetWidth, targetHeight, QualityValue(quality), &optimalWidth,
                                       &optimalHeight, &maxWidth, &maxHeight, &minWidth, &minHeight, &sharpness);
    if (NVSDK_NGX_FAILED(result) || optimalWidth == 0 || optimalHeight == 0 || optimalWidth > targetWidth ||
        optimalHeight > targetHeight) {
        // NVIDIA's published scale factors per axis.
        static constexpr double kScale[5] = {1.0, 2.0 / 3.0, 0.58, 0.5, 1.0 / 3.0};
        optimalWidth = std::max(1u, static_cast<unsigned>(std::lround(targetWidth * kScale[quality])));
        optimalHeight = std::max(1u, static_cast<unsigned>(std::lround(targetHeight * kScale[quality])));
        Info(Format(L"DLSS: no optimal render size from NGX for %s at %ux%u (%s); %ux%u by NVIDIA's scale.",
                    QualityName(quality), targetWidth, targetHeight, ResultName(result), optimalWidth,
                    optimalHeight));
    }
    s.sizeTargetWidth = targetWidth;
    s.sizeTargetHeight = targetHeight;
    s.sizeQuality = quality;
    s.sizeWidth = optimalWidth;
    s.sizeHeight = optimalHeight;
    width = optimalWidth;
    height = optimalHeight;
}

bool Evaluate(ID3D12GraphicsCommandList* list, const Inputs& in) {
    if (!s.available || list == nullptr || in.width == 0 || in.height == 0 || in.targetWidth < in.width ||
        in.targetHeight < in.height) {
        return false;
    }
    if (s.feature == nullptr || s.width != in.width || s.height != in.height || s.targetWidth != in.targetWidth ||
        s.targetHeight != in.targetHeight || s.quality != in.quality) {
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
        create.InTargetWidth = in.targetWidth;
        create.InTargetHeight = in.targetHeight;
        create.InPerfQualityValue = QualityValue(in.quality);
        create.InFeatureCreateFlags = NVSDK_NGX_DLSS_Feature_Flags_IsHDR | NVSDK_NGX_DLSS_Feature_Flags_MVLowRes |
                                      NVSDK_NGX_DLSS_Feature_Flags_AutoExposure;
        create.InEnableOutputSubrects = false;
        const NVSDK_NGX_Result result = NGX_D3D12_CREATE_DLSSD_EXT(list, 1, 1, &s.feature, s.parameters, &create);
        if (NVSDK_NGX_FAILED(result) || s.feature == nullptr) {
            Error(Format(L"DLSS: Ray Reconstruction could not be created at %ux%u to %ux%u (%s, %s); it stays off.",
                         in.width, in.height, in.targetWidth, in.targetHeight, QualityName(in.quality),
                         ResultName(result)));
            s.feature = nullptr;
            s.available = false;
            return false;
        }
        s.width = in.width;
        s.height = in.height;
        s.targetWidth = in.targetWidth;
        s.targetHeight = in.targetHeight;
        s.quality = in.quality;
        Info(Format(L"DLSS: Ray Reconstruction created: traced at %ux%u, out at %ux%u (%s; linear depth, roughness "
                    L"in the normals' w).",
                    in.width, in.height, in.targetWidth, in.targetHeight, QualityName(in.quality)));
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

void RenderSize(const unsigned targetWidth, const unsigned targetHeight, int, unsigned& width, unsigned& height) {
    width = targetWidth;
    height = targetHeight;
}

bool Evaluate(ID3D12GraphicsCommandList*, const Inputs&) {
    return false;
}

#endif

} // namespace sert::rt::dlss
