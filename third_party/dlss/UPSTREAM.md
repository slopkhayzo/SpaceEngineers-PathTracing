# NVIDIA DLSS SDK (Ray Reconstruction)

- Source: https://github.com/NVIDIA/DLSS, tag `v310.9.16` (released 2026-10-16).
- Fetched by `scripts/fetch_dlss.ps1` (only the parts listed there: headers,
  `lib/Windows_x86_64/x64/nvsdk_ngx_s.lib` for the static CRT,
  `lib/Windows_x86_64/rel/nvngx_dlssd.dll` 310.9.16.0 signed by NVIDIA
  Corporation, the licence, the DLSS and DLSS-RR integration guides, and the
  NGX logging and on-screen-indicator registry files). The files stay out of
  git; `SHA256SUMS.txt` (written by the script) lists their hashes.
- Licence: NVIDIA RTX SDKs License (`LICENSE.txt`). It allows shipping the
  runtime inside an application, for NVIDIA GPUs only, with attribution, and
  asks for a notice to NVIDIA before a commercial release.
- Used for: DLSS Ray Reconstruction as the optional NVIDIA denoiser of the
  traced light (research/docs/space-engineers-rt-plan.md §6.10); the build
  enables it when these files are present.
