#pragma once

#include "common.h"

#include <dxgi1_4.h>

namespace sert::rt {

// DLSS Frame Generation through NVIDIA Streamline (third_party/streamline; manual hooking: only the interfaces
// the presenter upgrades go through Streamline), on the presenter's D3D12 swap chain
// (research/docs/space-engineers-rt-plan.md P2ae). Built without the SDK (SERT_STREAMLINE undefined) it is never
// available.

// What a traced frame gives frame generation (rt_core's RecordFrame keeps it and hands it over on its own list,
// presenter.cpp PresenterTagTraced): its depth and motion at the render size, its camera (camera-relative,
// row-major matrices for row vectors, VRageMath's).
struct FrameGenFrame {
    ID3D12Resource* hardwareDepth = nullptr;  // R32F at the render size, unordered-access state after the frame: the
                                              // game's reverse-Z depth of each pixel (primary_view.hlsl UpscaleDepth)
    ID3D12Resource* motion = nullptr;  // RG16F in pixels (previous = current + motion), unordered-access state
    float jitter[2]{};                 // the projection's jitter of the traced frame (pixels, y down)
    double cameraPosition[3]{};        // world
    float frameTimeMs = 16.7f;         // since the previous traced frame
    UINT width = 0;                    // the render size
    UINT height = 0;
    float projection[16]{};
    float invProjection[16]{};
    float viewProjection[16]{};
    float invViewProjection[16]{};
    float previousViewProjection[16]{};
    float previousInvViewProjection[16]{};
    float invView[16]{};               // the camera's axes in its rows
    double cameraDelta[3]{};           // this frame's camera position minus the previous frame's
    bool reset = false;                // no usable history (the first frame, a cut)
    UINT64 sequence = 0;               // counts the traced frames kept (0: none yet)
};

// Once, after the core started: Streamline (sl.interposer.dll, checked for NVIDIA's signature, from the plugin's
// folder) initialized on `device`, DLSS-G and Reflex looked up. False when unavailable (the reason logged).
bool FrameGenLoad(ID3D12Device* device);
bool FrameGenLoaded();
// DLSS-G wanted and supported: the presenter prepares its inputs.
bool FrameGenEnabled();
// Whether frame generation can run here: 0 yes, 1 not tried yet (Streamline loads with the presenter), 2 no. `text`
// gets the reason when it cannot, and when it can, anything that hurts it (RivaTuner hooking the game).
int FrameGenStatus(std::wstring& text);
// Streamline's proxy of a device or DXGI factory (slUpgradeInterface): what the presenter creates its queue and
// swap chain through. The proxy holds the interface; the caller keeps the returned pointer for good.
bool FrameGenUpgrade(void** interfacePointer);
// The native swap chain behind a proxy (slGetNativeInterface), or null.
IDXGISwapChain* FrameGenNative(IDXGISwapChain* proxy);
// At the present of the game's frame (render thread), before its inputs: the frame's token (taken as the frame
// started, after the previous present), its simulation over, its submission begun.
void FrameGenBeginFrame();
// Render thread, mid-frame, on the core's command list: the traced frame's camera (Streamline's common
// constants) for the current frame's token, its motion and its hardware depth (`frame.hardwareDepth`, written:
// DLSS-G reads no linear depth) tagged and copied by Streamline on `list`. False when not set: the frame goes
// without generation.
bool FrameGenSetTracedInputs(ID3D12GraphicsCommandList* list, const FrameGenFrame& frame);
// Whether the current frame's traced inputs were set (FrameGenSetTracedInputs since its token was taken).
bool FrameGenFrameHasInputs();
// Render thread, at the frame's present, on the presenter's list: the frame before the HUD and the UI layer
// (`outputWidth` x `outputHeight`, in the common state and the unordered-access state) tagged and copied by
// Streamline, for a frame with its traced inputs set.
bool FrameGenSetUiInputs(ID3D12GraphicsCommandList* list, ID3D12Resource* hudless, ID3D12Resource* ui,
                         UINT outputWidth, UINT outputHeight);
// Around each present of the proxy swap chain (render thread): the frame's present markers; after it, the next
// frame starts (its token, Reflex's pacing sleep).
void FrameGenBeforePresent();
void FrameGenAfterPresent();
// DLSS-G's mode (off, or on generating one frame between each two), from the next present.
void FrameGenSetEnabled(bool on);
// Reflex's frame limit (frames per second; 0: none), from now or once Streamline is loaded.
void FrameGenSetFrameLimit(float framesPerSecond);
// Reflex's mode (0 off, 1 low latency: the default, 2 low latency with boost), from now or once Streamline is
// loaded. Its sleep and markers are called whichever (Streamline requires them).
void FrameGenSetReflexMode(int mode);
// Render thread: the statistics and DLSS-G's state logged (with the gate's periodic report).
void FrameGenReport(double seconds);

} // namespace sert::rt
