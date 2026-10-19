using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AnnoCfgComposer.Game;
using AnnoCfgComposer.Render;

namespace AnnoCfgComposer;

/// <summary>
/// Headless check: AnnoCfgComposer.exe --selftest &lt;output folder&gt; [cfg path …]
///   report.txt — archive loading, parse results for a cfg sample, dependency collection
///   *.png      — renders of the given cfgs
/// </summary>
static class SelfTest
{
    public static async Task Run(string outDir, string[] cfgs)
    {
        Directory.CreateDirectory(outDir);
        Loc.Current = Lang.Ko;
        var log = new StringBuilder();
        void L(string s) { log.AppendLine(s); File.WriteAllText(Path.Combine(outDir, "report.txt"), log.ToString()); }

        var dir = GameLocator.Find();
        L("game: " + dir);
        if (dir == null) return;
        var sw = Stopwatch.StartNew();
        using var game = GameFiles.Load(dir, new SyncProgress(s => { if (s.Contains("실패")) L(s); }));
        L($"load {sw.ElapsedMilliseconds} ms · archives {game.ArchiveCount} · files {game.Entries.Count}");
        var res = new FileResolver { Game = game };
        var all = game.Entries.Where(e => e.Path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)).Select(e => GameFiles.Normalize(e.Path)).ToList();
        L($"cfg {all.Count}");

        // Sample: parse 400 random files
        var rnd = new Random(117);
        var sample = all.OrderBy(_ => rnd.Next()).Take(400).ToList();
        int ok = 0, models = 0, tris = 0, missing = 0;
        var errors = new Dictionary<string, int>();
        var errorExample = new Dictionary<string, string>();
        sw.Restart();
        foreach (var p in sample)
        {
            var b = new SceneBuilder(res, new SceneOptions { Textures = false });
            try { b.Build(p, CancellationToken.None); ok++; }
            catch (Exception ex) { Count("EXC " + ex.GetType().Name + ": " + ex.Message, p); }
            models += b.ModelCount; tris += b.TriangleCount; missing += b.MissingFiles.Count;
            foreach (var kv in b.BrokenFiles) Count(kv.Value, kv.Key);
        }
        void Count(string k, string ex) { errors[k] = errors.GetValueOrDefault(k) + 1; errorExample.TryAdd(k, ex); }
        L($"sample {sample.Count}: ok {ok} · models {models} · tris {tris} · missing files {missing} · {sw.ElapsedMilliseconds} ms");
        foreach (var kv in errors.OrderByDescending(k => k.Value)) L($"  {kv.Value,5}  {kv.Key}   e.g. {errorExample[kv.Key]}");

        if (cfgs.Contains("?scanmat"))
        {
            // All game materials: distribution of (ShaderID, has Transparency section)
            var dist = new Dictionary<string, int>();
            var example = new Dictionary<string, string>();
            foreach (var p in all)
            {
                var data = game.Read(p);
                if (data == null) continue;
                var root = Cfg.CfgNode.Parse(Encoding.UTF8.GetString(data));
                foreach (var m in root.Descendants().Where(n => n.Name == "Config" && n.ConfigType == "MATERIAL"))
                {
                    var k = $"shader {m.Value("ShaderID"),-3} transparency {m.Child("cShadowAlphaRef") != null,-5} plant {m.Children.Any(c => c.Name.EndsWith("[ModelPlant]")),-5}";
                    dist[k] = dist.GetValueOrDefault(k) + 1;
                    example.TryAdd(k, p);
                }
            }
            foreach (var kv in dist.OrderByDescending(x => x.Value)) L($"  {kv.Value,7}  {kv.Key}   e.g. {example[kv.Key]}");
        }

        if (cfgs.Contains("?alphastat"))
        {
            // Diffuse texture alpha of non-plant materials — how much low alpha (pixels that get cut) there is
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int checkedTex = 0, withLow = 0;
            var worst = new List<(double frac, string tex)>();
            foreach (var p in all.OrderBy(_ => rnd.Next()).Take(1500))
            {
                var b = new SceneBuilder(res, new SceneOptions());
                try { b.Build(p, CancellationToken.None); } catch { continue; }
                foreach (var batch in b.Batches)
                {
                    var t = batch.Material.TexturePath;
                    if (t == null || batch.Material.Cutout || !seen.Add(t) || checkedTex >= 600) continue;
                    var frac = DdsTexture.LowAlphaFraction(b.TextureData[t], 0.1);
                    if (frac < 0) continue;
                    checkedTex++;
                    if (frac > 0.001) { withLow++; worst.Add((frac, t)); }
                }
            }
            L($"alphastat: textures {checkedTex} · with alpha<0.1 pixels {withLow}");
            foreach (var w in worst.OrderByDescending(x => x.frac).Take(25)) L($"  {w.frac:P1}  {w.tex}");
        }

        foreach (var probe in cfgs.Where(c => c.StartsWith('?') && c != "?scanmat" && c != "?alphastat" && c != "?gpu" && c != "?extract"))
        {
            var e = game.Find(probe[1..]);
            L($"probe {probe[1..]} → {(e == null ? "없음" : e.Archive.Name + " " + e.Size)} · resolve → {res.Resolve(probe[1..])}");
        }
        bool gpuTest = cfgs.Contains("?gpu");
        bool extractTest = cfgs.Contains("?extract");   // Extraction is hundreds of MB for large buildings — only when requested
        cfgs = cfgs.Where(c => !c.StartsWith('?')).ToArray();
        var renders = new List<(string path, string file)>();

        foreach (var cfg in cfgs)
        {
            var path = all.FirstOrDefault(p => p.EndsWith(cfg, StringComparison.OrdinalIgnoreCase)) ?? cfg;
            L("");
            L("== " + path);
            sw.Restart();
            var b = new SceneBuilder(res, new SceneOptions());
            b.Build(path, CancellationToken.None);
            L($"build {sw.ElapsedMilliseconds} ms · cfg {b.CfgCount} · models {b.ModelCount} · tris {b.TriangleCount} · batches {b.Batches.Count} · instances {b.Batches.Sum(x => x.Instances.Count)} · textures {b.TextureData.Count} (cut {b.CutoutTextureCount}) · bounds {b.Bounds}");
            foreach (var m in b.MissingFiles.Take(20)) L("  missing " + m);
            foreach (var m in b.MissingTextures.Take(20)) L("  missing tex " + m);
            foreach (var m in b.BrokenFiles.Take(20)) L($"  broken {m.Key} — {m.Value}");
            sw.Restart();
            var deps = DependencyCollector.Collect(res, path);
            L($"deps {deps.Found.Count} found · {deps.Missing.Count} missing · {sw.ElapsedMilliseconds} ms");
            foreach (var x in deps.Found.GroupBy(f => Path.GetExtension(f).ToLowerInvariant())) L($"  {x.Key} {x.Count()}");
            foreach (var m in deps.Missing.Take(15)) L("  dep missing " + m);
            if (extractTest)
            {
                int written = DependencyCollector.Extract(res, deps.Found, Path.Combine(outDir, "extract"));
                L($"extracted {written}");
            }
            renders.Add((path, Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + ".png")));
            sw.Restart();
            var gr = GltfExporter.Export(b.Scene!, res, true, Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + ".glb"));
            L($"glb {gr.Nodes} nodes · {gr.Meshes} meshes · {gr.Materials} materials · {gr.Textures} textures · {gr.Bytes / 1048576.0:0.0} MB · {sw.ElapsedMilliseconds} ms");
        }

        // Frames per second per graphics card (measured while rotating the camera)
        if (gpuTest)
        {
            var heavy = all.FirstOrDefault(p => p.EndsWith("data/base/graphics/roman/buildings/harbor/kontor_01/kontor_01.cfg", StringComparison.OrdinalIgnoreCase)) ?? all[0];
            foreach (var a in GpuAdapters.List())
            {
                var w = new MainWindow(null, a.Index) { ShowActivated = false, Width = 1400, Height = 900 };
                w.Show();
                try
                {
                    var (fps, frames) = await w.MeasureFpsAsync(heavy, res, 5);
                    L($"gpu {a.Index} {a.Name}: helix fps {fps:0.0} · wpf frames/s {frames / 5.0:0.0}");
                    await w.SnapshotAsync(heavy, res, Path.Combine(outDir, $"gpu{a.Index}.png"));
                }
                catch (Exception ex) { L($"gpu {a.Index} {a.Name}: FAIL {ex.Message}"); }
                w.Close();
            }
        }

        // Render with the same Direct3D 11 viewport as the real UI (opens a window, renders, then closes it)
        if (renders.Count > 0)
        {
            var w = new MainWindow { ShowActivated = false, Width = 1200, Height = 900 };
            w.Show();
            foreach (var (path, file) in renders)
            {
                sw.Restart();
                try { await w.SnapshotAsync(path, res, file); L($"render {Path.GetFileName(file)} {sw.ElapsedMilliseconds} ms"); }
                catch (Exception ex) { L($"render {Path.GetFileName(file)} FAIL {ex}"); }
            }
            w.Close();
        }
        L("done");
    }
}

sealed class SyncProgress(Action<string> a) : IProgress<string>
{
    public void Report(string value) => a(value);
}
