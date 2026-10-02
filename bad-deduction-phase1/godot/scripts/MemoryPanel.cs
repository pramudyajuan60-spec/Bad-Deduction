using BadDeduction.Cognition;
using BadDeduction.Core;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Panel 6 (NPC Memory): the selected NPC's memories (Memories/Details tabs), with
/// foggy / distorted markers. A playable-dossier simplification: it shows what the
/// sheet shows; memories never contain hidden-role truth regardless.
/// </summary>
public partial class MemoryPanel : PanelContainer, IPanel
{
    private Label _whoLabel = null!;
    private ItemList _list = null!;
    private RichTextLabel _details = null!;

    public override void _Ready()
    {
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        _whoLabel = UiTheme.TitleLabel("", 24);
        root.AddChild(_whoLabel);

        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(tabs);

        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        var memScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, Name = "Memories" };
        _list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        memScroll.AddChild(_list);
        tabs.AddChild(memScroll);

        _details = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        var detScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, Name = "Details" };
        _details.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        detScroll.AddChild(_details);
        tabs.AddChild(detScroll);
    }

    public void Refresh()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        var id = g.SelectedNpcId != "" && s.State.World.Characters.ContainsKey(g.SelectedNpcId)
            ? g.SelectedNpcId
            : "c_player";
        var pub = s.View.PublicProfile(id)!;
        _whoLabel.Text = $"{pub.DisplayName} — Memory";

        var mems = s.Cognition.GetMemories(id);
        _list.Clear();
        foreach (var m in mems)
        {
            var markers = "";
            if (m.IsFoggy) markers += " [foggy]";
            if (m.IsDistorted) markers += " [hazy]";
            var t = new GameTime(m.RecordedAt);
            _list.AddItem($"Day {t.Day} {t.Hour:00}:{t.Minute:00} · {m.Source} · {m.Confidence}% — {m.Summary}{markers}");
        }
        if (mems.Count == 0) _list.AddItem("(no memories recorded)");

        var bySource = new Dictionary<MemorySource, int>();
        var foggy = 0;
        var distorted = 0;
        var confSum = 0;
        foreach (var m in mems)
        {
            bySource.TryGetValue(m.Source, out var n);
            bySource[m.Source] = n + 1;
            if (m.IsFoggy) foggy++;
            if (m.IsDistorted) distorted++;
            confSum += m.Confidence;
        }
        _details.Clear();
        _details.AppendText($"[color=#d4af37]Total memories:[/color] {mems.Count}\n");
        foreach (var (src, n) in bySource)
            _details.AppendText($"[color=#d4af37]{src}:[/color] {n}\n");
        _details.AppendText($"[color=#d4af37]Average confidence:[/color] {(mems.Count == 0 ? 0 : confSum / mems.Count)}%\n");
        _details.AppendText($"[color=#d4af37]Foggy:[/color] {foggy}   [color=#d4af37]Distorted:[/color] {distorted}\n\n");
        _details.AppendText("[color=#9a958a]Memories fade with time. Hazy ones may mislead — cross-check them.[/color]");
    }
}
