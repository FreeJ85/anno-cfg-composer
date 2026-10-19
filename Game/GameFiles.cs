using System.Text.RegularExpressions;

namespace AnnoCfgComposer.Game;

/// <summary>
/// Merges all archives in maindata into a single file list.
/// Archives are read in name order and later ones override earlier ones — zz_patchfiles_* come last, so patched versions win.
/// A patch's deletion markers (block flags 8) remove the same paths of earlier archives from the list.
/// </summary>
public sealed partial class GameFiles : IDisposable
{
    readonly Dictionary<string, RdaEntry> map = new(StringComparer.OrdinalIgnoreCase);
    readonly List<RdaArchive> archives = [];

    public string GameDir { get; }
    public IReadOnlyCollection<RdaEntry> Entries => map.Values;
    public int ArchiveCount => archives.Count;

    GameFiles(string gameDir) => GameDir = gameDir;

    // Large archives unrelated to graphics
    static readonly HashSet<string> SkipArchives = new(StringComparer.OrdinalIgnoreCase)
    {
        "video.rda", "sound.rda", "shaders.rda",
    };

    [GeneratedRegex(@"^[a-z]{2}_[a-z]{2}\d+\.rda$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageArchive();

    public static string MainData(string gameDir) => System.IO.Path.Combine(gameDir, "maindata");

    public static GameFiles Load(string gameDir, IProgress<string>? progress = null)
    {
        var gf = new GameFiles(gameDir);
        var names = Directory.GetFiles(MainData(gameDir), "*.rda")
            .Where(p => !SkipArchives.Contains(System.IO.Path.GetFileName(p)) && !LanguageArchive().IsMatch(System.IO.Path.GetFileName(p)))
            .OrderBy(p => System.IO.Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

        int i = 0;
        foreach (var path in names)
        {
            progress?.Report(Loc.F("st.reading", ++i, names.Count, System.IO.Path.GetFileName(path)));
            var files = new List<RdaEntry>();
            var deleted = new List<string>();
            RdaArchive a;
            try { a = RdaArchive.Open(path, files, deleted); }
            catch (Exception ex) { progress?.Report(Loc.F("st.archiveFail", System.IO.Path.GetFileName(path), ex.Message)); continue; }
            gf.archives.Add(a);
            foreach (var d in deleted) gf.map.Remove(Normalize(d));
            foreach (var f in files) gf.map[Normalize(f.Path)] = f;
        }
        return gf;
    }

    public static string Normalize(string p) => p.Replace('\\', '/').TrimStart('/');

    public RdaEntry? Find(string path) => map.GetValueOrDefault(Normalize(path));

    public byte[]? Read(string path) => Find(path) is { } e ? e.Archive.Read(e) : null;

    public void Dispose()
    {
        foreach (var a in archives) a.Dispose();
        archives.Clear();
        map.Clear();
    }
}
