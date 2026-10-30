using System.IO.Compression;

namespace RdrAnimCore;

/// <summary>Rockstar's Jenkins one-at-a-time hash (case sensitive, as used for clip and dictionary names).</summary>
public static class Jenk
{
    public static uint Hash(string text)
    {
        if (text == null) return 0;
        uint h = 0;
        for (int i = 0; i < text.Length; i++)
        {
            h += (byte)text[i];
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        return h + (h << 15);
    }
}

internal static class Deflate
{
    /// <summary>Largest decompressed resource accepted (files may come from untrusted users on the server).</summary>
    public const int MaxDecompressed = 256 * 1024 * 1024;

    /// <summary>Raw deflate (no zlib header), as used by RSC7 and by RSC8 compressor 1.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var ds = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = ds.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + n > MaxDecompressed) throw new InvalidDataException("resource too large");
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }

    public static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var ds = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            ds.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }
}

internal static class Names
{
    /// <summary>"pack:/dir/name.clip" -> "name".</summary>
    public static string Short(string name)
    {
        string n = name ?? "";
        int slash = n.LastIndexOfAny(['/', ':']);
        if (slash >= 0) n = n[(slash + 1)..];
        return n.EndsWith(".clip", StringComparison.OrdinalIgnoreCase) ? n[..^5] : n;
    }

    /// <summary>Lower case, [a-z0-9_] only: safe as an RDR dictionary/clip name and in Lua strings.</summary>
    public static string Safe(string n)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in n.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        return sb.ToString().Trim('_') is { Length: > 0 } r ? r : "clip";
    }
}
