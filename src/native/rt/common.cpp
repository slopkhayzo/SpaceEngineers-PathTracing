#include "common.h"

#include "../logger.h"

#include <cstdarg>
#include <utility>

namespace sert::rt {

std::wstring Format(const wchar_t* format, ...) {
    wchar_t buffer[1024];
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(buffer, _countof(buffer), _TRUNCATE, format, args);
    va_end(args);
    return buffer;
}

void Info(const std::wstring& text) {
    log::Write(log::Level::info, L"RT: " + text);
}

void Error(const std::wstring& text) {
    log::Write(log::Level::error, L"RT: " + text);
}

ComPtr<ID3D12Resource> CreateBuffer(ID3D12Device* device, const UINT64 size, const D3D12_HEAP_TYPE type,
                                    const D3D12_RESOURCE_FLAGS flags, const D3D12_RESOURCE_STATES state) {
    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = type;
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Width = size;
    desc.Height = 1;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = 1;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    desc.Flags = flags;
    ComPtr<ID3D12Resource> resource;
    if (FAILED(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, state, nullptr,
                                               IID_PPV_ARGS(&resource)))) {
        return nullptr;
    }
    return resource;
}

void Transition(ID3D12GraphicsCommandList* list, ID3D12Resource* resource, const D3D12_RESOURCE_STATES before,
                const D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = resource;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = before;
    barrier.Transition.StateAfter = after;
    list->ResourceBarrier(1, &barrier);
}

void UavBarrier(ID3D12GraphicsCommandList* list, ID3D12Resource* resource) {
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
    barrier.UAV.pResource = resource;
    list->ResourceBarrier(1, &barrier);
}

bool CreateSharedTexture(const Devices& devices, const UINT width, const UINT height, const DXGI_FORMAT format,
                         const bool d3d11Uav, const wchar_t* name, SharedTexture& texture) {
    SharedTexture fresh;
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    // The UAV bind flag is what gives D3D12 unordered access to the opened texture.
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    HRESULT hr = devices.d3d11->CreateTexture2D(&desc, nullptr, &fresh.texture11);
    if (SUCCEEDED(hr)) {
        hr = devices.d3d11->CreateShaderResourceView(fresh.texture11.Get(), nullptr, &fresh.srv11);
    }
    if (SUCCEEDED(hr) && d3d11Uav) {
        hr = devices.d3d11->CreateUnorderedAccessView(fresh.texture11.Get(), nullptr, &fresh.uav11);
    }
    ComPtr<IDXGIResource1> dxgi;
    HANDLE handle = nullptr;
    if (SUCCEEDED(hr)) {
        hr = fresh.texture11.As(&dxgi);
    }
    if (SUCCEEDED(hr)) {
        hr = dxgi->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr,
                                      &handle);
    }
    if (SUCCEEDED(hr)) {
        hr = devices.d3d12->OpenSharedHandle(handle, IID_PPV_ARGS(&fresh.texture12));
        CloseHandle(handle);
    }
    if (FAILED(hr)) {
        Error(Format(L"the shared %s texture %ux%u (format %d) could not be created (0x%08lX).", name, width, height,
                     static_cast<int>(format), Hr(hr)));
        return false;
    }
    fresh.width = width;
    fresh.height = height;
    fresh.format = format;
    texture = std::move(fresh);
    Info(Format(L"shared %s texture %ux%u (format %d) created.", name, width, height, static_cast<int>(format)));
    return true;
}

} // namespace sert::rt
