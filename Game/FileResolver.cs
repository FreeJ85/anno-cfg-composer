namespace AnnoCfgComposer.Game;

/// <summary>
/// Resolves paths inside a cfg (data/base/...) to actual files.
/// For a cfg opened from disk, the folder above its data folder (the mod folder) is checked first, then the game archives.
///
/// cfg paths are the original source file names, so they differ from the actual files (verified on the original kontor_01):
///   model   .../rdm/harbor_warehouse_roman_01.fbx → .../rdm/harbor_warehouse_roman_01.rdm (or _lod0.rdm)
///   texture .../maps/kontor_01_decal01_diff.psd  → .../maps/kontor_01_decal01_diff_0.dds
/// </summary>
public sealed class FileResolver
{
    public GameFiles? Game { get; init; }
    /// <summary>The folder containing the "data" folder of a cfg opened from disk (the mod folder).</summary>
    public string? LooseRoot { get; init; }

    static readonly HashSet<string> ModelExt = new(StringComparer.OrdinalIgnoreCase) { ".fbx", ".rdm", ".max", ".obj" };
    static readonly HashSet<string> TextureExt = new(StringComparer.OrdinalIgnoreCase) { ".psd", ".tga", ".png", ".dds", ".jpg", ".tif", ".tiff", ".bmp" };

    public bool Exists(string path) => LoosePath(path) != null || Game?.Find(path) != null;

    public byte[]? Read(string path)
    {
        if (LoosePath(path) is { } lp) return File.ReadAllBytes(lp);
        return Game?.Read(path);
    }

    /// <summary>Where a file came from (for display).</summary>
    public string Source(string path)
    {
        if (LoosePath(path) is { } lp) return lp;
        return Game?.Find(path)?.Archive.Name ?? Loc.S("info.none");
    }

    string? LoosePath(string path)
    {
        if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
        if (LooseRoot == null) return null;
        var p = Path.Combine(LooseRoot, GameFiles.Normalize(path).Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(p) ? p : null;
    }

    public static IEnumerable<string> ModelCandidates(string fileName)
    {
        var p = GameFiles.Normalize(fileName);
        var stem = StripExt(p);
        yield return stem + ".rdm";
        yield return stem + "_lod0.rdm";
        yield return p;
    }

    public static IEnumerable<string> TextureCandidates(string fileName)
    {
        var p = GameFiles.Normalize(fileName);
        var stem = StripExt(p);
        yield return stem + "_0.dds";
        yield return stem + ".dds";
        yield return p;
    }

    /// <summary>Maps one path written in a cfg to an existing file path. null if not found.</summary>
    public string? Resolve(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        IEnumerable<string> c = ModelExt.Contains(ext) ? ModelCandidates(fileName)
            : TextureExt.Contains(ext) ? TextureCandidates(fileName)
            : [GameFiles.Normalize(fileName)];
        foreach (var p in c)
            if (Exists(p)) return p;
        return null;
    }

    static string StripExt(string p)
    {
        int slash = p.LastIndexOf('/');
        int dot = p.LastIndexOf('.');
        return dot > slash ? p[..dot] : p;
    }

    /// <summary>Takes the part of a disk path before ".../data/..." as the mod folder.</summary>
    public static (string? looseRoot, string virtualPath) FromDiskPath(string diskPath)
    {
        var norm = diskPath.Replace('\\', '/');
        int i = norm.LastIndexOf("/data/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return (null, diskPath);
        return (diskPath[..i], norm[(i + 1)..]);
    }
}
