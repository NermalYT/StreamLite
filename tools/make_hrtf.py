"""Converts SADIE II HRIR SOFA files (University of York, Apache-2.0) into StreamLite's head-response files.

Every measured direction and the full 256-tap, 48 kHz responses are kept, at the measurements' own 24-bit precision:
nothing is thinned out or shortened. The responses are already diffuse-field equalized by SADIE II; each head is only
levelled to the same loudness as the built-in KU100 set (res/hrtf_ku100.bin) so switching heads doesn't change volume.

Output: hrtf/<id>.blh - gzip of
    "BLH2", int32 count, int32 taps, float32 scale,
    count x (float32 azimuth anticlockwise, float32 elevation, taps x int24 left, taps x int24 right)

Usage: python tools/make_hrtf.py <folder with *_48K_24bit_256tap_FIR_SOFA.sofa> [out folder, default hrtf]
Needs numpy and h5py (development only).
"""
import glob
import gzip
import os
import struct
import sys

import h5py
import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def reference_energy():
    """Mean energy (both ears) of the built-in KU100 set's horizontal responses."""
    b = open(os.path.join(ROOT, "res", "hrtf_ku100.bin"), "rb").read()
    assert b[:4] == b"BLH1"
    n, taps = struct.unpack("<ii", b[4:12])
    scale = struct.unpack("<f", b[12:16])[0]
    off, e = 16, []
    for _ in range(n):
        az, el = struct.unpack("<ff", b[off:off + 8])
        off += 8
        l = np.frombuffer(b, "<i2", taps, off) * scale
        off += 2 * taps
        r = np.frombuffer(b, "<i2", taps, off) * scale
        off += 2 * taps
        if abs(el) < 0.5:
            e.append(np.sum(l * l) + np.sum(r * r))
    return float(np.mean(e))


def convert(path, out, ref):
    f = h5py.File(path, "r")
    fs = float(f["Data.SamplingRate"][0])
    assert fs == 48000, path
    pos = f["SourcePosition"][:]
    ir = f["Data.IR"][:].astype(np.float64)          # M x 2 x N (left, right)
    # One response per direction (the measurements repeat a few points).
    seen, keep = set(), []
    for i in range(len(pos)):
        az = round(float(pos[i, 0]) % 360, 3)
        el = round(float(pos[i, 1]), 3)
        if abs(el) >= 89.999:
            az = 0.0
        if (az, el) in seen:
            continue
        seen.add((az, el))
        keep.append(i)
    pos, ir = pos[keep], ir[keep]
    horiz = np.abs(pos[:, 1]) < 0.5
    e = np.mean(np.sum(ir[horiz] ** 2, axis=(1, 2)))
    ir *= np.sqrt(ref / e)
    peak = np.max(np.abs(ir))
    scale = peak / 8388607.0
    q = np.clip(np.round(ir / scale), -8388607, 8388607).astype(np.int32)
    taps = ir.shape[2]
    buf = bytearray(b"BLH2" + struct.pack("<iif", len(pos), taps, scale))
    for i in range(len(pos)):
        buf += struct.pack("<ff", float(pos[i, 0]) % 360, float(pos[i, 1]))
        for ear in (0, 1):
            v = q[i, ear].astype("<i4").tobytes()
            # Little-endian int32 -> the low three bytes.
            buf += b"".join(v[k:k + 3] for k in range(0, len(v), 4))
    with gzip.open(out, "wb", compresslevel=9) as g:
        g.write(bytes(buf))
    return len(pos), taps, horiz.sum()


def main():
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "hrtf")
    os.makedirs(dst, exist_ok=True)
    ref = reference_energy()
    for path in sorted(glob.glob(os.path.join(src, "*_48K_24bit_256tap_FIR_SOFA.sofa"))):
        subject = os.path.basename(path).split("_")[0]          # D1, D2, H3 ... H20
        name = {"D1": "ku100", "D2": "kemar"}.get(subject, "sadie-" + subject.lower())
        out = os.path.join(dst, name + ".blh")
        n, taps, h = convert(path, out, ref)
        print("%-12s %5d directions (%d on the horizon), %d taps, %.1f MB" % (name, n, h, taps, os.path.getsize(out) / 1e6))


if __name__ == "__main__":
    main()
