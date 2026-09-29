# Builds RXCapture.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# No SDK / Visual Studio required.   Usage:  powershell -ExecutionPolicy Bypass -File build.ps1 [-OutDir <folder>]   (default: bin)
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = if ($OutDir) { $OutDir } else { Join-Path $root 'bin' }
New-Item -ItemType Directory -Force $out | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

if (-not (Test-Path "$root\res\app.ico")) { & powershell -NoProfile -ExecutionPolicy Bypass -File "$root\tools\make-icon.ps1" }

$src = Get-ChildItem "$root\src" -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
        'System.Xml.dll', 'System.Xml.Linq.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll',
        'Microsoft.VisualBasic.dll'   # FileSystem.DeleteFile can send files to the Recycle Bin

$args = @('/nologo', '/target:winexe', '/optimize+', '/debug:pdbonly', '/codepage:65001', '/unsafe+', '/warn:3',
          "/out:$out\RXCapture.exe", "/win32manifest:$root\res\app.manifest", "/win32icon:$root\res\app.ico")
$args += $refs | ForEach-Object { "/r:$_" }
$args += $src

& $csc @args
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Copy-Item "$root\res\RXCapture.exe.config" $out -Force
Write-Host "OK -> $out\RXCapture.exe"
