cmake_minimum_required(VERSION 3.24)

project(
    SpaceEngineersRTHWAccel
    VERSION 0.1.0
    DESCRIPTION "Hardware ray tracing for Space Engineers"
    LANGUAGES C CXX
)

if(NOT WIN32 OR NOT MSVC)
    message(FATAL_ERROR "SpaceEngineers.RTHWAccel supports Windows/MSVC only")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR
        "Configure an x64 build environment; SpaceEngineers.exe is a 64-bit process")
endif()

set(SERT_NAME "SpaceEngineers.RTHWAccel")

# ---------------------------------------------------------------------------
# Shaders of the ray-tracing core, compiled to DXIL at build time and embedded
# in the plugin (no compiler is shipped).

find_program(SERT_DXC dxc
    HINTS "$ENV{WindowsSdkVerBinPath}x64"
    DOC "DXC from the Windows SDK (shader model 6.x, D3D12 side)")
find_program(SERT_FXC fxc
    HINTS "$ENV{WindowsSdkVerBinPath}x64"
    DOC "FXC from the Windows SDK (shader model 5.0, D3D11 side)")
if(NOT SERT_DXC OR NOT SERT_FXC)
    message(FATAL_ERROR "dxc.exe/fxc.exe not found; build from a Visual Studio x64 developer environment")
endif()

set(sert_generated "${CMAKE_BINARY_DIR}/generated")
set(sert_shader_headers "")
# <name>_cs.h defines `variable` as the DXIL of `source` (entry point main).
function(sert_compute_shader name source profile variable)
    set(header "${sert_generated}/${name}_cs.h")
    add_custom_command(
        OUTPUT "${header}"
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${sert_generated}"
        COMMAND "${SERT_DXC}" -nologo -T ${profile} -E main -O3 -Qstrip_debug -Qstrip_reflect
                -Fh "${header}" -Vn ${variable} "${CMAKE_SOURCE_DIR}/${source}"
        DEPENDS "${CMAKE_SOURCE_DIR}/${source}" ${sert_shader_includes}
        COMMENT "Compiling ${source} (${profile})"
        VERBATIM
    )
    set(sert_shader_headers ${sert_shader_headers} "${header}" PARENT_SCOPE)
endfunction()

# <name>_cs.h defines `variable` as the DXBC of `source` (D3D11 compute, entry main).
function(sert_d3d11_compute_shader name source variable)
    set(header "${sert_generated}/${name}_cs.h")
    add_custom_command(
        OUTPUT "${header}"
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${sert_generated}"
        COMMAND "${SERT_FXC}" /nologo /T cs_5_0 /E main /O3 /Fh "${header}" /Vn ${variable}
                "${CMAKE_SOURCE_DIR}/${source}"
        DEPENDS "${CMAKE_SOURCE_DIR}/${source}" ${sert_shader_includes}
        COMMENT "Compiling ${source} (cs_5_0)"
        VERBATIM
    )
    set(sert_shader_headers ${sert_shader_headers} "${header}" PARENT_SCOPE)
endfunction()

# The shaders' shared includes (a change rebuilds every shader).
file(GLOB sert_shader_includes CONFIGURE_DEPENDS "${CMAKE_SOURCE_DIR}/src/shaders/*.hlsli")

sert_compute_shader(debug_view src/shaders/debug_view.hlsl cs_6_0 g_debugViewCs)
sert_compute_shader(denoise src/shaders/denoise.hlsl cs_6_0 g_denoiseCs)
sert_compute_shader(emissive_lights src/shaders/emissive_lights.hlsl cs_6_5 g_emissiveLightsCs)
sert_compute_shader(decode_positions src/shaders/decode_positions.hlsl cs_6_0 g_decodePositionsCs)
sert_compute_shader(expand_blocks src/shaders/expand_blocks.hlsl cs_6_0 g_expandBlocksCs)
sert_compute_shader(skin_vertices src/shaders/skin_vertices.hlsl cs_6_0 g_skinVerticesCs)
sert_compute_shader(primary_view src/shaders/primary_view.hlsl cs_6_5 g_primaryViewCs)
sert_d3d11_compute_shader(pack_gbuffer src/shaders/pack_gbuffer.hlsl g_packGbufferCs)
sert_d3d11_compute_shader(env_atlas src/shaders/env_atlas.hlsl g_envAtlasCs)
add_custom_target(SpaceEngineersRTHWAccelShaders DEPENDS ${sert_shader_headers})

# ---------------------------------------------------------------------------
# Native plugin (the .asi the loader maps; hosts MinHook and the D3D12
# ray-tracing core, starts the managed part)

set(BUILD_SHARED_LIBS OFF CACHE BOOL "Build static dependencies" FORCE)
add_subdirectory(third_party/minhook EXCLUDE_FROM_ALL)
set_property(TARGET minhook PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>"
)

add_library(SpaceEngineersRTHWAccel MODULE
    src/native/bootstrap.cpp
    src/native/clr_host.cpp
    src/native/config.cpp
    src/native/dllmain.cpp
    src/native/logger.cpp
    src/native/native_api.cpp
    src/native/rt/common.cpp
    src/native/rt/dlss.cpp
    src/native/rt/rt_core.cpp
    src/native/rt/scene.cpp
    src/native/rt/textures.cpp
    src/native/rt/vtable_index.c
)
add_dependencies(SpaceEngineersRTHWAccel SpaceEngineersRTHWAccelShaders)
target_include_directories(SpaceEngineersRTHWAccel PRIVATE "${sert_generated}")
target_compile_features(SpaceEngineersRTHWAccel PRIVATE cxx_std_20)
target_compile_definitions(SpaceEngineersRTHWAccel PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    UNICODE
    _UNICODE
)
target_compile_options(SpaceEngineersRTHWAccel PRIVATE
    /W4
    /WX
    /utf-8
    /Zc:preprocessor
    $<$<COMPILE_LANGUAGE:CXX>:/permissive- /EHsc>
)
set_property(TARGET SpaceEngineersRTHWAccel PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>"
)
target_link_options(SpaceEngineersRTHWAccel PRIVATE
    /DYNAMICBASE
    /NXCOMPAT
    /INCREMENTAL:NO
)
target_link_libraries(SpaceEngineersRTHWAccel PRIVATE minhook d3d11 d3d12 dxgi shell32 ole32)

# DLSS Ray Reconstruction (optional): NVIDIA's DLSS SDK in third_party/dlss,
# fetched by scripts/fetch_dlss.ps1 (third_party/dlss/UPSTREAM.md). Without it
# the plugin builds with Ray Reconstruction unavailable.
set(SERT_DLSS_DIR "${CMAKE_SOURCE_DIR}/third_party/dlss")
set(SERT_DLSS_RUNTIME "${SERT_DLSS_DIR}/lib/Windows_x86_64/rel/nvngx_dlssd.dll")
if(EXISTS "${SERT_DLSS_DIR}/include/nvsdk_ngx.h" AND
   EXISTS "${SERT_DLSS_DIR}/lib/Windows_x86_64/x64/nvsdk_ngx_s.lib" AND EXISTS "${SERT_DLSS_RUNTIME}")
    message(STATUS "DLSS SDK found: building with Ray Reconstruction")
    set(SERT_DLSS_FOUND TRUE)
    set(sert_dlss_package_args
        "-DDLSS_RUNTIME_FILE=${SERT_DLSS_RUNTIME}"
        "-DDLSS_LICENSE_FILE=${SERT_DLSS_DIR}/LICENSE.txt"
    )
    target_include_directories(SpaceEngineersRTHWAccel SYSTEM PRIVATE "${SERT_DLSS_DIR}/include")
    target_link_libraries(SpaceEngineersRTHWAccel PRIVATE "${SERT_DLSS_DIR}/lib/Windows_x86_64/x64/nvsdk_ngx_s.lib")
    target_compile_definitions(SpaceEngineersRTHWAccel PRIVATE SERT_DLSS=1)
    add_custom_command(TARGET SpaceEngineersRTHWAccel POST_BUILD
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${SERT_DLSS_RUNTIME}"
                "$<TARGET_FILE_DIR:SpaceEngineersRTHWAccel>/nvngx_dlssd.dll"
        VERBATIM
    )
else()
    message(STATUS "DLSS SDK not found (scripts/fetch_dlss.ps1): building without Ray Reconstruction")
    set(SERT_DLSS_FOUND FALSE)
    set(sert_dlss_package_args "")
endif()
set_target_properties(SpaceEngineersRTHWAccel PROPERTIES
    OUTPUT_NAME "${SERT_NAME}"
    PREFIX ""
    SUFFIX ".asi"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

add_custom_command(TARGET SpaceEngineersRTHWAccel POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
            "${CMAKE_SOURCE_DIR}/config/${SERT_NAME}.ini"
            "$<TARGET_FILE_DIR:SpaceEngineersRTHWAccel>/${SERT_NAME}.ini"
    VERBATIM
)

# ---------------------------------------------------------------------------
# Managed part (C#, .NET Framework 4.8), compiled with the Roslyn compiler that
# ships with Visual Studio against the game's own assemblies.

set(SERT_GAME_BIN64 "D:/Program Files/Games/Steam/steamapps/common/SpaceEngineers/Bin64"
    CACHE PATH "Space Engineers Bin64 folder (compile-time references of the managed part)")
set(SERT_NETFX_REFERENCE_DIR
    "C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8"
    CACHE PATH ".NET Framework 4.8 reference assemblies")

if(NOT SERT_CSC)
    # cl.exe lives in <VS>/VC/Tools/MSVC/<version>/bin/Host<arch>/<arch>.
    get_filename_component(sert_vs_root "${CMAKE_CXX_COMPILER}" DIRECTORY)
    foreach(level RANGE 1 7)
        get_filename_component(sert_vs_root "${sert_vs_root}" DIRECTORY)
    endforeach()
    find_program(SERT_CSC csc.exe
        HINTS "${sert_vs_root}/MSBuild/Current/Bin/Roslyn"
        NO_DEFAULT_PATH)
endif()
if(NOT SERT_CSC)
    message(FATAL_ERROR "Roslyn csc.exe not found; set SERT_CSC")
endif()
foreach(required IN ITEMS "${SERT_NETFX_REFERENCE_DIR}/mscorlib.dll" "${SERT_GAME_BIN64}/VRage.Render.dll")
    if(NOT EXISTS "${required}")
        message(FATAL_ERROR "Missing ${required}; set SERT_NETFX_REFERENCE_DIR / SERT_GAME_BIN64")
    endif()
endforeach()

set(SERT_MANAGED_SOURCES
    src/managed/Compiled.cs
    src/managed/DebugAutomation.cs
    src/managed/Detour.cs
    src/managed/FreeCamera.cs
    src/managed/GameFrame.cs
    src/managed/GameReflection.cs
    src/managed/Hotkeys.cs
    src/managed/LampMirror.cs
    src/managed/LightingHooks.cs
    src/managed/Log.cs
    src/managed/NativeApi.cs
    src/managed/PhotoControls.cs
    src/managed/Plugin.cs
    src/managed/RenderThread.cs
    src/managed/SceneMirror.cs
    src/managed/Settings.cs
    src/managed/ShaderPatch.cs
    src/managed/ShaderPatcher.cs
)
set(SERT_GAME_REFERENCES
    Sandbox.Common.dll
    Sandbox.Game.dll
    Sandbox.Graphics.dll
    SharpDX.dll
    SharpDX.D3DCompiler.dll
    SharpDX.Direct3D11.dll
    SpaceEngineers.Game.dll
    VRage.dll
    VRage.Game.dll
    VRage.Input.dll
    VRage.Library.dll
    VRage.Math.dll
    VRage.Render.dll
)

set(sert_managed_rsp "${CMAKE_BINARY_DIR}/managed.rsp")
set(sert_managed_dll "${CMAKE_BINARY_DIR}/bin/${SERT_NAME}.Managed.dll")
set(sert_rsp_lines
    "-nologo"
    "-nostdlib+"
    "-target:library"
    "-unsafe+"
    "-optimize+"
    "-deterministic+"
    "-langversion:latest"
    "-warnaserror+"
    "-debug:portable"
    "-out:\"${sert_managed_dll}\""
    "-reference:\"${SERT_NETFX_REFERENCE_DIR}/mscorlib.dll\""
    "-reference:\"${SERT_NETFX_REFERENCE_DIR}/System.dll\""
    "-reference:\"${SERT_NETFX_REFERENCE_DIR}/System.Core.dll\""
)
file(GLOB sert_facades "${SERT_NETFX_REFERENCE_DIR}/Facades/*.dll")
foreach(facade IN LISTS sert_facades)
    list(APPEND sert_rsp_lines "-reference:\"${facade}\"")
endforeach()
foreach(reference IN LISTS SERT_GAME_REFERENCES)
    list(APPEND sert_rsp_lines "-reference:\"${SERT_GAME_BIN64}/${reference}\"")
endforeach()
foreach(source IN LISTS SERT_MANAGED_SOURCES)
    list(APPEND sert_rsp_lines "\"${CMAKE_SOURCE_DIR}/${source}\"")
endforeach()
list(JOIN sert_rsp_lines "\n" sert_rsp_text)
file(WRITE "${sert_managed_rsp}" "${sert_rsp_text}\n")

add_custom_command(
    OUTPUT "${sert_managed_dll}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${CMAKE_BINARY_DIR}/bin"
    COMMAND "${SERT_CSC}" -noconfig "@${sert_managed_rsp}"
    DEPENDS ${SERT_MANAGED_SOURCES} "${sert_managed_rsp}"
    COMMENT "Compiling ${SERT_NAME}.Managed.dll"
    VERBATIM
)
add_custom_target(SpaceEngineersRTHWAccelManaged ALL DEPENDS "${sert_managed_dll}")
add_dependencies(SpaceEngineersRTHWAccel SpaceEngineersRTHWAccelManaged)

# ---------------------------------------------------------------------------
# Game-free tests: the runtime shader patch against the installed game's own
# shader sources (read only).

enable_testing()
set(sert_tests_rsp "${CMAKE_BINARY_DIR}/managed_tests.rsp")
set(sert_tests_exe "${CMAKE_BINARY_DIR}/tests/${SERT_NAME}.Tests.exe")
set(SERT_TEST_SOURCES
    tests/ManagedTests.cs
    src/managed/PhotoControls.cs
    src/managed/ShaderPatch.cs
    src/managed/Settings.cs
    # The scene mirror's member lookups, resolved against the installed game.
    src/managed/Compiled.cs
    src/managed/Detour.cs
    src/managed/GameFrame.cs
    src/managed/GameReflection.cs
    src/managed/LampMirror.cs
    src/managed/Log.cs
    src/managed/NativeApi.cs
    src/managed/RenderThread.cs
    src/managed/SceneMirror.cs
)
set(SERT_TEST_GAME_REFERENCES
    SharpDX.dll
    SharpDX.D3DCompiler.dll
    SharpDX.Direct3D11.dll
    VRage.dll
    VRage.Library.dll
    VRage.Math.dll
    VRage.Render.dll
)
set(sert_tests_rsp_lines
    "-nologo"
    "-nostdlib+"
    "-target:exe"
    "-unsafe+"
    "-optimize+"
    "-langversion:latest"
    "-warnaserror+"
    "-out:\"${sert_tests_exe}\""
    "-reference:\"${SERT_NETFX_REFERENCE_DIR}/mscorlib.dll\""
    "-reference:\"${SERT_NETFX_REFERENCE_DIR}/System.dll\""
    "-reference:\"${SERT_NETFX_REFERENCE_DIR}/System.Core.dll\""
)
foreach(reference IN LISTS SERT_TEST_GAME_REFERENCES)
    list(APPEND sert_tests_rsp_lines "-reference:\"${SERT_GAME_BIN64}/${reference}\"")
endforeach()
foreach(facade IN LISTS sert_facades)
    list(APPEND sert_tests_rsp_lines "-reference:\"${facade}\"")
endforeach()
foreach(source IN LISTS SERT_TEST_SOURCES)
    list(APPEND sert_tests_rsp_lines "\"${CMAKE_SOURCE_DIR}/${source}\"")
endforeach()
list(JOIN sert_tests_rsp_lines "\n" sert_tests_rsp_text)
file(WRITE "${sert_tests_rsp}" "${sert_tests_rsp_text}\n")

add_custom_command(
    OUTPUT "${sert_tests_exe}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${CMAKE_BINARY_DIR}/tests"
    COMMAND "${SERT_CSC}" -noconfig "@${sert_tests_rsp}"
    # The tests load SharpDX from their own folder.
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${SERT_GAME_BIN64}/SharpDX.dll"
            "${SERT_GAME_BIN64}/SharpDX.D3DCompiler.dll" "${CMAKE_BINARY_DIR}/tests"
    DEPENDS ${SERT_TEST_SOURCES} "${sert_tests_rsp}"
    COMMENT "Compiling ${SERT_NAME}.Tests.exe"
    VERBATIM
)
add_custom_target(SpaceEngineersRTHWAccelTests ALL DEPENDS "${sert_tests_exe}")
get_filename_component(sert_game_root "${SERT_GAME_BIN64}" DIRECTORY)
add_test(NAME managed_tests COMMAND "${sert_tests_exe}" "${sert_game_root}/Content/Shaders")

# ---------------------------------------------------------------------------
# Release archives ship packaging/SpaceEngineers.RTHWAccel.ini.
# config/SpaceEngineers.RTHWAccel.ini is the development default (diagnostics
# on) and stays beside the built plugin.

set(SERT_PACKAGE_SCRIPT "${CMAKE_SOURCE_DIR}/cmake/package.cmake")
set(SERT_ULTIMATE_ASI_LOADER_DLL "" CACHE FILEPATH
    "Optional x64 Ultimate ASI Loader (v9.7.4) DLL used for the loader-bundled package")
# File name the loader is shipped under; see third_party/ultimate-asi-loader/UPSTREAM.md.
unset(SERT_LOADER_NAME CACHE)
set(SERT_LOADER_NAME "dinput8.dll")

add_custom_target(package_plugin_only
    COMMAND "${CMAKE_COMMAND}"
            -DMODE=plugin-only
            -DNAME=${SERT_NAME}
            -DVERSION=${PROJECT_VERSION}
            -DPLUGIN_FILE=$<TARGET_FILE:SpaceEngineersRTHWAccel>
            -DMANAGED_FILE=${sert_managed_dll}
            -DINI_FILE=${CMAKE_SOURCE_DIR}/packaging/${SERT_NAME}.ini
            -DREADME_FILE=${CMAKE_SOURCE_DIR}/packaging/README.txt
            -DMINHOOK_LICENSE_FILE=${CMAKE_SOURCE_DIR}/third_party/minhook/LICENSE.txt
            -DOUTPUT_DIR=${CMAKE_SOURCE_DIR}/dist/release/${SERT_NAME}-${PROJECT_VERSION}-plugin-only
            -DARCHIVE_FILE=${CMAKE_SOURCE_DIR}/dist/${SERT_NAME}-${PROJECT_VERSION}-plugin-only.zip
            ${sert_dlss_package_args}
            -P "${SERT_PACKAGE_SCRIPT}"
    DEPENDS SpaceEngineersRTHWAccel SpaceEngineersRTHWAccelManaged
    VERBATIM
)

if(SERT_ULTIMATE_ASI_LOADER_DLL AND EXISTS "${SERT_ULTIMATE_ASI_LOADER_DLL}")
    add_custom_target(package_loader_bundled
        COMMAND "${CMAKE_COMMAND}"
                -DMODE=loader-bundled
                -DNAME=${SERT_NAME}
                -DVERSION=${PROJECT_VERSION}
                -DPLUGIN_FILE=$<TARGET_FILE:SpaceEngineersRTHWAccel>
                -DMANAGED_FILE=${sert_managed_dll}
                -DINI_FILE=${CMAKE_SOURCE_DIR}/packaging/${SERT_NAME}.ini
                -DREADME_FILE=${CMAKE_SOURCE_DIR}/packaging/README-loader.txt
                -DMINHOOK_LICENSE_FILE=${CMAKE_SOURCE_DIR}/third_party/minhook/LICENSE.txt
                -DLOADER_FILE=${SERT_ULTIMATE_ASI_LOADER_DLL}
                -DLOADER_NAME=${SERT_LOADER_NAME}
                -DLOADER_LICENSE_FILE=${CMAKE_SOURCE_DIR}/third_party/ultimate-asi-loader/LICENSE.txt
                -DOUTPUT_DIR=${CMAKE_SOURCE_DIR}/dist/release/${SERT_NAME}-${PROJECT_VERSION}-with-Ultimate-ASI-Loader
                -DARCHIVE_FILE=${CMAKE_SOURCE_DIR}/dist/${SERT_NAME}-${PROJECT_VERSION}-with-Ultimate-ASI-Loader.zip
                ${sert_dlss_package_args}
                -P "${SERT_PACKAGE_SCRIPT}"
        DEPENDS SpaceEngineersRTHWAccel SpaceEngineersRTHWAccelManaged
        VERBATIM
    )
endif()
