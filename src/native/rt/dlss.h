#pragma once

// DLSS Ray Reconstruction (NVIDIA NGX) as the optional denoiser of the traced
// light (research/docs/space-engineers-rt-plan.md §6.10). Built when the DLSS
// SDK is present (third_party/dlss, scripts/fetch_dlss.ps1: SERT_DLSS);
// otherwise every call reports it unavailable. Render thread only.

#include <d3d12.h>

namespace sert::rt::dlss {

// The frame's inputs (at the render size) and its output (at the target size:
// the same for DLAA, larger when Ray Reconstruction upscales): the noisy light
// (linear HDR), the guide images and the camera.
struct Inputs {
    ID3D12Resource* color = nullptr;            // RGBA16F: this frame's light, unfiltered
    ID3D12Resource* diffuseAlbedo = nullptr;    // RGBA16F
    ID3D12Resource* specularAlbedo = nullptr;   // RGBA16F: the integrated specular BRDF
    ID3D12Resource* normalRoughness = nullptr;  // RGBA16F: world normal, roughness in w
    ID3D12Resource* depth = nullptr;            // R32F: linear view depth
    ID3D12Resource* motion = nullptr;           // RG16F: pixels, previous = current + motion
    ID3D12Resource* specularHitDistance = nullptr;  // R16F: the specular bounce's length (optional)
    ID3D12Resource* output = nullptr;           // RGBA16F, unordered access, at the target size
    unsigned width = 0;          // the render size (the inputs')
    unsigned height = 0;
    unsigned targetWidth = 0;    // the output's size
    unsigned targetHeight = 0;
    int quality = 0;             // rt::DlssMode: 0 DLAA, 1 quality, 2 balanced, 3 performance, 4 ultra performance
    const float* worldToView = nullptr;  // row-major, row vectors (camera-relative world)
    const float* viewToClip = nullptr;
    bool reset = false;          // no usable history (first frame, a cut)
    float jitterX = 0;           // the projection's sub-pixel jitter (pixels, y down: where the geometry moved)
    float jitterY = 0;
    float frameTimeMs = 16.7f;
};

// Whether this build has the SDK.
bool Built();

// Initializes NGX on `device` once (runtime DLLs searched in `runtimeDirectory`,
// logs under `dataDirectory`); true when Ray Reconstruction is available.
// Later calls return the first result.
bool Initialize(ID3D12Device* device, const wchar_t* runtimeDirectory, const wchar_t* dataDirectory);

// The render size Ray Reconstruction takes for an output of `targetWidth` x
// `targetHeight` in DLSS mode `quality` (rt::DlssMode): NVIDIA's optimal size
// for that mode, the target itself for DLAA or while Ray Reconstruction is
// unavailable.
void RenderSize(unsigned targetWidth, unsigned targetHeight, int quality, unsigned& width, unsigned& height);

// A DLSS mode's name for the log ("DLAA", "quality", ...).
const wchar_t* QualityName(int quality);

// Records Ray Reconstruction on `list`. The inputs are in the non-pixel shader
// resource state, the output in the unordered-access state. Creates the
// feature at a new size or mode first (recorded on `list`; that frame
// evaluates nothing): false then and when unavailable. NGX changes the list's
// pipeline, root signature and descriptor heaps.
bool Evaluate(ID3D12GraphicsCommandList* list, const Inputs& inputs);

} // namespace sert::rt::dlss
