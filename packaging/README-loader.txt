SpaceEngineers.RTHWAccel
========================

Path-traced lighting for Space Engineers (64-bit Windows, Steam).

This plugin replaces the game's lighting with light traced by the GPU's
ray-tracing hardware:

- Sunlight with soft shadows, and light from the sky
- The game's lamps, at any distance, with shadows
- Light cast by glowing surfaces: screens, keypads, light strips, glowing
  decals such as indicator lights and signs, and the glowing parts of
  character suits
- Bullet holes and the other decals the game projects onto blocks
- One bounce of indirect light, diffuse and glossy
- Denoised by NVIDIA DLSS Ray Reconstruction on GeForce RTX GPUs, and on
  other GPUs by the plugin's own filter, then anti-aliased or upscaled by
  AMD FSR
- Optional frame generation, a frame made between each two the game draws:
  NVIDIA DLSS Frame Generation on GeForce RTX 40 and 50 series GPUs, AMD
  FSR Frame Generation on any GPU

The game still draws everything else as usual: fog, atmosphere, clouds,
glass and other transparent surfaces, particles, post-processing and the
HUD. Only what you see changes: the simulation, the game's files and your
saves are untouched.

Version 0.5.0 adds AMD FSR. With the plugin's own filter (GPUs without
Ray Reconstruction), FSR anti-aliases the traced light at full resolution,
or traces it smaller and upscales it in the lower modes. FSR Frame
Generation runs on any GPU the plugin runs on, and both frame generators
now work with either denoiser. The plugin's own filter is also much
sharper than in 0.4.0. AMD GPUs are untested (see "Not covered").

Requirements
------------
- Space Engineers for Windows (Steam). Developed and tested with version
  1.211.017.
- A GPU with DirectX Raytracing 1.1 (GeForce RTX 20 series or newer, Radeon
  RX 6000 series or newer, Intel Arc) and a current driver, on Windows 10
  (21H2 or newer) or Windows 11. Without DirectX Raytracing 1.1 the
  plugin stays off and the game looks as usual.

Installation
------------
Place all files in the game's Bin64 folder, beside SpaceEngineers.exe
(Steam: right-click Space Engineers > Manage > Browse local files > Bin64).
The included dinput8.dll is the x64 Ultimate ASI Loader v9.7.4, which loads
SpaceEngineers.RTHWAccel.asi when the game starts. If a dinput8.dll is
already there, find out what it belongs to and back it up before replacing
it.

Performance
-----------
By default the light is traced at the game's full resolution every frame.
On a GeForce RTX 4090 at 1920x1080 this takes about 8 ms of GPU time per
frame in ships and stations, and 8 to 14 ms on planets and in caves.
Higher resolutions cost more; the lower upscaling modes (UpscalingMode
below) cost less.

Settings
--------
In the game, Options > Graphics > Path tracing changes the main settings
at once. SpaceEngineers.RTHWAccel.ini describes each setting; changes made
there take effect the next time the game starts. The main ones, under
[RayTracing]:
- GateMode = off shows the game's own lighting, for comparison.
- LampIntensity and EmissiveIntensity multiply the light of the game's
  lamps and of glowing surfaces (1 = as the game sets them). Raise them for
  a brighter look.
- Denoiser chooses between DLSS Ray Reconstruction and the plugin's own
  filter.
- UpscalingMode (DlssMode before 0.5.0, still read) sets the resolution the
  light is traced at: native (the default) traces it at the game's
  resolution; quality, balanced, performance and ultra-performance trace it
  smaller and let Ray Reconstruction, or AMD FSR after the plugin's filter,
  upscale it: cheaper, softer.
- Upscaler: with the plugin's filter, auto (the default) lets AMD FSR
  anti-alias the light (native) or upscale it (the lower modes); off keeps
  the filter alone at the game's resolution, as in 0.4.0. FsrSharpness adds
  FSR's sharpening (0 to 1).
- DecalLights = false makes glowing decals only glow, as in the game,
  without lighting their surroundings.
- LightTree = false traces lamps and glowing surfaces within 32 m of the
  camera only, as 0.2.0 did (beyond that, the game's own lamp light): a
  little cheaper.
- FrameGeneration = off, auto, dlss or fsr chooses frame generation (see
  below).
Set Enabled = false under [General] to switch the plugin off without
removing it.

The plugin patches a copy of the game's shaders at start (kept under
%LOCALAPPDATA%\SpaceEngineers.RTHWAccel); the game's own files are not
modified.

Frame generation
----------------
Frame generation makes a frame between each two the game draws: motion
looks smoother, at slightly more latency. Choose it in Options > Graphics >
Path tracing > Frame generation (or FrameGeneration in the INI):
- DLSS Frame Generation needs an NVIDIA GeForce RTX 40 or 50 series GPU, a
  current driver and hardware-accelerated GPU scheduling (Windows
  Settings > System > Display > Graphics > Change default graphics
  settings).
- AMD FSR Frame Generation runs on any GPU the plugin runs on. Not tested
  on AMD GPUs.
- Auto takes DLSS Frame Generation where it runs, else AMD FSR's.
Both work with either denoiser, with the game in a window or a full-screen
window: in exclusive full screen the game presents its frames itself,
without frame generation. If your choice cannot run, the Path tracing
screen says why. Frame generation pauses during loading screens and starts
10 seconds after a world appears; as DLSS Frame Generation starts, the game
can stop for a moment (up to a few seconds) while it prepares. In a test
on a GeForce RTX 4090 at 1920x1080 (Performance mode, a ship's bridge, about
140 frames per second without it), both frame generators showed 186 to 205
frames per second (93 to 103 drawn by the game); in an earlier test without
RivaTuner, DLSS Frame Generation showed 234.

RivaTuner Statistics Server (the on-screen display of MSI Afterburner)
draws into every frame frame generation shows and can slow it down a lot
(to about a quarter of the frame rate in a test). If it does, set
RivaTuner's Application detection level to None for SpaceEngineers.exe, or
close RivaTuner.

With HighFPSFix
---------------
SpaceEngineers.HighFPSFix (frame rates above 60 fps with smooth motion)
works together with this plugin. Both are loaded by the same ASI loader:
the dinput8.dll in its "with-Ultimate-ASI-Loader" archive is the same file
as this one.

Not covered
-----------
- Grass and glass are not ray traced: they keep the game's look. Light
  passes through glass.
- Decals do not show in reflections.
- Textures seen at a grazing angle look blurrier than in the game: the
  traced surfaces are not filtered anisotropically yet.
- Textures of the traced scene stream in during the first minute after a
  world loads.
- The plugin's own filter (GPUs without Ray Reconstruction, or
  Denoiser = filter) is noisier in motion than Ray Reconstruction. In
  FSR's lower modes some of that noise stays as coloured speckles in dark
  corners, and ultra-performance shows stair steps on thin rails.
- Shadows of moving things (such as your own character's) and rough
  reflections can leave short trails behind them, seen with Ray
  Reconstruction at the lower modes (UpscalingMode = performance), with or
  without frame generation.
- Tested on a GeForce RTX 4090 only. Radeon and Intel Arc GPUs should work
  with the plugin's own filter and AMD FSR but are untested. On Radeons,
  AMD's driver may choose FSR 4 instead of FSR 3.1 and its newer frame
  generation; nobody could test those here. Reports are welcome: see
  Troubleshooting.
- Multiplayer and mods are untested.

Troubleshooting
---------------
SpaceEngineers.RTHWAccel.log is written beside the plugin and replaced on
each launch; the previous session is kept as
SpaceEngineers.RTHWAccel.previous.log. If the game folder is not writable,
the log goes to %LOCALAPPDATA%\SpaceEngineers.RTHWAccel instead. If the
plugin switches something off, for example after a game update, the reason
is in the log. Set Diagnostics = true in the INI before reproducing a
problem you want to report; the log then names your GPU, its driver and
the FSR versions in use.

On an AMD or Intel GPU, if the image looks broken or the game stops
responding, try these one at a time and report what helped, with the log:
Upscaler = off, FrameGeneration = off, FsrVersion = 3 (FSR 3.1 instead of
FSR 4), and GateMode = off (the game's own lighting).

Removal
-------
Remove SpaceEngineers.RTHWAccel.asi, SpaceEngineers.RTHWAccel.Managed.dll,
SpaceEngineers.RTHWAccel.ini, nvngx_dlssd.dll, nvngx_dlssg.dll, the sl.*.dll
files, the amd_fidelityfx_*.dll files, SpaceEngineers.RTHWAccel.log,
SpaceEngineers.RTHWAccel.previous.log, dinput8.dll and the folder
%LOCALAPPDATA%\SpaceEngineers.RTHWAccel. Do not remove dinput8.dll if
another installed plugin (such as HighFPSFix) depends on it. The game's own
files and save data are not modified.

Third-party components
----------------------
- MinHook (MINHOOK-LICENSE.txt).
- Ultimate ASI Loader v9.7.4 by ThirteenAG (dinput8.dll,
  ULTIMATE-ASI-LOADER-LICENSE.txt).
- NVIDIA DLSS Ray Reconstruction (nvngx_dlssd.dll): NVIDIA DLSS SDK,
  (c) NVIDIA Corporation, under the NVIDIA RTX SDKs License
  (NVIDIA-DLSS-LICENSE.txt). NVIDIA, RTX and DLSS are trademarks of NVIDIA
  Corporation.
- NVIDIA DLSS Frame Generation (nvngx_dlssg.dll), (c) NVIDIA Corporation,
  under the NVIDIA RTX SDKs License
  (NVIDIA-DLSS-FRAME-GENERATION-LICENSE.txt).
- NVIDIA Streamline (sl.interposer.dll, sl.common.dll, sl.dlss_g.dll,
  sl.pcl.dll), (c) NVIDIA Corporation, under the MIT License
  (NVIDIA-STREAMLINE-LICENSE.txt; its third-party notices in
  NVIDIA-STREAMLINE-THIRD-PARTY-NOTICES.md), and NVIDIA Reflex
  (sl.reflex.dll) under the NVIDIA SDK license (NVIDIA-REFLEX-LICENSE.txt).
  Reflex is a trademark of NVIDIA Corporation.
- AMD FSR Upscaling and Frame Generation (amd_fidelityfx_loader_dx12.dll,
  amd_fidelityfx_upscaler_dx12.dll, amd_fidelityfx_framegeneration_dx12.dll),
  from the AMD FSR SDK 2.4.0, (c) Advanced Micro Devices, Inc., unmodified,
  under AMD's license (AMD-FSR-LICENSE.txt). AMD, FidelityFX and FSR are
  trademarks of Advanced Micro Devices, Inc.

Misc
-------
For more game fixes, visit https://slop-blog.enkhayzomachines.net/fixes :)
