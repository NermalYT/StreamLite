"""Builds ai/deepfilternet3_lean.onnx: the same DeepFilterNet 3 streaming model, rewritten so DirectML can run ALL of it.

The torchDF export keeps its 12 state tensors in one vector and splits it with SplitToSequence + SequenceAt, and checks
"is this state still all zeros (first frame)?" with NonZero -> Shape -> Gather -> Equal. Those run only on the CPU, so
ONNX Runtime cut the graph into 12 separately compiled DirectML pieces with copies in between - most of the model's
~150 MB of graphics memory. Here:
  SplitToSequence + SequenceAt(k)            -> one Split (same sizes), output k
  Equal(Gather(Shape(NonZero(x)), i), 0)     -> Equal(ReduceSum(Abs(x)), 0)    (no non-zero element <=> sum of |x| is 0)
Same results (checked by check_dfn_lean.py), one DirectML piece.

"gru16" also stores and runs the three GRUs (half the weights) in 16-bit floats, with everything else - the spectrum,
features, filters and the state vector - left in 32-bit: the Low VRAM model (ai/deepfilternet3_low.onnx). Making all
of it 16-bit changes the output far too much; GRUs alone stay ~50 dB below the speech (check_dfn_fp16.py).
Needs onnxconverter_common.

Usage: python make_dfn_lean.py ai/deepfilternet3.onnx ai/deepfilternet3_lean.onnx [gru16]
"""
import sys
import numpy as np
import onnx
from onnx import helper as h, numpy_helper as nh, TensorProto as T

src, dst = sys.argv[1], sys.argv[2]
GRU16 = len(sys.argv) > 3 and sys.argv[3] == 'gru16'
m = onnx.load(src)
g = m.graph
inits = {i.name: i for i in g.initializer}
producer = {o: n for n in g.node for o in n.output}
consumers = {}
for n in g.node:
    for i in n.input:
        consumers.setdefault(i, []).append(n)

def const_value(name):
    if name in inits:
        return nh.to_array(inits[name])
    p = producer.get(name)
    if p is not None and p.op_type == 'Constant':
        return nh.to_array(p.attribute[0].t)
    raise SystemExit('not a constant: ' + name)

new_nodes, drop, rename = [], set(), {}
counter = [0]
def name(p):
    counter[0] += 1
    return 'lean_%s_%d' % (p, counter[0])

# 1) SplitToSequence + SequenceAt -> Split
for n in g.node:
    if n.op_type != 'SplitToSequence':
        continue
    axis = next((a.i for a in n.attribute if a.name == 'axis'), 0)
    lens = const_value(n.input[1]).astype(np.int64)
    outs = [name('state') for _ in lens]
    split_sizes = name('sizes')
    g.initializer.append(nh.from_array(lens, split_sizes))
    new_nodes.append(h.make_node('Split', [n.input[0], split_sizes], outs, name=name('split'), axis=axis))
    drop.add(id(n))
    for c in consumers.get(n.output[0], []):
        if c.op_type != 'SequenceAt':
            raise SystemExit('unexpected consumer of the state sequence: ' + c.op_type)
        k = int(const_value(c.input[1]))
        rename[c.output[0]] = outs[k]
        drop.add(id(c))

# 2) Equal(Gather(Shape(NonZero(x)), i), 0) -> Equal(ReduceSum(Abs(x)), 0)
for n in g.node:
    if n.op_type != 'NonZero':
        continue
    x = n.input[0]
    shape = [c for c in consumers.get(n.output[0], []) if c.op_type == 'Shape']
    if len(shape) != 1 or len(consumers[n.output[0]]) != 1:
        raise SystemExit('NonZero used for something else')
    # Shape -> Gather (-> Gather ...) picks the element count; then Equal(count, 0).
    chain, cur = [], shape[0].output[0]
    while True:
        nxt = consumers.get(cur, [])
        if len(nxt) != 1:
            raise SystemExit('unexpected NonZero pattern at ' + cur)
        if nxt[0].op_type == 'Gather':
            chain.append(nxt[0]); cur = nxt[0].output[0]; continue
        break
    eq = nxt[0]
    if not chain or eq.op_type != 'Equal' or int(const_value(eq.input[1])) != 0:
        raise SystemExit('unexpected NonZero pattern: %s %s' % (eq.op_type, list(eq.input)))
    s = name('abs_sum')
    zero = name('zero')
    g.initializer.append(nh.from_array(np.array(0.0, np.float32), zero))
    new_nodes.append(h.make_node('Abs', [x], [s + '_abs'], name=name('abs')))
    new_nodes.append(h.make_node('ReduceSum', [s + '_abs'], [s], name=name('sum'), keepdims=0))
    new_nodes.append(h.make_node('Equal', [s, zero], [eq.output[0]], name=name('is_zero')))
    for d in [n, shape[0], eq] + chain:
        drop.add(id(d))

kept = [n for n in g.node if id(n) not in drop]
for n in kept:
    n.input[:] = [rename.get(i, i) for i in n.input]
for o in g.output:
    if o.name in rename:
        raise SystemExit('graph output renamed')
# Keep a valid topological order: new nodes depend only on graph inputs / earlier tensors; put them first where possible.
order, done = [], set(i.name for i in g.input) | set(i.name for i in g.initializer) | {''}
pending = new_nodes + kept
while pending:
    rest = []
    for n in pending:
        if all(i in done for i in n.input):
            order.append(n); done.update(n.output)
        else:
            rest.append(n)
    if len(rest) == len(pending):
        raise SystemExit('cycle or missing input: ' + rest[0].name)
    pending = rest
del g.node[:]
g.node.extend(order)
# Drop initializers nothing uses any more.
used = set(i for n in g.node for i in n.input)
keep_inits = [i for i in g.initializer if i.name in used]
del g.initializer[:]
g.initializer.extend(keep_inits)
if GRU16:
    from onnxconverter_common import float16
    others = sorted(set(n.op_type for n in g.node) - {'GRU'})
    m = float16.convert_float_to_float16(m, keep_io_types=True, op_block_list=others)
    g = m.graph
    # The converter can leave graph outputs as float16 when their producer sits between converted nodes: give such
    # outputs a float32 copy (internal users keep the float16 tensor).
    del g.value_info[:]
    types = {v.name: v.type.tensor_type.elem_type for v in onnx.shape_inference.infer_shapes(m).graph.value_info}
    fixed = []
    for n in g.node:
        for k, o in enumerate(n.output):
            out = next((x for x in g.output if x.name == o), None)
            if out is not None and types.get(o, out.type.tensor_type.elem_type) == T.FLOAT16:
                n.output[k] = o + '_fp16'
                out.type.tensor_type.elem_type = T.FLOAT
                fixed.append(o)
    for n in g.node:
        n.input[:] = [i + '_fp16' if i in fixed else i for i in n.input]
    for o in fixed:
        g.node.append(h.make_node('Cast', [o + '_fp16'], [o], name=o + '_to_fp32', to=T.FLOAT))
    del g.value_info[:]
onnx.checker.check_model(m)
onnx.save(m, dst)
ops = {}
for n in g.node: ops[n.op_type] = ops.get(n.op_type, 0) + 1
print('saved %s: %d nodes; NonZero %d, SplitToSequence %d, SequenceAt %d' % (dst, len(g.node), ops.get('NonZero', 0), ops.get('SplitToSequence', 0), ops.get('SequenceAt', 0)))
