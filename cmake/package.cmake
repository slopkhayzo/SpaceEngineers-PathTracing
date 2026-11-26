cmake_minimum_required(VERSION 3.24)

foreach(required IN ITEMS MODE NAME VERSION PLUGIN_FILE MANAGED_FILE INI_FILE README_FILE
                          MINHOOK_LICENSE_FILE OUTPUT_DIR ARCHIVE_FILE)
    if(NOT DEFINED ${required} OR "${${required}}" STREQUAL "")
        message(FATAL_ERROR "Missing packaging argument: ${required}")
    endif()
endforeach()

if(NOT MODE STREQUAL "plugin-only" AND NOT MODE STREQUAL "loader-bundled")
    message(FATAL_ERROR "Unknown package mode: ${MODE}")
endif()

foreach(input IN ITEMS PLUGIN_FILE MANAGED_FILE INI_FILE README_FILE MINHOOK_LICENSE_FILE)
    if(NOT EXISTS "${${input}}")
        message(FATAL_ERROR "Packaging input does not exist: ${${input}}")
    endif()
endforeach()

if(MODE STREQUAL "loader-bundled")
    foreach(input IN ITEMS LOADER_FILE LOADER_LICENSE_FILE LOADER_NAME)
        if(NOT DEFINED ${input} OR "${${input}}" STREQUAL "")
            message(FATAL_ERROR "Loader packaging input missing: ${input}")
        endif()
    endforeach()
    foreach(input IN ITEMS LOADER_FILE LOADER_LICENSE_FILE)
        if(NOT EXISTS "${${input}}")
            message(FATAL_ERROR "Loader packaging input does not exist: ${${input}}")
        endif()
    endforeach()
    # Only the verified x64 Ultimate ASI Loader v9.7.4 binary may be bundled.
    file(SHA512 "${LOADER_FILE}" loader_hash)
    string(TOUPPER "${loader_hash}" loader_hash)
    if(NOT loader_hash STREQUAL
       "840C97B7B3ED40DE3D4E6372D9BD43B022796F4EA57C71969B5DED978B344ED5A70579318337877B9AAD8C6F0E74CE19B32F1AC5D4C5E5679D5F4682819937FA")
        message(FATAL_ERROR
            "Loader binary is not the verified x64 Ultimate ASI Loader v9.7.4")
    endif()
endif()

file(READ "${README_FILE}" readme_contents)
set(required_misc_line
    "For more game fixes, visit https://slop-blog.enkhayzomachines.net/fixes :)")
string(FIND "${readme_contents}" "Misc" misc_heading_offset)
string(FIND "${readme_contents}" "${required_misc_line}" misc_line_offset)
if(misc_heading_offset EQUAL -1 OR misc_line_offset EQUAL -1)
    message(FATAL_ERROR "Release README must retain the Misc section and fixes URL")
endif()

file(REMOVE_RECURSE "${OUTPUT_DIR}")
file(MAKE_DIRECTORY "${OUTPUT_DIR}")
file(COPY_FILE "${PLUGIN_FILE}"
     "${OUTPUT_DIR}/${NAME}.asi" ONLY_IF_DIFFERENT)
file(COPY_FILE "${MANAGED_FILE}"
     "${OUTPUT_DIR}/${NAME}.Managed.dll" ONLY_IF_DIFFERENT)
file(COPY_FILE "${INI_FILE}"
     "${OUTPUT_DIR}/${NAME}.ini" ONLY_IF_DIFFERENT)
file(COPY_FILE "${README_FILE}" "${OUTPUT_DIR}/README.txt" ONLY_IF_DIFFERENT)
file(COPY_FILE "${MINHOOK_LICENSE_FILE}"
     "${OUTPUT_DIR}/MINHOOK-LICENSE.txt" ONLY_IF_DIFFERENT)

set(package_files
    ${NAME}.asi
    ${NAME}.Managed.dll
    ${NAME}.ini
    README.txt
    MINHOOK-LICENSE.txt
)

# DLSS Ray Reconstruction's runtime and NVIDIA's licence, when the build has the DLSS SDK.
if(DEFINED DLSS_RUNTIME_FILE AND NOT DLSS_RUNTIME_FILE STREQUAL "")
    foreach(input IN ITEMS DLSS_RUNTIME_FILE DLSS_LICENSE_FILE)
        if(NOT DEFINED ${input} OR NOT EXISTS "${${input}}")
            message(FATAL_ERROR "DLSS packaging input missing: ${input}")
        endif()
    endforeach()
    file(COPY_FILE "${DLSS_RUNTIME_FILE}" "${OUTPUT_DIR}/nvngx_dlssd.dll" ONLY_IF_DIFFERENT)
    file(COPY_FILE "${DLSS_LICENSE_FILE}" "${OUTPUT_DIR}/NVIDIA-DLSS-LICENSE.txt" ONLY_IF_DIFFERENT)
    list(APPEND package_files nvngx_dlssd.dll NVIDIA-DLSS-LICENSE.txt)
endif()

# DLSS Frame Generation: NVIDIA Streamline's runtimes and NVIDIA's licences, when the build has the Streamline SDK
# (third_party/streamline, scripts/fetch_streamline.ps1).
if(DEFINED STREAMLINE_DIR AND NOT STREAMLINE_DIR STREQUAL "")
    set(streamline_runtimes sl.interposer.dll sl.common.dll sl.dlss_g.dll sl.reflex.dll sl.pcl.dll nvngx_dlssg.dll)
    set(streamline_licences
        "license.txt|NVIDIA-STREAMLINE-LICENSE.txt"
        "bin/x64/nvngx_dlss.license.txt|NVIDIA-DLSS-FRAME-GENERATION-LICENSE.txt"
        "bin/x64/reflex.license.txt|NVIDIA-REFLEX-LICENSE.txt"
        "3rd-party-licenses.md|NVIDIA-STREAMLINE-THIRD-PARTY-NOTICES.md")
    foreach(runtime IN LISTS streamline_runtimes)
        if(NOT EXISTS "${STREAMLINE_DIR}/bin/x64/${runtime}")
            message(FATAL_ERROR "Streamline packaging input missing: ${STREAMLINE_DIR}/bin/x64/${runtime}")
        endif()
        file(COPY_FILE "${STREAMLINE_DIR}/bin/x64/${runtime}" "${OUTPUT_DIR}/${runtime}" ONLY_IF_DIFFERENT)
        list(APPEND package_files "${runtime}")
    endforeach()
    foreach(licence IN LISTS streamline_licences)
        string(REPLACE "|" ";" pair "${licence}")
        list(GET pair 0 source)
        list(GET pair 1 target)
        if(NOT EXISTS "${STREAMLINE_DIR}/${source}")
            message(FATAL_ERROR "Streamline packaging input missing: ${STREAMLINE_DIR}/${source}")
        endif()
        file(COPY_FILE "${STREAMLINE_DIR}/${source}" "${OUTPUT_DIR}/${target}" ONLY_IF_DIFFERENT)
        list(APPEND package_files "${target}")
    endforeach()
endif()

# AMD FSR (upscaling and frame generation): AMD's signed runtime and its licence, when the build has the FSR SDK
# (third_party/fsr, scripts/fetch_fsr.ps1). The licence allows redistributing the DLLs unmodified, in binary form,
# with its notices (third_party/fsr/UPSTREAM.md).
if(DEFINED FSR_DIR AND NOT FSR_DIR STREQUAL "")
    set(fsr_runtimes
        amd_fidelityfx_loader_dx12.dll amd_fidelityfx_upscaler_dx12.dll amd_fidelityfx_framegeneration_dx12.dll)
    foreach(runtime IN LISTS fsr_runtimes)
        if(NOT EXISTS "${FSR_DIR}/signedbin/${runtime}")
            message(FATAL_ERROR "FSR packaging input missing: ${FSR_DIR}/signedbin/${runtime}")
        endif()
        file(COPY_FILE "${FSR_DIR}/signedbin/${runtime}" "${OUTPUT_DIR}/${runtime}" ONLY_IF_DIFFERENT)
        list(APPEND package_files "${runtime}")
    endforeach()
    if(NOT EXISTS "${FSR_DIR}/docs/license.md")
        message(FATAL_ERROR "FSR packaging input missing: ${FSR_DIR}/docs/license.md")
    endif()
    file(COPY_FILE "${FSR_DIR}/docs/license.md" "${OUTPUT_DIR}/AMD-FSR-LICENSE.txt" ONLY_IF_DIFFERENT)
    list(APPEND package_files AMD-FSR-LICENSE.txt)
endif()

if(MODE STREQUAL "loader-bundled")
    file(COPY_FILE "${LOADER_FILE}" "${OUTPUT_DIR}/${LOADER_NAME}"
         ONLY_IF_DIFFERENT)
    file(COPY_FILE "${LOADER_LICENSE_FILE}"
         "${OUTPUT_DIR}/ULTIMATE-ASI-LOADER-LICENSE.txt" ONLY_IF_DIFFERENT)
    list(APPEND package_files "${LOADER_NAME}" ULTIMATE-ASI-LOADER-LICENSE.txt)
endif()

set(checksums "")
foreach(file IN LISTS package_files)
    file(SHA256 "${OUTPUT_DIR}/${file}" hash)
    string(TOUPPER "${hash}" hash)
    string(APPEND checksums "${hash}  ${file}\n")
endforeach()
file(WRITE "${OUTPUT_DIR}/SHA256SUMS.txt" "${checksums}")
list(APPEND package_files SHA256SUMS.txt)

get_filename_component(archive_dir "${ARCHIVE_FILE}" DIRECTORY)
file(MAKE_DIRECTORY "${archive_dir}")
file(REMOVE "${ARCHIVE_FILE}")
execute_process(
    COMMAND "${CMAKE_COMMAND}" -E tar cf "${ARCHIVE_FILE}" --format=zip -- ${package_files}
    WORKING_DIRECTORY "${OUTPUT_DIR}"
    COMMAND_ERROR_IS_FATAL ANY
)

message(STATUS "Packaged ${MODE}: ${ARCHIVE_FILE}")
