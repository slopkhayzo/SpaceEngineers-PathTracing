#pragma once

#include "common.h"

#include <cstddef>
#include <cstdint>
#include <string>

namespace sert::rt {

// Material textures for traced hits (research/docs/space-engineers-rt-plan.md
// §11 P1): the game's DDS files read again by the plugin (the game's
// block-compressed, mipped textures cannot be shared from D3D11, §4), loaded
// on a worker thread and uploaded inside the gate's D3D12 window into a
// bindless table of shader resource views.
//
// A texture's descriptor slot is written once, when the texture becomes
// resident, and never changed while it lives: until then (and when loading
// fails) users point at a fallback slot. TextureDescriptor() tells which slot
// to use now; TexturesGeneration() changes whenever any texture became
// resident, so users know when to look again.

constexpr std::uint32_t kMaxTextureDescriptors = 16384;

// Fallback slots, written at initialization: the game's own defaults for an
// empty or unloaded material slot (MyFileTextureManager.CreateAllTextures).
enum class TextureFallback : std::uint32_t {
    white = 0,       // 1, 1, 1, 1: alpha masks (fully opaque)
    normal = 1,      // 0.5, 0.5, 1, 0: a flat normal map, gloss 0
    black = 2,       // 0, 0, 0, 0: color-metal maps
    extensions = 3,  // 1, 0, 0, 0: AO 1, no emissive, no paint
    count = 4,
};

// `descriptors`: kMaxTextureDescriptors CBV/SRV/UAV descriptors of the
// shader-visible heap the trace passes bind (Texture2D table).
[[nodiscard]] bool TexturesInitialize(const Devices& devices, D3D12_CPU_DESCRIPTOR_HANDLE descriptors,
                                      UINT descriptorSize);
void TexturesShutdown();

// Any thread. The texture at `path` (absolute), viewed as sRGB when `srgb`;
// repeated requests return the same id. Loading starts at once.
[[nodiscard]] std::uint32_t TextureRequest(const std::wstring& path, bool srgb, TextureFallback fallback);

// Render thread. A texture the game renders into (an LCD screen's content,
// MyUserGeneratedTexture): mirrored by a copy of its first mip into a shared
// texture at every gate (TexturesCopyIn); repeated requests for the same
// texture return the same id. The mirror holds a reference to the game's
// texture and lets it go once the game has released it.
[[nodiscard]] std::uint32_t TextureRequestDynamic(ID3D11Resource* texture, TextureFallback fallback);

// Render thread, at the gate on the immediate context before the ready
// signal: refreshes the mirrors of the game's textures. D3D12 reads them
// through implicit promotion from COMMON, so they need no barriers.
void TexturesCopyIn(ID3D11DeviceContext* immediate);


// The descriptor slot to sample texture `id` through now: its own once
// resident, its fallback before that or after a failure.
[[nodiscard]] std::uint32_t TextureDescriptor(std::uint32_t id);
[[nodiscard]] std::uint64_t TexturesGeneration();

// Render thread, inside the gate's D3D12 window: uploads textures that
// finished loading, up to a byte budget per gate. `slot` selects the frame's
// upload memory (free when called).
void TexturesRecord(ID3D12GraphicsCommandList4* list, std::size_t slot);

// Statistics since the last report, into the log.
void TexturesReport();

} // namespace sert::rt
