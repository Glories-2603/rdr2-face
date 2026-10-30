"""
Expression VM for faces of both games (the stack machine of .yed expressions, same opcodes in GTA V and RDR 2 except
a few): runs a face expression on a skeleton pose with the facial control values of a clip frame and gives the face
bones' transforms. Programs:
- RDR: the viewer's format (rdr_viewer/server.py expressions()): ops [[code, args...]], 0x50 blend with lanes
- GTA: read from CodeWalker's expression XML (gta_program_from_xml): BlendVector / BlendQuaternion with per-axis
  piecewise sources

Values are 4-lane vectors (scalars splatted). Opcodes worked out from CodeWalker's ExpressionInstrType and from the
game data: jumps skip N instructions; GTA JumpIfTrue/False (44/45) peek at the condition (each branch starts with Pop),
RDR 0x70 pops it.
"""
import math
import re

TRACK_POS, TRACK_ROT, TRACK_SCALE = 0, 1, 2
CONTROL_TRACKS = (22, 25)          # GTA: 22 float + 25 vector (value in y); RDR: 25 float / 2-component


# ------------------------------------------------------------------------------------------------ quaternions
def qmul(a, b):
    return (a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1], a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
            a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3], a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2])


def qconj(q):
    return (-q[0], -q[1], -q[2], q[3])


def qnorm(q):
    l = math.sqrt(q[0] ** 2 + q[1] ** 2 + q[2] ** 2 + q[3] ** 2) or 1.0
    return (q[0] / l, q[1] / l, q[2] / l, q[3] / l)


def qrot(q, v):
    x, y, z, w = q
    ix = w * v[0] + y * v[2] - z * v[1]
    iy = w * v[1] + z * v[0] - x * v[2]
    iz = w * v[2] + x * v[1] - y * v[0]
    iw = -x * v[0] - y * v[1] - z * v[2]
    return (ix * w + iw * -x + iy * -z - iz * -y, iy * w + iw * -y + iz * -x - ix * -z, iz * w + iw * -z + ix * -y - iy * -x)


def from_euler(x, y, z):
    cx, sx, cy, sy, cz, sz = math.cos(x / 2), math.sin(x / 2), math.cos(y / 2), math.sin(y / 2), math.cos(z / 2), math.sin(z / 2)
    return (sx * cy * cz - cx * sy * sz, cx * sy * cz + sx * cy * sz, cx * cy * sz - sx * sy * cz, cx * cy * cz + sx * sy * sz)


def to_euler(q):
    x, y, z, w = q
    sinp = 2 * (w * y - z * x)
    return (math.atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y)),
            math.copysign(math.pi / 2, sinp) if abs(sinp) >= 1 else math.asin(sinp),
            math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z)))


def qslerp(a, b, t):
    d = sum(i * j for i, j in zip(a, b))
    if d < 0:
        b, d = tuple(-v for v in b), -d
    if d > 0.9995:
        return qnorm(tuple(i + t * (j - i) for i, j in zip(a, b)))
    th = math.acos(d)
    s = math.sin(th)
    return tuple((math.sin((1 - t) * th) * i + math.sin(t * th) * j) / s for i, j in zip(a, b))


# ------------------------------------------------------------------------------------------------ skeleton pose
class Pose:
    """Local transforms of a skeleton (tag -> parent, t, r, s) with model-space queries, plus control values."""

    def __init__(self, bones):
        self.bones = bones                                    # tag -> dict(parent, t, r, s, name)
        self.local = {k: [tuple(b["t"]), tuple(b["r"]), tuple(b["s"])] for k, b in bones.items()}
        self.controls = {}                                    # (bone, track) -> 4-vector

    def get(self, tag, track):
        L = self.local.get(tag)
        if L is None:
            return None
        return L[1] if track == TRACK_ROT else L[0] if track == TRACK_POS else L[2]

    def bind(self, tag, track):
        b = self.bones.get(tag)
        if b is None:
            return None
        return tuple(b["r"] if track == TRACK_ROT else b["t"] if track == TRACK_POS else b["s"])

    def set(self, tag, track, v):
        L = self.local.get(tag)
        if L is None:
            return
        if track == TRACK_ROT:
            L[1] = qnorm(tuple(v[:4]))
        elif track == TRACK_POS:
            L[0] = tuple(v[:3])
        elif track == TRACK_SCALE:
            L[2] = tuple(v[:3])

    def world(self, tag, depth=0):
        """Model-space position and rotation (scale ignored for the chain, like the face rigs need)."""
        L = self.local.get(tag)
        if L is None:
            return (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0)
        p = self.bones[tag]["parent"]
        if p < 0 or p not in self.local or depth > 64:
            return L[0], L[1]
        pp, pq = self.world(p, depth + 1)
        o = qrot(pq, L[0])
        return (pp[0] + o[0], pp[1] + o[1], pp[2] + o[2]), qmul(pq, L[1])

    def set_world(self, tag, track, v):
        p = self.bones.get(tag, {}).get("parent", -1)
        pp, pq = self.world(p) if p in self.local else ((0, 0, 0), (0, 0, 0, 1))
        if track == TRACK_ROT:
            self.set(tag, TRACK_ROT, qmul(qconj(pq), qnorm(tuple(v[:4]))))
        elif track == TRACK_POS:
            d = (v[0] - pp[0], v[1] - pp[1], v[2] - pp[2])
            self.set(tag, TRACK_POS, qrot(qconj(pq), d))


# ------------------------------------------------------------------------------------------------ VM
def splat(v):
    return (v, v, v, v)


ZERO = (0.0, 0.0, 0.0, 0.0)


def _vec4(v, track=None):
    if v is None:
        return (0.0, 0.0, 0.0, 1.0) if track == TRACK_ROT else (1.0, 1.0, 1.0, 0.0) if track == TRACK_SCALE else ZERO
    return (v[0], v[1], v[2], v[3] if len(v) > 3 else 0.0)


def _comp(pose, bone, track, fmt, comp, offset=False):
    """One component of a track value (rotations as euler angles, like the expressions compare them)."""
    if track in CONTROL_TRACKS and track != TRACK_POS:
        return pose.controls.get((bone, track), ZERO)[comp]
    v = pose.get(bone, track)
    if track == TRACK_ROT:
        q = v or (0.0, 0.0, 0.0, 1.0)
        if offset:
            b = pose.bind(bone, track) or (0, 0, 0, 1)
            q = qmul(qconj(b), q)
        return to_euler(q)[comp] if comp < 3 else q[3]
    if v is None:
        v = pose.controls.get((bone, track), (1.0, 1.0, 1.0, 0.0) if track == TRACK_SCALE else ZERO)
    if offset:
        b = pose.bind(bone, track) or (0, 0, 0)
        v = tuple(v[i] - b[i] for i in range(3))
    return v[comp] if comp < len(v) else 0.0


def _piece(axis, v):
    w, o, th = axis
    k = 0
    while k < len(th) and v >= th[k]:
        k += 1
    k = min(k, len(w) - 1)
    return w[k] * v + o[k]


def run(prog, pose, max_ops=200000):
    """Runs a program. prog = dict(ops=[...], regs=n, tracks=[[bone, track], ...]) as built by the loaders."""
    ops, tracks = prog["ops"], prog["tracks"]
    st = []
    regs = [0.0] * max(prog.get("regs", 1), 1)
    variables = {}
    pop = lambda: st.pop() if st else ZERO  # noqa: E731
    pc, n = 0, 0
    while pc < len(ops) and n < max_ops:
        o = ops[pc]
        c = o[0]
        n += 1
        if c == 0:
            break
        elif c == 1:
            pop()
        elif c == 2:
            v = pop(); st.append(v); st.append(v)
        elif c == 3:
            st.append(ZERO)
        elif c == 4:
            st.append((1.0, 1.0, 1.0, 1.0))
        elif c == 5:
            st.append(splat(o[1]))
        elif c in (6, 8):                                        # track value (8: relative to the default pose)
            _, ti, bone, track, fmt = o[:5]
            if track in CONTROL_TRACKS and track != TRACK_POS:
                st.append(pose.controls.get((bone, track), ZERO))
            else:
                v = _vec4(pose.get(bone, track), track)
                if c == 8:
                    b = _vec4(pose.bind(bone, track), track)
                    v = qmul(qconj(b), v) if track == TRACK_ROT else tuple(v[i] - b[i] for i in range(3)) + (0.0,)
                st.append(v)
        elif c in (7, 9):
            _, ti, bone, track, fmt, comp = o[:6]
            st.append(splat(_comp(pose, bone, track, fmt, comp, offset=(c == 9))))
        elif c == 10:                                            # model space
            _, ti, bone, track = o[:4]
            p, q = pose.world(bone)
            st.append(q if track == TRACK_ROT else p + (0.0,))
        elif c == 11:
            st.append(tuple(o[1]) if o[1] else ZERO)
        elif c == 14:                                            # spring definition: no dynamics here
            pass
        elif c == 15:
            st.append(tuple(abs(x) for x in pop()))
        elif c in (16, 27):
            v = pop(); st.append((-v[0], -v[1], -v[2], -v[3] if c == 16 else v[3]))
        elif c == 17:
            st.append(tuple(1.0 / x if x else 0.0 for x in pop()))
        elif c == 18:
            st.append(tuple(math.sqrt(x) if x > 0 else 0.0 for x in pop()))
        elif c == 28:
            st.append(tuple(x * x for x in pop()))
        elif c == 29:
            st.append(tuple(math.radians(x) for x in pop()))
        elif c == 30:
            st.append(tuple(math.degrees(x) for x in pop()))
        elif c == 31:
            st.append(tuple(min(1.0, max(0.0, x)) for x in pop()))
        elif c == 32:
            st.append((1.0, 1.0, 1.0, 1.0))
        elif c == 33:
            v = pop(); st.append(from_euler(v[0], v[1], v[2]))
        elif c == 34:
            st.append(to_euler(pop()) + (0.0,))
        elif c in (38, 40):                                      # set (40: relative to the default pose)
            v = pop()
            _, ti, bone, track = o[:4]
            if c == 40:
                b = pose.bind(bone, track)
                if b is None:
                    pc += 1
                    continue
                if track == TRACK_ROT:
                    q = v if abs(v[3]) > 1e-9 or abs(sum(x * x for x in v) - 1) < 1e-3 else from_euler(v[0], v[1], v[2])
                    v = qmul(b, q)
                else:
                    v = (b[0] + v[0], b[1] + v[1], b[2] + v[2])
            pose.set(bone, track, v)
        elif c in (39, 41):
            v = pop()
            _, ti, bone, track, fmt, comp = o[:6]
            cur = list(_vec4(pose.get(bone, track), track))
            if c == 41:
                b = _vec4(pose.bind(bone, track), track)
                cur[comp] = b[comp] + v[0]
            else:
                cur[comp] = v[0]
            pose.set(bone, track, tuple(cur))
        elif c == 42:
            v = pop()
            _, ti, bone, track = o[:4]
            if not (pc > 0 and ops[pc - 1][0] in (62, 0x5C)):    # look-at results need a target: keep the pose
                pose.set_world(bone, track, v)
        elif c == 43:
            pc += o[1]
        elif c == 44:                                            # GTA: peek
            if st and st[-1][0]:
                pc += o[1]
        elif c == 45:
            if not (st and st[-1][0]):
                pc += o[1]
        elif c == 0x70:                                          # RDR: pop
            if not pop()[0]:
                pc += o[1]
        elif c in (46, 47, 48, 49, 50, 53, 54, 55, 56, 72, 73, 0x4F):
            b, a = pop(), pop()
            f = {46: lambda x, y: x + y, 47: lambda x, y: x - y, 48: lambda x, y: x * y, 49: min, 50: max,
                 53: lambda x, y: 1.0 if x > y else 0.0, 54: lambda x, y: 1.0 if x < y else 0.0,
                 55: lambda x, y: 1.0 if x >= y else 0.0, 56: lambda x, y: 1.0 if x <= y else 0.0,
                 72: lambda x, y: 1.0 if x == y else 0.0, 73: lambda x, y: 1.0 if x != y else 0.0,
                 0x4F: lambda x, y: x ** y if x > 0 or float(y).is_integer() else 0.0}[c]
            st.append(tuple(f(a[i], b[i]) for i in range(4)))
        elif c == 51:
            b, a = pop(), pop()
            st.append(qmul(a, b))
        elif c == 57:
            hi, lo, x = pop(), pop(), pop()
            st.append(tuple(min(hi[i], max(lo[i], x[i])) for i in range(4)))
        elif c == 58:
            t, b, a = pop(), pop(), pop()
            st.append(tuple(a[i] + (b[i] - a[i]) * t[i] for i in range(4)))
        elif c == 59:
            cc, b, a = pop(), pop(), pop()
            st.append(tuple(a[i] * b[i] + cc[i] for i in range(4)))
        elif c == 60:
            t, b, a = pop(), pop(), pop()
            st.append(qslerp(a, b, t[0]))
        elif c == 61:
            z, y, x = pop(), pop(), pop()
            st.append((x[0], y[0], z[0], 0.0))
        elif c in (62, 0x5C):
            for _ in range(4):
                pop()
            st.append((0.0, 0.0, 0.0, 1.0))
        elif c in (63, 70):
            st.append(ZERO)
        elif c == 64:
            b, a = pop(), pop()
            st.append(qrot(a, b) + (0.0,))
        elif c in (66, 0x67):
            st.append(variables.get(o[1], ZERO))
        elif c == 67:
            variables[o[1]] = pop()
        elif c in (68, 69):                                      # GTA blends: sum of piecewise-linear sources
            acc = [0.0, 0.0, 0.0]
            for ti, comp, axes in o[1]:
                bone, track = tracks[ti][:2]
                v = _comp(pose, bone, track, 0, comp)
                for k in range(3):
                    acc[k] += _piece(axes[k], v)
            st.append(from_euler(*acc) if c == 69 else (acc[0], acc[1], acc[2], 0.0))
        elif c == 0x50:                                          # RDR blend: base + registers * lanes
            v = list(o[1])
            for r, x, y, z in o[2]:
                w = regs[r]
                if w:
                    v[0] += w * x; v[1] += w * y; v[2] += w * z
            st.append(tuple(v))
        elif c == 0x52:
            regs[o[1]] = pop()[0]
        elif c == 0x71:
            st.append(_vec4(pose.get(tracks[o[1]][0], TRACK_POS), TRACK_POS))
        elif c == 0x72:
            st.append(_vec4(pose.get(tracks[o[1]][0], TRACK_ROT), TRACK_ROT))
        else:
            raise ValueError(f"expression op {c} at {pc}")
        pc += 1
    return n


# ------------------------------------------------------------------------------------------------ GTA programs
GTA_OPS = {
    "End": 0, "Pop": 1, "Dup": 2, "Push0": 3, "Push1": 4, "PushFloat": 5, "TrackGet": 6, "TrackGetComp": 7,
    "TrackGetOffset": 8, "TrackGetOffsetComp": 9, "TrackGetBoneTransform": 10, "PushVector": 11, "DefineSpring": 14,
    "VectorAbs": 15, "VectorNeg": 16, "VectorRcp": 17, "VectorSqrt": 18, "VectorNeg3": 27, "VectorSquare": 28,
    "VectorDeg2Rad": 29, "VectorRad2Deg": 30, "VectorSaturate": 31, "TrackValid": 32, "FromEuler": 33, "ToEuler": 34,
    "TrackSet": 38, "TrackSetComp": 39, "TrackSetOffset": 40, "TrackSetOffsetComp": 41, "TrackSetBoneTransform": 42,
    "Jump": 43, "JumpIfTrue": 44, "JumpIfFalse": 45, "VectorAdd": 46, "VectorSub": 47, "VectorMul": 48, "VectorMin": 49,
    "VectorMax": 50, "QuatMul": 51, "VectorGreaterThan": 53, "VectorLessThan": 54, "VectorGreaterEqual": 55,
    "VectorLessEqual": 56, "VectorClamp": 57, "VectorLerp": 58, "VectorMad": 59, "QuatSlerp": 60, "ToVector": 61,
    "LookAt": 62, "PushTime": 63, "VectorTransform": 64, "GetVariable": 66, "SetVariable": 67, "BlendVector": 68,
    "BlendQuaternion": 69, "PushDeltaTime": 70, "VectorEqual": 72, "VectorNotEqual": 73,
}


def _floats(s):
    return [float(v) for v in s.split()] if s and s.strip() else []


def gta_program_from_xml(item_xml):
    """One <Item> of CodeWalker's expression dictionary XML -> program dict for run()."""
    head = item_xml[:item_xml.index("<Streams>")]
    tracks = [[int(b), int(t), int(f)] for b, t, f in
              re.findall(r'<BoneId value="(\d+)" />\s*<Track value="(\d+)" />\s*<Format value="(\d+)" />', head)]
    ins = item_xml[item_xml.index("<Instructions>"):item_xml.index("</Instructions>")]
    ops = []
    for m in re.finditer(r'\n     <Item type="(\w+)"( />|>(.*?)\n     </Item>)', ins, re.S):
        t, body = m.group(1), m.group(3) or ""
        code = GTA_OPS.get(t)
        if code is None:
            raise ValueError(f"unknown GTA expression op {t}")
        if t.startswith("Blend"):
            srcs = []
            for s in re.finditer(r'<TrackIndex value="(\d+)" />\s*<ComponentIndex value="(\d+)" />(.*?)</Z>', body, re.S):
                axes = []
                for ax in ("X", "Y", "Z"):
                    a = re.search(rf"<{ax}>\s*<Weights>([^<]*)</Weights>\s*<Offsets>([^<]*)</Offsets>\s*<Thresholds(?: />|>([^<]*)</Thresholds>)", s.group(3), re.S)
                    axes.append((_floats(a.group(1)), _floats(a.group(2)), _floats(a.group(3) or "")))
                srcs.append((int(s.group(1)), int(s.group(2)), axes))
            ops.append([code, srcs])
            continue
        f = dict(re.findall(r'<(\w+) value="([^"]*)"', body))
        if "BoneId" in f:
            ops.append([code, int(f["TrackIndex"]), int(f["BoneId"]), int(f["Track"]), int(f.get("Format", 0)), int(f.get("ComponentIndex", 0))])
        elif t == "PushFloat":
            ops.append([code, float(f["Value"])])
        elif t == "PushVector":
            v = re.search(r'<Value x="([^"]+)" y="([^"]+)" z="([^"]+)" w="([^"]+)"', body)
            ops.append([code, [float(x) for x in v.groups()] if v else None])
        elif t in ("Jump", "JumpIfTrue", "JumpIfFalse"):
            ops.append([code, int(f["InstructionOffset"])])
        elif t in ("GetVariable", "SetVariable"):
            ops.append([code, f.get("Variable", f.get("Hash", "0"))])
        else:
            ops.append([code])
    return {"regs": 1, "tracks": tracks, "ops": ops}


def gta_programs(xml_text, names):
    out = {}
    for it in re.split(r"\n <Item>", xml_text):
        m = re.search(r"<Name>pack:/([^<]*)\.expr</Name>", it)
        if m and m.group(1) in names:
            out[m.group(1)] = gta_program_from_xml(it)
    return out
