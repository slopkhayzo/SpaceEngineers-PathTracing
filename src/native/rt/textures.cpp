#include "textures.h"

#include <algorithm>
#include <array>
#include <condition_variable>
#include <cstring>
#include <cwctype>
#include <deque>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <vector>

namespace sert::rt {
namespace {

constexpr std::size_t kUploadSlots = 3;  // one per frame in flight (rt_core.cpp)
constexpr UINT64 kUploadBytesPerSlot = 64ull << 20;
constexpr UINT64 kReadAheadBytes = 512ull << 20;  // file data waiting for upload
constexpr UINT kMaxMips = 16;
constexpr std::uint32_t kFallbackCount = static_cast<std::uint32_t>(TextureFallback::count);
constexpr int kMaxLoggedFailures = 20;
// A released mirror of a game texture is freed this many gates later, when no
// frame in flight samples it any more.
constexpr std::uint64_t kRetireGates = 8;

enum class TextureState { queued, loaded, resident, failed };

struct Texture {
    std::wstring path;
    bool srgb = false;
    TextureFallback fallback = TextureFallback::white;
    TextureState state = TextureState::queued;
    // Loaded: the file and what its header says.
    std::vector<std::uint8_t> file;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;  // resource format (typeless when an sRGB twin exists)
    DXGI_FORMAT viewFormat = DXGI_FORMAT_UNKNOWN;
    UINT width = 0;
    UINT height = 0;
    UINT mips = 0;
    std::size_t dataOffset = 0;
    UINT blockBytes = 0;  // bytes per 4x4 block, or per pixel for uncompressed formats
    bool compressed = false;
    // Resident.
    ComPtr<ID3D12Resource> resource;
    std::uint32_t descriptor = UINT32_MAX;
    // A texture the game renders into (TextureRequestDynamic): the game's
    // texture (a reference of ours) and the shared mirror D3D12 samples.
    bool dynamic = false;
    ID3D11Resource* sourceKey = nullptr;
    ComPtr<ID3D11Texture2D> source;
    ComPtr<ID3D11Texture2D> mirror11;
};

struct RetiredMirror {
    ComPtr<ID3D11Texture2D> source;
    ComPtr<ID3D11Texture2D> mirror11;
    ComPtr<ID3D12Resource> mirror12;
    std::uint64_t gate = 0;
};

struct Stats {
    UINT64 requested = 0;
    UINT64 resident = 0;
    UINT64 failed = 0;
    UINT64 uploadedBytes = 0;
    UINT64 dynamicCreated = 0;
    UINT64 dynamicReleased = 0;
    UINT64 copies = 0;  // mirror refreshes, summed over the gates
    UINT64 copiedBytes = 0;
    UINT64 gates = 0;
};

struct State {
    bool ready = false;
    Devices devices;
    D3D12_CPU_DESCRIPTOR_HANDLE descriptors{};
    UINT descriptorSize = 0;
    std::uint32_t nextDescriptor = kFallbackCount;
    ComPtr<ID3D12Resource> upload;
    std::uint8_t* uploadMapped = nullptr;
    std::array<ComPtr<ID3D12Resource>, kFallbackCount> fallbacks;
    bool fallbacksUploaded = false;

    std::mutex mutex;  // everything below
    std::condition_variable wake;
    bool stopping = false;
    std::vector<Texture> textures;
    std::unordered_map<std::wstring, std::uint32_t> byKey;
    std::unordered_map<ID3D11Resource*, std::uint32_t> dynamicBySource;
    std::vector<std::uint32_t> dynamicIds;  // live mirrors
    std::vector<RetiredMirror> retired;
    std::uint64_t gates = 0;
    std::deque<std::uint32_t> queue;   // to read
    std::deque<std::uint32_t> loaded;  // to upload
    UINT64 pendingBytes = 0;
    std::uint64_t generation = 0;
    Stats stats;
    int loggedFailures = 0;
};

State s;

// --- DDS --------------------------------------------------------------------

constexpr std::uint32_t FourCc(const char a, const char b, const char c, const char d) {
    return static_cast<std::uint32_t>(static_cast<std::uint8_t>(a)) |
           (static_cast<std::uint32_t>(static_cast<std::uint8_t>(b)) << 8) |
           (static_cast<std::uint32_t>(static_cast<std::uint8_t>(c)) << 16) |
           (static_cast<std::uint32_t>(static_cast<std::uint8_t>(d)) << 24);
}

struct DdsPixelFormat {
    std::uint32_t size, flags, fourCc, rgbBitCount, rMask, gMask, bMask, aMask;
};

struct DdsHeader {
    std::uint32_t size, flags, height, width, pitchOrLinearSize, depth, mipMapCount, reserved1[11];
    DdsPixelFormat pixelFormat;
    std::uint32_t caps, caps2, caps3, caps4, reserved2;
};
static_assert(sizeof(DdsHeader) == 124);

struct DdsHeaderDx10 {
    std::uint32_t dxgiFormat, resourceDimension, miscFlag, arraySize, miscFlags2;
};

constexpr std::uint32_t kDdsMagic = FourCc('D', 'D', 'S', ' ');
constexpr std::uint32_t kPixelFormatFourCc = 0x4;
constexpr std::uint32_t kPixelFormatRgb = 0x40;
constexpr std::uint32_t kPixelFormatLuminance = 0x20000;
constexpr std::uint32_t kCaps2Cubemap = 0x200;
constexpr std::uint32_t kCaps2Volume = 0x200000;

struct FormatInfo {
    DXGI_FORMAT resource;  // typeless when an sRGB view exists
    DXGI_FORMAT linear;
    DXGI_FORMAT srgb;
    UINT blockBytes;
    bool compressed;
};

bool Describe(const DXGI_FORMAT format, FormatInfo& info) {
    switch (format) {
    case DXGI_FORMAT_BC1_TYPELESS:
    case DXGI_FORMAT_BC1_UNORM:
    case DXGI_FORMAT_BC1_UNORM_SRGB:
        info = {DXGI_FORMAT_BC1_TYPELESS, DXGI_FORMAT_BC1_UNORM, DXGI_FORMAT_BC1_UNORM_SRGB, 8, true};
        return true;
    case DXGI_FORMAT_BC2_TYPELESS:
    case DXGI_FORMAT_BC2_UNORM:
    case DXGI_FORMAT_BC2_UNORM_SRGB:
        info = {DXGI_FORMAT_BC2_TYPELESS, DXGI_FORMAT_BC2_UNORM, DXGI_FORMAT_BC2_UNORM_SRGB, 16, true};
        return true;
    case DXGI_FORMAT_BC3_TYPELESS:
    case DXGI_FORMAT_BC3_UNORM:
    case DXGI_FORMAT_BC3_UNORM_SRGB:
        info = {DXGI_FORMAT_BC3_TYPELESS, DXGI_FORMAT_BC3_UNORM, DXGI_FORMAT_BC3_UNORM_SRGB, 16, true};
        return true;
    case DXGI_FORMAT_BC7_TYPELESS:
    case DXGI_FORMAT_BC7_UNORM:
    case DXGI_FORMAT_BC7_UNORM_SRGB:
        info = {DXGI_FORMAT_BC7_TYPELESS, DXGI_FORMAT_BC7_UNORM, DXGI_FORMAT_BC7_UNORM_SRGB, 16, true};
        return true;
    case DXGI_FORMAT_BC4_UNORM:
    case DXGI_FORMAT_BC4_SNORM:
        info = {format, format, format, 8, true};
        return true;
    case DXGI_FORMAT_BC5_UNORM:
    case DXGI_FORMAT_BC5_SNORM:
    case DXGI_FORMAT_BC6H_UF16:
    case DXGI_FORMAT_BC6H_SF16:
        info = {format, format, format, 16, true};
        return true;
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        info = {DXGI_FORMAT_R8G8B8A8_TYPELESS, DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, 4,
                false};
        return true;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        info = {DXGI_FORMAT_B8G8R8A8_TYPELESS, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB, 4,
                false};
        return true;
    case DXGI_FORMAT_R8_UNORM:
        info = {format, format, format, 1, false};
        return true;
    case DXGI_FORMAT_R8G8_UNORM:
        info = {format, format, format, 2, false};
        return true;
    case DXGI_FORMAT_R16G16B16A16_FLOAT:
        info = {format, format, format, 8, false};
        return true;
    default:
        return false;
    }
}

UINT64 MipBytes(const Texture& t, const UINT mip, UINT& rowBytes, UINT& rows) {
    const UINT w = std::max(1u, t.width >> mip);
    const UINT h = std::max(1u, t.height >> mip);
    if (t.compressed) {
        rowBytes = std::max(1u, (w + 3) / 4) * t.blockBytes;
        rows = std::max(1u, (h + 3) / 4);
    } else {
        rowBytes = w * t.blockBytes;
        rows = h;
    }
    return static_cast<UINT64>(rowBytes) * rows;
}

// Fills the header fields of `t` from its file; false with `error` set when
// the file is not a 2D texture this loader handles.
bool ParseDds(Texture& t, const wchar_t*& error) {
    const std::vector<std::uint8_t>& file = t.file;
    if (file.size() < 4 + sizeof(DdsHeader)) {
        error = L"is too small for a DDS file";
        return false;
    }
    std::uint32_t magic = 0;
    std::memcpy(&magic, file.data(), 4);
    DdsHeader header{};
    std::memcpy(&header, file.data() + 4, sizeof(header));
    if (magic != kDdsMagic || header.size != sizeof(DdsHeader)) {
        error = L"is not a DDS file";
        return false;
    }
    if ((header.caps2 & (kCaps2Cubemap | kCaps2Volume)) != 0) {
        error = L"is a cube map or volume texture";
        return false;
    }
    std::size_t offset = 4 + sizeof(DdsHeader);
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    const DdsPixelFormat& pf = header.pixelFormat;
    if ((pf.flags & kPixelFormatFourCc) != 0) {
        switch (pf.fourCc) {
        case FourCc('D', 'X', 'T', '1'):
            format = DXGI_FORMAT_BC1_UNORM;
            break;
        case FourCc('D', 'X', 'T', '2'):
        case FourCc('D', 'X', 'T', '3'):
            format = DXGI_FORMAT_BC2_UNORM;
            break;
        case FourCc('D', 'X', 'T', '4'):
        case FourCc('D', 'X', 'T', '5'):
            format = DXGI_FORMAT_BC3_UNORM;
            break;
        case FourCc('A', 'T', 'I', '1'):
        case FourCc('B', 'C', '4', 'U'):
            format = DXGI_FORMAT_BC4_UNORM;
            break;
        case FourCc('B', 'C', '4', 'S'):
            format = DXGI_FORMAT_BC4_SNORM;
            break;
        case FourCc('A', 'T', 'I', '2'):
        case FourCc('B', 'C', '5', 'U'):
            format = DXGI_FORMAT_BC5_UNORM;
            break;
        case FourCc('B', 'C', '5', 'S'):
            format = DXGI_FORMAT_BC5_SNORM;
            break;
        case FourCc('D', 'X', '1', '0'): {
            if (file.size() < offset + sizeof(DdsHeaderDx10)) {
                error = L"has a truncated DX10 header";
                return false;
            }
            DdsHeaderDx10 dx10{};
            std::memcpy(&dx10, file.data() + offset, sizeof(dx10));
            offset += sizeof(dx10);
            if (dx10.arraySize > 1 || (dx10.miscFlag & 0x4) != 0) {  // arrays, cube maps
                error = L"is a texture array or cube map";
                return false;
            }
            format = static_cast<DXGI_FORMAT>(dx10.dxgiFormat);
            break;
        }
        default:
            break;
        }
    } else if ((pf.flags & kPixelFormatRgb) != 0 && pf.rgbBitCount == 32) {
        if (pf.rMask == 0x000000FF && pf.gMask == 0x0000FF00 && pf.bMask == 0x00FF0000) {
            format = DXGI_FORMAT_R8G8B8A8_UNORM;
        } else if (pf.rMask == 0x00FF0000 && pf.gMask == 0x0000FF00 && pf.bMask == 0x000000FF) {
            format = DXGI_FORMAT_B8G8R8A8_UNORM;
        }
    } else if ((pf.flags & kPixelFormatLuminance) != 0 && pf.rgbBitCount == 8) {
        format = DXGI_FORMAT_R8_UNORM;
    }
    FormatInfo info{};
    if (!Describe(format, info)) {
        error = L"has a pixel format this loader does not read";
        return false;
    }
    t.format = info.resource;
    // The game views every file texture but its normal maps as sRGB, whatever
    // the header declares (MyFileTexture.Load: MakeSrgb unless the texture is
    // NORMALMAP_GLOSS; the decals' _cm files and some _add files are declared
    // BC7_UNORM): the caller's `srgb` is the texture's role. A header that
    // declares sRGB is sRGB in any case, as in the game.
    const bool declaredSrgb = info.srgb != info.linear && format == info.srgb;
    t.viewFormat = declaredSrgb || t.srgb ? info.srgb : info.linear;
    t.blockBytes = info.blockBytes;
    t.compressed = info.compressed;
    t.width = header.width;
    t.height = header.height;
    t.mips = std::clamp(header.mipMapCount, 1u, kMaxMips);
    t.dataOffset = offset;
    if (t.width == 0 || t.height == 0 || t.width > 16384 || t.height > 16384) {
        error = L"has an unusable size";
        return false;
    }
    UINT possibleMips = 1;
    for (UINT size = std::max(t.width, t.height); size > 1; size >>= 1) {
        ++possibleMips;
    }
    t.mips = std::min(t.mips, possibleMips);
    UINT64 total = 0;
    for (UINT mip = 0; mip < t.mips; ++mip) {
        UINT rowBytes = 0;
        UINT rows = 0;
        total += MipBytes(t, mip, rowBytes, rows);
    }
    if (offset + total > file.size()) {
        error = L"is shorter than its mip chain";
        return false;
    }
    return true;
}

bool ReadFileBytes(const std::wstring& path, std::vector<std::uint8_t>& bytes) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                              FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }
    LARGE_INTEGER size{};
    bool ok = GetFileSizeEx(file, &size) != 0 && size.QuadPart > 0 && size.QuadPart < (1ll << 31);
    if (ok) {
        bytes.resize(static_cast<std::size_t>(size.QuadPart));
        DWORD read = 0;
        ok = ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) != 0 &&
             read == bytes.size();
    }
    CloseHandle(file);
    return ok;
}

void LogFailure(const Texture& t, const wchar_t* reason) {
    if (s.loggedFailures < kMaxLoggedFailures) {
        ++s.loggedFailures;
        Error(Format(L"textures: %s %s; its fallback is used.", t.path.c_str(), reason));
    }
}

// --- Worker -----------------------------------------------------------------

void WorkerMain() {
    for (;;) {
        std::uint32_t id = 0;
        std::wstring path;
        {
            std::unique_lock lock(s.mutex);
            s.wake.wait(lock, [] { return s.stopping || (!s.queue.empty() && s.pendingBytes < kReadAheadBytes); });
            if (s.stopping) {
                return;
            }
            id = s.queue.front();
            s.queue.pop_front();
            path = s.textures[id].path;
        }
        std::vector<std::uint8_t> bytes;
        const bool read = ReadFileBytes(path, bytes);
        std::lock_guard lock(s.mutex);
        Texture& t = s.textures[id];
        const wchar_t* error = L"could not be read";
        if (read) {
            t.file = std::move(bytes);
            if (ParseDds(t, error)) {
                t.state = TextureState::loaded;
                s.pendingBytes += t.file.size();
                s.loaded.push_back(id);
                continue;
            }
            t.file.clear();
            t.file.shrink_to_fit();
        }
        t.state = TextureState::failed;
        ++s.stats.failed;
        LogFailure(t, error);
    }
}

// --- Upload -----------------------------------------------------------------

D3D12_RESOURCE_DESC TextureDesc(const DXGI_FORMAT format, const UINT width, const UINT height, const UINT mips) {
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = width;
    desc.Height = height;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = static_cast<UINT16>(mips);
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    return desc;
}

void WriteDescriptor(const std::uint32_t slot, ID3D12Resource* resource, const DXGI_FORMAT viewFormat,
                     const UINT mips) {
    D3D12_SHADER_RESOURCE_VIEW_DESC view{};
    view.Format = viewFormat;
    view.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
    view.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
    view.Texture2D.MipLevels = mips;
    D3D12_CPU_DESCRIPTOR_HANDLE handle = s.descriptors;
    handle.ptr += static_cast<SIZE_T>(slot) * s.descriptorSize;
    s.devices.d3d12->CreateShaderResourceView(resource, &view, handle);
}

// Copies `t`'s mips into the upload memory at `base` and records the copies;
// returns the bytes used, or 0 when they do not fit into `available`.
UINT64 RecordUpload(ID3D12GraphicsCommandList4* list, const Texture& t, ID3D12Resource* resource, UINT64 base,
                    UINT64 available) {
    const D3D12_RESOURCE_DESC desc = resource->GetDesc();
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT layouts[kMaxMips]{};
    UINT rows[kMaxMips]{};
    UINT64 rowSizes[kMaxMips]{};
    UINT64 total = 0;
    s.devices.d3d12->GetCopyableFootprints(&desc, 0, t.mips, 0, layouts, rows, rowSizes, &total);
    if (total > available) {
        return 0;
    }
    const std::uint8_t* source = t.file.data() + t.dataOffset;
    for (UINT mip = 0; mip < t.mips; ++mip) {
        UINT rowBytes = 0;
        UINT rowCount = 0;
        const UINT64 mipBytes = MipBytes(t, mip, rowBytes, rowCount);
        std::uint8_t* destination = s.uploadMapped + base + layouts[mip].Offset;
        for (UINT row = 0; row < rowCount; ++row) {
            std::memcpy(destination + static_cast<UINT64>(row) * layouts[mip].Footprint.RowPitch,
                        source + static_cast<UINT64>(row) * rowBytes, rowBytes);
        }
        source += mipBytes;
        D3D12_TEXTURE_COPY_LOCATION to{};
        to.pResource = resource;
        to.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        to.SubresourceIndex = mip;
        D3D12_TEXTURE_COPY_LOCATION from{};
        from.pResource = s.upload.Get();
        from.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        from.PlacedFootprint = layouts[mip];
        from.PlacedFootprint.Offset += base;
        list->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
    }
    return AlignUp(total, D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT);
}

// How D3D12 views a mirror of a game texture of `format` (typeless ones as UNORM).
DXGI_FORMAT MirrorViewFormat(const DXGI_FORMAT format) {
    switch (format) {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
        return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
        return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS:
        return DXGI_FORMAT_R16G16B16A16_FLOAT;
    default:
        return format;
    }
}

std::wstring Key(const std::wstring& path, const bool srgb) {
    std::wstring key = path;
    for (wchar_t& c : key) {
        c = c == L'/' ? L'\\' : static_cast<wchar_t>(std::towlower(c));
    }
    key += srgb ? L"|s" : L"|l";
    return key;
}

} // namespace

bool TexturesInitialize(const Devices& devices, const D3D12_CPU_DESCRIPTOR_HANDLE descriptors,
                        const UINT descriptorSize) {
    s.devices = devices;
    s.descriptors = descriptors;
    s.descriptorSize = descriptorSize;
    s.upload = CreateBuffer(devices.d3d12.Get(), kUploadBytesPerSlot * kUploadSlots, D3D12_HEAP_TYPE_UPLOAD,
                            D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
    const D3D12_RANGE none{0, 0};
    if (!s.upload || FAILED(s.upload->Map(0, &none, reinterpret_cast<void**>(&s.uploadMapped)))) {
        Error(L"textures: the upload memory could not be created.");
        return false;
    }
    // Every slot starts as a null view; the fallbacks get 1x1 textures,
    // filled by the first upload.
    D3D12_SHADER_RESOURCE_VIEW_DESC nullView{};
    nullView.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    nullView.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
    nullView.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
    nullView.Texture2D.MipLevels = 1;
    for (std::uint32_t slot = 0; slot < kMaxTextureDescriptors; ++slot) {
        D3D12_CPU_DESCRIPTOR_HANDLE handle = descriptors;
        handle.ptr += static_cast<SIZE_T>(slot) * descriptorSize;
        devices.d3d12->CreateShaderResourceView(nullptr, &nullView, handle);
    }
    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    for (std::uint32_t i = 0; i < kFallbackCount; ++i) {
        const D3D12_RESOURCE_DESC desc = TextureDesc(DXGI_FORMAT_R8G8B8A8_UNORM, 1, 1, 1);
        if (FAILED(devices.d3d12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
                                                          D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                                          IID_PPV_ARGS(&s.fallbacks[i])))) {
            Error(L"textures: the fallback textures could not be created.");
            return false;
        }
        WriteDescriptor(i, s.fallbacks[i].Get(), DXGI_FORMAT_R8G8B8A8_UNORM, 1);
    }
    // Detached: process exit ends it before static destruction, which a
    // joinable std::thread would turn into std::terminate.
    std::thread(WorkerMain).detach();
    s.ready = true;
    Info(Format(L"textures: ready (%u descriptors, %llu MB upload per gate).", kMaxTextureDescriptors,
                static_cast<unsigned long long>(kUploadBytesPerSlot >> 20)));
    return true;
}

void TexturesShutdown() {
    {
        std::lock_guard lock(s.mutex);
        s.stopping = true;
    }
    s.wake.notify_all();
}

std::uint32_t TextureRequest(const std::wstring& path, const bool srgb, const TextureFallback fallback) {
    if (!s.ready || path.empty()) {
        return UINT32_MAX;
    }
    const std::wstring key = Key(path, srgb);
    std::uint32_t id = 0;
    {
        std::lock_guard lock(s.mutex);
        const auto found = s.byKey.find(key);
        if (found != s.byKey.end()) {
            return found->second;
        }
        id = static_cast<std::uint32_t>(s.textures.size());
        Texture t;
        t.path = path;
        t.srgb = srgb;
        t.fallback = fallback;
        s.textures.push_back(std::move(t));
        s.byKey.emplace(key, id);
        s.queue.push_back(id);
        ++s.stats.requested;
    }
    s.wake.notify_one();
    return id;
}

std::uint32_t TextureRequestDynamic(ID3D11Resource* resource, const TextureFallback fallback) {
    if (!s.ready || resource == nullptr) {
        return UINT32_MAX;
    }
    {
        std::lock_guard lock(s.mutex);
        const auto found = s.dynamicBySource.find(resource);
        if (found != s.dynamicBySource.end()) {
            return found->second;
        }
    }
    Texture t;
    t.dynamic = true;
    t.fallback = fallback;
    t.state = TextureState::failed;
    t.sourceKey = resource;
    D3D11_TEXTURE2D_DESC sourceDesc{};
    HRESULT hr = resource->QueryInterface(IID_PPV_ARGS(&t.source));
    if (SUCCEEDED(hr)) {
        t.source->GetDesc(&sourceDesc);
        hr = sourceDesc.ArraySize == 1 && sourceDesc.SampleDesc.Count == 1 ? S_OK : E_INVALIDARG;
    }
    ComPtr<ID3D12Resource> mirror12;
    if (SUCCEEDED(hr)) {
        // The mirror: the first mip in the game's own format, shared with D3D12.
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = sourceDesc.Width;
        desc.Height = sourceDesc.Height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = sourceDesc.Format;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
        hr = s.devices.d3d11->CreateTexture2D(&desc, nullptr, &t.mirror11);
    }
    ComPtr<IDXGIResource1> dxgi;
    HANDLE handle = nullptr;
    if (SUCCEEDED(hr)) {
        hr = t.mirror11.As(&dxgi);
    }
    if (SUCCEEDED(hr)) {
        hr = dxgi->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr,
                                      &handle);
    }
    if (SUCCEEDED(hr)) {
        hr = s.devices.d3d12->OpenSharedHandle(handle, IID_PPV_ARGS(&mirror12));
        CloseHandle(handle);
    }
    std::lock_guard lock(s.mutex);
    const auto id = static_cast<std::uint32_t>(s.textures.size());
    t.width = sourceDesc.Width;
    t.height = sourceDesc.Height;
    t.format = sourceDesc.Format;
    t.viewFormat = MirrorViewFormat(sourceDesc.Format);
    t.mips = 1;
    if (SUCCEEDED(hr) && s.nextDescriptor < kMaxTextureDescriptors) {
        t.descriptor = s.nextDescriptor++;
        WriteDescriptor(t.descriptor, mirror12.Get(), t.viewFormat, 1);
        t.resource = std::move(mirror12);
        t.state = TextureState::resident;
        s.dynamicIds.push_back(id);
        ++s.stats.dynamicCreated;
        ++s.generation;
    } else {
        t.source.Reset();
        t.mirror11.Reset();
        ++s.stats.failed;
        if (s.loggedFailures < kMaxLoggedFailures) {
            ++s.loggedFailures;
            Error(Format(L"textures: a game-rendered texture (%ux%u, format %d) cannot be mirrored (0x%08lX).",
                         sourceDesc.Width, sourceDesc.Height, static_cast<int>(sourceDesc.Format), Hr(hr)));
        }
    }
    s.textures.push_back(std::move(t));
    s.dynamicBySource.emplace(resource, id);
    return id;
}

void TexturesCopyIn(ID3D11DeviceContext* immediate) {
    if (!s.ready || immediate == nullptr) {
        return;
    }
    std::lock_guard lock(s.mutex);
    ++s.gates;
    ++s.stats.gates;
    std::erase_if(s.retired, [](const RetiredMirror& r) { return r.gate + kRetireGates <= s.gates; });
    bool released = false;
    for (std::size_t i = 0; i < s.dynamicIds.size();) {
        Texture& t = s.textures[s.dynamicIds[i]];
        // Only our reference left: the game has released its texture.
        t.source->AddRef();
        if (t.source->Release() <= 1) {
            s.retired.push_back({std::move(t.source), std::move(t.mirror11), std::move(t.resource), s.gates});
            s.dynamicBySource.erase(t.sourceKey);
            t.state = TextureState::failed;  // the fallback from now on
            s.dynamicIds[i] = s.dynamicIds.back();
            s.dynamicIds.pop_back();
            ++s.stats.dynamicReleased;
            released = true;
            continue;
        }
        immediate->CopySubresourceRegion(t.mirror11.Get(), 0, 0, 0, 0, t.source.Get(), 0, nullptr);
        ++s.stats.copies;
        s.stats.copiedBytes += static_cast<UINT64>(t.width) * t.height * 4;
        ++i;
    }
    if (released) {
        ++s.generation;
    }
}

std::uint32_t TextureDescriptor(const std::uint32_t id) {
    std::lock_guard lock(s.mutex);
    if (id >= s.textures.size()) {
        return static_cast<std::uint32_t>(TextureFallback::white);
    }
    const Texture& t = s.textures[id];
    return t.state == TextureState::resident ? t.descriptor : static_cast<std::uint32_t>(t.fallback);
}

std::uint64_t TexturesGeneration() {
    std::lock_guard lock(s.mutex);
    return s.generation;
}

void TexturesRecord(ID3D12GraphicsCommandList4* list, const std::size_t slot) {
    if (!s.ready || slot >= kUploadSlots) {
        return;
    }
    const UINT64 base = slot * kUploadBytesPerSlot;
    UINT64 used = 0;
    if (!s.fallbacksUploaded) {
        // RGBA8, little-endian: white, flat normal with gloss 0, black, extensions (AO 1).
        const std::uint32_t texels[kFallbackCount] = {0xFFFFFFFFu, 0x00FF8080u, 0x00000000u, 0x000000FFu};
        for (std::uint32_t i = 0; i < kFallbackCount; ++i) {
            const UINT64 offset = AlignUp(used, D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT);
            std::memcpy(s.uploadMapped + base + offset, &texels[i], 4);
            D3D12_TEXTURE_COPY_LOCATION to{};
            to.pResource = s.fallbacks[i].Get();
            to.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            D3D12_TEXTURE_COPY_LOCATION from{};
            from.pResource = s.upload.Get();
            from.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            from.PlacedFootprint.Offset = base + offset;
            from.PlacedFootprint.Footprint = {DXGI_FORMAT_R8G8B8A8_UNORM, 1, 1, 1,
                                              D3D12_TEXTURE_DATA_PITCH_ALIGNMENT};
            list->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
            Transition(list, s.fallbacks[i].Get(), D3D12_RESOURCE_STATE_COPY_DEST,
                       D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            used = offset + D3D12_TEXTURE_DATA_PITCH_ALIGNMENT;
        }
        s.fallbacksUploaded = true;
    }
    std::unique_lock lock(s.mutex);
    bool freed = false;
    while (!s.loaded.empty() && s.nextDescriptor < kMaxTextureDescriptors) {
        const std::uint32_t id = s.loaded.front();
        Texture& t = s.textures[id];
        const D3D12_RESOURCE_DESC desc = TextureDesc(t.format, t.width, t.height, t.mips);
        D3D12_HEAP_PROPERTIES heap{};
        heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        ComPtr<ID3D12Resource> resource;
        if (FAILED(s.devices.d3d12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
                                                            D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                                            IID_PPV_ARGS(&resource)))) {
            t.state = TextureState::failed;
            ++s.stats.failed;
            LogFailure(t, L"could not be created on the GPU");
        } else {
            const UINT64 offset = AlignUp(used, D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT);
            const UINT64 bytes = RecordUpload(list, t, resource.Get(), base + offset, kUploadBytesPerSlot - offset);
            if (bytes == 0) {
                if (used == 0) {
                    t.state = TextureState::failed;  // larger than a whole gate's upload memory
                    ++s.stats.failed;
                    LogFailure(t, L"is larger than one gate's upload memory");
                } else {
                    break;  // the next gate
                }
            } else {
                Transition(list, resource.Get(), D3D12_RESOURCE_STATE_COPY_DEST,
                           D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE |
                               D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
                t.descriptor = s.nextDescriptor++;
                WriteDescriptor(t.descriptor, resource.Get(), t.viewFormat, t.mips);
                t.resource = std::move(resource);
                t.state = TextureState::resident;
                used = offset + bytes;
                ++s.stats.resident;
                s.stats.uploadedBytes += bytes;
                ++s.generation;
            }
        }
        s.pendingBytes -= t.file.size();
        t.file.clear();
        t.file.shrink_to_fit();
        s.loaded.pop_front();
        freed = true;
    }
    lock.unlock();
    if (freed) {
        s.wake.notify_one();  // read-ahead room
    }
}

void TexturesReport() {
    if (!s.ready) {
        return;
    }
    std::lock_guard lock(s.mutex);
    const double gates = s.stats.gates != 0 ? static_cast<double>(s.stats.gates) : 1.0;
    Info(Format(L"textures: %zu known, %u descriptors used; this window %llu requested, %llu resident, %llu "
                L"failed, %llu MB uploaded; %zu waiting to be read, %zu to be uploaded (%llu MB); game-rendered "
                L"textures (screens): %zu mirrored, %llu new, %llu released, %.1f copies and %.2f MB per gate.",
                s.textures.size(), s.nextDescriptor, static_cast<unsigned long long>(s.stats.requested),
                static_cast<unsigned long long>(s.stats.resident), static_cast<unsigned long long>(s.stats.failed),
                static_cast<unsigned long long>(s.stats.uploadedBytes >> 20), s.queue.size(), s.loaded.size(),
                static_cast<unsigned long long>(s.pendingBytes >> 20), s.dynamicIds.size(),
                static_cast<unsigned long long>(s.stats.dynamicCreated),
                static_cast<unsigned long long>(s.stats.dynamicReleased),
                static_cast<double>(s.stats.copies) / gates,
                static_cast<double>(s.stats.copiedBytes) / gates / (1024.0 * 1024.0)));
    s.stats = Stats{};
}

} // namespace sert::rt
