<#
.SYNOPSIS
  One game session with the development build of the plugin.

.DESCRIPTION
  Installs the Ultimate ASI Loader, the plugin and an INI into the game's Bin64
  folder for the duration of the run only, starts the game through Steam, lets
  the plugin load a test world and quit after -Seconds (unattended runs), or
  waits for the player to quit (-Seconds 0), collects the plugin and game logs,
  traces and screenshots into research\runs\<stamp>-<Name>, and removes
  everything it installed again (also on failure, timeout or a canceled job).

  It launches the game: run it through the shared queue
  (D:\Githubs\_agents\queue_executor).

.EXAMPLE
  scripts\run_session.ps1 -Name inspector -World RedShip -Seconds 10 -Inspector
#>
param(
    [string]$Name = 'session',
    # Folder under research\worlds (copied fresh for each run); empty or 'menu' =
    # main menu only.
    [string]$World = 'RedShip',
    # Seconds in the loaded world (after the inspector sequence, if any) before
    # the plugin quits the game; 0 = the player quits.
    [double]$Seconds = 30,
    # INI overrides, 'Section.Key=Value;Section.Key=Value'.
    [string]$Set = '',
    # [RayTracing] GateMode: off, roundtrip, overlay, replace.
    [ValidateSet('', 'off', 'roundtrip', 'overlay', 'replace')][string]$GateMode = '',
    # Shortcuts for the play profile: [RayTracing] DebugView (lit, lit-raw, ...),
    # Denoiser, DlssMode, CharacterLights, LampIntensity and EmissiveIntensity
    # (numbers, 0 to 100; empty: the INI's).
    [string]$DebugView = '',
    [ValidateSet('', 'auto', 'rr', 'filter')][string]$Denoiser = '',
    [ValidateSet('', 'dlaa', 'quality', 'balanced', 'performance', 'ultra-performance')][string]$DlssMode = '',
    [ValidateSet('', 'on', 'off')][string]$CharacterLights = '',
    [ValidateSet('', 'on', 'off')][string]$DecalLights = '',
    [string]$LampIntensity = '',
    [string]$EmissiveIntensity = '',
    # [Debug] InspectorSequence: every gate mode in turn, screenshots, timings.
    [switch]$Inspector,
    # [Debug] FreeCamera: the game's spectator camera (F8) in this world even if it does not allow it.
    [switch]$FreeCamera,
    # [Debug] TestDecals: bullet holes where the player's view meets a grid, then views of them.
    [switch]$Decals,
    # [Debug] TestMenu: the game's options screen and the plugin's Path tracing screen, with screenshots.
    [switch]$Menu,
    # [Debug] LookAtSkins: views of the nearest block with each armor skin listed (comma-separated subtypes).
    [string]$Skins = '',
    # Also install the HighFPSFix release build (the sister repository's
    # plugin-only package) for this run, with these settings of its INI:
    # [FrameRate] TargetFPS (0: the display's refresh rate, -1: no cap),
    # [Interpolation] Enabled and MouseLook, [General] Diagnostics.
    [switch]$WithHighFps,
    [string]$HighFpsPackage = 'D:\Githubs\SpaceEngineers\SpaceEngineers-HighFPS-RIL\dist\release\SpaceEngineers.HighFPSFix-1.0.1-plugin-only',
    [string]$HighFpsTargetFps = '',
    [ValidateSet('', 'on', 'off')][string]$HighFpsInterpolation = '',
    [ValidateSet('', 'on', 'off')][string]$HighFpsMouseLook = '',
    [switch]$HighFpsDiagnostics,
    [switch]$NoTrace,
    [string]$BuildDir = 'build-x64',
    # Install a release folder (dist\release\...) instead of the build.
    [string]$PackageDir = '',
    [string]$GameBin64 = 'D:\Program Files\Games\Steam\steamapps\common\SpaceEngineers\Bin64',
    [string]$Loader = '',
    # File names the loader is installed under (a .NET game resolves version.dll
    # from System32 only, see third_party\ultimate-asi-loader\UPSTREAM.md).
    [string[]]$LoaderNames = @('dinput8.dll'),
    [int]$TimeoutSeconds = 480,
    [switch]$PresentMon
)

$ErrorActionPreference = 'Stop'
if ($World -eq 'menu') { $World = '' }
$root = Split-Path -Parent $PSScriptRoot
$pluginName = 'SpaceEngineers.RTHWAccel'
$highFpsName = 'SpaceEngineers.HighFPSFix'
if (-not $Loader) { $Loader = Join-Path $root 'research\loader\version.dll' }
$bin = Join-Path $root "$BuildDir\bin"
if ($PackageDir) {
    $bin = (Resolve-Path $PackageDir).Path
    # The bundled loader: any DLL but the plugin's and NVIDIA's runtimes (DLSS's nvngx_*, Streamline's sl.*).
    $bundled = Get-ChildItem $bin -Filter '*.dll' |
        Where-Object { $_.Name -notlike "$pluginName*" -and $_.Name -notlike 'nvngx_*' -and $_.Name -notlike 'sl.*' } |
        Select-Object -First 1
    if ($bundled) { $Loader = $bundled.FullName; $LoaderNames = @($bundled.Name) }
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $root "research\runs\$stamp-$Name"
New-Item -ItemType Directory -Force $runDir | Out-Null
$appData = Join-Path $env:APPDATA 'SpaceEngineers'

function Write-Step([string]$text) { Write-Output ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $text) }

function Set-IniValue([string[]]$lines, [string]$section, [string]$key, [string]$value) {
    $result = New-Object System.Collections.Generic.List[string]
    $inSection = $false; $done = $false; $sawSection = $false
    foreach ($line in $lines) {
        if ($line -match '^\s*\[(.+)\]\s*$') {
            if ($inSection -and -not $done) { $result.Add("$key = $value"); $done = $true }
            $inSection = ($Matches[1] -eq $section)
            if ($inSection) { $sawSection = $true }
        } elseif ($inSection -and -not $done -and $line -match "^\s*$([regex]::Escape($key))\s*=") {
            $result.Add("$key = $value"); $done = $true; continue
        }
        $result.Add($line)
    }
    if (-not $done) {
        # Either the section is missing, or it is the last one and lacks the key.
        if (-not $sawSection) { $result.Add("[$section]") }
        $result.Add("$key = $value")
    }
    return ,$result.ToArray()
}

# An interrupted run (a force-canceled job ends this script without its
# finally block) leaves this file behind; undo what it records first.
$stateFile = Join-Path $root 'research\runs\.session-state.json'

function Restore-Session([string]$path) {
    $state = Get-Content $path -Raw | ConvertFrom-Json
    if ($state.GamePid) {
        $process = Get-Process -Id $state.GamePid -ErrorAction SilentlyContinue
        if ($process -and $process.ProcessName -eq 'SpaceEngineers' -and
            $process.StartTime.ToString('o') -eq $state.GameStart) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
    }
    foreach ($file in @($state.Installed)) {
        if ($file -and (Test-Path $file)) { Remove-Item -Force $file -ErrorAction SilentlyContinue }
    }
    foreach ($backup in @($state.LastSession)) {
        if ($backup -and (Test-Path $backup.Backup)) { Copy-Item -Force $backup.Backup $backup.Path }
    }
    Remove-Item -Force $path
}

if (Test-Path $stateFile) {
    Write-Step 'Undoing an interrupted earlier run.'
    Restore-Session $stateFile
}
if (Get-Process -Name SpaceEngineers -ErrorAction SilentlyContinue) {
    throw 'SpaceEngineers.exe is already running; not starting a session.'
}
foreach ($required in @("$bin\$pluginName.asi", "$bin\$pluginName.Managed.dll", $Loader)) {
    if (-not (Test-Path $required)) { throw "Missing $required" }
}
if ($WithHighFps) {
    foreach ($file in @("$highFpsName.asi", "$highFpsName.Managed.dll", "$highFpsName.ini")) {
        if (-not (Test-Path (Join-Path $HighFpsPackage $file))) { throw "Missing $HighFpsPackage\$file" }
    }
}

# INI: the build's (or the package's) + test-run settings + overrides.
$iniSource = if ($PackageDir) { Join-Path $bin "$pluginName.ini" } else { Join-Path $root "config\$pluginName.ini" }
$ini = Get-Content $iniSource
$worldCopy = ''
if ($World) {
    $worldSource = Join-Path $root "research\worlds\$World"
    if (-not (Test-Path $worldSource)) { throw "Unknown test world $World" }
    $worldCopy = Join-Path $env:TEMP "$pluginName\worlds\$World"
    if (Test-Path $worldCopy) { Remove-Item -Recurse -Force $worldCopy }
    New-Item -ItemType Directory -Force (Split-Path $worldCopy) | Out-Null
    Copy-Item -Recurse $worldSource $worldCopy
}
$ini = Set-IniValue $ini 'Debug' 'AutoLoadWorld' $worldCopy
$ini = Set-IniValue $ini 'Debug' 'AutoQuitSeconds' ([string]$Seconds)
$ini = Set-IniValue $ini 'Debug' 'TracePath' $(if ($NoTrace) { '' } else { Join-Path $runDir 'trace' })
$ini = Set-IniValue $ini 'Debug' 'CapturePath' (Join-Path $runDir 'captures')
if ($Inspector) { $ini = Set-IniValue $ini 'Debug' 'InspectorSequence' 'true' }
if ($FreeCamera) { $ini = Set-IniValue $ini 'Debug' 'FreeCamera' 'true' }
if ($Decals) { $ini = Set-IniValue $ini 'Debug' 'TestDecals' 'true' }
if ($Menu) { $ini = Set-IniValue $ini 'Debug' 'TestMenu' 'true' }
if ($Skins) { $ini = Set-IniValue $ini 'Debug' 'LookAtSkins' $Skins }
if ($GateMode) { $ini = Set-IniValue $ini 'RayTracing' 'GateMode' $GateMode }
if ($DebugView) { $ini = Set-IniValue $ini 'RayTracing' 'DebugView' $DebugView }
if ($Denoiser) { $ini = Set-IniValue $ini 'RayTracing' 'Denoiser' $Denoiser }
if ($DlssMode) { $ini = Set-IniValue $ini 'RayTracing' 'DlssMode' $DlssMode }
if ($CharacterLights) {
    $ini = Set-IniValue $ini 'RayTracing' 'CharacterLights' $(if ($CharacterLights -eq 'on') { 'true' } else { 'false' })
}
if ($DecalLights) {
    $ini = Set-IniValue $ini 'RayTracing' 'DecalLights' $(if ($DecalLights -eq 'on') { 'true' } else { 'false' })
}
foreach ($key in 'LampIntensity', 'EmissiveIntensity') {
    $text = Get-Variable -Name $key -ValueOnly
    if (-not $text) { continue }
    $number = 0.0
    if (-not [double]::TryParse($text, [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or $number -lt 0 -or $number -gt 100) {
        throw "-$key must be a number from 0 to 100, not '$text'"
    }
    $ini = Set-IniValue $ini 'RayTracing' $key $number.ToString([Globalization.CultureInfo]::InvariantCulture)
}
foreach ($assignment in ($Set -split ';' | Where-Object { $_.Trim() })) {
    if ($assignment -notmatch '^\s*([^.=]+)\.([^=]+?)\s*=\s*(.*)$') { throw "Bad -Set entry '$assignment'" }
    $ini = Set-IniValue $ini $Matches[1].Trim() $Matches[2].Trim() $Matches[3].Trim()
}

$state = [ordered]@{ Installed = @(); LastSession = @(); GamePid = 0; GameStart = '' }
function Save-State { $state | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $stateFile }
function Add-Installed([string]$path) { $state.Installed += $path; Save-State }

# The game records the loaded test world as the last session; keep the
# player's "Continue" pointing at their own world.
$index = 0
$lastSessionFiles = if ($World) { Get-ChildItem -Path (Join-Path $appData 'Saves') -Filter LastSession.sbl -Recurse -ErrorAction SilentlyContinue } else { @() }
foreach ($file in $lastSessionFiles) {
    $backup = Join-Path $runDir ("LastSession.{0}.sbl.bak" -f $index++)
    Copy-Item $file.FullName $backup
    $state.LastSession += [ordered]@{ Path = $file.FullName; Backup = $backup }
}
Save-State
$gameProcess = $null
$presentMonProcess = $null
$script:loaderMissed = $false
$started = Get-Date

try {
    # Install for this run only.
    foreach ($loaderName in $LoaderNames) {
        $loaderTarget = Join-Path $GameBin64 $loaderName
        if (Test-Path $loaderTarget) {
            if ((Get-FileHash $loaderTarget).Hash -ne (Get-FileHash $Loader).Hash) {
                throw "A different $loaderName is already in $GameBin64; not replacing it."
            }
            Write-Step "$loaderName (same loader) already present; leaving it."
        } else {
            Add-Installed $loaderTarget; Copy-Item $Loader $loaderTarget
        }
    }
    foreach ($file in @("$pluginName.asi", "$pluginName.Managed.dll", "$pluginName.Managed.pdb")) {
        $source = Join-Path $bin $file
        if (Test-Path $source) {
            $target = Join-Path $GameBin64 $file
            Add-Installed $target; Copy-Item -Force $source $target
        }
    }
    # The NVIDIA runtimes the build ships next to the plugin (DLSS Ray Reconstruction with the DLSS SDK; Streamline
    # and DLSS Frame Generation with the Streamline SDK); never over another copy.
    foreach ($runtime in @('nvngx_dlssd.dll', 'sl.interposer.dll', 'sl.common.dll', 'sl.dlss_g.dll', 'sl.reflex.dll',
                           'sl.pcl.dll', 'nvngx_dlssg.dll')) {
        $runtimeSource = Join-Path $bin $runtime
        if (-not (Test-Path $runtimeSource)) { continue }
        $runtimeTarget = Join-Path $GameBin64 $runtime
        if (Test-Path $runtimeTarget) {
            if ((Get-FileHash $runtimeTarget).Hash -ne (Get-FileHash $runtimeSource).Hash) {
                throw "A different $runtime is already in $GameBin64; not replacing it."
            }
            Write-Step "$runtime (same file) already present; leaving it."
        } else {
            Add-Installed $runtimeTarget; Copy-Item $runtimeSource $runtimeTarget
        }
    }
    $iniTarget = Join-Path $GameBin64 "$pluginName.ini"
    Add-Installed $iniTarget; [System.IO.File]::WriteAllLines($iniTarget, $ini)
    Copy-Item $iniTarget (Join-Path $runDir "$pluginName.ini")
    $logNames = @("$pluginName.log", "$pluginName.previous.log")
    if ($WithHighFps) {
        foreach ($file in @("$highFpsName.asi", "$highFpsName.Managed.dll", "$highFpsName.ini")) {
            $target = Join-Path $GameBin64 $file
            if (Test-Path $target) { throw "$target is already installed; not replacing it." }
            Add-Installed $target; Copy-Item (Join-Path $HighFpsPackage $file) $target
        }
        # Its INI as packaged, with this run's settings.
        $highFpsIniTarget = Join-Path $GameBin64 "$highFpsName.ini"
        $highFpsIni = Get-Content $highFpsIniTarget
        if ($HighFpsTargetFps) { $highFpsIni = Set-IniValue $highFpsIni 'FrameRate' 'TargetFPS' $HighFpsTargetFps }
        if ($HighFpsInterpolation) {
            $highFpsIni = Set-IniValue $highFpsIni 'Interpolation' 'Enabled' $(if ($HighFpsInterpolation -eq 'on') { 'true' } else { 'false' })
        }
        if ($HighFpsMouseLook) {
            $highFpsIni = Set-IniValue $highFpsIni 'Interpolation' 'MouseLook' $(if ($HighFpsMouseLook -eq 'on') { 'true' } else { 'false' })
        }
        if ($HighFpsDiagnostics) { $highFpsIni = Set-IniValue $highFpsIni 'General' 'Diagnostics' 'true' }
        [System.IO.File]::WriteAllLines($highFpsIniTarget, $highFpsIni)
        Copy-Item $highFpsIniTarget (Join-Path $runDir "$highFpsName.ini")
        $logNames += @("$highFpsName.log", "$highFpsName.previous.log")
        Write-Step "HighFPSFix installed from $HighFpsPackage for this run."
    }
    foreach ($log in $logNames) {
        $path = Join-Path $GameBin64 $log
        if (Test-Path $path) { Remove-Item -Force $path }
        Add-Installed $path
    }
    # Streamline's own logs (frame generation), written to the temp folder: this run's only.
    $streamlineLogs = Join-Path $env:TEMP "$pluginName\streamline"
    if (Test-Path $streamlineLogs) { Remove-Item -Recurse -Force $streamlineLogs }

    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -Name SteamExe).SteamExe
    Write-Step "Starting the game through Steam ($steam)."
    Start-Process -FilePath $steam -ArgumentList @('-applaunch', '244850') | Out-Null
    $deadline = (Get-Date).AddSeconds(90)
    while (-not $gameProcess -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $gameProcess = Get-Process -Name SpaceEngineers -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $gameProcess) { throw 'SpaceEngineers.exe did not start.' }
    $state.GamePid = $gameProcess.Id
    $state.GameStart = $gameProcess.StartTime.ToString('o')
    Save-State
    Write-Step "Game running (pid $($gameProcess.Id))."

    if ($PresentMon) {
        # Read-only use of the shared tools folder (research\tools is a link to it).
        $presentMonExe = Join-Path $root 'research\tools\PresentMon-2.5.1-x64.exe'
        $presentMonProcess = Start-Process -FilePath $presentMonExe -PassThru -WindowStyle Hidden -ArgumentList @(
            '--process_id', $gameProcess.Id, '--output_file', (Join-Path $runDir 'presentmon.csv'),
            '--no_console_stats', '--stop_existing_session', '--session_name', "$pluginName-$stamp")
    }

    # The plugin writes its log as soon as the loader maps it; without one
    # after two minutes the loader did not load it and the run cannot succeed.
    $pluginLogInGame = Join-Path $GameBin64 "$pluginName.log"
    $deadline = $started.AddSeconds($TimeoutSeconds)
    $exited = $false
    while (-not $exited -and (Get-Date) -lt $deadline) {
        $exited = $gameProcess.WaitForExit(1000)
        if (-not $exited -and -not $script:loaderMissed -and ((Get-Date) - $started).TotalSeconds -gt 120 -and
            -not (Test-Path $pluginLogInGame)) {
            $script:loaderMissed = $true
            break
        }
    }
    if ($script:loaderMissed) {
        Write-Step 'No plugin log 120 s after the start: the loader did not load the plugin; ending the game.'
        Stop-Process -Id $gameProcess.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    } elseif (-not $exited) {
        Write-Step "Timeout after $TimeoutSeconds s; ending the game."
        Stop-Process -Id $gameProcess.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    } else {
        Write-Step ("Game exited after {0:F0} s." -f ((Get-Date) - $started).TotalSeconds)
    }
}
finally {
    if ($presentMonProcess -and -not $presentMonProcess.HasExited) {
        Stop-Process -Id $presentMonProcess.Id -ErrorAction SilentlyContinue
    }
    Get-Process -Name SpaceEngineers -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    foreach ($log in @("$pluginName.log", "$pluginName.previous.log", "$highFpsName.log")) {
        $path = Join-Path $GameBin64 $log
        if (Test-Path $path) { Copy-Item $path (Join-Path $runDir $log) }
    }
    $streamlineLogs = Join-Path $env:TEMP "$pluginName\streamline"
    if (Test-Path $streamlineLogs) {
        $streamlineTarget = Join-Path $runDir 'streamline'
        New-Item -ItemType Directory -Force $streamlineTarget | Out-Null
        Get-ChildItem $streamlineLogs -File | ForEach-Object { Copy-Item $_.FullName $streamlineTarget }
    }
    Get-ChildItem $appData -Filter 'SpaceEngineers_*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $started } | Sort-Object LastWriteTime | Select-Object -Last 1 |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $runDir 'game.log') }
    Restore-Session $stateFile
    Write-Step "Uninstalled; outputs in $runDir"
}

$pluginLog = Join-Path $runDir "$pluginName.log"
$highFpsLog = Join-Path $runDir "$highFpsName.log"
if ($WithHighFps -and (Test-Path $highFpsLog)) {
    Write-Output '--- HighFPSFix log ---'
    Get-Content $highFpsLog | Select-Object -Last 12
}
if (Test-Path $pluginLog) {
    Write-Output '--- plugin log ---'
    Get-Content $pluginLog | Select-Object -Last 80
} else {
    Write-Output 'No plugin log was written (the loader did not load the plugin?).'
    exit 2
}
$captures = Join-Path $runDir 'captures'
if (Test-Path $captures) {
    Write-Output '--- captures ---'
    Get-ChildItem $captures | ForEach-Object { Write-Output ("{0}  {1:N0} bytes" -f $_.Name, $_.Length) }
}
