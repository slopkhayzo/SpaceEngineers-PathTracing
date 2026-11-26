#pragma once

// D3D12 validation for finding what one vendor's driver forgives and another's may not (research/docs/
// amd-fsr-plan.md §4.8): [Debug] D3D12Validation in the plugin's INI turns on the D3D12 debug layer (on) and GPU-based
// validation (gpu), with DRED's breadcrumbs and page faults; the layer's messages go to the plugin's log. Off by
// default: the layer costs CPU time per call, GPU-based validation much more.

#include <d3d12.h>

#include <string>

namespace sert::rt {

// The INI's choice: 0 off, 1 the debug layer (on), 2 the layer with GPU-based validation of resource states (gpu),
// 3 with every shader access validated too (gpu-full: the first traced frames take minutes).
int ValidationMode(const std::wstring& iniPath);

// Before the D3D12 device is made (it applies to devices made afterwards): the layer and DRED as `mode` asks. False
// when the layer could not be loaded (Windows' optional feature "Graphics Tools" installs it); DRED still works then.
bool ValidationEnable(int mode);

// Once the device is made: the layer's warnings, errors and corruption messages into the log (each message id the
// first few times, the rest counted), nothing breaking into a debugger.
void ValidationAttach(ID3D12Device* device);

// Every report window: the messages since the last report, counted (without a callback, read from the layer's queue
// here).
void ValidationReport();

// After a device removal: DRED's account of it (the command lists that did not finish, the operation each stopped
// at, a page fault's address) into the log.
void ValidationDeviceRemoved(ID3D12Device* device);

} // namespace sert::rt
