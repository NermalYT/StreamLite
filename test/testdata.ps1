# Tests never write to your own StreamLite settings (4.2.1). Dot-sourced by run.ps1 and live.ps1: unless STREAMLITE_DATA
# is set already, a test runs on its own copy of them in %LOCALAPPDATA%\StreamLiteTest\<name> (fresh every run), so
# tests that read your mic tuning or the running app's status still can, and whatever they save stays in the copy.
# Tests that make their own folder (SetupLive, UiShots...) still do.
function Use-TestData([string]$Name) {
    if ($env:STREAMLITE_DATA) { return }
    $data = Join-Path $env:LOCALAPPDATA "StreamLiteTest\$Name"
    if (Test-Path $data) { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force $data | Out-Null
    $real = Join-Path $env:APPDATA 'StreamLite'
    foreach ($f in 'settings.json', 'status.txt', 'health.log', 'voice-sample.wav', 'autoeq-index.md', 'squig-index.tsv') {
        if (Test-Path "$real\$f") { Copy-Item "$real\$f" $data -ErrorAction SilentlyContinue }
    }
    $env:STREAMLITE_DATA = $data
}
