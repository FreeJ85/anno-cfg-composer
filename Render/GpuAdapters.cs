using SharpDX.DXGI;

namespace AnnoCfgComposer.Render;

/// <summary>
/// List of graphics cards (DXGI order = the index used by Helix DefaultEffectsManager(adapterIndex)).
///
/// ⚠ 2026-10-19: cards not connected to a display (0 outputs) must not be used.
/// Creating the device on a laptop RTX 4050 (0 outputs) made Helix's WPF presentation (D3DImage shared surface)
/// fail to cross between cards, re-initialize endlessly, and crash with a stack overflow.
/// On the display-connected Intel Arc, rotating the trading post (kontor_01) runs at 60fps (capped by the refresh rate).
/// </summary>
public static class GpuAdapters
{
    public sealed record Adapter(int Index, string Name, long VideoMemory);

    /// <summary>Usable card = not a software adapter and has at least one display output.</summary>
    public static List<Adapter> List()
    {
        var list = new List<Adapter>();
        try
        {
            using var f = new Factory1();
            for (int i = 0; i < f.GetAdapterCount1(); i++)
            {
                using var a = f.GetAdapter1(i);
                var d = a.Description1;
                if ((d.Flags & AdapterFlags.Software) != 0 || a.GetOutputCount() == 0) continue;
                list.Add(new Adapter(i, d.Description.Trim(), d.DedicatedVideoMemory));
            }
        }
        catch { }
        return list;
    }

    /// <summary>The saved card if set and usable, otherwise the display-connected card with the most dedicated memory.</summary>
    public static Adapter? Pick(string? savedName)
    {
        var list = List();
        if (!string.IsNullOrEmpty(savedName) && list.FirstOrDefault(a => a.Name == savedName) is { } saved) return saved;
        return list.OrderByDescending(a => a.VideoMemory).FirstOrDefault();
    }
}
