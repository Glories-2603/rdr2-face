using System.Numerics;

namespace RdrAnimCore;

public enum RdrChannelType
{
    StaticQuaternion, StaticVector3, StaticFloat, RawFloat, QuantizeFloat, IndirectQuantizeFloat, LinearFloat,
    CachedQuaternion1, CachedQuaternion2, IncrementalFloat,
}

/// <summary>A channel of an RDR sequence. Only the types the encoder produces are supported.</summary>
public sealed class RdrChannel
{
    public RdrChannelType Type;
    public int Track, Index, QuatIndex;
    public float Value;              // StaticFloat
    public float Quantum, Offset;    // QuantizeFloat
    public float[] Values;           // QuantizeFloat, per frame (unquantized)

    // set while packing
    public int FrameBits, DataOffset, FrameOffset;
}

public sealed class RdrTrack
{
    public BoneRef Bone;
    public RdrChannel[] Channels;
}

public sealed class RdrSequence
{
    public uint Signature;
    public ushort FrameCount;
    public RdrTrack[] Tracks;
}

public sealed class RdrAnim
{
    public string Name;
    public ushort Flags;
    public int FrameCount;
    public ushort SequenceSize;
    public float Duration;
    public uint Signature;
    public BoneRef[] BoneIds;
    public List<RdrSequence> Sequences = new();
}

/// <summary>
/// Quantizes a ported animation and splits it into sequences the way game files are built
/// (same rules as RdrYcdTool's ChannelFixer.RebuildSequences).
/// </summary>
public static class RdrEncoder
{
    public const float RotationPrecision = 5e-4f;
    public const float DefaultPrecision = 1e-4f;
    public const int MaxSequenceBytes = 20 * 1024;
    private const float MinDroppedComponent = 0.05f;

    private sealed class ChannelPlan
    {
        public float[] Values;
        public float Quantum, Offset;
        public int Bits;
        public bool IsStatic;
        public float StaticValue;
    }

    public static RdrAnim Encode(RdrAnimSource src)
    {
        int frames = Math.Max(src.FrameCount, 1);
        var bones = src.Tracks.Select(t => t.Bone).ToArray();
        int trackCount = src.Tracks.Count;

        // Every stored channel over the whole animation (quaternion tracks too: used for the size estimate).
        var plans = new ChannelPlan[trackCount][];
        for (int ti = 0; ti < trackCount; ti++)
        {
            var t = src.Tracks[ti];
            bool isRotation = bones[ti].TypeId == 1;
            float precision = isRotation ? RotationPrecision : DefaultPrecision;
            plans[ti] = t.Channels.Select(values => Quantize(values, precision)).ToArray();
        }

        // Rotation tracks: full, sign-continuous quaternions.
        var quats = new Vector4[trackCount][];
        for (int ti = 0; ti < trackCount; ti++)
        {
            var t = src.Tracks[ti];
            if (t.QuatIndex < 0 || t.Channels.Count != 3) continue;
            var q = new Vector4[frames];
            for (int f = 0; f < frames; f++)
            {
                q[f] = CachedQuat(t, f);
                if (f > 0 && Vector4.Dot(q[f - 1], q[f]) < 0) q[f] = -q[f];
            }
            quats[ti] = q;
        }

        int frameBits = plans.SelectMany(p => p).Where(p => p.Values != null && !p.IsStatic).Sum(p => p.Bits);
        int frameBytes = Math.Max((frameBits + 7) / 8, 1);
        int maxFrames = Math.Clamp(MaxSequenceBytes / frameBytes, 16, 640);

        var anim = new RdrAnim
        {
            Name = src.Name, Flags = src.Flags, FrameCount = src.FrameCount, Duration = src.Duration,
            Signature = src.Signature, BoneIds = bones,
        };

        while (true)
        {
            int seqSize;
            List<(int Start, int Count)> ranges;
            while (true)
            {
                (seqSize, ranges) = SplitSequences(frames, maxFrames);
                bool allOk = ranges.All(r => quats.All(q => q == null || PickDroppedComponent(q, r.Start, r.Count).Min >= MinDroppedComponent));
                if (allOk || maxFrames <= 16) break;
                maxFrames = Math.Max(16, Math.Min(maxFrames, seqSize + 1) - 16);
            }

            var seqs = new List<RdrSequence>();
            for (int si = 0; si < ranges.Count; si++)
            {
                var (start, count) = ranges[si];
                var tracks = new RdrTrack[trackCount];
                for (int ti = 0; ti < trackCount; ti++)
                {
                    RdrChannel[] chans;
                    if (quats[ti] != null)
                    {
                        chans = CachedQuatChannels(quats[ti], start, count, ti);
                    }
                    else
                    {
                        chans = new RdrChannel[plans[ti].Length];
                        for (int ci = 0; ci < chans.Length; ci++)
                        {
                            var plan = plans[ti][ci];
                            chans[ci] = plan.IsStatic
                                ? new RdrChannel { Type = RdrChannelType.StaticFloat, Value = plan.StaticValue }
                                : new RdrChannel { Type = RdrChannelType.QuantizeFloat, Quantum = plan.Quantum, Offset = plan.Offset, Values = plan.Values.AsSpan(start, count).ToArray() };
                            chans[ci].Track = ti;
                            chans[ci].Index = ci;
                        }
                    }
                    tracks[ti] = new RdrTrack { Bone = bones[ti], Channels = chans };
                }
                seqs.Add(new RdrSequence { Signature = SequenceSignature(src.Name, si, tracks), FrameCount = (ushort)count, Tracks = tracks });
            }

            anim.SequenceSize = (ushort)seqSize;
            anim.Sequences = seqs;
            int largest = seqs.Max(s => RdrPacker.Pack(s).Data.Length);
            if (largest <= MaxSequenceBytes || maxFrames <= 16) break;
            maxFrames = Math.Max(16, Math.Min(maxFrames, seqSize + 1) - 16);
        }
        return anim;
    }

    /// <summary>Quaternion of a rotation track at a frame: three stored components + the rebuilt one.</summary>
    private static Vector4 CachedQuat(RdrTrackSource t, int f)
    {
        var q = new float[4];
        int k = 0;
        float sum = 0;
        foreach (var values in t.Channels)
        {
            if (k == t.QuatIndex) k++;
            q[k] = values[Math.Clamp(f, 0, values.Length - 1)];
            sum += q[k] * q[k];
            k++;
        }
        q[t.QuatIndex] = MathF.Sqrt(Math.Max(0, 1 - sum));
        return Vector4.Normalize(new Vector4(q[0], q[1], q[2], q[3]));
    }

    private static ChannelPlan Quantize(float[] values, float precision)
    {
        float min = values.Min();
        float max = values.Max();
        float range = max - min;
        if (range <= precision * 0.5f)
        {
            return new ChannelPlan { Values = values, IsStatic = true, StaticValue = (min + max) * 0.5f };
        }
        int bits = 1;
        while (bits < 24 && range / ((1u << bits) - 1) > precision) bits++;
        return new ChannelPlan { Values = values, Quantum = range / ((1u << bits) - 1), Offset = min, Bits = bits };
    }

    private static RdrChannel QuantizedChannel(float[] values, float precision)
    {
        var plan = Quantize(values, precision);
        if (plan.IsStatic) return new RdrChannel { Type = RdrChannelType.StaticFloat, Value = plan.StaticValue };
        return new RdrChannel { Type = RdrChannelType.QuantizeFloat, Quantum = plan.Quantum, Offset = plan.Offset, Values = (float[])values.Clone() };
    }

    private static (int SeqSize, List<(int Start, int Count)> Ranges) SplitSequences(int frames, int maxFrames)
    {
        var ranges = new List<(int Start, int Count)>();
        if (frames <= maxFrames)
        {
            ranges.Add((0, frames));
            return (Ceil16(frames) - 1, ranges);
        }
        int n = (int)Math.Ceiling((frames - 1) / (double)(maxFrames - 1));
        int per = (int)Math.Ceiling((frames - 1) / (double)n);
        int seqSize = Math.Max(Ceil16(per + 1) - 1, 15);
        if (seqSize + 1 > maxFrames) seqSize = Math.Max((maxFrames & ~15) - 1, 15);
        for (int start = 0; start < frames - 1; start += seqSize)
            ranges.Add((start, Math.Min(seqSize + 1, frames - start)));
        return (seqSize, ranges);
    }

    private static int Ceil16(int v) => (v + 15) / 16 * 16;

    private static float Comp(Vector4 v, int i) => i switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };

    private static (int Index, float Sign, float Min) PickDroppedComponent(Vector4[] q, int start, int count)
    {
        (int, float, float) best = (3, 1, float.MinValue);
        for (int c = 0; c < 4; c++)
        {
            float sign = Comp(q[start], c) < 0 ? -1 : 1;
            float min = float.MaxValue;
            for (int f = start; f < start + count; f++) min = Math.Min(min, sign * Comp(q[f], c));
            if (min > best.Item3) best = (c, sign, min);
        }
        return best;
    }

    /// <summary>Three quantized components + CachedQuaternion1 (channel order as the porter emits them).</summary>
    private static RdrChannel[] CachedQuatChannels(Vector4[] q, int start, int count, int track)
    {
        var (dropped, sign, _) = PickDroppedComponent(q, start, count);
        var chans = new RdrChannel[4];
        int comp = 0;
        for (int ci = 0; ci < 3; ci++)
        {
            if (comp == dropped) comp++;
            var values = new float[count];
            for (int f = 0; f < count; f++) values[f] = sign * Comp(q[start + f], comp);
            chans[ci] = QuantizedChannel(values, RotationPrecision);
            chans[ci].Track = track;
            chans[ci].Index = ci;
            comp++;
        }
        chans[3] = new RdrChannel { Type = RdrChannelType.CachedQuaternion1, QuatIndex = dropped, Track = track, Index = 3 };
        return chans;
    }

    /// <summary>Unique per content: xxHash32 of the name, index and every channel value.</summary>
    private static uint SequenceSignature(string animName, int index, RdrTrack[] tracks)
    {
        var h = new System.IO.Hashing.XxHash32();
        h.Append(System.Text.Encoding.UTF8.GetBytes($"{animName}#{index}"));
        foreach (var t in tracks)
        {
            foreach (var ch in t.Channels ?? [])
            {
                h.Append(BitConverter.GetBytes((int)ch.Type));
                if (ch.Type == RdrChannelType.QuantizeFloat) h.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(ch.Values.AsSpan()));
                else if (ch.Type == RdrChannelType.StaticFloat) h.Append(BitConverter.GetBytes(ch.Value));
            }
        }
        uint sig = BitConverter.ToUInt32(h.GetCurrentHash());
        return sig == 0 ? 1u : sig;
    }
}
