using BadDeduction.Core;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Panels 8+9 (Dialogue + Response Information): free-text chat with the selected NPC
/// via the Phase 6 pipeline (Mock provider). After each exchange the reply is shown
/// with its delta chips (trust / suspicion) inline — panel 9 lives here.
/// </summary>
public partial class DialoguePanel : PanelContainer, IPanel
{
    private Label _header = null!;
    private Label _bandLabel = null!;
    private RichTextLabel _log = null!;
    private LineEdit _input = null!;
    private HBoxContainer _chips = null!;
    private readonly Dictionary<string, System.Collections.Generic.List<string>> _history = new();

    public override void _Ready()
    {
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        _header = UiTheme.TitleLabel("", 24);
        root.AddChild(_header);
        _bandLabel = UiTheme.DimLabel("");
        root.AddChild(_bandLabel);

        _log = new RichTextLabel { BbcodeEnabled = true, ScrollFollowing = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(_log);

        _chips = new HBoxContainer();
        _chips.AddThemeConstantOverride("separation", 10);
        root.AddChild(_chips);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        _input = new LineEdit { PlaceholderText = "Say something…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _input.TextSubmitted += _ => Send();
        row.AddChild(_input);
        var send = new Button { Text = "Send" };
        send.Pressed += Send;
        row.AddChild(send);
        root.AddChild(row);
        root.AddChild(UiTheme.DimLabel("Mock provider: replies are deterministic stand-ins. Real LLM providers plug into IAIProvider later."));
    }

    public void Refresh()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        var id = g.SelectedNpcId;
        if (id == "" || !s.State.World.Characters.ContainsKey(id))
        {
            _header.Text = "No one selected";
            _bandLabel.Text = "Pick someone from the World view or the graph.";
            _input.Editable = false;
            return;
        }
        var pub = s.View.PublicProfile(id)!;
        _header.Text = pub.DisplayName;
        var rel = s.Social.View("c_player", id);
        _bandLabel.Text = $"{rel.Kind?.ToString() ?? "Stranger"} ({rel.Band}) — Trust {rel.Trust}/100 · Suspicion {rel.Suspicion}/100";
        _input.Editable = true;
        RenderLog(id);
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
        if (result.FallbackReason is not null)
            AddChip("(deflected)", UiTheme.Dim);

        // Relationship may have moved — refresh the band line.
        var rel = s.Social.View("c_player", npcId);
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
