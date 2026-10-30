using System.Numerics;

namespace RdrAnimCore;

/// <summary>A ported animation over its whole length, before it is split into sequences and packed.</summary>
public sealed class RdrAnimSource
{
    public string Name;           // "pack:/gta_<clip>.anim"
    public ushort Flags;
    public int FrameCount;
    public float Duration;
    public uint Signature;
    public List<RdrTrackSource> Tracks = new();
}

public sealed class RdrTrackSource
{
    public BoneRef Bone;
    public List<float[]> Channels;  // per-frame values, one array per stored component
    public int QuatIndex = -1;      // rotation tracks: component rebuilt by the game (CachedQuaternion1)
}

/// <summary>A GTA clip ported to RDR: its first animation and timing.</summary>
public sealed class PortedClip
{
    public string ClipName;     // GTA clip name (short)
    public RdrAnimSource Anim;
    public float Start, End, Speed;
}

/// <summary>
/// Retargets GTA V ped animations to the RDR 2 ped skeleton (world-space rotation deltas, rest-pose corrections,
/// spine/neck interpolation, finger remap; see the README of RdrYcdTool for the method).
/// </summary>
public static class Retarget
{
    private static readonly (string Rdr, string Gta)[] MapOverrides =
    {
        ("SKEL_Spine0", "SKEL_Spine0"),
        ("SKEL_Spine2", "SKEL_Spine1"),
        ("SKEL_Spine4", "SKEL_Spine2"),
        ("SKEL_Spine6", "SKEL_Spine3"),
        ("SKEL_Neck0", "SKEL_Neck_1"),
    };

    // RDR fingers 1-4 have a metacarpal (X0) + phalanges X1-X3, GTA only phalanges X0-X2.
    private static readonly (string Rdr, string Gta)[] FingerOverrides =
        (from side in new[] { "L", "R" }
         from f in new[] { 1, 2, 3, 4 }
         from j in new[] { 0, 1, 2 }
         select ($"SKEL_{side}_Finger{f}{j + 1}", $"SKEL_{side}_Finger{f}{j}")).ToArray();

    private static readonly string[] UnmappedRdrBones =
        (from side in new[] { "L", "R" } from f in new[] { 1, 2, 3, 4 } select $"SKEL_{side}_Finger{f}0").ToArray();

    private static readonly (string Bone, string From, string To, float T)[] Interpolated =
    {
        ("SKEL_Spine1", "SKEL_Spine0", "SKEL_Spine2", 0.5f),
        ("SKEL_Spine3", "SKEL_Spine2", "SKEL_Spine4", 0.5f),
        ("SKEL_Spine5", "SKEL_Spine4", "SKEL_Spine6", 0.5f),
        ("SKEL_Neck1", "SKEL_Neck0", "SKEL_Head", 1f / 3f),
        ("SKEL_Neck2", "SKEL_Neck0", "SKEL_Head", 2f / 3f),
    };

    private static readonly string[] CorrectedLimbParts = ["Clavicle", "UpperArm", "Forearm", "Hand", "Finger", "Thigh", "Calf", "Foot"];

    private const byte TrackBoneTranslation = 0, TrackBoneRotation = 1, TrackMoverTranslation = 5, TrackMoverRotation = 6;

    /// <summary>Clips of a GTA dictionary (optionally filtered by short name), ported in dictionary order.</summary>
    public static List<PortedClip> Port(GtaYcd ycd, Func<string, bool> clipFilter = null)
    {
        var gta = Skeleton.Gta;
        var rdr = Skeleton.Rdr;
        var map = BuildMapping(gta, rdr);
        var corrections = BuildRestCorrections(gta, rdr, map);
        float scale = LegLength(rdr) / LegLength(gta);

        var result = new List<PortedClip>();
        var portedAnims = new Dictionary<GtaAnim, RdrAnimSource>();
        var usedNames = new List<string>();
        foreach (var clip in ycd.Clips)
        {
            string clipName = Names.Short(clip.Name);
            if (clipFilter != null && !clipFilter(clipName)) continue;
            if (clip.Parts.Count == 0) continue;

            foreach (var part in clip.Parts)
            {
                if (portedAnims.ContainsKey(part.Anim)) continue;
                string animName = "gta_" + (portedAnims.Count == 0 && clip.Parts.Count == 1 ? clipName : clipName + "_" + portedAnims.Count);
                animName = MakeUnique(animName, usedNames);
                usedNames.Add(animName);
                portedAnims[part.Anim] = BuildAnim(part.Anim, animName, gta, rdr, map, corrections, scale);
            }
            var first = clip.Parts[0];
            result.Add(new PortedClip { ClipName = clipName, Anim = portedAnims[first.Anim], Start = first.Start, End = first.End, Speed = first.Speed });
        }
        return result;
    }

    private static string MakeUnique(string name, IEnumerable<string> existing)
    {
        var set = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string r = name;
        for (int i = 2; set.Contains(r); i++) r = $"{name}_{i}";
        return r;
    }

    // ------------------------------------------------------------------ mapping

    private static Dictionary<int, int> BuildMapping(Skeleton gta, Skeleton rdr)
    {
        var map = new Dictionary<int, int>();
        for (int r = 0; r < rdr.Names.Length; r++)
        {
            string name = rdr.Names[r];
            if (!name.StartsWith("SKEL_", StringComparison.OrdinalIgnoreCase)) continue;
            if (gta.ByName.TryGetValue(name, out int g)) map[r] = g;
        }
        foreach (var (rName, _, _, _) in Interpolated)
            if (rdr.ByName.TryGetValue(rName, out int r)) map.Remove(r);
        foreach (var rName in UnmappedRdrBones)
            if (rdr.ByName.TryGetValue(rName, out int r)) map.Remove(r);
        foreach (var (rName, gName) in MapOverrides.Concat(FingerOverrides))
            if (rdr.ByName.TryGetValue(rName, out int r) && gta.ByName.TryGetValue(gName, out int g)) map[r] = g;
        return map;
    }

    private static Dictionary<int, Quaternion> BuildRestCorrections(Skeleton gta, Skeleton rdr, Dictionary<int, int> map)
    {
        var result = new Dictionary<int, Quaternion>();
        foreach (var (r, g) in map)
        {
            result[r] = Quaternion.Identity;
            string name = rdr.Names[r];
            if (!CorrectedLimbParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) continue;

            int rChild = -1;
            for (int c = 0; c < rdr.Parent.Length; c++)
            {
                if (!map.TryGetValue(c, out int gc) || !IsAncestor(rdr, r, c) || !IsAncestor(gta, g, gc) || !ChainIsDirect(rdr, r, c, map)) continue;
                if (rChild < 0 || rdr.Names[c].Contains("Finger2", StringComparison.OrdinalIgnoreCase)) rChild = c;
            }
            Vector3 dr, dg;
            if (rChild >= 0)
            {
                dr = rdr.RestWorldPos[rChild] - rdr.RestWorldPos[r];
                dg = gta.RestWorldPos[map[rChild]] - gta.RestWorldPos[g];
            }
            else if (rdr.Parent[r] >= 0 && gta.Parent[g] >= 0)
            {
                dr = rdr.RestWorldPos[r] - rdr.RestWorldPos[rdr.Parent[r]];
                dg = gta.RestWorldPos[g] - gta.RestWorldPos[gta.Parent[g]];
            }
            else continue;
            if (dr.LengthSquared() < 1e-8f || dg.LengthSquared() < 1e-8f) continue;

            var side = name.Contains("_L_") ? "L" : name.Contains("_R_") ? "R" : null;
            if (side != null && (name.Contains("Hand") || name.Contains("Finger"))
                && PalmAcross(rdr, side, "SKEL_{0}_Finger11", "SKEL_{0}_Finger41") is Vector3 ar
                && PalmAcross(gta, side, "SKEL_{0}_Finger10", "SKEL_{0}_Finger40") is Vector3 ag)
            {
                result[r] = Quaternion.Normalize(FrameRotation(dg, ag) * Quaternion.Conjugate(FrameRotation(dr, ar)));
            }
            else
            {
                result[r] = FromTo(Vector3.Normalize(dr), Vector3.Normalize(dg));
            }
        }
        return result;
    }

    private static bool ChainIsDirect(Skeleton s, int ancestor, int child, Dictionary<int, int> map)
    {
        for (int p = s.Parent[child]; p >= 0 && p != ancestor; p = s.Parent[p])
            if (map.ContainsKey(p)) return false;
        return true;
    }

    private static bool IsAncestor(Skeleton s, int ancestor, int bone)
    {
        for (int p = s.Parent[bone]; p >= 0; p = s.Parent[p])
            if (p == ancestor) return true;
        return false;
    }

    private static Vector3? PalmAcross(Skeleton s, string side, string indexName, string littleName)
    {
        if (!s.ByName.TryGetValue(string.Format(indexName, side), out int a) || !s.ByName.TryGetValue(string.Format(littleName, side), out int b)) return null;
        var v = s.RestWorldPos[b] - s.RestWorldPos[a];
        return v.LengthSquared() > 1e-8f ? v : null;
    }

    private static Quaternion FrameRotation(Vector3 primary, Vector3 secondary)
    {
        var x = Vector3.Normalize(primary);
        var z = Vector3.Cross(x, secondary);
        if (z.LengthSquared() < 1e-10f) return FromTo(Vector3.UnitX, x);
        z = Vector3.Normalize(z);
        var y = Vector3.Cross(z, x);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, a);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c, 1 + d));
    }

    private static float LegLength(Skeleton s)
    {
        if (s.ByName.TryGetValue("SKEL_Pelvis", out int p) && s.ByName.TryGetValue("SKEL_L_Foot", out int f))
        {
            float l = s.RestWorldPos[p].Z - s.RestWorldPos[f].Z;
            if (l > 0.1f) return l;
        }
        return 1f;
    }

    // ------------------------------------------------------------------ one animation

    private static RdrAnimSource BuildAnim(GtaAnim anim, string animName, Skeleton gta, Skeleton rdr,
        Dictionary<int, int> map, Dictionary<int, Quaternion> corrections, float scale)
    {
        int frames = Math.Max(anim.FrameCount, 1);
        var sampler = new Sampler(anim);

        var interp = new List<(int Bone, int From, int To, float T)>();
        foreach (var (b, f, t, w) in Interpolated)
        {
            if (rdr.ByName.TryGetValue(b, out int bi) && rdr.ByName.TryGetValue(f, out int fi) && rdr.ByName.TryGetValue(t, out int ti)
                && map.ContainsKey(fi) && map.ContainsKey(ti))
            {
                interp.Add((bi, fi, ti, w));
            }
        }
        var rotBones = map.Keys.Concat(interp.Select(i => i.Bone)).Distinct().ToList();
        var rotBoneSet = rotBones.ToHashSet();
        int rdrRoot = rdr.ByName.GetValueOrDefault("SKEL_ROOT", 0);
        int rdrPelvis = rdr.ByName.GetValueOrDefault("SKEL_Pelvis", -1);
        int gtaRoot = gta.ByName.GetValueOrDefault("SKEL_ROOT", 0);
        int gtaPelvis = gta.ByName.GetValueOrDefault("SKEL_Pelvis", -1);

        var rotValues = rotBones.ToDictionary(b => b, _ => new Quaternion[frames]);
        var posValues = new Dictionary<int, Vector3[]> { [rdrRoot] = new Vector3[frames] };
        if (rdrPelvis >= 0 && gtaPelvis >= 0) posValues[rdrPelvis] = new Vector3[frames];
        bool hasMover = sampler.Has(TrackMoverTranslation, 0) || sampler.Has(TrackMoverRotation, 0);
        var moverPos = new Vector3[frames];
        var moverRot = new Quaternion[frames];

        var gWorld = new Quaternion[gta.Names.Length];
        var rWorld = new Quaternion[rdr.Names.Length];
        var effDelta = new Quaternion[rdr.Names.Length];

        for (int f = 0; f < frames; f++)
        {
            foreach (int i in gta.Order)
            {
                var local = sampler.TryQuaternion(TrackBoneRotation, gta.Tags[i], f, out var q) ? q : gta.RestLocal[i];
                int p = gta.Parent[i];
                gWorld[i] = p < 0 ? local : Quaternion.Normalize(gWorld[p] * local);
            }

            foreach (var (r, g) in map)
            {
                var d = gWorld[g] * Quaternion.Conjugate(gta.RestWorld[g]);
                effDelta[r] = Quaternion.Normalize(d * corrections[r]);
            }
            foreach (var (b, from, to, t) in interp)
            {
                effDelta[b] = Quaternion.Slerp(effDelta[from], effDelta[to], t);
            }

            foreach (int i in rdr.Order)
            {
                int p = rdr.Parent[i];
                var parentWorld = p < 0 ? Quaternion.Identity : rWorld[p];
                if (rotBoneSet.Contains(i))
                {
                    rWorld[i] = Quaternion.Normalize(effDelta[i] * rdr.RestWorld[i]);
                    var local = Quaternion.Normalize(Quaternion.Conjugate(parentWorld) * rWorld[i]);
                    if (local.W < 0) local = -local;
                    rotValues[i][f] = local;
                }
                else
                {
                    rWorld[i] = p < 0 ? rdr.RestLocal[i] : Quaternion.Normalize(parentWorld * rdr.RestLocal[i]);
                }
            }

            posValues[rdrRoot][f] = rdr.RestPos[rdrRoot] + (sampler.TryVector(TrackBoneTranslation, gta.Tags[gtaRoot], f, out var rp) ? rp - gta.RestPos[gtaRoot] : Vector3.Zero) * scale;
            if (posValues.ContainsKey(rdrPelvis))
            {
                posValues[rdrPelvis][f] = rdr.RestPos[rdrPelvis] + (sampler.TryVector(TrackBoneTranslation, gta.Tags[gtaPelvis], f, out var pp) ? pp - gta.RestPos[gtaPelvis] : Vector3.Zero) * scale;
            }
            if (hasMover)
            {
                moverPos[f] = sampler.TryVector(TrackMoverTranslation, 0, f, out var mp) ? mp * scale : Vector3.Zero;
                moverRot[f] = sampler.TryQuaternion(TrackMoverRotation, 0, f, out var mq) ? mq : Quaternion.Identity;
                if (moverRot[f].W < 0) moverRot[f] = -moverRot[f];
            }
        }

        var tracks = new List<RdrTrackSource>();
        foreach (var (b, vals) in posValues)
            tracks.Add(new RdrTrackSource { Bone = new BoneRef(rdr.Tags[b], 0, TrackBoneTranslation), Channels = Vec3Channels(vals) });
        foreach (var b in rotBones)
        {
            var (ch, qi) = QuatChannels(rotValues[b]);
            tracks.Add(new RdrTrackSource { Bone = new BoneRef(rdr.Tags[b], 1, TrackBoneRotation), Channels = ch, QuatIndex = qi });
        }
        if (hasMover)
        {
            tracks.Add(new RdrTrackSource { Bone = new BoneRef(0, 0, TrackMoverTranslation), Channels = Vec3Channels(moverPos) });
            var (mch, mqi) = QuatChannels(moverRot);
            tracks.Add(new RdrTrackSource { Bone = new BoneRef(0, 1, TrackMoverRotation), Channels = mch, QuatIndex = mqi });
        }
        tracks.Sort((a, b) => a.Bone.TrackId != b.Bone.TrackId ? a.Bone.TrackId.CompareTo(b.Bone.TrackId) : a.Bone.BoneId.CompareTo(b.Bone.BoneId));

        return new RdrAnimSource
        {
            Name = $"pack:/{animName}.anim",
            Flags = (ushort)(hasMover ? 272 : 256),
            FrameCount = frames,
            Duration = anim.Duration,
            Signature = Jenk.Hash(animName),
            Tracks = tracks,
        };
    }

    private static List<float[]> Vec3Channels(Vector3[] v) =>
        [v.Select(x => x.X).ToArray(), v.Select(x => x.Y).ToArray(), v.Select(x => x.Z).ToArray()];

    private static (List<float[]> Channels, int QuatIndex) QuatChannels(Quaternion[] q)
    {
        static float C(Quaternion v, int i) => i switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };
        int best = 3;
        float bestMin = -1;
        for (int i = 0; i < 4; i++)
        {
            float min = q.Length == 0 ? 0 : q.Min(v => MathF.Abs(C(v, i)));
            if (min > bestMin) { bestMin = min; best = i; }
        }
        var chans = new List<float[]>();
        for (int i = 0; i < 4; i++)
        {
            if (i == best) continue;
            chans.Add(q.Select(v => C(v, best) < 0 ? -C(v, i) : C(v, i)).ToArray());
        }
        return (chans, best);
    }

    /// <summary>Evaluates GTA animation tracks per frame.</summary>
    private sealed class Sampler
    {
        private readonly GtaAnim anim;
        private readonly Dictionary<(byte, ushort), int> trackIndex = new();

        public Sampler(GtaAnim anim)
        {
            this.anim = anim;
            var ids = anim.BoneIds ?? [];
            for (int i = 0; i < ids.Length; i++) trackIndex.TryAdd((ids[i].TrackId, ids[i].BoneId), i);
        }

        public bool Has(byte trackId, ushort boneId) => trackIndex.ContainsKey((trackId, boneId));

        private Vector4? Eval(byte trackId, ushort boneId, int frame) =>
            trackIndex.TryGetValue((trackId, boneId), out int ti) ? anim.Evaluate(ti, frame) : null;

        public bool TryQuaternion(byte trackId, ushort boneId, int frame, out Quaternion q)
        {
            var v = Eval(trackId, boneId, frame);
            q = v.HasValue ? Quaternion.Normalize(new Quaternion(v.Value.X, v.Value.Y, v.Value.Z, v.Value.W)) : Quaternion.Identity;
            return v.HasValue;
        }

        public bool TryVector(byte trackId, ushort boneId, int frame, out Vector3 p)
        {
            var v = Eval(trackId, boneId, frame);
            p = v.HasValue ? new Vector3(v.Value.X, v.Value.Y, v.Value.Z) : Vector3.Zero;
            return v.HasValue;
        }
    }
}
