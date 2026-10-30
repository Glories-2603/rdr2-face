"""
RDR 2 facial animation converter: GTA V facial animations -> RDR 2 face controls, an editor to make face animations
on the RDR head, and a RedM resource with all of them.

    python server.py [--port 8776]      then open http://localhost:8776
    python server.py --host 0.0.0.0 --password <pw> [--data DIR]     on a remote server (browser asks the password)

Needs assets/ (python make_data.py, once) and RdrFaceTool (bin/<win-x64|linux-x64>/, self-contained) or the
RdrYcdTool build (facebuild / gtaface2json run without CodeX).
"""
import argparse
import base64
import hmac
import os
import platform
import json
import mimetypes
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import traceback
import urllib.parse
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import face_convert as fc  # noqa: E402

STATIC = HERE / "static"
ASSETS = HERE / "assets"
TOOL = HERE.parent.parent / "bin" / "Release" / "net8.0-windows" / "RdrYcdTool.dll"
FACE_TOOL = HERE / "bin" / ("win-x64/RdrFaceTool.exe" if os.name == "nt" else
                            ("linux-arm64" if platform.machine().lower() in ("aarch64", "arm64") else "linux-x64") + "/RdrFaceTool")
PASSWORD = ""


def set_data(path):
    global DATA, UPLOADS, LIBRARY, EXPORTS, SETTINGS_FILE
    DATA = Path(path)
    UPLOADS, LIBRARY, EXPORTS, SETTINGS_FILE = DATA / "uploads", DATA / "library", DATA / "export", DATA / "settings.json"


set_data(HERE / "data")
DEFAULTS = {"resource": "arbat_faces", "prefix": "arbat_face@", "command": "face", "folder": ""}
ID_RE = re.compile(r"^[a-z0-9_]{1,40}$")
LOCK = threading.Lock()
CONVERTERS = {}


def safe(name):
    return re.sub(r"[^a-z0-9_]+", "_", name.lower()).strip("_")[:40] or "face"


def settings():
    s = dict(DEFAULTS)
    if SETTINGS_FILE.exists():
        s.update(json.loads(SETTINGS_FILE.read_text(encoding="utf-8")))
    return s


def tool(*args):
    cmd = [str(FACE_TOOL)] if FACE_TOOL.is_file() else ["dotnet", str(TOOL)]
    r = subprocess.run([*cmd, *map(str, args)], capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError((r.stdout + r.stderr).strip().splitlines()[-1] if (r.stdout + r.stderr).strip() else "RdrFaceTool failed")
    return r.stdout


def converter(sex):
    with LOCK:
        if sex not in CONVERTERS:
            CONVERTERS[sex] = fc.Converter(sex)
        return CONVERTERS[sex]


# ---------------------------------------------------------------------------------------------- uploads (GTA files)
def upload_clips(name):
    """Facial clips of an uploaded GTA clip dictionary (the ones that animate face controls)."""
    js = UPLOADS / f"{name}.json"
    if not js.exists():
        tool("gtaface2json", UPLOADS / name, js)
    d = json.loads(js.read_text(encoding="utf-8"))
    facial = {a["hash"] for a in d["anims"] if any(t["track"] in (22, 25) and t["type"] in (0, 2) for t in a["tracks"])}
    return [{"name": c["name"], "duration": round(c["duration"], 2)} for c in d["clips"] if any(p["anim"] in facial for p in c["parts"])]


def uploads():
    out = []
    for f in sorted(UPLOADS.glob("*.ycd")):
        try:
            out.append({"name": f.name, "clips": upload_clips(f.name)})
        except Exception as e:  # noqa: BLE001
            out.append({"name": f.name, "clips": [], "error": str(e)[:300]})
    return out


# ---------------------------------------------------------------------------------------------- library
def item_dir(iid):
    if not ID_RE.match(iid):
        raise ValueError("id: латиница, цифры и _")
    return LIBRARY / iid


def library():
    out = []
    for m in sorted(LIBRARY.glob("*/meta.json")):
        meta = json.loads(m.read_text(encoding="utf-8"))
        out.append(meta)
    out.sort(key=lambda x: x.get("created", 0), reverse=True)
    return out


def unique_id(base):
    iid, n = base, 2
    while (LIBRARY / iid).exists():
        iid = f"{base[:40 - len(str(n)) - 1]}_{n}"        # ids stay within ID_RE's 40 characters
        n += 1
    return iid


def check_spec(spec):
    frames = int(spec["frames"])
    if not 2 <= frames <= 30 * 600:
        raise ValueError("длина: от 2 кадров до 10 минут")
    for c in spec["controls"]:
        if not isinstance(c["id"], int) or not 0 < c["id"] < 65536:
            raise ValueError("bad control id")
        if not 1 <= len(c["values"]) <= 2 or any(len(v) != frames for v in c["values"]):
            raise ValueError("bad control values")


def save_item(name, sex, spec, source, editor=None, iid=None):
    check_spec(spec)
    if sex not in ("m", "f"):
        raise ValueError("sex")
    iid = iid if iid and (LIBRARY / iid).exists() else unique_id(safe(name))
    d = item_dir(iid)
    d.mkdir(parents=True, exist_ok=True)
    (d / "spec.json").write_text(json.dumps(spec), encoding="utf-8")
    if editor is not None:
        (d / "editor.json").write_text(json.dumps(editor), encoding="utf-8")
    meta = {"id": iid, "name": name, "sex": sex, "source": source, "frames": spec["frames"],
            "duration": round(float(spec["duration"]), 3), "created": time.time(), "editable": editor is not None}
    old = d / "meta.json"
    if old.exists():
        meta["created"] = json.loads(old.read_text(encoding="utf-8")).get("created", meta["created"])
    old.write_text(json.dumps(meta, ensure_ascii=False), encoding="utf-8")
    return meta


# ---------------------------------------------------------------------------------------------- conversion job
JOB = {"running": False, "done": 0, "total": 0, "log": [], "title": ""}


def run_convert(upload, clips, sex):
    try:
        data = json.loads((UPLOADS / f"{upload}.json").read_text(encoding="utf-8"))
        conv = converter(sex)
        for clip in clips:
            try:
                spec, rep = conv.convert(data, clip)
                if rep["motion_mm"] < 0.05:
                    raise ValueError("клип не двигает лицо MP-персонажа GTA (у сюжетных героев своя голова)")
                meta = save_item(clip, sex, spec, f"GTA: {upload} / {clip}")
                JOB["log"].append(f"OK {clip}: {rep['frames']} кадров, совпадение с GTA {max(0, round(100 * (1 - rep['error_mm'] / max(rep['motion_mm'], 1e-6))))}% "
                                  f"(движение {rep['motion_mm']} мм, отклонение {rep['error_mm']} мм) -> {meta['id']}")
            except Exception as e:  # noqa: BLE001
                traceback.print_exc()
                JOB["log"].append(f"ОШИБКА {clip}: {e}")
            JOB["done"] += 1
    finally:
        JOB["running"] = False


# ---------------------------------------------------------------------------------------------- export
CLIENT_LUA = r'''-- __RES__: facial animations for RedM (made with the RDR 2 facial animation converter, tools/rdr_face).
-- Exports (client): PlayFace(name[, ped][, loop]) -> true if it plays, StopFace([ped]), GetFaces()
-- Command: /__CMD__ <name> plays, /__CMD__ lists, /__CMD__stop stops.
local RES = GetCurrentResourceName()
local SET_FACIAL_IDLE_ANIM_OVERRIDE = 0xFFC24B988B938B38
local CLEAR_FACIAL_IDLE_ANIM_OVERRIDE = 0x726256CC1EEB182F

local faces = {}
do
    local ok, list = pcall(json.decode, LoadResourceFile(RES, 'faces.json') or '[]')
    for _, f in ipairs(ok and list or {}) do faces[f.name] = f end
end

local playing = {}   -- ped -> token

local function load(dict)
    if not DoesAnimDictExist(dict) then return false end
    RequestAnimDict(dict)
    local t = GetGameTimer()
    while not HasAnimDictLoaded(dict) and GetGameTimer() - t < 5000 do Wait(0) end
    return HasAnimDictLoaded(dict)
end

local function stop(ped)
    ped = ped or PlayerPedId()
    playing[ped] = nil
    Citizen.InvokeNative(CLEAR_FACIAL_IDLE_ANIM_OVERRIDE, ped)
end

local function play(name, ped, loop)
    local f = faces[name]
    if not f then return false end
    ped = ped or PlayerPedId()
    if not load(f.dict) then return false end
    Citizen.InvokeNative(SET_FACIAL_IDLE_ANIM_OVERRIDE, ped, f.clip, f.dict)
    local token = {}
    playing[ped] = token
    if not loop then
        CreateThread(function()
            Wait(math.floor((f.duration or 3) * 1000))
            if playing[ped] == token then stop(ped) end
        end)
    end
    return true
end

exports('PlayFace', play)
exports('StopFace', stop)
exports('GetFaces', function() return faces end)

RegisterCommand('__CMD__', function(_, args)
    if not args[1] then
        local names = {}
        for n in pairs(faces) do names[#names + 1] = n end
        table.sort(names)
        print(('[__RES__] %d: %s'):format(#names, table.concat(names, ', ')))
        return
    end
    CreateThread(function()
        if not play(args[1], nil, args[2] == 'loop') then print(('[__RES__] %s: not found'):format(args[1])) end
    end)
end, false)
RegisterCommand('__CMD__stop', function() stop() end, false)
'''

FXMANIFEST = '''fx_version 'cerulean'
game 'rdr3'
rdr3_warning 'I acknowledge that this is a prerelease build of RedM, and I am aware my resources *will* become incompatible once RedM ships.'

description 'Facial animations (RDR 2 facial animation converter)'

files { 'faces.json' }
client_script 'client.lua'
'''


def export(body):
    s = settings()
    for k in ("resource", "prefix", "command", "folder"):
        if k in body:
            s[k] = str(body[k]).strip()
    if not re.match(r"^[a-zA-Z0-9_\-]{2,40}$", s["resource"]):
        raise ValueError("имя ресурса: латиница, цифры, _ и -")
    if not re.match(r"^[a-z0-9_@]{1,20}$", s["prefix"]):
        raise ValueError("префикс словарей: латиница, цифры, _ и @")
    if not re.match(r"^[a-zA-Z0-9_]{2,20}$", s["command"]):
        raise ValueError("команда: латиница и цифры")
    SETTINGS_FILE.write_text(json.dumps(s, ensure_ascii=False, indent=1), encoding="utf-8")
    ids = body.get("ids") or [m["id"] for m in library()]
    if not ids:
        raise ValueError("библиотека пуста")
    out = EXPORTS / s["resource"]
    if out.exists():
        shutil.rmtree(out)
    (out / "stream").mkdir(parents=True)
    faces = []
    for iid in ids:
        d = item_dir(iid)
        meta = json.loads((d / "meta.json").read_text(encoding="utf-8"))
        dict_name = s["prefix"] + iid
        tool("facebuild", d / "spec.json", out / "stream" / f"{dict_name}.ycd", dict_name, iid)
        faces.append({"name": iid, "label": meta["name"], "dict": dict_name, "clip": iid, "duration": meta["duration"], "sex": meta["sex"]})
    (out / "faces.json").write_text(json.dumps(faces, ensure_ascii=False, indent=1), encoding="utf-8")
    (out / "fxmanifest.lua").write_text(FXMANIFEST, encoding="utf-8")
    (out / "client.lua").write_text(CLIENT_LUA.replace("__RES__", s["resource"]).replace("__CMD__", s["command"]), encoding="utf-8")
    (out / "README.md").write_text(
        f"# {s['resource']}\n\nFacial animations for RedM. `/{s['command']} <name>` plays one on your character, `/{s['command']}` lists them, "
        f"`/{s['command']}stop` stops.\n\nClient exports: `PlayFace(name[, ped][, loop])`, `StopFace([ped])`, `GetFaces()`.\n"
        f"Played with SET_FACIAL_IDLE_ANIM_OVERRIDE (the face only: body animations keep playing).\n", encoding="utf-8")
    zpath = EXPORTS / f"{s['resource']}.zip"
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
        for f in out.rglob("*"):
            if f.is_file():
                z.write(f, f"{s['resource']}/{f.relative_to(out).as_posix()}")
    copied = None
    if s["folder"]:
        dst = Path(s["folder"]) / s["resource"]
        if dst.exists():
            shutil.rmtree(dst)
        shutil.copytree(out, dst)
        copied = str(dst)
    return {"zip": f"/api/download?f={zpath.name}", "count": len(faces), "copied": copied}


# ---------------------------------------------------------------------------------------------- http
class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def send(self, code, body, ctype="application/json; charset=utf-8", extra=None):
        if isinstance(body, (dict, list)):
            body = json.dumps(body, ensure_ascii=False).encode()
        elif isinstance(body, str):
            body = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        try:
            self.end_headers()
            self.wfile.write(body)
        except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
            self.close_connection = True

    def fail(self, e, code=500):
        if code == 500:
            traceback.print_exc()
        msg = str(e).strip().splitlines()[-1] if str(e).strip() else repr(e)
        self.send(code, {"error": msg[:600]})

    def file(self, path, base):
        path = path.resolve()
        if base.resolve() not in path.parents or not path.is_file():
            return self.send(404, {"error": "not found"})
        self.send(200, path.read_bytes(), mimetypes.guess_type(path.name)[0] or "application/octet-stream")

    def authorized(self):
        """--password: HTTP basic auth (any user name), the browser asks once."""
        if not PASSWORD:
            return True
        h = self.headers.get("Authorization", "")
        if h.startswith("Basic "):
            try:
                pw = base64.b64decode(h[6:]).decode("utf-8").split(":", 1)[1]
                if hmac.compare_digest(pw.encode(), PASSWORD.encode()):
                    return True
            except (ValueError, IndexError):
                pass
        if self.command == "POST":
            self.rfile.read(int(self.headers.get("Content-Length") or 0))
        self.send(401, {"error": "password"}, extra={"WWW-Authenticate": 'Basic realm="RDR 2 Face", charset="UTF-8"'})
        return False

    def do_GET(self):
        if not self.authorized():
            return
        u = urllib.parse.urlparse(self.path)
        q = {k: v[0] for k, v in urllib.parse.parse_qs(u.query).items()}
        try:
            if u.path in ("/", "/index.html"):
                return self.file(STATIC / "index.html", STATIC)
            if u.path.startswith("/static/"):
                return self.file(STATIC / u.path[8:], STATIC)
            if u.path.startswith("/assets/"):
                return self.file(ASSETS / u.path[8:], ASSETS)
            if u.path == "/api/state":
                return self.send(200, {"settings": settings(), "library": library(), "uploads": uploads(),
                                       "job": {k: JOB[k] for k in ("running", "done", "total", "title")}})
            if u.path == "/api/job":
                since = int(q.get("since", 0))
                return self.send(200, {**{k: JOB[k] for k in ("running", "done", "total", "title")}, "log": JOB["log"][since:], "next": len(JOB["log"])})
            if u.path == "/api/item":
                d = item_dir(q.get("id", ""))
                out = {"meta": json.loads((d / "meta.json").read_text(encoding="utf-8")),
                       "spec": json.loads((d / "spec.json").read_text(encoding="utf-8"))}
                if (d / "editor.json").exists():
                    out["editor"] = json.loads((d / "editor.json").read_text(encoding="utf-8"))
                return self.send(200, out)
            if u.path == "/api/download":
                return self.file(EXPORTS / Path(q.get("f", "")).name, EXPORTS)
            self.send(404, {"error": "not found"})
        except ValueError as e:
            self.fail(e, 400)
        except Exception as e:  # noqa: BLE001
            self.fail(e)

    def do_POST(self):
        if not self.authorized():
            return
        u = urllib.parse.urlparse(self.path)
        q = {k: v[0] for k, v in urllib.parse.parse_qs(u.query).items()}
        try:
            n = int(self.headers.get("Content-Length") or 0)
            if u.path == "/api/upload":
                name = Path(q.get("name", "")).name
                if not name.lower().endswith(".ycd"):
                    raise ValueError("нужен .ycd файл GTA V (словарь анимаций)")
                if n > 200 * 1024 * 1024:
                    raise ValueError("файл больше 200 МБ")
                name = safe(name[:-4]) + ".ycd"
                UPLOADS.mkdir(parents=True, exist_ok=True)
                (UPLOADS / name).write_bytes(self.rfile.read(n))
                (UPLOADS / f"{name}.json").unlink(missing_ok=True)
                try:
                    clips = upload_clips(name)
                except Exception as e:
                    (UPLOADS / name).unlink(missing_ok=True)
                    raise ValueError(f"не читается как словарь анимаций GTA V: {e}")
                return self.send(200, {"name": name, "clips": clips})
            body = json.loads(self.rfile.read(n) or b"{}")
            if u.path == "/api/delete_upload":
                name = Path(body.get("name", "")).name
                (UPLOADS / name).unlink(missing_ok=True)
                (UPLOADS / f"{name}.json").unlink(missing_ok=True)
                return self.send(200, {"ok": True})
            if u.path == "/api/convert":
                if JOB["running"]:
                    raise ValueError("перенос уже идёт")
                upload = Path(body["upload"]).name
                clips = [c for c in body.get("clips", []) if isinstance(c, str)]
                if not clips:
                    raise ValueError("выберите клипы")
                JOB.update(running=True, done=0, total=len(clips), log=[], title=upload)
                threading.Thread(target=run_convert, args=(upload, clips, body.get("sex", "m")), daemon=True).start()
                return self.send(200, {"ok": True})
            if u.path == "/api/save":
                meta = save_item(body["name"].strip() or "face", body["sex"], body["spec"], body.get("source") or "редактор",
                                 body.get("editor"), body.get("id"))
                return self.send(200, meta)
            if u.path == "/api/delete":
                shutil.rmtree(item_dir(body.get("id", "")), ignore_errors=True)
                return self.send(200, {"ok": True})
            if u.path == "/api/export":
                return self.send(200, export(body))
            if u.path == "/api/settings":
                s = settings()
                for k in DEFAULTS:
                    if k in body:
                        s[k] = str(body[k]).strip()
                DATA.mkdir(parents=True, exist_ok=True)
                SETTINGS_FILE.write_text(json.dumps(s, ensure_ascii=False, indent=1), encoding="utf-8")
                return self.send(200, s)
            self.send(404, {"error": "not found"})
        except (ValueError, KeyError) as e:
            self.fail(e, 400)
        except Exception as e:  # noqa: BLE001
            self.fail(e)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8776)
    ap.add_argument("--host", default="127.0.0.1", help="0.0.0.0 to open it to the network (needs --password)")
    ap.add_argument("--password", default=os.environ.get("RDR_FACE_PASSWORD", ""), help="or env RDR_FACE_PASSWORD")
    ap.add_argument("--data", help="uploads, library and exports (default ./data)")
    a = ap.parse_args()
    global PASSWORD
    PASSWORD = a.password
    if a.data:
        set_data(a.data)
    if not (ASSETS / "rdr_m.json").exists():
        print("assets/ is missing: run python make_data.py first")
        return 1
    if a.host not in ("127.0.0.1", "localhost", "::1") and not PASSWORD:
        print("--host other than 127.0.0.1 needs --password (or RDR_FACE_PASSWORD)")
        return 1
    for d in (UPLOADS, LIBRARY, EXPORTS):
        d.mkdir(parents=True, exist_ok=True)
    srv = ThreadingHTTPServer((a.host, a.port), Handler)
    print(f"Facial animation converter: http://{'localhost' if a.host == '127.0.0.1' else a.host}:{a.port}"
          + (" (password)" if PASSWORD else ""))
    srv.serve_forever()


if __name__ == "__main__":
    sys.exit(main())
