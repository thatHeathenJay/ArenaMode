<#
Rips EFT: Arena's UI assets (sprites, atlases, fonts, menu prefabs as YAML) with AssetRipper 2.0.0,
driven headlessly over its local HTTP API. Run this yourself; the output is the source for the
layout transpiler (tools/arena-ui-export.py in the ArenaMode fork).

Usage (PowerShell 7 or 5.1):
  .\Rip-ArenaUi.ps1                       # Arena at the default BSG path, output Tools\arena\rip
  .\Rip-ArenaUi.ps1 -Arena "D:\Games\EFT Arena" -Out "D:\rip" -Filter 'ui|menu|font|icon'
#>
#Requires -Version 7
param(
    [string]$Arena = 'C:\Battlestate Games\Escape from Tarkov Arena',
    [string]$Out = 'C:\Games\SPT-4.1\Tools\arena-rip',
    [int]$Port = 5577
)
$ErrorActionPreference = 'Stop'
$ripper = 'C:\Games\SPT-4.1\Tools\AssetRipper\AssetRipper.GUI.Free.exe'
$data = Join-Path $Arena 'EscapeFromTarkovArena_Data'
if (-not (Test-Path $ripper)) { throw "AssetRipper not found at $ripper" }
if (-not (Test-Path "$data\resources.assets")) { throw "Arena data not found under $data (install not finished?)" }

# Settings: AssetRipper's JSON uses numeric enums. Defaults already give PNG images, YAML sprites and prefabs,
# hybrid scripts and dummy shaders; the one thing to force is StreamingAssetsMode = 0 (Ignore) so the 6,000
# map/item bundles next to the loaded files are not pulled in.
$settings = @{
    Import = @{ StreamingAssetsMode = 0 }
    Processing = @{ EnableStaticMeshSeparation = $false }
    Export = @{ SaveSettingsToDisk = $true }
}
$settings | ConvertTo-Json -Depth 4 | Set-Content -Encoding ascii (Join-Path (Split-Path $ripper) 'AssetRipper.Settings.json')

# Files to load: the serialized files that hold the menu UI. Their .resS/.resource companions are picked up automatically.
$files = @('globalgamemanagers', 'globalgamemanagers.assets', 'resources.assets', 'sharedassets0.assets', 'level0', 'level1', 'sharedassets1.assets') |
    ForEach-Object { Join-Path $data $_ } | Where-Object { Test-Path $_ }
Write-Host "Loading $($files.Count) files from $data"

New-Item -ItemType Directory -Force $Out | Out-Null
$stdout = Join-Path $Out 'assetripper-stdout.txt'; $stderr = Join-Path $Out 'assetripper-stderr.txt'
# Switch arguments take no value in AssetRipper's POSIX parser ("--log true" is rejected, and it exits without a log).
$proc = Start-Process -FilePath $ripper -ArgumentList @('--headless', "--port=$Port") -PassThru -WindowStyle Minimized -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try {
    $base = "http://127.0.0.1:$Port"
    $deadline = (Get-Date).AddSeconds(90)
    do { Start-Sleep -Milliseconds 500; try { $null = Invoke-WebRequest "$base/" -UseBasicParsing -TimeoutSec 2; $up = $true } catch { $up = $false } } until ($up -or $proc.HasExited -or (Get-Date) -gt $deadline)
    if (-not $up) {
        Write-Host '--- AssetRipper output ---'; Get-Content $stdout, $stderr -ErrorAction SilentlyContinue | Select-Object -Last 40
        throw "AssetRipper did not start (exit code $($proc.ExitCode)); see $stdout"
    }

    # LoadFile accepts repeated Path form values. The call blocks until loading and processing finish, then answers 302 (-SkipHttpErrorCheck).
    $body = ($files | ForEach-Object { 'Path=' + [uri]::EscapeDataString($_) }) -join '&'
    Invoke-WebRequest "$base/LoadFile" -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded' -UseBasicParsing -TimeoutSec 7200 -MaximumRedirection 0 -SkipHttpErrorCheck | Out-Null
    Write-Host 'Loaded. Exporting Unity project (prefab YAML + PNG sprites)...'
    # Export into an empty subfolder: AssetRipper refuses or wipes a non-empty export directory, and the logs live in $Out.
    $project = Join-Path $Out 'project'
    if (Test-Path $project) { Remove-Item -Recurse -Force $project }
    $body = 'Path=' + [uri]::EscapeDataString($project)
    Invoke-WebRequest "$base/Export/UnityProject" -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded' -UseBasicParsing -TimeoutSec 7200 -MaximumRedirection 0 -SkipHttpErrorCheck | Out-Null
    Write-Host "Done. Output: $project  (ExportedProject\Assets\...)"
} finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
