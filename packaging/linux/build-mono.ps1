# Builds StreamLite for Linux on Windows: the same sources as the Windows app (src/) with the Windows-only system
# layer swapped for the Linux one (linux/mono/), compiled with LINUX defined. The result runs on Mono (mono StreamLite.exe)
# with the exact same windows and pages as on Windows.
# Usage: packaging\linux\build-mono.ps1 [-Out dir]     (default: linux\mono-bin)
param([string]$Out)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $Out) { $Out = Join-Path $root 'linux\mono-bin' }
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
# Windows-only files: their Linux versions (same classes, same members) are in linux/mono.
$windowsOnly = Get-Content (Join-Path $root 'linux\mono\windows-only.txt') | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object { (Join-Path $root $_.Trim()).ToLowerInvariant() }
$sources = @(Get-ChildItem "$root\src" -Recurse -Filter *.cs | Where-Object { $windowsOnly -notcontains $_.FullName.ToLowerInvariant() } | ForEach-Object { $_.FullName })
$sources += @(Get-ChildItem "$root\linux\mono" -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
$resources = Get-ChildItem "$root\res" -Include *.bin,*.txt -Recurse | ForEach-Object { "/resource:$($_.FullName),$($_.Name)" }
# Mono can't read the icon out of the program file: it travels as a resource too (Program.AppIcon).
if (-not (Test-Path "$root\app.ico")) { & "$root\make-icon.ps1" -Out "$root\app.ico" | Out-Null }
$resources += "/resource:$root\app.ico,app.ico", "/win32icon:$root\app.ico", "/resource:$root\linux\mono\desktop-helper.py,desktop-helper.py"
# Only what Mono has (no WPF, no System.Speech).
$refs = 'System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Management.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Xml.dll' | ForEach-Object { "/r:$_" }
New-Item -ItemType Directory -Force $Out | Out-Null
$exe = Join-Path $Out 'StreamLite.exe'
$log = & "$fw\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /unsafe /langversion:5 /define:LINUX /nowarn:0649,0169,0414,0618 "/out:$exe" @refs @resources @sources
$errors = $log | Select-String 'error'
if ($LASTEXITCODE -ne 0) { $errors | ForEach-Object { $_.Line }; throw "Linux build failed ($($errors.Count) errors)" }
"Built $exe"
