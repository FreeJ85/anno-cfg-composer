using System.Text;
using AnnoCfgComposer.Cfg;

namespace AnnoCfgComposer.Game;

/// <summary>
/// All files used by one cfg — prop cfgs (recursive), model rdms (+ other LODs), dds textures, and .ifo/.fc/.cf7 with the same name.
/// Any cfg tag value starting with "data/" is treated as a path and followed (animation and particle paths included).
/// </summary>
public static class DependencyCollector
{
    public sealed record Result(List<string> Found, List<string> Missing);

    static readonly string[] SiblingExt = [".ifo", ".fc", ".cf7"];

    public static Result Collect(FileResolver files, string rootCfg, CancellationToken ct = default)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        var seenCfg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        queue.Enqueue(rootCfg);
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var cfg = queue.Dequeue();
            if (!seenCfg.Add(cfg)) continue;
            var data = files.Read(cfg);
            if (data == null) { missing.Add(cfg); continue; }
            found.Add(cfg);

            var stem = cfg[..^Path.GetExtension(cfg).Length];
            foreach (var ext in SiblingExt)
                if (files.Exists(stem + ext)) found.Add(stem + ext);

            CfgNode root;
            try { root = CfgNode.Parse(Encoding.UTF8.GetString(data)); }
            catch { continue; }

            foreach (var n in root.Descendants())
            {
                var t = n.Text;
                if (t.Length < 6 || !(t.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || t.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (Path.GetExtension(t).Length == 0) continue;

                var resolved = files.Resolve(t);
                if (resolved == null) { missing.Add(GameFiles.Normalize(t)); continue; }
                if (resolved.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)) { queue.Enqueue(resolved); continue; }
                found.Add(resolved);

                // If the model was found as _lod0, include _lod1.. as well
                if (resolved.EndsWith("_lod0.rdm", StringComparison.OrdinalIgnoreCase))
                {
                    var baseName = resolved[..^"_lod0.rdm".Length];
                    for (int i = 1; i <= 6; i++)
                        if (files.Exists($"{baseName}_lod{i}.rdm")) found.Add($"{baseName}_lod{i}.rdm");
                }
            }
        }
        return new Result([.. found], [.. missing]);
    }

    /// <summary>Writes the found files under dest, keeping the data/... structure. Absolute disk paths (a directly opened cfg) are skipped.</summary>
    public static int Extract(FileResolver files, IEnumerable<string> paths, string dest, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int n = 0;
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (Path.IsPathRooted(p)) continue;
            var data = files.Read(p);
            if (data == null) continue;
            var target = Path.Combine(dest, GameFiles.Normalize(p).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
            if (++n % 20 == 0) progress?.Report(Loc.F("st.extracting", n));
        }
        return n;
    }
}
