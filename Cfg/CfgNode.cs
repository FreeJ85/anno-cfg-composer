using System.Globalization;
using System.Net;
using System.Text;

namespace AnnoCfgComposer.Cfg;

/// <summary>
/// cfg tag tree. cfg looks like XML but is not XML —
/// `&lt;Common [ModelCommon]&gt;&lt;/Common [ModelCommon]&gt;` has tags containing spaces and brackets, and there may be more than one root,
/// so XmlDocument can't read it. The tag name is taken whole, up to '&gt;'.
/// </summary>
public sealed class CfgNode
{
    public string Name { get; }
    public string Text { get; internal set; } = "";
    public List<CfgNode> Children { get; } = [];

    public CfgNode(string name) => Name = name;

    public CfgNode? Child(string name) => Children.FirstOrDefault(c => c.Name == name);
    public IEnumerable<CfgNode> ChildrenNamed(string name) => Children.Where(c => c.Name == name);
    public string? Value(string name) => Child(name)?.Text;

    public double Num(string name, double fallback = 0) =>
        double.TryParse(Value(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>ConfigType of a Config node (MAIN, MODEL, FILE, MATERIAL, ORIENTATION_TRANSFORM …).</summary>
    public string? ConfigType => Value("ConfigType");

    public IEnumerable<CfgNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    /// <summary>Places the whole document under an unnamed root.</summary>
    public static CfgNode Parse(string text)
    {
        var root = new CfgNode("");
        var stack = new List<CfgNode> { root };
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            int lt = text.IndexOf('<', i);
            if (lt < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, lt - i);

            if (string.CompareOrdinal(text, lt, "<!--", 0, 4) == 0)
            {
                int endc = text.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = endc < 0 ? text.Length : endc + 3;
                continue;
            }
            int gt = text.IndexOf('>', lt + 1);
            if (gt < 0) break;
            string tag = text.Substring(lt + 1, gt - lt - 1);
            i = gt + 1;
            if (tag.StartsWith('?') || tag.StartsWith('!')) continue;

            var top = stack[^1];
            if (tag.StartsWith('/'))
            {
                string name = tag[1..];
                // Closes up to the matching open tag. Ignored if there is no match.
                int at = stack.FindLastIndex(n => n.Name == name);
                if (at > 0)
                {
                    top.Text = WebUtility.HtmlDecode(sb.ToString().Trim());
                    sb.Clear();
                    stack.RemoveRange(at, stack.Count - at);
                }
                continue;
            }

            sb.Clear(); // Drop leading whitespace of nodes that get children
            bool selfClose = tag.EndsWith('/');
            var node = new CfgNode(selfClose ? tag[..^1].Trim() : tag);
            top.Children.Add(node);
            if (!selfClose) stack.Add(node);
        }
        return root;
    }
}
