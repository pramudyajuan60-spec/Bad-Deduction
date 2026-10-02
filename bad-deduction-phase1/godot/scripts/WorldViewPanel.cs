using BadDeduction.Core;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Panel 1 (Main World View): day/time header, objectives, location travel, who's here,
/// and the player's knowledge feed (journal). Player moves with MoveCharacter — the
/// police cordon can deny entry, which is reported, not bypassed.
/// </summary>
public partial class WorldViewPanel : PanelContainer, IPanel
{
    private Label _dayLabel = null!;
    private Label _locLabel = null!;
    private Label _alertLabel = null!;
    private Label _objectivesLabel = null!;
    private Label _noticeLabel = null!;
    private GridContainer _locGrid = null!;
    private VBoxContainer _locHost = null!;
    private ItemList _peopleList = null!;
    private RichTextLabel _feed = null!;

    public override void _Ready()
    {
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        var header = new HBoxContainer();
        root.AddChild(header);
        _dayLabel = UiTheme.TitleLabel("", 22);
        _dayLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_dayLabel);
        _alertLabel = UiTheme.DimLabel("");
        header.AddChild(_alertLabel);

        _locLabel = UiTheme.HeaderLabel("", 17);
        root.AddChild(_locLabel);
        _objectivesLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _objectivesLabel.AddThemeColorOverride("font_color", UiTheme.Dim);
        root.AddChild(_objectivesLabel);
        _noticeLabel = new Label();
        _noticeLabel.AddThemeColorOverride("font_color", UiTheme.Red);
        root.AddChild(_noticeLabel);
        root.AddChild(UiTheme.GoldRule());

        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", 16);
        cols.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AddChild(cols);

        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _locHost = left;
        left.AddChild(UiTheme.HeaderLabel("Travel"));
        _locGrid = new GridContainer { Columns = 2 };
        _locGrid.AddThemeConstantOverride("h_separation", 8);
        _locGrid.AddThemeConstantOverride("v_separation", 8);
        left.AddChild(_locGrid);
        cols.AddChild(left);

        var mid = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        mid.AddChild(UiTheme.HeaderLabel("Here with you"));
        _peopleList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 160) };
        _peopleList.ItemSelected += OnPersonSelected;
        mid.AddChild(_peopleList);
        cols.AddChild(mid);

        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.4f };
        right.AddChild(UiTheme.HeaderLabel("What you know"));
        _feed = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        right.AddChild(_feed);
        cols.AddChild(right);
    }

    public void Refresh()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        _noticeLabel.Text = "";

        _dayLabel.Text = $"{UiTheme.FmtTime(s.State.TotalMinutes)} — The Veiled City";
        // Gameplay state (not hidden truth): the city's alert level is public knowledge.
        _alertLabel.Text = $"Alert: {s.State.Police.Alert}";
        var playerLoc = s.World.GetCharacter("c_player").CurrentLocationId;
        _locLabel.Text = "Current location: " + s.Content.GetLocation(playerLoc).Name;

        var objs = s.Agenda.ObjectivesOf("c_player");
        _objectivesLabel.Text = objs.Count == 0
            ? "Objectives: survive the week. (Your hidden agenda will surface.)"
            : "Objectives: " + string.Join("  •  ", objs.Select(ObjectiveText));

        _locGrid.QueueFree();
        _locGrid = new GridContainer { Columns = 2 };
        _locGrid.AddThemeConstantOverride("h_separation", 8);
        _locGrid.AddThemeConstantOverride("v_separation", 8);
        _locHost.AddChild(_locGrid);
        foreach (var loc in s.Content.Locations)
        {
            var b = new Button { Text = loc.Name, ToggleMode = true, ButtonPressed = loc.Id == playerLoc };
            var id = loc.Id;
            b.Pressed += () =>
            {
                var evt = s.World.MoveCharacter("c_player", id);
                _noticeLabel.Text = evt is null ? "Access denied — the location is sealed by police." : "";
                Refresh();
            };
            _locGrid.AddChild(b);
        }

        _peopleList.Clear();
        foreach (var c in s.World.CharactersAt(playerLoc))
        {
            if (c.Id == "c_player" || !c.IsAlive) continue;
            var idx = _peopleList.AddItem($"{c.DisplayName} — {c.Activity}");
            _peopleList.SetItemMetadata(idx, c.Id);
        }

        _feed.Clear();
        var entries = s.View.JournalEntries();
        var start = System.Math.Max(0, entries.Count - 14);
        for (var i = start; i < entries.Count; i++)
        {
            var e = entries[i];
            var t = new GameTime(e.Timestamp);
            _feed.AppendText($"[color=#9a958a][Day {t.Day} {t.Hour:00}:{t.Minute:00}][/color] {e.Type}\n");
        }
    }

    private string ObjectiveText(BadDeduction.Agenda.HiddenObjective o)
    {
        var s = GameController.Instance.Session!;
        string Who(string id) => s.View.PublicProfile(id)?.DisplayName ?? id;
        return o.Kind switch
        {
            BadDeduction.Agenda.ObjectiveKind.EliminateObstacle => $"Eliminate {Who(o.TargetId!)}",
            BadDeduction.Agenda.ObjectiveKind.SowDistrust => $"Turn {Who(o.TargetId!)} against {Who(o.TargetId2!)}",
            BadDeduction.Agenda.ObjectiveKind.EvadeSuspicion => "Keep suspicion off yourself",
            BadDeduction.Agenda.ObjectiveKind.ProtectTarget => $"Protect {Who(o.TargetId!)}",
            BadDeduction.Agenda.ObjectiveKind.GatherAlly => $"Win over {Who(o.TargetId!)}",
            BadDeduction.Agenda.ObjectiveKind.PursueLead => "Pursue a lead",
            _ => o.Kind.ToString(),
        } + (o.Status == BadDeduction.Agenda.ObjectiveStatus.Active ? "" : $" [{o.Status}]");
    }

    private void OnPersonSelected(long index)
    {
        var id = (string)_peopleList.GetItemMetadata((int)index);
        GameController.Instance.SelectedNpcId = id;
        GetNode<Main>("/root/Main").ShowPanel("npc");
    }
}
