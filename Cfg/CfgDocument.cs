using System.Text;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AnnoCfgComposer.Cfg;

/// <summary>
/// AlphaCutout: materials cut out by texture alpha (grass, leaves, vines = shaders 21, 22, 23). Verified against all ~70,000 game materials (2026-10-19).
/// Other materials may store other data in alpha, so they are kept opaque.
/// </summary>
/// Hidden: materials invisible in the game — "[CutOut]" section (CUT_OUT_TERRAIN · CUT_OUT_WATER, shader 5).
/// They are planes that cut away terrain/water under buildings and render as white sheets if drawn (cut_out_plane of the hippodrome, 2026-10-19).
public sealed record CfgMaterial(string Name, string? DiffuseTexture, Color DiffuseColor, bool AlphaCutout = false, double AlphaRef = 0.5, bool Hidden = false);

/// <summary>MODEL in MAIN/Models — one rdm and its materials. A submesh's material index refers to the Materials order.</summary>
public sealed class CfgModel
{
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public Matrix3D Transform { get; init; } = Matrix3D.Identity;
    public List<CfgMaterial> Materials { get; init; } = [];
    public Rect3D? Bounds { get; init; }
}

/// <summary>FILE in MAIN/Files — loads another cfg (props etc.) with position, rotation and scale.</summary>
public sealed class CfgFileRef
{
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public Matrix3D Transform { get; init; } = Matrix3D.Identity;
}

/// <summary>
/// One graphics cfg. Structure of the original kontor_01.cfg:
///   &lt;Config&gt;&lt;ConfigType&gt;MAIN … &lt;Files&gt;(FILE …)&lt;/Files&gt; &lt;Models&gt;(MODEL …)&lt;/Models&gt; &lt;Decals&gt; …
/// Each entry's Transformer has an ORIENTATION_TRANSFORM with Position.xyz · Rotation.xyzw (quaternion) · Scale · NonUniformScale.xyz.
/// </summary>
public sealed class CfgDocument
{
    public CfgNode Root { get; }
    public List<CfgModel> Models { get; } = [];
    public List<CfgFileRef> Files { get; } = [];
    public int DecalCount { get; private set; }

    CfgDocument(CfgNode root) => Root = root;

    public static CfgDocument Parse(byte[] data)
    {
        var doc = new CfgDocument(CfgNode.Parse(Encoding.UTF8.GetString(data)));
        var main = doc.Root.Children.FirstOrDefault(c => c.Name == "Config" && c.ConfigType == "MAIN")
                   ?? doc.Root.Children.FirstOrDefault(c => c.Name == "Config");
        if (main == null) return doc;

        foreach (var c in Configs(main.Child("Files"), "FILE"))
        {
            var fn = c.Value("FileName");
            if (string.IsNullOrWhiteSpace(fn)) continue;
            doc.Files.Add(new CfgFileRef { Name = c.Value("Name") ?? "", FileName = fn, Transform = ReadTransform(c.Child("Transformer")) });
        }

        foreach (var c in Configs(main.Child("Models"), "MODEL"))
        {
            var fn = c.Value("FileName");
            if (string.IsNullOrWhiteSpace(fn)) continue;
            doc.Models.Add(new CfgModel
            {
                Name = c.Value("Name") ?? "",
                FileName = fn,
                Transform = ReadTransform(c.Child("Transformer")),
                Materials = Configs(c.Child("Materials"), "MATERIAL").Select(ReadMaterial).ToList(),
                Bounds = ReadBounds(c),
            });
        }

        doc.DecalCount = Configs(main.Child("Decals"), "DECAL").Count();
        return doc;
    }

    static IEnumerable<CfgNode> Configs(CfgNode? list, string type) =>
        list?.ChildrenNamed("Config").Where(c => c.ConfigType == type) ?? [];

    static CfgMaterial ReadMaterial(CfgNode m)
    {
        string? tex = m.Value("cModelDiffTex");
        if (m.Value("DIFFUSE_ENABLED") == "0") tex = null;
        var color = Color.FromScRgb(1f,
            (float)Math.Clamp(m.Num("cDiffuseColor.r", 1), 0, 1),
            (float)Math.Clamp(m.Num("cDiffuseColor.g", 1), 0, 1),
            (float)Math.Clamp(m.Num("cDiffuseColor.b", 1), 0, 1));
        // Plant materials: the "… [ModelPlant]" section (Wind etc.) exists only on shaders 21 (grass), 22, 23 (leaves), and on all of them.
        // Grass (21) has no Transparency section, so checking only that renders it as beige sheets (seen on the Celtic wall wall_01_o).
        // cShadowAlphaRef (usually 0.2) is the threshold for shadows, so the view cuts at 0.5
        bool cutout = m.Child("cShadowAlphaRef") != null || m.Children.Any(c => c.Name.EndsWith("[ModelPlant]", StringComparison.Ordinal));
        bool hidden = m.Children.Any(c => c.Name.EndsWith("[CutOut]", StringComparison.Ordinal));
        return new CfgMaterial(m.Value("Name") ?? "", string.IsNullOrWhiteSpace(tex) ? null : tex, color, cutout, Hidden: hidden);
    }

    static Rect3D? ReadBounds(CfgNode model)
    {
        var lo = model.Child("AABBMin");
        var hi = model.Child("AABBMax");
        if (lo == null || hi == null) return null;
        var a = new Point3D(lo.Num("x"), lo.Num("y"), lo.Num("z"));
        var b = new Point3D(hi.Num("x"), hi.Num("y"), hi.Num("z"));
        return new Rect3D(a, new Size3D(Math.Max(0, b.X - a.X), Math.Max(0, b.Y - a.Y), Math.Max(0, b.Z - a.Z)));
    }

    /// <summary>
    /// Scale → rotate → translate. WPF Matrix3D uses row vectors (v * M), so combine with the parent as local * parent.
    /// Anything other than ORIENTATION_TRANSFORM (VISIBILITY · ANIMATION · BONELINK …) is ignored in stage 1.
    /// </summary>
    public static Matrix3D ReadTransform(CfgNode? transformer)
    {
        var total = Matrix3D.Identity;
        if (transformer == null) return total;
        foreach (var t in transformer.ChildrenNamed("Config").Where(c => c.ConfigType == "ORIENTATION_TRANSFORM"))
        {
            double s = t.Num("Scale", 1);
            var m = Matrix3D.Identity;
            m.Scale(new Vector3D(s * t.Num("NonUniformScale.x", 1), s * t.Num("NonUniformScale.y", 1), s * t.Num("NonUniformScale.z", 1)));
            var q = new Quaternion(t.Num("Rotation.x"), t.Num("Rotation.y"), t.Num("Rotation.z"), t.Num("Rotation.w", 1));
            if (q.IsNormalized == false && (q.X != 0 || q.Y != 0 || q.Z != 0 || q.W != 0)) q.Normalize();
            if (!q.IsIdentity) m.Rotate(q);
            m.Translate(new Vector3D(t.Num("Position.x"), t.Num("Position.y"), t.Num("Position.z")));
            total = m * total;
        }
        return total;
    }
}
