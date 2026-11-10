# Builds the companion app "ADBora Mic" (companion\mic) without Gradle:
# aapt2 (manifest) + javac + d8 + zipalign + apksigner.
# Output: src\AdbTool\Resources\ADBoraMic.apk (embedded in ADBora).
# Signing key: build\keys\companion.jks (not in the repository; created on first run).
param([string]$Sdk = "C:\AI-Projects\SDKs\android-sdk", [int]$VersionCode = 4, [string]$VersionName = "1.3")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "companion\mic"
$tools = Join-Path $Sdk "build-tools\34.0.0"
$androidJar = Join-Path $Sdk "platforms\android-34\android.jar"
$work = Join-Path $root "build\companion-work"
$outApk = Join-Path $root "src\AdbTool\Resources\ADBoraMic.apk"
$jdk = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME "bin" } else { Split-Path (Get-Command javac).Source }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work, (Join-Path $work "classes"), (Join-Path $work "dex"), (Split-Path $outApk) | Out-Null

function Run($exe, [string[]]$a) {
    & $exe @a
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed ($LASTEXITCODE)" }
}

# 1. Manifest → base APK
Run (Join-Path $tools "aapt2.exe") @("link", "-o", (Join-Path $work "base.apk"), "-I", $androidJar,
    "--manifest", (Join-Path $src "AndroidManifest.xml"), "--min-sdk-version", "29", "--target-sdk-version", "29",
    "--version-code", "$VersionCode", "--version-name", $VersionName)

# 2. Java → classes → dex
$java = Get-ChildItem (Join-Path $src "src") -Recurse -Filter *.java | ForEach-Object { $_.FullName }
Run (Join-Path $jdk "javac.exe") (@("-source", "8", "-target", "8", "-nowarn", "-encoding", "UTF-8", "-classpath", $androidJar, "-d", (Join-Path $work "classes")) + $java)
$classes = Get-ChildItem (Join-Path $work "classes") -Recurse -Filter *.class | ForEach-Object { $_.FullName }
Run (Join-Path $tools "d8.bat") (@("--release", "--min-api", "29", "--lib", $androidJar, "--output", (Join-Path $work "dex")) + $classes)

# 3. Add classes.dex, align, sign
Push-Location (Join-Path $work "dex")
Run (Join-Path $jdk "jar.exe") @("uf", (Join-Path $work "base.apk"), "classes.dex")
Pop-Location
Run (Join-Path $tools "zipalign.exe") @("-p", "-f", "4", (Join-Path $work "base.apk"), (Join-Path $work "aligned.apk"))

$keys = Join-Path $PSScriptRoot "keys"
$ks = Join-Path $keys "companion.jks"
$passFile = Join-Path $keys "companion.pass"
if (-not (Test-Path $ks)) {
    New-Item -ItemType Directory -Force $keys | Out-Null
    $pass = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ })
    Set-Content $passFile $pass -NoNewline
    Run (Join-Path $jdk "keytool.exe") @("-genkeypair", "-keystore", $ks, "-alias", "adbora", "-keyalg", "RSA", "-keysize", "2048",
        "-validity", "10000", "-storepass", $pass, "-keypass", $pass, "-dname", "CN=ADBora, O=ADBora")
    Write-Host "New signing key created: $ks (back it up; it is not part of the repository)" -ForegroundColor Yellow
}
$pass = Get-Content $passFile -Raw
Run (Join-Path $tools "apksigner.bat") @("sign", "--ks", $ks, "--ks-pass", "pass:$pass", "--ks-key-alias", "adbora", "--out", $outApk, (Join-Path $work "aligned.apk"))
Run (Join-Path $tools "apksigner.bat") @("verify", $outApk)
Remove-Item "$outApk.idsig" -ErrorAction SilentlyContinue # v4 signature file (only for incremental installs)
Remove-Item $work -Recurse -Force
Write-Host "ADBora Mic built: $outApk ($((Get-Item $outApk).Length) bytes)" -ForegroundColor Green
