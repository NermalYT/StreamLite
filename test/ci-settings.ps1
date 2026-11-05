# The release build's update tests only (on a throwaway runner, never on a real PC: -Plant replaces settings.json).
# -Plant: settings of someone who finished the setup and tuned things. -Check: they came through an update.
param([switch]$Plant, [switch]$Check)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Only for the release build: this replaces settings.json.' }
$dir = "$env:APPDATA\StreamLite"
$file = "$dir\settings.json"

if ($Plant) {
    Get-Process StreamLite -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    New-Item -ItemType Directory -Force $dir | Out-Null
    Remove-Item "$dir\settings.json.lastgood", "$dir\update-tried.txt", "$dir\update.log", "$dir\health.log", "$dir\settings.kept*" -ErrorAction SilentlyContinue
    # Told apart from every default: the mic's noise removal strength, and the setup marked done so it doesn't open.
    '{"Strength":33,"NsEngineVersion":3,"StyleFixes":1,"SetupDoneFor":"4.0.5","SetupKind":null,"WelcomeShown":true,"VoiceSetupDone":true,"StartMinimized":true,"Defaults41":"4.1.0","RunAsAdmin":false,"LastVersion":"4.0.5","UpdateOnStartup":true}' |
        Set-Content $file -Encoding ASCII
    # This install's mark counts as seen, as on a PC that has already started it.
    $mark = (Get-ItemProperty 'HKLM:\Software\StreamLite' -ErrorAction SilentlyContinue).FreshInstall
    if ($mark) { $mark | Set-Content "$dir\install-seen.txt" -Encoding ASCII }
    "Planted settings (install mark: [$mark])"
}

if ($Check) {
    if (-not (Test-Path $file)) { throw 'settings.json is gone' }
    $s = Get-Content $file -Raw | ConvertFrom-Json
    $bad = @()
    if ($s.Strength -ne 33) { $bad += "Strength is $($s.Strength)" }
    if ($s.SetupDoneFor -ne '4.0.5') { $bad += "the setup is due again: SetupDoneFor $($s.SetupDoneFor)" }
    if (-not $s.VoiceSetupDone) { $bad += 'the voice setup is due again' }
    if (Select-String -Path "$dir\health.log" -Pattern 'New install' -Quiet -ErrorAction SilentlyContinue) { $bad += 'it started as a new install' }
    if ((Get-ItemProperty 'HKLM:\Software\StreamLite' -ErrorAction SilentlyContinue).FreshInstall) { $bad += 'the update left a new-install mark' }
    if ($bad.Count) { throw ($bad -join '; ') }
    "Settings came through: Strength 33, setup done, not treated as a new install, version $($s.LastVersion)"
}
