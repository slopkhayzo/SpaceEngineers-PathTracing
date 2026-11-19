# SpaceEngineers.RTHWAccel

Path-traced lighting for *Space Engineers* (Steam, 64-bit), delivered as an
ASI plugin like [SpaceEngineers.HighFPSFix](https://github.com/slopkhayzo/SpaceEngineers-HighFPS-RIL).

**Status: first release (0.1.0).** The plugin replaces the game's lighting
with light traced by the GPU's ray-tracing hardware:
- sunlight with soft shadows, and light from the sky;
- the game's lamps within 32 m of the camera, with shadows;
- light cast by glowing surfaces (screens, keypads, light strips, suits);
- one bounce of indirect light, diffuse and glossy.

The traced light is denoised by NVIDIA DLSS Ray Reconstruction on GeForce
RTX GPUs, and by the plugin's own filter on other GPUs. The game still
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
  scene: models, cube blocks, deformed armor, voxels and skinned characters.
  The native core keeps bottom-level acceleration structures for its meshes,
  with their materials and textures, and rebuilds the top-level structure
  each frame. The scene's lamps are mirrored too.
- **Tracing.** Compute shaders with inline ray queries (DXR 1.1, shader
  model 6.5) trace jittered primary rays and the sun, sky, lamp and emissive
  light, plus one diffuse and one glossy bounce. A light grid around the
  camera (2 m cells, each listing the strongest lights that reach it) feeds
  the light sampling, and light samples are reused across frames and
  between neighbouring pixels.
- **Denoising.** DLSS Ray Reconstruction (NVIDIA RTX), or the plugin's own
  spatio-temporal filter.
- **Composite.** At start the plugin applies a runtime patch to the game's
  `Lighting/LightDir.hlsl`. The patch works on a copy of the game's shader
  tree in `%LOCALAPPDATA%\SpaceEngineers.RTHWAccel`, which the plugin
  compiles and the game then reloads. The patched pass reads the traced
  light in place of the game's sun, ambient and emissive light. The game's
  own lamp passes keep lighting what lies beyond the light grid.

## Build

You need:
- Visual Studio 2026 (MSVC x64, Roslyn, CMake/Ninja);
- the Windows SDK's `dxc`;
- the .NET Framework 4.8 reference assemblies;
- an installed copy of the game.

DLSS Ray Reconstruction is optional. [scripts/fetch_dlss.ps1](scripts/fetch_dlss.ps1)
downloads the pinned NVIDIA DLSS SDK files into `third_party/dlss`
(see its `UPSTREAM.md`), and the build enables Ray Reconstruction when they
are present.

```bat
"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
powershell -ExecutionPolicy Bypass -File scripts\fetch_dlss.ps1
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

## License

MIT, see [LICENSE](LICENSE). MinHook, the Ultimate ASI Loader and NVIDIA's
DLSS runtime keep their own licenses ([third_party/](third_party/)), which
are included in the release archives.
