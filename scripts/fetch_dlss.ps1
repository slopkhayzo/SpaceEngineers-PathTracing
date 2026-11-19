<#
.SYNOPSIS
  Downloads the parts of NVIDIA's DLSS SDK the plugin builds against (DLSS Ray
  Reconstruction): headers, the x64 static-CRT NGX library, the release
  runtime nvngx_dlssd.dll, the licence and the integration guides.

.DESCRIPTION
  Pinned to one tag of https://github.com/NVIDIA/DLSS (third_party/dlss/UPSTREAM.md
  records it and the files' SHA-256). The files land in third_party/dlss/ under
  the SDK's own layout and stay out of git (.gitignore); the build enables Ray
  Reconstruction when they are present. The SDK is under the NVIDIA RTX SDKs
  License (third_party/dlss/LICENSE.txt): NVIDIA GPUs only, attribution, and a
  notice to NVIDIA before a commercial release.

.EXAMPLE
  scripts\fetch_dlss.ps1
#>
[CmdletBinding()]
param(
    [string]$Tag = 'v310.9.16',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'third_party\dlss'
$files = @(
    'LICENSE.txt',
    'README.md',
    'doc/DLSS-RR Integration Guide.pdf',
    'doc/DLSS_Programming_Guide_Release.pdf',
    'include/nvsdk_ngx.h',
    'include/nvsdk_ngx_defs.h',
    'include/nvsdk_ngx_defs_dlssd.h',
    'include/nvsdk_ngx_defs_vk.h',
    'include/nvsdk_ngx_helpers.h',
    'include/nvsdk_ngx_helpers_cuda.h',
    'include/nvsdk_ngx_helpers_d3d.h',
    'include/nvsdk_ngx_helpers_dlssd.h',
    'include/nvsdk_ngx_helpers_dlssd_cuda.h',
    'include/nvsdk_ngx_helpers_dlssd_d3d.h',
    'include/nvsdk_ngx_helpers_dlssd_vk.h',
    'include/nvsdk_ngx_helpers_vk.h',
    'include/nvsdk_ngx_loader.h',
    'include/nvsdk_ngx_params.h',
    'include/nvsdk_ngx_params_dlssd.h',
    'include/nvsdk_ngx_standalone_common.h',
    'include/nvsdk_ngx_standalone_cuda.h',
    'include/nvsdk_ngx_vk.h',
    'lib/Windows_x86_64/x64/nvsdk_ngx_s.lib',
    'lib/Windows_x86_64/rel/nvngx_dlssd.dll',
    'utils/ngx_driver_onscreenindicator.reg',
    'utils/ngx_driver_onscreenindicator_off.reg',
    'utils/ngx_log_on.reg',
    'utils/ngx_log_off.reg'
)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$hashes = New-Object System.Collections.Generic.List[string]
foreach ($file in $files) {
    $destination = Join-Path $target ($file -replace '/', '\')
    if ((Test-Path $destination) -and -not $Force) {
        Write-Host "present: $file"
    } else {
        New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
        $path = (($file -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
        $url = "https://raw.githubusercontent.com/NVIDIA/DLSS/$Tag/$path"
        Write-Host "downloading: $file"
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $destination
    }
    $hash = (Get-FileHash -Algorithm SHA256 $destination).Hash.ToLowerInvariant()
    $size = (Get-Item $destination).Length
    $hashes.Add("$hash  $size  $file")
}
$hashes | Set-Content -Encoding UTF8 (Join-Path $target 'SHA256SUMS.txt')
Write-Host "DLSS SDK $Tag files in $target ($($files.Count) files); hashes in SHA256SUMS.txt."
