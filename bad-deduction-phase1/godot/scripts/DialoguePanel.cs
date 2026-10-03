using BadDeduction.Core;
using BadDeduction.Social;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Panels 8+9 (Dialogue + Response Information): free-text chat with the selected NPC
/// via the Phase 6 pipeline (Mock provider). Phase 14 rework: two-column layout —
/// LEFT is the NPC profile box (procedural avatar, name, occupation, trust bar of
/// the NPC TOWARD the player, manipulability tier, despair meter), RIGHT is the
/// response log + free-text input. After each exchange the reply is shown with its
/// delta chips (trust / suspicion / order outcome / despair) inline — panel 9 lives here.
///
/// Knowledge note: everything shown is player-known — profile facts, the NPC's
/// felt trust toward the player (the manipulation gameplay meter), their tier
/// (fixed at run start), and despair (rises from your own exchanges). No truth reads.
/// </summary>
public partial class DialoguePanel : PanelContainer, IPanel
{
    private NpcPortrait _portrait = null!;
    private Label _nameLabel = null!;
    private Label _occLabel = null!;
    private ProgressBar _trustBar = null!;
    private Label _trustNum = null!;
    private Label _tierLabel = null!;
    private ProgressBar _despairBar = null!;
    private Label _despairNum = null!;
    private Label _bandLabel = null!;
    private RichTextLabel _log = null!;
    private LineEdit _input = null!;
    private HBoxContainer _chips = null!;
    private readonly Dictionary<string, System.Collections.Generic.List<string>> _history = new();

    public override void _Ready()
    {
        var root = new HBoxContainer();
        root.AddThemeConstantOverride("separation", 12);
        AddChild(root);

        root.AddChild(BuildProfileBox());

        var right = new VBoxContainer();
        right.AddThemeConstantOverride("separation", 8);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        root.AddChild(right);

        _bandLabel = UiTheme.DimLabel("");
        right.AddChild(_bandLabel);

        _log = new RichTextLabel { BbcodeEnabled = true, ScrollFollowing = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        right.AddChild(_log);

        _chips = new HBoxContainer();
        _chips.AddThemeConstantOverride("separation", 10);
        right.AddChild(_chips);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        _input = new LineEdit { PlaceholderText = "Say something…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _input.TextSubmitted += _ => Send();
        row.AddChild(_input);
        var send = new Button { Text = "Send" };
        send.Pressed += Send;
        row.AddChild(send);
        right.AddChild(row);
        right.AddChild(UiTheme.DimLabel("Scripted provider is deterministic; pick Ollama on the New Run screen for local-LLM dialogue."));
    }

    private Control BuildProfileBox()
    {
        var box = new PanelContainer();
        box.CustomMinimumSize = new Vector2(300, 0);
        var pv = new VBoxContainer();
        pv.AddThemeConstantOverride("separation", 6);
        box.AddChild(pv);

        var center = new CenterContainer();
        _portrait = new NpcPortrait();
        center.AddChild(_portrait);
        pv.AddChild(center);

        _nameLabel = UiTheme.TitleLabel("No one selected", 20);
        _nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _nameLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        pv.AddChild(_nameLabel);

        _occLabel = UiTheme.DimLabel("", 14);
        _occLabel.HorizontalAlignment = HorizontalAlignment.Center;
        pv.AddChild(_occLabel);

        pv.AddChild(UiTheme.GoldRule());

        pv.AddChild(UiTheme.HeaderLabel("Trust — them toward you", 15));
        _trustBar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 16) };
        pv.AddChild(_trustBar);
        _trustNum = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _trustNum.AddThemeFontSizeOverride("font_size", 20);
        _trustNum.AddThemeColorOverride("font_color", UiTheme.Green);
        pv.AddChild(_trustNum);

        _tierLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _tierLabel.AddThemeFontSizeOverride("font_size", 15);
        _tierLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        pv.AddChild(_tierLabel);

        pv.AddChild(UiTheme.GoldRule());

        pv.AddChild(UiTheme.HeaderLabel("Despair", 15));
        _despairBar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 12) };
        // Subtle styling: muted dark-red fill, not the gold default — this meter
        // tracks psychological pressure, not progress.
        var despairFill = new StyleBoxFlat { BgColor = new Color("#7a2e26") };
        despairFill.SetCornerRadiusAll(3);
        _despairBar.AddThemeStyleboxOverride("fill", despairFill);
        pv.AddChild(_despairBar);
        _despairNum = UiTheme.DimLabel("", 14);
        _despairNum.HorizontalAlignment = HorizontalAlignment.Center;
        pv.AddChild(_despairNum);
        var despairHint = UiTheme.DimLabel("At 100 they break.", 12);
        despairHint.HorizontalAlignment = HorizontalAlignment.Center;
        pv.AddChild(despairHint);

        return box;
    }

    public void Refresh()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        var id = g.SelectedNpcId;
        if (id == "" || !s.State.World.Characters.ContainsKey(id))
        {
            _nameLabel.Text = "No one selected";
            _occLabel.Text = "";
            _portrait.SetNpc("", "?");
            _trustBar.Value = 0;
            _trustNum.Text = "— / 100";
            _tierLabel.Text = "";
            _despairBar.Value = 0;
            _despairNum.Text = "";
            _bandLabel.Text = "Pick someone from the World view or the graph.";
            _input.Editable = false;
            return;
        }
        var pub = s.View.PublicProfile(id)!;
        _nameLabel.Text = pub.DisplayName;
        _occLabel.Text = OccupationName(s, pub.OccupationId);
        _portrait.SetNpc(id, pub.DisplayName);
        RefreshMeters(s, id);

        // The NPC (speaker) replies to the player's utterance; their felt trust is
        // what dialogue deltas move, so the band line tracks the same direction.
        var rel = s.Social.View(id, "c_player");
        _bandLabel.Text = $"{rel.Kind?.ToString() ?? "Stranger"} ({rel.Band}) — Trust {rel.Trust}/100 · Suspicion {rel.Suspicion}/100";
        _input.Editable = true;

        // NPC-initiated conversation: show their opening line in the log before the
        // player types anything. Consumed once; a stale opener for another NPC waits.
        var opener = g.PendingNpcOpener;
        if (opener is not null && opener.NpcId == id)
        {
            g.PendingNpcOpener = null;
            if (!_history.TryGetValue(id, out var lines))
                _history[id] = lines = new System.Collections.Generic.List<string>();
            lines.Add($"[color=#9a958a]{Escape(pub.DisplayName)}:[/color] {Escape(opener.OpeningLine)} [color=#5f5b52](they approached you)[/color]");
        }

        RenderLog(id);
    }

    private void RefreshMeters(GameSession s, string npcId)
    {
        var trust = s.Social.View(npcId, "c_player").Trust;
        _trustBar.Value = trust;
        _trustNum.Text = $"{trust} / 100";
        _trustNum.AddThemeColorOverride("font_color", trust >= 60 ? UiTheme.Green : trust >= 30 ? UiTheme.Gold : UiTheme.Red);

        var tier = s.Manipulation.TierOf(npcId);
        _tierLabel.Text = TierText(tier);
        _tierLabel.AddThemeColorOverride("font_color", TierColor(tier));

        var despair = s.Manipulation.DespairOf(npcId);
        _despairBar.Value = despair;
        _despairNum.Text = $"{despair} / 100";
    }

    private static string TierText(ManipulabilityTier tier) => tier switch
    {
        ManipulabilityTier.Gullible => "◈ Gullible — mudah dipengaruhi",
        ManipulabilityTier.Wary => "▲ Wary — waspada",
        _ => "● Standard — biasa",
    };

    private static Color TierColor(ManipulabilityTier tier) => tier switch
    {
        ManipulabilityTier.Gullible => UiTheme.Green,
        ManipulabilityTier.Wary => UiTheme.Red,
        _ => UiTheme.Gold,
    };

    private static string OccupationName(GameSession s, string occupationId)
    {
        try
        {
            return s.Content.GetOccupation(occupationId).Name;
        }
        catch (KeyNotFoundException)
        {
            return occupationId;
        }
    }

    private void RenderLog(string npcId)
    {
        _log.Clear();
        if (_history.TryGetValue(npcId, out var lines))
            foreach (var l in lines) _log.AppendText(l + "\n");
        else
            _log.AppendText("[color=#9a958a]Start the conversation.[/color]\n");
    }

    private void Send()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        var npcId = g.SelectedNpcId;
        var text = _input.Text.Trim();
        if (npcId == "" || text == "") return;
        _input.Text = "";

        // The NPC (speaker) replies to the player's utterance.
        var result = s.Dialogue.Exchange(npcId, "c_player", text);
        var name = s.View.PublicProfile(npcId)?.DisplayName ?? npcId;

        if (!_history.TryGetValue(npcId, out var lines))
            _history[npcId] = lines = new System.Collections.Generic.List<string>();
        lines.Add($"[color=#d4af37]You:[/color] {Escape(text)}");
        lines.Add($"[color=#9a958a]{Escape(name)}:[/color] {Escape(result.ReplyText)}");
        RenderLog(npcId);

        foreach (var c in _chips.GetChildren()) c.QueueFree();
        AddChip($"Trust {(result.TrustDelta >= 0 ? "+" : "")}{result.TrustDelta}",
            result.TrustDelta >= 0 ? UiTheme.Green : UiTheme.Red);
        AddChip($"Suspicion {(result.SuspicionDelta >= 0 ? "+" : "")}{result.SuspicionDelta}",
            result.SuspicionDelta >= 0 ? UiTheme.Red : UiTheme.Green);
        if (result.DespairDelta != 0)
            AddChip($"Despair {(result.DespairDelta >= 0 ? "+" : "")}{result.DespairDelta}",
                result.DespairDelta > 0 ? UiTheme.Red : UiTheme.Green);
        if (result.OrderOutcome is not null)
        {
            var split = result.OrderOutcome.Split(':', 2);
            var complied = split[0] == "accepted";
            var kind = split.Length > 1 ? split[1] : "?";
            AddChip($"Order {(complied ? "accepted" : "refused")}: {kind}",
                complied ? UiTheme.Green : UiTheme.Red);
        }
        if (result.FallbackReason is not null)
            AddChip("(deflected)", UiTheme.Dim);
        if (result.UsedFallback)
            AddChip("(offline dialogue)", UiTheme.Dim);

        // Meters may have moved — refresh the profile box and band line.
        RefreshMeters(s, npcId);
        var rel = s.Social.View(npcId, "c_player");
        _bandLabel.Text = $"{rel.Kind?.ToString() ?? "Stranger"} ({rel.Band}) — Trust {rel.Trust}/100 · Suspicion {rel.Suspicion}/100";
    }

    private void AddChip(string text, Color color)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeFontSizeOverride("font_size", 15);
        _chips.AddChild(l);
    }

    private static string Escape(string s) => s.Replace("[", "\\[").Replace("]", "\\]");
}
