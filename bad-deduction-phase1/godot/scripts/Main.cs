using BadDeduction.Initiative;
using Godot;
using System.Linq;

namespace BadDeduction.Godot;

/// <summary>
/// Root of the main scene: menu bar (panel switcher + time controls + save/load),
/// a content host for the ten panels, and the New Run / Resolution overlays.
/// </summary>
public partial class Main : Control
{
    private readonly (string key, string label, string scene)[] _panelDefs =
    {
        ("explore", "Explore", "res://scenes/World2D.tscn"),
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

        // Phase 13: 2D open-world presentation. Headless-safe: direct method calls,
        // no real input. Any exception surfaces as a Godot script error.
        var w2 = (World2DView)GD.Load<PackedScene>("res://scenes/World2D.tscn").Instantiate();
        GetTree().Root.AddChild(w2);
        var playerLoc = s.World.GetCharacter("c_player").CurrentLocationId;
        w2.BuildLocation(playerLoc);
        GD.Print("AUTOTEST 2D: location=", playerLoc, " npcNodes=", w2.NpcNodeCount);
        if (w2.NpcNodeCount == 0)
            GD.PushWarning("AUTOTEST 2D: no NPC nodes placed at the player's location");

        var pending = s.Initiative.Evaluate();
        GD.Print("AUTOTEST 2D: pending initiatives=", pending.Count);
        NpcInitiative? accepted = null;
        string openerNpc = "";
        if (pending.Count > 0 && s.Initiative.TryAccept(pending[0].NpcId, out accepted) && accepted is not null)
        {
            openerNpc = accepted.NpcId;
        }
        else
        {
            // No volunteer: fabricate an initiative to exercise the NPC-opens path.
            var anyNpc = s.World.CharactersAt(playerLoc)
                .FirstOrDefault(c => c.Id != "c_player" && c.IsAlive);
            if (anyNpc is not null)
            {
                accepted = new NpcInitiative
                {
                    NpcId = anyNpc.Id,
                    Motive = NpcMotive.ShareRumor,
                    EnqueuedAt = s.State.TotalMinutes,
                    MotiveDetails = "autotest",
                };
                openerNpc = anyNpc.Id;
            }
        }
        if (accepted is not null)
        {
            var opening = s.Dialogue.OpeningLine(openerNpc, "c_player", accepted);
            GD.Print("AUTOTEST 2D: opening line len=", opening.ReplyText.Length);
            OpenDialogueWith(openerNpc, opening.ReplyText);
            GD.Print("AUTOTEST 2D: opener consumed=", g.PendingNpcOpener is null);
        }
        g.AdvanceMinutes(60);
        w2.Refresh();
        GD.Print("AUTOTEST 2D: after advance npcNodes=", w2.NpcNodeCount);
        w2.QueueFree();
        GD.Print("AUTOTEST 2D OK");

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
        ShowPanel("explore");
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

    /// <summary>
    /// True while a modal-ish overlay is up: the New Run / Resolution overlays, or
    /// the dialogue panel. The 2D explorer only advances the clock while this is false.
    /// </summary>
    public bool IsOverlayOpen() =>
        _newRunOverlay.Visible || _resolutionOverlay.Visible ||
        (_panels.TryGetValue("dialogue", out var d) && d.Visible);

    /// <summary>
    /// NPC-initiated conversation: the opener is stored on the GameController and
    /// consumed once by DialoguePanel.Refresh, so the NPC's line appears in the log
    /// before the player types anything.
    /// </summary>
    public void OpenDialogueWith(string npcId, string openingLine)
    {
        var g = GameController.Instance;
        g.PendingNpcOpener = new GameController.PendingOpener
        {
            NpcId = npcId,
            OpeningLine = openingLine,
        };
        g.SelectedNpcId = npcId;
        ShowPanel("dialogue");
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey k || !k.Pressed || k.Echo) return;
        var g = GameController.Instance;
        if (!g.HasRun) return;
        // Never steal keystrokes from text fields (dialogue input, save name, ...).
        bool typing = GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;
        int code = (int)k.Keycode; // Key.Key1..Key.Key9 are consecutive (49..57)
        if (code >= (int)Key.Key1 && code <= (int)Key.Key9 && !typing)
        {
            int idx = code - (int)Key.Key1;
            if (idx < _panelDefs.Length) ShowPanel(_panelDefs[idx].key);
        }
        else if (k.Keycode == Key.Tab && !typing)
        {
            int idx = System.Array.FindIndex(_panelDefs, d => d.key == _current);
            ShowPanel(_panelDefs[(idx + 1) % _panelDefs.Length].key);
        }
        else if (k.Keycode == Key.Escape)
        {
            if (typing)
            {
                GetViewport().GuiGetFocusOwner()?.ReleaseFocus();
                return;
            }
            if (_current != "" && _current != "explore") ShowPanel("explore");
        }
    }

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
