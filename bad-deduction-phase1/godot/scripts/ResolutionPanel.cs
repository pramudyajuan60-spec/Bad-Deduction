using BadDeduction.Core;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Shown past Day 7: what the PLAYER established — discovered crimes, own hypotheses,
/// investigation stats. Only player-known facts; never hidden truth.
/// </summary>
public partial class ResolutionPanel : PanelContainer, IPanel
{
    private VBoxContainer _body = null!;

    public override void _Ready()
    {
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var card = new PanelContainer { CustomMinimumSize = new Vector2(640, 0) };
        center.AddChild(card);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 560) };
        card.AddChild(scroll);
        _body = new VBoxContainer();
        _body.AddThemeConstantOverride("separation", 8);
        _body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_body);
    }

    public void Refresh()
    {
        foreach (var c in _body.GetChildren()) c.QueueFree();
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;

        _body.AddChild(UiTheme.TitleLabel("THE WEEK CONCLUDES", 30));
        _body.AddChild(UiTheme.DimLabel("Seven days have passed. This is what you established — nothing more."));
        _body.AddChild(UiTheme.GoldRule());

        _body.AddChild(UiTheme.HeaderLabel("Crimes discovered"));
        var anyCrime = false;
        foreach (var crime in s.Crime.AllCrimes())
        {
            var scene = s.Crime.GetScene(crime.SceneId);
            if (!scene.IsDiscovered) continue;
            anyCrime = true;
            var victim = s.View.PublicProfile(crime.VictimId)?.DisplayName ?? crime.VictimId;
            var loc = s.Content.GetLocation(crime.LocationId).Name;
            var t = new GameTime(crime.OccurredAt);
            _body.AddChild(new Label { Text = $"• {crime.DefinitionId} — {victim} at {loc} (Day {t.Day} {t.Hour:00}:{t.Minute:00})" });
        }
        if (!anyCrime) _body.AddChild(UiTheme.DimLabel("No crime was ever discovered. The city keeps its secrets."));

        _body.AddChild(UiTheme.GoldRule());
        _body.AddChild(UiTheme.HeaderLabel("Your hypotheses"));
        var anyHyp = false;
        foreach (var h in s.Investigate.AllHypotheses())
        {
            if (h.OwnerId is not null) continue; // shared board only
            anyHyp = true;
            _body.AddChild(new Label
            {
                Text = $"• {h.Description} — confidence {h.Confidence}/100 ({h.SupportingEvidenceIds.Count} for, {h.RefutingEvidenceIds.Count} against)",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            });
        }
        if (!anyHyp) _body.AddChild(UiTheme.DimLabel("You proposed no hypotheses."));

        _body.AddChild(UiTheme.GoldRule());
        _body.AddChild(UiTheme.HeaderLabel("By the numbers"));
        var contraCount = 0;
        foreach (var e in s.Events.Query(typePrefix: "investigation.contradiction_found")) contraCount++;
        var dlgCount = 0;
        foreach (var e in s.Events.Query(typePrefix: "dialogue.exchanged")) dlgCount++;
        _body.AddChild(new Label { Text = $"Journal entries: {s.View.JournalEntries().Count}   Interviews/dialogues: {dlgCount}   Contradictions caught: {contraCount}" });

        _body.AddChild(UiTheme.DimLabel("The hidden truth stays hidden — as it should. Play again with a new seed."));
        var again = new Button { Text = "New Run" };
        again.Pressed += () => { Hide(); GetNode<Main>("/root/Main").ShowNewRun(); };
        _body.AddChild(again);
    }
}
