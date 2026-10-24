"""Runs the original and the lean DeepFilterNet side by side on the CPU (ONNX Runtime) for a few seconds of noisy
speech-like audio, feeding each its own states back, and prints the largest difference between their outputs.
Usage: python check_dfn_lean.py ai/deepfilternet3.onnx ai/deepfilternet3_lean.onnx"""
import sys
import numpy as np
import onnxruntime as ort

a = ort.InferenceSession(sys.argv[1], providers=['CPUExecutionProvider'])
b = ort.InferenceSession(sys.argv[2], providers=['CPUExecutionProvider'])
n_states = a.get_inputs()[1].shape[0]
sa = np.zeros(n_states, np.float32)
sb = np.zeros(n_states, np.float32)
rng = np.random.default_rng(1)
t = np.arange(480 * 600) / 48000.0
voice = (0.2 * np.sin(2 * np.pi * 180 * t) * (np.sin(2 * np.pi * 3 * t) > 0)).astype(np.float32)
noise = (rng.standard_normal(voice.size) * 0.03).astype(np.float32)
x = voice + noise
worst = worst_state = 0.0
peak = 0.0
for k in range(600):
    frame = x[k * 480:(k + 1) * 480]
    ya, sa, la = a.run(None, {'input_frame': frame, 'states': sa, 'atten_lim_db': np.array(100.0, np.float32)})
    yb, sb, lb = b.run(None, {'input_frame': frame, 'states': sb, 'atten_lim_db': np.array(100.0, np.float32)})
    worst = max(worst, float(np.max(np.abs(ya - yb))))
    worst_state = max(worst_state, float(np.max(np.abs(sa - sb))))
    peak = max(peak, float(np.max(np.abs(ya))))
print('600 frames: largest output difference %.3g (output peak %.3f), largest state difference %.3g' % (worst, peak, worst_state))
print('IDENTICAL' if worst < 1e-4 else 'DIFFERENT')
