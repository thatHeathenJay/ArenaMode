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

# AssetRipper only recovers script layouts (uGUI Image/Text, TextMeshPro, layout groups, Arena screens) when it
# sees a whole IL2CPP game: the exe, GameAssembly.dll and il2cpp_data next to the data folder. Loading single files
# gives "Unknown scripting backend" and every MonoBehaviour loses its fields. So build a stub game folder of
# hardlinks (no copy, same volume) holding only the UI data files, and load that folder.
$stub = Join-Path (Split-Path $Out) 'arena-stub'
if (Test-Path $stub) { Remove-Item -Recurse -Force $stub }
New-Item -ItemType Directory -Force "$stub\EscapeFromTarkovArena_Data\il2cpp_data\Metadata" | Out-Null
$stubFiles = @('EscapeFromTarkovArena.exe', 'GameAssembly.dll', 'UnityPlayer.dll', 'baselib.dll') +
    (@('app.info', 'boot.config', 'ScriptingAssemblies.json', 'RuntimeInitializeOnLoads.json', 'globalgamemanagers', 'globalgamemanagers.assets',
       'resources.assets', 'resources.assets.resS', 'resources.resource', 'sharedassets0.assets', 'level0', 'level1',
       'sharedassets1.assets', 'sharedassets1.assets.resS', 'sharedassets1.resource', 'il2cpp_data\Metadata\global-metadata.dat') |
       ForEach-Object { "EscapeFromTarkovArena_Data\$_" })
foreach ($f in $stubFiles) {
    $src = Join-Path $Arena $f
    if (Test-Path $src) { New-Item -ItemType HardLink -Path (Join-Path $stub $f) -Target $src | Out-Null }
}
Write-Host "Stub game folder: $stub ($($stubFiles.Count) hardlinks)"

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

    # LoadFolder blocks until loading (incl. Cpp2IL on GameAssembly.dll, a few minutes) and processing finish, then redirects home.
    $body = 'Path=' + [uri]::EscapeDataString($stub)
    Invoke-WebRequest "$base/LoadFolder" -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded' -UseBasicParsing -TimeoutSec 7200 -SkipHttpErrorCheck | Out-Null
    Write-Host 'Loaded. Exporting Unity project (prefab YAML + PNG sprites)...'
    # Export into an empty subfolder: AssetRipper refuses or wipes a non-empty export directory, and the logs live in $Out.
    $project = Join-Path $Out 'project'
    if (Test-Path $project) { Remove-Item -Recurse -Force $project }
    $body = 'Path=' + [uri]::EscapeDataString($project)
    Invoke-WebRequest "$base/Export/UnityProject" -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded' -UseBasicParsing -TimeoutSec 7200 -SkipHttpErrorCheck | Out-Null
    Write-Host "Done. Output: $project  (ExportedProject\Assets\...)"
} finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
