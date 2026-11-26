# AMD FSR SDK (FSR Upscaling, FSR Frame Generation)

- Source: https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK (the FidelityFX SDK before 2.x), tag
  `v2.4.0` (released 2026-10-28), commit `7539ae8b6568ad29ed78a753404351daf3b5cca7`.
- Fetched by `scripts/fetch_fsr.ps1` (only the files listed there, each checked against its git blob hash in that
  commit):
  - the FFX API headers for upscaling and frame generation (`Kits/FidelityFX/api/include`,
    `upscalers/include`, `framegeneration/include`);
  - AMD's signed D3D12 runtime from `Kits/FidelityFX/signedbin`: `amd_fidelityfx_loader_dx12.dll`,
    `amd_fidelityfx_upscaler_dx12.dll` and `amd_fidelityfx_framegeneration_dx12.dll`, each checked for AMD's
    Authenticode signature;
  - the licence and the technique documentation.

  The files stay out of git; `SHA256SUMS.txt` (written by the script) lists their hashes.
- Licence: `Kits/FidelityFX/docs/license.md`.
  - AMD's terms for the signed DLLs: redistribution in binary form only, unmodified, with the notices and
    disclaimers; no reverse engineering, decompilation or disassembly.
  - MIT for the files it lists, the headers among them.
- Used for: FSR Upscaling of the traced light after the plugin's filter, and FSR Frame Generation of the presented
  frames (research/docs/amd-fsr-plan.md). The build enables them when these files are present.
