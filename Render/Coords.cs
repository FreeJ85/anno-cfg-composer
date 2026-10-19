using System.Numerics;
using System.Windows.Media.Media3D;

namespace AnnoCfgComposer.Render;

/// <summary>
/// Coordinate conversion shared by the 3D view (SceneBuilder / DxScene) and the glTF export, so both stay identical.
///
/// Anno data is treated as left-handed; the view and glTF are right-handed. "Mirror" flips the X axis:
///   vertices   p' = F·p           with F = diag(-1, 1, 1)
///   transforms M' = F·M·F         (so composing parent and child transforms still works, since F·F = I)
///   triangles  (a, b, c) → (a, c, b)  (a mirror reverses the winding)
/// With mirror off, everything passes through unchanged.
/// </summary>
public static class Coords
{
    static readonly Matrix3D Flip = new(-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

    public static Matrix3D Transform(Matrix3D m, bool mirror) => mirror ? Flip * m * Flip : m;

    public static Vector3 Position(Point3D p, bool mirror) =>
        new((mirror ? -1 : 1) * (float)p.X, (float)p.Y, (float)p.Z);

    /// <summary>Mirrored and normalized; degenerate normals become +Y (glTF only accepts unit-length normals).</summary>
    public static Vector3 Normal(Vector3D n, bool mirror)
    {
        var v = new Vector3((mirror ? -1 : 1) * (float)n.X, (float)n.Y, (float)n.Z);
        float len = v.Length();
        return len > 1e-6f && !float.IsNaN(len) ? v / len : Vector3.UnitY;
    }

    /// <summary>Triangle corner order after mirroring.</summary>
    public static (int a, int b, int c) Triangle(int a, int b, int c, bool mirror) => mirror ? (a, c, b) : (a, b, c);

    public static Rect3D Box(Rect3D r, bool mirror) =>
        mirror ? new Rect3D(-r.X - r.SizeX, r.Y, r.Z, r.SizeX, r.SizeY, r.SizeZ) : r;

    // WPF Matrix3D and System.Numerics.Matrix4x4 both use row vectors (v·M) — the elements map one to one
    public static Matrix4x4 ToMatrix4x4(Matrix3D m) => new(
        (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
        (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
        (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
        (float)m.OffsetX, (float)m.OffsetY, (float)m.OffsetZ, (float)m.M44);

    public static Matrix3D ToMatrix3D(Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);
}
