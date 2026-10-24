# Runs every automated (offline) test and prints a one-line verdict each. Nothing here touches devices or settings.
# Usage: .\all.ps1 [-Skip Name,Name]     (CI skips DfnChainTest and LowVramTest: no GPU there)
param([string[]]$Skip = @())
$ErrorActionPreference = 'Continue'
$here = $PSScriptRoot
$checks = [ordered]@{
    'ChainTest'   = { param($o) $o -match 'ALL PASSED' }                       # voice chain, styles, voice-test recorder
    'ProfileTest' = { param($o) $o -match 'ALL PASSED' }                       # game profiles + upgrades
    'NaturalTest' = { param($o) $o -match 'ALL PASSED' }                       # natural 3D for videos
    'SpatialTest' = { param($o) $o -match 'NaN False' }                        # 3D processor sanity
    'RingTest'    = { param($o) -not ($o | Select-String 'swing ([1-9]|0\.[1-9])') }   # treble steady through drift correction
    'LimiterTest' = { param($o) -not ($o | Select-String 'harmonics [1-9]') }  # 3D limiter adds under 1% harmonics
    'DspTest'     = { param($o) $o -match 'reconstruction error: [0-9.]+E-00[5-9]' }
    'Spatial360Test' = { param($o) $o -match 'ALL PASS' }                  # true 360° placement vs measured head responses
    'DfnChainTest' = { param($o) $o -match 'ALL PASS' }                    # DeepFilterNet CPU + GPU in the mic chain (needs the ai folder)
    'SphereTest'  = { param($o) $o -match 'ALL PASS' }                     # footsteps above / level / below + footstep decay
    'MicRingTest' = { param($o) $o -match 'ALL PASS' }                     # virtual mic buffer: 20 min of +-300 ppm clock drift, no dropouts
    'CubeViewTest' = { param($o) $o -match 'ALL PASS' }                    # 3D sound cube: right-drag turns it, floors and cells
    'UpdaterTest' = { param($o) $o -match 'ALL PASS' }                     # updates: versions, checksums, zip update by the helper (fake install)
    'HeadphoneMatchTest' = { param($o) $o -match 'ALL PASS' }              # device names -> the right AutoEq model (downloads the index)
    'InputChannelsTest' = { param($o) $o -match 'ALL PASS' }               # multi-channel mics: interfaces, stereo mics, out-of-phase channels
    'DfnFallbackTest' = { param($o) $o -match 'ALL PASS' }                 # a GPU that can't run DeepFilterNet -> DeepFilterNet on the CPU
    'LowVramTest' = { param($o) $o -match 'ALL PASS' }                     # Low VRAM: all AI < 250 MB of video memory, same picture, switching (needs a GPU)
}
$failed = 0
foreach ($name in $checks.Keys) {
    if ($Skip -contains $name) { '{0,-12} skipped' -f $name; continue }
    $out = try { & "$here\run.ps1" $name 2>&1 | Out-String } catch { $_.Exception.Message }
    $ok = & $checks[$name] $out
    if (-not $ok) { $failed++ }
    '{0,-12} {1}' -f $name, ($(if ($ok) { 'PASS' } else { 'FAIL' }))
    if (-not $ok) { $out.Trim() -split "`n" | Select-Object -Last 8 | ForEach-Object { '    ' + $_ } }
}
if ($failed) { "$failed FAILED" } else { 'ALL PASSED' }
