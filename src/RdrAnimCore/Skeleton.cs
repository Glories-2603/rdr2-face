using System.Numerics;
using System.Text.Json;

namespace RdrAnimCore;

/// <summary>
/// A ped skeleton in rest pose. The GTA V (mp_m_freemode_01) and RDR 2 (meta_base_mp_male) skeletons are embedded
/// as JSON exported from the game models (index, name, tag, parent, local position / rotation xyzw / scale).
/// </summary>
public sealed class Skeleton
{
    public string[] Names;
    public ushort[] Tags;
    public int[] Parent;
    public Quaternion[] RestLocal;
    public Vector3[] RestPos;
    public Quaternion[] RestWorld;
    public Vector3[] RestWorldPos;
    public int[] Order; // parents before children
    public Dictionary<string, int> ByName;

    private static Skeleton gta, rdr;
    public static Skeleton Gta => gta ??= FromResource("gta_skeleton.json");
    public static Skeleton Rdr => rdr ??= FromResource("rdr_skeleton.json");

    private static Skeleton FromResource(string name)
    {
        using var s = typeof(Skeleton).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded {name} missing");
        return FromJson(s);
    }

    public static Skeleton FromJson(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var bones = doc.RootElement.EnumerateArray().ToArray();
        int n = bones.Length;
        var k = new Skeleton
        {
            Names = new string[n], Tags = new ushort[n], Parent = new int[n],
            RestLocal = new Quaternion[n], RestPos = new Vector3[n],
            RestWorld = new Quaternion[n], RestWorldPos = new Vector3[n],
            ByName = new(StringComparer.OrdinalIgnoreCase),
        };
        for (int i = 0; i < n; i++)
        {
            var b = bones[i];
            var pos = b.GetProperty("pos");
            var rot = b.GetProperty("rot");
            k.Names[i] = b.GetProperty("name").GetString();
            k.Tags[i] = (ushort)b.GetProperty("tag").GetInt32();
            k.Parent[i] = b.GetProperty("parent").GetInt32();
            k.RestLocal[i] = Quaternion.Normalize(new Quaternion(rot[0].GetSingle(), rot[1].GetSingle(), rot[2].GetSingle(), rot[3].GetSingle()));
            k.RestPos[i] = new Vector3(pos[0].GetSingle(), pos[1].GetSingle(), pos[2].GetSingle());
            k.ByName.TryAdd(k.Names[i], i);
        }
        k.Order = TopoOrder(k.Parent);
        foreach (int i in k.Order)
        {
            int p = k.Parent[i];
            if (p < 0)
            {
                k.RestWorld[i] = k.RestLocal[i];
                k.RestWorldPos[i] = k.RestPos[i];
            }
            else
            {
                k.RestWorld[i] = Quaternion.Normalize(k.RestWorld[p] * k.RestLocal[i]);
                k.RestWorldPos[i] = k.RestWorldPos[p] + Vector3.Transform(k.RestPos[i], k.RestWorld[p]);
            }
        }
        return k;
    }

    private static int[] TopoOrder(int[] parent)
    {
        var order = new List<int>();
        var done = new bool[parent.Length];
        void Visit(int i)
        {
            if (done[i]) return;
            if (parent[i] >= 0) Visit(parent[i]);
            done[i] = true;
            order.Add(i);
        }
        for (int i = 0; i < parent.Length; i++) Visit(i);
        return order.ToArray();
    }
}
