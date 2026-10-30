using System.Buffers.Binary;
using System.Numerics;

namespace RdrAnimCore;

/// <summary>Bone/track id of an animation track (same layout in GTA V and RDR 2).</summary>
public readonly record struct BoneRef(ushort BoneId, byte TypeId, byte TrackId);

public sealed class GtaAnim
{
    public ulong Position;
    public uint Hash;
    public ushort Flags;
    public int FrameCount;
    public ushort SequenceSize;
    public float Duration;
    public BoneRef[] BoneIds;
    internal GtaSequence[] Sequences;

    /// <summary>Track value at a frame (quaternion tracks: x, y, z, w), or null when the track is missing.</summary>
    public Vector4? Evaluate(int track, int frame)
    {
        if (Sequences == null || Sequences.Length == 0) return null;
        int ss = Math.Max((int)SequenceSize, 1);
        int si = Math.Min(frame / ss, Sequences.Length - 1);
        var seq = Sequences[si];
        int local = Math.Clamp(frame - si * ss, 0, Math.Max(seq.FrameCount - 1, 0));
        var t = track < seq.Tracks.Length ? seq.Tracks[track] : null;
        return t?.Evaluate(local);
    }
}

public sealed class GtaClipPart
{
    public GtaAnim Anim;
    public float Start, End, Speed;
}

public sealed class GtaClip
{
    public string Name;
    public List<GtaClipPart> Parts = new();
    /// <summary>Multi clips have their own duration; single clips use (end - start) / speed.</summary>
    public float? MultiDuration;
}

/// <summary>GTA V clip dictionary (.ycd, RSC7), read without CodeX.</summary>
public sealed class GtaYcd
{
    private const uint Rsc7Magic = 0x37435352;
    private byte[] data;
    private int virtualSize;

    public List<GtaClip> Clips { get; } = new();
    public List<GtaAnim> Anims { get; } = new();

    public static GtaYcd Load(string path) => Load(File.ReadAllBytes(path));

    public static GtaYcd Load(byte[] file)
    {
        var y = new GtaYcd();
        if (file.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Rsc7Magic)
            throw new InvalidDataException("not a GTA V resource (RSC7)");
        uint vflags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        y.virtualSize = SizeFromFlags(vflags);
        y.data = Deflate.Decompress(file.AsSpan(16));
        y.Parse();
        return y;
    }

    private static int SizeFromFlags(uint flags)
    {
        uint n1 = (flags >> 27) & 1;
        uint n2 = ((flags >> 26) & 1) << 1;
        uint n3 = ((flags >> 25) & 1) << 2;
        uint n4 = ((flags >> 24) & 1) << 3;
        uint n5 = ((flags >> 17) & 0x7F) << 4;
        uint n6 = ((flags >> 11) & 0x3F) << 5;
        uint n7 = ((flags >> 7) & 0xF) << 6;
        uint n8 = ((flags >> 5) & 3) << 7;
        uint n9 = ((flags >> 4) & 1) << 8;
        uint n10 = flags & 0xF;
        return (int)((512 << (int)n10) * (n1 + n2 + n3 + n4 + n5 + n6 + n7 + n8 + n9));
    }

    // ---- raw access by resource pointer (0x5xxxxxxx virtual, 0x6xxxxxxx physical)

    private int Off(ulong p)
    {
        if ((p & 0x50000000) == 0x50000000 && (p & 0xF0000000) == 0x50000000) return (int)(p & 0x0FFFFFFF);
        if ((p & 0xF0000000) == 0x60000000) return (int)(p & 0x1FFFFFFF) + virtualSize;
        throw new InvalidDataException($"bad pointer 0x{p:X}");
    }

    private ulong U64(ulong p, int f) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(Off(p) + f));
    private uint U32(ulong p, int f) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Off(p) + f));
    private ushort U16(ulong p, int f) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Off(p) + f));
    private float F32(ulong p, int f) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(Off(p) + f));

    /// <summary>Pointer array (ptr, count, capacity, unknown) -> item pointers.</summary>
    private ulong[] PtrArr(ulong p, int f)
    {
        ulong arr = U64(p, f);
        int count = U16(p, f + 8);
        var r = new ulong[count];
        if (arr == 0) return r;
        for (int i = 0; i < count; i++) r[i] = U64(arr, i * 8);
        return r;
    }

    private string StrA(ulong p, int f)
    {
        ulong s = U64(p, f);
        int len = U16(p, f + 8);
        if (s == 0) return null;
        return System.Text.Encoding.ASCII.GetString(data, Off(s), len).TrimEnd('\0');
    }

    /// <summary>Hash map (bucket array of entries: hash @0, item @8, next @0x10), flattened in bucket order.</summary>
    private List<(uint Hash, ulong Item)> Map(ulong p, int f)
    {
        var list = new List<(uint, ulong)>();
        foreach (ulong head in PtrArr(p, f))
        {
            for (ulong e = head; e != 0; e = U64(e, 0x10)) list.Add((U32(e, 0), U64(e, 8)));
        }
        return list;
    }

    private void Parse()
    {
        const ulong root = 0x50000000;
        var anims = new Dictionary<ulong, GtaAnim>();

        ulong animDict = U64(root, 0x18);
        if (animDict != 0)
        {
            foreach (var (hash, item) in Map(animDict, 0x18))
            {
                if (item == 0 || anims.ContainsKey(item)) continue;
                var a = ReadAnim(item);
                a.Hash = hash;
                anims[item] = a;
                Anims.Add(a);
            }
        }

        foreach (var (_, item) in Map(root, 0x28))
        {
            if (item == 0) continue;
            var clip = new GtaClip { Name = StrA(item, 0x18) ?? "" };
            uint type = U32(item, 0x10);
            if (type == 1) // single
            {
                if (anims.TryGetValue(U64(item, 0x50), out var a))
                    clip.Parts.Add(new GtaClipPart { Anim = a, Start = F32(item, 0x58), End = F32(item, 0x5C), Speed = F32(item, 0x60) });
            }
            else if (type == 2) // multi: list of 24-byte entries
            {
                ulong list = U64(item, 0x50);
                int count = U16(item, 0x58);
                clip.MultiDuration = F32(item, 0x60);
                for (int i = 0; list != 0 && i < count; i++)
                {
                    ulong e = list + (ulong)(i * 24);
                    if (anims.TryGetValue(U64(e, 0x10), out var a))
                        clip.Parts.Add(new GtaClipPart { Anim = a, Start = F32(e, 0), End = F32(e, 4), Speed = F32(e, 8) });
                }
            }
            else continue;
            Clips.Add(clip);
        }
    }

    private GtaAnim ReadAnim(ulong p)
    {
        var a = new GtaAnim
        {
            Position = p,
            Flags = U16(p, 0x10),
            FrameCount = U16(p, 0x14),
            SequenceSize = U16(p, 0x16),
            Duration = F32(p, 0x18),
        };
        ulong bones = U64(p, 0x50);
        int boneCount = U16(p, 0x58);
        a.BoneIds = new BoneRef[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            int o = Off(bones) + i * 4;
            a.BoneIds[i] = new BoneRef(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o)), data[o + 2], data[o + 3]);
        }
        var seqPtrs = PtrArr(p, 0x40);
        a.Sequences = new GtaSequence[seqPtrs.Length];
        for (int i = 0; i < seqPtrs.Length; i++)
        {
            int o = Off(seqPtrs[i]);
            a.Sequences[i] = GtaSequence.Read(data.AsSpan(o));
        }
        return a;
    }
}
