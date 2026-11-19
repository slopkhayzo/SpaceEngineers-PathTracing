SpaceEngineers.RTHWAccel
========================

Path-traced lighting for Space Engineers (64-bit Windows, Steam).

This plugin replaces the game's lighting with light traced by the GPU's
ray-tracing hardware:

- Sunlight with soft shadows, and light from the sky
- The game's lamps within 32 m of the camera, with shadows (beyond that,
  the game's own lamp light)
- Light cast by glowing surfaces: screens, keypads, light strips, and the
  glowing parts of character suits
- One bounce of indirect light, diffuse and glossy
- Denoised by NVIDIA DLSS Ray Reconstruction on GeForce RTX GPUs, and by
  the plugin's own filter on other GPUs

The game still draws everything else as usual: fog, atmosphere, clouds,
glass and other transparent surfaces, particles, post-processing and the
HUD. Only what you see changes: the simulation, the game's files and your
saves are untouched.

Version 0.1.0 is the first release.

Requirements
------------
- Space Engineers for Windows (Steam). Developed and tested with version
  1.211.017.
- A GPU with DirectX Raytracing 1.1 (GeForce RTX 20 series or newer, Radeon
  RX 6000 series or newer, Intel Arc) and a current driver, on Windows 10
  (21H2 or newer) or Windows 11. Without DirectX Raytracing 1.1 the
  plugin stays off and the game looks as usual.
- An x64 ASI loader. It is not included in this archive; the
  "with-Ultimate-ASI-Loader" archive includes one.

Installation
------------
Place SpaceEngineers.RTHWAccel.asi, SpaceEngineers.RTHWAccel.Managed.dll,
SpaceEngineers.RTHWAccel.ini and nvngx_dlssd.dll in the game's Bin64
folder, beside SpaceEngineers.exe (Steam: right-click Space Engineers >
Manage > Browse local files > Bin64).

Performance
-----------
The light is traced at the game's full resolution every frame. On a
GeForce RTX 4090 at 1920x1080 this takes about 6 ms of GPU time per frame
in ships and stations, and 8 to 14 ms on planets and in caves. Higher
resolutions cost more.

Settings
--------
SpaceEngineers.RTHWAccel.ini describes each setting; changes take effect
the next time the game starts. The main ones, under [RayTracing]:
- GateMode = off shows the game's own lighting, for comparison.
- LampIntensity and EmissiveIntensity multiply the light of the game's
  lamps and of glowing surfaces (1 = as the game sets them). Raise them for
  a brighter look.
- Denoiser chooses between DLSS Ray Reconstruction and the plugin's own
  filter.
Set Enabled = false under [General] to switch the plugin off without
removing it.

The plugin patches a copy of the game's shaders at start (kept under
%LOCALAPPDATA%\SpaceEngineers.RTHWAccel); the game's own files are not
modified.

With HighFPSFix
---------------
SpaceEngineers.HighFPSFix (frame rates above 60 fps with smooth motion)
works together with this plugin. Both are loaded by the same ASI loader:
install it once.

Not covered
-----------
- Grass, decals and glass are not ray traced: they keep the game's look.
  Light passes through glass.
- Textures of the traced scene stream in during the first minute after a
  world loads.
- The plugin's own filter (GPUs without Ray Reconstruction, or
  Denoiser = filter) is noisier and blurrier in motion than Ray
  Reconstruction.
- Tested on a GeForce RTX 4090 only. Radeon and Intel Arc GPUs should work
  with the plugin's own filter but are untested.
- Multiplayer and mods are untested.

Troubleshooting
---------------
SpaceEngineers.RTHWAccel.log is written beside the plugin and replaced on
each launch; the previous session is kept as
SpaceEngineers.RTHWAccel.previous.log. If the game folder is not writable,
the log goes to %LOCALAPPDATA%\SpaceEngineers.RTHWAccel instead. If the
plugin switches something off, for example after a game update, the reason
is in the log. Set Diagnostics = true in the INI before reproducing a
problem you want to report.

Removal
-------
Remove SpaceEngineers.RTHWAccel.asi, SpaceEngineers.RTHWAccel.Managed.dll,
SpaceEngineers.RTHWAccel.ini, nvngx_dlssd.dll, SpaceEngineers.RTHWAccel.log,
and SpaceEngineers.RTHWAccel.previous.log, and the folder
%LOCALAPPDATA%\SpaceEngineers.RTHWAccel. Do not remove a shared ASI loader
unless no other installed plugin depends on it. The game's own files and
save data are not modified.

Third-party components
----------------------
- MinHook (MINHOOK-LICENSE.txt).
- NVIDIA DLSS Ray Reconstruction (nvngx_dlssd.dll): NVIDIA DLSS SDK,
  (c) NVIDIA Corporation, under the NVIDIA RTX SDKs License
  (NVIDIA-DLSS-LICENSE.txt). NVIDIA, RTX and DLSS are trademarks of NVIDIA
  Corporation.

Misc
-------
For more game fixes, visit https://slop-blog.enkhayzomachines.net/fixes :)
