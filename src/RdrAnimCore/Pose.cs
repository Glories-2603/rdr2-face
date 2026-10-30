using System.Numerics;
using System.Text.Json;

namespace RdrAnimCore;

/// <summary>
/// A body clip authored as local bone rotations per frame (a computed pose or loop, e.g. a rider on a bicycle), built
/// into an RDR .ycd the same way as a ported clip.
/// </summary>
public static class Pose
{
    /// <summary>
    /// spec: {"name":"bike_ride","frames":N,"duration":s,"bones":[{"tag":t,"q":[[x,y,z,w] per frame],
    /// "t":[[x,y,z] per frame] (optional local translation)}]}. A bone may have only "q" or only "t".
    /// Quaternions are local rotations in the skeleton's convention (rdr_skeleton.json); frames = number of keys.
    /// </summary>
    public static byte[] BuildFromSpec(string specJson, string dict, string clip)
    {
        using var doc = JsonDocument.Parse(specJson);
        var root = doc.RootElement;
        int frames = root.GetProperty("frames").GetInt32();
        float duration = root.GetProperty("duration").GetSingle();
        string animName = root.TryGetProperty("name", out var n) ? n.GetString() : clip;
        var tracks = new List<RdrTrackSource>();
        foreach (var b in root.GetProperty("bones").EnumerateArray())
        {
            ushort tag = (ushort)b.GetProperty("tag").GetInt32();
            if (b.TryGetProperty("t", out var tp))
            {
                var t = tp.EnumerateArray().Select(a => a.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToArray();
                if (t.Length != frames) throw new InvalidDataException($"bone {tag}: {t.Length} positions, {frames} frames");
                tracks.Add(new RdrTrackSource { Bone = new BoneRef(tag, 0, 0),
                    Channels = [t.Select(v => v[0]).ToArray(), t.Select(v => v[1]).ToArray(), t.Select(v => v[2]).ToArray()] });
            }
            if (!b.TryGetProperty("q", out var qp)) continue;
            var q = qp.EnumerateArray().Select(a =>
            {
                var v = a.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                return Quaternion.Normalize(new Quaternion(v[0], v[1], v[2], v[3]));
            }).ToArray();
            if (q.Length != frames) throw new InvalidDataException($"bone {tag}: {q.Length} keys, {frames} frames");
            // sign-continuous keys (no hemisphere flips between neighbouring frames: they jerk in game)
            for (int f = 1; f < q.Length; f++)
                if (Quaternion.Dot(q[f - 1], q[f]) < 0) q[f] = Quaternion.Negate(q[f]);
            var (ch, qi) = QuatChannels(q);
            tracks.Add(new RdrTrackSource { Bone = new BoneRef(tag, 1, 1), Channels = ch, QuatIndex = qi });
        }
        tracks.Sort((a, b) => a.Bone.TrackId != b.Bone.TrackId ? a.Bone.TrackId.CompareTo(b.Bone.TrackId) : a.Bone.BoneId.CompareTo(b.Bone.BoneId));
        var src = new RdrAnimSource
        {
            Name = $"pack:/{animName}.anim", Flags = 256, FrameCount = frames, Duration = duration,
            Signature = Jenk.Hash(animName), Tracks = tracks,
        };
        return Porting.BuildYcd(new PortedClip { ClipName = clip, Anim = src, Start = 0, End = duration, Speed = 1 }, dict, clip);
    }

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
}
