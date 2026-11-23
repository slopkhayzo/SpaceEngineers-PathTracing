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
};

// What denoises the lit view's traced light.
enum class Denoiser : int {
    automatic = 0,          // DLSS Ray Reconstruction where available (NVIDIA RTX), else our filter
    rayReconstruction = 1,  // DLSS Ray Reconstruction (our filter when it is unavailable)
    filter = 2,             // our filter (denoise.hlsl)
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
