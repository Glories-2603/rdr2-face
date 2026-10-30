"""
Game data of the facial animation converter (run once on Windows with RDR 2, GTA V, CodeX and the viewer's extracted
assets). Writes assets/ (Rockstar data: local only, never published):
- rdr_m.json / rdr_f.json: MP ped skeleton + the MP head's face bones, the face expression (bcustom of
  meta_base_male/female.yed) as a VM program, the face controls (id, 1 or 2 values, range used by the game's own
  face clips, what each one moves -> label and group for the editor)
- gta_m.json / gta_f.json: GTA MP skeleton (FB_ face bones) and the MP head expression head_000_r (male_mp_ambient.yed)
- presets_m.json / presets_f.json: the game's moods (face_human@gen_<sex>@base clips) as control values, for the editor

    python tools/make_data.py

Not self-contained: it reads the games through the author's toolchain, located by environment variables:
  RDR_VIEWER_DIR  a folder with the RDR 2 viewer's server.py and its extracted game assets (skeletons, heads, clips)
  RDR_YCD_TOOL    RdrYcdTool.dll (ycd/expression export; needs CodeX)     CODEX_DIR  CodeX 0.28
  GTA_SCAN        GtaVanillaScan.exe (GTA V expressions and heads)        GTA_DIR    the GTA V folder
The template clip dictionaries in src/RdrAnimCore/Data are not made here: see that folder's README.
"""
import os
import json
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, os.environ.get("RDR_VIEWER_DIR", str(ROOT.parent / "rdr_viewer")))
import face_vm as vm  # noqa: E402
import server as viewer  # noqa: E402  (the viewer's game data access: skeletons, heads, expressions)

ASSETS = ROOT / "assets"
TOOL = Path(os.environ.get("RDR_YCD_TOOL", "RdrYcdTool.dll"))
GTA_SCAN = Path(os.environ.get("GTA_SCAN", "GtaVanillaScan.exe"))
GTA = os.environ.get("GTA_DIR", r"C:\Program Files\Epic Games\GTAV")
CODEX = os.environ.get("CODEX_DIR", "CodeX")
CORE_DATA = ROOT / "src" / "RdrAnimCore" / "Data"
FACE_CLIPS = viewer.ASSETS / "anims" / "anim_0.rpf" / "anim" / "ingame" / "clip_face_human.rpf"
HEADS = {"m": "peds/x64/dlcpacks/mp003/dlc.rpf/x64/packs/base/models/metapeds_m.rpf/mp_head_mr1_010.ydd",
         "f": "peds/x64/dlcpacks/mp001/dlc.rpf/x64/packs/base/models/metapeds_m.rpf/mp_head_fr1_001.ydd"}
# GTA MP heads: their own skeleton holds the real face bone positions (the ped skeleton's are placeholders)
GTA_HEADS = {"m": r"mp_m_freemode_01.head_000_r\.ydd$", "f": r"mp_f_freemode_01.head_000_r\.ydd$"}
MOOD_DICTS = {"m": ["face_human@gen_male@base", "face_human@mp_male@base"], "f": ["face_human@gen_female@base"]}

# label of a control from the face bones it moves most (Russian, for the editor)
REGIONS = [("Tongue", "Язык", "Рот"), ("LipCorner", "Уголок рта", "Рот"), ("LipUpper", "Верхняя губа", "Губы"),
           ("LipLower", "Нижняя губа", "Губы"), ("Jaw", "Челюсть", "Рот"), ("Chin", "Подбородок", "Рот"),
           ("Mouth", "Рот", "Рот"), ("Nasolabial", "Носогубная складка", "Щёки"), ("Cheek", "Щека", "Щёки"),
           ("Eyelid", "Веко", "Глаза"), ("Eyesack", "Под глазом", "Глаза"), ("EyeCorner", "Уголок глаза", "Глаза"),
           ("Eyeball", "Глаз", "Глаза"), ("LookAt", "Взгляд", "Глаза"), ("Forehead", "Лоб / бровь", "Брови"),
           ("Brow", "Бровь", "Брови"), ("Nose", "Нос", "Нос"), ("Nostril", "Ноздря", "Нос"), ("Masseter", "Желвак", "Щёки"),
           ("Temple", "Висок", "Лоб"), ("Neck", "Шея", "Шея"), ("AdamsApple", "Кадык", "Шея"), ("UnderChin", "Под подбородком", "Шея")]


def tool(*args):
    r = subprocess.run(["dotnet", str(TOOL), "--codex", CODEX, *map(str, args)], capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(r.stdout + r.stderr)
    return r.stdout


def skin_points(xml, index_tags=None):
    """Where each bone's skin is: centroid of the head mesh vertices the bone moves (weight >= 0.15, weighted by w^2; bind pose, model
    space). RDR models map weights through <BoneMapping> (tags); GTA geometries through <BoneIDs> (skeleton indices
    -> index_tags)."""
    import re
    sums, verts = {}, []

    def add(sem, vertices, to_tag):
        for line in vertices.strip().splitlines():
            f = {}
            for ch, text in zip(sem, line.split("	")):
                f.setdefault(ch, []).extend(float(v) for v in text.split())
            w, i = f.get("W", []), f.get("I", [])
            infl = {}
            for k in range(min(len(w), len(i))):
                if w[k] > 0:
                    t = to_tag(int(i[k]))
                    if t is not None:
                        infl[t] = infl.get(t, 0.0) + w[k] / 255.0
            if infl:
                tot = sum(infl.values())
                verts.append([[round(v, 5) for v in f["P"][:3]], [[t, round(x / tot, 4)] for t, x in infl.items()]])
            for k in range(min(len(w), len(i))):
                wk = w[k] / 255.0
                if wk < 0.15:
                    continue
                bone = to_tag(int(i[k]))
                if bone is None:
                    continue
                s = sums.setdefault(bone, [0.0, 0.0, 0.0, 0.0, 0])
                ww = wk * wk
                s[0] += f["P"][0] * ww; s[1] += f["P"][1] * ww; s[2] += f["P"][2] * ww; s[3] += ww; s[4] += 1

    if "<LodHigh>" in xml:
        hi = xml[xml.index("<LodHigh>"):xml.index("</LodHigh>")]
        for model in re.finditer(r"<BoneMapping>([^<]*)</BoneMapping>(.*?)</Geometries>", hi, re.S):
            mapping = [int(v) for v in model.group(1).split()]
            for g in re.finditer(r"<Semantics>([^<]*)</Semantics>.*?<Vertices>(.*?)</Vertices>", model.group(2), re.S):
                add(g.group(1), g.group(2), lambda k: mapping[k] if k < len(mapping) else None)
    else:
        hi = xml[xml.index("<DrawableModelsHigh>"):xml.index("</DrawableModelsHigh>")]
        for g in re.finditer(r"<BoneIDs>([^<]*)</BoneIDs>.*?<Semantics>([^<]*)</Semantics>.*?<Vertices>(.*?)</Vertices>", hi, re.S):
            ids = [int(v) for v in g.group(1).split()]
            add(g.group(2), g.group(3), lambda k: index_tags[ids[k]] if k < len(ids) and ids[k] < len(index_tags) else None)
    return {b: [s[0] / s[3], s[1] / s[3], s[2] / s[3]] for b, s in sums.items() if s[4] >= 3}, verts


HEAD_TEXTURES = {"m": "mp_head_mr1_sc03_c0_000_ab", "f": "mp_head_fr1_sc03_c0_000_ab"}


def head_mesh(xml):
    """The RDR head for the editor's preview: positions, normals, uvs, triangles and 4 (bone tag, weight) per vertex."""
    import base64
    import re
    hi = xml[xml.index("<LodHigh>"):xml.index("</LodHigh>")]
    P, N, T, I, ST, SW = [], [], [], [], [], []
    for model in re.finditer(r"<BoneMapping>([^<]*)</BoneMapping>(.*?)</Geometries>", hi, re.S):
        mapping = [int(v) for v in model.group(1).split()]
        for g in re.finditer(r"<Semantics>([^<]*)</Semantics>.*?<Vertices>(.*?)</Vertices>\s*<Indices>(.*?)</Indices>", model.group(2), re.S):
            sem, base = g.group(1), len(P)
            for line in g.group(2).strip().splitlines():
                f = {}
                for ch, text in zip(sem, line.split("\t")):
                    f.setdefault(ch, []).append([float(v) for v in text.split()])
                P.append(f["P"][0][:3]); N.append(f["N"][0][:3]); T.append(f["T"][0][:2])
                w = [x for grp in f.get("W", []) for x in grp]
                i = [int(x) for grp in f.get("I", []) for x in grp]
                pairs = sorted(((w[k] / 255.0, mapping[i[k]] if i[k] < len(mapping) else 0) for k in range(min(len(w), len(i))) if w[k] > 0), reverse=True)[:4]
                tot = sum(p[0] for p in pairs) or 1.0
                pairs += [(0.0, 0)] * (4 - len(pairs))
                ST.append([t for _, t in pairs]); SW.append([x / tot for x, _ in pairs])
            I.extend(int(v) + base for v in g.group(3).split())
    enc = lambda a, dt: base64.b64encode(np.asarray(a, dt).tobytes()).decode()  # noqa: E731
    return {"verts": len(P), "pos": enc(P, np.float32), "nrm": enc(N, np.float32), "uv": enc(T, np.float32),
            "idx": enc(I, np.uint32), "tags": enc(ST, np.uint16), "weights": enc(SW, np.float32)}


def rdr_rig(sex):
    sk = json.loads(viewer.skeleton(sex))["bones"]
    bones = {}
    for b in sk:
        bones[b["tag"]] = dict(name=b["name"], parent=sk[b["parent"]]["tag"] if b["parent"] >= 0 else -1, t=b["t"], r=b["r"], s=b["s"])
    xml, _ = viewer.converted(HEADS[sex])
    for b in viewer.skeleton_bones(xml.read_text(encoding="utf-8", errors="replace")):
        bones.setdefault(b["tag"], dict(name=b["name"], parent=b["parent"], t=b["t"], r=b["r"], s=b["s"]))
    prog = json.loads(viewer.expressions(sex))["bcustom"]
    skin, verts = skin_points(xml.read_text(encoding="utf-8", errors="replace"))
    return bones, prog, skin, verts


def face_clip_data(names):
    """Control values of the game's face clips: {anim name: (frames, duration, {id: (n, values (frames, n))})}."""
    out = {}
    with tempfile.TemporaryDirectory() as tmp:
        for dict_name in names:
            src = FACE_CLIPS / f"{dict_name}.ycd"
            if not src.exists():
                continue
            js = Path(tmp) / "a.json"
            tool("anim2json", src, js, "facial")
            for a in json.loads(js.read_text(encoding="utf-8"))["anims"]:
                tr = {t["bone"]: (t["n"], np.array(t["v"], float).reshape(-1, t["n"])) for t in a["tracks"]}
                out[f"{dict_name}/{a['name']}"] = (a["frames"], a["duration"], tr)
    return out


GAME_FRAMES = {}      # filled by game_ranges(): id -> (n, frames x n) of every game face clip


def face_basis(controls, k=36, max_frames=60000):
    """Face shapes of the game: principal directions of the control vectors of every game face clip frame
    (not centred: all zero = neutral face). The converter solves faces in this space so they stay natural."""
    dims = [(c["id"], comp) for c in controls if not c["skip"] for comp in range(c["n"])]
    cols = []
    lengths = {len(GAME_FRAMES[cid][1]) for cid, _ in dims if cid in GAME_FRAMES}
    total = max(lengths) if lengths else 0
    for cid, comp in dims:
        v = GAME_FRAMES.get(cid)
        cols.append(v[1][:, comp] if v is not None and len(v[1]) == total else np.zeros(total))
    F = np.stack(cols, 1)
    F = F[np.abs(F).max(1) > 0.01]                            # frames with some expression
    if len(F) > max_frames:
        F = F[np.random.default_rng(0).choice(len(F), max_frames, replace=False)]
    _, sv, vt = np.linalg.svd(F, full_matrices=False)
    k = min(k, len(sv))
    return {"dims": [[cid, comp] for cid, comp in dims], "basis": np.round(vt[:k].T, 6).tolist(),
            "sigma": np.round(sv[:k] / np.sqrt(len(F)), 6).tolist(), "frames": int(len(F))}


def game_ranges():
    """How far the game's own face clips (every face_human@ dictionary, both sexes) usually take each control:
    {id: (n, lo per component, hi per component)}. "Usually" = the 95th percentile of the frames that use the
    control, so rare extremes (snarls, screams) don't become the normal range."""
    names = sorted(f.stem for f in FACE_CLIPS.glob("face_human@*.ycd") if "@test" not in f.stem)
    vals = {}
    clips = list(face_clip_data(names).values())
    all_ids = {}
    for _, _, tr in clips:
        for cid, (n, v) in tr.items():
            all_ids.setdefault(cid, n)
    for frames, _, tr in clips:
        for cid, n in all_ids.items():
            v = tr[cid][1] if cid in tr and tr[cid][0] == n and len(tr[cid][1]) == frames else np.zeros((frames, n))
            vals.setdefault(cid, (n, []))[1].append(v)
    out = {}
    for cid, (n, chunks) in vals.items():
        v = np.concatenate(chunks)
        GAME_FRAMES[cid] = (n, v)
        lo, hi = np.zeros(n), np.zeros(n)
        for k in range(n):
            pos, neg = v[:, k][v[:, k] > 0.01], v[:, k][v[:, k] < -0.01]
            hi[k] = np.percentile(pos, 95) if len(pos) >= 20 else (pos.max() if len(pos) else 0.0)
            lo[k] = np.percentile(neg, 5) if len(neg) >= 20 else (neg.min() if len(neg) else 0.0)
        out[cid] = (n, lo, hi)
    return out


def clip_parts(dict_name):
    """Clips of a game face dictionary: {clip name: (animation name, start, end)} (first layer)."""
    import re
    with tempfile.TemporaryDirectory() as tmp:
        x = Path(tmp) / "d.xml"
        tool("ycd2xml", FACE_CLIPS / f"{dict_name}.ycd", x)
        text = x.read_text(encoding="utf-8")
    out = {}
    clips = text[text.index("<Clips>"):text.index("<AnimDict")]
    for chunk in re.split(r'<Item type="(?:Multi|Single)">', clips)[1:]:
        name = re.search(r"<Name>pack:/([^<]*)\.clip</Name>", chunk)
        a = re.search(r"<Animation>([^<]*)</Animation>\s*<StartTime value=\"([^\"]+)\" />\s*<EndTime value=\"([^\"]+)\" />", chunk)
        if name and a:
            out[name.group(1)] = (a.group(1), float(a.group(2)), float(a.group(3)))
    return out


DIRECTIONS = {"up": "вверх", "down": "вниз", "fwd": "вперёд", "back": "назад", "out": "наружу", "in": "внутрь"}


def describe(bones, prog, cid, n):
    """What a control moves: label, group, the bones (strongest first) and whether it is eye gaze."""
    by_tag = bones
    p0 = vm.Pose(bones)
    vm.run(prog, p0)
    face = [t for t, b in bones.items() if b["name"].startswith(("FACIAL_", "LOC_", "CTRL_"))]
    base = {t: p0.world(t)[0] for t in face}
    moved, first = {}, {}
    for comp in range(n):
        for sign in (1, -1):
            p = vm.Pose(bones)
            v = [0.0] * 4
            if n == 1:
                v = [sign * 1.0] * 4
            else:
                v[comp] = sign * 1.0
            p.controls = {(cid, 25): tuple(v)}
            vm.run(prog, p)
            for t in face:
                vec = np.subtract(p.world(t)[0], base[t])
                d = float(np.linalg.norm(vec)) * 1000
                moved[t] = max(moved.get(t, 0.0), d)
                if comp == 0 and sign == 1:
                    first[t] = vec
    top = sorted(((d, by_tag[t]["name"]) for t, d in moved.items() if d > 0.3), reverse=True)
    gaze = any(nm.startswith(("LOC_", "CTRL_")) and "Look" in nm for _, nm in top[:3])
    label, group = "Канал", "Прочее"
    if top:
        main = top[0][1]
        for key, lab, grp in REGIONS:
            if key in main:
                side = " слева" if "_L_" in main else " справа" if "_R_" in main else ""
                label, group = lab + side, grp
                break
        tag = next(t for t, b in by_tag.items() if b["name"] == main)
        v = first.get(tag)
        if v is not None and n == 1 and np.linalg.norm(v) > 1e-5:
            out_x = v[0] * (1 if base[tag][0] >= 0 else -1)
            comps = {"up" if v[2] > 0 else "down": abs(v[2]), "fwd" if v[1] < 0 else "back": abs(v[1]),
                     "out" if out_x > 0 else "in": abs(v[0]) * (0 if "_C_" in main else 1)}
            label += ", " + DIRECTIONS[max(comps, key=comps.get)]
    return {"label": label, "group": group, "bones": [nm for _, nm in top[:4]], "mm": round(top[0][0], 1) if top else 0.0,
            "gaze": gaze}


def controls_of(bones, prog, ranges):
    """Face controls of this head: the game's range per component (+15 %), unused ones and eye gaze marked."""
    ids = sorted({t[0] for t in prog["tracks"] if t[1] == 25})
    out = []
    for cid in ids:
        n, lo, hi = ranges.get(cid, (1, np.zeros(1), np.zeros(1)))
        d = describe(bones, prog, cid, n)
        if d["mm"] < 0.3:
            continue                                              # moves nothing on this head
        lo = [round(max(-1.0, float(l) * 1.15), 3) if l < -0.02 else 0.0 for l in lo]
        hi = [round(min(1.0, float(h) * 1.15), 3) if h > 0.02 else 0.0 for h in hi]
        unused = not any(lo) and not any(hi)
        out.append(dict(id=cid, n=n, lo=lo, hi=hi, skip=d["gaze"] or unused, unused=unused, **d))
    return out


def presets(dicts):
    """The game's face clips as poses for the editor: control values at the middle of each clip."""
    out = []
    for dict_name in dicts:
        anims = {k.split("/", 1)[1].replace("pack:/", "").replace(".anim", ""): v for k, v in face_clip_data([dict_name]).items()}
        for clip, (anim, start, end) in sorted(clip_parts(dict_name).items()):
            if anim not in anims:
                continue
            frames, duration, tr = anims[anim]
            f = int(round((start + end) / 2 / max(duration, 1e-6) * (frames - 1)))
            f = min(max(f, 0), frames - 1)
            vals = {str(cid): [round(float(x), 4) for x in v[f]] for cid, (n, v) in tr.items() if np.abs(v[f]).max() > 1e-3}
            if vals:
                out.append({"name": clip, "dict": dict_name, "values": vals})
    return out


def gta_head_bones(sex):
    """Skeleton of the GTA MP head drawable (head_000_r): tag -> name (without the _000 suffix), parent tag, t, r, s."""
    import re
    sys.path.insert(0, str(TOOLS / "rdr_converter"))
    import gta_xml
    with tempfile.TemporaryDirectory() as tmp:
        r = subprocess.run([str(GTA_SCAN), "extract", GTA, GTA_HEADS[sex], tmp, "1"], capture_output=True, text=True)
        files = list(Path(tmp).glob("*.ydd"))
        if not files:
            raise RuntimeError("GTA MP head not found: " + r.stdout + r.stderr)
        x = gta_xml.ydd_xml(files[0].read_bytes(), Path(tmp) / "tex")
    sk = re.search(r"<Skeleton>(.*?)</Skeleton>", x, re.S).group(1)
    rows = []
    for b in re.finditer(r"<Item>\s*<Name>([^<]*)</Name>\s*<Tag value=\"(\d+)\" />(.*?)</Item>", sk, re.S):
        body = b.group(3)
        num = lambda tag, attrs: [float(v) for v in re.search(rf"<{tag} " + " ".join(f'{a}="([^"]+)"' for a in attrs), body).groups()]  # noqa: E731
        rows.append((re.sub(r"_\d{3}$", "", b.group(1)), int(b.group(2)), int(re.search(r'<ParentIndex value="(-?\d+)"', body).group(1)),
                     num("Translation", "xyz"), num("Rotation", "xyzw"), num("Scale", "xyz")))
    bones = {tag: dict(name=n, parent=rows[pi][1] if pi >= 0 else -1, t=t, r=r, s=s) for n, tag, pi, t, r, s in rows}
    skin, verts = skin_points(x, [row[1] for row in rows])
    return bones, skin, verts


def gta_rig(sex):
    bones, skin, verts = gta_head_bones(sex)
    with tempfile.TemporaryDirectory() as tmp:
        r = subprocess.run([str(GTA_SCAN), "yedxml", GTA, r"male_mp_ambient\.yed$", tmp], capture_output=True, text=True)
        xmls = sorted(Path(tmp).glob("*.xml"))
        if not xmls:
            raise RuntimeError("male_mp_ambient.yed not found in GTA V: " + r.stdout + r.stderr)
        prog = vm.gta_programs(xmls[-1].read_text(encoding="utf-8"), {"head_000_r"})["head_000_r"]
    return bones, prog, skin, verts


def main():
    ASSETS.mkdir(exist_ok=True)
    ranges = game_ranges()
    for sex in ("m", "f"):
        bones, prog, skin, verts = rdr_rig(sex)
        ctrls = controls_of(bones, prog, ranges)
        (ASSETS / f"rdr_{sex}.json").write_text(json.dumps({"bones": bones, "program": prog, "controls": ctrls, "skin": skin, "verts": verts}), encoding="utf-8")
        if sex == "m":
            basis = face_basis(ctrls)
            (ASSETS / "face_basis.json").write_text(json.dumps(basis), encoding="utf-8")
            print(f"face basis: {len(basis['basis'][0])} shapes from {basis['frames']} game frames, {len(basis['dims'])} dims")
        pre = presets(MOOD_DICTS[sex])
        (ASSETS / f"presets_{sex}.json").write_text(json.dumps(pre, ensure_ascii=False), encoding="utf-8")
        xml, _ = viewer.converted(HEADS[sex])
        (ASSETS / f"head_{sex}.json").write_text(json.dumps(head_mesh(xml.read_text(encoding="utf-8", errors="replace"))), encoding="utf-8")
        name = HEAD_TEXTURES[sex]
        (ASSETS / f"head_{sex}.png").write_bytes(viewer.texture_png(viewer.catalogue()["textures"][name], name))
        gb, gp, gskin, gverts = gta_rig(sex)
        (ASSETS / f"gta_{sex}.json").write_text(json.dumps({"bones": gb, "program": gp, "skin": gskin, "verts": gverts}), encoding="utf-8")
        print(f"{sex}: {len(bones)} bones, {len(ctrls)} controls ({sum(c['unused'] for c in ctrls)} unused by the game), "
              f"{len(pre)} game poses, skin points RDR {len(skin)} GTA {len(gskin)}, head verts RDR {len(verts)} GTA {len(gverts)}, GTA {len(gp['ops'])} ops")


if __name__ == "__main__":
    main()
