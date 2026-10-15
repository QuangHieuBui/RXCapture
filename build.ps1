# Builds RXCapture.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# No SDK / Visual Studio required.   Usage:  powershell -ExecutionPolicy Bypass -File build.ps1 [-OutDir <folder>]   (default: bin)
param([string]$OutDir, [string]$Version)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
# the version shown in the app comes from version.txt (build-setup.ps1 -Version passes its own); it is compiled into the exe
if (-not $Version) { $Version = (Get-Content "$root\version.txt" -ErrorAction SilentlyContinue | Select-Object -First 1) }
if (-not $Version -or $Version -notmatch '^\d+\.\d+\.\d+$') { $Version = '0.0.0' }
$verInfo = Join-Path ([IO.Path]::GetTempPath()) ("RXCaptureVersion-" + [Guid]::NewGuid().ToString('N') + ".cs")
@"
using System.Reflection;
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content $verInfo -Encoding UTF8
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
$args += $verInfo

& $csc @args
$cscExit = $LASTEXITCODE
Remove-Item $verInfo -Force -ErrorAction SilentlyContinue
if ($cscExit -ne 0) { throw "Build failed" }
Copy-Item "$root\res\RXCapture.exe.config" $out -Force
Write-Host "OK -> $out\RXCapture.exe"
