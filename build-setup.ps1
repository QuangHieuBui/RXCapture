# Builds the installer: dist\RXCapture-Setup-<version>.exe, and keeps a copy of every version in releases\<version>\ (committed to git)
#   powershell -ExecutionPolicy Bypass -File build-setup.ps1 [-Version 1.0.2] [-OutDir <folder>] [-NoRelease] [-Force]
# The application is compiled first (build.ps1), zipped, and embedded in a small self-contained installer
# (setup\Setup.cs). Needs nothing but the C# compiler that ships with Windows.
#   Both RXCapture.exe and the installer are code-signed (tools\sign.ps1: self-signed certificate, or -Thumbprint <sha1> of a
#   certificate you bought). -NoSign skips it.
#   releases\<version>\ holds the installer, a portable zip, the SHA-256, the public certificate and BUILD.txt. An existing version folder is
#   never overwritten (bump -Version) unless -Force. -OutDir builds (tests) and -NoRelease do not touch releases\.
param([string]$Version = '1.0.2', [string]$OutDir, [string]$Thumbprint, [switch]$NoSign, [switch]$NoRelease, [switch]$Force)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = if ($OutDir) { $OutDir } else { Join-Path $root 'dist' }
if (-not $dist) { throw 'no output folder' }
$app = Join-Path $dist 'app'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null

& powershell -NoProfile -ExecutionPolicy Bypass -File "$root\build.ps1" -OutDir $app
if ($LASTEXITCODE -ne 0) { throw 'Building the application failed' }
Remove-Item "$app\*.pdb" -Force -ErrorAction SilentlyContinue

function Sign-Files([string[]]$files) {
    if ($NoSign) { return }
    $signArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$root\tools\sign.ps1")
    if ($Thumbprint) { $signArgs += @('-Thumbprint', $Thumbprint) }
    & powershell @signArgs @files
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Signing failed - the file is left unsigned' }
}
Sign-Files "$app\RXCapture.exe"          # signed before it is zipped into the installer

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $dist 'payload.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$info = Join-Path $dist 'SetupAssemblyInfo.cs'
@"
using System.Reflection;
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyProduct("RXCapture")]
"@ | Set-Content $info -Encoding UTF8

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $dist "RXCapture-Setup-$Version.exe"
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll'
$cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', "/out:$out",
             "/win32manifest:$root\setup\setup.manifest", "/win32icon:$root\res\app.ico", "/resource:$zip,payload.zip")
$cscArgs += $refs | ForEach-Object { "/r:$_" }
$cscArgs += "$root\setup\Setup.cs", $info
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed' }
Sign-Files $out
Write-Host ("OK -> {0}  ({1:N1} MB)" -f $out, ((Get-Item $out).Length / 1MB))

# ---- keep every version: releases\<version>\ is committed to git, so old builds stay available ----------------------------
if (-not $OutDir -and -not $NoRelease) {
    $rel = Join-Path $root "releases\$Version"
    if ((Test-Path $rel) -and -not $Force) {
        Write-Warning "releases\$Version already exists and is kept as it is (a published version is never replaced). Use a new -Version, or -Force to overwrite it."
    }
    else {
        New-Item -ItemType Directory -Force $rel | Out-Null
        Copy-Item $out $rel -Force
        Copy-Item $zip (Join-Path $rel "RXCapture-$Version-portable.zip") -Force            # RXCapture.exe + .config, no installer needed
        if (-not $NoSign) { & powershell -NoProfile -ExecutionPolicy Bypass -File "$root\tools\sign.ps1" -CreateOnly -ExportCer (Join-Path $rel 'RXCapture-CodeSigning.cer') | Out-Null }
        $hash = (Get-FileHash $out -Algorithm SHA256).Hash
        Set-Content (Join-Path $rel "RXCapture-Setup-$Version.exe.sha256") ("{0} *RXCapture-Setup-{1}.exe" -f $hash, $Version) -Encoding ASCII
        $commit = (& git -C $root rev-parse --short HEAD 2>$null)
        $dirty = if (& git -C $root status --porcelain 2>$null) { ' (uncommitted changes)' } else { '' }
        Set-Content (Join-Path $rel 'BUILD.txt') @(
            "RXCapture $Version",
            "Built:   " + (Get-Date -Format 'yyyy-MM-dd HH:mm'),
            "Source:  git $commit$dirty",
            "SHA-256: $hash  (RXCapture-Setup-$Version.exe)"
        ) -Encoding UTF8
        Write-Host "Saved to $rel  - commit this folder to keep the version in git"
    }
}
