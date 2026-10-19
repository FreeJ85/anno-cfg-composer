using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AnnoCfgComposer.Game;

/// <summary>Finds the installed Anno 117 folder — Steam libraries first, then the Ubisoft Connect registry.</summary>
public static partial class GameLocator
{
    const string SteamFolderName = "Anno 117 - Pax Romana";

    public static bool IsGameDir(string? dir) =>
        !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(GameFiles.MainData(dir), "graphics_roman.rda"));

    public static string? Find()
    {
        foreach (var dir in Candidates())
            if (IsGameDir(dir)) return dir;
        return null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    static IEnumerable<string> Candidates()
    {
        var steamRoots = new List<string>();
        foreach (var (hive, key, value) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            if (Read(hive, key, value) is { } s) steamRoots.Add(s.Replace('/', '\\'));
        }

        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, "steamapps", "common", SteamFolderName);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); } catch { continue; }
            foreach (Match m in VdfPath().Matches(text))
                yield return Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common", SteamFolderName);
        }

        // Ubisoft Connect: Installs\<game id>\InstallDir
        foreach (var baseKey in new[] { @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", @"SOFTWARE\Ubisoft\Launcher\Installs" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(baseKey);
            if (k == null) continue;
            foreach (var sub in k.GetSubKeyNames())
            {
                using var s = k.OpenSubKey(sub);
                if (s?.GetValue("InstallDir") is string dir) yield return dir.Replace('/', '\\');
            }
        }
    }

    static string? Read(RegistryKey hive, string key, string value)
    {
        try
        {
            using var k = hive.OpenSubKey(key);
            return k?.GetValue(value) as string;
        }
        catch { return null; }
    }
}
