using System.Buffers.Binary;
using System.Numerics;

namespace RdrAnimCore;

internal enum GtaChannelType
{
    StaticQuaternion, StaticVector3, StaticFloat, RawFloat, QuantizeFloat, IndirectQuantizeFloat, LinearFloat,
    CachedQuaternion1, CachedQuaternion2,
}

/// <summary>One decoded channel of a GTA V animation sequence.</summary>
internal sealed class GtaChannel
{
    public GtaChannelType Type;
    public int Track, Index, QuatIndex, FrameBits;
    public float Quantum, Offset;
    public Vector4 Static;       // static quaternion / vector3 / float (X)
    public float[] Values;       // per frame (raw, quantize, linear) or value table (indirect)
    public uint[] Frames;        // indirect: value index per frame
    public int ValueBits, NumInts;

    public float EvaluateFloat(int frame) => Type switch
    {
        GtaChannelType.StaticFloat => Static.X,
        GtaChannelType.RawFloat or GtaChannelType.QuantizeFloat or GtaChannelType.LinearFloat =>
            Values == null ? 0f : Values[frame % Values.Length],
        GtaChannelType.IndirectQuantizeFloat => Values == null ? 0f : Values[Frames[frame % Frames.Length]],
        _ => 0f,
    };
}

internal sealed class GtaTrack
{
    public int ChannelCount;
    public GtaChannel[] Channels;

    /// <summary>Combines the channels of a track into a vector (quaternion tracks rebuild their cached component).</summary>
    public Vector4 Evaluate(int frame)
    {
        Vector4 v = Vector4.Zero;
        int c = 0;
        for (int i = 0; i < ChannelCount; i++)
        {
            var ch = Channels[i];
            if (ch == null) continue;
            switch (ch.Type)
            {
                case GtaChannelType.StaticFloat:
                case GtaChannelType.RawFloat:
                case GtaChannelType.QuantizeFloat:
                case GtaChannelType.IndirectQuantizeFloat:
                case GtaChannelType.LinearFloat:
                    v[c] = ch.EvaluateFloat(frame);
                    c++;
                    break;
                case GtaChannelType.StaticVector3:
                    if (c == 0) { v.X = ch.Static.X; v.Y = ch.Static.Y; v.Z = ch.Static.Z; }
                    else if (c == 1) { v.Y = ch.Static.X; v.Z = ch.Static.Y; v.W = ch.Static.Z; }
                    c += 3;
                    break;
                case GtaChannelType.StaticQuaternion:
                    if (c == 0) v = ch.Static;
                    c += 4;
                    break;
                case GtaChannelType.CachedQuaternion1:
                case GtaChannelType.CachedQuaternion2:
                    if (c == 3)
                    {
                        v.W = (float)Math.Sqrt(Math.Max(1f - (v.X * v.X + v.Y * v.Y + v.Z * v.Z), 0.0));
                        v = ch.QuatIndex switch
                        {
                            0 => new Vector4(v.W, v.X, v.Y, v.Z),
                            1 => new Vector4(v.X, v.W, v.Y, v.Z),
                            2 => new Vector4(v.X, v.Y, v.W, v.Z),
                            _ => v,
                        };
                    }
                    c++;
                    break;
            }
        }
        return v;
    }
}

/// <summary>GTA V animation sequence: header + packed channel data (decoded like CodeX's Rsc7AnimSequence).</summary>
internal sealed class GtaSequence
{
    public ushort FrameCount;
    public GtaTrack[] Tracks;

    public static GtaSequence Read(ReadOnlySpan<byte> block)
    {
        uint dataLength = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(4));
        uint frameOffset = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(12));
        ushort frameCount = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(22));
        ushort frameLength = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(24));
        byte chunkSize = block[30];
        byte[] data = block.Slice(32, (int)dataLength).ToArray();

        var r = new Reader(data, frameCount, chunkSize, frameOffset, frameLength);
        const int typeCount = 9;
        var byType = new GtaChannel[typeCount][];
        int maxTrack = 0;
        for (int t = 0; t < typeCount; t++)
        {
            int count = r.ReadChannelCount();
            if (count == 0) continue;
            var list = new GtaChannel[count];
            for (int j = 0; j < count; j++)
            {
                ushort bits = r.ReadChannelDataBits();
                var ch = new GtaChannel { Type = (GtaChannelType)t };
                ReadHeader(ch, r);
                ch.Track = bits >> 2;
                ch.Index = bits & 3;
                if (ch.Type is GtaChannelType.CachedQuaternion1 or GtaChannelType.CachedQuaternion2)
                {
                    ch.QuatIndex = ch.Index;
                    ch.Index = ch.Type == GtaChannelType.CachedQuaternion1 ? 3 : 4;
                }
                list[j] = ch;
                maxTrack = Math.Max(maxTrack, ch.Track);
            }
            r.AlignChannelDataOffset(count);
            byType[t] = list;
        }

        for (int f = 0; f < frameCount; f++)
        {
            r.BeginFrame(f);
            foreach (var list in byType)
            {
                if (list == null) continue;
                foreach (var ch in list) ReadFrame(ch, r);
            }
        }

        var tracks = new GtaTrack[maxTrack + 1];
        foreach (var list in byType)
        {
            if (list == null) continue;
            foreach (var ch in list)
            {
                var t = tracks[ch.Track] ??= new GtaTrack();
                t.ChannelCount = Math.Max(t.ChannelCount, ch.Index + 1);
            }
        }
        foreach (var list in byType)
        {
            if (list == null) continue;
            foreach (var ch in list)
            {
                var t = tracks[ch.Track];
                t.Channels ??= new GtaChannel[t.ChannelCount];
                t.Channels[ch.Index] = ch;
            }
        }
        return new GtaSequence { FrameCount = frameCount, Tracks = tracks };
    }

    private static void ReadHeader(GtaChannel ch, Reader r)
    {
        switch (ch.Type)
        {
            case GtaChannelType.StaticQuaternion:
            {
                var v = r.ReadVector3();
                ch.Static = new Vector4(v, MathF.Sqrt(Math.Max(1f - v.LengthSquared(), 0f)));
                break;
            }
            case GtaChannelType.StaticVector3:
                ch.Static = new Vector4(r.ReadVector3(), 0);
                break;
            case GtaChannelType.StaticFloat:
                ch.Static = new Vector4(r.ReadSingle(), 0, 0, 0);
                break;
            case GtaChannelType.RawFloat:
                ch.FrameBits = 32;
                ch.Values = new float[r.NumFrames];
                break;
            case GtaChannelType.QuantizeFloat:
                ch.FrameBits = r.ReadInt32();
                ch.Quantum = r.ReadSingle();
                ch.Offset = r.ReadSingle();
                ch.Values = new float[r.NumFrames];
                break;
            case GtaChannelType.IndirectQuantizeFloat:
            {
                ch.FrameBits = r.ReadInt32();
                ch.ValueBits = r.ReadInt32();
                ch.NumInts = r.ReadInt32();
                ch.Quantum = r.ReadSingle();
                ch.Offset = r.ReadSingle();
                ch.Frames = new uint[r.NumFrames];
                int n = ch.NumInts * 32 / ch.ValueBits;
                uint maxIndex = (uint)((1 << ch.FrameBits) - 1);
                long count = Math.Min(n, maxIndex);
                ch.Values = new float[count];
                r.BitPosition = r.Position * 8;
                for (int i = 0; i < count; i++) ch.Values[i] = (float)r.ReadBits(ch.ValueBits) * ch.Quantum + ch.Offset;
                r.Position += ch.NumInts * 4;
                break;
            }
            case GtaChannelType.LinearFloat:
                ReadLinear(ch, r);
                break;
        }
    }

    private static void ReadLinear(GtaChannel ch, Reader r)
    {
        int numInts = r.ReadInt32();
        int counts = r.ReadInt32();
        ch.Quantum = r.ReadSingle();
        ch.Offset = r.ReadSingle();
        int bit = r.Position * 8;
        int count1 = counts & 0xFF, count2 = (counts >> 8) & 0xFF, count3 = (counts >> 16) & 0xFF;
        int dataBits = (r.Data?.Length ?? 0) * 8;
        ushort numFrames = r.NumFrames;
        byte chunkSize = r.ChunkSize;
        ushort chunks = (ushort)((chunkSize + numFrames - 1) / chunkSize);
        int deltaStart = bit + chunks * (count1 + count2);
        r.BitPosition = bit;
        var offsets = new int[chunks];
        var bases = new int[chunks];
        var values = new float[numFrames];
        for (int i = 0; i < chunks; i++) offsets[i] = (int)(count1 > 0 ? r.ReadBits(count1) : 0);
        for (int i = 0; i < chunks; i++) bases[i] = (int)(count2 > 0 ? r.ReadBits(count2) : 0);
        for (int k = 0; k < chunks; k++)
        {
            r.BitPosition = offsets[k] + deltaStart;
            int value = bases[k];
            int first = k * chunkSize;
            int delta = 0;
            for (int l = 0; l < chunkSize; l++)
            {
                int f = first + l;
                if (f >= numFrames) break;
                values[f] = (float)value * ch.Quantum + ch.Offset;
                if (l + 1 >= chunkSize) break;
                int d = (int)(count3 != 0 ? r.ReadBits(count3) : 0);
                int start = r.BitPosition;
                uint one = 0;
                while (one == 0)
                {
                    one = r.ReadBits(1);
                    if (r.BitPosition >= dataBits) break;
                }
                d |= r.BitPosition - start - 1 << count3;
                if (d != 0 && r.ReadBits(1) == 1) d = -d;
                delta += d;
                value += delta;
            }
        }
        ch.Values = values;
        r.Position -= 16;
        r.Position += numInts * 4;
    }

    private static void ReadFrame(GtaChannel ch, Reader r)
    {
        switch (ch.Type)
        {
            case GtaChannelType.RawFloat:
                ch.Values[r.Frame] = BitConverter.UInt32BitsToSingle(r.ReadFrameBits(32));
                break;
            case GtaChannelType.QuantizeFloat:
                ch.Values[r.Frame] = (float)r.ReadFrameBits(ch.FrameBits) * ch.Quantum + ch.Offset;
                break;
            case GtaChannelType.IndirectQuantizeFloat:
                ch.Frames[r.Frame] = r.ReadFrameBits(ch.FrameBits);
                break;
        }
    }

    /// <summary>Bit and byte reader of a sequence data block (CodeX Rsc7AnimSequenceReader).</summary>
    private sealed class Reader
    {
        public readonly byte[] Data;
        public readonly ushort NumFrames;
        public readonly byte ChunkSize;
        public int Position, Frame, BitPosition;
        private readonly uint frameOffset;
        private readonly ushort frameLength;
        private int channelListOffset, channelDataOffset, channelFrameOffset;

        public Reader(byte[] data, ushort numFrames, byte chunkSize, uint frameOffset, ushort frameLength)
        {
            Data = data;
            NumFrames = numFrames;
            ChunkSize = chunkSize;
            this.frameOffset = frameOffset;
            this.frameLength = frameLength;
            channelListOffset = (int)frameOffset + frameLength * numFrames;
            channelDataOffset = channelListOffset + 18;
        }

        public int ReadInt32() { int v = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(Position)); Position += 4; return v; }
        public float ReadSingle() { float v = BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan(Position)); Position += 4; return v; }
        public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());

        private uint GetBits(int startBit, int length)
        {
            if (startBit < 0) return 0;
            int bytePos = startBit / 8;
            int shift = -(startBit % 8);
            uint result = 0;
            int remaining = length;
            while (remaining > 0)
            {
                uint b = bytePos < Data.Length ? Data[bytePos++] : 0u;
                uint shifted = shift < 0 ? b >> -shift : b << shift;
                uint mask = (uint)((1 << Math.Min(remaining, 8)) - 1 << Math.Max(shift, 0));
                result += shifted & mask;
                remaining -= 8 + Math.Min(shift, 0);
                shift += 8;
            }
            return result;
        }

        public uint ReadBits(int length)
        {
            if (length == 0) return 0;
            uint v = GetBits(BitPosition, length);
            BitPosition += length;
            return v;
        }

        public ushort ReadChannelCount() { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(channelListOffset)); channelListOffset += 2; return v; }
        public ushort ReadChannelDataBits() { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(channelDataOffset)); channelDataOffset += 2; return v; }

        public void AlignChannelDataOffset(int channelCount)
        {
            int rem = channelCount % 4;
            if (rem > 0) channelDataOffset += (4 - rem) * 2;
        }

        public void BeginFrame(int f)
        {
            Frame = f;
            channelFrameOffset = (int)((frameOffset + frameLength * f) * 8);
        }

        public uint ReadFrameBits(int n)
        {
            uint v = GetBits(channelFrameOffset, n);
            channelFrameOffset += n;
            return v;
        }
    }
}
