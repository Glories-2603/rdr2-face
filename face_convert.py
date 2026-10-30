"""
GTA V facial animation -> RDR 2 face controls.

Both games animate faces through controls (GTA: ~20, track 22/25; RDR: ~120, track 25) that an expression turns into
face bone motion. A GTA clip is evaluated with the GTA MP head expression (head_000_r); the motion of matching face
points (lip corners, lips, jaw, eyelids, brows, cheeks, tongue) is measured in the head's own axes, scaled to the RDR
head, and the RDR controls that reproduce it are solved per frame: bounded least squares on the RDR expression's
response to each control (bcustom), refined twice with the real expression, smoothed over time.
"""
import json
import math
from pathlib import Path

import numpy as np
from scipy.optimize import lsq_linear

import face_vm as vm

HERE = Path(__file__).resolve().parent
ASSETS = HERE / "assets"
FPS = 30.0

# face points: (GTA bone, RDR bone, weight, how far in front of the bone the point sits (m))
LANDMARKS = [
    ("FB_L_Lip_Corner", "FACIAL_L_LipCorner", 1.0, 0.004),
    ("FB_R_Lip_Corner", "FACIAL_R_LipCorner", 1.0, 0.004),
    ("FB_UpperLip", "FACIAL_C_LipUpper", 1.0, 0.004),
    ("FB_L_Lip_Top", "FACIAL_L_LipUpper", 0.8, 0.004),
    ("FB_R_Lip_Top", "FACIAL_R_LipUpper", 0.8, 0.004),
    ("FB_LowerLip", "FACIAL_C_LipLower", 1.0, 0.004),
    ("FB_L_Lip_Bot", "FACIAL_L_LipLower", 0.8, 0.004),
    ("FB_R_Lip_Bot", "FACIAL_R_LipLower", 0.8, 0.004),
    ("FB_Jaw", "FACIAL_C_Chin", 1.2, 0.006),
    ("FB_L_Lid_Upper", "FACIAL_L_EyelidUpper", 1.2, 0.012),
    ("FB_R_Lid_Upper", "FACIAL_R_EyelidUpper", 1.2, 0.012),
    ("FB_L_Brow_Out", "FACIAL_L_ForeheadOut", 0.8, 0.004),
    ("FB_R_Brow_Out", "FACIAL_R_ForeheadOut", 0.8, 0.004),
    ("FB_Brow_Centre", "FACIAL_L_ForeheadIn", 0.5, 0.004),
    ("FB_Brow_Centre", "FACIAL_R_ForeheadIn", 0.5, 0.004),
    ("FB_L_CheekBone", "FACIAL_L_CheekInner", 0.5, 0.004),
    ("FB_R_CheekBone", "FACIAL_R_CheekInner", 0.5, 0.004),
    ("FB_Tongue", "FACIAL_C_Tongue2", 0.2, 0.004),
]
# head size: distances measured on both heads (bone pairs)
SCALE_PAIRS = [(("FB_L_Lip_Corner", "FB_R_Lip_Corner"), ("FACIAL_L_LipCorner", "FACIAL_R_LipCorner")),
               (("FB_L_Brow_Out", "FB_R_Brow_Out"), ("FACIAL_L_ForeheadOut", "FACIAL_R_ForeheadOut"))]
REG = 0.3            # penalty per game-spread of each face shape (mm^2)
SMOOTH = 1           # frames each side of the moving average


class Rig:
    """A face rig: bones (tag -> parent, t, r, s, name), its face expression program, name -> tag."""

    def __init__(self, bones, prog, skin=None, verts=None, head="SKEL_Head"):
        self.bones, self.prog = bones, prog
        self.skin = skin or {}                                # tag -> where the bone's skin is (bind, model space)
        self.verts = verts or []                              # head mesh: [position, [[tag, weight], ...]]
        self.vpos = np.array([v[0] for v in self.verts]) if self.verts else np.zeros((0, 3))
        self._near = {}
        self.by_name = {b["name"]: t for t, b in bones.items()}
        self.head = self.by_name[head]
        self.neutral = self.evaluate({})

    def evaluate(self, controls):
        p = vm.Pose(self.bones)
        p.controls = controls
        vm.run(self.prog, p)
        return p

    def frame(self, front_bone):
        """Head axes (right, forward, up) from the bind pose: forward = towards the given face bone, level."""
        hp = np.array(self.neutral.world(self.head)[0])
        fp = np.array(self.neutral.world(self.by_name[front_bone])[0])
        fwd = fp - hp
        fwd[2] = 0
        fwd /= np.linalg.norm(fwd)
        up = np.array([0.0, 0.0, 1.0])
        right = np.cross(fwd, up)
        return np.stack([right, fwd, up]), hp

    def rest_point(self, name, k, fwd_world):
        """The face point a bone moves: its skin (head mesh vertices it weighs), else `k` in front of the bone."""
        tag = self.by_name[name]
        if tag in self.skin:
            return np.array(self.skin[tag])
        return np.array(self.neutral.world(tag)[0]) + fwd_world * k

    NEAR = 5

    def near(self, name, k, fwd_world):
        """The head mesh vertices around a face point (the ones the bone moves, nearest first)."""
        key = (name, k)
        if key not in self._near:
            rest = self.rest_point(name, k, fwd_world)
            tag = self.by_name[name]
            moved = [i for i, v in enumerate(self.verts) if any(t == tag for t, _ in v[1])]
            pool = np.array(moved) if len(moved) >= self.NEAR else np.arange(len(self.verts))
            d = np.linalg.norm(self.vpos[pool] - rest, axis=1)
            self._near[key] = pool[np.argsort(d)[:self.NEAR]] if len(pool) else np.zeros(0, int)
        return self._near[key]

    def points(self, pose, names, offsets, fwd_world):
        """Face points in this pose: the vertices around each point skinned like the game does (every bone of each
        vertex by its weight), averaged. Without a mesh: a point riding on the bone."""
        cache = {}

        def delta(tag):
            if tag not in cache:
                bp, bq = self.neutral.world(tag)
                p, q = pose.world(tag)
                cache[tag] = (np.array(bp), vm.qconj(bq), np.array(p), q)
            return cache[tag]

        out = []
        for name, k in zip(names, offsets):
            idx = self.near(name, k, fwd_world) if self.verts else []
            if len(idx):
                acc = np.zeros(3)
                for i in idx:
                    v = self.vpos[i]
                    for tag, w in self.verts[i][1]:
                        if tag not in self.bones:
                            acc += w * v
                            continue
                        bp, bqi, p, q = delta(tag)
                        acc += w * (p + np.array(vm.qrot(q, vm.qrot(bqi, tuple(v - bp)))))
                out.append(acc / len(idx))
                continue
            tag = self.by_name[name]
            bp, bq = self.neutral.world(tag)
            local = vm.qrot(vm.qconj(bq), tuple(self.rest_point(name, k, fwd_world) - np.array(bp)))
            p, q = pose.world(tag)
            o = vm.qrot(q, local)
            out.append((p[0] + o[0], p[1] + o[1], p[2] + o[2]))
        return np.array(out)


def load_rdr(sex="m"):
    d = json.loads((ASSETS / f"rdr_{sex}.json").read_text(encoding="utf-8"))
    bones = {int(k): v for k, v in d["bones"].items()}
    return Rig(bones, d["program"], {int(k): v for k, v in d.get("skin", {}).items()}, d.get("verts")), d["controls"]


def load_gta(sex="m"):
    d = json.loads((ASSETS / f"gta_{sex}.json").read_text(encoding="utf-8"))
    bones = {int(k): v for k, v in d["bones"].items()}
    return Rig(bones, d["program"], {int(k): v for k, v in d.get("skin", {}).items()}, d.get("verts"))


class Solver:
    """RDR controls for target face point motion (in RDR head axes, mm), solved in the space of the game's own face
    shapes (face_basis.json: principal directions of every game face clip frame), so the result is a face the game's
    animators would make; then held to the game's usual range per control and refined with the real expression."""

    def __init__(self, rdr, controls):
        self.rdr = rdr
        self.axes, _ = rdr.frame("FACIAL_C_LipUpper")
        self.fwd = self.axes[1]
        self.names = [l[1] for l in LANDMARKS]
        self.offsets = [l[3] for l in LANDMARKS]
        self.w = np.repeat(np.array([l[2] for l in LANDMARKS]), 3)
        self.p0 = self.measure({})
        basis = json.loads((ASSETS / "face_basis.json").read_text(encoding="utf-8"))
        info = {c["id"]: c for c in controls}
        self.dims, cols, keep = [], [], []
        for j, (cid, comp) in enumerate(basis["dims"]):
            c = info.get(cid)
            if not c or c["skip"]:
                continue
            lo, hi = c["lo"][comp], c["hi"][comp]
            fp = (self.measure(self.vector({(cid, comp): 1.0}, c)) - self.p0).ravel() if hi > 0 else None
            fn = (self.measure(self.vector({(cid, comp): -1.0}, c)) - self.p0).ravel() if lo < 0 else None
            col = fp if fn is None else -fn if fp is None else (fp - fn) / 2
            self.dims.append((cid, comp, lo, hi, c))
            cols.append(col)
            keep.append(j)
        self.Jd = np.stack(cols, 1)                              # face point motion per unit of each control
        self.B = np.array(basis["basis"])[keep]                  # dims x shapes
        self.sigma = np.array(basis["sigma"])
        self.lo = np.array([d[2] for d in self.dims])
        self.hi = np.array([d[3] for d in self.dims])
        self.JB = self.Jd @ self.B
        self.vars = self.dims                                    # (for the converter's bookkeeping)

    @staticmethod
    def vector(values, c=None):
        """{(id, comp): value} -> controls for the VM (track 25 values; 2-component controls as x, y)."""
        out = {}
        for (cid, comp), v in values.items():
            cur = list(out.get((cid, 25), (0.0, 0.0, 0.0, 0.0)))
            if (c and c["n"] == 1) or comp is None:
                cur = [v, v, v, v]
            else:
                cur[comp] = v
            out[(cid, 25)] = tuple(cur)
        return out

    def measure(self, controls):
        pose = self.rdr.evaluate(controls)
        pts = self.rdr.points(pose, self.names, self.offsets, self.fwd)
        return (pts @ self.axes.T) * 1000.0                    # mm in head axes

    def controls_of(self, x):
        out = {}
        for (cid, comp, lo, hi, c), v in zip(self.dims, x):
            if abs(v) < 1e-5:
                continue
            cur = list(out.get((cid, 25), (0.0, 0.0, 0.0, 0.0)))
            if c["n"] == 1:
                cur = [v, v, v, v]
            else:
                cur[comp] = v
            out[(cid, 25)] = tuple(cur)
        return out

    def _shapes(self, t):
        """Shape coefficients for a weighted target (least squares, each shape held near its game spread)."""
        A = np.vstack([self.JB * self.w[:, None], math.sqrt(REG) * np.diag(1.0 / np.maximum(self.sigma, 1e-4))])
        rhs = np.concatenate([t * self.w, np.zeros(self.B.shape[1])])
        return np.linalg.lstsq(A, rhs, rcond=None)[0]

    def solve(self, target, iterations=2):
        """target: (L, 3) mm displacement of the face points; returns control values (one per dim)."""
        t = target.ravel()
        a = self._shapes(t)
        x = np.clip(self.B @ a, self.lo, self.hi)
        for _ in range(iterations):
            got = (self.measure(self.controls_of(x)) - self.p0).ravel()
            a = a + self._shapes(t - got)
            x = np.clip(self.B @ a, self.lo, self.hi)
        return x


# ------------------------------------------------------------------------------------------------ GTA clips
def gta_frames(clip_json, clip_name):
    """Per-frame GTA control values of a clip (30 fps): list of {(bone, track): 4-vector}, duration."""
    clip = next(c for c in clip_json["clips"] if c["name"].lower() == clip_name.lower())
    anims = {a["hash"]: a for a in clip_json["anims"]}
    duration = max(clip["duration"], 1.0 / FPS)
    count = max(2, int(round(duration * FPS)) + 1)
    frames = [dict() for _ in range(count)]
    for part in clip["parts"]:
        a = anims.get(part["anim"])
        if not a:
            continue
        for tr in a["tracks"]:
            if tr["track"] not in (22, 25):
                continue
            n = 1 if tr["type"] == 2 else 3 if tr["type"] == 0 else 4
            vals = np.array(tr["values"], float).reshape(-1, n)
            for k in range(count):
                at = part["start"] + (k / FPS) * part["speed"]
                f = min(max(at / max(a["duration"], 1e-6) * (a["frames"] - 1), 0), a["frames"] - 1)
                i0 = int(f)
                i1 = min(i0 + 1, a["frames"] - 1)
                v = vals[i0] * (1 - (f - i0)) + vals[i1] * (f - i0)
                frames[k][(tr["bone"], tr["track"])] = (float(v[0]),) * 4 if n == 1 else tuple(float(x) for x in v[:3]) + (0.0,)
    for fr in frames:                                     # a control given only one way: the other from it
        for (bone, track) in list(fr):
            if track == 22 and (bone, 25) not in fr:
                fr[(bone, 25)] = (0.0, fr[(bone, 22)][0], 0.0, 0.0)
            if track == 25 and (bone, 22) not in fr:
                fr[(bone, 22)] = (fr[(bone, 25)][1],) * 4
    return frames, duration


class Converter:
    def __init__(self, sex="m"):
        self.gta = load_gta(sex)
        self.rdr, self.controls = load_rdr(sex)
        self.solver = Solver(self.rdr, self.controls)
        self.g_axes, _ = self.gta.frame("FB_UpperLip")
        self.g_names = [l[0] for l in LANDMARKS]
        self.g_offsets = [l[3] for l in LANDMARKS]
        self.g0 = self.g_measure(self.gta.neutral)
        ratios = []
        gf, rf = self.g_axes[1], self.solver.fwd
        for (ga, gb), (ra, rb) in SCALE_PAIRS:
            dg = np.linalg.norm(self.gta.rest_point(ga, 0, gf) - self.gta.rest_point(gb, 0, gf))
            dr = np.linalg.norm(self.rdr.rest_point(ra, 0, rf) - self.rdr.rest_point(rb, 0, rf))
            ratios.append(dr / dg)
        self.scale = float(np.mean(ratios))

    def g_measure(self, pose):
        pts = self.gta.points(pose, self.g_names, self.g_offsets, self.g_axes[1])
        return (pts @ self.g_axes.T) * 1000.0

    def convert(self, clip_json, clip_name, progress=None):
        """-> facebuild spec {frames, duration, controls: [{id, values}]} and a quality report."""
        frames, duration = gta_frames(clip_json, clip_name)
        xs, errs, sizes = [], [], []
        for k, fr in enumerate(frames):
            g = self.g_measure(self.gta.evaluate(fr)) - self.g0
            target = g * self.scale
            x = self.solver.solve(target)
            xs.append(x)
            got = self.solver.measure(self.solver.controls_of(x)) - self.solver.p0
            errs.append(float(np.sqrt(np.mean(np.sum((got - target) ** 2, 1)))))
            sizes.append(float(np.sqrt(np.mean(np.sum(target ** 2, 1)))))
            if progress:
                progress(k + 1, len(frames))
        X = np.array(xs)
        if SMOOTH and len(X) > 2:
            k = np.ones(2 * SMOOTH + 1) / (2 * SMOOTH + 1)
            X = np.stack([np.convolve(np.pad(X[:, j], SMOOTH, mode="edge"), k, mode="valid") for j in range(X.shape[1])], 1)
        per = {}
        for j, (cid, comp, lo, hi, c) in enumerate(self.solver.dims):
            arr = per.setdefault(cid, [np.zeros(len(X)) for _ in range(c["n"])])
            arr[comp] += X[:, j]
        spec = {"frames": len(X), "duration": duration,
                "controls": [{"id": cid, "values": [np.round(a, 4).tolist() for a in arrs]} for cid, arrs in sorted(per.items())
                             if max(np.abs(a).max() for a in arrs) > 1e-3]}
        report = {"frames": len(X), "duration": duration, "motion_mm": round(float(np.mean(sizes)), 2),
                  "error_mm": round(float(np.mean(errs)), 2), "controls": len(spec["controls"])}
        return spec, report
