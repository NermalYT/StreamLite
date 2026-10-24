"""Finds the first node that makes DirectML graph fusion fail. Keeps only the nodes that feed one output, then cuts
that chain after k nodes (outputs = the tensors nothing later uses, with their inferred types) and tries a fused
DirectML session in a child process. Usage: python bisect_dml.py model.onnx output_name"""
import sys, subprocess, os, tempfile
import onnx
from onnx import helper as h

src, target = sys.argv[1], sys.argv[2]
m = onnx.shape_inference.infer_shapes(onnx.load(src))
vi = {v.name: v for v in list(m.graph.value_info) + list(m.graph.output) + list(m.graph.input)}
prod = {o: n for n in m.graph.node for o in n.output}
need, stack = set(), target.split(",")
while stack:
    t = stack.pop()
    n = prod.get(t)
    if n is None or id(n) in need: continue
    need.add(id(n)); stack.extend(i for i in n.input if i)
nodes = [n for n in m.graph.node if id(n) in need]
tmp = os.path.join(tempfile.gettempdir(), 'sl_bisect.onnx')
CHILD = r'''
import sys, onnxruntime as ort
so = ort.SessionOptions(); so.enable_mem_pattern = False; so.log_severity_level = 4
ort.InferenceSession(sys.argv[1], so, providers=["DmlExecutionProvider"])
print("OK")
'''


def ok(k):
    keep = nodes[:k]
    consumed = set(); [consumed.update(n.input) for n in keep]
    outs = [o for n in keep for o in n.output if o and o not in consumed and o in vi]
    if not outs: return True
    used = set(); [used.update(n.input) for n in keep]
    g = h.make_graph(keep, 'cut', [i for i in m.graph.input if i.name in used], [vi[o] for o in outs],
                     [i for i in m.graph.initializer if i.name in used])
    mm = h.make_model(g, opset_imports=m.opset_import); mm.ir_version = m.ir_version
    onnx.save(mm, tmp)
    r = subprocess.run([sys.executable, '-I', '-c', CHILD, tmp], capture_output=True, text=True, timeout=300)
    return 'OK' in r.stdout


print('chain for', target, ':', len(nodes), 'nodes; whole chain ok?', ok(len(nodes)))
lo, hi = 1, len(nodes)
while lo < hi:
    mid = (lo + hi) // 2
    if ok(mid): lo = mid + 1
    else: hi = mid
n = nodes[lo - 1]
print('first failing node', lo, n.op_type, n.name, list(n.input), [(a.name, h.get_attribute_value(a)) for a in n.attribute if a.type != onnx.AttributeProto.TENSOR])
for i in n.input:
    if i in vi: print('   in ', i, [d.dim_value or d.dim_param for d in vi[i].type.tensor_type.shape.dim], vi[i].type.tensor_type.elem_type)
