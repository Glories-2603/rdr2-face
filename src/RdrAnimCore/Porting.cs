namespace RdrAnimCore;

/// <summary>A clip found in a GTA V clip dictionary.</summary>
public sealed record GtaClipInfo(string File, string Clip, float Duration);

/// <summary>One facial control of a facial clip: control id and per-frame values (1 array, or 2 for x/y controls).</summary>
public sealed record FacialControl(ushort Id, List<float[]> Values);

/// <summary>A facial animation in RDR face controls.</summary>
public sealed class FacialClip
{
    public int FrameCount;
    public float Duration;
    public List<FacialControl> Controls { get; } = new();
}

/// <summary>A ported animation: RDR dictionary and clip names to play it with.</summary>
public sealed record PortedAnim(string Dict, string Clip, string Source, float Duration);

public sealed class PortJob
{
    public List<GtaClipInfo> Clips { get; } = new();
    /// <summary>Folder the .ycd files are written to.</summary>
    public string OutputDir { get; set; }
    /// <summary>Dictionary name = prefix + clip name.</summary>
    public string DictPrefix { get; set; } = "gta@";
}

/// <summary>GTA V -> RDR 2 porting, entry points for front ends. Needs nothing but .NET.</summary>
public static class Porting
{
    public static List<GtaClipInfo> ListClips(string gtaYcdPath)
    {
        var ycd = GtaYcd.Load(gtaYcdPath);
        return ycd.Clips.Select(c =>
        {
            float duration = c.MultiDuration ?? (c.Parts.Count > 0 ? (c.Parts[0].End - c.Parts[0].Start) / (c.Parts[0].Speed <= 0 ? 1 : c.Parts[0].Speed) : 0);
            return new GtaClipInfo(gtaYcdPath, Names.Short(c.Name), duration);
        }).ToList();
    }

    /// <summary>Name of the RDR clip a GTA clip is ported to.</summary>
    public static string RdrClipName(string gtaClip) => Names.Safe(gtaClip);

    /// <summary>One ported clip as a finished RDR .ycd file.</summary>
    public static byte[] BuildYcd(PortedClip clip, string dictName, string clipName)
    {
        var anim = RdrEncoder.Encode(clip.Anim);
        var p = new RdrPatcher();
        p.SetAnimation(anim);
        p.SetTiming(clip.Start, clip.End, clip.Speed);
        p.RenameClip(clipName);
        p.RenameDict(dictName);
        return p.Save();
    }

    /// <summary>
    /// A facial clip (face controls, track 25) as a finished RDR .ycd: one value per frame for float controls, two for
    /// 2-component ones. Played with SET_FACIAL_IDLE_ANIM_OVERRIDE(ped, clip, dict).
    /// </summary>
    public static byte[] BuildFacialYcd(FacialClip clip, string dictName, string clipName)
    {
        string animName = $"pack:/face_{clipName}.anim";
        var src = new RdrAnimSource
        {
            Name = animName, Flags = 256, FrameCount = clip.FrameCount, Duration = clip.Duration, Signature = Jenk.Hash(animName),
        };
        foreach (var c in clip.Controls)
        {
            src.Tracks.Add(new RdrTrackSource
            {
                Bone = new BoneRef(c.Id, (byte)(c.Values.Count == 2 ? 3 : 2), 25),
                Channels = c.Values,
            });
        }
        var p = RdrPatcher.Face();
        p.SetAnimation(RdrEncoder.Encode(src));
        p.SetTiming(0, clip.Duration, 1);
        p.RenameClip(clipName);
        p.RenameDict(dictName);
        return p.Save();
    }

    /// <summary>
    /// Ports the clips of the job. progress(done, total, message) is called after every clip; errors of single
    /// clips are reported through progress ("ERROR ...") and skipped.
    /// </summary>
    public static List<PortedAnim> Port(PortJob job, Action<int, int, string> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(job.OutputDir);
        var rows = new List<PortedAnim>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = job.Clips.Count, done = 0;

        foreach (var group in job.Clips.GroupBy(c => c.File, StringComparer.OrdinalIgnoreCase))
        {
            cancel.ThrowIfCancellationRequested();
            string file = group.Key;
            var wanted = group.Select(c => c.Clip).ToHashSet(StringComparer.OrdinalIgnoreCase);
            progress(done, total, $"{Path.GetFileName(file)}: {wanted.Count}");

            List<PortedClip> ported;
            try
            {
                ported = Retarget.Port(GtaYcd.Load(file), wanted.Contains);
            }
            catch (Exception ex)
            {
                done += wanted.Count;
                progress(done, total, $"ERROR {Path.GetFileName(file)}: {ex.Message}");
                continue;
            }

            var byName = new Dictionary<string, PortedClip>(StringComparer.OrdinalIgnoreCase);
            foreach (var pc in ported) byName.TryAdd(pc.ClipName, pc);
            foreach (string clipName in wanted)
            {
                cancel.ThrowIfCancellationRequested();
                done++;
                if (!byName.TryGetValue(clipName, out var src))
                {
                    progress(done, total, $"ERROR {clipName}: no animation data");
                    continue;
                }
                string baseName = Names.Safe(clipName), name = baseName;
                for (int i = 2; !used.Add(name); i++) name = $"{baseName}_{i}";
                string dict = job.DictPrefix + name;
                try
                {
                    File.WriteAllBytes(Path.Combine(job.OutputDir, dict + ".ycd"), BuildYcd(src, dict, name));
                    float speed = src.Speed <= 0 ? 1 : src.Speed;
                    rows.Add(new PortedAnim(dict, name, Path.GetFileNameWithoutExtension(file), (src.End - src.Start) / speed));
                    progress(done, total, $"OK {name}");
                }
                catch (Exception ex)
                {
                    progress(done, total, $"ERROR {name}: {ex.Message}");
                }
            }
        }
        return rows;
    }
}
