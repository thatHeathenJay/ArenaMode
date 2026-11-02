<#
Rips EFT: Arena's UI assets (sprites, atlases, fonts, menu prefabs as YAML) with AssetRipper 2.0.0,
driven headlessly over its local HTTP API. Run this yourself; the output is the source for the
layout transpiler (tools/arena-ui-export.py in the ArenaMode fork).

Usage (PowerShell 7 or 5.1):
  .\Rip-ArenaUi.ps1                       # Arena at the default BSG path, output Tools\arena\rip
  .\Rip-ArenaUi.ps1 -Arena "D:\Games\EFT Arena" -Out "D:\rip" -Filter 'ui|menu|font|icon'
#>
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

# Settings: PNG sprites + YAML prefabs, scripts at structure level only (IL2CPP, fields needed for TMP/layout).
$settings = @{
    Import = @{ ScriptContentLevel = 'Level2'; StreamingAssetsMode = 'Ignore' }
    Processing = @{ EnablePrefabOutlining = $false; EnableStaticMeshSeparation = $false; BundledAssetsExportMode = 'DirectExport' }
    Export = @{ ImageExportFormat = 'Png'; SpriteExportMode = 'Yaml'; ScriptExportMode = 'Hybrid'; ShaderExportMode = 'Dummy'; TextExportMode = 'Parse'; SaveSettingsToDisk = $true }
}
$settings | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path (Split-Path $ripper) 'AssetRipper.Settings.json')

# Files to load: the serialized files that hold the menu UI. Their .resS/.resource companions are picked up automatically.
$files = @('globalgamemanagers', 'globalgamemanagers.assets', 'resources.assets', 'sharedassets0.assets', 'level0', 'level1', 'sharedassets1.assets') |
    ForEach-Object { Join-Path $data $_ } | Where-Object { Test-Path $_ }
Write-Host "Loading $($files.Count) files from $data"

$proc = Start-Process -FilePath $ripper -ArgumentList @('--headless', '--port', $Port, '--log', 'true') -PassThru -WindowStyle Minimized
try {
    $base = "http://127.0.0.1:$Port"
    $deadline = (Get-Date).AddSeconds(60)
    do { Start-Sleep -Milliseconds 500; try { $null = Invoke-WebRequest "$base/" -UseBasicParsing -TimeoutSec 2; $up = $true } catch { $up = $false } } until ($up -or (Get-Date) -gt $deadline)
    if (-not $up) { throw 'AssetRipper did not start' }

    # LoadFile accepts repeated Path form values. The call blocks until loading and processing finish.
    $body = ($files | ForEach-Object { 'Path=' + [uri]::EscapeDataString($_) }) -join '&'
    Invoke-WebRequest "$base/LoadFile" -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded' -UseBasicParsing -TimeoutSec 7200 -MaximumRedirection 0 -ErrorAction SilentlyContinue | Out-Null
    Write-Host 'Loaded. Exporting Unity project (prefab YAML + PNG sprites)...'
    New-Item -ItemType Directory -Force $Out | Out-Null
    $body = 'Path=' + [uri]::EscapeDataString((Resolve-Path $Out).Path)
    Invoke-WebRequest "$base/Export/UnityProject" -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded' -UseBasicParsing -TimeoutSec 7200 -MaximumRedirection 0 -ErrorAction SilentlyContinue | Out-Null
    Write-Host "Done. Output: $Out  (ExportedProject\Assets\...)"
} finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
