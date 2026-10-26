# Builds one test (test\<Name>.cs) together with the app's sources and runs it.
# Usage: .\run.ps1 <Name> [args...]      e.g. .\run.ps1 DspTest
param([Parameter(Mandatory = $true)][string]$Name, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$file = Join-Path $PSScriptRoot "$Name.cs"
$text = Get-Content $file -Raw
# The test's entry point: the last class declared before "static void Main".
$mainAt = $text.IndexOf('static void Main')
$top = [regex]::Matches($text.Substring(0, $mainAt), '(?m)^class\s+(\w+)')   # top-level classes only (not nested helpers)
if ($top.Count -eq 0) { $top = [regex]::Matches($text.Substring(0, $mainAt), '\bclass\s+(\w+)') }
$cls = ($top | Select-Object -Last 1).Groups[1].Value
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$resources = Get-ChildItem "$root\res" -Include *.bin,*.txt -Recurse | ForEach-Object { "/resource:$($_.FullName),$($_.Name)" }
$sources = @(Get-ChildItem "$root\src" -Recurse -Filter *.cs | ForEach-Object { $_.FullName }) + $file
# Helpers from other tests: "// include: Other.cs" lines at the top of the test.
foreach ($m in [regex]::Matches($text, '(?m)^// include: (\S+\.cs)')) { $sources += Join-Path $PSScriptRoot $m.Groups[1].Value }
$refs = 'System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Management.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll',"$fw\WPF\System.Speech.dll" |
    ForEach-Object { "/r:$_" }
$exe = Join-Path $out "$Name.exe"
$log = & "$fw\csc.exe" /nologo /target:exe /platform:x64 /optimize+ /langversion:5 /nowarn:0649,0169,0414 "/main:$cls" "/out:$exe" @refs @resources @sources
if ($LASTEXITCODE -ne 0) { $log | Select-String 'error' | ForEach-Object { Write-Host $_.Line }; throw "Build of $Name failed" }
& $exe @Rest
