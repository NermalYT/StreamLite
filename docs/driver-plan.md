# A StreamLite audio driver: what it would gain, what it takes

StreamLite uses VB-Audio's virtual cables today: VB-CABLE carries the mic, Hi-Fi Cable carries game sound. They work,
but two of their limits now set most of StreamLite's remaining delay. Fixing those limits needs StreamLite's own
kernel-mode audio driver.

## Where the delay is (measured on the test PC, 3.1.1)

**Mic to Discord.** The mic part is measured from status.txt; the cable's reading side is Windows' fixed engine
step.

| Step | Now | Why |
| --- | --- | --- |
| Mic packet | 1.6 ms | the Yeti's 3 ms packets (USB, exclusive) |
| StreamLite buffer | 0.2 ms | only what the drift correction's interpolator needs (3.1.0: 3.3-4.4 ms) |
| Cable lead | 4.8 ms | the one safety margin: VB-CABLE's play position moves in steps of up to 1.5 ms, plus the mic's 3 ms packets and their jitter |
| Cable to the app (Discord) | 10 ms step | Windows mixes the cable's input side in 10 ms steps (VB-CABLE allows no smaller one) |

**Game to headphones (Direct to headphones).**

| Step | Now | Why |
| --- | --- | --- |
| StreamLite buffer | 12.0 ms | game sound arrives from StreamLite - Out in Windows' 10 ms packets |
| Output queue | 4.5 ms | 3 ms exclusive steps |

## What StreamLite's own driver would change

- **Mic input that StreamLite clocks itself.**
  - "StreamLite Mic" would be fed straight from StreamLite through shared memory, with no render side.
  - Its clock would be StreamLite's writes, so there's no second clock to drift against and nothing to write ahead.
  - The cable lead (about 5 ms) would shrink to about 1 ms.
- **Small steps on the app's side.**
  - The driver would declare 1-2 ms packets (WaveRT low-latency packet constraints).
  - StreamLite would hold a low-latency stream on the endpoint, so Windows mixes it in small steps for every app
    that records it.
  - Discord's 10 ms step would become about 2 ms.
- **The same for game sound.**
  - "StreamLite - Out" would hand StreamLite each small packet as the game writes it, with no 10 ms loopback packets.
  - The 3D buffer would drop from about 12 ms to about 3 ms.

Estimated total:
- **Mic to Discord:** about 12 ms less.
- **Game to headphones:** about 9 ms less.

Some steps would remain:
- the mic's own 3 ms packets
- StreamLite's processing (0.3 ms) and realtime noise removal (2.7 ms)
- the headphones' 3 ms steps
- the game's and Discord's own buffering

## What it takes

1. **Code.**
   - A WaveRT driver based on Microsoft's SysVAD sample (Windows-driver-samples, MIT license), with:
     - a capture endpoint and a render endpoint
     - a private interface StreamLite reads and writes through
     - low-latency packet constraints
   - The tools are free: Visual Studio 2022 and the Windows Driver Kit.
2. **Testing in a virtual machine.**
   - A Hyper-V VM with test signing switched on inside the VM only, never on your PC.
   - A bug in a kernel driver crashes Windows (blue screen), so every build runs in the VM first.
3. **Signing.** Windows 10/11 only loads kernel drivers that Microsoft has signed, and Memory Integrity (on by
   default) enforces it. The route for a small publisher is attestation signing:
   - an **EV code-signing certificate**: about $250-600 a year from a certificate authority, issued after identity
     and organization checks
   - a **Microsoft Partner Center hardware developer account**: free; registering needs the EV certificate
   - each driver release uploaded to Partner Center; Microsoft signs it, usually within hours
   - Attestation-signed drivers install on Windows 10/11 desktop without test mode. They aren't WHQL-certified; that
     would need Microsoft's hardware lab kit tests, which is optional.
4. **Shipping.**
   - The installer would add the driver (pnputil, with Windows asking for permission once).
   - StreamLite would use it when present and keep VB-Audio's cables as the fallback.

Steps 3 and 4 can only be done by the owner of the certificate. Until the certificate exists, StreamLite stays on
VB-Audio's cables. Every measured delay above is then what StreamLite can reach.
