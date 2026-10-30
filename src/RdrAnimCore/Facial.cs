using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RdrAnimCore;

/// <summary>
/// The facial converter's file formats: a GTA V clip dictionary as JSON (clips + per-frame track values) and an RDR
/// facial clip dictionary from a JSON spec of face control values.
/// </summary>
public static class Facial
{
    /// <summary>spec: {"frames":N,"duration":s,"controls":[{"id":n,"values":[[per frame...]] (or two arrays)}]}</summary>
    public static (byte[] Ycd, FacialClip Clip) BuildFromSpec(string specJson, string dict, string clip)
    {
        using var doc = JsonDocument.Parse(specJson);
        var root = doc.RootElement;
        var fc = new FacialClip { FrameCount = root.GetProperty("frames").GetInt32(), Duration = root.GetProperty("duration").GetSingle() };
        foreach (var c in root.GetProperty("controls").EnumerateArray())
        {
            var values = c.GetProperty("values").EnumerateArray().Select(a => a.EnumerateArray().Select(v => v.GetSingle()).ToArray()).ToList();
            fc.Controls.Add(new FacialControl((ushort)c.GetProperty("id").GetInt32(), values));
        }
        return (Porting.BuildFacialYcd(fc, dict, clip), fc);
    }

    /// <summary>Clip name without the path and ".clip".</summary>
    public static string ShortName(string name)
    {
        string n = name ?? "";
        int slash = n.LastIndexOfAny(['/', ':']);
        if (slash >= 0) n = n[(slash + 1)..];
        return n.EndsWith(".clip", StringComparison.OrdinalIgnoreCase) ? n[..^5] : n;
    }

    /// <summary>Clips and every animation's tracks evaluated per frame, as JSON.</summary>
    public static (string Json, int Clips, int Anims) GtaFaceJson(string path)
    {
        var ycd = GtaYcd.Load(path);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("{\"clips\":[");
        bool first = true;
        foreach (var c in ycd.Clips)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"name\":\"").Append(ShortName(c.Name)).Append("\",\"duration\":")
              .Append((c.MultiDuration ?? (c.Parts.Count > 0 ? (c.Parts[0].End - c.Parts[0].Start) / Math.Max(c.Parts[0].Speed, 1e-6f) : 0)).ToString(inv))
              .Append(",\"parts\":[");
            for (int i = 0; i < c.Parts.Count; i++)
            {
                var p = c.Parts[i];
                sb.Append(i > 0 ? "," : "").Append("{\"anim\":").Append(p.Anim?.Hash ?? 0).Append(",\"start\":").Append(p.Start.ToString(inv))
                  .Append(",\"end\":").Append(p.End.ToString(inv)).Append(",\"speed\":").Append(p.Speed.ToString(inv)).Append('}');
            }
            sb.Append("]}");
        }
        sb.Append("],\"anims\":[");
        first = true;
        foreach (var a in ycd.Anims)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"hash\":").Append(a.Hash).Append(",\"frames\":").Append(a.FrameCount).Append(",\"duration\":")
              .Append(a.Duration.ToString(inv)).Append(",\"tracks\":[");
            for (int ti = 0; ti < a.BoneIds.Length; ti++)
            {
                var b = a.BoneIds[ti];
                int n = b.TypeId == 1 ? 4 : b.TypeId == 0 ? 3 : 1;
                sb.Append(ti > 0 ? "," : "").Append("{\"bone\":").Append(b.BoneId).Append(",\"track\":").Append(b.TrackId)
                  .Append(",\"type\":").Append(b.TypeId).Append(",\"values\":[");
                for (int f = 0; f < a.FrameCount; f++)
                {
                    var v = a.Evaluate(ti, f) ?? System.Numerics.Vector4.Zero;
                    float[] comps = [v.X, v.Y, v.Z, v.W];
                    for (int k = 0; k < n; k++)
                        sb.Append(f > 0 || k > 0 ? "," : "").Append(MathF.Round(comps[k], 5).ToString(inv));
                }
                sb.Append("]}");
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        return (sb.ToString(), ycd.Clips.Count, ycd.Anims.Count);
    }
}
