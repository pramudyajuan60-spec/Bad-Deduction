using BadDeduction.Core;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Panel 2 (NPC Inspection): dossier for the selected NPC — portrait placeholder,
/// public facts, relationship from the player's perspective, trust dials, last known
/// activity (only when shared-location knowledge allows), statements they've made,
/// and Talk / Interview / Interrogate actions.
/// </summary>
public partial class NpcInspectPanel : PanelContainer, IPanel
{
    private Label _nameLabel = null!;
    private Label _factsLabel = null!;
    private Label _relationLabel = null!;
    private Label _activityLabel = null!;
    private Label _infoLabel = null!;
    private Label _portraitLetter = null!;
    private TrustDialPanel _dials = null!;
    private Button _talkBtn = null!;
    private Button _interviewBtn = null!;
    private Button _interrogateBtn = null!;

    public override void _Ready()
    {
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        _nameLabel = UiTheme.TitleLabel("No one selected", 26);
        root.AddChild(_nameLabel);

        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", 18);
        cols.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AddChild(cols);

        var portrait = new ColorRect
        {
            Color = UiTheme.PanelLight,
            CustomMinimumSize = new Vector2(150, 190),
        };
        _portraitLetter = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _portraitLetter.AddThemeFontSizeOverride("font_size", 72);
        _portraitLetter.AddThemeColorOverride("font_color", UiTheme.GoldDim);
        _portraitLetter.SetAnchorsPreset(LayoutPreset.FullRect);
        portrait.AddChild(_portraitLetter);
        cols.AddChild(portrait);

        var info = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        info.AddThemeConstantOverride("separation", 6);
        _factsLabel = new Label();
        info.AddChild(_factsLabel);
        _relationLabel = UiTheme.HeaderLabel("", 17);
        info.AddChild(_relationLabel);
        _dials = (TrustDialPanel)GD.Load<PackedScene>("res://scenes/TrustDial.tscn").Instantiate();
        info.AddChild(_dials);
        _activityLabel = new Label();
        info.AddChild(_activityLabel);
        info.AddChild(UiTheme.GoldRule());
        info.AddChild(UiTheme.HeaderLabel("Known information", 16));
        _infoLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _infoLabel.AddThemeColorOverride("font_color", UiTheme.Dim);
        info.AddChild(_infoLabel);
        cols.AddChild(info);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 10);
        _talkBtn = new Button { Text = "Talk" };
        _talkBtn.Pressed += () => GetNode<Main>("/root/Main").ShowPanel("dialogue");
        _interviewBtn = new Button { Text = "Interview" };
        _interviewBtn.Pressed += () => DoInterview(pressured: false);
        _interrogateBtn = new Button { Text = "Interrogate" };
        _interrogateBtn.Pressed += () => DoInterview(pressured: true);
        actions.AddChild(_talkBtn);
        actions.AddChild(_interviewBtn);
        actions.AddChild(_interrogateBtn);
        root.AddChild(actions);
    }

    public void Refresh()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        var id = g.SelectedNpcId;
        var has = id != "" && s.State.World.Characters.ContainsKey(id);
        _talkBtn.Disabled = !has;
        _interviewBtn.Disabled = !has;
        _interrogateBtn.Disabled = !has;
        if (!has)
        {
            _nameLabel.Text = "No one selected";
            _factsLabel.Text = "Pick someone from the World view, the graph, or the board.";
            _relationLabel.Text = "";
            _activityLabel.Text = "";
            _infoLabel.Text = "";
            _portraitLetter.Text = "?";
            return;
        }

        var c = s.World.GetCharacter(id);
        var pub = s.View.PublicProfile(id)!;
        _nameLabel.Text = pub.DisplayName;
        _portraitLetter.Text = pub.DisplayName.Substring(0, 1);
        _factsLabel.Text = $"Age {pub.Age}  •  {pub.OccupationId}  •  {pub.Kind}";

        var rel = s.Social.View("c_player", id);
        _relationLabel.Text = $"Relationship: {rel.Kind?.ToString() ?? "Stranger"} ({rel.Band})";
        _dials.SetValues(rel.Trust, rel.Suspicion);

        // Light knowledge gating: whereabouts shown only when you share their location.
        var playerLoc = s.World.GetCharacter("c_player").CurrentLocationId;
        _activityLabel.Text = c.CurrentLocationId == playerLoc
            ? $"Last known activity: {c.Activity} (here)"
            : "Last known activity: unknown";

        var stmts = s.Investigate.StatementsBy(id);
        var knownEvents = 0;
        foreach (var e in s.View.JournalEntries())
            if (e.Participants.Contains(id)) knownEvents++;
        _infoLabel.Text = $"Statements on record: {stmts.Count}\nEvents you've seen them in: {knownEvents}";
    }

    private void DoInterview(bool pressured)
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        var id = g.SelectedNpcId;

        string topic = $"whereabouts:{s.State.TotalMinutes}";
        string? crimeId = null;
        foreach (var crime in s.Crime.AllCrimes())
        {
            var scene = s.Crime.GetScene(crime.SceneId);
            if (!scene.IsDiscovered) continue;
            topic = $"event:{crime.IncidentEventId}";
            crimeId = crime.Id;
            break;
        }

        var result = pressured
            ? s.Investigate.Interrogate("c_player", id, topic, crimeId)
            : s.Investigate.Interview("c_player", id, topic, crimeId);

        var contra = s.Investigate.ContradictionsFor(result.StatementId);
        var text = $"{result.Answer}\n\n— recorded as a statement." +
                   (contra.Count > 0 ? $"\nContradictions flagged: {contra.Count}" : "");
        var d = new AcceptDialog { Title = pressured ? "Interrogation" : "Interview" };
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0) };
        d.AddChild(l);
        GetNode<Main>("/root/Main").AddChild(d);
        d.PopupCentered();
        Refresh();
    }
}
