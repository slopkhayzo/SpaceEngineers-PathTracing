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
- Denoised by NVIDIA DLSS Ray Reconstruction on GeForce RTX GPUs, and by
  the plugin's own filter on other GPUs
- Optional NVIDIA DLSS Frame Generation on GeForce RTX 40 and 50 series
  GPUs: a frame made between each two the game draws

The game still draws everything else as usual: fog, atmosphere, clouds,
glass and other transparent surfaces, particles, post-processing and the
HUD. Only what you see changes: the simulation, the game's files and your
saves are untouched.

Version 0.4.0 adds DLSS Frame Generation (GeForce RTX 40 and 50 series),
DLSS modes that trace the light at a lower resolution and let Ray
Reconstruction upscale it, and a Path tracing screen in the game's graphics
options whose changes apply at once. It also takes much less CPU time per
frame: in Sparks of the Future, a world with very many blocks, from 48 to
about 90 frames per second (GeForce RTX 4090, 1920x1080, DLSS Performance).

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
Higher resolutions cost more; the lower DLSS modes (DlssMode below) cost
less.

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
- DlssMode sets Ray Reconstruction's resolution: dlaa (the default) traces
  the light at the game's resolution; quality, balanced, performance and
  ultra-performance trace it smaller and let Ray Reconstruction upscale it:
  cheaper, softer.
- DecalLights = false makes glowing decals only glow, as in the game,
  without lighting their surroundings.
- LightTree = false traces lamps and glowing surfaces within 32 m of the
  camera only, as 0.2.0 did (beyond that, the game's own lamp light): a
  little cheaper.
- FrameGeneration = true switches DLSS Frame Generation on (see below).
Set Enabled = false under [General] to switch the plugin off without
removing it.

The plugin patches a copy of the game's shaders at start (kept under
%LOCALAPPDATA%\SpaceEngineers.RTHWAccel); the game's own files are not
modified.

DLSS Frame Generation
---------------------
Frame generation makes a frame between each two the game draws: motion
looks smoother, at slightly more latency. Switch it on in Options >
Graphics > Path tracing (or FrameGeneration = true in the INI). It needs:
- an NVIDIA GeForce RTX 40 or 50 series GPU and a current driver;
- hardware-accelerated GPU scheduling (Windows Settings > System >
  Display > Graphics > Change default graphics settings);
- DLSS Ray Reconstruction as the denoiser (Denoiser = auto or rr);
- the game in a window or a full-screen window: in exclusive full screen
  the game presents its frames itself, without frame generation.
If something is missing, the Path tracing screen says what when you
switch it on. It pauses during loading screens and in full-screen menus.
On a GeForce RTX 4090 at 1920x1080 (DLSS Performance), a ship interior
went from 144 frames per second to 234 shown (117 drawn by the game).

RivaTuner Statistics Server (the on-screen display of MSI Afterburner)
draws into every frame frame generation shows and slows it down a lot (to
about a quarter of the frame rate in a test). While using frame
generation, set RivaTuner's Application detection level to None for
SpaceEngineers.exe, or close RivaTuner.

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
  Denoiser = filter) is noisier and blurrier in motion than Ray
  Reconstruction.
- Shadows of moving things (such as your own character's) and rough
  reflections can leave short trails behind them, seen at the lower DLSS
  modes (DlssMode = performance), with or without frame generation.
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
SpaceEngineers.RTHWAccel.ini, nvngx_dlssd.dll, nvngx_dlssg.dll, the sl.*.dll
files, SpaceEngineers.RTHWAccel.log, SpaceEngineers.RTHWAccel.previous.log,
dinput8.dll, and the folder %LOCALAPPDATA%\SpaceEngineers.RTHWAccel. Do not
remove dinput8.dll if another installed plugin (such as HighFPSFix) depends
on it. The game's own files and save data are not modified.

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

Misc
-------
For more game fixes, visit https://slop-blog.enkhayzomachines.net/fixes :)
