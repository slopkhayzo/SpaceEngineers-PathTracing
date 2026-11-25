# SpaceEngineers.RTHWAccel

> [!WARNING]
> This patch has been pretty much entirely been generated using AI; 
> I won't and will never claim to have enough knowledge or expertise to do this 
> kind of reverse-engineering by myself; I've tested (very) briefly and so far spotted 
> no major issues (on my machine ofc, if you have issues feel free to open an Issue).
> If you're interested and want more (human generated) info, I have a blog post [here](https://slop-blog.enkhayzomachines.net/posts/space-engineers-path-tracing-mod) :)

Path-traced lighting for *Space Engineers* (Steam, 64-bit), delivered as an
ASI plugin like [SpaceEngineers.HighFPSFix](https://github.com/slopkhayzo/SpaceEngineers-HighFPS-RIL).

**Status: 0.3.0.** The plugin replaces the game's lighting with light traced
by the GPU's ray-tracing hardware:
- sunlight with soft shadows, and light from the sky;
- the game's lamps, at any distance, with shadows;
- light cast by glowing surfaces (screens, keypads, light strips, glowing
  decals, suits);
- one bounce of indirect light, diffuse and glossy.

The traced frame includes the blocks' own decals (labels, vent grilles,
keyholes), the decals the game projects onto blocks (bullet holes and the
like) and the blocks' armor skins.

The traced light is denoised by NVIDIA DLSS Ray Reconstruction on GeForce
RTX GPUs, and by the plugin's own filter on other GPUs. NVIDIA DLSS Frame
Generation is optional on GeForce RTX 40 and 50 series GPUs. The game still
draws fog, atmosphere, transparent surfaces, particles, post-processing and
the HUD.

It needs a GPU with DirectX Raytracing 1.1 and Space Engineers 1.211.017.
Installation, settings and known gaps are in the release README
([packaging/README.txt](packaging/README.txt)). The plan, the findings it
rests on and the progress log are in the private
`research/docs/space-engineers-rt-plan.md`.

## How it works

- **Loading.** As in HighFPSFix, the Ultimate ASI Loader (as `dinput8.dll`)
  loads `SpaceEngineers.RTHWAccel.asi`. The ASI hosts MinHook and starts the
  managed part (`SpaceEngineers.RTHWAccel.Managed.dll`) in the game's
  default AppDomain.
- **Side device.** The native core creates a D3D12 device on the adapter of
  the game's D3D11 device, with a fence shared between the two. Shared
  textures are created by D3D11 (NT handles) and opened in D3D12, the
  direction that works for every format needed.
- **The gate.** The game records its lighting on a deferred context
  (`MyGBufferResolver.DoWork`) and replays it later. A hook on the immediate
  context's `ExecuteCommandList` signals D3D12 right before that replay and
  waits for D3D12's work on the GPU. So the ray tracing runs between
  "G-buffer complete" and "lighting".
- **Scene mirror.** Every frame the managed part walks the game's render
  scene: models (with their armor skins), cube blocks, deformed armor,
  voxels, skinned characters and the decals projected onto them.
  The native core keeps bottom-level acceleration structures for its meshes,
  with their materials and textures, and rebuilds the top-level structure
  each frame. The scene's lamps are mirrored too.
- **Tracing.** Compute shaders with inline ray queries (DXR 1.1, shader
  model 6.5) trace jittered primary rays and the sun, sky, lamp and emissive
  light, plus one diffuse and one glossy bounce. A light grid around the
  camera feeds the light sampling: 2 m cells out to 32 m, each listing the
  strongest lights that reach it, and coarser cells beyond (8 m to 512 m,
  out to 8 km), filled from a light tree (a bounding volume hierarchy over
  every lamp and glowing surface, rebuilt each frame) only where the pixels
  need them. Light samples are reused across frames and between
  neighbouring pixels.
- **Denoising.** DLSS Ray Reconstruction (NVIDIA RTX), or the plugin's own
  spatio-temporal filter.
- **Frame generation.** DLSS Frame Generation only works on a D3D12 swap
  chain, and the game presents through D3D11. So the plugin's presenter
  takes over the game's `Present`. It copies each finished frame into a
  D3D12 flip-model swap chain on a child window covering the game's client
  area, made through NVIDIA Streamline's proxies (with manual hooking).
  The traced frame hands Streamline its camera, motion and hardware depth
  on the ray-tracing command list. The presenter adds the frame before the
  HUD (copied right after the game draws its scene) and a UI layer (where
  the finished frame differs from it). DLSS-G presents the real and the
  generated frames from its own thread.
- **Composite.** At start the plugin applies a runtime patch to the game's
  `Lighting/LightDir.hlsl`. The patch works on a copy of the game's shader
  tree in `%LOCALAPPDATA%\SpaceEngineers.RTHWAccel`, which the plugin
  compiles and the game then reloads. The patched pass reads the traced
  light in place of the game's sun, ambient and emissive light, and the
  game's own lamp passes skip what the traced lamps light.

## Build

You need:
- Visual Studio 2026 (MSVC x64, Roslyn, CMake/Ninja);
- the Windows SDK's `dxc`;
- the .NET Framework 4.8 reference assemblies;
- an installed copy of the game.

DLSS Ray Reconstruction is optional. [scripts/fetch_dlss.ps1](scripts/fetch_dlss.ps1)
downloads the pinned NVIDIA DLSS SDK files into `third_party/dlss`
(see its `UPSTREAM.md`), and the build enables Ray Reconstruction when they
are present. DLSS Frame Generation is optional the same way:
[scripts/fetch_streamline.ps1](scripts/fetch_streamline.ps1) downloads the
pinned NVIDIA Streamline SDK into `third_party/streamline`, checked against
its SHA-256 and NVIDIA's signature.

```bat
"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
powershell -ExecutionPolicy Bypass -File scripts\fetch_dlss.ps1
powershell -ExecutionPolicy Bypass -File scripts\fetch_streamline.ps1
cmake -S . -B build-x64 -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo ^
  -DSERT_GAME_BIN64="D:/Program Files/Games/Steam/steamapps/common/SpaceEngineers/Bin64"
cmake --build build-x64
ctest --test-dir build-x64 --output-on-failure
```

The tests check the hooks and the scene mirror's member lookups against the
installed game's assemblies. They also apply the shader patch to the game's
own shaders (read only) and compile it in every variant the game uses.

Packaging works as in HighFPSFix (`package_plugin_only`, and
`package_loader_bundled` with `SERT_ULTIMATE_ASI_LOADER_DLL`). It produces
`dist/SpaceEngineers.RTHWAccel-<version>-plugin-only.zip` and
`dist/SpaceEngineers.RTHWAccel-<version>-with-Ultimate-ASI-Loader.zip`, with
the release INI and READMEs from [packaging/](packaging/).

## Development runs

Builds and game sessions run through the shared queue
(`D:\Githubs\_agents\queue_executor`).

[scripts/run_session.ps1](scripts/run_session.ps1) handles one run:
- installs the loader, the plugin and an INI into `Bin64` for that run only;
- starts the game through Steam and loads a test world from
  `research/worlds`;
- collects the logs, gate traces and screenshots into
  `research/runs/<stamp>-<name>`;
- removes everything again.

```powershell
& 'D:\Githubs\_agents\queue_executor\queue.ps1' submit -Name 'inspector, RedShip' -Owner 'SpaceEngineers-RTHWAccel' `
    -Command "& 'D:\Githubs\SpaceEngineers\SpaceEngineers-RTHWAccel\scripts\run_session.ps1' -Name inspector -World RedShip -Seconds 5 -Inspector"
```

Its main switches:
- `-Inspector` runs every gate mode and debug view, with screenshots and
  timing windows;
- `-GateMode` and `-DebugView` set the mode for a session;
- `-PackageDir` installs a release folder from `dist/release` instead of
  the build;
- `-WithHighFps` installs the HighFPSFix release build alongside;
- `-FreeCamera` makes the game's spectator camera (F8) available in any
  world the machine runs, for screenshots (`[Debug] FreeCamera`).

The development INI ([config/](config/)) lists the debug views and the
`[Debug]` settings: the automation, a free photo camera (F8: W/A/S/D, Space
up, Ctrl down, Shift and Alt for speed, Q/E roll, the mouse wheel zooms), a
screenshot key (F12 saves the frame without the HUD as a PNG in the game
folder's `Screenshots`) and a tracing toggle (F2: the game's own lighting and
back, for before/after shots).

## Logs

`SpaceEngineers.RTHWAccel.log` is written beside the ASI (or under
`%LOCALAPPDATA%\SpaceEngineers.RTHWAccel`) and replaced on each launch. The
previous session is kept as `SpaceEngineers.RTHWAccel.previous.log`.

With `Diagnostics = true`, statistics are logged every five seconds:
- the gate's frame, CPU and GPU times;
- how well the traced primary rays match the game's G-buffer;
- the traced material;
- the light sampling and the light grid;
- the scene mirror and texture streaming.

The private `research/` directory (plan, probes, runs, test worlds) is
excluded from Git. `research/tools` links to the shared tools folder.

## Third-party

- [MinHook](third_party/minhook/) for function hooking, with a local patch
  for JIT-compiled AVX prologues (its `UPSTREAM.md`).
- [Ultimate ASI Loader](https://github.com/ThirteenAG/Ultimate-ASI-Loader)
  (binary not stored in Git) for the loader-bundled archive.
- [NVIDIA DLSS SDK](https://github.com/NVIDIA/DLSS) (not stored in Git) for
  Ray Reconstruction, under the NVIDIA RTX SDKs License.
- [NVIDIA Streamline](https://github.com/NVIDIAGameWorks/Streamline) (not
  stored in Git) for DLSS Frame Generation and Reflex. Streamline is under
  the MIT License, DLSS-G under the NVIDIA RTX SDKs License, and Reflex
  under the NVIDIA SDK license.

## License

MIT, see [LICENSE](LICENSE). MinHook, the Ultimate ASI Loader and NVIDIA's
DLSS and Streamline runtimes keep their own licenses
([third_party/](third_party/)), which are included in the release
archives.
