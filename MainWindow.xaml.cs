using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using AnnoCfgComposer.Game;
using AnnoCfgComposer.Render;
using Microsoft.Win32;

namespace AnnoCfgComposer;

public partial class MainWindow : Window
{
    readonly Settings settings = Settings.Load();
    readonly DispatcherTimer searchTimer;
    // Direct3D 11 device / shaders (released when the window closes). Card chosen by GpuAdapters.Pick — so laptops use the discrete GPU
    HelixToolkit.SharpDX.DefaultEffectsManager effects;
    GpuAdapters.Adapter? gpu;
    GameFiles? game;
    List<string> allCfgs = [];
    int shownCount;

    // The cfg currently shown
    string? currentPath;
    FileResolver? currentResolver;
    SceneBuilder? currentBuilder;
    TimeSpan currentElapsed;
    CancellationTokenSource? loadCts;
    bool busy;

    // Text rebuilt when the language changes
    Func<string> statusText = () => Loc.S("st.ready");
    Func<string> gameStatusText = () => "";

    readonly string? startupCfg;

    public MainWindow(string? startupCfg = null, int? adapterOverride = null)
    {
        gpu = adapterOverride is int ai ? GpuAdapters.List().FirstOrDefault(a => a.Index == ai) : GpuAdapters.Pick(settings.GpuAdapter);
        effects = gpu != null ? new HelixToolkit.SharpDX.DefaultEffectsManager(gpu.Index) : new HelixToolkit.SharpDX.DefaultEffectsManager();
        this.startupCfg = startupCfg;
        Loc.Current = Loc.FromCode(settings.Language);
        InitializeComponent();
        View.EffectsManager = effects;
        BuildGpuMenu();
        BuildGrid(40);
        searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); ApplyFilter(); };
        MirrorToggle.IsChecked = settings.Mirror;
        TextureToggle.IsChecked = settings.Textures;
        ApplyLanguage();
    }

    /// <summary>--mod &lt;folder&gt;: add that mod folder to the list at startup (same as drag and drop).</summary>
    public string? StartupMod { get; init; }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Mod folders dropped last time (deleted folders are silently skipped)
        foreach (var f in settings.ModFolders.ToList())
            if (Directory.Exists(f)) AddModFolder(f, false);
        if (StartupMod != null) AddModFolder(StartupMod, true);

        var dir = GameLocator.IsGameDir(settings.GameDir) ? settings.GameDir : GameLocator.Find();
        if (dir == null)
        {
            SetStatus(() => Loc.S("st.noGame"));
            return;
        }
        await LoadGame(dir);
        if (startupCfg != null && game != null)
        {
            if (File.Exists(startupCfg))
            {
                var (root, vpath) = FileResolver.FromDiskPath(startupCfg);
                await ShowCfg(root == null ? startupCfg : vpath, new FileResolver { Game = game, LooseRoot = root });
            }
            else await ShowCfg(GameFiles.Normalize(startupCfg), new FileResolver { Game = game });
        }
    }

    void OnClosed(object? sender, EventArgs e)
    {
        settings.Save();
        loadCts?.Cancel();
        game?.Dispose();
        effects.Dispose();
    }

    void OnExit(object sender, RoutedEventArgs e) => Close();

    void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.O) { OnOpenDisk(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.E && ExtractButton.IsEnabled) { OnExtract(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.G && GltfButton.IsEnabled) { OnExportGltf(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (!ctrl && e.Key == Key.F && Keyboard.FocusedElement is not TextBox) { OnZoomExtents(this, e); e.Handled = true; }
    }

    // ---------------------------------------------------------------- Language

    void OnLanguageMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        Loc.Current = (Lang)int.Parse(tag);
        settings.Language = Loc.Code(Loc.Current);
        settings.Save();
        ApplyLanguage();
        ApplyFilter();   // so the "[Mod] name" group names change too
    }

    void ApplyLanguage()
    {
        // Menu
        FileMenu.Header = Loc.S("menu.file");
        GameMenuItem.Header = Loc.S("btn.game");
        OpenMenuItem.Header = Loc.S("btn.open");
        AddModMenuItem.Header = Loc.S("menu.addMod");
        DropHint.Text = Loc.S("hint.drop");
        ExtractMenuItem.Header = Loc.S("btn.extract");
        GltfMenuItem.Header = Loc.S("btn.gltf");
        ExitMenuItem.Header = Loc.S("menu.exit");
        ViewMenu.Header = Loc.S("menu.view");
        MirrorMenuItem.Header = Loc.S("chk.mirror");
        TextureMenuItem.Header = Loc.S("chk.textures");
        GridMenuItem.Header = Loc.S("chk.grid");
        AxesMenuItem.Header = Loc.S("menu.axes");
        GpuMenu.Header = Loc.S("menu.gpu");
        if (GpuMenu.Items.Count > 0) ((MenuItem)GpuMenu.Items[0]).Header = Loc.S("menu.gpuAuto");
        GpuStatusText.Text = gpu == null ? "" : Loc.F("st.gpu", gpu.Name);
        FitMenuItem.Header = Loc.S("btn.zoom");
        LangMenu.Header = Loc.S("menu.lang");
        foreach (MenuItem m in LangMenu.Items) m.IsChecked = (string)m.Tag == ((int)Loc.Current).ToString();
        HelpMenu.Header = Loc.S("menu.help");
        AboutMenuItem.Header = Loc.S("menu.about");
        LogsMenuItem.Header = Loc.S("menu.logs");

        // Toolbar
        OpenLabel.Text = Loc.S("tb.open");
        OpenButton.ToolTip = Loc.S("btn.open") + "\n" + Loc.S("tip.open");
        ExtractLabel.Text = Loc.S("tb.extract");
        ExtractButton.ToolTip = Loc.S("btn.extract") + "\n" + Loc.S("tip.extract");
        GltfLabel.Text = Loc.S("tb.gltf");
        GltfButton.ToolTip = Loc.S("btn.gltf") + "\n" + Loc.S("tip.gltf");
        MirrorLabel.Text = Loc.S("chk.mirror");
        MirrorToggle.ToolTip = Loc.S("tip.mirror");
        TextureLabel.Text = Loc.S("chk.textures");
        GridLabel.Text = Loc.S("chk.grid");
        FitLabel.Text = Loc.S("btn.zoom");
        FitButton.ToolTip = Loc.S("btn.zoom") + " (F)";

        // Panels
        FilesHeader.Text = Loc.S("panel.files");
        SearchPlaceholder.Text = Loc.S("search.placeholder") + "  (Ctrl+F)";
        SearchBox.ToolTip = Loc.S("tip.search");
        InfoHeader.Text = Loc.S("panel.info");
        InfoEmpty.Text = Loc.S("info.empty");
        ViewHintText.Text = Loc.S("hint.pick");
        MouseHint.Text = Loc.S("hint.mouse");
        LoadingText.Text = Loc.S("hint.loading");
        StatCfgLabel.Text = Loc.S("stat.cfg");
        StatModelsLabel.Text = Loc.S("stat.models");
        StatTrisLabel.Text = Loc.S("stat.tris");
        StatTimeLabel.Text = Loc.S("stat.time");
        PartsTab.Header = Loc.S("tab.parts");

        UpdateCount();
        StatusText.Text = statusText();
        GameStatusText.Text = gameStatusText();
        ShowInfo();
    }

    void SetStatus(Func<string> text)
    {
        statusText = text;
        StatusText.Text = text();
    }

    // Progress messages are already finished sentences, so keep them as is
    IProgress<string> StatusProgress() => new Progress<string>(s => SetStatus(() => s));

    void OnAbout(object sender, RoutedEventArgs e)
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        MessageBox.Show(this, Loc.F("about.text", v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}"), "Anno Cfg Composer",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // Opens the CrashLog folder in Explorer (created if it doesn't exist yet)
    void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CrashLog.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CrashLog.Folder}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ---------------------------------------------------------------- Game archives

    async Task LoadGame(string dir)
    {
        if (busy) return;
        busy = true;
        try
        {
            CfgTree.ItemsSource = null;
            game?.Dispose();
            game = null;
            var progress = StatusProgress();
            var sw = Stopwatch.StartNew();
            var gf = await Task.Run(() => GameFiles.Load(dir, progress));
            game = gf;
            allCfgs = await Task.Run(() => gf.Entries
                .Where(x => x.Path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                .Select(x => GameFiles.Normalize(x.Path))
                .ToList());
            settings.GameDir = dir;
            settings.Save();
            ApplyFilter();
            int archives = gf.ArchiveCount, files = gf.Entries.Count, cfgs = allCfgs.Count;
            gameStatusText = () => Loc.F("st.game", archives, files, cfgs);
            GameStatusText.Text = gameStatusText();
            GameStatusText.ToolTip = dir;
            SetStatus(() => Loc.S("st.ready"));
        }
        catch (Exception ex)
        {
            SetStatus(() => Loc.F("st.gameFail", ex.Message));
        }
        finally { busy = false; }
    }

    async void OnPickGame(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.S("dlg.gameTitle") };
        if (GameLocator.IsGameDir(settings.GameDir)) dlg.InitialDirectory = settings.GameDir;
        if (dlg.ShowDialog(this) != true) return;
        var dir = dlg.FolderName;
        // If maindata itself was picked, go one level up
        if (!GameLocator.IsGameDir(dir) && Path.GetFileName(dir).Equals("maindata", StringComparison.OrdinalIgnoreCase))
            dir = Path.GetDirectoryName(dir)!;
        if (!GameLocator.IsGameDir(dir))
        {
            MessageBox.Show(this, Loc.S("dlg.notGame"), Loc.S("dlg.gameCaption"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await LoadGame(dir);
    }

    // ---------------------------------------------------------------- Tree / search

    void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchTimer.Stop();
        searchTimer.Start();
    }

    void ApplyFilter()
    {
        var tokens = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Match(string p) => tokens.All(t => p.Contains(t, StringComparison.OrdinalIgnoreCase));
        var list = tokens.Length == 0 ? allCfgs : allCfgs.Where(Match).ToList();

        // Dropped mod folders on top, game files below
        var nodes = new List<CfgTreeNode>();
        foreach (var (root, cfgs) in modCfgs)
        {
            var hits = tokens.Length == 0 ? cfgs : cfgs.Where(p => Match(p) || Match(Path.GetFileName(root))).ToList();
            if (tokens.Length > 0 && hits.Count == 0) continue;
            nodes.Add(CfgTreeNode.BuildMod(root, Loc.F("tree.mod", Path.GetFileName(root)), hits, tokens.Length == 0 ? 0 : 300));
        }
        // Expand everything if there are 300 search results or fewer
        nodes.AddRange(CfgTreeNode.Build(list, tokens.Length == 0 ? 0 : 300));
        CfgTree.ItemsSource = nodes;
        shownCount = list.Count;
        UpdateCount();
    }

    // ---------------------------------------------------------------- Mod folders (drag and drop)

    // Mod folder (the folder containing data) → relative cfg paths inside it (data/author/…)
    readonly SortedDictionary<string, List<string>> modCfgs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a mod folder to the left list. Whether the mod folder itself, its data, or any deeper folder is dropped,
    /// the part before "…/data/" is taken as the mod folder. Not added if it has no cfg.
    /// </summary>
    bool AddModFolder(string dir, bool remember)
    {
        dir = Path.GetFullPath(dir).TrimEnd('\\', '/');
        string root = dir;
        if (!Directory.Exists(Path.Combine(dir, "data")) && FileResolver.FromDiskPath(dir + "/").looseRoot is { } lr) root = lr;

        List<string> cfgs;
        try
        {
            cfgs = Directory.EnumerateFiles(root, "*.cfg", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { cfgs = []; }
        if (cfgs.Count == 0)
        {
            if (remember) SetStatus(() => Loc.F("st.modNoCfg", root));
            return false;
        }

        modCfgs[root] = cfgs;
        if (remember)
        {
            if (!settings.ModFolders.Contains(root, StringComparer.OrdinalIgnoreCase)) settings.ModFolders.Add(root);
            settings.Save();
            int n = cfgs.Count;
            SetStatus(() => Loc.F("st.modAdded", root, n));
        }
        ApplyFilter();
        return true;
    }

    void OnRemoveMod(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CfgTreeNode { IsModRoot: true, ModRoot: { } root }) return;
        modCfgs.Remove(root);
        settings.ModFolders.RemoveAll(f => string.Equals(f, root, StringComparison.OrdinalIgnoreCase));
        settings.Save();
        ApplyFilter();
    }

    void OnAddModFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.S("dlg.modTitle") };
        if (dlg.ShowDialog(this) == true) AddModFolder(dlg.FolderName, true);
    }

    void OnTreeDragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok) CfgTree.Background = (System.Windows.Media.Brush)FindResource("AccentSoft");
        e.Handled = true;
    }

    void OnTreeDragLeave(object sender, DragEventArgs e) => CfgTree.Background = System.Windows.Media.Brushes.Transparent;

    async void OnTreeDrop(object sender, DragEventArgs e)
    {
        CfgTree.Background = System.Windows.Media.Brushes.Transparent;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        string? cfgToOpen = null;
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) AddModFolder(p, true);
            else if (p.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) && File.Exists(p)) cfgToOpen = p;
        }
        if (cfgToOpen != null) await OpenDiskCfg(cfgToOpen);
    }

    Task OpenDiskCfg(string file)
    {
        var (root, vpath) = FileResolver.FromDiskPath(file);
        return ShowCfg(root == null ? file : vpath, new FileResolver { Game = game, LooseRoot = root });
    }

    void UpdateCount() =>
        CountText.Text = shownCount == allCfgs.Count ? Loc.F("cnt.all", allCfgs.Count) : Loc.F("cnt.filtered", shownCount, allCfgs.Count);

    async void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not CfgTreeNode { IsCfg: true } node) return;
        if (node.ModRoot != null)
            await ShowCfg(node.FullPath, new FileResolver { Game = game, LooseRoot = node.ModRoot });   // Mod files first, then the game
        else if (game != null)
            await ShowCfg(node.FullPath, new FileResolver { Game = game });
    }

    void OnCopyTreePath(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CfgTreeNode n) CopyPath(n.FullPath);
    }

    void OnCopyPartPath(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PartNode n) CopyPath(n.Path);
    }

    void CopyPath(string path)
    {
        try { Clipboard.SetText(path); SetStatus(() => Loc.F("st.copied", path)); }
        catch { }
    }

    async void OnOpenDisk(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.S("dlg.openFilter"), Title = Loc.S("dlg.openTitle") };
        if (dlg.ShowDialog(this) != true) return;
        var (root, vpath) = FileResolver.FromDiskPath(dlg.FileName);
        await ShowCfg(root == null ? dlg.FileName : vpath, new FileResolver { Game = game, LooseRoot = root });
    }

    // ---------------------------------------------------------------- 3D view

    async Task ShowCfg(string path, FileResolver resolver)
    {
        loadCts?.Cancel();
        var cts = loadCts = new CancellationTokenSource();
        var options = new SceneOptions { Mirror = MirrorToggle.IsChecked == true, Textures = TextureToggle.IsChecked == true };
        SetStatus(() => Loc.F("st.loading", path));
        LoadingOverlay.Visibility = Visibility.Visible;
        var sw = Stopwatch.StartNew();
        try
        {
            var builder = await Task.Run(() =>
            {
                var b = new SceneBuilder(resolver, options);
                b.Build(path, cts.Token);
                return b;
            }, cts.Token);
            if (cts.IsCancellationRequested) return;

            // Visual elements on the UI thread
            SceneGroup.Children.Clear();
            foreach (var el in DxScene.Create(builder)) SceneGroup.Children.Add(el);
            ViewHint.Visibility = Visibility.Collapsed;
            currentPath = path;
            currentResolver = resolver;
            currentBuilder = builder;
            currentElapsed = sw.Elapsed;
            ExtractButton.IsEnabled = ExtractMenuItem.IsEnabled = GltfButton.IsEnabled = GltfMenuItem.IsEnabled = true;

            ZoomTo(builder.Bounds);
            foreach (var p in builder.Parts) p.IsExpanded = builder.Parts.Count <= 30;
            PartsTree.ItemsSource = builder.Parts;
            ShowInfo();
            var elapsed = currentElapsed;
            SetStatus(() => Loc.F("st.shown", Path.GetFileName(path), builder.ModelCount, builder.TriangleCount, (long)elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus(() => Loc.F("st.loadFail", ex.Message));
        }
        finally
        {
            if (loadCts == cts) LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>For self-test: show a cfg, wait until textures are uploaded, then save the viewport as PNG.</summary>
    internal async Task SnapshotAsync(string cfgPath, FileResolver resolver, string file)
    {
        await ShowCfg(cfgPath, resolver);
        for (int i = 0; i < 15; i++) await Task.Delay(100);
        var bmp = HelixToolkit.Wpf.SharpDX.ViewportExtensions.RenderBitmap(View, 1000, 750);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    /// <summary>Ground grid (XZ plane, -half to half). A darker line every 10 cells.</summary>
    void BuildGrid(int half)
    {
        var minor = new HelixToolkit.SharpDX.LineBuilder();
        var major = new HelixToolkit.SharpDX.LineBuilder();
        for (int i = -half; i <= half; i++)
        {
            var b = i % 10 == 0 ? major : minor;
            b.AddLine(new System.Numerics.Vector3(i, 0, -half), new System.Numerics.Vector3(i, 0, half));
            b.AddLine(new System.Numerics.Vector3(-half, 0, i), new System.Numerics.Vector3(half, 0, i));
        }
        GridMinor.Geometry = minor.ToLineGeometry3D(false);
        GridMajor.Geometry = major.ToLineGeometry3D(false);
    }

    /// <summary>
    /// Fits the camera to the bounds — keeps the view direction, changes only the distance. (Helix DX's ZoomExtents misses small models, so computed here)
    /// </summary>
    void ZoomTo(Rect3D b)
    {
        if (b.IsEmpty || View.Camera is not HelixToolkit.Wpf.SharpDX.ProjectionCamera cam) return;
        var dir = cam.LookDirection;
        if (dir.Length < 1e-9) dir = new Vector3D(-1, -0.8, -1);
        dir.Normalize();
        var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        double radius = Math.Max(0.3, new Vector3D(b.SizeX, b.SizeY, b.SizeZ).Length / 2);
        // FieldOfView is the horizontal FOV — on a wide view the vertical FOV is narrower, so fit to that
        double hfov = (cam as HelixToolkit.Wpf.SharpDX.PerspectiveCamera)?.FieldOfView ?? 45;
        double w = Math.Max(1, View.ActualWidth), h = Math.Max(1, View.ActualHeight);
        double half = Math.Atan(Math.Tan(hfov * Math.PI / 360) * Math.Min(1, h / w));
        double dist = radius / Math.Sin(half) * 1.05;
        cam.Position = center - dir * dist;
        cam.LookDirection = dir * dist;
        cam.UpDirection = new Vector3D(0, 1, 0);
    }

    void OnZoomExtents(object sender, RoutedEventArgs e) =>
        ZoomTo(currentBuilder?.Bounds ?? Rect3D.Empty);

    async void OnViewOptionChanged(object sender, RoutedEventArgs e)
    {
        settings.Mirror = MirrorToggle.IsChecked == true;
        settings.Textures = TextureToggle.IsChecked == true;
        settings.Save();
        if (currentPath != null && currentResolver != null) await ShowCfg(currentPath, currentResolver);
    }

    void OnGridChanged(object sender, RoutedEventArgs e) =>
        GridVisual.Visibility = GridToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- Graphics card

    void BuildGpuMenu()
    {
        GpuMenu.Items.Clear();
        var auto = new MenuItem { Tag = "", IsCheckable = true };
        auto.Click += OnGpuMenu;
        GpuMenu.Items.Add(auto);
        foreach (var a in GpuAdapters.List())
        {
            var mi = new MenuItem { Header = $"{a.Name}  ({a.VideoMemory / 1048576:N0} MB)", Tag = a.Name, IsCheckable = true };
            mi.Click += OnGpuMenu;
            GpuMenu.Items.Add(mi);
        }
        UpdateGpuChecks();
    }

    void UpdateGpuChecks()
    {
        foreach (MenuItem m in GpuMenu.Items) m.IsChecked = (string)m.Tag == (settings.GpuAdapter ?? "");
    }

    async void OnGpuMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string name }) return;
        settings.GpuAdapter = name.Length == 0 ? null : name;
        settings.Save();
        UpdateGpuChecks();
        var next = GpuAdapters.Pick(settings.GpuAdapter);
        if (next?.Index == gpu?.Index) return;

        // Switch device and reload the current cfg (textures and meshes are re-uploaded to the new device)
        var old = effects;
        gpu = next;
        effects = gpu != null ? new HelixToolkit.SharpDX.DefaultEffectsManager(gpu.Index) : new HelixToolkit.SharpDX.DefaultEffectsManager();
        SceneGroup.Children.Clear();
        View.EffectsManager = effects;
        old.Dispose();
        GpuStatusText.Text = gpu == null ? "" : Loc.F("st.gpu", gpu.Name);
        if (currentPath != null && currentResolver != null) await ShowCfg(currentPath, currentResolver);
    }

    /// <summary>For self-test: show a cfg, rotate the camera for seconds, and return the frames per second measured by Helix.</summary>
    internal async Task<(double helixFps, int wpfFrames)> MeasureFpsAsync(string cfgPath, FileResolver resolver, double seconds)
    {
        await ShowCfg(cfgPath, resolver);
        await Task.Delay(1000);
        View.ShowFrameRate = true;
        int frames = 0;
        if (View.Camera is not HelixToolkit.Wpf.SharpDX.ProjectionCamera cam) return (0, 0);
        var target = cam.Position + cam.LookDirection;
        double dist = cam.LookDirection.Length, height = -cam.LookDirection.Y;
        double r = Math.Sqrt(Math.Max(1e-6, dist * dist - height * height));
        var sw = Stopwatch.StartNew();
        void Tick(object? s, EventArgs e)
        {
            frames++;
            double a = sw.Elapsed.TotalSeconds * 1.5;
            cam.Position = new Point3D(target.X + r * Math.Cos(a), target.Y + height, target.Z + r * Math.Sin(a));
            cam.LookDirection = target - cam.Position;
        }
        System.Windows.Media.CompositionTarget.Rendering += Tick;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        System.Windows.Media.CompositionTarget.Rendering -= Tick;
        return (View.FrameRate, frames);
    }

    void OnAxesChanged(object sender, RoutedEventArgs e)
    {
        View.ShowCoordinateSystem = AxesMenuItem.IsChecked;
        View.ShowViewCube = AxesMenuItem.IsChecked;
    }

    // ---------------------------------------------------------------- Right-hand details

    void ShowInfo()
    {
        var b = currentBuilder;
        if (b == null || currentPath == null || currentResolver == null)
        {
            InfoEmpty.Visibility = Visibility.Visible;
            InfoPanel.Visibility = Visibility.Collapsed;
            return;
        }
        InfoEmpty.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Visible;

        InfoName.Text = Path.GetFileName(currentPath);
        InfoPath.Text = currentPath;
        InfoSource.Text = Loc.F("info.source", currentResolver.Source(currentPath));
        StatCfg.Text = Loc.F("{0:N0}", b.CfgCount);
        StatModels.Text = Loc.F("{0:N0}", b.ModelCount);
        StatTris.Text = Loc.F("{0:N0}", b.TriangleCount);
        StatTime.Text = Loc.F("stat.ms", currentElapsed.TotalMilliseconds);

        DecalNote.Visibility = b.DecalCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        DecalNote.Text = Loc.F("info.decals", b.DecalCount);

        int problems = b.MissingFiles.Count + b.MissingTextures.Count + b.BrokenFiles.Count;
        WarnBox.Visibility = problems > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarnText.Text = Loc.F("warn.summary", problems);
        IssuesTab.Header = Loc.F("tab.issues", problems);
        IssuesBox.Text = DescribeIssues(b);
    }

    static string DescribeIssues(SceneBuilder b)
    {
        var sb = new StringBuilder();
        void Section(string titleKey, ICollection<string> items)
        {
            if (items.Count == 0) return;
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"{Loc.S(titleKey)} ({items.Count})");
            foreach (var i in items.Take(500)) sb.AppendLine("  " + i);
            if (items.Count > 500) sb.AppendLine("  …");
        }
        Section("info.missing", b.MissingFiles);
        Section("info.missingTex", b.MissingTextures);
        Section("info.broken", b.BrokenFiles.Select(kv => $"{kv.Key} — {kv.Value}").ToList());
        Section("info.skipped", b.SkippedFiles);
        return sb.Length == 0 ? Loc.S("issues.none") : sb.ToString();
    }

    // ---------------------------------------------------------------- Copyright notice

    /// <summary>Before the first extract / glTF export: the game's models and textures belong to Ubisoft — don't redistribute. Shown once.</summary>
    void ShowCopyrightNoticeOnce()
    {
        if (settings.CopyrightNoticeShown) return;
        MessageBox.Show(this, Loc.S("dlg.copyright"), Loc.S("dlg.copyrightTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        settings.CopyrightNoticeShown = true;
        settings.Save();
    }

    // ---------------------------------------------------------------- glTF

    async void OnExportGltf(object sender, RoutedEventArgs e)
    {
        if (currentBuilder?.Scene is not { } scene || currentResolver == null || currentPath == null || busy) return;
        ShowCopyrightNoticeOnce();
        var dlg = new SaveFileDialog
        {
            Filter = Loc.S("dlg.gltfFilter"),
            Title = Loc.S("dlg.gltfTitle"),
            FileName = Path.GetFileNameWithoutExtension(currentPath) + ".glb",
        };
        if (Directory.Exists(settings.ExtractDir)) dlg.InitialDirectory = settings.ExtractDir;
        if (dlg.ShowDialog(this) != true) return;
        var file = dlg.FileName;
        settings.ExtractDir = Path.GetDirectoryName(file);
        settings.Save();

        busy = true;
        GltfButton.IsEnabled = GltfMenuItem.IsEnabled = false;
        LoadingOverlay.Visibility = Visibility.Visible;
        SetStatus(() => Loc.S("st.gltf"));
        try
        {
            var res = currentResolver;
            bool mirror = MirrorToggle.IsChecked == true;
            var r = await Task.Run(() => GltfExporter.Export(scene, res, mirror, file));
            double mb = r.Bytes / 1048576.0;
            SetStatus(() => Loc.F("st.gltfDone", file, mb));
            if (MessageBox.Show(this, Loc.F("dlg.gltfDone", file, r.Nodes, r.Meshes, r.Textures, mb), Loc.S("dlg.gltfTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus(() => Loc.F("st.gltfFail", ex.Message));
            MessageBox.Show(this, ex.Message, Loc.S("dlg.gltfTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            busy = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            GltfButton.IsEnabled = GltfMenuItem.IsEnabled = currentPath != null;
        }
    }

    // ---------------------------------------------------------------- Extraction

    async void OnExtract(object sender, RoutedEventArgs e)
    {
        if (currentPath == null || currentResolver == null || busy) return;
        ShowCopyrightNoticeOnce();
        var dlg = new OpenFolderDialog { Title = Loc.S("dlg.extractTitle") };
        if (Directory.Exists(settings.ExtractDir)) dlg.InitialDirectory = settings.ExtractDir;
        if (dlg.ShowDialog(this) != true) return;
        var dest = dlg.FolderName;
        settings.ExtractDir = dest;
        settings.Save();

        busy = true;
        ExtractButton.IsEnabled = ExtractMenuItem.IsEnabled = false;
        try
        {
            var res = currentResolver;
            var root = currentPath;
            var progress = StatusProgress();
            SetStatus(() => Loc.S("st.deps"));
            var deps = await Task.Run(() => DependencyCollector.Collect(res, root));
            int written = await Task.Run(() => DependencyCollector.Extract(res, deps.Found, dest, progress));
            SetStatus(() => Loc.F("st.extracted", written, dest));

            var msg = new StringBuilder(Loc.F("dlg.extractDone", written, dest));
            if (deps.Missing.Count > 0)
            {
                msg.Append(Loc.F("dlg.extractMissing", deps.Missing.Count));
                foreach (var m in deps.Missing.Take(15)) msg.Append("  ").Append(m).Append('\n');
                if (deps.Missing.Count > 15) msg.Append("  …\n");
            }
            msg.Append(Loc.S("dlg.openFolder"));
            if (MessageBox.Show(this, msg.ToString(), Loc.S("dlg.extractCaption"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dest}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus(() => Loc.F("st.extractFail", ex.Message));
            MessageBox.Show(this, ex.Message, Loc.S("dlg.extractFailCaption"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            busy = false;
            ExtractButton.IsEnabled = ExtractMenuItem.IsEnabled = currentPath != null;
        }
    }
}
