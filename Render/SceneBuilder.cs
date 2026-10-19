using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AnnoCfgComposer.Cfg;
using AnnoCfgComposer.Game;
using AnnoCfgComposer.Rdm;
using HelixToolkit;
using DxMesh = HelixToolkit.SharpDX.MeshGeometry3D;

namespace AnnoCfgComposer.Render;

public sealed class SceneOptions
{
    /// <summary>
    /// Treats Anno as a left-handed coordinate system and flips X for display (same direction as rdm4's glTF conversion).
    /// Turn it off if the view looks mirrored compared to the game — not yet verified against the game.
    /// </summary>
    public bool Mirror { get; init; } = true;
    public bool Textures { get; init; } = true;
}

/// <summary>One item in the right-hand [Structure] tree — a prop cfg or a model. A prop cfg has its own structure below it.</summary>
public sealed class PartNode
{
    static readonly Brush CfgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB)));
    static readonly Brush ModelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x5C, 0xD6)));

    public required bool IsCfg { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public List<PartNode> Children { get; } = [];
    public bool IsExpanded { get; set; }

    public string Label => string.IsNullOrWhiteSpace(Name) ? System.IO.Path.GetFileName(Path) : Name;
    public string Icon => IsCfg ? "" : "";   // document · box (Segoe icon font)
    public Brush IconBrush => IsCfg ? CfgBrush : ModelBrush;

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}

/// <summary>
/// Scene tree mirroring the cfg structure (for glTF export). Local is in Anno coordinates relative to the parent (before mirroring).
/// Model nodes have Mesh and Materials; prop cfg nodes only have Children.
/// </summary>
public sealed class SceneNode
{
    public required string Name { get; init; }
    public Matrix3D Local { get; init; } = Matrix3D.Identity;
    public List<SceneNode> Children { get; } = [];
    public string? RdmPath { get; set; }
    public RdmMesh? Mesh { get; set; }
    public IReadOnlyList<CfgMaterial> Materials { get; init; } = [];
}

/// <summary>One material (for display). TexturePath is the path of the file actually found (.dds etc.).</summary>
public sealed record DrawMaterial(string? TexturePath, Color Color, bool Cutout, bool Placeholder = false);

/// <summary>
/// Draw batch = same rdm submesh + same material. One matrix per placement → drawn in one go with GPU instancing.
/// Dozens of baskets still make a single batch.
/// </summary>
public sealed class DrawBatch
{
    public required DxMesh Geometry { get; init; }
    public required DrawMaterial Material { get; init; }
    public List<Matrix4x4> Instances { get; } = [];
}

/// <summary>
/// One cfg → list of draw batches (recursing into prop cfgs). Built on a background thread.
/// Visual elements (Viewport3DX Element3D) are created by DxScene on the UI thread.
/// </summary>
public sealed class SceneBuilder(FileResolver files, SceneOptions options)
{
    const int MaxDepth = 16;
    const int MaxParts = 5000;
    int partCount;

    readonly Dictionary<string, CfgDocument?> cfgCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, MeshEntry> meshCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string?> textureCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DrawBatch> batches = new(StringComparer.Ordinal);
    readonly Dictionary<string, DxMesh> boxCache = new(StringComparer.Ordinal);

    sealed record MeshEntry(RdmMesh? Mesh, DxMesh[]? Geometry);

    public int CfgCount { get; private set; }
    public int ModelCount { get; private set; }
    public int TriangleCount { get; private set; }
    public SortedSet<string> MissingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedSet<string> MissingTextures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedDictionary<string, string> BrokenFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedSet<string> SkippedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PartNode> Parts { get; } = [];
    public SceneNode? Scene { get; private set; }
    public int DecalCount { get; private set; }

    public IReadOnlyCollection<DrawBatch> Batches => batches.Values;
    /// <summary>Overall bounds in display coordinates (after mirroring).</summary>
    public Rect3D Bounds { get; private set; } = Rect3D.Empty;
    /// <summary>Texture files actually used → contents (uploaded to the GPU as is).</summary>
    public Dictionary<string, byte[]> TextureData { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Build(string cfgPath, CancellationToken ct)
    {
        Scene = new SceneNode { Name = System.IO.Path.GetFileNameWithoutExtension(cfgPath) };
        AddCfg(cfgPath, Matrix3D.Identity, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), Parts, Scene, ct);

        // Only alpha-cutout textures are rebuilt with alpha cut to 0/255 (in parallel). Opaque textures stay as dds.
        var cut = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(TextureData.ToList(), new ParallelOptions { CancellationToken = ct }, kv =>
        {
            try { if (DdsTexture.CutoutDds(kv.Value, 1024, 0.5) is { } c) cut[kv.Key] = c; }
            catch { }
        });
        foreach (var kv in cut) TextureData[kv.Key] = kv.Value;
        CutoutTextureCount = cut.Count;
    }

    /// <summary>Number of textures rebuilt with cut alpha (the rest use the original dds).</summary>
    public int CutoutTextureCount { get; private set; }

    void AddCfg(string path, Matrix3D world, int depth, HashSet<string> stack, List<PartNode> parts, SceneNode scene, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > MaxDepth || !stack.Add(path)) return;
        var doc = LoadCfg(path);
        if (doc == null) { stack.Remove(path); return; }
        CfgCount++;
        DecalCount += doc.DecalCount;

        foreach (var m in doc.Models)
        {
            AddPart(parts, false, m.Name, m.FileName);
            var sn = new SceneNode { Name = m.Name, Local = m.Transform, Materials = m.Materials };
            scene.Children.Add(sn);
            AddModel(m, m.Transform * world, sn);
        }
        foreach (var f in doc.Files)
        {
            if (!f.FileName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            {
                SkippedFiles.Add(GameFiles.Normalize(f.FileName));
                continue;
            }
            var node = AddPart(parts, true, f.Name, f.FileName);
            var child = new SceneNode { Name = f.Name, Local = f.Transform };
            scene.Children.Add(child);
            AddCfg(GameFiles.Normalize(f.FileName), f.Transform * world, depth + 1, stack, node?.Children ?? [], child, ct);
        }
        stack.Remove(path);
    }

    PartNode? AddPart(List<PartNode> parts, bool isCfg, string name, string path)
    {
        if (++partCount > MaxParts) return null;
        var n = new PartNode { IsCfg = isCfg, Name = name, Path = GameFiles.Normalize(path) };
        parts.Add(n);
        return n;
    }

    CfgDocument? LoadCfg(string path)
    {
        if (cfgCache.TryGetValue(path, out var doc)) return doc;
        try
        {
            var data = files.Read(path);
            if (data == null) MissingFiles.Add(path);
            doc = data == null ? null : CfgDocument.Parse(data);
        }
        catch (Exception ex)
        {
            BrokenFiles[path] = ex.Message;
            doc = null;
        }
        cfgCache[path] = doc;
        return doc;
    }

    void AddModel(CfgModel model, Matrix3D world, SceneNode scene)
    {
        ModelCount++;
        var display = ToDisplay(world);
        var m4 = ToMatrix4x4(display);

        var rdmPath = files.Resolve(model.FileName);
        var entry = rdmPath == null ? null : LoadMesh(rdmPath);
        if (rdmPath == null) MissingFiles.Add(GameFiles.Normalize(model.FileName));

        if (entry?.Mesh is { } mesh && entry.Geometry is { } geos)
        {
            scene.RdmPath = rdmPath;
            scene.Mesh = mesh;
            for (int i = 0; i < geos.Length; i++)
            {
                var sub = mesh.Submeshes[i];
                var cm = sub.Material < model.Materials.Count ? model.Materials[sub.Material] : null;
                if (cm?.Hidden == true) continue;   // Terrain / water cutout planes (invisible in the game)
                var mat = GetMaterial(cm);
                var batch = GetBatch($"{rdmPath}#{i}#{mat}", geos[i], mat);
                batch.Instances.Add(m4);
                Grow(geos[i], display);
                TriangleCount += sub.Count / 3;
            }
            return;
        }

        // If the model can't be read, show the cfg's AABB as a semi-transparent orange box
        if (model.Bounds is { } box && !box.IsEmpty)
        {
            var r = options.Mirror ? new Rect3D(-box.X - box.SizeX, box.Y, box.Z, box.SizeX, box.SizeY, box.SizeZ) : box;
            string key = r.ToString();
            if (!boxCache.TryGetValue(key, out var geo)) boxCache[key] = geo = Box(r);
            var mat = new DrawMaterial(null, Color.FromArgb(110, 255, 140, 0), false, Placeholder: true);
            GetBatch("box#" + key, geo, mat).Instances.Add(m4);
            Grow(geo, display);
        }
    }

    DrawBatch GetBatch(string key, DxMesh geo, DrawMaterial mat)
    {
        if (!batches.TryGetValue(key, out var b)) batches[key] = b = new DrawBatch { Geometry = geo, Material = mat };
        return b;
    }

    void Grow(DxMesh geo, Matrix3D display)
    {
        var bb = geo.Bound;
        for (int c = 0; c < 8; c++)
        {
            var p = new Point3D((c & 1) == 0 ? bb.Minimum.X : bb.Maximum.X, (c & 2) == 0 ? bb.Minimum.Y : bb.Maximum.Y, (c & 4) == 0 ? bb.Minimum.Z : bb.Maximum.Z);
            var w = display.Transform(p);
            var r = new Rect3D(w, new Size3D());
            if (Bounds.IsEmpty) Bounds = r; else Bounds = Rect3D.Union(Bounds, r);
        }
    }

    /// <summary>Mirror F = diag(-1,1,1). Display matrix = F·M·F (mesh coordinates are flipped by F as well).</summary>
    Matrix3D ToDisplay(Matrix3D m)
    {
        if (!options.Mirror) return m;
        var f = new Matrix3D(-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        return f * m * f;
    }

    // WPF Matrix3D and System.Numerics.Matrix4x4 both use row vectors (v·M) — copy the elements as is
    static Matrix4x4 ToMatrix4x4(Matrix3D m) => new(
        (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
        (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
        (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
        (float)m.OffsetX, (float)m.OffsetY, (float)m.OffsetZ, (float)m.M44);

    MeshEntry LoadMesh(string path)
    {
        if (meshCache.TryGetValue(path, out var e)) return e;
        try
        {
            var data = files.Read(path) ?? throw new FileNotFoundException(path);
            var mesh = RdmMesh.Parse(data);
            e = new MeshEntry(mesh, ToGeometry(mesh));
        }
        catch (Exception ex)
        {
            BrokenFiles[path] = ex.Message;
            e = new MeshEntry(null, null);
        }
        meshCache[path] = e;
        return e;
    }

    /// <summary>rdm → one DX mesh per submesh. When mirrored, X is flipped and the winding order reversed.</summary>
    DxMesh[] ToGeometry(RdmMesh mesh)
    {
        float sx = options.Mirror ? -1 : 1;
        var result = new DxMesh[mesh.Submeshes.Length];
        for (int s = 0; s < result.Length; s++)
        {
            var sub = mesh.Submeshes[s];
            // Collect only the vertices a submesh uses and renumber them (one rdm has tens of thousands of vertices — copying all of them per submesh is wasteful)
            var remap = new Dictionary<int, int>();
            var pos = new Vector3Collection();
            var nrm = mesh.Normals != null ? new Vector3Collection() : null;
            var uv = mesh.Uvs != null ? new Vector2Collection() : null;
            var idx = new IntCollection(sub.Count);
            int Map(int v)
            {
                if (remap.TryGetValue(v, out int n)) return n;
                n = pos.Count;
                remap[v] = n;
                var p = mesh.Positions[v];
                pos.Add(new Vector3(sx * (float)p.X, (float)p.Y, (float)p.Z));
                if (nrm != null)
                {
                    var q = mesh.Normals![v];
                    var nv = new Vector3(sx * (float)q.X, (float)q.Y, (float)q.Z);
                    nrm.Add(nv.LengthSquared() > 1e-8f ? Vector3.Normalize(nv) : Vector3.UnitY);
                }
                if (uv != null) uv.Add(new Vector2((float)mesh.Uvs![v].X, (float)mesh.Uvs[v].Y));
                return n;
            }
            for (int i = sub.Start; i + 2 < sub.Start + sub.Count; i += 3)
            {
                int a = Map(mesh.Indices[i]), b = Map(mesh.Indices[i + 1]), c = Map(mesh.Indices[i + 2]);
                idx.Add(a);
                if (options.Mirror) { idx.Add(c); idx.Add(b); } else { idx.Add(b); idx.Add(c); }
            }
            var g = new DxMesh { Positions = pos, Indices = idx };
            if (nrm != null) g.Normals = nrm;
            if (uv != null) g.TextureCoordinates = uv;
            g.UpdateBounds();
            result[s] = g;
        }
        return result;
    }

    static DxMesh Box(Rect3D r)
    {
        var lo = new Vector3((float)r.X, (float)r.Y, (float)r.Z);
        var hi = new Vector3((float)(r.X + r.SizeX), (float)(r.Y + r.SizeY), (float)(r.Z + r.SizeZ));
        var pos = new Vector3Collection();
        var nrm = new Vector3Collection();
        var idx = new IntCollection();
        void Face(Vector3 n, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int o = pos.Count;
            pos.Add(a); pos.Add(b); pos.Add(c); pos.Add(d);
            for (int i = 0; i < 4; i++) nrm.Add(n);
            idx.Add(o); idx.Add(o + 1); idx.Add(o + 2); idx.Add(o); idx.Add(o + 2); idx.Add(o + 3);
        }
        Face(-Vector3.UnitX, new(lo.X, lo.Y, lo.Z), new(lo.X, lo.Y, hi.Z), new(lo.X, hi.Y, hi.Z), new(lo.X, hi.Y, lo.Z));
        Face(Vector3.UnitX, new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, lo.Y, hi.Z));
        Face(-Vector3.UnitY, new(lo.X, lo.Y, lo.Z), new(hi.X, lo.Y, lo.Z), new(hi.X, lo.Y, hi.Z), new(lo.X, lo.Y, hi.Z));
        Face(Vector3.UnitY, new(lo.X, hi.Y, lo.Z), new(lo.X, hi.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, hi.Y, lo.Z));
        Face(-Vector3.UnitZ, new(lo.X, lo.Y, lo.Z), new(lo.X, hi.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, lo.Y, lo.Z));
        Face(Vector3.UnitZ, new(lo.X, lo.Y, hi.Z), new(hi.X, lo.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(lo.X, hi.Y, hi.Z));
        var g = new DxMesh { Positions = pos, Normals = nrm, Indices = idx };
        g.UpdateBounds();
        return g;
    }

    DrawMaterial GetMaterial(CfgMaterial? m)
    {
        var color = m?.DiffuseColor ?? Colors.White;
        string? tex = options.Textures && m?.DiffuseTexture != null ? GetTexture(m.DiffuseTexture) : null;
        bool cutout = tex != null && m!.AlphaCutout;
        // Without a texture, multiply a light stone color by cDiffuseColor
        if (tex == null)
            color = Color.FromScRgb(1f, 0.55f * color.ScR, 0.52f * color.ScG, 0.46f * color.ScB);
        return new DrawMaterial(tex, color, cutout);
    }

    /// <summary>Finds the texture file, stores its contents in TextureData and returns the actual path. null if not found.</summary>
    string? GetTexture(string fileName)
    {
        if (textureCache.TryGetValue(fileName, out var p)) return p;
        p = files.Resolve(fileName);
        if (p == null) MissingTextures.Add(GameFiles.Normalize(fileName));
        else if (!TextureData.ContainsKey(p))
        {
            string found = p;
            try
            {
                var data = files.Read(found);
                if (data != null) TextureData[found] = data; else p = null;
            }
            catch (Exception ex) { BrokenFiles[found] = ex.Message; p = null; }
        }
        textureCache[fileName] = p;
        return p;
    }
}
