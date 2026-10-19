using System.IO;
using System.Text.Json;

namespace AnnoCfgComposer;

/// <summary>%APPDATA%\AnnoCfgComposer\settings.json</summary>
public sealed class Settings
{
    public string? GameDir { get; set; }
    public string? ExtractDir { get; set; }
    public bool Mirror { get; set; } = true;
    public bool Textures { get; set; } = true;
    /// <summary>en · de · ko. If missing, the Windows display language.</summary>
    public string? Language { get; set; }
    /// <summary>Graphics card name. If missing, automatic (the card with the most dedicated memory).</summary>
    public string? GpuAdapter { get; set; }
    /// <summary>Mod folders dropped onto the left list.</summary>
    public List<string> ModFolders { get; set; } = [];
    /// <summary>The copyright notice before the first extract / glTF export has been shown.</summary>
    public bool CopyrightNoticeShown { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnnoCfgComposer", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
