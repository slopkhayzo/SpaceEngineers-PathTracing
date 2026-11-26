#pragma once

#include <cstdint>
#include <string>

struct ID3D11CommandList;
struct ID3D11Resource;
struct ID3D11Device;
struct ID3D11DeviceContext;

namespace sert::rt {

struct RtFrame;

// What the gate does each frame (research/docs/space-engineers-rt-plan.md, §5.3).
enum class GateMode : int {
    off = 0,        // no cross-device work; the lighting replays untouched
    roundTrip = 1,  // D3D11 signal -> D3D12 wait/signal -> D3D11 wait, no work
    overlay = 2,    // the D3D12 debug view is added by the patched sun pass
    replace = 3,    // the D3D12 debug view replaces the lit frame (the pure
                    // path tracing output path)
};

// What the D3D12 debug view shows (src/shaders/debug_view.hlsl; the traced
// views are src/shaders/primary_view.hlsl through the scene mirror).
enum class DebugView : int {
    stripes = 0,
    normals = 1,
    baseColor = 2,
    worldGrid = 3,
    reprojection = 4,
    parts = 5,     // primary rays: a color per geometry record
    mismatch = 6,  // primary-ray depth against the G-buffer's
    faces = 7,     // front and back faces by DXR's winding rule
    clay = 8,      // traced sun shadows and sky occlusion on white surfaces
    material = 9,         // the traced base color (textures, paint)
    materialCompare = 10, // the traced material against the G-buffer's
    paths = 11,           // how each hit object reaches the mirror (models, cube blocks, ...)
    lit = 12,             // traced light: emissive, the sun, emissive surfaces as lights, one diffuse and one
                          // specular bounce (P2), denoised
    litRaw = 13,          // the same before the spatial filter (temporal accumulation only)
    emitters = 14,        // the light of emissive surfaces alone (accumulated, not filtered)
    motion = 15,          // each pixel's motion since the previous frame (objects' and the camera's)
    litRR = 16,           // traced light denoised by DLSS Ray Reconstruction (when available)
    litFilter = 17,       // traced light denoised by our filter (src/shaders/denoise.hlsl)
    emittersReference = 18,  // the emitters view without reusing light samples (the new candidates alone,
                             // unbiased): what the emitters view must match on average
    emittersPowerGrid = 19,  // the emitters view with P2p's light grid (its lights drawn by power alone, no
                             // jittered cells): the cells' lists' A/B
    litFsr = 20,             // traced light denoised by our filter, then AMD FSR Upscaling (in replace mode; when
                             // available)
};

// What denoises the lit view's traced light.
enum class Denoiser : int {
    automatic = 0,          // DLSS Ray Reconstruction where available (NVIDIA RTX), else our filter
    rayReconstruction = 1,  // DLSS Ray Reconstruction (our filter when it is unavailable)
    filter = 2,             // our filter (denoise.hlsl)
};

// The upscaling mode (the UpscalingMode setting; DlssMode before FSR): the size the lit view's light is traced at,
// which Ray Reconstruction, or FSR after our filter, upscales to the game's (NVIDIA's render sizes for each mode:
// dlss::RenderSize; AMD's: fsr::RenderSize). Only for the lit view in replace mode; our filter alone and the overlay
// composite trace at the game's size.
enum class DlssMode : int {
    dlaa = 0,              // the game's size (DLAA; FSR's native AA)
    quality = 1,           // about 67 % per axis
    balanced = 2,          // about 58 %
    performance = 3,       // 50 %
    ultraPerformance = 4,  // about 33 %
};

// What upscales the light our filter denoised (Ray Reconstruction upscales by itself).
enum class Upscaler : int {
    automatic = 0,  // AMD FSR where available
    fsr = 1,        // AMD FSR (our filter alone when it is unavailable)
    off = 2,        // none: our filter at the game's size
};

struct Options {
    GateMode gateMode = GateMode::overlay;
    DebugView debugView = DebugView::stripes;
    float patternStrength = 0.5f;
    std::wstring tracePath;  // directory for gate.csv, empty = no trace
    Denoiser denoiser = Denoiser::automatic;
    bool characterLights = true;  // characters' glowing suit parts are lights (else they only glow)
    float lampIntensity = 1.0f;   // the lamps' light: traced (scene.cpp UploadLamps), the game's beyond the traced area
    float emissiveIntensity = 1.0f;  // the light emissive surfaces cast (lights.hlsli Emission), not their own glow
    bool decalLights = true;  // glowing decals are lights (else they only glow; SetDecalLights changes it)
    bool lightTree = false;   // lamps and emitters traced at any distance (the grid's coarse levels, the tree)
    DlssMode dlssMode = DlssMode::dlaa;  // the upscaling mode (SetDlssMode changes it)
    Upscaler upscaler = Upscaler::automatic;  // what upscales our filter's light (Option::upscaler)
    float fsrSharpness = 0.0f;  // FSR's sharpening, 0 (off) to 1 (Option::fsrSharpness)
};

// Creates the D3D12 device on the adapter of the game's D3D11 device, the
// shared fence and the pipelines, and logs what the game's device offers.
// Returns 0, or the number of the step that failed (logged).
[[nodiscard]] int Initialize(ID3D11Device* gameDevice, const Options& options);

// Hooks ID3D11DeviceContext::ExecuteCommandList. Call on the render thread,
// between two frames.
[[nodiscard]] bool InstallGate();

// The command list MyGBufferResolver.DoWork recorded this frame (lighting),
// and the camera of that frame. The list's replay is the gate.
void SetLightingList(ID3D11CommandList* list);
void SetFrame(const RtFrame& frame);

// Before the sun/ambient draw is recorded on its deferred context: binds the
// RT result textures (MyLightsRendering.RenderDirectionalEnvironmentLight).
void BindLighting(ID3D11DeviceContext* context);

// After it was recorded: notes the G-buffer views and the lit HDR target the
// draw used (sizes the shared textures), and logs all bindings once.
void SunPassRecorded(ID3D11DeviceContext* context);

void SetGateMode(GateMode mode);
void SetDebugView(DebugView view);
// Any thread. Whether glowing decals light their surroundings, from the next
// frame on (the DecalLights setting; the inspector's A/B); starts a fresh
// statistics window.
void SetDecalLights(bool on);
// Any thread. Whether the lit views trace lamps and emitters at any distance
// (the light grid's coarse levels beyond its fine one, filled from the light
// tree; P2z) or within the fine level (32 m around the camera), from the next
// frame on (the LightTree setting; the inspector's A/B); starts a fresh
// statistics window.
void SetLightTree(bool on);
// Any thread. The DLSS mode of Ray Reconstruction (the DlssMode setting; the
// inspector's A/B), from the next frame on; starts a fresh statistics window.
void SetDlssMode(DlssMode mode);

// The settings the game's options screen (the plugin's "Path tracing" screen) changes while playing, besides the
// gate mode, the DLSS mode, the light tree and the decals' light, which have their own calls.
enum class Option : int {
    denoiser = 0,           // value: Denoiser
    characterLights = 1,    // 0 or 1
    lampIntensity = 2,      // 0 to 100: the lamps' light, traced and the game's own
    emissiveIntensity = 3,  // 0 to 100: the light emissive surfaces cast
    handOffOrder = 4,       // diagnostics: 0 the D3D12 commands recorded after the hand-off to D3D12, 1 before it
                            // (the default: the driver submits the game's work meanwhile), 2 by turns per report
                            // window
    frameLimit = 5,         // frame generation: Reflex's frame limit in frames per second (0: none)
    presentSync = 6,        // frame generation: our presents' sync interval (-1: the game's, 0, 1)
    reflexMode = 7,         // frame generation: Reflex's mode (0 off, 1 low latency: the default, 2 with boost)
    sampleThreads = 8,      // diagnostics: the thread sampler (sampler.h) on (1) or off (0)
    upscaler = 9,           // value: Upscaler
    fsrSharpness = 10,      // 0 (off) to 1: FSR's sharpening
    fsrDebugView = 11,      // diagnostics: FSR's debug view in the output (1) or not (0)
    last = fsrDebugView,
};
// Any thread. One of the settings above, from the next frame on; starts a fresh statistics window.
void SetOption(Option option, float value);

// Whether AMD FSR can upscale: 0 yes, 1 not known yet (it loads with the first frame that would use it), 2 no.
// `text` gets the versions it offers, or why not.
int FsrStatus(std::wstring& text);

// Whether the frame generation the presenter was last set to (DLSS-G through Streamline, or AMD FSR's) can run: 0
// yes, 1 not known yet, 2 no. `text` gets why not, or when it can, anything that hurts it.
int FrameGenerationStatus(std::wstring& text);

// After Initialize: the presenter (presenter.h) on the game's swap chain `swapChain` (an IDXGISwapChain) in `mode`:
// 0 the game's chain presents, 1 ours, 2 ours through Streamline (DLSS-G off), 3 DLSS-G on, 4 ours as AMD FSR's
// frame generation swap chain with its generation on, 5 that with it off, 6 frame generation as the GPU allows
// (DLSS-G on NVIDIA GPUs that run it, else FSR's). `window` (if not null) gets the game's window. False when it could
// not be set up.
bool SetPresenter(void* swapChain, int mode, void** window);
// On the thread of the game's window, after SetPresenter: the presenter's window made (PresenterAttachWindow).
bool AttachPresenterWindow();
// Render thread, once the game drew its scene: the frame before the HUD copied for frame generation.
void CaptureHudless();

// Up to 8 probe pixels (x | y << 16): the material views log what their rays
// found there (primary_view.hlsl) with each statistics window. 0 = none.
void SetProbes(const std::uint32_t* pixels, std::uint32_t count);

// Any thread. The game's environment probe cubemaps (MyEnvironmentProbe
// FarCubemapFinal, CloseCubemapFinal; null: none): converted at every gate
// into 2D atlases of their faces the traced passes sample (t9, t10).
void SetEnvironment(ID3D11Resource* farProbe, ID3D11Resource* closeProbe);

// Stops all cross-device work; the hook stays and passes calls through.
void Shutdown();

} // namespace sert::rt
