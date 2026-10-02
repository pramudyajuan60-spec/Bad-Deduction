using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Root of the main scene: menu bar (panel switcher + time controls + save/load),
/// a content host for the ten panels, and the New Run / Resolution overlays.
/// </summary>
public partial class Main : Control
{
    private readonly (string key, string label, string scene)[] _panelDefs =
    {
        ("world", "World", "res://scenes/WorldView.tscn"),
        ("npc", "NPC", "res://scenes/NpcInspect.tscn"),
        ("dialogue", "Dialogue", "res://scenes/DialoguePanel.tscn"),
        ("evidence", "Evidence", "res://scenes/EvidencePanel.tscn"),
        ("timeline", "Timeline", "res://scenes/TimelinePanel.tscn"),
        ("memory", "Memory", "res://scenes/MemoryPanel.tscn"),
        ("graph", "Graph", "res://scenes/RelationGraph.tscn"),
        ("board", "Board", "res://scenes/InvestigationBoard.tscn"),
    };

    private readonly Dictionary<string, Control> _panels = new();
    private readonly Dictionary<string, Button> _menuButtons = new();
    private PanelContainer _host = null!;
    private Control _newRunOverlay = null!;
    private Control _resolutionOverlay = null!;
    private string _current = "";

    public override void _Ready()
    {
        Theme = UiTheme.Build();
        var menuBar = GetNode<HBoxContainer>("Root/MenuBar");
        _host = GetNode<PanelContainer>("Root/ContentHost");

        var group = new ButtonGroup();
        foreach (var (key, label, scenePath) in _panelDefs)
        {
            var b = UiTheme.MenuButton(label);
            b.ButtonGroup = group;
            var k = key;
            b.Pressed += () => ShowPanel(k);
            menuBar.AddChild(b);
            _menuButtons[key] = b;

            var panel = (Control)GD.Load<PackedScene>(scenePath).Instantiate();
            panel.Visible = false;
            panel.SizeFlagsVertical = SizeFlags.ExpandFill;
            _host.AddChild(panel);
            _panels[key] = panel;
        }

        menuBar.AddChild(new VSeparator());
        AddMenuAction(menuBar, "+1h", () => Advance(60));
        AddMenuAction(menuBar, "+8h", () => Advance(480));
        AddMenuAction(menuBar, "Next Day", () => Advance(1440));
        menuBar.AddChild(new VSeparator());
        AddMenuAction(menuBar, "Save", ShowSaveDialog);
        AddMenuAction(menuBar, "Load", ShowLoadDialog);
        AddMenuAction(menuBar, "New Run", () => _newRunOverlay.Show());

        _newRunOverlay = (Control)GD.Load<PackedScene>("res://scenes/NewRunPanel.tscn").Instantiate();
        AddChild(_newRunOverlay);
        _resolutionOverlay = (Control)GD.Load<PackedScene>("res://scenes/ResolutionPanel.tscn").Instantiate();
        _resolutionOverlay.Visible = false;
        AddChild(_resolutionOverlay);

        var g = GameController.Instance;
        g.RunStarted += OnRunStarted;
        g.TimeAdvanced += OnTimeAdvanced;
        g.SelectionChanged += () =>
        {
            if (_current is "npc" or "dialogue" or "memory") RefreshCurrent();
        };

        // Headless smoke test: `godot --headless --path . -- --autotest`
        if (OS.GetCmdlineUserArgs().Contains("--autotest"))
            CallDeferred(nameof(RunAutotest));
    }

    /// <summary>
    /// Exercises the full playable loop without a display: new run, every panel,
    /// NPC selection, dialogue exchange, interview, time advance, save. Any exception
    /// surfaces as a Godot script error in the headless log.
    /// </summary>
    private void RunAutotest()
    {
        var g = GameController.Instance;
        g.NewRun(12345, BadDeduction.Core.Campaign.Lumiel, BadDeduction.Core.Difficulty.Medium);
        var s = g.Session!;
        GD.Print("AUTOTEST: run started, chars=", s.State.World.Characters.Count);

        g.SelectedNpcId = "c_01";
        foreach (var key in _panels.Keys) ShowPanel(key);
        GD.Print("AUTOTEST: all panels refreshed");

        var dlg = s.Dialogue.Exchange("c_01", "c_player", "What did you see last night?");
        GD.Print("AUTOTEST: dialogue reply len=", dlg.ReplyText.Length, " accepted=", dlg.Accepted);
        var iv = s.Investigate.Interview("c_player", "c_01", $"whereabouts:{s.State.TotalMinutes}");
        GD.Print("AUTOTEST: interview answer len=", iv.Answer.Length);

        g.AdvanceMinutes(1440);
        g.AdvanceMinutes(2880);
        GD.Print("AUTOTEST: day=", g.CurrentDay);
        g.SaveRun("autotest");
        GD.Print("AUTOTEST: saves=", string.Join(",", g.ListSaves()));
        GD.Print("AUTOTEST OK");
        GetTree().Quit();
    }

    private void AddMenuAction(HBoxContainer bar, string label, System.Action onPress)
    {
        var b = new Button { Text = label };
        b.Pressed += onPress;
        bar.AddChild(b);
    }

    private void Advance(int minutes)
    {
        var g = GameController.Instance;
        if (!g.HasRun || _newRunOverlay.Visible) return;
        g.AdvanceMinutes(minutes);
    }

    private void OnRunStarted()
    {
        _newRunOverlay.Hide();
        _resolutionOverlay.Hide();
        ShowPanel("world");
    }

    private void OnTimeAdvanced()
    {
        var g = GameController.Instance;
        if (g.IsRunOver)
        {
            ((IPanel)_resolutionOverlay).Refresh();
            _resolutionOverlay.Show();
        }
        else
        {
            RefreshCurrent();
        }
    }

    public void ShowPanel(string key)
    {
        var g = GameController.Instance;
        if (!g.HasRun || !_panels.ContainsKey(key)) return;
        _current = key;
        foreach (var (k, p) in _panels) p.Visible = k == key;
        if (_menuButtons.TryGetValue(key, out var b)) b.ButtonPressed = true;
        ((IPanel)_panels[key]).Refresh();
    }

    public void ShowNewRun() => _newRunOverlay.Show();

    private void RefreshCurrent()
    {
        if (_current != "" && _panels.TryGetValue(_current, out var p) && p.Visible)
            ((IPanel)p).Refresh();
    }

    private void ShowSaveDialog()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var d = new AcceptDialog { Title = "Save Run" };
        var vb = new VBoxContainer();
        d.AddChild(vb);
        var le = new LineEdit { Text = $"save_day{g.CurrentDay}", CustomMinimumSize = new Vector2(280, 0) };
        vb.AddChild(le);
        d.Confirmed += () =>
        {
            var name = le.Text.Trim();
            if (name.Length > 0) g.SaveRun(name);
        };
        AddChild(d);
        d.PopupCentered();
    }

    private void ShowLoadDialog()
    {
        var g = GameController.Instance;
        var saves = g.ListSaves();
        var d = new AcceptDialog { Title = "Load Run" };
        var vb = new VBoxContainer();
        d.AddChild(vb);
        if (saves.Count == 0)
        {
            vb.AddChild(UiTheme.DimLabel("No saves yet."));
            d.GetOkButton().Disabled = true;
        }
        else
        {
            var ob = new OptionButton();
            foreach (var s in saves) ob.AddItem(s);
            vb.AddChild(ob);
            d.Confirmed += () => g.LoadRun(ob.GetItemText(ob.Selected));
        }
        AddChild(d);
        d.PopupCentered();
    }
}
