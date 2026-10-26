"""Runs two camera models on the same 1080p NV12 frame on DirectML (a few frames, so the matting settles) in several
modes and reports how close their NV12 outputs are (PSNR of Y and UV, the share of pixels off by more than 4 levels),
whether they find the same faces, and saves both previews as PNG side by side.
Usage: python compare_camfx.py <reference.onnx> <candidate.onnx> <frame.nv12> <outdir>"""
import sys, os, struct, zlib
import numpy as np
import onnxruntime as ort

ref_path, cand_path, frame, outdir = sys.argv[1:5]
W, H = (3840, 2160) if os.path.getsize(frame) == 3840 * 2160 * 3 // 2 else (1920, 1080)
os.makedirs(outdir, exist_ok=True)
raw = np.fromfile(frame, np.uint8)
y = raw[:W * H].reshape(H, W); uv = raw[W * H:].reshape(H // 2, W)
pic = np.zeros((540, 960, 3), np.uint8); pic[:, :480] = (200, 120, 40); pic[:, 480:] = (30, 90, 200)   # replacement picture
pic[200:340] = np.linspace(0, 255, 960).astype(np.uint8)[None, :, None]


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
    p[12] = 1 if mode == 'mirror' else 0
    p[13] = 0.3
    return p


def run(path, mode, crop):
    so = ort.SessionOptions(); so.enable_mem_pattern = False; so.log_severity_level = 3
    so.add_session_config_entry('ep.dml.disable_graph_fusion', '1')
    s = ort.InferenceSession(path, so, providers=[('DmlExecutionProvider', {'device_id': '0', 'disable_metacommands': 'true'})])
    states = {i.name: np.zeros([d if isinstance(d, int) else 1 for d in i.shape], np.float32) for i in s.get_inputs() if i.name.startswith('r')}
    names = [o.name for o in s.get_outputs()]
    shapes = {i.name: i.shape for i in s.get_inputs()}
    pv = params(mode); pv = np.concatenate([pv, np.zeros(shapes['p'][0] - len(pv), np.float32)])   # newer models: more params (0 = off)
    f = {'y': y, 'uv': uv, 'crop': np.array(crop, np.int64), 'p': pv, 'lut': np.linspace(0, 1, 256).astype(np.float32), 'bg': pic}
    if 'warp' in shapes: f['warp'] = np.array([1, 0, 0, 0, 1, 0, 0, 0, 1], np.float32)   # no warp: the crop as it is
    for _ in range(8):
        f.update(states)
        out = dict(zip(names, s.run(None, f)))
        states = {k.replace('o', 'i'): out[k] for k in names if k.startswith('r') and k.endswith('o')}
    return out


def psnr(a, b):
    mse = np.mean((a.astype(np.float64) - b.astype(np.float64)) ** 2)
    return 99.0 if mse == 0 else 10 * np.log10(255.0 ** 2 / mse)


worst = 99.0
for mode, crop in [('none', [0, 0, H, W]), ('blur', [0, 0, H, W]), ('image', [0, 0, H, W]), ('dim', [0, 0, H, W]),
                   ('mirror', [0, 0, H, W]), ('blur', [H // 6, W // 6, H * 2 // 3, W * 2 // 3])]:
    a = run(ref_path, mode, crop); b = run(cand_path, mode, crop)
    py, puv = psnr(a['y_out'], b['y_out']), psnr(a['uv_out'], b['uv_out'])
    off = np.mean(np.abs(a['y_out'].astype(int) - b['y_out'].astype(int)) > 4) * 100
    fa = np.sort(a['scores'][0, :, 1])[-3:]; fb = np.sort(b['scores'][0, :, 1])[-3:]
    tag = '%s%s' % (mode, '' if crop[2] == H else '_zoom')
    print('  %-11s Y %5.1f dB, UV %5.1f dB, %4.1f%% of pixels off by >4; best face scores %s vs %s; thumb diff %d' % (
        tag, py, puv, off, np.round(fa, 3).tolist(), np.round(fb, 3).tolist(), int(np.max(np.abs(a['thumb'].astype(int) - b['thumb'].astype(int))))))
    worst = min(worst, py)
    side = np.concatenate([a['preview'][:, :, :3][:, :, ::-1], b['preview'][:, :, :3][:, :, ::-1]], axis=1)
    png(os.path.join(outdir, 'compare_%s.png' % tag), np.ascontiguousarray(side))
    # Where the luma differs most: that 240x320 patch of both, full resolution, plus the difference x8.
    d = np.abs(a['y_out'].astype(int) - b['y_out'].astype(int))
    pooled = d.reshape(9, 120, 1920 // 160, 160).mean(axis=(1, 3))
    r, c = np.unravel_index(np.argmax(pooled), pooled.shape)
    r0, c0 = min(max(r * 120 - 60, 0), 1080 - 240), min(max(c * 160 - 80, 0), 1920 - 320)
    pa, pb = a['y_out'][r0:r0 + 240, c0:c0 + 320], b['y_out'][r0:r0 + 240, c0:c0 + 320]
    pd = np.clip(d[r0:r0 + 240, c0:c0 + 320] * 8, 0, 255).astype(np.uint8)
    strip = np.concatenate([pa, pb, pd], axis=1)
    png(os.path.join(outdir, 'worst_%s.png' % tag), np.ascontiguousarray(np.repeat(strip[:, :, None], 3, axis=2)))
print('worst Y PSNR %.1f dB' % worst)
