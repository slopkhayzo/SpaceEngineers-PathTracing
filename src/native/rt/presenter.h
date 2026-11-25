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

// After the core started (any thread): hooks IDXGISwapChain::Present of the game's swap chain `swapChain` (the
// chain on the game's window is the game's, also one it makes anew) and sets the mode. `streamline`: ours made
// through Streamline (framegen.h; its queue and swap chain through Streamline's proxies) when it loads.
bool PresenterInitialize(const Devices& devices, IDXGISwapChain* swapChain, PresenterMode mode, bool streamline);
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
// Render thread, on the core's command list once a traced frame's inputs are final (frame generation on): its
// hardware depth made from its linear depth (fg_ui.hlsl), then the frame's camera and its depth and motion handed
// to Streamline, which copies them on `list` (the core's queue never waits on the presenter's). `index`: the
// core's frame slot (its descriptors are free). The list's descriptor heap, root signature and pipeline are the
// pass's afterwards. False when not tagged.
struct FrameGenFrame;
bool PresenterTagTraced(ID3D12GraphicsCommandList* list, const FrameGenFrame& traced, UINT index);

} // namespace sert::rt
