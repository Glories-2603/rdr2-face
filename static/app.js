// RDR 2 Face: GTA V facial animations -> RDR 2 face controls, an editor for face animations on the RDR MP head,
// a library and the RedM resource export. The head is drawn with the game's own face expression (bcustom, run by
// expr.js) and skinned on the CPU, so what is shown is what the game does with the controls.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { runExpression } from './expr.js';
import { startI18n, setLang, lang, t } from './i18n.js';

const $ = (id) => document.getElementById(id);
const FPS = 30;

let mode = 'convert', sex = 'm';
const rigs = {};                         // sex -> {bones, prog, controls, byId, presets, mesh data, texture}
let rig = null;
let state = null;

// ------------------------------------------------------------------------------------------------ helpers
async function api(path, body, raw) {
  const opt = body === undefined ? undefined : { method: 'POST', headers: { 'Content-Type': raw ? 'application/octet-stream' : 'application/json' }, body: raw ? body : JSON.stringify(body) };
  const r = await fetch(path, opt);
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(j.error || r.statusText);
  return j;
}
let toastTimer;
function toast(msg, ms = 3500) {
  const t = $('toast'); t.textContent = msg; t.hidden = false;
  clearTimeout(toastTimer); toastTimer = setTimeout(() => { t.hidden = true; }, ms);
}
function b64(s, T) { const b = Uint8Array.from(atob(s), (c) => c.charCodeAt(0)); return new T(b.buffer); }
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

// ------------------------------------------------------------------------------------------------ math / pose
function qmul(a, b) {
  return [a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1], a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
    a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3], a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]];
}
const qconj = (q) => [-q[0], -q[1], -q[2], q[3]];
function qrot(q, v) {
  const [x, y, z, w] = q;
  const ix = w * v[0] + y * v[2] - z * v[1], iy = w * v[1] + z * v[0] - x * v[2], iz = w * v[2] + x * v[1] - y * v[0], iw = -x * v[0] - y * v[1] - z * v[2];
  return [ix * w + iw * -x + iy * -z - iz * -y, iy * w + iw * -y + iz * -x - ix * -z, iz * w + iw * -z + ix * -y - iy * -x];
}

function makePose(r) {
  const local = new Map();
  for (const [tag, b] of r.bones) local.set(tag, { t: b.t.slice(), r: b.r.slice(), s: b.s.slice() });
  const cache = new Map();
  const world = (tag) => {
    let w = cache.get(tag);
    if (w) return w;
    const L = local.get(tag), b = r.bones.get(tag);
    if (!L) w = { p: [0, 0, 0], q: [0, 0, 0, 1] };
    else if (b.parent < 0 || !r.bones.has(b.parent)) w = { p: L.t.slice(), q: L.r.slice() };
    else {
      const P = world(b.parent), o = qrot(P.q, L.t);
      w = { p: [P.p[0] + o[0], P.p[1] + o[1], P.p[2] + o[2]], q: qmul(P.q, L.r) };
    }
    cache.set(tag, w);
    return w;
  };
  return {
    controls: new Map(), world,
    get(tag, track) { const L = local.get(tag); if (!L) return null; return track === 1 ? L.r : track === 0 ? L.t : L.s; },
    bind(tag, track) { const b = r.bones.get(tag); if (!b) return null; return track === 1 ? b.r : track === 0 ? b.t : b.s; },
    set(tag, track, v) {
      const L = local.get(tag); if (!L) return;
      cache.clear();
      if (track === 1) L.r = v.slice(0, 4); else if (track === 0) L.t = v.slice(0, 3); else if (track === 2) L.s = v.slice(0, 3);
    },
    getObject(tag, track) { const w = world(tag); return track === 1 ? w.q : w.p; },
    setObject() {},
  };
}

// ------------------------------------------------------------------------------------------------ 3D head
const stage = $('stage');
const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
renderer.outputColorSpace = THREE.SRGBColorSpace;
stage.appendChild(renderer.domElement);
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x0b0b0b);
const camera = new THREE.PerspectiveCamera(24, 1, 0.01, 20);
camera.up.set(0, 0, 1);
const orbit = new OrbitControls(camera, renderer.domElement);
orbit.enableDamping = true;
scene.add(new THREE.HemisphereLight(0xffffff, 0x3a3030, 1.4));
const key = new THREE.DirectionalLight(0xffffff, 1.7); key.position.set(-0.6, -1.5, 1.2); scene.add(key);
const fill = new THREE.DirectionalLight(0xffffff, 0.5); fill.position.set(1, -0.5, 0.3); scene.add(fill);
let headObj = null, eyes = [];

function resize() {
  const w = stage.clientWidth, h = stage.clientHeight;
  renderer.setSize(w, h); camera.aspect = w / h; camera.updateProjectionMatrix();
}
addEventListener('resize', resize);

async function loadRig(s) {
  if (rigs[s]) return rigs[s];
  const [d, m, presets] = await Promise.all([fetch(`/assets/rdr_${s}.json`).then((r) => r.json()), fetch(`/assets/head_${s}.json`).then((r) => r.json()),
    fetch(`/assets/presets_${s}.json`).then((r) => r.json())]);
  const bones = new Map(Object.entries(d.bones).map(([k, v]) => [Number(k), v]));
  const r = { bones, prog: d.program, controls: d.controls, presets, byName: new Map() };
  for (const [t, b] of bones) r.byName.set(b.name, t);
  r.byId = new Map(d.controls.map((c) => [c.id, c]));
  r.mesh = { pos: b64(m.pos, Float32Array), nrm: b64(m.nrm, Float32Array), uv: b64(m.uv, Float32Array), idx: b64(m.idx, Uint32Array),
    tags: b64(m.tags, Uint16Array), w: b64(m.weights, Float32Array), n: m.verts };
  // bind pose (no expression): what the mesh is skinned from
  const bp = makePose(r);
  r.skinTags = [...new Set(r.mesh.tags)].filter((t) => bones.has(t));
  r.bindWorld = new Map(r.skinTags.map((t) => [t, bp.world(t)]));
  r.texture = await new THREE.TextureLoader().loadAsync(`/assets/head_${s}.png`);
  r.texture.flipY = false; r.texture.colorSpace = THREE.SRGBColorSpace;
  rigs[s] = r;
  return r;
}

async function showHead(s) {
  rig = await loadRig(s);
  if (headObj) { scene.remove(headObj); headObj.geometry.dispose(); }
  for (const e of eyes) scene.remove(e);
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(rig.mesh.pos.slice(), 3));
  g.setAttribute('normal', new THREE.BufferAttribute(rig.mesh.nrm.slice(), 3));
  g.setAttribute('uv', new THREE.BufferAttribute(rig.mesh.uv, 2));
  g.setIndex(new THREE.BufferAttribute(rig.mesh.idx, 1));
  headObj = new THREE.Mesh(g, new THREE.MeshStandardMaterial({ map: rig.texture, roughness: 0.7 }));
  scene.add(headObj);
  eyes = ['FACIAL_L_Eyeball', 'FACIAL_R_Eyeball'].map((n) => {
    const m = new THREE.Mesh(new THREE.SphereGeometry(0.0118, 24, 16), new THREE.MeshStandardMaterial({ color: 0xe8e2da, roughness: 0.25 }));
    const iris = new THREE.Mesh(new THREE.CircleGeometry(0.0052, 24), new THREE.MeshStandardMaterial({ color: 0x3b2a1e, roughness: 0.3 }));
    iris.position.set(0, -0.0117, 0); iris.rotation.x = Math.PI / 2;
    m.add(iris);
    m.userData.tag = rig.byName.get(n);
    scene.add(m);
    return m;
  });
  const head = makePose(rig).world(rig.byName.get('SKEL_Head')).p;
  orbit.target.set(head[0], head[1] - 0.05, head[2] + 0.04);
  camera.position.set(head[0] + 0.16, head[1] - 0.95, head[2] + 0.12);
  orbit.update();
  dirty = true;
}

// controls (Map id -> number | [x, y]) -> deformed head
function applyFace(values) {
  if (!rig || !headObj) return;
  const pose = makePose(rig);
  pose.controls = new Map();
  for (const [id, v] of values) pose.controls.set(id, Array.isArray(v) ? [v[0], v[1], 0, 0] : v);
  try { runExpression(rig.prog, pose, new Map()); } catch (e) { console.warn(e); }
  const M = new Map();
  for (const t of rig.skinTags) {
    const b = rig.bindWorld.get(t), c = pose.world(t);
    const q = qmul(c.q, qconj(b.q));
    const bp = qrot(q, b.p);
    M.set(t, { q, o: [c.p[0] - bp[0], c.p[1] - bp[1], c.p[2] - bp[2]] });
  }
  const P = rig.mesh.pos, T = rig.mesh.tags, W = rig.mesh.w, out = headObj.geometry.attributes.position.array;
  for (let i = 0; i < rig.mesh.n; i++) {
    let x = 0, y = 0, z = 0, ws = 0;
    const v = [P[3 * i], P[3 * i + 1], P[3 * i + 2]];
    for (let k = 0; k < 4; k++) {
      const w = W[4 * i + k];
      if (!w) continue;
      const m = M.get(T[4 * i + k]);
      if (!m) { x += w * v[0]; y += w * v[1]; z += w * v[2]; ws += w; continue; }
      const r = qrot(m.q, v);
      x += w * (r[0] + m.o[0]); y += w * (r[1] + m.o[1]); z += w * (r[2] + m.o[2]); ws += w;
    }
    if (ws < 1e-6) { x = v[0]; y = v[1]; z = v[2]; ws = 1; }
    out[3 * i] = x / ws; out[3 * i + 1] = y / ws; out[3 * i + 2] = z / ws;
  }
  headObj.geometry.attributes.position.needsUpdate = true;
  headObj.geometry.computeVertexNormals();
  for (const e of eyes) {
    const w = pose.world(e.userData.tag);
    e.position.set(w.p[0], w.p[1], w.p[2]);
  }
}

// ------------------------------------------------------------------------------------------------ animation model
// editor: keys [{t, v: Map(id -> number | [x, y])}], sorted by t; a playing library item: frames of controls
let keys = [], duration = 3, cursor = 0, playing = false, playStart = 0, dirty = true, editingId = null;
let preview = null;          // {frames: [Map], duration} of a library item shown in convert mode

const ease = (t) => (($('interp').value === 'linear') ? t : t * t * (3 - 2 * t));
function lerpV(a, b, t) {
  if (Array.isArray(a) || Array.isArray(b)) {
    const A = Array.isArray(a) ? a : [a || 0, 0], B = Array.isArray(b) ? b : [b || 0, 0];
    return [A[0] + (B[0] - A[0]) * t, A[1] + (B[1] - A[1]) * t];
  }
  return (a || 0) + ((b || 0) - (a || 0)) * t;
}
function valuesAt(t) {
  if (!keys.length) return new Map();
  if (t <= keys[0].t) return keys[0].v;
  if (t >= keys[keys.length - 1].t) return keys[keys.length - 1].v;
  let i = 0;
  while (keys[i + 1].t < t) i++;
  const a = keys[i], b = keys[i + 1], k = ease((t - a.t) / Math.max(1e-6, b.t - a.t));
  const out = new Map();
  for (const id of new Set([...a.v.keys(), ...b.v.keys()])) out.set(id, lerpV(a.v.get(id), b.v.get(id), k));
  return out;
}
const keyAt = (t) => keys.find((k) => Math.abs(k.t - t) < 0.5 / FPS);
function ensureKey(t) {
  let k = keyAt(t);
  if (!k) {
    k = { t: Math.round(t * FPS) / FPS, v: new Map(valuesAt(t)) };
    keys.push(k); keys.sort((a, b) => a.t - b.t);
    renderKeys();
  }
  return k;
}

function sampleSpec() {
  const frames = Math.max(2, Math.round(duration * FPS) + 1);
  const used = new Map();
  for (const k of keys) for (const [id, v] of k.v) if (Array.isArray(v) ? (v[0] || v[1]) : v) used.set(id, Array.isArray(v) ? 2 : (used.get(id) || 1));
  const tracks = new Map([...used].map(([id, n]) => [id, Array.from({ length: n }, () => new Array(frames).fill(0))]));
  for (let f = 0; f < frames; f++) {
    const vals = valuesAt(f / FPS);
    for (const [id, arrs] of tracks) {
      const v = vals.get(id);
      if (arrs.length === 2) { const a = Array.isArray(v) ? v : [v || 0, 0]; arrs[0][f] = +a[0].toFixed(4); arrs[1][f] = +a[1].toFixed(4); }
      else arrs[0][f] = +(Array.isArray(v) ? v[0] : (v || 0)).toFixed(4);
    }
  }
  return { frames, duration: (frames - 1) / FPS, controls: [...tracks].map(([id, values]) => ({ id, values })) };
}
function specFrames(spec) {
  const frames = [];
  for (let f = 0; f < spec.frames; f++) {
    const m = new Map();
    for (const c of spec.controls) m.set(c.id, c.values.length === 2 ? [c.values[0][f], c.values[1][f]] : c.values[0][f]);
    frames.push(m);
  }
  return frames;
}

// ------------------------------------------------------------------------------------------------ editor UI
const openGroups = new Set(['Рот', 'Губы']);
function renderControls() {
  const box = $('controls');
  box.innerHTML = '';
  if (!rig) return;
  const filter = $('search').value.trim().toLowerCase();
  const vals = keyAt(cursor)?.v || valuesAt(cursor);
  const groups = new Map();
  for (const c of rig.controls) {
    if (c.skip) continue;                       // unused by the game's faces, or eye gaze (the game aims the eyes)
    if (filter && !(`${c.label} ${c.group} ${t(c.label)} ${t(c.group)} ${c.bones.join(' ')} ${c.id}`.toLowerCase().includes(filter))) continue;
    if (!groups.has(c.group)) groups.set(c.group, []);
    groups.get(c.group).push(c);
  }
  for (const [g, list] of [...groups].sort((a, b) => t(a[0]).localeCompare(t(b[0]), lang))) {
    const div = document.createElement('div'); div.className = 'group';
    const open = filter || openGroups.has(g);
    const head = document.createElement('button');
    const active = list.filter((c) => { const v = vals.get(c.id); return Array.isArray(v) ? v[0] || v[1] : v; }).length;
    head.innerHTML = `<span>${open ? '▾' : '▸'} ${esc(g)}</span><small>${active ? `${active} / ` : ''}${list.length}</small>`;
    head.onclick = () => { if (openGroups.has(g)) openGroups.delete(g); else openGroups.add(g); renderControls(); };
    div.append(head);
    if (open) {
      const seen = new Map();
      for (const c of list) {
        const n = (seen.get(c.label) || 0) + 1; seen.set(c.label, n);
        for (let comp = 0; comp < c.n; comp++) {
          const v = vals.get(c.id);
          const cur = Array.isArray(v) ? v[comp] : (comp ? 0 : v || 0);
          const row = document.createElement('div'); row.className = 'ctrl' + (cur ? ' on' : '');
          row.title = `${c.id}: ${c.bones.join(', ')}`;
          const name = document.createElement('span');
          name.textContent = `${c.label}${n > 1 ? ` ${n}` : ''}${c.n === 2 ? (comp ? ' ↕' : ' ↔') : ''}`;
          const r = document.createElement('input');
          r.type = 'range'; r.min = c.lo[comp]; r.max = c.hi[comp] || 1; r.step = 0.01; r.value = cur;
          const o = document.createElement('output'); o.value = (+cur).toFixed(2);
          r.oninput = () => {
            const k = ensureKey(cursor);
            const x = parseFloat(r.value);
            if (c.n === 2) { const a = Array.isArray(k.v.get(c.id)) ? k.v.get(c.id).slice() : [0, 0]; a[comp] = x; k.v.set(c.id, a); }
            else if (x) k.v.set(c.id, x); else k.v.delete(c.id);
            o.value = x.toFixed(2); row.classList.toggle('on', !!x);
            dirty = true; renderKeys();
          };
          row.append(name, r, o);
          div.append(row);
        }
      }
    }
    box.append(div);
  }
  const k = keyAt(cursor);
  $('keyInfo').textContent = k ? `ключ ${k.t.toFixed(2)} с` : 'между ключами';
}

function renderKeys() {
  const box = $('keys');
  box.innerHTML = '';
  for (const k of keys) {
    const d = document.createElement('div');
    d.className = 'k' + (Math.abs(k.t - cursor) < 0.5 / FPS ? ' sel' : '');
    d.style.left = `${(k.t / duration) * 100}%`;
    box.append(d);
  }
  $('cursor').style.left = `${(cursor / duration) * 100}%`;
  $('timeOut').value = `${cursor.toFixed(2)} с`;
}

function setCursor(t, snap = true) {
  cursor = Math.min(duration, Math.max(0, t));
  if (snap) {
    const k = keys.find((x) => Math.abs(x.t - cursor) < 0.12 * duration / 10);
    if (k) cursor = k.t;
  }
  dirty = true; renderKeys(); renderControls();
}

$('track').addEventListener('pointerdown', (e) => {
  const r = $('track').getBoundingClientRect();
  const move = (ev) => { playing = false; setCursor(((ev.clientX - r.left) / r.width) * duration, true); };
  move(e);
  const up = () => { removeEventListener('pointermove', move); removeEventListener('pointerup', up); };
  addEventListener('pointermove', move); addEventListener('pointerup', up);
});
$('playBtn').onclick = () => { playing = !playing; playStart = performance.now() - cursor * 1000; };
$('addKey').onclick = () => { ensureKey(cursor); renderControls(); };
$('delKey').onclick = () => { const k = keyAt(cursor); if (k) { keys = keys.filter((x) => x !== k); dirty = true; renderKeys(); renderControls(); } };
$('duration').addEventListener('change', () => {
  duration = Math.max(0.2, Math.min(600, parseFloat($('duration').value) || 3));
  $('duration').value = duration;
  keys = keys.filter((k) => k.t <= duration + 1e-6);
  setCursor(Math.min(cursor, duration), false);
});
$('interp').addEventListener('change', () => { dirty = true; });
$('search').addEventListener('input', renderControls);
$('applyPreset').onclick = () => {
  const p = rig.presets[parseInt($('preset').value, 10)];
  if (!p) return toast('Выберите позу');
  const k = ensureKey(cursor);
  k.v = new Map(Object.entries(p.values).map(([id, v]) => [Number(id), v.length === 2 ? v : v[0]]));
  dirty = true; renderKeys(); renderControls();
};
$('zeroKey').onclick = () => { const k = ensureKey(cursor); k.v = new Map(); dirty = true; renderControls(); };

function newAnimation() {
  editingId = null;
  duration = 3; $('duration').value = 3;
  keys = [{ t: 0, v: new Map() }, { t: 1.5, v: new Map() }, { t: 3, v: new Map() }];
  $('itemName').value = '';
  setCursor(1.5, false);
}
$('newBtn').onclick = newAnimation;

$('saveBtn').onclick = async () => {
  const name = $('itemName').value.trim();
  if (!/^[a-zA-Z0-9_]{1,40}$/.test(name)) return toast('Название: латиница, цифры и _');
  if (!keys.some((k) => k.v.size)) return toast('Анимация пустая: подвигайте ползунки');
  try {
    const editor = { interp: $('interp').value, duration, keys: keys.map((k) => ({ t: k.t, v: Object.fromEntries(k.v) })) };
    const meta = await api('/api/save', { id: editingId, name, sex, spec: sampleSpec(), editor, source: 'редактор' });
    editingId = meta.id;
    toast(`Сохранено: ${meta.id}`);
    await refresh();
  } catch (e) { toast(e.message, 6000); }
};

// ------------------------------------------------------------------------------------------------ library
function renderLibrary() {
  const box = $('library');
  box.innerHTML = '';
  const list = state.library;
  $('libCount').textContent = list.length || '';
  for (const it of list) {
    const row = document.createElement('div');
    row.className = 'item' + ((preview && preview.id === it.id) || editingId === it.id ? ' active' : '');
    row.innerHTML = `<div><strong>${esc(it.name)}</strong><small>${it.sex === 'f' ? '♀' : '♂'} ${it.duration.toFixed(1)} с · ${esc(it.source)}</small></div>`;
    const b = document.createElement('div'); b.className = 'b';
    const mk = (txt, title, fn) => { const x = document.createElement('button'); x.textContent = txt; x.title = title; x.onclick = fn; return x; };
    b.append(
      mk('▶', 'Посмотреть', () => playItem(it)),
      mk('✎', 'Открыть в редакторе', () => editItem(it)),
      mk('✕', 'Удалить', async () => { if (confirm(t(`Удалить «${it.name}»?`))) { await api('/api/delete', { id: it.id }); await refresh(); } }),
    );
    row.append(b);
    box.append(row);
  }
}
async function setSex(s) {
  sex = s;
  document.querySelector(`input[name=sex][value=${s}]`).checked = true;
  await showHead(s);
  const sel = $('preset');
  sel.innerHTML = '<option value="">— выбрать —</option>' + rig.presets.map((p, i) => `<option value="${i}">${esc(p.name)} (${esc(p.dict.replace('face_human@', ''))})</option>`).join('');
  renderControls();
}
async function playItem(it) {
  const d = await api(`/api/item?id=${encodeURIComponent(it.id)}`);
  if (d.meta.sex !== sex) await setSex(d.meta.sex);
  preview = { id: it.id, frames: specFrames(d.spec), duration: d.spec.duration };
  playStart = performance.now();
  if (mode === 'edit') setMode('convert');
  renderLibrary();
}
async function editItem(it) {
  const d = await api(`/api/item?id=${encodeURIComponent(it.id)}`);
  if (d.meta.sex !== sex) await setSex(d.meta.sex);
  preview = null;
  if (d.editor) {
    $('interp').value = d.editor.interp || 'smooth';
    duration = d.editor.duration;
    keys = d.editor.keys.map((k) => ({ t: k.t, v: new Map(Object.entries(k.v).map(([id, v]) => [Number(id), v])) }));
    editingId = it.id;
  } else {                                           // a conversion: every 3rd frame becomes a key
    $('interp').value = 'linear';
    const frames = specFrames(d.spec);
    duration = d.spec.duration;
    keys = [];
    for (let f = 0; f < frames.length; f += 3) keys.push({ t: f / FPS, v: frames[f] });
    if ((frames.length - 1) % 3) keys.push({ t: (frames.length - 1) / FPS, v: frames[frames.length - 1] });
    editingId = null;
  }
  $('duration').value = +duration.toFixed(2);
  $('itemName').value = d.meta.name.replace(/[^a-zA-Z0-9_]/g, '_');
  setMode('edit');
  setCursor(0, false);
  renderLibrary();
}

// ------------------------------------------------------------------------------------------------ GTA conversion UI
const selected = new Map();   // upload -> Set(clip)
function renderUploads() {
  const box = $('uploads');
  box.innerHTML = '';
  for (const u of state.uploads) {
    const div = document.createElement('div'); div.className = 'upload-block';
    const head = document.createElement('div'); head.className = 'upload-head';
    head.innerHTML = `<span class="n" title="${esc(u.name)}">${esc(u.name)}</span><small>${u.clips.length} лиц.</small>`;
    const del = document.createElement('button'); del.textContent = '✕'; del.title = 'Убрать файл';
    del.onclick = async () => { await api('/api/delete_upload', { name: u.name }); selected.delete(u.name); await refresh(); };
    head.append(del); div.append(head);
    if (u.error) { const e = document.createElement('div'); e.className = 'hint'; e.textContent = u.error; div.append(e); }
    if (!u.clips.length && !u.error) { const e = document.createElement('div'); e.className = 'hint'; e.textContent = 'В этом файле нет лицевых анимаций'; div.append(e); }
    const sel = selected.get(u.name) || new Set();
    for (const c of u.clips) {
      const row = document.createElement('label'); row.className = 'clip';
      const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = sel.has(c.name);
      cb.onchange = () => { if (cb.checked) sel.add(c.name); else sel.delete(c.name); selected.set(u.name, sel); updateConvertBtn(); };
      row.append(cb); row.insertAdjacentHTML('beforeend', `<span>${esc(c.name)}</span><small>${c.duration} с</small>`);
      div.append(row);
    }
    selected.set(u.name, sel);
    box.append(div);
  }
  updateConvertBtn();
}
function updateConvertBtn() {
  const n = [...selected.values()].reduce((a, s) => a + s.size, 0);
  $('convertBtn').disabled = !n || state.job.running;
  $('convertBtn').textContent = n ? `Перенести выбранное (${n})` : 'Перенести выбранное';
}
$('selAll').onclick = () => {
  const all = state.uploads.every((u) => u.clips.every((c) => selected.get(u.name)?.has(c.name)));
  for (const u of state.uploads) selected.set(u.name, new Set(all ? [] : u.clips.map((c) => c.name)));
  renderUploads();
};
async function uploadFiles(files) {
  for (const f of files) {
    if (!f.name.toLowerCase().endsWith('.ycd')) { toast(`${f.name}: нужен .ycd`); continue; }
    try {
      toast(`Загрузка ${f.name}…`, 60000);
      const r = await api(`/api/upload?name=${encodeURIComponent(f.name)}`, await f.arrayBuffer(), true);
      toast(`${r.name}: лицевых клипов ${r.clips.length}`);
    } catch (e) { toast(`${f.name}: ${e.message}`, 8000); }
  }
  await refresh();
}
$('files').addEventListener('change', (e) => { uploadFiles([...e.target.files]); e.target.value = ''; });
const drop = $('drop');
drop.addEventListener('dragover', (e) => { e.preventDefault(); drop.classList.add('over'); });
drop.addEventListener('dragleave', () => drop.classList.remove('over'));
drop.addEventListener('drop', (e) => { e.preventDefault(); drop.classList.remove('over'); uploadFiles([...e.dataTransfer.files]); });

$('convertBtn').onclick = async () => {
  const queue = [...selected].filter(([, s]) => s.size);
  $('job').hidden = false; $('jobLog').innerHTML = '';
  for (const [upload, clips] of queue) {
    try {
      await api('/api/convert', { upload, clips: [...clips], sex });
    } catch (e) { toast(e.message, 6000); break; }
    state.job.running = true; updateConvertBtn();
    let since = 0;
    for (;;) {
      await new Promise((r) => setTimeout(r, 700));
      const j = await api(`/api/job?since=${since}`);
      since = j.next;
      for (const line of j.log) { const d = document.createElement('div'); d.textContent = line; if (line.startsWith('ОШИБКА')) d.className = 'e'; $('jobLog').prepend(d); }
      $('jobBar').style.width = `${(100 * j.done) / Math.max(1, j.total)}%`;
      if (!j.running) break;
    }
    selected.set(upload, new Set());
  }
  await refresh();
};

// ------------------------------------------------------------------------------------------------ export
$('exportBtn').onclick = () => {
  const s = state.settings;
  $('expRes').value = s.resource; $('expPrefix').value = s.prefix; $('expCmd').value = s.command; $('expFolder').value = s.folder;
  $('expResult').innerHTML = '';
  $('exportDlg').showModal();
};
$('expGo').onclick = async () => {
  $('expGo').disabled = true;
  $('expResult').textContent = 'Сборка…';
  try {
    const r = await api('/api/export', { resource: $('expRes').value, prefix: $('expPrefix').value, command: $('expCmd').value, folder: $('expFolder').value });
    $('expResult').innerHTML = `Готово: ${r.count} анимаций. <a href="${r.zip}">Скачать .zip</a>${r.copied ? `<br>Скопировано в ${esc(r.copied)}` : ''}`;
    await refresh();
  } catch (e) { $('expResult').textContent = e.message; }
  $('expGo').disabled = false;
};

// ------------------------------------------------------------------------------------------------ mode / loop / start
function setMode(m) {
  mode = m;
  document.body.className = `mode-${m}`;
  document.querySelector(`input[name=mode][value=${m}]`).checked = true;
  dirty = true;
}
for (const r of document.querySelectorAll('input[name=mode]')) r.addEventListener('change', () => { setMode(r.value); if (r.value === 'edit') preview = null; renderLibrary(); });
for (const r of document.querySelectorAll('input[name=sex]')) r.addEventListener('change', async () => { preview = null; await setSex(r.value); if (mode === 'edit') newAnimation(); });

async function refresh() {
  state = await api('/api/state');
  renderUploads();
  renderLibrary();
}

let lastFrame = -1;
(function loop() {
  requestAnimationFrame(loop);
  orbit.update();
  if (mode === 'convert' && preview) {
    const t = ((performance.now() - playStart) / 1000) % Math.max(preview.duration, 0.1);
    const f = Math.min(preview.frames.length - 1, Math.round(t * FPS));
    if (f !== lastFrame) { applyFace(preview.frames[f]); lastFrame = f; }
  } else if (mode === 'edit') {
    if (playing) {
      let t = (performance.now() - playStart) / 1000;
      if (t > duration) { if ($('loopPrev').checked) { playStart = performance.now(); t = 0; } else { playing = false; t = duration; } }
      cursor = t; renderKeys(); dirty = true;
    }
    if (dirty) { applyFace(valuesAt(cursor)); dirty = false; lastFrame = -1; }
  } else if (dirty) { applyFace(new Map()); dirty = false; }
  renderer.render(scene, camera);
})();

startI18n();
$('lang').value = lang;
$('lang').addEventListener('change', () => { setLang($('lang').value); renderControls(); });

(async () => {
  resize();
  await refresh();
  await setSex('m');
  newAnimation();
})();
