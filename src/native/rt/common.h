#pragma once

#include <Windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <wrl/client.h>

#include <cstdint>
#include <string>

namespace sert::rt {

using Microsoft::WRL::ComPtr;

[[nodiscard]] std::wstring Format(const wchar_t* format, ...);
void Info(const std::wstring& text);
void Error(const std::wstring& text);
[[nodiscard]] inline unsigned long Hr(const HRESULT hr) {
    return static_cast<unsigned long>(hr);
}

// The game's D3D11 device and the plugin's D3D12 device on the same adapter.
struct Devices {
    ComPtr<ID3D11Device5> d3d11;
    ComPtr<ID3D11DeviceContext> immediate;
    ComPtr<ID3D11DeviceContext4> immediate4;
    ComPtr<ID3D12Device> d3d12;
    ComPtr<ID3D12Device5> d3d12dxr;  // the same device (DXR entry points)
    ComPtr<ID3D12CommandQueue> queue;
};

[[nodiscard]] constexpr UINT64 AlignUp(const UINT64 value, const UINT64 alignment) {
    return (value + alignment - 1) / alignment * alignment;
}

// A committed buffer, or null.
[[nodiscard]] ComPtr<ID3D12Resource> CreateBuffer(ID3D12Device* device, UINT64 size, D3D12_HEAP_TYPE type,
                                                  D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES state);

void Transition(ID3D12GraphicsCommandList* list, ID3D12Resource* resource, D3D12_RESOURCE_STATES before,
                D3D12_RESOURCE_STATES after);
void UavBarrier(ID3D12GraphicsCommandList* list, ID3D12Resource* resource);  // null: every UAV access

// A texture created by D3D11 with an NT handle and opened in D3D12: the
// sharing direction that works (research/docs/space-engineers-rt-plan.md, §4).
struct SharedTexture {
    ComPtr<ID3D11Texture2D> texture11;
    ComPtr<ID3D11ShaderResourceView> srv11;
    ComPtr<ID3D11UnorderedAccessView> uav11;
    ComPtr<ID3D12Resource> texture12;
    UINT width = 0;
    UINT height = 0;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;

    [[nodiscard]] bool Matches(const UINT w, const UINT h, const DXGI_FORMAT f) const {
        return texture12 && width == w && height == h && format == f;
    }
};

// SRV and UAV in D3D11 (the UAV only with `d3d11Uav`); D3D12 always gets UAV access.
[[nodiscard]] bool CreateSharedTexture(const Devices& devices, UINT width, UINT height, DXGI_FORMAT format,
                                       bool d3d11Uav, const wchar_t* name, SharedTexture& texture);

// sert::RtFrame from the managed side: the camera of the frame being drawn,
// read from MyRender11.Environment.Matrices (VRageMath matrices: row-major,
// row vectors, camera-relative "At0" forms). Mirrored in NativeApi.cs.
struct RtFrame {
    std::uint32_t size;
    std::uint32_t width;   // the game's render resolution
    std::uint32_t height;
    std::uint32_t reserved;
    double cameraPosition[3];
    double reserved2;
    float viewAt0[16];
    float invViewAt0[16];
    float projection[16];
    float invProjection[16];
    float viewProjectionAt0[16];
    float invViewProjectionAt0[16];
    // MyRender11.Environment.Data.EnvironmentLight: the sun's light travels
    // along sunDirection (shaders light with -directionalLightVec).
    float sunDirection[3];
    float sunDiffuse;   // SunDiffuseFactor
    float sunColor[3];  // SunColorRaw
    float reserved3;
    // MyRender11.Environment.Data.TextureMultipliers, applied to every material
    // the G-buffer receives (ApplyMultipliers): albedo, metalness, gloss, AO,
    // emissive and color-mask multipliers, the same six shifts, then the
    // ColorizeHSV paint offset.
    float multipliers[16];
    // MyCommon.FrameConstantsData, the game's cbuffer Frame (src/shaders/game_frame.hlsli):
    // copied by SetFrame when gameFrameSize is kGameFrameBytes.
    std::uint64_t gameFrame;
    std::uint32_t gameFrameSize;
    std::uint32_t reserved4;
};

static_assert(sizeof(RtFrame) == 544);
constexpr std::uint32_t kGameFrameBytes = 1280;

} // namespace sert::rt
