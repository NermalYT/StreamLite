# Performance across hardware tiers

`test/PerfTest.cs` measures StreamLite's real-time work on three hardware tiers and compares every release with the
one before it. Run it on a quiet PC (no game or video playing):

```powershell
.\test\run.ps1 PerfTest          # all tiers, ~40 s of real-time work each, compared with perf/baseline.json
.\test\run.ps1 PerfTest quick    # shorter runs
.\test\run.ps1 PerfTest save     # also stores this version's results in perf/baseline.json
```

## The tiers

Each tier runs in its own process on the test PC:

| Tier | CPU threads | A game busy on | Graphics card |
| --- | --- | --- | --- |
| low | 2 | 1 | none (all AI on the CPU) |
| mid | 4 | 2 | 4 GB class (the Low VRAM models) |
| high | all | half | the full card |

The thread count comes from process affinity. The "game" is threads at normal priority that never rest, so
StreamLite's audio threads run first, as they do live (highest priority, "Pro Audio" scheduling). A tier gets the
defaults a fresh install picks for that hardware (`HardwareTuning`).

## What runs, at the real pace

- **Mic:** the full voice chain (noise remover, gate, compressor, de-esser, auto level, EQ, limiter) in 3 ms packets,
  with two added microphones mixed in by the auto mix.
- **3D audio:** 7.1 game sound placed in true 360° (the footsteps preset), in 5.3 ms blocks, plus two added outputs,
  each with its own EQ and drift-corrected buffer.
- **StreamLite Camera:** the effects graph (face tracking, background blur, tone and skin) at 30 fps.

Probes then run each noise remover and each camera graph on its own per tier.

## What's measured

- **Dropouts:** a packet finished later than the buffer can bridge (10 ms).
- **Processing time:** average and slowest 1% per packet, and the worst lateness.
- **Speed headroom:** how many times slower a CPU core could be and each part still keeps real time. The tiers emulate
  fewer threads, not slower cores, so this says how much slower hardware gets away with.
- **Latency:** mic (packet + processing + the noise remover's own delay) and 3D (block + renderer).
- **CPU:** from the CPU's cycle counters, minus the game's. Windows' CPU times are sampled every 15.6 ms and miss
  the short bursts of audio threads.
- **RAM:** private bytes of the test process.
- **Video memory:** dedicated, from Windows' GPU counters.

## How the defaults follow from it

- **DeepFilterNet on the CPU** becomes the default from the fewest CPU threads where it keeps real time under a
  game's load and, on cores 2.5x slower than the test PC's, still costs under 6% of the whole CPU. The test checks
  that this matches `HardwareTuning.DfnMinThreads`.
- **The camera without a graphics card:** the effects manage 2-8 fps on the CPU, so StreamLite Camera plays without
  effects there (30 fps) and says why.
- **4K capture** stays the default on a graphics card: it costs only a little more than 1080p.

## Results: 3.0.2, 3.0.0 and 2.7.0

Test PC: AMD Ryzen 9 9950X3D2 (32 threads), GeForce RTX 5080 16 GB. Everything at once (mic with two added mics, 3D with
two added outputs, camera) for 40 s per tier, under the game load. All releases were measured with the same test
(2.7.0 from its tag).

| Tier | Release | Dropouts | StreamLite CPU | Mic: slowest 1% | 3D: slowest 1% | Camera | RAM | Video memory |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| low | 2.7.0 | 0 | 57.3% | 0.50 ms | 1.08 ms | 2.3 fps (effects on the CPU) | 98 MB | - |
| low | 3.0.0 | 0 | **13.3%** | 0.43 ms | 0.97 ms | **30 fps** (no effects) | 94 MB | - |
| low | **3.0.2** | 0 | 15.9% | 0.46 ms | 1.27 ms | 30 fps (no effects) | 91 MB | - |
| mid | 2.7.0 | 0 | 7.6% | 0.44 ms | 1.03 ms | 30 fps | 307 MB | 166 MB |
| mid | 3.0.0 | 0 | 7.4% | 0.43 ms | 0.99 ms | 30 fps | 312 MB | 166 MB |
| mid | **3.0.2** | 0 | 8.6% | 0.47 ms | 1.26 ms | 30 fps | 312 MB | 166 MB |
| high | 2.7.0 | 0 | 1.4% | 0.04 ms | 1.02 ms | 30 fps (4K) | 387 MB | 438 MB |
| high | 3.0.0 | 0 | 1.4% | 0.04 ms | 1.01 ms | 30 fps (4K) | 374 MB | 438 MB |
| high | **3.0.2** | 0 | 1.6% | 0.51 ms | 1.27 ms | 30 fps (4K) | 384 MB | 438 MB |

- **Latency (StreamLite's part):**
  - Mic: 3.0.2 6.1 ms with any noise remover (3 ms packet, processing, 2.7 ms realtime filter). 3.0.0 23.4 ms with
    RNNoise and 53 ms with DeepFilterNet (its own look-ahead).
  - 3D: 3.0.2 11.9 ms; 3.0.0 13.0 ms.
- **What 3.0.2's realtime costs:**
  - CPU: 12-20% more. The noise remover still runs in full, on a copy, to steer the 2.7 ms filter.
  - 3D: the renderer works in 1.3 ms steps (was 2.7 ms), which is 0.3 ms more per block at the slowest 1%.
  - DeepFilterNet tier: the mic's slowest 1% includes RNNoise, which steers while you talk (0.5 ms of each 3 ms packet).
- **Speed headroom:**
  - Mic chain: keeps real time on cores 20-23x slower than the test PC's (7.0x with DeepFilterNet on the CPU, 1.4 ms
    per 10 ms frame).
  - 3D with two added outputs: cores 5.2-6.1x slower.
  - Camera on the graphics card: 3.1-3.5x headroom.
- **Noise removers on their own (2-thread tier, 3.0.2 with the realtime filter):**
  - Classic: 1.5% of the CPU
  - RNNoise: 2.1%
  - DeepFilterNet on the CPU: 10.5% (19% of one core)
- **Camera graph on the CPU:** 2-6 fps at 47-49% of the CPU on every tier, which is why it falls back to no effects.

The raw numbers for every tier and probe are in `perf/baseline.json`. The next release is compared with them: more
dropouts, more than 20% more CPU, or more than 20% more RAM or video memory fail the test.
