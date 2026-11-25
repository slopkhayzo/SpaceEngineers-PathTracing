#pragma once

#include "common.h"

namespace sert::rt {

// Diagnostics ([Debug] SampleThreads): where chosen threads spend their time, sampled every millisecond without
// symbols. Per sample: the module of the instruction it interrupts and, for samples inside the system's wait and
// syscall code (ntdll, KERNELBASE, kernel32, win32u), the first other module found on the stack (the waiter). A
// sampled thread is suspended only for GetThreadContext, VirtualQuery and a copy of its stack's top: nothing that
// takes a user-mode lock runs meanwhile.
void SamplerEnable(bool on);
// Any thread: a thread to sample, by name (up to four; the same id again is ignored).
void SamplerAddThread(DWORD threadId, const wchar_t* name);
// The samples since the last report logged, then reset (nothing while disabled).
void SamplerReport(double seconds);

} // namespace sert::rt
