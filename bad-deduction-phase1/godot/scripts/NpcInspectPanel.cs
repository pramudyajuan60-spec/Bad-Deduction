using BadDeduction.Core;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Panel 2 (NPC Inspection): dossier for the selected NPC — procedural avatar,
/// public facts, relationship from the player's perspective, trust dials, last known
/// activity (only when shared-location knowledge allows), statements they've made,
/// observed routines (knowledge-gated: only blocks the player actually observed via
/// <c>Manipulation.ObserveRoutines</c>, unlearned stretches render as "???"),
/// and Talk / Interview / Interrogate actions.
/// </summary>
public partial class NpcInspectPanel : PanelContainer, IPanel
{
    private Label _nameLabel = null!;
    private Label _factsLabel = null!;
    private Label _relationLabel = null!;
    private Label _activityLabel = null!;
    private Label _infoLabel = null!;
    private Label _scheduleLabel = null!;
    private Button _observeBtn = null!;
    private NpcPortrait _portrait = null!;
    private TrustDialPanel _dials = null!;
    private Button _talkBtn = null!;
    private Button _interviewBtn = null!;
    private Button _interrogateBtn = null!;
    private string _lastScheduleNpc = "";

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

        var portraitBox = new PanelContainer
        {
            CustomMinimumSize = new Vector2(150, 190),
        };
        var portraitCenter = new CenterContainer();
        _portrait = new NpcPortrait();
        portraitCenter.AddChild(_portrait);
        portraitBox.AddChild(portraitCenter);
        cols.AddChild(portraitBox);

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
        info.AddChild(UiTheme.GoldRule());
        var schedHead = new HBoxContainer();
        schedHead.AddThemeConstantOverride("separation", 10);
        var schedTitle = UiTheme.HeaderLabel("Observed routines", 16);
        schedTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        schedHead.AddChild(schedTitle);
        _observeBtn = new Button { Text = "Observe" };
        _observeBtn.Pressed += OnObservePressed;
        schedHead.AddChild(_observeBtn);
        info.AddChild(schedHead);
        _scheduleLabel = new Label();
        _scheduleLabel.AddThemeColorOverride("font_color", UiTheme.Dim);
        info.AddChild(_scheduleLabel);
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
        _observeBtn.Disabled = !has;
        if (!has)
        {
            _nameLabel.Text = "No one selected";
            _factsLabel.Text = "Pick someone from the World view, the graph, or the board.";
            _relationLabel.Text = "";
            _activityLabel.Text = "";
            _infoLabel.Text = "";
            _scheduleLabel.Text = "";
            _portrait.SetNpc("", "?");
            _lastScheduleNpc = "";
            return;
        }

        var c = s.World.GetCharacter(id);
        var pub = s.View.PublicProfile(id)!;
        _nameLabel.Text = pub.DisplayName;
        _portrait.SetNpc(id, pub.DisplayName);
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

        // UI-driven observation (like Initiative.Evaluate): learn the current block
        // of every co-located NPC when the panel opens for a new NPC, or via the
        // Observe button. Never on plain time-advance refreshes.
        if (id != _lastScheduleNpc)
        {
            s.Manipulation.ObserveRoutines();
            _lastScheduleNpc = id;
        }
        RenderSchedule(s, id);
    }

    /// <summary>
    /// Renders today's learned schedule. Only observed blocks are listed —
    /// anything unobserved renders as "???". Never shows the true schedule.
    /// </summary>
    private void RenderSchedule(GameSession s, string npcId)
    {
        var blocks = s.Manipulation.GetLearnedSchedule(npcId);
        if (blocks.Count == 0)
        {
            _scheduleLabel.Text = "No routines observed yet.\nOpen this panel while sharing their location — or press Observe.";
            return;
        }
        var sb = new System.Text.StringBuilder();
        int prevEnd = -1;
        foreach (var b in blocks)
        {
            if (prevEnd >= 0 && b.StartMinute > prevEnd)
                sb.AppendLine("??:??–??:??  •  ??? (unobserved)");
            var locName = s.Content.GetLocation(b.LocationId).Name;
            sb.AppendLine($"{FmtClock(b.StartMinute)}–{FmtClock(b.EndMinute)}  •  {locName} ({b.Source})");
            prevEnd = b.EndMinute;
        }
        _scheduleLabel.Text = sb.ToString().TrimEnd();
    }

    private static string FmtClock(int minuteOfDay)
    {
        var t = new GameTime(minuteOfDay);
        return $"{t.Hour:00}:{t.Minute:00}";
    }

    private void OnObservePressed()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        var id = g.SelectedNpcId;
        if (id == "" || !s.State.World.Characters.ContainsKey(id)) return;
        s.Manipulation.ObserveRoutines();
        RenderSchedule(s, id);
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
