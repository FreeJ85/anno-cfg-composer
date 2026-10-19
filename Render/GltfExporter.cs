using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AnnoCfgComposer.Cfg;
using AnnoCfgComposer.Game;
using AnnoCfgComposer.Rdm;

namespace AnnoCfgComposer.Render;

/// <summary>
/// Scene tree → a single glTF 2.0 binary (.glb). Blender opens it without add-ons via File → Import → glTF 2.0.
///
/// - Node hierarchy = cfg structure (a prop cfg node has its models below it). Matrices are relative to the parent.
/// - Vertices and indices of the same rdm are stored only once. Materials come from the cfg, so one mesh per (rdm, material list) combination.
/// - Only diffuse textures are embedded, as PNG (opaque, same as the view). Normal and metal maps are not included yet.
/// - Mirroring: same rule as the view (X flip F·M·F + reversed winding). glTF is right-handed.
/// One-way conversion — glb is not converted back to cfg.
/// </summary>
public sealed class GltfExporter
{
    public sealed record Result(int Nodes, int Meshes, int Materials, int Textures, long Bytes);

    const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;
    const int Float = 5126, UInt = 5125;

    readonly FileResolver files;
    readonly bool mirror;
    readonly int maxTextureSize;

    readonly MemoryStream bin = new();
    readonly JsonArray bufferViews = [], accessors = [], meshes = [], materials = [], textures = [], images = [], nodes = [];
    readonly Dictionary<string, Geometry> geometryCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> meshCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> materialCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> textureCache = new(StringComparer.OrdinalIgnoreCase);

    sealed record Geometry(int Position, int? Normal, int? Uv, int[] Indices);

    GltfExporter(FileResolver files, bool mirror, int maxTextureSize)
    {
        this.files = files;
        this.mirror = mirror;
        this.maxTextureSize = maxTextureSize;
    }

    public static Result Export(SceneNode root, FileResolver files, bool mirror, string outPath, int maxTextureSize = 2048, CancellationToken ct = default)
    {
        var x = new GltfExporter(files, mirror, maxTextureSize);
        int rootIndex = x.AddNode(root, ct);
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Anno Cfg Composer" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["name"] = root.Name, ["nodes"] = new JsonArray(rootIndex) }),
            ["nodes"] = x.nodes,
        };
        if (x.meshes.Count > 0) json["meshes"] = x.meshes;
        if (x.materials.Count > 0) json["materials"] = x.materials;
        if (x.textures.Count > 0)
        {
            json["textures"] = x.textures;
            json["images"] = x.images;
            // REPEAT — some textures have UVs outside 0..1
            json["samplers"] = new JsonArray(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 });
        }
        if (x.bufferViews.Count > 0)
        {
            json["accessors"] = x.accessors;
            json["bufferViews"] = x.bufferViews;
            json["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = x.bin.Length });
        }
        long bytes = x.WriteGlb(outPath, json.ToJsonString());
        return new Result(x.nodes.Count, x.meshes.Count, x.materials.Count, x.textures.Count, bytes);
    }

    // ---------------------------------------------------------------- Nodes

    int AddNode(SceneNode n, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var node = new JsonObject { ["name"] = string.IsNullOrWhiteSpace(n.Name) ? "node" : n.Name };
        int index = nodes.Count;
        nodes.Add(node);

        var m = mirror ? Mirror(n.Local) : n.Local;
        if (!m.IsIdentity)
        {
            // WPF uses row vectors (v·M), glTF uses column vectors + column-major storage → writing the WPF matrix in row order matches directly
            node["matrix"] = new JsonArray(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                                           m.M31, m.M32, m.M33, m.M34, m.OffsetX, m.OffsetY, m.OffsetZ, m.M44);
        }

        if (n.Mesh != null && n.RdmPath != null && GetMesh(n.RdmPath, n.Mesh, n.Materials) is int mesh and >= 0) node["mesh"] = mesh;

        if (n.Children.Count > 0)
        {
            var children = new JsonArray();
            foreach (var c in n.Children) children.Add(AddNode(c, ct));
            node["children"] = children;
        }
        return index;
    }

    static Matrix3D Mirror(Matrix3D m)
    {
        var f = new Matrix3D(-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        return f * m * f;
    }

    // ---------------------------------------------------------------- Meshes

    int GetMesh(string rdmPath, RdmMesh mesh, IReadOnlyList<CfgMaterial> mats)
    {
        var key = rdmPath + "|" + string.Join("|", mats.Select(MaterialKey));
        if (meshCache.TryGetValue(key, out int idx)) return idx;

        var g = GetGeometry(rdmPath, mesh);
        var prims = new JsonArray();
        for (int i = 0; i < mesh.Submeshes.Length; i++)
        {
            if (g.Indices[i] < 0) continue;
            var sub = mesh.Submeshes[i];
            // Terrain / water cutout planes are invisible in the game — not exported
            if (sub.Material < mats.Count && mats[sub.Material].Hidden) continue;
            var attrs = new JsonObject { ["POSITION"] = g.Position };
            if (g.Normal is int nrm) attrs["NORMAL"] = nrm;
            if (g.Uv is int uv) attrs["TEXCOORD_0"] = uv;
            prims.Add(new JsonObject
            {
                ["attributes"] = attrs,
                ["indices"] = g.Indices[i],
                ["material"] = GetMaterial(sub.Material < mats.Count ? mats[sub.Material] : null),
            });
        }
        // No mesh if nothing is visible (glTF does not allow empty primitives)
        idx = prims.Count == 0 ? -1 : meshes.Count;
        if (idx >= 0) meshes.Add(new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(rdmPath), ["primitives"] = prims });
        meshCache[key] = idx;
        return idx;
    }

    Geometry GetGeometry(string rdmPath, RdmMesh mesh)
    {
        if (geometryCache.TryGetValue(rdmPath, out var g)) return g;
        float sx = mirror ? -1 : 1;
        int n = mesh.Positions.Length;

        var pos = new float[n * 3];
        float[] min = [float.MaxValue, float.MaxValue, float.MaxValue], max = [float.MinValue, float.MinValue, float.MinValue];
        for (int i = 0; i < n; i++)
        {
            var p = mesh.Positions[i];
            float x = sx * (float)p.X, y = (float)p.Y, z = (float)p.Z;
            pos[i * 3] = x; pos[i * 3 + 1] = y; pos[i * 3 + 2] = z;
            min[0] = Math.Min(min[0], x); min[1] = Math.Min(min[1], y); min[2] = Math.Min(min[2], z);
            max[0] = Math.Max(max[0], x); max[1] = Math.Max(max[1], y); max[2] = Math.Max(max[2], z);
        }
        int posAcc = AddAccessor(AddView(Bytes(pos), ArrayBuffer), Float, n, "VEC3", min, max);

        int? nrmAcc = null;
        if (mesh.Normals != null)
        {
            var nrm = new float[n * 3];
            for (int i = 0; i < n; i++)
            {
                var v = mesh.Normals[i];
                v.X *= sx;
                double len = v.Length;
                // glTF only allows unit-length normals
                if (len < 1e-6 || double.IsNaN(len)) v = new Vector3D(0, 1, 0); else v /= len;
                nrm[i * 3] = (float)v.X; nrm[i * 3 + 1] = (float)v.Y; nrm[i * 3 + 2] = (float)v.Z;
            }
            nrmAcc = AddAccessor(AddView(Bytes(nrm), ArrayBuffer), Float, n, "VEC3");
        }

        int? uvAcc = null;
        if (mesh.Uvs != null)
        {
            var uv = new float[n * 2];
            for (int i = 0; i < n; i++) { uv[i * 2] = (float)mesh.Uvs[i].X; uv[i * 2 + 1] = (float)mesh.Uvs[i].Y; }
            uvAcc = AddAccessor(AddView(Bytes(uv), ArrayBuffer), Float, n, "VEC2");
        }

        var idx = new int[mesh.Submeshes.Length];
        for (int s = 0; s < idx.Length; s++)
        {
            var sub = mesh.Submeshes[s];
            int count = sub.Count - sub.Count % 3;
            if (count <= 0) { idx[s] = -1; continue; }
            var tri = new uint[count];
            for (int i = 0; i < count; i += 3)
            {
                int a = mesh.Indices[sub.Start + i], b = mesh.Indices[sub.Start + i + 1], c = mesh.Indices[sub.Start + i + 2];
                tri[i] = (uint)a;
                tri[i + 1] = (uint)(mirror ? c : b);
                tri[i + 2] = (uint)(mirror ? b : c);
            }
            idx[s] = AddAccessor(AddView(Bytes(tri), ElementArrayBuffer), UInt, count, "SCALAR");
        }

        g = new Geometry(posAcc, nrmAcc, uvAcc, idx);
        geometryCache[rdmPath] = g;
        return g;
    }

    // ---------------------------------------------------------------- Materials / textures

    static string MaterialKey(CfgMaterial? m) => m == null ? "-" : (m.DiffuseTexture ?? "") + "#" + m.DiffuseColor + "#" + m.AlphaCutout;

    int GetMaterial(CfgMaterial? m)
    {
        var key = MaterialKey(m);
        if (materialCache.TryGetValue(key, out int idx)) return idx;

        var c = m?.DiffuseColor ?? Colors.White;
        var pbr = new JsonObject
        {
            ["baseColorFactor"] = new JsonArray(c.ScR, c.ScG, c.ScB, 1.0),
            ["metallicFactor"] = 0.0,
            ["roughnessFactor"] = 0.85,
        };
        // Alpha-cutout materials: plants, or textures with low-alpha areas (ropes, ivy, grilles, decals …)
        // The game and the view (Direct3D) cut them out by alpha too
        bool cutout = m?.DiffuseTexture is { } t0 && (m.AlphaCutout || HasCutoutAlpha(t0));
        if (m?.DiffuseTexture is { } tex && GetTexture(tex, cutout ? m.AlphaRef : null) is int t and >= 0)
            pbr["baseColorTexture"] = new JsonObject { ["index"] = t };
        else cutout = false;

        idx = materials.Count;
        materials.Add(new JsonObject
        {
            ["name"] = string.IsNullOrWhiteSpace(m?.Name) ? "material" : m.Name,
            ["pbrMetallicRoughness"] = pbr,
            ["doubleSided"] = true,
        });
        // Imported as MASK in Blender too
        if (cutout)
        {
            materials[idx]!["alphaMode"] = "MASK";
            materials[idx]!["alphaCutoff"] = 0.5;
        }
        materialCache[key] = idx;
        return idx;
    }

    readonly Dictionary<string, bool> alphaCache = new(StringComparer.OrdinalIgnoreCase);

    bool HasCutoutAlpha(string fileName)
    {
        if (alphaCache.TryGetValue(fileName, out bool has)) return has;
        try
        {
            var path = files.Resolve(fileName);
            var data = path == null ? null : files.Read(path);
            has = data != null && DdsTexture.LowAlphaFraction(data, 0.5) > 0.0005;
        }
        catch { has = false; }
        alphaCache[fileName] = has;
        return has;
    }

    int GetTexture(string fileName, double? alphaCut)
    {
        string cacheKey = fileName + "|" + alphaCut;
        if (textureCache.TryGetValue(cacheKey, out int idx)) return idx;
        idx = -1;
        try
        {
            var path = files.Resolve(fileName);
            var data = path == null ? null : files.Read(path);
            if (data != null && DdsTexture.Load(data, maxTextureSize, alphaCut) is { } img)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using var ms = new MemoryStream();
                enc.Save(ms);
                int view = AddView(ms.ToArray(), null);
                images.Add(new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(path), ["bufferView"] = view, ["mimeType"] = "image/png" });
                idx = textures.Count;
                textures.Add(new JsonObject { ["source"] = images.Count - 1, ["sampler"] = 0 });
            }
        }
        catch { idx = -1; }
        textureCache[cacheKey] = idx;
        return idx;
    }

    // ---------------------------------------------------------------- Buffers

    int AddView(byte[] data, int? target)
    {
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = data.Length };
        if (target is int t) view["target"] = t;
        bin.Write(data);
        bufferViews.Add(view);
        return bufferViews.Count - 1;
    }

    int AddAccessor(int view, int componentType, int count, string type, float[]? min = null, float[]? max = null)
    {
        var a = new JsonObject { ["bufferView"] = view, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
        if (min != null && max != null)
        {
            a["min"] = new JsonArray(min.Select(v => (JsonNode)v).ToArray());
            a["max"] = new JsonArray(max.Select(v => (JsonNode)v).ToArray());
        }
        accessors.Add(a);
        return accessors.Count - 1;
    }

    static byte[] Bytes<T>(T[] data) where T : struct
    {
        var span = System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan());
        return span.ToArray();
    }

    long WriteGlb(string outPath, string json)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        long binLen = bin.Length;
        long total = 12 + 8 + jsonBytes.Length + jsonPad + (binLen > 0 ? 8 + binLen : 0);

        using var fs = File.Create(outPath);
        using var w = new BinaryWriter(fs);
        w.Write(0x46546C67u);            // "glTF"
        w.Write(2u);
        w.Write((uint)total);
        w.Write((uint)(jsonBytes.Length + jsonPad));
        w.Write(0x4E4F534Au);            // "JSON"
        w.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) w.Write((byte)' ');
        if (binLen > 0)
        {
            w.Write((uint)binLen);
            w.Write(0x004E4942u);        // "BIN\0"
            w.Flush();
            bin.Position = 0;
            bin.CopyTo(fs);
        }
        return total;
    }
}
