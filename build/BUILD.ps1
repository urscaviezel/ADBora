param([switch]$NoPause)
# Builds ADBora (self-contained single-file, Windows x64) in two variants:
#   dist\ADBora-Portable\           portable: settings/cache in "Data" next to the EXE
#   dist\ADBora-Portable-<ver>-Windows-x64.zip
#   dist\ADBora-Setup-<ver>.exe     installer (needs Inno Setup 6; data in %LOCALAPPDATA%\ADBora)
# Requirements (build PC only): .NET 8 SDK, optional Inno Setup 6.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\AdbTool\AdbTool.csproj"
$distRoot = Join-Path $root "dist"
$publish = Join-Path $distRoot "publish"
$portable = Join-Path $distRoot "ADBora-Portable"

[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Building ADBora $version ..." -ForegroundColor Cyan

# Old outputs of previous build layouts
foreach ($old in @("ADB_Tool", "ADB_Tool-Windows-x64.zip", "ADB_Tool-Portable", "publish", "ADBora-Portable")) {
    $p = Join-Path $distRoot $old
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}
Get-ChildItem $distRoot -Filter "*-Portable-*.zip" -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $distRoot -Filter "*-Setup-*.exe" -ErrorAction SilentlyContinue | Remove-Item -Force

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $root "README.md") $publish
Copy-Item (Join-Path $root "LICENSE") $publish

# Optional: bundle Android Platform Tools if they were placed in build\adb
$adbSource = Join-Path $PSScriptRoot "adb"
if (Test-Path (Join-Path $adbSource "adb.exe")) {
    Copy-Item $adbSource (Join-Path $publish "adb") -Recurse
    Write-Host "Platform Tools bundled from build\adb" -ForegroundColor Yellow
}

# --- Portable variant ------------------------------------------------------
Copy-Item $publish $portable -Recurse
Set-Content -Path (Join-Path $portable "portable.flag") -Encoding UTF8 -Value @(
    "ADBora portable mode / Portabler Modus",
    "Settings and caches are stored in the 'Data' folder next to ADBora.exe.",
    "Einstellungen und Cache liegen im Ordner 'Data' neben ADBora.exe.",
    "Delete this file to use %LOCALAPPDATA%\ADBora instead.",
    "Diese Datei loeschen, um stattdessen %LOCALAPPDATA%\ADBora zu verwenden.")
$zip = Join-Path $distRoot "ADBora-Portable-$version-Windows-x64.zip"
Compress-Archive -Path "$portable\*" -DestinationPath $zip
Write-Host "Portable: $zip" -ForegroundColor Green

# --- Installer (Inno Setup 6) ----------------------------------------------
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source }
if (-not $iscc) {
    # Look up the install location of Inno Setup 5/6 in the registry
    $keys = @("HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
              "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
              "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*")
    foreach ($k in $keys) {
        Get-ItemProperty $k -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like "Inno Setup*" -and $_.InstallLocation } |
            ForEach-Object {
                $candidate = Join-Path $_.InstallLocation "ISCC.exe"
                if (-not $iscc -and (Test-Path $candidate)) { $iscc = $candidate }
            }
    }
}

if ($iscc) {
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$publish" "/DOutputDir=$distRoot" (Join-Path $PSScriptRoot "installer.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
    Write-Host "Installer: $distRoot\ADBora-Setup-$version.exe" -ForegroundColor Green
} else {
    Write-Host "Inno Setup 6 not found - installer skipped (https://jrsoftware.org/isdl.php)." -ForegroundColor Yellow
}

Remove-Item $publish -Recurse -Force
Write-Host ""
Write-Host "Done." -ForegroundColor Green
