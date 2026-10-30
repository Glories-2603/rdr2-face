using System.Buffers.Binary;

namespace RdrAnimCore;

/// <summary>
/// Builds an RDR clip dictionary by patching the decompressed image of a game dictionary with one Multi clip
/// (embedded "carrier.bin"). The game crashes on dictionaries whose layout differs from its own builder's, so
/// the original image is kept: new blocks are appended within 64 KB pages and only fields/pointers are patched.
/// </summary>
public sealed class RdrPatcher
{
    public const int PageSize = 0x10000;
    private const ulong VirtualBase = 0x50000000;

    // dictionary root
    private const int RootPageMap = 0x08, DictName = 0x38, DictBuckets = 0x48, DictHash = 0x68;
    // clip dictionary entry
    private const int EntryClipHash = 0x00, EntryDictHash = 0x04, EntryClip = 0x08, EntryNext = 0x10;
    // clip / multi clip
    private const int ClipName = 0x28, MultiParts = 0x68, MultiDuration = 0x78;
    private const int PartClips = 0x00, PartDuration = 0x10;
    private const int SubAnim = 0x00, SubWarp = 0x10, SubStart = 0x28, SubEnd = 0x2C, SubSpeed = 0x30;
    // animation
    private const int AnimFlags = 0x20, AnimFrameCount = 0x24, AnimSequenceSize = 0x26, AnimDuration = 0x28, AnimSignature = 0x2C;
    private const int AnimMaxSeqBlockLength = 0x48, AnimSequences = 0x50, AnimBoneIds = 0x60;

    private readonly List<byte> image;
    private readonly uint version, virtualFlags, physicalFlags;
    private readonly ulong clip, part, sub, anim;

    private static byte[] carrier, faceCarrier;

    /// <summary>Body clips: the embedded carrier.bin (box_put_down).</summary>
    public RdrPatcher() : this(carrier ??= LoadCarrier("carrier.bin")) { }

    /// <summary>Facial clips (face controls, played with SET_FACIAL_IDLE_ANIM_OVERRIDE): carrier_face.bin
    /// (face_human@test@mp_facial, one clip).</summary>
    public static RdrPatcher Face() => new(faceCarrier ??= LoadCarrier("carrier_face.bin"));

    private RdrPatcher(byte[] c)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(c) != 0x43524452) throw new InvalidDataException("bad carrier.bin");
        version = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(4));
        virtualFlags = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(8));
        physicalFlags = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(12));
        int len = BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(16));
        image = new List<byte>(c.AsSpan(20, len).ToArray());

        // root -> first clip entry -> Multi clip -> first part -> first layer -> animation
        foreach (ulong e in Entries())
        {
            clip = U64(e, EntryClip);
            break;
        }
        part = U64(clip, MultiParts);
        sub = U64(part, PartClips);
        anim = U64(sub, SubAnim);
        if (clip == 0 || part == 0 || sub == 0 || anim == 0 || U16(anim, AnimFrameCount) == 0)
            throw new InvalidDataException("carrier.bin is not a dictionary with a Multi clip");
    }

    private static byte[] LoadCarrier(string name)
    {
        using var s = typeof(RdrPatcher).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded {name} missing");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // ---- raw access

    private static int Off(ulong pos) => checked((int)(pos & 0x0FFFFFFF));
    private Span<byte> At(ulong pos, int field, int len) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(image).Slice(Off(pos) + field, len);
    private ushort U16(ulong p, int f) => BinaryPrimitives.ReadUInt16LittleEndian(At(p, f, 2));
    private uint U32(ulong p, int f) => BinaryPrimitives.ReadUInt32LittleEndian(At(p, f, 4));
    private ulong U64(ulong p, int f) => BinaryPrimitives.ReadUInt64LittleEndian(At(p, f, 8));
    private void W16(ulong p, int f, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(At(p, f, 2), v);
    private void W32(ulong p, int f, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(At(p, f, 4), v);
    private void W64(ulong p, int f, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(At(p, f, 8), v);
    private void WF(ulong p, int f, float v) => BinaryPrimitives.WriteSingleLittleEndian(At(p, f, 4), v);

    private IEnumerable<ulong> Entries()
    {
        ulong buckets = U64(VirtualBase, DictBuckets);
        int n = U16(VirtualBase, DictBuckets + 8);
        var list = new List<ulong>();
        for (int i = 0; i < n; i++)
            for (ulong e = U64(buckets, i * 8); e != 0; e = U64(e, EntryNext)) list.Add(e);
        return list;
    }

    private string ReadStr(ulong p, int f)
    {
        ulong s = U64(p, f);
        int len = U16(p, f + 8);
        return s == 0 ? null : System.Text.Encoding.ASCII.GetString(At(s, 0, len));
    }

    /// <summary>Appends a block (16-aligned, never crossing a 64 KB page) and returns its pointer.</summary>
    private ulong Append(byte[] block)
    {
        if (block.Length > PageSize) throw new InvalidDataException($"block of {block.Length} bytes does not fit in a page");
        int start = (image.Count + 15) & ~15;
        if (start / PageSize != (start + block.Length - 1) / PageSize) start = (start / PageSize + 1) * PageSize;
        while (image.Count < start) image.Add(0);
        image.AddRange(block);
        while (image.Count % 16 != 0) image.Add(0);
        return VirtualBase + (ulong)start;
    }

    // ---- edits

    /// <summary>Replaces the animation of the clip with an encoded animation.</summary>
    public void SetAnimation(RdrAnim a)
    {
        var seqPtrs = new ulong[a.Sequences.Count];
        uint maxBlock = 0;
        for (int i = 0; i < a.Sequences.Count; i++)
        {
            byte[] block = RdrPacker.Pack(a.Sequences[i]).ToBlock();
            seqPtrs[i] = Append(block);
            maxBlock = Math.Max(maxBlock, (uint)block.Length);
        }
        ulong seqArr = Append(seqPtrs.SelectMany(BitConverter.GetBytes).ToArray());
        ulong boneArr = Append(a.BoneIds.SelectMany(b => BitConverter.GetBytes(b.BoneId).Concat(new[] { b.TypeId, b.TrackId })).ToArray());

        W16(anim, AnimFlags, a.Flags);
        W16(anim, AnimFrameCount, (ushort)a.FrameCount);
        W16(anim, AnimSequenceSize, a.SequenceSize);
        WF(anim, AnimDuration, a.Duration);
        W32(anim, AnimSignature, a.Signature);
        W64(anim, AnimMaxSeqBlockLength, maxBlock);
        W64(anim, AnimSequences, seqArr);
        W16(anim, AnimSequences + 8, (ushort)a.Sequences.Count);
        W16(anim, AnimSequences + 10, (ushort)a.Sequences.Count);
        W64(anim, AnimBoneIds, boneArr);
        W16(anim, AnimBoneIds + 8, (ushort)a.BoneIds.Length);
        W16(anim, AnimBoneIds + 10, (ushort)a.BoneIds.Length);
    }

    /// <summary>The clip plays only its first animation with the given timing.</summary>
    public void SetTiming(float start, float end, float speed)
    {
        float duration = (end - start) / (speed <= 0 ? 1 : speed);
        WF(sub, SubStart, start);
        WF(sub, SubEnd, end);
        WF(sub, SubSpeed, speed);
        W64(sub, SubWarp, 0);
        W16(sub, SubWarp + 8, 0);
        W16(sub, SubWarp + 10, 0);
        W16(part, PartClips + 8, 1);
        WF(part, PartDuration, duration);
        W16(clip, MultiParts + 8, 1);
        WF(clip, MultiDuration, duration);
    }

    /// <summary>Renames the clip (name string + clip hash of its entry). Call before RenameDict.</summary>
    public void RenameClip(string newName)
    {
        ulong entry = Entries().First(e => U64(e, EntryClip) == clip);
        string full = $"pack:/{newName}.clip";
        var bytes = System.Text.Encoding.ASCII.GetBytes(full + "\0");
        ulong ptr = Append(bytes);
        W64(clip, ClipName, ptr);
        W16(clip, ClipName + 8, (ushort)full.Length);
        W16(clip, ClipName + 10, (ushort)bytes.Length);
        W32(entry, EntryClipHash, Jenk.Hash(newName));
    }

    /// <summary>
    /// New dictionary name: every reference to the name string, every copy of the dictionary hash, and the clip
    /// hash table rebucketed with the game's key ((dictHash &lt;&lt; 32 | clipHash) % n).
    /// </summary>
    public void RenameDict(string newName)
    {
        const ulong root = VirtualBase;
        string oldName = ReadStr(root, DictName);
        ulong oldNamePtr = U64(root, DictName);
        uint oldHash = U32(root, DictHash);
        uint newHash = Jenk.Hash(newName);

        var nameBytes = System.Text.Encoding.ASCII.GetBytes(newName + "\0");
        ulong namePtr = Append(nameBytes);
        W32(root, DictHash, newHash);

        for (int o = 0; o + 12 <= image.Count; o += 8)
        {
            ulong p = VirtualBase + (ulong)o;
            ulong v = U64(p, 0);
            if (v != oldNamePtr && !PointsToString(v, o, oldName)) continue;
            W64(p, 0, namePtr);
            W16(p, 8, (ushort)newName.Length);
            W16(p, 10, (ushort)nameBytes.Length);
        }

        ulong buckets = U64(root, DictBuckets);
        int n = U16(root, DictBuckets + 8);
        var entries = Entries().ToList();
        for (int i = 0; i < n; i++) W64(buckets, i * 8, 0);
        var tails = new ulong[n];
        foreach (ulong e in entries)
        {
            W32(e, EntryDictHash, newHash);
            W64(e, EntryNext, 0);
            ulong key = ((ulong)newHash << 32) | U32(e, EntryClipHash);
            int b = (int)(key % (ulong)n);
            if (tails[b] == 0) W64(buckets, b * 8, e); else W64(tails[b], EntryNext, e);
            tails[b] = e;
        }

        // the same hash in other structures (a second, empty map before the animation dictionary)
        for (int o = 0; o + 4 <= image.Count; o += 4)
        {
            ulong p = VirtualBase + (ulong)o;
            if (U32(p, 0) == oldHash) W32(p, 0, newHash);
        }
    }

    private bool PointsToString(ulong ptr, int o, string text)
    {
        if (text == null || (ptr & 0xF0000000) != VirtualBase || BinaryPrimitives.ReadUInt16LittleEndian(At(VirtualBase, o + 8, 2)) != text.Length) return false;
        int t = Off(ptr);
        if (t + text.Length + 1 > image.Count) return false;
        for (int i = 0; i < text.Length; i++) if (image[t + i] != (byte)text[i]) return false;
        return image[t + text.Length] == 0;
    }

    /// <summary>The finished resource file (RSC8 header, deflate compressed).</summary>
    public byte[] Save()
    {
        // The block map (pointer at +8 of the root) has 16 header bytes plus one 8-byte slot per page that the game
        // fills on load; with more pages than the original it needs more slots, so it is moved to the end.
        ulong oldMap = U64(VirtualBase, RootPageMap);
        int origPages = image[Off(oldMap) + 8];
        int pages = (image.Count + PageSize - 1) / PageSize;
        if (pages != origPages)
        {
            var map = new byte[16 + (pages + 1) * 8];
            image.GetRange(Off(oldMap), 16).ToArray().CopyTo(map, 0);
            ulong newMap = Append(map);
            pages = (image.Count + PageSize - 1) / PageSize;
            W64(VirtualBase, RootPageMap, newMap);
            image[Off(newMap) + 8] = (byte)pages;
        }
        while (image.Count % 16 != 0) image.Add(0);
        byte[] raw = image.ToArray();

        uint vflags = ((uint)raw.Length & 0xFFFFFFF0u) | (virtualFlags & 0xF);
        byte[] packed = Deflate.Compress(raw);
        var file = new byte[16 + packed.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0x38435352);   // "RSC8"
        file[4] = (byte)version;
        file[5] = 0;                                                   // compressor 1 (deflate) - 1
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), vflags);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), physicalFlags);
        packed.CopyTo(file, 16);
        return file;
    }

    /// <summary>Decompressed image (for tests).</summary>
    public byte[] Image => image.ToArray();
}
