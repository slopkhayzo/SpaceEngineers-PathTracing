#include "sampler.h"

#include <psapi.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstring>
#include <cwctype>
#include <map>
#include <string>
#include <utility>
#include <vector>

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

namespace sert::rt {
namespace {

constexpr int kMaxThreads = 4;
constexpr std::size_t kStackWords = 96;  // the stack's top copied per sample (768 bytes)
constexpr int kModuleRefreshSamples = 1000;  // the module list read again every second or so

struct Module {
    std::uintptr_t begin = 0;
    std::uintptr_t end = 0;
    std::wstring name;
    bool system = false;  // wait and syscall code: the waiter is looked for further up the stack
};

struct Target {
    DWORD id = 0;
    HANDLE handle = nullptr;
    std::wstring name;
    std::map<std::wstring, UINT64> executing;  // samples by the module of the interrupted instruction
    std::map<std::wstring, UINT64> waiters;    // samples in system code, by the first other module on the stack
    UINT64 samples = 0;
};

struct State {
    std::atomic<bool> enabled{false};
    HANDLE thread = nullptr;
    SRWLOCK lock = SRWLOCK_INIT;  // the targets (the sampler thread, SamplerAddThread, SamplerReport)
    std::array<Target, kMaxThreads> targets;
    int count = 0;
    std::vector<Module> modules;  // the sampler thread's own (sorted by address)
};

State s;

bool IsSystem(std::wstring name) {
    for (wchar_t& c : name) {
        c = static_cast<wchar_t>(std::towlower(c));
    }
    return name == L"ntdll.dll" || name == L"kernelbase.dll" || name == L"kernel32.dll" || name == L"win32u.dll";
}

// The sampler thread, with no target suspended (the loader lock is taken).
void RefreshModules() {
    HMODULE handles[1024];
    DWORD needed = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), handles, sizeof(handles), &needed)) {
        return;
    }
    const DWORD count = std::min<DWORD>(needed / sizeof(HMODULE), static_cast<DWORD>(std::size(handles)));
    std::vector<Module> modules;
    modules.reserve(count);
    for (DWORD i = 0; i < count; ++i) {
        MODULEINFO info{};
        wchar_t name[MAX_PATH]{};
        if (!K32GetModuleInformation(GetCurrentProcess(), handles[i], &info, sizeof(info)) ||
            K32GetModuleBaseNameW(GetCurrentProcess(), handles[i], name, MAX_PATH) == 0) {
            continue;
        }
        Module module;
        module.begin = reinterpret_cast<std::uintptr_t>(info.lpBaseOfDll);
        module.end = module.begin + info.SizeOfImage;
        module.name = name;
        module.system = IsSystem(module.name);
        modules.push_back(std::move(module));
    }
    std::sort(modules.begin(), modules.end(),
              [](const Module& a, const Module& b) { return a.begin < b.begin; });
    s.modules = std::move(modules);
}

const Module* Find(const std::uintptr_t address) {
    auto it = std::upper_bound(s.modules.begin(), s.modules.end(), address,
                               [](const std::uintptr_t value, const Module& m) { return value < m.begin; });
    if (it == s.modules.begin()) {
        return nullptr;
    }
    --it;
    return address < it->end ? &*it : nullptr;
}

// After the target resumed: its sample counted.
void Classify(Target& target, const std::uintptr_t ip, const std::uintptr_t* words, const std::size_t count) {
    ++target.samples;
    const Module* module = Find(ip);
    ++target.executing[module != nullptr ? module->name : L"(no module: JIT code)"];
    if (module == nullptr || !module->system) {
        return;
    }
    // The first other module on the stack (the waiter) and the next one after it (the waiter's caller).
    const Module* waiter = nullptr;
    for (std::size_t i = 0; i < count; ++i) {
        const Module* caller = Find(words[i]);
        if (caller == nullptr || caller->system) {
            continue;
        }
        if (waiter == nullptr) {
            waiter = caller;
        } else if (caller != waiter) {
            ++target.waiters[waiter->name + L" <- " + caller->name];
            return;
        }
    }
    ++target.waiters[waiter != nullptr ? waiter->name : L"(none found)"];
}

DWORD WINAPI SamplerThread(void*) {
    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (timer == nullptr) {
        timer = CreateWaitableTimerW(nullptr, FALSE, nullptr);
    }
    int sinceRefresh = kModuleRefreshSamples;
    while (s.enabled.load()) {
        LARGE_INTEGER due{};
        due.QuadPart = -10000;  // 1 ms
        if (timer != nullptr && SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE)) {
            WaitForSingleObject(timer, 100);
        } else {
            Sleep(1);
        }
        if (++sinceRefresh >= kModuleRefreshSamples) {
            sinceRefresh = 0;
            RefreshModules();
        }
        AcquireSRWLockExclusive(&s.lock);
        for (int t = 0; t < s.count; ++t) {
            Target& target = s.targets[static_cast<std::size_t>(t)];
            CONTEXT context{};
            context.ContextFlags = CONTEXT_CONTROL;
            std::uintptr_t words[kStackWords];
            std::size_t count = 0;
            if (SuspendThread(target.handle) == static_cast<DWORD>(-1)) {
                continue;
            }
            const bool got = GetThreadContext(target.handle, &context) != FALSE;
            if (got) {
                MEMORY_BASIC_INFORMATION region{};
                if (VirtualQuery(reinterpret_cast<const void*>(context.Rsp), &region, sizeof(region)) != 0 &&
                    region.State == MEM_COMMIT) {
                    const std::uintptr_t end = reinterpret_cast<std::uintptr_t>(region.BaseAddress) + region.RegionSize;
                    count = std::min<std::size_t>(kStackWords, (end - context.Rsp) / sizeof(std::uintptr_t));
                    std::memcpy(words, reinterpret_cast<const void*>(context.Rsp), count * sizeof(std::uintptr_t));
                }
            }
            ResumeThread(target.handle);
            if (got) {
                Classify(target, static_cast<std::uintptr_t>(context.Rip), words, count);
            }
        }
        ReleaseSRWLockExclusive(&s.lock);
    }
    if (timer != nullptr) {
        CloseHandle(timer);
    }
    return 0;
}

// The largest entries as "name share %", the rest summed.
std::wstring Top(const std::map<std::wstring, UINT64>& counts, const UINT64 total) {
    std::vector<std::pair<std::wstring, UINT64>> sorted(counts.begin(), counts.end());
    std::sort(sorted.begin(), sorted.end(), [](const auto& a, const auto& b) { return a.second > b.second; });
    std::wstring text;
    UINT64 rest = 0;
    for (std::size_t i = 0; i < sorted.size(); ++i) {
        if (i < 7) {
            text += Format(L"%s%s %.1f %%", text.empty() ? L"" : L", ", sorted[i].first.c_str(),
                           100.0 * static_cast<double>(sorted[i].second) / static_cast<double>(total));
        } else {
            rest += sorted[i].second;
        }
    }
    if (rest != 0) {
        text += Format(L", others %.1f %%", 100.0 * static_cast<double>(rest) / static_cast<double>(total));
    }
    return text.empty() ? L"none" : text;
}

} // namespace

void SamplerEnable(const bool on) {
    if (on == s.enabled.load()) {
        return;
    }
    if (on) {
        s.enabled.store(true);
        s.thread = CreateThread(nullptr, 0, &SamplerThread, nullptr, 0, nullptr);
        if (s.thread == nullptr) {
            s.enabled.store(false);
            Error(Format(L"sampler: its thread could not be made (error %lu).", GetLastError()));
            return;
        }
        SetThreadPriority(s.thread, THREAD_PRIORITY_TIME_CRITICAL);
        Info(L"sampler: on (chosen threads sampled every millisecond).");
    } else {
        s.enabled.store(false);
        if (s.thread != nullptr) {
            WaitForSingleObject(s.thread, 1000);
            CloseHandle(s.thread);
            s.thread = nullptr;
        }
    }
}

void SamplerAddThread(const DWORD threadId, const wchar_t* name) {
    if (!s.enabled.load() || threadId == 0) {
        return;
    }
    AcquireSRWLockExclusive(&s.lock);
    bool known = false;
    for (int t = 0; t < s.count; ++t) {
        known = known || s.targets[static_cast<std::size_t>(t)].id == threadId;
    }
    if (!known && s.count < kMaxThreads) {
        HANDLE handle = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, threadId);
        if (handle != nullptr) {
            Target& target = s.targets[static_cast<std::size_t>(s.count++)];
            target.id = threadId;
            target.handle = handle;
            target.name = name;
            Info(Format(L"sampler: sampling the %s (thread %lu).", name, threadId));
        }
    }
    ReleaseSRWLockExclusive(&s.lock);
}

void SamplerReport(const double seconds) {
    if (!s.enabled.load()) {
        return;
    }
    AcquireSRWLockExclusive(&s.lock);
    for (int t = 0; t < s.count; ++t) {
        Target& target = s.targets[static_cast<std::size_t>(t)];
        if (target.samples == 0) {
            continue;
        }
        UINT64 waiting = 0;
        for (const auto& entry : target.waiters) {
            waiting += entry.second;
        }
        Info(Format(L"sampler over %.1f s, the %s (%llu samples): executing in %s; in system code %.1f %% of the time, "
                    L"called from %s.",
                    seconds, target.name.c_str(), static_cast<unsigned long long>(target.samples),
                    Top(target.executing, target.samples).c_str(),
                    100.0 * static_cast<double>(waiting) / static_cast<double>(target.samples),
                    Top(target.waiters, waiting != 0 ? waiting : 1).c_str()));
        target.executing.clear();
        target.waiters.clear();
        target.samples = 0;
    }
    ReleaseSRWLockExclusive(&s.lock);
}

} // namespace sert::rt
