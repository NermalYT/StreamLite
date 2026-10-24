"""Compares the half-precision DeepFilterNet (Low VRAM) with the full-precision one, both on DirectML, for quiet,
normal and full-scale signals: no NaN/inf, and how far the fp16 output is from the fp32 output (in dB below it).
Usage: python check_dfn_fp16.py ai/deepfilternet3.onnx ai/deepfilternet3_fp16.onnx"""
import sys
import numpy as np
import onnxruntime as ort

so = ort.SessionOptions()
so.enable_mem_pattern = False
prov = [('DmlExecutionProvider', {'device_id': '0', 'disable_metacommands': 'true'})]
a = ort.InferenceSession(sys.argv[1], so, providers=prov)
b = ort.InferenceSession(sys.argv[2], so, providers=prov)
n_states = a.get_inputs()[1].shape[0]
rng = np.random.default_rng(1)
t = np.arange(480 * 500) / 48000.0
# Speech-like: a gliding voiced tone with harmonics, syllable rhythm.
f0 = 140 + 40 * np.sin(2 * np.pi * 0.7 * t)
voice = sum(np.sin(2 * np.pi * k * np.cumsum(f0) / 48000.0) / k for k in range(1, 12)) * (0.5 + 0.5 * np.sin(2 * np.pi * 4 * t)) ** 2
voice = voice / np.max(np.abs(voice))
cases = {
    'silence': np.zeros_like(t),
    'quiet speech + noise (-50 dBFS)': 0.003 * voice + 0.0005 * rng.standard_normal(t.size),
    'normal speech + noise (-20 dBFS)': 0.1 * voice + 0.01 * rng.standard_normal(t.size),
    'loud speech + noise (-3 dBFS)': 0.7 * voice + 0.05 * rng.standard_normal(t.size),
    'full-scale white noise': np.clip(rng.standard_normal(t.size) * 0.5, -1, 1),
    'full-scale 1 kHz sine': np.sin(2 * np.pi * 1000 * t),
}
worst = 1e9
worst_abs = -1e9
ok = True
for name, x in cases.items():
    x = x.astype(np.float32)
    sa = np.zeros(n_states, np.float32); sb = np.zeros(n_states, np.float32)
    ya_all, yb_all = [], []
    for k in range(500):
        frame = x[k * 480:(k + 1) * 480]
        ya, sa, _ = a.run(None, {'input_frame': frame, 'states': sa, 'atten_lim_db': np.array(100.0, np.float32)})
        yb, sb, _ = b.run(None, {'input_frame': frame, 'states': sb, 'atten_lim_db': np.array(100.0, np.float32)})
        ya_all.append(ya); yb_all.append(yb)
    ya = np.concatenate(ya_all)[4800:]; yb = np.concatenate(yb_all)[4800:]      # skip the first 0.1 s
    finite = bool(np.all(np.isfinite(yb)))
    sig = np.sqrt(np.mean(ya ** 2)); err = np.sqrt(np.mean((ya - yb) ** 2))
    db = 20 * np.log10(sig / err) if err > 0 and sig > 0 else float('inf')
    print('  %-34s output %6.1f dBFS, fp16 differs by %s (%6.1f dBFS)  %s' % (name, 20 * np.log10(sig + 1e-12), ('%5.1f dB below it' % db) if np.isfinite(db) else 'nothing',
                                                                          20 * np.log10(err + 1e-12), '' if finite else 'NOT FINITE'))
    ok = ok and finite
    if 'speech' in name and sig > 1e-3: worst = min(worst, db)       # speech the model lets through
    worst_abs = max(worst_abs, 20 * np.log10(err + 1e-12))
# Pass: speech that comes through stays >= 30 dB clean of fp16 error, and no difference anywhere is louder than -60 dBFS.
print('speech: fp16 differs by %.1f dB below the signal; largest difference %.1f dBFS' % (worst, worst_abs))
print('PASS' if ok and worst >= 30 and worst_abs <= -60 else 'FAIL')
