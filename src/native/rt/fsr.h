#pragma once

// AMD FSR Upscaling of the lit view's traced light after our filter: the FFX API's upscale effect through AMD's
// signed DLLs (research/docs/amd-fsr-plan.md §4.1-4.3). AMD's loader picks the implementation: FSR 4
// (machine-learned) on the Radeons it supports, FSR 3.1 on every other GPU. Built when the SDK is present
// (third_party/fsr, scripts/fetch_fsr.ps1: SERT_FSR); otherwise every call reports it unavailable. Render thread,
// except Initialize.

#include <d3d12.h>
#include <dxgi1_5.h>

#include <string>

namespace sert::rt::fsr {

// A frame's inputs (at the render size) and output (at the target size).
struct Inputs {
    ID3D12Resource* color = nullptr;   // RGBA16F: the light, filtered (linear HDR)
    ID3D12Resource* depth = nullptr;   // R32F: the game's reverse-Z depth of what each pixel shows (0: the sky)
    ID3D12Resource* motion = nullptr;  // RG16F: pixels, previous = current + motion
    ID3D12Resource* output = nullptr;  // RGBA16F, unordered access, at the target size
    unsigned width = 0;                // the render size (the inputs')
    unsigned height = 0;
    unsigned targetWidth = 0;          // the output's size
    unsigned targetHeight = 0;
    float jitterX = 0;                 // the projection's jitter (pixels, y down: where the geometry moved)
    float jitterY = 0;
    float frameTimeMs = 16.7f;
    bool reset = false;                // no usable history (the first frame, a cut)
    float cameraNear = 0.05f;          // the camera's planes (the far one ignored: an infinite projection)
    float cameraFar = 1.0e6f;
    float fovY = 1.0f;                 // the vertical field of view (radians)
    float sharpness = 0.0f;            // FSR's sharpening, 0 (off) to 1
    bool debugView = false;            // FSR's debug view in the output (with diagnostics)
};

// Whether this build has the SDK.
bool Built();

// Once (any thread; later calls return the first answer): AMD's loader and the effect DLLs loaded from
// `directory` (AMD's signatures checked), the versions they offer on `device` logged. `iniPath`'s [RayTracing]
// FsrVersion may pin one (by the start of its name: "3" picks the newest FSR 3.x); `diagnostics` turns FSR's
// own checks on, its messages in the log. True when FSR Upscaling can run.
bool Initialize(ID3D12Device* device, const std::wstring& directory, const std::wstring& iniPath, bool diagnostics);

bool Available();

// For the log and the options screen: why FSR is unavailable, or the versions offered and in use.
std::wstring Status();

// A mode's name for the log ("native AA", "quality", ...): rt::DlssMode values.
const wchar_t* QualityName(int quality);

// The render size FSR takes for an output of `targetWidth` x `targetHeight` in mode `quality` (rt::DlssMode
// values: 0 native AA, 1 quality, 2 balanced, 3 performance, 4 ultra performance): AMD's.
void RenderSize(unsigned targetWidth, unsigned targetHeight, int quality, unsigned& width, unsigned& height);

// Render thread: the context for these sizes, made (or made again; the one before is destroyed once the core's
// fence passes `retireFence`). False when FSR cannot run here (logged once).
bool Prepare(unsigned width, unsigned height, unsigned targetWidth, unsigned targetHeight, int quality,
             unsigned long long retireFence);

// The jitter of frame `frame` for the prepared sizes: FSR's offset of the projection (pixels, y down: where the
// geometry moves). The primary rays sample the pixel centre minus it.
void JitterOffset(unsigned long long frame, float& x, float& y);

// Records FSR Upscaling on `list` with the prepared context: the inputs in the non-pixel-shader-resource state, the
// output in the unordered-access state. FSR sets the list's descriptor heaps, root signature and pipeline.
bool Evaluate(ID3D12GraphicsCommandList* list, const Inputs& inputs);

// The contexts retired before `completed` (the core's fence) destroyed.
void ReleaseRetired(unsigned long long completed);

// The prepared context's version name ("FSR 3.1.6", ...), empty without one.
const std::wstring& ContextVersion();

// --- AMD FSR Frame Generation (research/docs/amd-fsr-plan.md §4.5): its swap chain on the presenter's window and its
// context. Render thread (the presenter's and the core's lists), except FrameGenAvailable.

// Whether FSR offers frame generation on this GPU (after Initialize).
bool FrameGenAvailable();

// A traced frame's inputs for frame generation, at the render size, recorded on the core's list.
struct FrameGenInputs {
    ID3D12Resource* depth = nullptr;   // R32F: the game's reverse-Z depth (unordered-access state)
    ID3D12Resource* motion = nullptr;  // RG16F: pixels, previous = current + motion (unordered-access state)
    unsigned width = 0;
    unsigned height = 0;
    float jitterX = 0;                 // the projection's jitter (pixels, y down)
    float jitterY = 0;
    float frameTimeMs = 16.7f;
    bool reset = false;
    float cameraNear = 0.05f;
    float cameraFar = 1.0e6f;
    float fovY = 1.0f;                 // radians
    double cameraPosition[3]{};        // world
    float cameraRight[3]{};            // world axes of the camera
    float cameraUp[3]{};
    float cameraForward[3]{};
};

// On `window`: FSR's frame generation swap chain made from `desc` through `factory`, presenting on `queue` (FSR asks
// for double buffering), with its frame generation context for the chain's size and format. Null when it could not
// be made (logged); else the caller holds the returned reference and releases it after FrameGenDestroy.
IDXGISwapChain4* FrameGenCreate(HWND window, const DXGI_SWAP_CHAIN_DESC1& desc, IDXGIFactory* factory,
                                ID3D12CommandQueue* queue);
// The chain resized (its ResizeBuffers called): the context made again for the new size and format.
bool FrameGenResize(unsigned width, unsigned height, DXGI_FORMAT format);
// Frame generation off, its context and the swap chain's destroyed (the presenter's queue idle first); the caller then
// releases its reference to the chain.
void FrameGenDestroy();
// Mid-frame, on the core's list: frame `frameId`'s depth, motion and camera (FSR's prepare dispatch).
bool FrameGenPrepare(ID3D12GraphicsCommandList* list, const FrameGenInputs& inputs, unsigned long long frameId);
// At the present of frame `frameId`, before Present: generation on or off for it, with the frame before the HUD
// (`hudless`, in the common state; null: none) at `width` x `height`.
bool FrameGenConfigure(bool enabled, ID3D12Resource* hudless, unsigned width, unsigned height,
                       unsigned long long frameId);
// The frame generation context's version name, empty without one.
const std::wstring& FrameGenVersion();

} // namespace sert::rt::fsr
