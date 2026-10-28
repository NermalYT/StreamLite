# Builds StreamLite.exe with the C# compiler that ships with Windows (.NET Framework 4.8). No SDK needed.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
if (-not (Test-Path "$root\app.ico")) { & "$root\make-icon.ps1" -Out "$root\app.ico" | Out-Null }

$resources = Get-ChildItem "$root\res" -Include *.bin,*.txt -Recurse | ForEach-Object { "/resource:$($_.FullName),$($_.Name)" }
$sources = Get-ChildItem "$root\src" -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
$refs = 'System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Management.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Xaml.dll','System.Security.dll',"$fw\WPF\PresentationCore.dll","$fw\WPF\PresentationFramework.dll","$fw\WPF\WindowsBase.dll","$fw\WPF\WindowsFormsIntegration.dll","$fw\WPF\System.Speech.dll" |
    ForEach-Object { "/r:$_" }
$cscArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/langversion:5',
    "/win32icon:$root\app.ico", "/win32manifest:$root\app.manifest",
    "/out:$out\StreamLite.exe") + $refs + $resources + $sources
& "$fw\csc.exe" @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Output "Built $out\StreamLite.exe"


