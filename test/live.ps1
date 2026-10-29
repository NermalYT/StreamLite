# Live checks against the installed, running StreamLite (all.ps1 covers the offline suites).
# Plays quiet test noise into the headphones (360 check) and opens the virtual camera briefly.
# Usage: .\live.ps1
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot
foreach ($t in 'MicFlow', 'Walk360Live', 'CamReadTest', 'FormatTest', 'NameProbe') {
    if (-not (Test-Path "bin\$t.exe")) { powershell -NoProfile -File run.ps1 $t 2>&1 | Out-Null }
}
$fails = 0
function Verdict($name, $ok, $detail) { "{0,-14} {1}  {2}" -f $name, ($(if ($ok) { 'PASS' } else { 'FAIL' })), $detail; if (-not $ok) { $script:fails++ } }

$status = Get-Content "$env:APPDATA\StreamLite\status.txt" -Raw
Verdict 'App status' ($status -match 'mic: Running' -and $status -match 'direct to cable') (($status -split "`r`n" | Select-String 'mic output') -join '')

# Samples must keep coming at the full rate and the app must report no dropouts. (How much of the signal is
# non-zero depends on the room: noise removal and the gate turn a quiet room into digital silence.)
$mic = .\bin\MicFlow.exe 5 | Select-Object -First 1
$full = $mic -match ': (\d+) samples' -and [double]$Matches[1] -gt 48000 * 4.8
$status = Get-Content "$env:APPDATA\StreamLite\status.txt" -Raw
Verdict 'Mic flow' ($full -and $status -match 'mic output: [^\r\n]*dropouts 0') $mic

# Direct to headphones holds them exclusively: nothing else can record them, so the 360 check can't listen (4.1.4: it used to fail).
if ($status -match 'direct to headphones: exclusive') { "{0,-14} SKIP  headphones held exclusively (Direct to headphones) - turn it off to run this check" -f '360 render' }
else { $walk = .\bin\Walk360Live.exe 2>&1 | Select-Object -Last 1; Verdict '360 render' ($walk -match 'LIVE 360 OK') $walk }

$cam = .\bin\CamReadTest.exe StreamLite 1920 1080 30 5 2>&1 | Select-String 'frames' | Select-Object -First 1
Verdict 'Camera' ($cam -match '= (\d+\.\d) fps' -and [double]$Matches[1] -gt 25) $cam

$fmt = .\bin\FormatTest.exe | Select-Object -Last 1
Verdict 'Formats' ($fmt -match 'ALL PASS') $fmt

$names = .\bin\NameProbe.exe | Select-String 'st1' | Out-String
Verdict 'Device names' ($names -match 'adapter2=StreamLite Mic' -and $names -match 'adapter2=StreamLite Game Audio') ''

if ($fails -eq 0) { 'ALL LIVE CHECKS PASSED' } else { "$fails LIVE CHECKS FAILED" }
