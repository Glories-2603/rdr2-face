// RdrFaceTool: facebuild <spec.json> <out.ycd> <dict> <clip> | gtaface2json <gta.ycd> <out.json>
// (the same commands as RdrYcdTool's, without CodeX and Windows).
static int Usage()
{
    Console.Error.WriteLine("RdrFaceTool facebuild <spec.json> <out.ycd> <dict> <clip>\nRdrFaceTool gtaface2json <gta.ycd> <out.json>");
    return 1;
}

try
{
    if (args.Length == 5 && args[0] == "facebuild")
    {
        var (ycd, fc) = RdrAnimCore.Facial.BuildFromSpec(File.ReadAllText(args[1]), args[3], args[4]);
        File.WriteAllBytes(args[2], ycd);
        Console.WriteLine($"Wrote {args[2]}: {fc.Controls.Count} controls, {fc.FrameCount} frames, {fc.Duration:0.###} s");
        return 0;
    }
    if (args.Length == 3 && args[0] == "gtaface2json")
    {
        var (json, clips, anims) = RdrAnimCore.Facial.GtaFaceJson(args[1]);
        File.WriteAllText(args[2], json);
        Console.WriteLine($"Wrote {args[2]}: {clips} clips, {anims} anims");
        return 0;
    }
    return Usage();
}
catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
