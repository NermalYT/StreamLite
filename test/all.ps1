# Runs every automated (offline) test and prints a one-line verdict each. Nothing here touches devices or settings.
# Usage: .\all.ps1 [-Skip Name,Name]     (CI skips DfnChainTest, DfnBufferTest and LowVramTest: no GPU there)
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
    'DspTest'     = { param($o) $o -match 'reconstruction error: ([0-9.]+E-00[5-9]|0\.00E\+000)' }   # noise removal off: straight through
    'Spatial360Test' = { param($o) $o -match 'ALL PASS' }                  # true 360° placement vs measured head responses
    'DfnChainTest' = { param($o) $o -match 'ALL PASS' }                    # DeepFilterNet CPU + GPU in the mic chain (needs the ai folder)
    'DfnBufferTest' = { param($o) $o -match 'ALL PASS' }                   # DeepFilterNet's buffer: 0 frames on a fast device, 1 again at once when late (needs the ai folder)
    'SphereTest'  = { param($o) $o -match 'ALL PASS' }                     # footsteps above / level / below + footstep decay
    'RingsTest'   = { param($o) $o -match 'ALL PASS' }                     # Test footsteps: circles at ear level, above and below (true elevations)
    'SettingsRecoveryTest' = { param($o) $o -match 'ALL PASS' }           # a damaged settings file comes back from the last good copy
    'HotkeyTest'  = { param($o) $o -match 'ALL PASS' }                     # hotkeys: keys + modifiers, mouse buttons, push to talk, recording, actions
    'MicCalibrationTest' = { param($o) $o -match 'ALL PASS' }             # multi-mic calibration: bleed measured, volumes, doubling -21 dB, both talking kept
    'OutputSyncTest' = { param($o) $o -match 'ALL PASS' }                 # output sync: clicks timed by the mic, line up, delay line
    'VoiceCompareTest' = { param($o) $o -match 'ALL PASS' }               # voice test: compare engines at matched loudness, sample kept
    'Personal3DTest' = { param($o) $o -match 'ALL PASS' }                 # personal 3D listening test: fair levels, real cues, scoring, per headphones
    'Personal3DWorldTest' = { param($o) $o -match 'ALL PASS' }            # Personalize 3D building: looks, directions, scoring, decisions, head responses between measurements
    'FootstepTest' = { param($o) $o -match 'ALL PASSED' }                  # footsteps: bright, sharp, a knock not a boom
    'Personal3DFlowTest' = { param($o) $o -match 'ALL PASS' }             # Personalize 3D played through: every round, buttons, pause, practice
    'StudioVoiceTest' = { param($o) $o -match 'ALL PASS' }               # studio voice: cheap mics corrected, treble rebuilt, pops caught, no delay
    'SsbProbe' = { param($o) $o -match 'ALL PASS' }                      # studio voice's treble shift: 90 degrees across the band, no mirror image
    'CameraDirectorTest' = { param($o) $o -match 'ALL PASS' }           # the camera's virtual gimbal: tracking, framings, zones, presets, level, whiteboard / desk view, gestures, whiteboard finder
    'FanLeakTest' = { param($o) $o -match 'ALL PASS' }                   # a voice over a real fan (local recording): DeepFilterNet clearly beats RNNoise and realtime
    'LongTalkTest' = { param($o) $o -match 'ALL PASS' }                  # a minute of talking over a fan: the voice never sinks
    'SetupExportTest' = { param($o) $o -match 'ALL PASS' }                # setup export / import: no ids or paths, devices remapped by name, exact undo
    'ProfileSwitchTest' = { param($o) $o -match 'ALL PASS' }              # Auto EQ with overlapping apps: priorities, reasons, no flip-flop, same loudness, no clicks
    'NsRealtimeTest' = { param($o) $o -match 'ALL PASS' }                 # realtime noise removal: 2.7 ms, as much noise removed, voice and word starts kept
    'AllocTest' = { param($o) $o -match 'ALL PASS' }
    'ConcealRingTest' = { param($o) $o -match 'ALL PASS' }                # mic -> cable with late packets: one margin in the cable, late ones filled in, no holes, less delay
    'PlcTest' = { param($o) $o -match 'ALL PASS' }                        # a packet the mic's driver lost: filled in close to the real sound, no clicks
    'AppMixerTest' = { param($o) $o -match 'ALL PASS' }                   # Direct to headphones' mixer: apps from the side output as they are, drift-corrected, no gaps, no doubles                      # the audio paths allocate nothing while running (no garbage-collection pauses)
    'GapPolicyTest' = { param($o) $o -match 'ALL PASS' }                  # short gaps: faded, one-offs don't add delay, recurring ones set a floor; Health warns only when they keep coming
    'MultiMicTest' = { param($o) $o -match 'ALL PASS' }                    # several mics as one: auto mix, bleed, noise, no clicks, unplugged mic
    'MicRingTest' = { param($o) $o -match 'ALL PASS' }                     # virtual mic buffer: 20 min of +-300 ppm clock drift, no dropouts
    'CubeViewTest' = { param($o) $o -match 'ALL PASS' }                    # 3D sound cube: right-drag turns it, floors and cells
    'UpdaterTest' = { param($o) $o -match 'ALL PASS' }                     # updates: versions, checksums, zip update by the helper (fake install)
    'HeadphoneMatchTest' = { param($o) $o -match 'ALL PASS' }              # device names -> the right AutoEq model (downloads the index)
    'HeadsetEqTest' = { param($o) $o -match 'ALL PASS' }                   # each headset's own AutoEq model; 3D and profile EQ don't stack
    'MicPresetsTest' = { param($o) $o -match 'ALL PASS' }                  # each mic's own settings: keep, follow, export/import, assign
    'VoiceLockTest' = { param($o) $o -match 'ALL PASS' }                   # Only my voice: you stay on, someone else turned down (Windows' voices)
    'FarVoiceTest' = { param($o) $o -match 'ALL PASS' }                    # Across the room: a far voice as loud as at the desk, fan kept out
    'InputChannelsTest' = { param($o) $o -match 'ALL PASS' }               # multi-channel mics: interfaces, stereo mics, out-of-phase channels
    'DfnFallbackTest' = { param($o) $o -match 'ALL PASS' }                 # a GPU that can't run DeepFilterNet -> DeepFilterNet on the CPU
    'LowLatencyRingTest' = { param($o) $o -match 'ALL PASS' }              # 3 ms exclusive packets + 0.5 ms cable writer: no dropouts, small buffer
    'ChainDelayTest' = { param($o) $o -match 'ALL PASS' }                  # the latency readout matches each noise engine's measured delay
    'HardwareTuningTest' = { param($o) $o -match 'ALL PASS' }              # AI settings picked by CPU threads and graphics memory, once
    'CreditsTest' ={ param($o) $o -match 'ALL PASS' }                     # credits for every release, CREDITS.md in sync
    'LowVramTest' = { param($o) $o -match 'ALL PASS' }                     # Low VRAM: all AI < 250 MB of video memory, same picture, switching (needs a GPU)
}
$failed = 0
foreach ($name in $checks.Keys) {
    if ($Skip -contains $name) { '{0,-12} skipped' -f $name; continue }
    $out = try { & "$here\run.ps1" $name 2>&1 | Out-String } catch { $_.Exception.Message }
    $ok = & $checks[$name] $out
    if (-not $ok) { $failed++ }
    '{0,-12} {1}' -f $name, ($(if ($ok) { 'PASS' } else { 'FAIL' }))
    if (-not $ok) { $lines = $out.Trim() -split "`n"; ($lines | Where-Object { $_ -match 'FAIL' }) + ($lines | Select-Object -Last 4) | Select-Object -Unique | ForEach-Object { '    ' + $_ } }
}
if ($failed) { "$failed FAILED" } else { 'ALL PASSED' }
