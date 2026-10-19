using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace AnnoCfgComposer;

/// <summary>
/// Error log, so a crash on someone else's PC can be diagnosed.
/// %LOCALAPPDATA%\AnnoCfgComposer\logs\error-yyyyMMdd-HHmmss.txt (only the latest 20 are kept).
/// </summary>
public static class CrashLog
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnnoCfgComposer", "logs");

    /// <summary>Writes the exception to a file and returns its path, or null if it could not be written.</summary>
    public static string? Write(Exception ex, string where)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, $"error-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"Anno Cfg Composer {Assembly.GetExecutingAssembly().GetName().Version}");
            sb.AppendLine($"Time:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Where:   {where}");
            sb.AppendLine($"OS:      {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($".NET:    {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Culture: {System.Globalization.CultureInfo.CurrentUICulture.Name}");
            try { sb.AppendLine($"GPU:     {string.Join(" | ", Render.GpuAdapters.List().Select(a => $"{a.Index} {a.Name}"))}"); } catch { }
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
            File.WriteAllText(file, sb.ToString());

            foreach (var old in new DirectoryInfo(Folder).GetFiles("error-*.txt").OrderByDescending(f => f.Name).Skip(20))
                old.Delete();
            return file;
        }
        catch { return null; }
    }
}
