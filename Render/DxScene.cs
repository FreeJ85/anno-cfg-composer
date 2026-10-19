using System.Numerics;
using Matrix3D = System.Windows.Media.Media3D.Matrix3D;
using MatrixTransform3D = System.Windows.Media.Media3D.MatrixTransform3D;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using SharpDX.Direct3D11;

namespace AnnoCfgComposer.Render;

/// <summary>
/// SceneBuilder draw batches → Viewport3DX elements (on the UI thread). Direct3D 11.
/// - One batch = one MeshGeometryModel3D; multiple placements use GPU instancing (Instances).
/// - Textures are uploaded to the GPU as dds directly (BC7 is not decoded on the CPU).
/// - Alpha: Helix's default shader discards low-alpha pixels (clip) — IsTransparent must stay off.
///   Turning it on takes the OIT path and leaves disappeared entirely. Leaves, ropes, ivy and grilles get cut out like in the game.
///   (272 of 600 non-plant textures use alpha cutout — ropes 88%, windows 88% … measured 2026-10-19)
/// </summary>
public static class DxScene
{
    public static List<Element3D> Create(SceneBuilder b)
    {
        var textures = new Dictionary<string, TextureModel>(StringComparer.OrdinalIgnoreCase);
        var materials = new Dictionary<DrawMaterial, Material>();
        var result = new List<Element3D>();

        foreach (var batch in b.Batches)
        {
            if (batch.Instances.Count == 0) continue;
            var dm = batch.Material;
            if (!materials.TryGetValue(dm, out var mat))
            {
                var c = dm.Color;
                var phong = new PhongMaterial
                {
                    DiffuseColor = new Color4(c.ScR, c.ScG, c.ScB, c.ScA),
                    AmbientColor = new Color4(0.35f, 0.35f, 0.35f, 1f),
                    SpecularColor = new Color4(0.06f, 0.06f, 0.06f, 1f),
                    SpecularShininess = 20f,
                };
                if (dm.TexturePath != null && b.TextureData.TryGetValue(dm.TexturePath, out var data))
                {
                    if (!textures.TryGetValue(dm.TexturePath, out var tm))
                        textures[dm.TexturePath] = tm = new TextureModel(new MemoryStream(data), true);
                    phong.DiffuseMap = tm;
                }
                materials[dm] = mat = phong;
            }

            var model = new MeshGeometryModel3D
            {
                Geometry = batch.Geometry,
                Material = mat,
                CullMode = CullMode.None,
                IsTransparent = dm.Placeholder,
            };
            if (batch.Instances.Count == 1) model.Transform = new MatrixTransform3D(Coords.ToMatrix3D(batch.Instances[0]));
            else model.Instances = batch.Instances;
            result.Add(model);
        }
        return result;
    }
}
