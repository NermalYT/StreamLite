"""Builds ai/camfx.onnx: StreamLite's whole camera effect chain as ONE graph for ONNX Runtime + DirectML.

Per frame (NV12 in, NV12 out, everything on the GPU):
  crop/zoom (face tracking) -> 4K/1080p -> 1080p resample -> tone curve (auto light) -> colour (saturation, warmth)
  -> denoise (self-guided filter) -> skin smoothing (inside the face ellipse) -> sharpen
  -> background (Robust Video Matting alpha, refined with a guided filter): blur / replace / dim
  -> mirror -> NV12 + a small BGRA preview.
Also, from the FULL (uncropped) frame: UltraFace face detection (so tracking sees the whole field of view) and a
small luma thumbnail for the auto-light statistics.

"lowvram" (1080p capture only) builds the Low VRAM variant: the same effects with far fewer full-size images in flight
on the graphics card - no 4K supersampling buffer, the replacement picture converted at its own 540p, each guided filter
computed once instead of twice, the face finder's small input taken straight from the 8-bit frame, the auto-light
thumbnail made first, and the mirror done in one step - then stored and computed in 16-bit floats (the guided filters
take their variance around each window's mean, which stays precise there). Check it with compare_camfx.py.

Usage: python make_camfx.py <rvm_mobilenetv3_fp32.onnx> <version-RFB-320.onnx> <out.onnx> <capture W> <capture H> [lowvram]
"""
import sys, os
import numpy as np
import onnx
from onnx import helper as h, TensorProto as T, numpy_helper as nh, version_converter

OPSET = 17
OH, OW = 1080, 1920
rvm_path, face_path, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
IW, IH = int(sys.argv[4]), int(sys.argv[5])          # capture size: every shape static (DirectML fusion needs it)
LOW = len(sys.argv) > 6 and sys.argv[6] == 'lowvram'
if LOW and (IW, IH) != (OW, OH): raise SystemExit('the Low VRAM variant is for 1080p capture')
RS = [(16, 135, 240), (20, 68, 120), (40, 34, 60), (64, 17, 30)]   # RVM states for 540x960 at downsample 0.5

nodes, inits = [], []
_n = [0]


def nm(p):
    _n[0] += 1
    return '%s_%d' % (p, _n[0])


def const(value, dtype=np.float32, name=None):
    a = np.array(value, dtype=dtype)
    n = name or nm('c')
    inits.append(nh.from_array(a, n))
    return n


def op(t, ins, n_out=1, **attrs):
    outs = [nm(t) for _ in range(n_out)]
    nodes.append(h.make_node(t, ins, outs, name=nm(t + '_node'), **attrs))
    return outs[0] if n_out == 1 else outs


def mul(a, b): return op('Mul', [a, b])
def add(a, b): return op('Add', [a, b])
def sub(a, b): return op('Sub', [a, b])
def div(a, b): return op('Div', [a, b])
def clip(x, lo, hi): return op('Clip', [x, const(lo), const(hi)])


def box(x, k):
    """k x k mean filter, same size (edges average only the valid pixels)."""
    p = k // 2
    return op('AveragePool', [x], kernel_shape=[k, k], strides=[1, 1], pads=[p, p, p, p], count_include_pad=0)


def pool(x, k):
    return op('AveragePool', [x], kernel_shape=[k, k], strides=[k, k])


def resize(x, sizes, mode='linear'):
    return op('Resize', [x, '', '', const(sizes, np.int64)], mode=mode, coordinate_transformation_mode='half_pixel')


def resize_aa(x, sizes):
    """Antialiased downscale (opset-18 behaviour emulated): area pre-filter by 2 when shrinking a lot."""
    return op('Resize', [x, '', '', const(sizes, np.int64)], mode='linear', coordinate_transformation_mode='half_pixel')


def guided(I, p, k, eps):
    """Guided filter (He et al.): edge-preserving smoothing of p, guided by I (both [1,1,H,W] or broadcastable)."""
    mI, mp = box(I, k), box(p, k)
    cov = sub(box(mul(I, p), k), mul(mI, mp))
    var = sub(box(mul(I, I), k), mul(mI, mI))
    a = div(cov, add(var, eps))
    b = sub(mp, mul(a, mI))
    return add(mul(box(a, k), I), box(b, k))


def guided_self(I, k, eps):
    """guided(I, I, k, eps) with each box filter done once and the variance taken around each pixel's window mean
    (never negative and precise in half floats, where mean(I*I) - mean(I)^2 cancels badly)."""
    mI = box(I, k)
    dev = sub(I, mI)
    var = box(mul(dev, dev), k)
    a = div(var, add(var, eps))
    b = sub(mI, mul(a, mI))
    return add(mul(box(a, k), I), box(b, k))


def scalar(p, i):
    """Element i of the params vector as a [1] tensor (broadcasts against images; DirectML fusion dislikes 0-D)."""
    return op('Slice', [p, const([i], np.int64), const([i + 1], np.int64)])


def yuv_to_rgb(Y, U, V):
    """BT.601 limited range (what webcams send); Y,U,V as [1,1,H,W], U/V centred on 0."""
    y = mul(sub(Y, const(16.0 / 255)), const(1.164))
    r = add(y, mul(V, const(1.596)))
    g = sub(sub(y, mul(U, const(0.392))), mul(V, const(0.813)))
    b = add(y, mul(U, const(2.017)))
    return op('Concat', [r, g, b], axis=1)


def rgb_to_yuv(rgb):
    r, g, b = op('Split', [rgb, const([1, 1, 1], np.int64)], n_out=3, axis=1)
    Y = add(add(add(mul(r, const(0.257)), mul(g, const(0.504))), mul(b, const(0.098))), const(16.0 / 255))
    U = add(add(mul(r, const(-0.148)), mul(g, const(-0.291))), mul(b, const(0.439)))
    V = add(add(mul(r, const(0.439)), mul(g, const(-0.368))), mul(b, const(-0.071)))
    return Y, U, V


# ------------------------------------------------------------------ inputs
inputs = [
    h.make_tensor_value_info('y', T.UINT8, [IH, IW]),
    h.make_tensor_value_info('uv', T.UINT8, [IH // 2, IW]),
    h.make_tensor_value_info('crop', T.INT64, [4]),          # top, left, height, width (luma pixels, even)
    h.make_tensor_value_info('p', T.FLOAT, [16]),
    h.make_tensor_value_info('lut', T.FLOAT, [256]),
    h.make_tensor_value_info('bg', T.UINT8, [540, 960, 3]),
    h.make_tensor_value_info('r1i', T.FLOAT, [1, *RS[0]]),
    h.make_tensor_value_info('r2i', T.FLOAT, [1, *RS[1]]),
    h.make_tensor_value_info('r3i', T.FLOAT, [1, *RS[2]]),
    h.make_tensor_value_info('r4i', T.FLOAT, [1, *RS[3]]),
]
P = 'p'
p_denoise, p_sharpen, p_sat, p_warm, p_skin = (scalar(P, i) for i in range(5))
p_blur, p_img, p_dim = scalar(P, 5), scalar(P, 6), scalar(P, 7)
p_fcx, p_fcy, p_frx, p_fry = (scalar(P, i) for i in range(8, 12))
p_mirror, p_edge = scalar(P, 12), scalar(P, 13)

# ------------------------------------------------------------------ crop (zoom) + resample to 1080p
crop = 'crop'
tl = op('Slice', [crop, const([0], np.int64), const([2], np.int64)])
sz = op('Slice', [crop, const([2], np.int64), const([4], np.int64)])
br = add(tl, sz)
y8c = op('Slice', ['y', tl, br, const([0, 1], np.int64)])
H2W = op('Shape', ['uv'])
uv3 = op('Reshape', ['uv', op('Concat', [op('Slice', [H2W, const([0], np.int64), const([1], np.int64)]), const([-1, 2], np.int64)], axis=0)])
tl2, br2 = div(tl, const([2, 2], np.int64)), div(br, const([2, 2], np.int64))
uv8c = op('Slice', [uv3, tl2, br2, const([0, 1], np.int64)])

Yc = op('Unsqueeze', [div(op('Cast', [y8c], to=T.FLOAT), const(255.0)), const([0, 1], np.int64)])        # [1,1,h,w]
UVc = op('Transpose', [div(op('Cast', [uv8c], to=T.FLOAT), const(255.0))], perm=[2, 0, 1])               # [2,h/2,w/2]
UVc = op('Unsqueeze', [UVc, const([0], np.int64)])                                                          # [1,2,h/2,w/2]
if LOW:
    # Straight to 1080p (the capture is 1080p, so only a zoomed crop is actually resampled).
    Y = resize(Yc, [1, 1, OH, OW])
    UV = resize(UVc, [1, 2, OH // 2, OW // 2])
else:
    # Supersample: resize to 2x the output, then 2x2 average -> clean 4K->1080p, and smooth interpolation when zoomed.
    Y = pool(resize(Yc, [1, 1, OH * 2, OW * 2]), 2)                                                         # [1,1,1080,1920]
    UV = pool(resize(UVc, [1, 2, OH, OW]), 2)                                                               # [1,2,540,960]

# ------------------------------------------------------------------ tone curve (auto light, brightness, contrast)
Y_pre = Y                                                                            # before the tone curve (auto-light stats)
if LOW:   # made now, so the untoned frame isn't kept until the end; two smaller averages keep half floats precise
    thumb = op('Cast', [op('Reshape', [clip(add(mul(pool(pool(Y_pre, 3), 4), const(255.0)), const(0.5)), 0.0, 255.0), const([90, 160], np.int64)])], to=T.UINT8)
idx = op('Cast', [clip(add(mul(Y, const(255.0)), const(0.5)), 0.0, 255.0)], to=T.INT32)
Y = op('Gather', ['lut', idx], axis=0)

# ------------------------------------------------------------------ colour: saturation + warmth (U/V centred)
C = sub(UV, const(128.0 / 255))
warm_shift = op('Concat', [mul(p_warm, const([-0.035])), mul(p_warm, const([0.035]))], axis=0)   # [2]
C = add(mul(C, p_sat), op('Reshape', [warm_shift, const([1, 2, 1, 1], np.int64)]))

# ------------------------------------------------------------------ denoise: self-guided filter on Y, box on chroma
eps_d = add(mul(mul(p_denoise, p_denoise), const(0.004)), const(1e-6))
Yd = add(Y, mul(p_denoise, sub((guided_self(Y, 5, eps_d) if LOW else guided(Y, Y, 5, eps_d)), Y)))
Cd = add(C, mul(p_denoise, sub(box(C, 3), C)))

# ------------------------------------------------------------------ grids for the face ellipse
gx = const(((np.arange(OW) + 0.5) / OW).reshape(1, 1, 1, OW))
gy = const(((np.arange(OH) + 0.5) / OH).reshape(1, 1, OH, 1))
dx = div(sub(gx, p_fcx), add(p_frx, const(1e-6)))
dy = div(sub(gy, p_fcy), add(p_fry, const(1e-6)))
face_mask = clip(mul(sub(const(1.0), add(mul(dx, dx), mul(dy, dy))), const(3.0)), 0.0, 1.0)   # soft-edged ellipse

# ------------------------------------------------------------------ skin smoothing (strong guided filter inside the face)
Ysm = guided_self(Yd, 13, const(0.0025)) if LOW else guided(Yd, Yd, 13, const(0.0025))
Ys = add(Yd, mul(mul(p_skin, face_mask), sub(Ysm, Yd)))

# ------------------------------------------------------------------ sharpen (unsharp mask, small radius)
Yh = clip(add(Ys, mul(p_sharpen, sub(Ys, box(Ys, 5)))), 0.0, 1.0)

# ------------------------------------------------------------------ person matte (Robust Video Matting at 540p)
Yq = pool(Yd, 2)                                                                     # [1,1,540,960]
Uq, Vq = op('Split', [Cd, const([1, 1], np.int64)], n_out=2, axis=1)
rgb540 = clip(yuv_to_rgb(Yq, Uq, Vq), 0.0, 1.0)

rvm = version_converter.convert_version(onnx.load(rvm_path), OPSET)
rmap = {'src': rgb540, 'r1i': 'r1i', 'r2i': 'r2i', 'r3i': 'r3i', 'r4i': 'r4i', 'downsample_ratio': const([0.5])}
def embed(model, prefix, mapping):
    g = model.graph
    ren = {}
    for i in g.initializer:
        ren[i.name] = prefix + i.name
        i.name = prefix + i.name
        inits.append(i)
    for i in g.input:
        if i.name in mapping: ren[i.name] = mapping[i.name]
    for n in g.node:
        for a in n.attribute:
            if a.type == onnx.AttributeProto.GRAPH: raise SystemExit('subgraphs not supported')
        n.input[:] = [ren.get(x, prefix + x) if x else x for x in n.input]
        for k, o in enumerate(n.output):
            ren[o] = prefix + o
            n.output[k] = prefix + o
        n.name = prefix + (n.name or nm('n'))
        nodes.append(n)
    return {o.name: ren[o.name] for o in g.output}
rvm_out = embed(rvm, 'rvm/', rmap)
pha = rvm_out['pha']                                                                 # [1,1,540,960]

# Fast guided upsampling of the matte: crisp hair/edges that follow the full-resolution image.
eps_a = const(2e-4)
mI, mp = box(Yq, 9), box(pha, 9)
if LOW:   # covariance / variance around each window's means (see guided_self)
    dI, dp = sub(Yq, mI), sub(pha, mp)
    a_ = div(box(mul(dI, dp), 9), add(box(mul(dI, dI), 9), eps_a))
else:
    a_ = div(sub(box(mul(Yq, pha), 9), mul(mI, mp)), add(sub(box(mul(Yq, Yq), 9), mul(mI, mI)), eps_a))
b_ = sub(mp, mul(a_, mI))
A = clip(add(mul(resize(box(a_, 9), [1, 1, OH, OW]), Yd), resize(box(b_, 9), [1, 1, OH, OW])), 0.0, 1.0)
# Edge tightening (p_edge 0..1): pushes the soft matte towards 0/1.
A = clip(add(mul(sub(A, const(0.5)), add(const(1.0), mul(p_edge, const(2.0)))), const(0.5)), 0.0, 1.0)
Ah = pool(A, 2)                                                                      # [1,1,540,960]

# ------------------------------------------------------------------ backgrounds
# Blur: area-downscale by 8, blur twice, back up (a wide, smooth, cheap lens blur).
bgY = resize(box(box(pool(Yd, 8), 5), 5), [1, 1, OH, OW])
bgC = resize(box(box(pool(Cd, 4), 5), 5), [1, 2, OH // 2, OW // 2])
# Replace: any-size RGB picture, scaled to fill.
bgf = op('Unsqueeze', [op('Transpose', [div(op('Cast', ['bg'], to=T.FLOAT), const(255.0))], perm=[2, 0, 1]), const([0], np.int64)])
if LOW:   # YUV at the picture's own 540p, then only Y up to 1080p (colour conversion and resizing commute)
    iY, iU, iV = rgb_to_yuv(bgf)
    iY = resize(iY, [1, 1, OH, OW])
    iC = op('Concat', [iU, iV], axis=1)
else:
    bgf = resize(bgf, [1, 3, OH, OW])
    iY, iU, iV = rgb_to_yuv(bgf)
    iC = pool(op('Concat', [iU, iV], axis=1), 2)
# Dim: darker and greyer room.
dY, dC = mul(Yd, const(0.35)), mul(Cd, const(0.3))
w_none = sub(sub(sub(const(1.0), p_blur), p_img), p_dim)
BY = add(add(add(mul(p_blur, bgY), mul(p_img, iY)), mul(p_dim, dY)), mul(w_none, Yh))
BC = add(add(add(mul(p_blur, bgC), mul(p_img, iC)), mul(p_dim, dC)), mul(w_none, Cd))
Yo = add(mul(A, Yh), mul(sub(const(1.0), A), BY))
Co = add(mul(Ah, Cd), mul(sub(const(1.0), Ah), BC))

# ------------------------------------------------------------------ mirror
import os
if LOW:   # one Gather with forward or reversed column numbers (instead of a reversed copy plus a Where)
    flip = op('Greater', [p_mirror, const(0.5)])
    cols = op('Where', [flip, const(np.arange(OW - 1, -1, -1), np.int64), const(np.arange(OW), np.int64)])
    cols2 = op('Where', [flip, const(np.arange(OW // 2 - 1, -1, -1), np.int64), const(np.arange(OW // 2), np.int64)])
    Yo = op('Gather', [Yo, cols], axis=3)
    Co = op('Gather', [Co, cols2], axis=3)
elif not os.environ.get('NOMIRROR'):
  Yo = op('Where', [op('Greater', [p_mirror, const(0.5)]),
                    op('Slice', [Yo, const([OW - 1], np.int64), const([-OW - 1], np.int64), const([3], np.int64), const([-1], np.int64)]), Yo])
  Co = op('Where', [op('Greater', [p_mirror, const(0.5)]),
                    op('Slice', [Co, const([OW // 2 - 1], np.int64), const([-OW // 2 - 1], np.int64), const([3], np.int64), const([-1], np.int64)]), Co])

# ------------------------------------------------------------------ outputs: NV12 + preview + thumbnail
y_out = op('Cast', [op('Reshape', [clip(add(mul(Yo, const(255.0)), const(0.5)), 0.0, 255.0), const([OH, OW], np.int64)])], to=T.UINT8)
uvo = op('Transpose', [op('Reshape', [add(Co, const(128.0 / 255)), const([2, OH // 2, OW // 2], np.int64)])], perm=[1, 2, 0])
uv_out = op('Cast', [op('Reshape', [clip(add(mul(uvo, const(255.0)), const(0.5)), 0.0, 255.0), const([OH // 2, OW], np.int64)])], to=T.UINT8)
pY = pool(Yo, 3)                                                                     # [1,1,360,640]
pC = resize(Co, [1, 2, 360, 640])
pU, pV = op('Split', [pC, const([1, 1], np.int64)], n_out=2, axis=1)
prgb = clip(yuv_to_rgb(pY, pU, pV), 0.0, 1.0)
pr, pg, pb = op('Split', [prgb, const([1, 1, 1], np.int64)], n_out=3, axis=1)
pbgra = op('Concat', [pb, pg, pr, op('Expand', [const(1.0), const([1, 1, 360, 640], np.int64)])], axis=1)
preview = op('Cast', [op('Transpose', [op('Reshape', [clip(add(mul(pbgra, const(255.0)), const(0.5)), 0.0, 255.0), const([4, 360, 640], np.int64)])], perm=[1, 2, 0])], to=T.UINT8)
if not LOW:
    thumb = op('Cast', [op('Reshape', [clip(add(mul(pool(Y_pre, 12), const(255.0)), const(0.5)), 0.0, 255.0), const([90, 160], np.int64)])], to=T.UINT8)

# ------------------------------------------------------------------ face detection on the full frame (UltraFace)
if LOW:
    # Exactly the pixels the 1080p -> 320x180 bilinear resize below reads, picked from the 8-bit frame: luma rows/columns
    # 6i+2 and 6i+3 (averaged), chroma 3i+1 - so no full-size float copy of the frame is made.
    y6 = op('Slice', [op('Reshape', ['y', const([180, 6, 320, 6], np.int64)]), const([2, 2], np.int64), const([4, 4], np.int64), const([1, 3], np.int64)])
    fY = op('Unsqueeze', [op('ReduceMean', [div(op('Cast', [y6], to=T.FLOAT), const(255.0))], axes=[1, 3], keepdims=0), const([0, 1], np.int64)])
    uv_s = op('Slice', [uv3, const([1, 1], np.int64), const([IH // 2, IW // 2], np.int64), const([0, 1], np.int64), const([3, 3], np.int64)])
    fC = sub(op('Unsqueeze', [op('Transpose', [div(op('Cast', [uv_s], to=T.FLOAT), const(255.0))], perm=[2, 0, 1]), const([0], np.int64)]), const(128.0 / 255))
else:
    Yf = op('Unsqueeze', [div(op('Cast', ['y'], to=T.FLOAT), const(255.0)), const([0, 1], np.int64)])
    UVf = op('Unsqueeze', [op('Transpose', [div(op('Cast', [uv3], to=T.FLOAT), const(255.0))], perm=[2, 0, 1]), const([0], np.int64)])
    fY = resize(Yf, [1, 1, 180, 320])
    fC = sub(resize(UVf, [1, 2, 180, 320]), const(128.0 / 255))
fU, fV = op('Split', [fC, const([1, 1], np.int64)], n_out=2, axis=1)
frgb = clip(yuv_to_rgb(fY, fU, fV), 0.0, 1.0)
fin = div(sub(mul(frgb, const(255.0)), const(127.0)), const(128.0))
fin = op('Pad', [fin, const([0, 0, 30, 0, 0, 0, 30, 0], np.int64), const(-127.0 / 128)], mode='constant')   # 16:9 into 4:3
face = version_converter.convert_version(onnx.load(face_path), OPSET)
face_out = embed(face, 'face/', {'input': fin})

outputs = [
    h.make_tensor_value_info('y_out', T.UINT8, [OH, OW]),
    h.make_tensor_value_info('uv_out', T.UINT8, [OH // 2, OW]),
    h.make_tensor_value_info('preview', T.UINT8, [360, 640, 4]),
    h.make_tensor_value_info('thumb', T.UINT8, [90, 160]),
    h.make_tensor_value_info('scores', T.FLOAT, [1, 4420, 2]),
    h.make_tensor_value_info('boxes', T.FLOAT, [1, 4420, 4]),
    h.make_tensor_value_info('r1o', T.FLOAT, [1, *RS[0]]), h.make_tensor_value_info('r2o', T.FLOAT, [1, *RS[1]]),
    h.make_tensor_value_info('r3o', T.FLOAT, [1, *RS[2]]), h.make_tensor_value_info('r4o', T.FLOAT, [1, *RS[3]]),
]
wires = {'y_out': y_out, 'uv_out': uv_out, 'preview': preview, 'thumb': thumb,
         'scores': face_out['scores'], 'boxes': face_out['boxes'],
         'r1o': rvm_out['r1o'], 'r2o': rvm_out['r2o'], 'r3o': rvm_out['r3o'], 'r4o': rvm_out['r4o']}
for k, v in wires.items():
    nodes.append(h.make_node('Identity', [v], [k], name='out_' + k))

# Constant nodes -> initializers (DirectML's graph fusion rejects node-produced 0-D constants, e.g. Clip bounds).
kept = []
for n in nodes:
    if n.op_type == 'Constant' and n.attribute and n.attribute[0].name == 'value':
        t = onnx.TensorProto(); t.CopyFrom(n.attribute[0].t); t.name = n.output[0]
        inits.append(t)
    else:
        kept.append(n)
nodes = kept
# Clip on an embedded model's output -> Min/Max (DirectML fusion rejected RVM's final alpha Clip).
kept = []
for n in nodes:
    if n.op_type == 'Clip' and n.name.startswith(('rvm/', 'face/')) and len(n.input) == 3 and n.input[1] and n.input[2]:
        mid = n.output[0] + '_hi'
        kept.append(h.make_node('Min', [n.input[0], n.input[2]], [mid], name=n.name + '_min'))
        kept.append(h.make_node('Max', [mid, n.input[1]], [n.output[0]], name=n.name + '_max'))
    else:
        kept.append(n)
nodes = kept

# Dead-code elimination (RVM's foreground-colour branch is unused). DirectML's graph fusion fails on any node output
# that nothing consumes, so prune until stable and report what's left.
out_names = {o.name for o in outputs}
while True:
    consumed = set(); [consumed.update(n.input) for n in nodes]
    dead = [n for n in nodes if all((not o) or (o not in consumed and o not in out_names) for o in n.output)]
    if not dead: break
    ids = {id(n) for n in dead}
    nodes = [n for n in nodes if id(n) not in ids]
# Partly-used Splits (RVM splits its output into colour residual + alpha): keep only the used parts, as Slices.
consumed = set(); [consumed.update(n.input) for n in nodes]
init_map = {i.name: i for i in inits}
fixed = []
for n in nodes:
    if n.op_type == 'Split' and any(o and o not in consumed and o not in out_names for o in n.output):
        axis = next((a.i for a in n.attribute if a.name == 'axis'), 0)
        sizes = None
        if len(n.input) > 1 and n.input[1] in init_map: sizes = nh.to_array(init_map[n.input[1]]).tolist()
        elif len(n.input) > 1:
            cn = next((c for c in nodes if c.op_type == 'Constant' and n.input[1] in c.output), None)
            if cn is not None: sizes = nh.to_array(cn.attribute[0].t).tolist()
        if sizes is None: sizes = next((list(a.ints) for a in n.attribute if a.name == 'split'), None)
        if sizes is None: raise SystemExit('Split without constant sizes: ' + n.name)
        start = 0
        for o, k in zip(n.output, sizes):
            if o in consumed or o in out_names:
                fixed.append(h.make_node('Slice', [n.input[0], const([start], np.int64), const([start + k], np.int64), const([axis], np.int64)], [o], name=n.name + '_' + o))
            start += k
    else:
        fixed.append(n)
nodes = fixed
while True:   # the Splits' size constants are now unused too
    consumed = set(); [consumed.update(n.input) for n in nodes]
    dead = [n for n in nodes if all((not o) or (o not in consumed and o not in out_names) for o in n.output)]
    if not dead: break
    ids = {id(n) for n in dead}
    nodes = [n for n in nodes if id(n) not in ids]
consumed = set(); [consumed.update(n.input) for n in nodes]
dangling = [o for n in nodes for o in n.output if o and o not in consumed and o not in out_names]
if dangling: raise SystemExit('still dangling: %s' % dangling)
used = set(); [used.update(n.input) for n in nodes]
inits = [i for i in inits if i.name in used]
graph = h.make_graph(nodes, 'streamlite_camfx', inputs, outputs, inits)
model = h.make_model(graph, opset_imports=[h.make_opsetid('', OPSET)], producer_name='StreamLite')
model.ir_version = 8
onnx.checker.check_model(model)
onnx.save(model, out_path)
print('saved', out_path, len(nodes), 'nodes')


def half_precision(path):
    """Low VRAM: fold the constants (ONNX Runtime's basic, standard-ops-only optimizations, so every shape is static),
    then keep and compute every image and network tensor in 16-bit floats; the model's inputs and outputs keep their
    types. Needs onnxruntime and onnxconverter_common."""
    import onnxruntime as ort
    from onnxconverter_common import float16
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_BASIC
    so.optimized_model_filepath = path
    ort.InferenceSession(path, so, providers=['CPUExecutionProvider'])
    m = onnx.load(path)
    del m.graph.value_info[:]
    m = float16.convert_float_to_float16(m, keep_io_types=True, op_block_list=['Range', 'NonZero', 'Shape', 'ConstantOfShape'])
    del m.graph.value_info[:]
    inits = {i.name: i for i in m.graph.initializer}
    for n in m.graph.node:
        # This script's own 8-bit -> float casts now produce float16 (the converter leaves Cast targets alone).
        if n.op_type == 'Cast' and n.name.startswith('Cast_node_'):
            for a in n.attribute:
                if a.name == 'to' and a.i == T.FLOAT: a.i = T.FLOAT16
        # Resize's roi / scales must stay float32.
        if n.op_type == 'Resize':
            for k in (1, 2):
                if k < len(n.input) and n.input[k] in inits and inits[n.input[k]].data_type == T.FLOAT16:
                    name = n.input[k] + '_f32'
                    if name not in inits:
                        inits[name] = nh.from_array(nh.to_array(inits[n.input[k]]).astype(np.float32), name)
                        m.graph.initializer.append(inits[name])
                    n.input[k] = name
    onnx.checker.check_model(m)
    onnx.save(m, path)
    print('half precision:', len(m.graph.node), 'nodes')


if LOW:
    half_precision(out_path)
