using BadDeduction.Core;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Panel 10 (Investigation Board): key NPCs, locations of interest, pinned hypothesis
/// cards (the corkboard, simplified: cards with confidence + evidence links), evidence
/// attach flow, mini timeline. Motto included, as the sheet demands.
/// </summary>
public partial class InvestigationBoardPanel : PanelContainer, IPanel
{
    private HBoxContainer _npcRow = null!;
    private HBoxContainer _locRow = null!;
    private Label _locInfo = null!;
    private VBoxContainer _cards = null!;
    private RichTextLabel _miniTimeline = null!;
    private LineEdit _hypDesc = null!;
    private OptionButton _hypCrime = null!;
    private int _hypCounter = 0;

    public override void _Ready()
    {
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(scroll);
        var root = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(root);

        root.AddChild(UiTheme.TitleLabel("Investigation Board", 24));
        var motto = UiTheme.DimLabel("Investigate. Connect. Compare. Discover the Truth.", 15);
        motto.AddThemeColorOverride("font_color", UiTheme.GoldDim);
        root.AddChild(motto);
        root.AddChild(UiTheme.GoldRule());

        root.AddChild(UiTheme.HeaderLabel("Key NPCs", 17));
        _npcRow = new HBoxContainer();
        _npcRow.AddThemeConstantOverride("separation", 8);
        root.AddChild(_npcRow);

        root.AddChild(UiTheme.HeaderLabel("Locations of interest", 17));
        _locRow = new HBoxContainer();
        _locRow.AddThemeConstantOverride("separation", 8);
        root.AddChild(_locRow);
        _locInfo = UiTheme.DimLabel("");
        root.AddChild(_locInfo);

        root.AddChild(UiTheme.GoldRule());
        root.AddChild(UiTheme.HeaderLabel("Pinned hypotheses", 17));
        _cards = new VBoxContainer();
        _cards.AddThemeConstantOverride("separation", 8);
        root.AddChild(_cards);

        var newRow = new HBoxContainer();
        newRow.AddThemeConstantOverride("separation", 8);
        _hypDesc = new LineEdit { PlaceholderText = "New hypothesis…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        newRow.AddChild(_hypDesc);
        _hypCrime = new OptionButton();
        newRow.AddChild(_hypCrime);
        var add = new Button { Text = "Pin" };
        add.Pressed += OnPinHypothesis;
        newRow.AddChild(add);
        root.AddChild(newRow);

        root.AddChild(UiTheme.GoldRule());
        root.AddChild(UiTheme.HeaderLabel("Mini timeline", 17));
        _miniTimeline = new RichTextLabel { BbcodeEnabled = true, CustomMinimumSize = new Vector2(0, 150) };
        root.AddChild(_miniTimeline);
    }

    public void Refresh()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;

        foreach (var c in _npcRow.GetChildren()) c.QueueFree();
        var keyNpcs = new System.Collections.Generic.List<string>();
        foreach (var crime in s.Crime.AllCrimes())
        {
            var scene = s.Crime.GetScene(crime.SceneId);
            if (!scene.IsDiscovered) continue;
            foreach (var w in s.Crime.GetWitnesses(crime.Id))
                if (!keyNpcs.Contains(w) && w != "c_player") keyNpcs.Add(w);
        }
        if (keyNpcs.Count == 0)
            foreach (var c in s.State.World.Characters.Values)
            {
                if (c.Id == "c_player" || !c.IsAlive) continue;
                keyNpcs.Add(c.Id);
                if (keyNpcs.Count >= 6) break;
            }
        foreach (var id in keyNpcs)
        {
            var b = new Button { Text = s.View.PublicProfile(id)?.DisplayName ?? id };
            b.Pressed += () => { g.SelectedNpcId = id; GetNode<Main>("/root/Main").ShowPanel("npc"); };
            _npcRow.AddChild(b);
        }

        foreach (var c in _locRow.GetChildren()) c.QueueFree();
        _locInfo.Text = "";
        foreach (var crime in s.Crime.AllCrimes())
        {
            var scene = s.Crime.GetScene(crime.SceneId);
            if (!scene.IsDiscovered) continue;
            var locId = scene.LocationId;
            var b = new Button { Text = s.Content.GetLocation(locId).Name };
            b.Pressed += () =>
            {
                var evCount = s.Crime.EvidenceAtScene(scene.Id).Count(e => e.Discovered);
                var t = new GameTime(crime.OccurredAt);
                _locInfo.Text = $"{crime.DefinitionId} — Day {t.Day} {t.Hour:00}:{t.Minute:00}. Evidence found: {evCount}.";
            };
            _locRow.AddChild(b);
        }

        foreach (var c in _cards.GetChildren()) c.QueueFree();
        var anyHyp = false;
        foreach (var h in s.Investigate.AllHypotheses())
        {
            if (h.OwnerId is not null) continue; // shared board only
            anyHyp = true;
            _cards.AddChild(BuildHypCard(s, h));
        }
        if (!anyHyp)
            _cards.AddChild(UiTheme.DimLabel("No hypotheses pinned. Propose one below."));

        _hypCrime.Clear();
        _hypCrime.AddItem("(no crime)", -1);
        foreach (var crime in s.Crime.AllCrimes())
        {
            if (!s.Crime.GetScene(crime.SceneId).IsDiscovered) continue;
            _hypCrime.AddItem($"{crime.DefinitionId} ({crime.Id})", _hypCrime.ItemCount);
            _hypCrime.SetItemMetadata(_hypCrime.ItemCount - 1, crime.Id);
        }

        _miniTimeline.Clear();
        var entries = s.View.JournalEntries();
        var start = System.Math.Max(0, entries.Count - 8);
        for (var i = start; i < entries.Count; i++)
        {
            var e = entries[i];
            var t = new GameTime(e.Timestamp);
            _miniTimeline.AppendText($"[color=#9a958a][Day {t.Day} {t.Hour:00}:{t.Minute:00}][/color] {e.Type}\n");
        }
    }

    private PanelContainer BuildHypCard(GameSession s, BadDeduction.Investigation.Hypothesis h)
    {
        var card = new PanelContainer();
        var vb = new VBoxContainer();
        vb.AddThemeConstantOverride("separation", 4);
        card.AddChild(vb);
        vb.AddChild(new Label { Text = h.Description, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var bar = new ProgressBar { MinValue = 0, MaxValue = 100, Value = h.Confidence, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 12) };
        vb.AddChild(bar);
        vb.AddChild(UiTheme.DimLabel($"Confidence {h.Confidence}/100 — {h.SupportingEvidenceIds.Count} for, {h.RefutingEvidenceIds.Count} against"));
        var attach = new Button { Text = "Attach evidence" };
        attach.Pressed += () => ShowAttachDialog(s, h.Id);
        vb.AddChild(attach);
        return card;
    }

    private void ShowAttachDialog(GameSession s, string hypId)
    {
        var d = new AcceptDialog { Title = "Attach evidence" };
        var vb = new VBoxContainer { CustomMinimumSize = new Vector2(400, 0) };
        vb.AddThemeConstantOverride("separation", 8);
        d.AddChild(vb);
        var ob = new OptionButton();
        var ids = new System.Collections.Generic.List<string>();
        foreach (var crime in s.Crime.AllCrimes())
            foreach (var ev in s.Crime.EvidenceAtScene(crime.SceneId))
            {
                if (!ev.Discovered) continue;
                ob.AddItem(ev.Label);
                ids.Add(ev.Id);
            }
        vb.AddChild(ob);
        var supports = new CheckBox { Text = "Supports (unchecked = refutes)", ButtonPressed = true };
        vb.AddChild(supports);
        vb.AddChild(UiTheme.DimLabel("Only discovered evidence. False evidence may be attached — it stays false."));
        d.Confirmed += () =>
        {
            if (ids.Count == 0) return;
            s.Investigate.AttachEvidence(hypId, ids[ob.Selected], supports.ButtonPressed);
            Refresh();
        };
        if (ids.Count == 0) d.GetOkButton().Disabled = true;
        GetNode<Main>("/root/Main").AddChild(d);
        d.PopupCentered();
    }

    private void OnPinHypothesis()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        var desc = _hypDesc.Text.Trim();
        if (desc == "") return;
        _hypCounter++;
        string? crimeId = null;
        var sel = _hypCrime.Selected;
        if (sel > 0 && _hypCrime.IsItemDisabled(sel) == false)
            crimeId = (string)_hypCrime.GetItemMetadata(sel);
        try
        {
            s.Investigate.ProposeHypothesis($"board_{s.State.TotalMinutes}_{_hypCounter}", desc, crimeId);
        }
        catch (System.InvalidOperationException) { return; }
        _hypDesc.Text = "";
        Refresh();
    }
}
