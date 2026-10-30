namespace RdrAnimCore;

/// <summary>A packed RDR animation sequence (32-byte header fields + data).</summary>
public sealed class PackedSequence
{
    public uint Signature;
    public uint FrameOffset;
    public uint RootMotionRefsOffset;
    public ushort Unknown14 = 1;
    public ushort FrameCount;
    public ushort FrameLength;
    public ushort IndirectQuantizeFloatNumInts;
    public ushort QuantizeFloatValueBits;
    public byte ChunkSize = 10;
    public byte RootMotionRefCounts;
    public byte[] Data;

    /// <summary>The block as stored in the resource: header + data.</summary>
    public byte[] ToBlock()
    {
        var b = new byte[32 + Data.Length];
        BitConverter.GetBytes(Signature).CopyTo(b, 0);
        BitConverter.GetBytes((uint)Data.Length).CopyTo(b, 4);
        BitConverter.GetBytes(0u).CopyTo(b, 8);
        BitConverter.GetBytes(FrameOffset).CopyTo(b, 12);
        BitConverter.GetBytes(RootMotionRefsOffset).CopyTo(b, 16);
        BitConverter.GetBytes(Unknown14).CopyTo(b, 20);
        BitConverter.GetBytes(FrameCount).CopyTo(b, 22);
        BitConverter.GetBytes(FrameLength).CopyTo(b, 24);
        BitConverter.GetBytes(IndirectQuantizeFloatNumInts).CopyTo(b, 26);
        BitConverter.GetBytes(QuantizeFloatValueBits).CopyTo(b, 28);
        b[30] = ChunkSize;
        b[31] = RootMotionRefCounts;
        Data.CopyTo(b, 32);
        return b;
    }
}

/// <summary>
/// Packs an RDR animation sequence into the game's binary layout: channel headers (main data), bit-packed frames,
/// channel counts per type, channel items (track/index) and root motion references. Byte-identical to CodeX 0.28.
/// </summary>
public static class RdrPacker
{
    private const int TypeCount = 10;

    public static PackedSequence Pack(RdrSequence seq)
    {
        var w = new Writer();
        var byType = new List<RdrChannel>[TypeCount];
        for (int i = 0; i < seq.Tracks.Length; i++)
        {
            var t = seq.Tracks[i];
            if (t?.Channels == null) continue;
            foreach (var ch in t.Channels)
            {
                if (ch == null) continue;
                int type = (int)ch.Type;
                if (type < 0 || type >= TypeCount) continue;
                if (ch.Type is RdrChannelType.CachedQuaternion1 or RdrChannelType.CachedQuaternion2) ch.Index = ch.QuatIndex;
                (byType[type] ??= new List<RdrChannel>()).Add(ch);
            }
        }

        for (int k = 0; k < TypeCount; k++)
        {
            var list = byType[k];
            ushort count = (ushort)(list?.Count ?? 0);
            w.ChannelList(count);
            for (int l = 0; l < count; l++)
            {
                var ch = list[l];
                w.ChannelItem((ushort)(ch.Index + (ch.Track << 2)));
                ch.DataOffset = w.MainLength / 4;
                WriteHeader(ch, w);
            }
            if (count % 4 > 0)
            {
                ushort pad = (ushort)(seq.Tracks.Length << 2);
                for (int i = 0; i < 4 - count % 4; i++) w.ChannelItem(pad);
            }
        }

        for (int f = 0; f < seq.FrameCount; f++)
        {
            w.BeginFrame();
            for (int k = 0; k < TypeCount; k++)
            {
                if (byType[k] == null) continue;
                foreach (var ch in byType[k])
                {
                    if (ch.Type == RdrChannelType.QuantizeFloat) w.FrameBits(Quanta(ch, ch.Values[f]), ch.FrameBits);
                }
            }
            w.EndFrame();
        }

        int frameOffset = 0;
        foreach (var list in byType)
        {
            if (list == null) continue;
            foreach (var ch in list)
            {
                ch.FrameOffset = frameOffset;
                frameOffset += ch.FrameBits;
            }
        }

        // Root motion (mover) references: position = track id 5, rotation = track id 6.
        var posRefs = RootRefs(seq, 5);
        var rotRefs = RootRefs(seq, 6);
        foreach (var r in posRefs) w.ChannelRef(r);
        foreach (var r in rotRefs) w.ChannelRef(r);

        byte[] main = w.Main.ToArray();
        byte[] frames = w.FrameData();
        byte[] list2 = w.List.ToArray();
        byte[] items = w.Items.ToArray();
        var data = new byte[main.Length + frames.Length + list2.Length + items.Length];
        int o = 0;
        main.CopyTo(data, o); o += main.Length;
        frames.CopyTo(data, o); o += frames.Length;
        list2.CopyTo(data, o); o += list2.Length;
        items.CopyTo(data, o);

        int quantizeBits = seq.Tracks.Where(t => t?.Channels != null).SelectMany(t => t.Channels)
            .Where(c => c != null && c.Type == RdrChannelType.QuantizeFloat).Sum(c => c.FrameBits);
        int refCount = posRefs.Count + rotRefs.Count;
        return new PackedSequence
        {
            Signature = seq.Signature,
            FrameCount = seq.FrameCount,
            Data = data,
            FrameOffset = (uint)main.Length,
            FrameLength = w.FrameLength,
            QuantizeFloatValueBits = (ushort)quantizeBits,
            RootMotionRefCounts = (byte)((posRefs.Count << 4) | rotRefs.Count),
            RootMotionRefsOffset = (uint)(32 + data.Length - refCount * 6),
        };
    }

    private static void WriteHeader(RdrChannel ch, Writer w)
    {
        switch (ch.Type)
        {
            case RdrChannelType.StaticFloat:
                w.Single(ch.Value);
                break;
            case RdrChannelType.QuantizeFloat:
            {
                uint max = 0;
                foreach (float v in ch.Values ?? []) max = Math.Max(max, Quanta(ch, v));
                ch.FrameBits = Math.Max(BitCount(max), 1);
                w.Int32(ch.FrameBits);
                w.Single(ch.Quantum);
                w.Single(ch.Offset);
                break;
            }
            case RdrChannelType.CachedQuaternion1:
            case RdrChannelType.CachedQuaternion2:
                break;
            default:
                throw new NotSupportedException($"channel type {ch.Type} is not produced by the encoder");
        }
    }

    private static uint Quanta(RdrChannel ch, float v) => (uint)((v - ch.Offset) / ch.Quantum + 0.5f);

    private static int BitCount(uint bits)
    {
        int result = 0;
        for (int i = 0; i < 32; i++) if ((bits & (1u << i)) != 0) result = i + 1;
        return result;
    }

    private static List<(byte Type, byte Index, ushort DataOffset, ushort FrameOffset)> RootRefs(RdrSequence seq, int trackId)
    {
        var list = new List<(byte, byte, ushort, ushort)>();
        foreach (var t in seq.Tracks)
        {
            if (t == null || t.Bone.TrackId != trackId || t.Channels == null) continue;
            foreach (var ch in t.Channels)
            {
                if (ch != null) list.Add(((byte)ch.Type, (byte)ch.Index, (ushort)ch.DataOffset, (ushort)ch.FrameOffset));
            }
        }
        list.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
        return list;
    }

    private sealed class Writer
    {
        public readonly MemoryStream Main = new(), List = new(), Items = new();
        private readonly List<uint[]> frames = new();
        private readonly List<uint> frame = new();
        private int frameBit;
        public ushort FrameLength;

        public int MainLength => (int)Main.Length;

        public void ChannelList(ushort c) => List.Write(BitConverter.GetBytes(c));
        public void ChannelItem(ushort c) => Items.Write(BitConverter.GetBytes(c));
        public void Int32(int v) => Main.Write(BitConverter.GetBytes(v));
        public void Single(float v) => Main.Write(BitConverter.GetBytes(v));

        public void ChannelRef((byte Type, byte Index, ushort DataOffset, ushort FrameOffset) r)
        {
            Items.WriteByte(r.Type);
            Items.WriteByte(r.Index);
            Items.Write(BitConverter.GetBytes(r.DataOffset));
            Items.Write(BitConverter.GetBytes(r.FrameOffset));
        }

        public void BeginFrame()
        {
            frame.Clear();
            frameBit = 0;
        }

        /// <summary>Appends n bits (the value is not masked, like the original writer).</summary>
        public void FrameBits(uint bits, int n)
        {
            int shift = frameBit % 32;
            int word = frameBit / 32;
            while (word >= frame.Count) frame.Add(0);
            frame[word] += bits << shift;
            if (shift + n - 32 > 0)
            {
                while (word + 1 >= frame.Count) frame.Add(0);
                frame[word + 1] += bits >> (32 - shift);
            }
            frameBit += n;
        }

        public void EndFrame()
        {
            FrameLength = Math.Max(FrameLength, (ushort)(frame.Count * 4));
            frames.Add(frame.ToArray());
            frame.Clear();
        }

        public byte[] FrameData()
        {
            int words = FrameLength / 4;
            var ms = new MemoryStream();
            foreach (var f in frames)
                for (int j = 0; j < words; j++) ms.Write(BitConverter.GetBytes(j < f.Length ? f[j] : 0u));
            return ms.ToArray();
        }
    }
}
