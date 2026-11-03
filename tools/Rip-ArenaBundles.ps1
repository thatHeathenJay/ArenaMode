<#
Rips the EFT: Arena asset BUNDLES the first rip (Rip-ArenaUi.ps1, resources + scenes) left out: Arena's own
sound effects, the stadium announcer / speaker voice banks, crowd ambience and the intro sound library.
Same AssetRipper 2.0.0 headless flow; bundles are hardlinked into a staging folder so only the listed ones load.

Usage (PowerShell 7):
  .\Rip-ArenaBundles.ps1                                   # default paths, output Tools\arena-rip-bundles
  .\Rip-ArenaBundles.ps1 -Extra 'assets/content/audio/phrases/arena/arenaspeakertagbankmanagertagillaeng.bundle'
#>
#Requires -Version 7
param(
    [string]$Arena = 'C:\Battlestate Games\Escape from Tarkov Arena',
    [string]$Out = 'C:\Games\SPT-4.1\Tools\arena-rip-bundles',
    [string[]]$Extra = @(),
    [int]$Port = 5578
)
$ErrorActionPreference = 'Stop'
$ripper = 'C:\Games\SPT-4.1\Tools\AssetRipper\AssetRipper.GUI.Free.exe'
$assets = Join-Path $Arena 'EscapeFromTarkovArena_Data\StreamingAssets\Windows'
if (-not (Test-Path $ripper)) { throw "AssetRipper not found at $ripper" }
if (-not (Test-Path "$assets\assets")) { throw "Arena bundles not found under $assets" }

# Relative to StreamingAssets\Windows. Sizes from the 2026-11-03 install.
$bundles = @(
    'assets/arena/resources/arenaaudio/arenasoundeffectswrapper.bundle',             # 7.7 MB  Arena UI/match sound effects
    'assets/content/audio/arena/soundeffects/arenasoundeffectstagbankmanager.bundle', #         effect tag bank
    'assets/content/audio/arena/blastgang/blastgangsoundsso.bundle',                  #         Blast Gang sounds
    'assets/content/audio/phrases/arena/arenaspeakertagbankmanagereng.bundle',        # 570 MB  stadium announcer, English
    'assets/content/audio/phrases/arena_effects.bundle',                              # 11 MB
    'assets/content/audio/phrases/speaker_voice.bundle',                              # PA speaker
    'assets/content/audio/phrases/speaker_voice2.bundle',
    'assets/content/audio/phrases/fan_speaker_voice.bundle',                          # 28 MB   crowd
    'assets/content/audio/startmission/introanim/introanimsoundlibrary.bundle'        # deploy intro sounds
) + $Extra

$settings = @{
    Import = @{ StreamingAssetsMode = 0 }
    Export = @{ SaveSettingsToDisk = $true }
}
$settings | ConvertTo-Json -Depth 4 | Set-Content -Encoding ascii (Join-Path (Split-Path $ripper) 'AssetRipper.Settings.json')

$stage = Join-Path $Out 'stage'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
$linked = 0
foreach ($rel in $bundles) {
    $src = Join-Path $assets ($rel -replace '/', '\')
    if (-not (Test-Path $src)) { Write-Warning "missing: $rel"; continue }
    New-Item -ItemType HardLink -Path (Join-Path $stage ([IO.Path]::GetFileName($src))) -Target $src | Out-Null
    $linked++
}
Write-Host "Staged $linked bundle(s) in $stage"

$stdout = Join-Path $Out 'assetripper-stdout.txt'; $stderr = Join-Path $Out 'assetripper-stderr.txt'
$proc = Start-Process -FilePath $ripper -ArgumentList @('--headless', "--port=$Port") -PassThru -WindowStyle Minimized -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try {
    $base = "http://127.0.0.1:$Port"
    $deadline = (Get-Date).AddSeconds(90)
    do { Start-Sleep -Milliseconds 500; try { $null = Invoke-WebRequest "$base/" -UseBasicParsing -TimeoutSec 2; $up = $true } catch { $up = $false } } until ($up -or $proc.HasExited -or (Get-Date) -gt $deadline)
    if (-not $up) {
        Get-Content $stdout, $stderr -ErrorAction SilentlyContinue | Select-Object -Last 40
        throw "AssetRipper did not start (exit code $($proc.ExitCode)); see $stdout"
    }
    # A folder of bare bundles is not a game structure (the folder loader crashes on it); each bundle is loaded as a
    # standalone file and exported into its own subfolder.
    $project = Join-Path $Out 'project'
    if (Test-Path $project) { Remove-Item -Recurse -Force $project }
    foreach ($file in Get-ChildItem $stage -File) {
        Write-Host "Loading $($file.Name) ($([math]::Round($file.Length / 1MB)) MB)..."
        $body = 'Path=' + [uri]::EscapeDataString($file.FullName)
        Invoke-WebRequest "$base/LoadFile" -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded' -UseBasicParsing -TimeoutSec 7200 -SkipHttpErrorCheck -MaximumRedirection 5 | Out-Null
        $target = Join-Path $project ([IO.Path]::GetFileNameWithoutExtension($file.Name))
        New-Item -ItemType Directory -Force $target | Out-Null
        $body = 'Path=' + [uri]::EscapeDataString($target)
        Invoke-WebRequest "$base/Export/UnityProject" -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded' -UseBasicParsing -TimeoutSec 7200 -SkipHttpErrorCheck -MaximumRedirection 5 | Out-Null
        $clips = @(Get-ChildItem (Join-Path $target 'ExportedProject\Assets\AudioClip') -File -ErrorAction SilentlyContinue | Where-Object Extension -ne '.meta')
        Write-Host "  exported: $($clips.Count) audio clip(s) -> $target"
        Invoke-WebRequest "$base/Reset" -Method Post -UseBasicParsing -TimeoutSec 600 -SkipHttpErrorCheck -MaximumRedirection 5 | Out-Null
    }
    Write-Host "Done. Output: $project"
} finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
