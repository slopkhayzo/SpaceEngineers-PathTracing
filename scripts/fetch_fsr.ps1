<#
.SYNOPSIS
  Downloads the parts of AMD's FSR SDK (the FidelityFX SDK before 2.x) the plugin uses: the FFX API headers for
  upscaling and frame generation, AMD's signed D3D12 runtime DLLs (loader, upscaler, frame generation), the licence
  and the technique documentation.

.DESCRIPTION
  Pinned to one commit of https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK (the tag v2.4.0;
  third_party/fsr/UPSTREAM.md records it). Each file is fetched from that commit and checked against its git blob
  hash in the commit's tree; the DLLs also against AMD's Authenticode signature. The files land in third_party/fsr/
  under the SDK's own layout and stay out of git (.gitignore).

  Licence (Kits/FidelityFX/docs/license.md): AMD's signed DLLs may be redistributed in binary form only, unmodified,
  with the licence's notices; no reverse engineering, decompilation or disassembly. The headers are MIT.

.EXAMPLE
  scripts\fetch_fsr.ps1
#>
[CmdletBinding()]
param(
    [string]$Commit = '7539ae8b6568ad29ed78a753404351daf3b5cca7',  # the tag v2.4.0 (2026-10-28)
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'third_party\fsr'

# Each file with its git blob hash (SHA-1 of "blob <size>\0" + the content) in the commit's tree.
$files = [ordered]@{
    'Kits/FidelityFX/api/include/ffx_api.h' = 'd351c168edad86d0f7851bfd8ff5eb77be3667f0'
    'Kits/FidelityFX/api/include/ffx_api.hpp' = '53879977a9d83853afeda48815d4187fa7ebc9dc'
    'Kits/FidelityFX/api/include/ffx_api_types.h' = '380c4c0a1bcc7eb9bb4097f1046de886f9f4c587'
    'Kits/FidelityFX/api/include/ffx_api_loader.h' = 'ac05ec4bbd5d338643825d81c0ffccd5fbbd1c3e'
    'Kits/FidelityFX/api/include/dx12/ffx_api_dx12.h' = '1b711807c597630f4a8d2dfa2572a845c069829a'
    'Kits/FidelityFX/api/include/dx12/ffx_api_dx12.hpp' = 'accebb1943d16c00dcc244fa4044e1d6cfb8d2f4'
    'Kits/FidelityFX/upscalers/include/ffx_upscale.h' = '9d4a3e5f6c60cf601c42bfb9953c6e1a1a481891'
    'Kits/FidelityFX/upscalers/include/ffx_upscale.hpp' = '602687f7203f04c11083dbe3cf6befe848d7868a'
    'Kits/FidelityFX/framegeneration/include/ffx_framegeneration.h' = '823a3b4720f10e03c1fcf6db2a85d7c60d846eac'
    'Kits/FidelityFX/framegeneration/include/ffx_framegeneration.hpp' = 'a8d495a7a0029dbfb7a10359fefd1b4d678ff69b'
    'Kits/FidelityFX/framegeneration/include/ffx_framegeneration_api_types.h' = 'e7e514ddb6666753de3b14f720d8e6774339b0b9'
    'Kits/FidelityFX/framegeneration/include/dx12/ffx_api_framegeneration_dx12.h' = '908b300183983b78f839598c274e2cfc0fdd8651'
    'Kits/FidelityFX/framegeneration/include/dx12/ffx_api_framegeneration_dx12.hpp' = '0fe46f0b08efbbc9481459c22370bb494bf56031'
    'Kits/FidelityFX/signedbin/amd_fidelityfx_loader_dx12.dll' = 'f1afee09dd3bcded005c722b1e94befb3ee3f9e6'
    'Kits/FidelityFX/signedbin/amd_fidelityfx_upscaler_dx12.dll' = '6d01d9f52c412fc336350f04e948eb1bee196a38'
    'Kits/FidelityFX/signedbin/amd_fidelityfx_framegeneration_dx12.dll' = '445674331676665f18b5acd1b3c8621629cb0e75'
    'Kits/FidelityFX/docs/license.md' = 'aef7adddbcf121c1f7f1248fe97de0f7f7a3d719'
    'Kits/FidelityFX/readme.md' = 'cb1ae63091a07f3d9c13fad762f2c8ff08e9ac9c'
    'Kits/FidelityFX/docs/getting-started/ffx-api.md' = 'fceeadce1b16e7d5a6177e4b95690b3c7f084113'
    'Kits/FidelityFX/docs/techniques/super-resolution-upscaler.md' = 'aea7a224c7ea65720693a558f6523ae60231d0be'
    'Kits/FidelityFX/docs/techniques/super-resolution-ml.md' = '08a3551adcda0b22ac8639f66da416f51b449674'
    'Kits/FidelityFX/docs/techniques/frame-interpolation.md' = '8b670e25ffdea52242d9151048914d88928e0d00'
    'Kits/FidelityFX/docs/techniques/frame-interpolation-ml.md' = '5f52ece0437f6f00e500fbf0705e5b5d53988535'
    'Kits/FidelityFX/docs/techniques/frame-interpolation-api.md' = 'df8b9ea7d892c1e98d4b254e626cae4f86d880dd'
    'Kits/FidelityFX/docs/techniques/frame-interpolation-swap-chain.md' = '028db9b39c958e82c09bfaadbd993a2c710c8892'
}

function Get-GitBlobHash([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $header = [Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
    $sha1 = [Security.Cryptography.SHA1]::Create()
    try {
        [void]$sha1.TransformBlock($header, 0, $header.Length, $null, 0)
        [void]$sha1.TransformFinalBlock($bytes, 0, $bytes.Length)
        return (($sha1.Hash | ForEach-Object { $_.ToString('x2') }) -join '')
    } finally {
        $sha1.Dispose()
    }
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
# Downloads in a folder of their own (downloaded content is not run from), moved into place once checked.
$download = Join-Path ([IO.Path]::GetTempPath()) "SpaceEngineers.RTHWAccel-fsr-$($Commit.Substring(0, 12))"
New-Item -ItemType Directory -Force $download | Out-Null
$hashes = New-Object System.Collections.Generic.List[string]
foreach ($file in $files.Keys) {
    $expected = $files[$file]
    $destination = Join-Path $target ($file -replace '/', '\')
    if ($Force -or -not (Test-Path $destination) -or (Get-GitBlobHash $destination) -ne $expected) {
        $temporary = Join-Path $download (($file -split '/')[-1])
        $path = (($file -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
        $url = "https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/$Commit/$path"
        Write-Host "downloading: $file"
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $temporary
        $actual = Get-GitBlobHash $temporary
        if ($actual -ne $expected) {
            throw "$file has the git blob hash $actual, not the pinned $expected."
        }
        New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
        Move-Item -Force $temporary $destination
    } else {
        Write-Host "present: $file"
    }
    if ($file -like '*.dll') {
        $signature = Get-AuthenticodeSignature $destination
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Advanced Micro Devices') {
            throw "$file is not signed by AMD ($($signature.Status), $($signature.SignerCertificate.Subject))."
        }
    }
    $hash = (Get-FileHash -Algorithm SHA256 $destination).Hash.ToLowerInvariant()
    $hashes.Add("$hash  $((Get-Item $destination).Length)  $file")
}
$hashes | Set-Content -Encoding UTF8 (Join-Path $target 'SHA256SUMS.txt')
Write-Host "FSR SDK files at $Commit in $target ($($files.Count) files, DLLs signed by AMD); hashes in SHA256SUMS.txt."
