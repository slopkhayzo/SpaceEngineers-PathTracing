# NVIDIA Streamline SDK (DLSS Frame Generation)

- Source: https://github.com/NVIDIA-RTX/Streamline, release `v2.14.16` (published
  2026-10-16), asset `streamline-sdk-v2.14.16.zip` (SHA-256
  `32041e51db0cab709a271b72a352281b690c2abbd669a827809401a5dd23956b`).
- Fetched by `scripts/fetch_streamline.ps1` (only the parts listed there: the
  headers, the x64 runtime DLLs `sl.interposer`, `sl.common`, `sl.dlss_g`,
  `sl.reflex`, `sl.pcl` and `nvngx_dlssg.dll`, checked for NVIDIA's Authenticode
  signature, the licences and the programming guides). The files stay out of
  git; `SHA256SUMS.txt` (written by the script) lists their hashes.
- Licences: Streamline itself MIT (`license.txt`); the DLSS runtime
  `nvngx_dlssg.dll` the NVIDIA RTX SDKs License (`bin/x64/nvngx_dlss.license.txt`:
  NVIDIA GPUs only, attribution, a notice to NVIDIA before a commercial release);
  Reflex its own (`bin/x64/reflex.license.txt`).
- Used for: DLSS Frame Generation of the game's presented frames
  (research/docs/space-engineers-rt-plan.md).
