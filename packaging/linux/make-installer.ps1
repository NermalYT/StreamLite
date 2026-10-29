# Builds the Linux installer: dist\StreamLite-<version>-linux.sh - one file for every distro (a script with the program
# packed behind it). Builds the Linux program first (build-mono.ps1).
# Usage: packaging\linux\make-installer.ps1 [-Version 4.1.3] [-OnnxRuntime path\to\libonnxruntime.so]
param([string]$Version, [string]$OnnxRuntime)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $Version) { $Version = ([regex]::Match((Get-Content "$root\src\Program.cs" -Raw), 'AssemblyFileVersion\("(\d+\.\d+\.\d+)')).Groups[1].Value }
& "$PSScriptRoot\build-mono.ps1" | Out-Null

# The ONNX Runtime for Linux (from the Microsoft.ML.OnnxRuntime package, CPU): given, from an earlier Linux build, or downloaded.
if (-not $OnnxRuntime) { $OnnxRuntime = "$root\linux\publish\libonnxruntime.so" }
if (-not (Test-Path $OnnxRuntime)) {
    $pkg = Join-Path $env:TEMP "ort-linux.zip"   # a .nupkg is a zip; Windows PowerShell only unpacks it named .zip
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.ML.OnnxRuntime/1.24.4" -OutFile $pkg -UseBasicParsing
    $ex = Join-Path $env:TEMP "ort-linux"; Remove-Item $ex -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive $pkg $ex
    $OnnxRuntime = Join-Path $ex "runtimes\linux-x64\native\libonnxruntime.so"
}

# Vosk (offline speech recognizer, Apache-2.0) and its small English model for Tune to my voice: downloaded once,
# checked against known hashes, kept in linux\publish\vosk.
$vosk = "$root\linux\publish\vosk"
$voskParts = @(
    @{ Url = "https://github.com/alphacep/vosk-api/releases/download/v0.3.45/vosk-linux-x86_64-0.3.45.zip"; Hash = "BBDC8ED85C43979F6443142889770EA95CBFBC56CFFB5C5DCD73AFA875C5FBB2"; Dir = "lib" },
    @{ Url = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip"; Hash = "30F26242C4EB449F948E42CB302DD7A686CB29A3423A8367F99FF41780942498"; Dir = "model" })
foreach ($part in $voskParts) {
    if (Test-Path "$vosk\$($part.Dir)") { continue }
    $zip = Join-Path $env:TEMP ("vosk-" + $part.Dir + ".zip")
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest $part.Url -OutFile $zip -UseBasicParsing
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $part.Hash) { throw "Vosk download changed: $($part.Url)" }
    $ex = Join-Path $env:TEMP ("vosk-" + $part.Dir); Remove-Item $ex -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive $zip $ex
    New-Item -ItemType Directory -Force $vosk | Out-Null
    Move-Item (Get-ChildItem $ex -Directory | Select-Object -First 1).FullName "$vosk\$($part.Dir)"
}

$stage = Join-Path $env:TEMP "streamlite-linux-stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
$app = New-Item -ItemType Directory -Force (Join-Path $stage "streamlite")
New-Item -ItemType Directory -Force "$app\ai", "$app\hrtf" | Out-Null
Copy-Item "$root\linux\mono-bin\StreamLite.exe" $app
Copy-Item "$root\linux\streamlite.png" $app
Copy-Item "$root\LICENSE*", "$root\THIRD-PARTY-NOTICES.md", "$root\CREDITS.md" $app -ErrorAction SilentlyContinue
# The AI models (Windows' DirectML and its runtime aren't needed: Linux runs the CPU ONNX Runtime).
Get-ChildItem "$root\ai" -File | Where-Object { $_.Extension -in '.onnx', '.txt' -and $_.Name -notmatch 'DIRECTML' } | Copy-Item -Destination "$app\ai"
Copy-Item $OnnxRuntime "$app\ai\libonnxruntime.so"
Copy-Item "$root\hrtf\*.blh" "$app\hrtf"
New-Item -ItemType Directory -Force "$app\speech" | Out-Null
Copy-Item "$vosk\lib\libvosk.so" "$app\speech"
Copy-Item "$vosk\model" "$app\speech\model" -Recurse

New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$payload = Join-Path $env:TEMP "streamlite-payload.tar.gz"
Push-Location $stage; try { tar -czf $payload streamlite } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "tar failed" }

# The script part (LF line endings) and the payload behind it.
$head = [IO.File]::ReadAllText("$PSScriptRoot\installer-head.sh").Replace("`r`n", "`n").Replace("@VERSION@", $Version)
if (-not $head.EndsWith("`n")) { $head += "`n" }
$out = Join-Path $root "dist\StreamLite-$Version-linux.sh"
$fs = [IO.File]::Create($out)
try {
    $b = [Text.Encoding]::UTF8.GetBytes($head); $fs.Write($b, 0, $b.Length)
    $p = [IO.File]::ReadAllBytes($payload); $fs.Write($p, 0, $p.Length)
} finally { $fs.Dispose() }
"{0} ({1:N0} MB)" -f $out, ((Get-Item $out).Length / 1MB)
