#include "validation.h"

#include "common.h"

#include <d3d12sdklayers.h>

#include <algorithm>
#include <cwctype>
#include <iterator>
#include <map>
#include <string_view>
#include <vector>

namespace sert::rt {
namespace {

constexpr unsigned kLoggedPerId = 3;  // each message id logged this often, then counted

struct State {
    int mode = 0;
    ComPtr<ID3D12InfoQueue> queue;  // the layer's (read here at each report when no callback could be registered)
    bool callback = false;
    DWORD cookie = 0;
    SRWLOCK lock = SRWLOCK_INIT;   // guards the counts (the layer calls back on the thread of the faulty call)
    std::map<int, unsigned> seen;  // message id -> times seen (this session)
    unsigned counts[3]{};          // corruption, error, warning since the last report
    unsigned long long total = 0;  // this session
};

State v;

std::wstring Widen(const char* text) {
    if (text == nullptr || *text == '\0') {
        return {};
    }
    const int length = MultiByteToWideChar(CP_UTF8, 0, text, -1, nullptr, 0);
    if (length <= 1) {
        return {};
    }
    std::wstring wide(static_cast<std::size_t>(length - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text, -1, wide.data(), length);
    return wide;
}

const wchar_t* SeverityName(const D3D12_MESSAGE_SEVERITY severity) {
    switch (severity) {
    case D3D12_MESSAGE_SEVERITY_CORRUPTION:
        return L"corruption";
    case D3D12_MESSAGE_SEVERITY_ERROR:
        return L"error";
    case D3D12_MESSAGE_SEVERITY_WARNING:
        return L"warning";
    default:
        return L"information";
    }
}

void Record(const D3D12_MESSAGE_SEVERITY severity, const D3D12_MESSAGE_ID id, const char* description) {
    if (severity != D3D12_MESSAGE_SEVERITY_CORRUPTION && severity != D3D12_MESSAGE_SEVERITY_ERROR &&
        severity != D3D12_MESSAGE_SEVERITY_WARNING) {
        return;
    }
    // The layer's list of the objects still alive as the game's process ends (the game exits without releasing the
    // plugin's device): not a finding.
    const std::string_view message = description != nullptr ? description : "";
    if (id == static_cast<D3D12_MESSAGE_ID>(0) &&
        (message.rfind("Live ", 0) == 0 || message.find("Live Object") != std::string_view::npos ||
         message.find("Process is terminating") != std::string_view::npos)) {
        return;
    }
    AcquireSRWLockExclusive(&v.lock);
    const unsigned times = ++v.seen[static_cast<int>(id)];
    ++v.counts[static_cast<int>(severity)];
    ++v.total;
    ReleaseSRWLockExclusive(&v.lock);
    if (times > kLoggedPerId) {
        return;
    }
    const std::wstring text = Format(L"D3D12 validation %s (id %d%s): ", SeverityName(severity), static_cast<int>(id),
                                     times == kLoggedPerId ? L"; its later ones only counted" : L"") +
                              Widen(description);
    if (severity == D3D12_MESSAGE_SEVERITY_WARNING) {
        Info(text);
    } else {
        Error(text);
    }
}

void __stdcall OnMessage(D3D12_MESSAGE_CATEGORY, const D3D12_MESSAGE_SEVERITY severity, const D3D12_MESSAGE_ID id,
                         const LPCSTR description, void*) {
    Record(severity, id, description);
}

} // namespace

int ValidationMode(const std::wstring& iniPath) {
    wchar_t value[32]{};
    GetPrivateProfileStringW(L"Debug", L"D3D12Validation", L"", value, 32, iniPath.c_str());
    std::wstring text = value;
    const std::size_t comment = text.find(L';');
    if (comment != std::wstring::npos) {
        text.resize(comment);
    }
    std::wstring word;
    for (const wchar_t c : text) {
        if (!std::iswspace(c)) {
            word.push_back(static_cast<wchar_t>(std::towlower(c)));
        }
    }
    if (word == L"gpu-full") {
        return 3;
    }
    if (word == L"gpu") {
        return 2;
    }
    return word == L"on" || word == L"true" || word == L"1" ? 1 : 0;
}

bool ValidationEnable(const int mode) {
    v.mode = mode;
    if (mode <= 0) {
        return true;
    }
    bool layer = false;
    ComPtr<ID3D12Debug> debug;
    if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug)))) {
        debug->EnableDebugLayer();
        layer = true;
        ComPtr<ID3D12Debug1> debug1;
        if (mode >= 2 && SUCCEEDED(debug.As(&debug1))) {
            debug1->SetEnableGPUBasedValidation(TRUE);
        }
    }
    // DRED (d3d12.dll itself, no layer needed): what each command list did last, and page faults, after a removal.
    ComPtr<ID3D12DeviceRemovedExtendedDataSettings> dred;
    const bool dredOn = SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&dred)));
    if (dredOn) {
        dred->SetAutoBreadcrumbsEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
        dred->SetPageFaultEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
    }
    Info(Format(L"D3D12 validation ([Debug] D3D12Validation): %s; DRED breadcrumbs and page faults %s. Expect "
                L"lower frame rates.",
                !layer       ? L"the debug layer is not installed (Windows' optional feature \"Graphics Tools\")"
                : mode >= 3 ? L"the debug layer with GPU-based validation of every shader access (very slow)"
                : mode >= 2 ? L"the debug layer with GPU-based validation of resource states"
                            : L"the debug layer",
                dredOn ? L"on" : L"unavailable"));
    return layer;
}

void ValidationAttach(ID3D12Device* device) {
    if (v.mode <= 0 || device == nullptr) {
        return;
    }
    ComPtr<ID3D12InfoQueue> queue;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&queue)))) {
        return;  // the layer did not load (logged by ValidationEnable)
    }
    // GPU-based validation of resource states only (gpu): every shader instrumented (gpu-full) took RedShip's first
    // traced frames minutes, the game hanging (f4f).
    ComPtr<ID3D12DebugDevice1> debugDevice;
    if (v.mode == 2 && SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&debugDevice)))) {
        D3D12_DEBUG_DEVICE_GPU_BASED_VALIDATION_SETTINGS settings{};
        settings.MaxMessagesPerCommandList = 16;
        settings.DefaultShaderPatchMode = D3D12_GPU_BASED_VALIDATION_SHADER_PATCH_MODE_STATE_TRACKING_ONLY;
        settings.PipelineStateCreateFlags = D3D12_GPU_BASED_VALIDATION_PIPELINE_STATE_CREATE_FLAG_NONE;
        debugDevice->SetDebugParameter(D3D12_DEBUG_DEVICE_PARAMETER_GPU_BASED_VALIDATION_SETTINGS, &settings,
                                       sizeof(settings));
    }
    // Warnings and worse only; nothing breaks into a debugger. Left out: "Ignoring InitialState COPY_DEST, buffers are
    // created in COMMON" (1328): readback buffers keep COPY_DEST, which Windows 10's runtime requires for them.
    D3D12_MESSAGE_SEVERITY denied[] = {D3D12_MESSAGE_SEVERITY_INFO, D3D12_MESSAGE_SEVERITY_MESSAGE};
    D3D12_MESSAGE_ID deniedIds[] = {static_cast<D3D12_MESSAGE_ID>(1328)};
    D3D12_INFO_QUEUE_FILTER filter{};
    filter.DenyList.NumSeverities = static_cast<UINT>(std::size(denied));
    filter.DenyList.pSeverityList = denied;
    filter.DenyList.NumIDs = static_cast<UINT>(std::size(deniedIds));
    filter.DenyList.pIDList = deniedIds;
    queue->PushStorageFilter(&filter);
    queue->SetBreakOnSeverity(D3D12_MESSAGE_SEVERITY_CORRUPTION, FALSE);
    queue->SetBreakOnSeverity(D3D12_MESSAGE_SEVERITY_ERROR, FALSE);
    queue->SetBreakOnSeverity(D3D12_MESSAGE_SEVERITY_WARNING, FALSE);
    ComPtr<ID3D12InfoQueue1> queue1;
    v.callback = SUCCEEDED(queue.As(&queue1)) &&
                 SUCCEEDED(queue1->RegisterMessageCallback(&OnMessage, D3D12_MESSAGE_CALLBACK_FLAG_NONE, nullptr,
                                                           &v.cookie));
    v.queue = queue;
    Info(Format(L"D3D12 validation: the layer's messages go to this log (%s).",
                v.callback ? L"as they come" : L"read at each report: this Windows has no message callback"));
}

void ValidationReport() {
    if (!v.queue) {
        return;
    }
    if (!v.callback) {
        const UINT64 count = v.queue->GetNumStoredMessagesAllowedByRetrievalFilter();
        std::vector<char> buffer;
        for (UINT64 i = 0; i < count; ++i) {
            SIZE_T length = 0;
            if (FAILED(v.queue->GetMessage(i, nullptr, &length)) || length == 0) {
                continue;
            }
            buffer.resize(length);
            auto* message = reinterpret_cast<D3D12_MESSAGE*>(buffer.data());
            if (SUCCEEDED(v.queue->GetMessage(i, message, &length))) {
                Record(message->Severity, message->ID, message->pDescription);
            }
        }
        v.queue->ClearStoredMessages();
    }
    AcquireSRWLockExclusive(&v.lock);
    const unsigned corruption = v.counts[0];
    const unsigned errors = v.counts[1];
    const unsigned warnings = v.counts[2];
    const unsigned long long total = v.total;
    const std::size_t ids = v.seen.size();
    v.counts[0] = v.counts[1] = v.counts[2] = 0;
    ReleaseSRWLockExclusive(&v.lock);
    Info(Format(L"D3D12 validation over the window: %u corruption, %u errors, %u warnings (this session %llu "
                L"messages of %zu kinds).",
                corruption, errors, warnings, total, ids));
}

void ValidationDeviceRemoved(ID3D12Device* device) {
    if (v.mode <= 0 || device == nullptr) {
        return;
    }
    ComPtr<ID3D12DeviceRemovedExtendedData> dred;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dred)))) {
        Info(L"DRED: no account of the removal (unavailable on this Windows).");
        return;
    }
    D3D12_DRED_AUTO_BREADCRUMBS_OUTPUT breadcrumbs{};
    if (SUCCEEDED(dred->GetAutoBreadcrumbsOutput(&breadcrumbs))) {
        unsigned unfinished = 0;
        for (const D3D12_AUTO_BREADCRUMB_NODE* node = breadcrumbs.pHeadAutoBreadcrumbNode; node != nullptr;
             node = node->pNext) {
            const UINT done = node->pLastBreadcrumbValue != nullptr ? *node->pLastBreadcrumbValue : 0;
            if (done >= node->BreadcrumbCount) {
                continue;  // finished
            }
            ++unfinished;
            // The operations (D3D12_AUTO_BREADCRUMB_OP values) around where it stopped.
            std::wstring history;
            const UINT first = done >= 3 ? done - 3 : 0;
            const UINT last = std::min(node->BreadcrumbCount, done + 3);
            for (UINT i = first; i < last && node->pCommandHistory != nullptr; ++i) {
                history += Format(i == done ? L" [%d]" : L" %d", static_cast<int>(node->pCommandHistory[i]));
            }
            Error(Format(L"DRED: command list %s on queue %s stopped after %u of %u operations; operations %u-%u:%s "
                         L"(D3D12_AUTO_BREADCRUMB_OP; the one in brackets did not finish).",
                         node->pCommandListDebugNameW != nullptr ? node->pCommandListDebugNameW : L"(unnamed)",
                         node->pCommandQueueDebugNameW != nullptr ? node->pCommandQueueDebugNameW : L"(unnamed)",
                         done, node->BreadcrumbCount, first, last == 0 ? 0 : last - 1, history.c_str()));
        }
        if (unfinished == 0) {
            Info(L"DRED: every command list it saw had finished.");
        }
    }
    D3D12_DRED_PAGE_FAULT_OUTPUT fault{};
    if (SUCCEEDED(dred->GetPageFaultAllocationOutput(&fault)) && fault.PageFaultVA != 0) {
        std::wstring allocations;
        for (const D3D12_DRED_ALLOCATION_NODE* node = fault.pHeadExistingAllocationNode; node != nullptr;
             node = node->pNext) {
            allocations += Format(L" %s;", node->ObjectNameW != nullptr ? node->ObjectNameW : L"(unnamed)");
        }
        for (const D3D12_DRED_ALLOCATION_NODE* node = fault.pHeadRecentFreedAllocationNode; node != nullptr;
             node = node->pNext) {
            allocations += Format(L" %s (freed);", node->ObjectNameW != nullptr ? node->ObjectNameW : L"(unnamed)");
        }
        Error(Format(L"DRED: page fault at GPU address 0x%llX; allocations there:%s", fault.PageFaultVA,
                     allocations.empty() ? L" none known" : allocations.c_str()));
    }
}

} // namespace sert::rt
