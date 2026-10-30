# RDR 2 Face

Facial animations for RedM (Red Dead Redemption 2):

- **Convert** GTA V facial animation clips to RDR 2.
- **Edit** your own facial animations on the RDR 2 head: sliders per face control, keyframes, live 3D preview,
  the game's own moods as presets.
- **Export** everything as one RedM resource: `/face <name>` in game, and exports `PlayFace`, `StopFace`, `GetFaces`
  for your scripts.

Web app (Python server + browser UI), English and Russian interface.

> **Game data is not included.** The converter needs data made from the games' files (rigs, the head mesh, face
> expressions, a template clip dictionary). Those are Rockstar Games assets and cannot be published: every user
> builds them from their own copies of RDR 2 and GTA V (see [Building the game data](#building-the-game-data)).

> **Rockstar's Mod Guidelines** forbid combining assets from one Rockstar title with another. Converting GTA V
> animations for RDR 2 may break them, and RedM servers are bound to those guidelines through the Cfx.re Creator
> Platform License. You are responsible for what you make and run with this tool.

## How it works

Both games animate faces through *controls* (GTA V about 20, RDR 2 about 120) that a face expression turns into bone
motion. A GTA clip is played through the GTA MP head's expression; the motion of matching points of the face (lip
corners, lips, jaw, eyelids, brows, cheeks, tongue) is measured, scaled to the RDR head, and the RDR controls that
reproduce it are solved frame by frame (bounded least squares in a space of the game's own face shapes). The result
is written as an RDR 2 clip dictionary and played in game with `SET_FACIAL_IDLE_ANIM_OVERRIDE`.

## Layout

| Path | What |
| --- | --- |
| `server.py`, `static/` | the web app (port 8776) |
| `face_vm.py` | an interpreter of the games' face expressions (GTA V and RDR 2) |
| `face_convert.py` | GTA clip -> RDR face controls |
| `src/RdrFaceTool`, `src/RdrAnimCore` | the .NET tool that writes RDR 2 clip dictionaries (no CodeX, no game needed at run time) |
| `tools/make_data.py` | builds `assets/` from the games (once) |
| `deploy/` | systemd service and Caddy (HTTPS) for a Linux server |
| `assets/`, `bin/`, `data/` | not in git: game data, built tool, your uploads and library |

## Requirements

- Python 3.10+ with `numpy` and `scipy` (`start.bat` / `start.sh` install them).
- `assets/` built from your games (below).
- `bin/win-x64/RdrFaceTool.exe` or `bin/linux-x64/RdrFaceTool`, built from `src/` (below).

## Build the tool

Needs the .NET 8 SDK and the template files in `src/RdrAnimCore/Data/` (see the README there).

```
cd src\RdrFaceTool
publish.bat
```

Copy `src/RdrFaceTool/publish/win-x64/RdrFaceTool.exe` to `bin/win-x64/` and, for a Linux server,
`publish/linux-x64/RdrFaceTool` to `bin/linux-x64/`. Both are self-contained: the machine running the app needs no .NET.

## Building the game data

`tools/make_data.py` writes `assets/` (rigs, head meshes and textures, controls with labels, presets, the face shape
basis) once, on Windows, from RDR 2 and GTA V. It is **not self-contained**: it reads the games through an external
toolchain (an RDR 2 viewer with extracted game assets, RdrYcdTool with CodeX 0.28, GtaVanillaScan), located by the
environment variables listed at the top of the script. Without that toolchain the data cannot be built.

```
python tools/make_data.py
```

## Run

### Windows

```
start.bat
```

Then open http://localhost:8776.

### Linux server

```
sudo apt install python3 python3-venv
./start.sh --host 0.0.0.0 --port 8776 --password <password>
```

`start.sh` creates `venv/` with numpy and scipy on the first run. With `--host` the browser asks for the password
(any user name); without it the app listens on 127.0.0.1 only.

As a service behind HTTPS (Caddy): copy the folder to `/opt/rdr2-face`, set the password in
`deploy/rdr2-face.service` and your domain in `deploy/Caddyfile`, then

```
sudo useradd -r -s /usr/sbin/nologin rdrface
sudo chown -R rdrface /opt/rdr2-face
sudo -u rdrface /opt/rdr2-face/start.sh --setup-only
sudo cp /opt/rdr2-face/deploy/rdr2-face.service /etc/systemd/system/
sudo systemctl enable --now rdr2-face
```

and put `deploy/Caddyfile`'s block into `/etc/caddy/Caddyfile` (`sudo systemctl reload caddy`).

Options: `--port`, `--host`, `--password` (or env `RDR_FACE_PASSWORD`), `--data DIR` (uploads, library, exports;
default `data/`).

## Use

1. **Convert:** upload GTA V facial clip dictionaries (`.ycd`, e.g. `facials@gen_male@base`). Each clip is converted
   and added to the library; clips with no face motion are skipped.
2. **Edit:** open a clip or start a new one. Move the sliders (grouped by mouth, lips, eyes, brows, cheeks...) or
   apply a preset, set keyframes on the timeline, watch the head.
3. **Export:** pick clips from the library and export one RedM resource (a zip, or straight into a resources folder
   on the machine the app runs on). Put it on your server, `ensure` it, and in game:

```
/face <name>      play a face animation on yourself
/face             stop it
```

From your own scripts: `exports['<resource>']:PlayFace(ped, name)`, `StopFace(ped)`, `GetFaces()`.

## License

The code of this repository: MIT (see `LICENSE`). Bundled: three.js (MIT), Inter font (SIL OFL 1.1).
Game data built with `tools/make_data.py` belongs to Rockstar Games and is not covered by this license.
