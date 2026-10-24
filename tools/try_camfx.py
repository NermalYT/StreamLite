"""Runs ai/camfx.onnx on a saved NV12 frame (DirectML and CPU): timing, which nodes fall back to the CPU, outputs.
Usage: python try_camfx.py <model> <frame.nv12> <W> <H> <outdir> [mode]"""
import sys, os, time, struct, zlib
import numpy as np
import onnxruntime as ort

model, frame, W, H, outdir = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), sys.argv[5]
mode = sys.argv[6] if len(sys.argv) > 6 else 'blur'
os.makedirs(outdir, exist_ok=True)
raw = np.fromfile(frame, np.uint8)
y = raw[:W * H].reshape(H, W)
uv = raw[W * H:].reshape(H // 2, W)


def png(path, rgb):
    h_, w_ = rgb.shape[:2]
    rows = b''.join(b'\x00' + rgb[r].tobytes() for r in range(h_))
    def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    open(path, 'wb').write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w_, h_, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows, 6)) + chunk(b'IEND', b''))


def params(mode):
    p = np.zeros(16, np.float32)
    p[0] = 0.5; p[1] = 0.4; p[2] = 1.1; p[3] = 0.2; p[4] = 0.6
    if mode == 'blur': p[5] = 1
    if mode == 'image': p[6] = 1
    if mode == 'dim': p[7] = 1
    p[8], p[9], p[10], p[11] = 0.5, 0.42, 0.12, 0.2
    p[12] = 0; p[13] = 0.3
    return p


def feeds(states, crop, p):
    bg = np.zeros((540, 960, 3), np.uint8); bg[:, :, 2] = 160; bg[:, :, 1] = 90
    f = {'y': y, 'uv': uv, 'crop': np.array(crop, np.int64), 'p': p,
         'lut': np.linspace(0, 1, 256).astype(np.float32), 'bg': bg}
    f.update(states)
    return f


RS = [(16, 135, 240), (20, 68, 120), (40, 34, 60), (64, 17, 30)]
provs = os.environ.get('PROVS', 'DmlExecutionProvider,CPUExecutionProvider').split(',')
for prov in provs:
    so = ort.SessionOptions(); so.enable_mem_pattern = False
    so.log_severity_level = 3
    if os.environ.get('NOFUSE'): so.add_session_config_entry('ep.dml.disable_graph_fusion', '1')
    t0 = time.perf_counter()
    s = ort.InferenceSession(model, so, providers=[prov])
    load = time.perf_counter() - t0
    states = {'r%di' % (i + 1): np.zeros((1,) + RS[i], np.float32) for i in range(4)}
    crop = [0, 0, H, W]
    names = [o.name for o in s.get_outputs()]
    times = []
    for i in range(12 if prov.startswith('Dml') else 3):
        t0 = time.perf_counter()
        out = dict(zip(names, s.run(None, feeds(states, crop, params(mode)))))
        times.append(time.perf_counter() - t0)
        states = {'r1i': out['r1o'], 'r2i': out['r2o'], 'r3i': out['r3o'], 'r4i': out['r4o']}
    print('%s: load %.2f s, per frame %.1f ms (first %.0f ms)' % (prov, load, 1000 * np.median(times[2:]), 1000 * times[0]))
    if True:
        pv = out['preview'][:, :, :3][:, :, ::-1]
        png(os.path.join(outdir, 'preview_%s_%dp.png' % (mode, H)), np.ascontiguousarray(pv))
        sc = out['scores'][0, :, 1]; bx = out['boxes'][0]
        top = np.argsort(-sc)[:3]
        print('  faces (score, x0 y0 x1 y1 of the 320x240 padded input):', [(round(float(sc[i]), 3), [round(float(v), 3) for v in bx[i]]) for i in top])
        print('  y_out', out['y_out'].shape, out['y_out'].dtype, 'mean', out['y_out'].mean().round(1), ' uv_out', out['uv_out'].shape, ' thumb', out['thumb'].shape)
        print('  state shapes', [out[k].shape for k in ['r1o', 'r2o', 'r3o', 'r4o']])
