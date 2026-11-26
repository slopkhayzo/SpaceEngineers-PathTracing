#pragma once

#include "common.h"

#include <dxgi1_4.h>

namespace sert::rt {

// The presenter (research/docs/space-engineers-rt-plan.md P2ae): the game's finished frames presented through a
// D3D12 flip-model swap chain of the core, in place of the presentation of the game's own D3D11 blt-model swap
// chain (DXGI's Present hooked; the game's chain keeps everything else: its back buffer, resizes, full-screen
// switches). The base of DLSS Frame Generation, which works on D3D12 swap chains only. DXGI allows no second swap
// chain on the game's window (E_ACCESSDENIED while the game's exists), so ours is on a child window of it,
// covering its client area: disabled, the mouse goes to the game's window as before.
enum class PresenterMode : int {
    game = 0,   // the game's swap chain presents, as without the presenter
    d3d12 = 1,  // ours presents the game's frames (windowed and borderless; full screen falls back to the game's)
};

// What ours is made with (one swap chain per window: changing it releases ours first, the game's chain presents
// that frame).
enum class PresenterBackend : int {
    plain = 0,       // a D3D12 flip-model chain of ours
    streamline = 1,  // through NVIDIA Streamline's proxies (framegen.h: DLSS Frame Generation); loads on first use
    fsr = 2,         // AMD FSR's frame generation swap chain (fsr.h), on our own queue
};

// After the core started (any thread): hooks IDXGISwapChain::Present of the game's swap chain `swapChain` (the
// chain on the game's window is the game's, also one it makes anew) and sets the mode and the backend. Called again,
// it sets them only.
bool PresenterInitialize(const Devices& devices, IDXGISwapChain* swapChain, PresenterMode mode,
                         PresenterBackend backend);
// Any thread: AMD FSR's frame generation on or off (with the fsr backend), from the next frame on.
void PresenterSetFsrFrameGeneration(bool on);
// Any thread: how long frame generation waits once traced frames come back after a gap (a world loading); 10 s by
// default ([Debug] FrameGenWarmUp), 0 none.
void PresenterSetWarmUp(double seconds);
// Render thread, once per traced frame (lit, replace mode): whether it should hand frame generation its inputs
// (PresenterTagTraced): DLSS-G on through Streamline, or FSR's on, and not in the warm-up after a gap without traced
// frames (a world loading).
bool PresenterWantsTracedInputs();
// The backend asked for (PresenterInitialize), and whether frame generation can run on it: 0 yes, 1 not known yet,
// 2 no (`text` says why; when it can, anything that hurts it).
PresenterBackend PresenterWantedBackend();
int PresenterFsrStatus(std::wstring& text);
// On the thread of the game's window (it pumps the child's messages): the child window made. Until then the
// game's swap chain presents.
bool PresenterAttachWindow();
// The game's window (its swap chain's), after PresenterInitialize.
HWND PresenterGameWindow();
// Any thread: the mode from the next present on (falling back to the game's chain releases ours).
void PresenterSetMode(PresenterMode mode);
// Any thread: our presents' sync interval (-1: the game's; 0; 1).
void PresenterSetSync(int syncInterval);
// Render thread: the window's statistics logged and reset (with the gate's periodic report).
void PresenterReport(double seconds);
// Render thread, after the game drew its scene (the HUD not yet): the frame copied for frame generation.
void PresenterCaptureHudless();
// Render thread, on the core's command list once a traced frame's inputs are final (frame generation on): the frame's
// camera, its hardware depth (the primary pass's) and its motion handed to Streamline, which copies them on `list`,
// or to FSR's prepare dispatch (the core's queue never waits on the presenter's). The list's descriptor heap, root
// signature and pipeline may be theirs afterwards. False when not handed over.
struct FrameGenFrame;
bool PresenterTagTraced(ID3D12GraphicsCommandList* list, const FrameGenFrame& traced);

} // namespace sert::rt
