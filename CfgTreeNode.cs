using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace AnnoCfgComposer;

/// <summary>
/// One item in the left tree: a folder, a cfg, or the group for a dropped mod folder.
/// Levels with a single subfolder and no cfg are merged into one line, like "data/base/graphics".
/// shared/ holds the patch's versions with props pre-merged (same buildings as data/).
/// </summary>
public sealed class CfgTreeNode : INotifyPropertyChanged
{
    public string Name { get; private set; }
    public string FullPath { get; private set; }
    public bool IsCfg { get; }
    public List<CfgTreeNode> Children { get; } = [];
    public int CfgCount { get; private set; }

    /// <summary>For nodes from a mod folder, that mod folder (the folder containing data). null for game files.</summary>
    public string? ModRoot { get; private set; }
    /// <summary>Top node of a mod group (right-click "Remove from list").</summary>
    public bool IsModRoot { get; private set; }
    public Visibility ModRootVisibility => IsModRoot ? Visibility.Visible : Visibility.Collapsed;

    // Segoe icon font: document E8A5 · folder E8B7 · box E7B8
    static readonly string CfgIcon = ((char)0xE8A5).ToString();
    static readonly string FolderIcon = ((char)0xE8B7).ToString();
    static readonly string ModIcon = ((char)0xE7B8).ToString();
    static readonly Brush FolderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0x92, 0x1E)));
    static readonly Brush CfgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB)));
    static readonly Brush ModBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1A, 0x9A, 0x5B)));

    public string Icon => IsModRoot ? ModIcon : IsCfg ? CfgIcon : FolderIcon;
    public Brush IconBrush => IsModRoot ? ModBrush : IsCfg ? CfgBrush : FolderBrush;

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    public string CountText => IsCfg ? "" : CfgCount.ToString("N0");
    public FontWeight Weight => IsCfg || IsModRoot ? FontWeights.SemiBold : FontWeights.Normal;

    bool isExpanded;
    public bool IsExpanded
    {
        get => isExpanded;
        set { if (isExpanded != value) { isExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    CfgTreeNode(string name, string fullPath, bool isCfg, string? modRoot = null)
    {
        Name = name;
        FullPath = fullPath;
        IsCfg = isCfg;
        ModRoot = modRoot;
    }

    /// <summary>List of cfg paths → top-level nodes. If modRoot is set, all nodes belong to that mod.</summary>
    public static List<CfgTreeNode> Build(IEnumerable<string> cfgPaths, int expandAllBelow, string? modRoot = null)
    {
        var root = new CfgTreeNode("", "", false, modRoot);
        var folders = new Dictionary<string, CfgTreeNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };

        CfgTreeNode Folder(string path)
        {
            if (folders.TryGetValue(path, out var f)) return f;
            int slash = path.LastIndexOf('/');
            var parent = Folder(slash < 0 ? "" : path[..slash]);
            f = new CfgTreeNode(slash < 0 ? path : path[(slash + 1)..], path, false, modRoot);
            parent.Children.Add(f);
            folders[path] = f;
            return f;
        }

        int total = 0;
        foreach (var p in cfgPaths)
        {
            int slash = p.LastIndexOf('/');
            var folder = Folder(slash < 0 ? "" : p[..slash]);
            folder.Children.Add(new CfgTreeNode(p[(slash + 1)..], p, true, modRoot));
            total++;
        }

        root.Finish();
        bool expandAll = total <= expandAllBelow;
        foreach (var c in root.Children) c.Expand(expandAll);
        return root.Children;
    }

    /// <summary>One mod folder → a "[Mod] name" group node (with the mod's cfg tree below it). Mod groups are always expanded.</summary>
    public static CfgTreeNode BuildMod(string modRoot, string label, IReadOnlyCollection<string> cfgPaths, int expandAllBelow)
    {
        var node = new CfgTreeNode(label, modRoot, false, modRoot) { IsModRoot = true };
        node.Children.AddRange(Build(cfgPaths, Math.Max(expandAllBelow, 40), modRoot));
        node.CfgCount = cfgPaths.Count;
        node.IsExpanded = true;
        return node;
    }

    int Finish()
    {
        if (IsCfg) return 1;
        CfgCount = 0;
        foreach (var c in Children) CfgCount += c.Finish();
        Children.Sort((a, b) => a.IsCfg != b.IsCfg ? (a.IsCfg ? 1 : -1) : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        // Merge levels that have only one subfolder
        while (Children.Count == 1 && !Children[0].IsCfg && Name.Length > 0)
        {
            var only = Children[0];
            Name = Name + "/" + only.Name;
            FullPath = only.FullPath;
            Children.Clear();
            Children.AddRange(only.Children);
        }
        return CfgCount;
    }

    void Expand(bool all)
    {
        // Expand everything for small search results, otherwise only the top
        IsExpanded = !IsCfg && (all || Children.Count(c => !c.IsCfg) <= 1);
        if (all) foreach (var c in Children) c.Expand(true);
    }
}
