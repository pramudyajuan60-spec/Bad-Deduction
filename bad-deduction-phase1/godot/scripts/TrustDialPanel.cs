using BadDeduction.Core;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Panels 2+3 (NPC Inspection + Trust/Suspicion dials): big Trust 72/100 and
/// Suspicion 18/100 gauges for the selected NPC, from the PLAYER's perspective.
/// Reused inside NpcInspect and the Dialogue header.
/// </summary>
public partial class TrustDialPanel : VBoxContainer, IPanel
{
    private ProgressBar _trustBar = null!;
    private ProgressBar _suspicionBar = null!;
    private Label _trustNum = null!;
    private Label _suspicionNum = null!;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddChild(row);

        var tv = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var tl = UiTheme.HeaderLabel("TRUST", 16);
        tl.HorizontalAlignment = HorizontalAlignment.Center;
        tv.AddChild(tl);
        _trustBar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 18) };
        tv.AddChild(_trustBar);
        _trustNum = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _trustNum.AddThemeFontSizeOverride("font_size", 22);
        _trustNum.AddThemeColorOverride("font_color", UiTheme.Green);
        tv.AddChild(_trustNum);
        row.AddChild(tv);

        var sv = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var sl = UiTheme.HeaderLabel("SUSPICION", 16);
        sl.HorizontalAlignment = HorizontalAlignment.Center;
        sv.AddChild(sl);
        _suspicionBar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 18) };
        sv.AddChild(_suspicionBar);
        _suspicionNum = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _suspicionNum.AddThemeFontSizeOverride("font_size", 22);
        _suspicionNum.AddThemeColorOverride("font_color", UiTheme.Red);
        sv.AddChild(_suspicionNum);
        row.AddChild(sv);

        var note = UiTheme.DimLabel("Affected by: dialogue, actions, lies, contradictions, observed behavior, evidence.", 12);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(note);
    }

    /// <summary>Trust/suspicion of <paramref name="npcId"/> as seen BY the player.</summary>
    public void SetFor(GameSession s, string npcId)
    {
        var view = s.Social.View("c_player", npcId);
        SetValues(view.Trust, view.Suspicion);
    }

    public void SetValues(int trust, int suspicion)
    {
        _trustBar.Value = trust;
        _suspicionBar.Value = suspicion;
        _trustNum.Text = $"{trust} / 100";
        _suspicionNum.Text = $"{suspicion} / 100";
    }

    public void Refresh() { }
}
