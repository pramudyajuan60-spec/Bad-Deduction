using BadDeduction.Core;
using BadDeduction.Investigation;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Panel 5 (Timeline): Day 1–7 strip, the player's knowledge-gated event list for the
/// selected day, and a Compare Statements box surfacing flagged contradictions.
/// </summary>
public partial class TimelinePanel : PanelContainer, IPanel
{
    private HBoxContainer _dayBar = null!;
    private RichTextLabel _events = null!;
    private RichTextLabel _compare = null!;
    private int _day = 1;

    public override void _Ready()
    {
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        root.AddChild(UiTheme.TitleLabel("Timeline", 24));
        _dayBar = new HBoxContainer();
        _dayBar.AddThemeConstantOverride("separation", 6);
        root.AddChild(_dayBar);

        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", 16);
        cols.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AddChild(cols);

        _events = new RichTextLabel { BbcodeEnabled = true, ScrollFollowing = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cols.AddChild(_events);
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddChild(UiTheme.HeaderLabel("Compare Statements", 17));
        _compare = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        right.AddChild(_compare);
        cols.AddChild(right);
    }

    public void Refresh()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;

        foreach (var c in _dayBar.GetChildren()) c.QueueFree();
        var group = new ButtonGroup();
        for (var d = 1; d <= 7; d++)
        {
            var b = UiTheme.MenuButton($"Day {d}");
            b.ButtonGroup = group;
            var day = d;
            b.Pressed += () => { _day = day; Refresh(); };
            if (d == _day) b.ButtonPressed = true;
            if (d == g.CurrentDay) b.Text = $"Day {d} ◀";
            _dayBar.AddChild(b);
        }

        _events.Clear();
        var any = false;
        foreach (var e in s.View.JournalEntries())
        {
            var t = new GameTime(e.Timestamp);
            if (t.Day != _day) continue;
            any = true;
            _events.AppendText($"[color=#d4af37]{t.Hour:00}:{t.Minute:00}[/color]  {e.Type}\n");
        }
        if (!any) _events.AppendText("[color=#9a958a]Nothing you know of happened this day.[/color]");

        _compare.Clear();
        var found = 0;
        foreach (var e in s.Events.Query(typePrefix: "investigation.contradiction_found"))
        {
            if (!e.Data.TryGetValue("contradiction", out var cid)) continue;
            Contradiction contra;
            try { contra = s.Investigate.GetContradiction(cid); }
            catch { continue; }
            if (!contra.Flagged) continue;
            var stmt = s.Investigate.GetStatement(contra.StatementId);
            var who = s.View.PublicProfile(stmt.SpeakerId)?.DisplayName ?? stmt.SpeakerId;
            var t = new GameTime(contra.Timestamp);
            _compare.AppendText($"[color=#c0392b]⚠ {who}[/color] [color=#9a958a](Day {t.Day})[/color]\n{contra.Reason}\n\n");
            found++;
            if (found >= 8) break;
        }
        if (found == 0) _compare.AppendText("[color=#9a958a]No contradictions caught yet. Interrogate suspects.[/color]");
    }
}
