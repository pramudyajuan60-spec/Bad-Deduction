using BadDeduction.Core;
using Godot;

namespace BadDeduction.Godot;

/// <summary>Overlay shown at startup (and via "New Run"): seed, campaign, difficulty, load.</summary>
public partial class NewRunPanel : PanelContainer, IPanel
{
    private LineEdit _seedEdit = null!;
    private OptionButton _campaignOpt = null!;
    private OptionButton _difficultyOpt = null!;
    private OptionButton _loadOpt = null!;
    private Button _loadButton = null!;

    public override void _Ready()
    {
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var card = new PanelContainer { CustomMinimumSize = new Vector2(460, 0) };
        center.AddChild(card);
        var vb = new VBoxContainer();
        vb.AddThemeConstantOverride("separation", 10);
        card.AddChild(vb);

        vb.AddChild(UiTheme.TitleLabel("THE VEILED CITY", 34));
        vb.AddChild(UiTheme.DimLabel("Bad Deduction — truth lives in the shadows."));
        vb.AddChild(UiTheme.GoldRule());

        var seedRow = new HBoxContainer();
        seedRow.AddChild(new Label { Text = "Seed:", CustomMinimumSize = new Vector2(110, 0) });
        _seedEdit = new LineEdit
        {
            Text = new System.Random().Next(1, 999999).ToString(),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        seedRow.AddChild(_seedEdit);
        vb.AddChild(seedRow);
        vb.AddChild(UiTheme.DimLabel("Same seed + same actions = same city."));

        var campRow = new HBoxContainer();
        campRow.AddChild(new Label { Text = "Your side:", CustomMinimumSize = new Vector2(110, 0) });
        _campaignOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _campaignOpt.AddItem("Lumiel (Order)", 0);
        _campaignOpt.AddItem("Malvr (Chaos)", 1);
        campRow.AddChild(_campaignOpt);
        vb.AddChild(campRow);
        vb.AddChild(UiTheme.DimLabel("You always know your own side. The other genius hides."));

        var diffRow = new HBoxContainer();
        diffRow.AddChild(new Label { Text = "Difficulty:", CustomMinimumSize = new Vector2(110, 0) });
        _difficultyOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _difficultyOpt.AddItem("Easy", 0);
        _difficultyOpt.AddItem("Medium", 1);
        _difficultyOpt.AddItem("Hard", 2);
        _difficultyOpt.AddItem("Genius", 3);
        _difficultyOpt.Selected = 1;
        diffRow.AddChild(_difficultyOpt);
        vb.AddChild(diffRow);

        var start = new Button { Text = "Begin the Investigation" };
        start.AddThemeFontSizeOverride("font_size", 20);
        start.Pressed += OnStart;
        vb.AddChild(start);

        vb.AddChild(UiTheme.GoldRule());
        var loadRow = new HBoxContainer();
        _loadOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        loadRow.AddChild(_loadOpt);
        _loadButton = new Button { Text = "Load" };
        _loadButton.Pressed += OnLoad;
        loadRow.AddChild(_loadButton);
        vb.AddChild(loadRow);

        Refresh();
    }

    public void Refresh()
    {
        _loadOpt.Clear();
        var saves = GameController.Instance.ListSaves();
        foreach (var s in saves) _loadOpt.AddItem(s);
        _loadButton.Disabled = saves.Count == 0;
        _loadOpt.Disabled = saves.Count == 0;
    }

    private void OnStart()
    {
        if (!ulong.TryParse(_seedEdit.Text.Trim(), out var seed))
            seed = (ulong)new System.Random().Next(1, 999999);
        var campaign = _campaignOpt.Selected == 1 ? Campaign.Malvr : Campaign.Lumiel;
        var difficulty = (Difficulty)_difficultyOpt.Selected;
        GameController.Instance.NewRun(seed, campaign, difficulty);
    }

    private void OnLoad()
    {
        if (_loadOpt.ItemCount == 0) return;
        GameController.Instance.LoadRun(_loadOpt.GetItemText(_loadOpt.Selected));
    }
}
