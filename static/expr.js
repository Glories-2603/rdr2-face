// RDR 2 expression VM: runs the metaped customisation expressions (meta_base_male/female.yed: bcustom = face,
// mp_meta_cust = body) that turn the character sliders (SetCharExpression values, input tracks 64) into bone
// transforms. Programs come from /api/expr (server.py expressions()): ops = [[code, args...]].
//
// Values on the stack are 4-lane vectors (scalars are splatted). Opcodes are the game's byte values; the ones CodeX
// leaves unnamed were worked out from their use:
//   0x50 blend   base + sum(reg[i] * (x, y, z)) over lanes (data decoded by the server)
//   0x52 store   pop -> register k          0x4F pow   a ^ b
//   0x67 get     like GetVariable          0x70 jump  pop condition, skip n ops when false
//   0x5C / 0x71 / 0x72  eye look-at drivers: left at their current pose (no look-at target in the viewer)

// TRACK_FACIAL: the face clips' control channels (face_human@*: floats and 2-component values), read through
// pose.controls (Map control id -> value or [x, y]); without it the face is neutral
export const TRACK_POS = 0, TRACK_ROT = 1, TRACK_SCALE = 2, TRACK_FACIAL = 25, TRACK_INPUT = 64;

const OP = {
    End: 0, Push0: 3, TrackGet: 6, Push1: 4, PushFloat: 5, TrackGetComp: 7, TrackGetObject: 10, PushVector: 11, Abs: 15, Square: 28,
    Deg2Rad: 29, Rad2Deg: 30, FromEuler: 33, TrackSet: 38, TrackSetComp: 39, TrackSetOffset: 40, TrackSetObject: 42,
    Jump: 43, Add: 46, Sub: 47, Mul: 48, Min: 49, Max: 50, Greater: 53, Less: 54, Clamp: 57, Mad: 59, ToVector: 61,
    GetVariable: 66, SetVariable: 67, Equal: 72, Pow: 0x4F, Blend: 0x50, Store: 0x52, LookAt: 0x5C, GetVariable2: 0x67,
    JumpIfFalse: 0x70, CopyPos: 0x71, CopyRot: 0x72,
};

const splat = (v) => [v, v, v, v];
const lanes = (a, b, f) => [f(a[0], b[0]), f(a[1], b[1]), f(a[2], b[2]), f(a[3], b[3])];
const map = (a, f) => [f(a[0]), f(a[1]), f(a[2]), f(a[3])];

// euler (radians, x then y then z) <-> quaternion [x, y, z, w]
export function quatFromEuler(x, y, z) {
    const cx = Math.cos(x / 2), sx = Math.sin(x / 2), cy = Math.cos(y / 2), sy = Math.sin(y / 2);
    const cz = Math.cos(z / 2), sz = Math.sin(z / 2);
    return [sx * cy * cz - cx * sy * sz, cx * sy * cz + sx * cy * sz, cx * cy * sz - sx * sy * cz, cx * cy * cz + sx * sy * sz];
}
export function eulerFromQuat(q) {
    const [x, y, z, w] = q;
    const sinp = 2 * (w * y - z * x);
    return [Math.atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y)),
        Math.abs(sinp) >= 1 ? Math.sign(sinp) * Math.PI / 2 : Math.asin(sinp),
        Math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z))];
}
export function quatMul(a, b) {
    return [a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1], a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
        a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3], a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]];
}

/**
 * Runs one program.
 * pose: { get(tag, track) -> [x,y,z(,w)] current local value, bind(tag, track) -> default value,
 *         set(tag, track, value) }
 * inputs: Map input hash -> slider value (-1..1); missing inputs and facial animation controls read 0.
 * Returns the number of executed ops (for tests).
 */
export function runExpression(prog, pose, inputs, vars = new Map()) {
    const ops = prog.ops, st = [], regs = new Float64Array(Math.max(prog.regs, 1));
    const pop = () => st.pop() || splat(0);
    let n = 0;
    for (let pc = 0; pc < ops.length; pc++, n++) {
        const o = ops[pc];
        switch (o[0]) {
        case OP.End: return n;
        case OP.Push0: st.push(splat(0)); break;
        case OP.Push1: st.push(splat(1)); break;
        case OP.PushFloat: st.push(splat(o[1])); break;
        case OP.PushVector: st.push([...(o[1] || [0, 0, 0, 0])]); break;
        case OP.TrackGet: {                                             // a bone's local value (beard copies the face)
            const v = pose.get(o[2], o[3]) || (o[3] === TRACK_ROT ? [0, 0, 0, 1] : o[3] === TRACK_SCALE ? [1, 1, 1] : [0, 0, 0]);
            st.push([v[0], v[1], v[2], v[3] ?? 0]);
            break;
        }
        case OP.TrackGetComp: {
            const [, , bone, track, format, comp] = o;
            if (track === TRACK_INPUT) st.push(splat(inputs.get(bone) || 0));
            else if (track === TRACK_POS || track === TRACK_SCALE) st.push(splat((pose.get(bone, track) || [0, 0, 0])[comp] || 0));
            else if (track === TRACK_ROT) {
                const q = pose.get(bone, track) || [0, 0, 0, 1];
                st.push(splat(format === 3 ? eulerFromQuat(q)[comp] : q[comp]));
            } else if (track === TRACK_FACIAL) {                        // facial animation controls (face clips)
                const v = pose.controls ? pose.controls.get(bone) : undefined;
                st.push(splat(Array.isArray(v) ? (v[comp] ?? 0) : (v || 0)));
            } else st.push(splat(0));
            break;
        }
        case OP.TrackGetObject: {                                       // object (model) space
            const v = (pose.getObject || pose.get)(o[2], o[3]) || (o[3] === TRACK_ROT ? [0, 0, 0, 1] : [0, 0, 0]);
            st.push([v[0], v[1], v[2], v[3] ?? 0]);
            break;
        }
        case OP.Abs: st.push(map(pop(), Math.abs)); break;
        case OP.Square: st.push(map(pop(), (x) => x * x)); break;
        case OP.Deg2Rad: st.push(map(pop(), (x) => x * Math.PI / 180)); break;
        case OP.Rad2Deg: st.push(map(pop(), (x) => x * 180 / Math.PI)); break;
        case OP.FromEuler: { const v = pop(); st.push(quatFromEuler(v[0], v[1], v[2])); break; }
        case OP.TrackSet: {
            const v = pop(), [, , bone, track] = o;
            pose.set(bone, track, track === TRACK_ROT ? normalise(v) : v.slice(0, 3));
            break;
        }
        case OP.TrackSetComp: {
            const v = pop(), [, , bone, track, , comp] = o;
            if (track === TRACK_INPUT) inputs.set(bone, v[0]);
            else {
                const cur = [...(pose.get(bone, track) || [0, 0, 0, 1])];
                cur[comp] = v[0];
                pose.set(bone, track, cur);
            }
            break;
        }
        case OP.TrackSetOffset: {                                       // relative to the bone's default pose
            const v = pop(), [, , bone, track] = o;
            const b = pose.bind(bone, track);
            if (!b) break;
            if (track === TRACK_ROT) pose.set(bone, track, normalise(quatMul(b, v)));
            else pose.set(bone, track, [b[0] + v[0], b[1] + v[1], b[2] + v[2]]);
            break;
        }
        case OP.TrackSetObject: {
            const v = pop();
            // look-at results need a target the viewer has not got: those bones keep their pose
            if (ops[pc - 1][0] !== OP.LookAt && pose.setObject) pose.setObject(o[2], o[3], o[3] === TRACK_ROT ? normalise(v) : v.slice(0, 3));
            break;
        }
        case OP.Jump: pc += o[1]; break;
        case OP.JumpIfFalse: if (!pop()[0]) pc += o[1]; break;
        case OP.Add: { const b = pop(), a = pop(); st.push(lanes(a, b, (x, y) => x + y)); break; }
        case OP.Sub: { const b = pop(), a = pop(); st.push(lanes(a, b, (x, y) => x - y)); break; }
        case OP.Mul: { const b = pop(), a = pop(); st.push(lanes(a, b, (x, y) => x * y)); break; }
        case OP.Min: { const b = pop(), a = pop(); st.push(lanes(a, b, Math.min)); break; }
        case OP.Max: { const b = pop(), a = pop(); st.push(lanes(a, b, Math.max)); break; }
        case OP.Pow: { const b = pop(), a = pop(); st.push(lanes(a, b, (x, y) => Math.pow(x, y))); break; }
        case OP.Greater: { const b = pop(), a = pop(); st.push(lanes(a, b, (x, y) => (x > y ? 1 : 0))); break; }
        case OP.Less: { const b = pop(), a = pop(); st.push(lanes(a, b, (x, y) => (x < y ? 1 : 0))); break; }
        case OP.Equal: { const b = pop(), a = pop(); st.push(lanes(a, b, (x, y) => (x === y ? 1 : 0))); break; }
        case OP.Clamp: {
            const hi = pop(), lo = pop(), x = pop();
            st.push([0, 1, 2, 3].map((k) => Math.min(hi[k], Math.max(lo[k], x[k]))));
            break;
        }
        case OP.Mad: {
            const c = pop(), b = pop(), a = pop();
            st.push([0, 1, 2, 3].map((k) => a[k] * b[k] + c[k]));
            break;
        }
        case OP.ToVector: { const z = pop(), y = pop(), x = pop(); st.push([x[0], y[0], z[0], 0]); break; }
        case OP.GetVariable:
        case OP.GetVariable2: st.push([...(vars.get(o[1]) || splat(0))]); break;
        case OP.SetVariable: vars.set(o[1], pop()); break;
        case OP.Store: regs[o[1]] = pop()[0]; break;
        case OP.Blend: {
            const v = [...o[1]];
            for (const [r, x, y, z] of o[2]) {
                const w = regs[r];
                if (w) { v[0] += w * x; v[1] += w * y; v[2] += w * z; }
            }
            st.push(v);
            break;
        }
        case OP.LookAt: pop(); pop(); pop(); pop(); st.push([0, 0, 0, 1]); break;
        case OP.CopyRot: st.push([...(pose.get(prog.tracks[o[1]][0], TRACK_ROT) || [0, 0, 0, 1])]); break;
        case OP.CopyPos: st.push([...(pose.get(prog.tracks[o[1]][0], TRACK_POS) || [0, 0, 0]), 0]); break;
        default:
            throw new Error(`expression op 0x${o[0].toString(16)} at ${pc}`);
        }
    }
    return n;
}

function normalise(q) {
    const l = Math.hypot(q[0], q[1], q[2], q[3]) || 1;
    return [q[0] / l, q[1] / l, q[2] / l, q[3] / l];
}
