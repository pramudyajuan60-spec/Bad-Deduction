using BadDeduction.Core;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Panel 4 (Evidence): tabbed evidence browser. Reliability % is KNOWLEDGE-based
/// (corroborating witnesses + own discovery) — never the hidden authenticity.
/// Interpretations come from hypotheses that reference the item. Unexamined items
/// at the player's current (discovered) scene can be examined on the spot.
/// </summary>
public partial class EvidencePanel : PanelContainer, IPanel
{
    private TabContainer _tabs = null!;
    private readonly Dictionary<string, ItemList> _lists = new();
    private readonly Dictionary<int, string> _rowEvidence = new();

    private static readonly string[] DocKeywords =
        { "letter", "note", "ledger", "document", "record", "parchment", "threatening" };

    public override void _Ready()
    {
        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(_tabs);
        foreach (var tab in new[] { "All", "Documents", "Items", "Scenes" })
        {
            var list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
            list.ItemSelected += idx => OnRowSelected(tab, (int)idx);
            _lists[tab] = list;
            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, Name = tab };
            scroll.AddChild(list);
            list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _tabs.AddChild(scroll);
        }
    }

    public void Refresh()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        _rowEvidence.Clear();
        foreach (var l in _lists.Values) l.Clear();

        var playerLoc = s.World.GetCharacter("c_player").CurrentLocationId;
        var rows = new List<(BadDeduction.Crime.Evidence ev, string tab, string suffix)>();
        foreach (var crime in s.Crime.AllCrimes())
        {
            foreach (var ev in s.Crime.EvidenceAtScene(crime.SceneId))
            {
                var scene = s.Crime.GetScene(ev.SceneId);
                if (!scene.IsDiscovered) continue;
                if (!ev.Discovered)
                {
                    // Unexamined: only visible while standing at the scene.
                    if (scene.LocationId != playerLoc) continue;
                    rows.Add((ev, "All", "  [unexamined]"));
                    rows.Add((ev, "Items", "  [unexamined]"));
                    continue;
                }
                var rel = Reliability(s, crime.Id, ev);
                var suffix = $"  [{rel}%]";
                var tab = IsDocument(ev) ? "Documents" : "Items";
                rows.Add((ev, "All", suffix));
                rows.Add((ev, tab, suffix));
            }
        }

        var rowId = 0;
        foreach (var (ev, tab, suffix) in rows)
        {
            var scene = s.Crime.GetScene(ev.SceneId);
            var text = $"{ev.Label} — {s.Content.GetLocation(scene.LocationId).Name}{suffix}";
            var idx = _lists[tab].AddItem(text);
            _lists[tab].SetItemMetadata(idx, rowId);
            _rowEvidence[rowId] = ev.Id;
            rowId++;
        }
        // Scenes tab: list discovered scenes properly.
        _lists["Scenes"].Clear();
        foreach (var crime in s.Crime.AllCrimes())
        {
            var scene = s.Crime.GetScene(crime.SceneId);
            if (!scene.IsDiscovered) continue;
            var loc = s.Content.GetLocation(scene.LocationId).Name;
            var t = new GameTime(crime.OccurredAt);
            _lists["Scenes"].AddItem($"{loc} — {crime.DefinitionId} (Day {t.Day})");
        }
    }

    private static bool IsDocument(BadDeduction.Crime.Evidence ev)
    {
        var hay = (ev.Label + " " + ev.Summary).ToLowerInvariant();
        foreach (var k in DocKeywords)
            if (hay.Contains(k)) return true;
        return false;
    }

    private static int Reliability(GameSession s, string crimeId, BadDeduction.Crime.Evidence ev)
    {
        var witnesses = s.Crime.GetWitnesses(crimeId).Count;
        var rel = 40 + 15 * witnesses + (ev.DiscoveredBy == "c_player" ? 10 : 0);
        return System.Math.Clamp(rel, 5, 95);
    }

    private void OnRowSelected(string tab, int idx)
    {
        if (tab == "Scenes") return;
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        var evId = _rowEvidence[(int)_lists[tab].GetItemMetadata(idx)];
        if (evId == "") return;
        var ev = s.Crime.GetEvidence(evId);
        var scene = s.Crime.GetScene(ev.SceneId);
        var crime = s.Crime.AllCrimes().First(c => c.SceneId == ev.SceneId);

        var d = new AcceptDialog { Title = ev.Label };
        var vb = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
        vb.AddThemeConstantOverride("separation", 8);
        d.AddChild(vb);
        vb.AddChild(new Label { Text = ev.Summary, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        vb.AddChild(UiTheme.DimLabel($"Found at {s.Content.GetLocation(scene.LocationId).Name}" +
            (ev.Discovered ? $" by {s.View.PublicProfile(ev.DiscoveredBy!)?.DisplayName ?? ev.DiscoveredBy}" : "")));
        vb.AddChild(UiTheme.HeaderLabel($"Reliability: {Reliability(s, crime.Id, ev)}%", 16));
        vb.AddChild(UiTheme.DimLabel("Based on corroborating witnesses and your own findings — never on hidden truth."));
        vb.AddChild(UiTheme.HeaderLabel("Possible interpretations", 16));
        var anyInterp = false;
        foreach (var h in s.Investigate.AllHypotheses())
        {
            if (h.SupportingEvidenceIds.Contains(evId))
            {
                vb.AddChild(new Label { Text = $"Supports: {h.Description}", AutowrapMode = TextServer.AutowrapMode.WordSmart });
                anyInterp = true;
            }
            if (h.RefutingEvidenceIds.Contains(evId))
            {
                vb.AddChild(new Label { Text = $"Refutes: {h.Description}", AutowrapMode = TextServer.AutowrapMode.WordSmart });
                anyInterp = true;
            }
        }
        if (!anyInterp) vb.AddChild(UiTheme.DimLabel("None yet — attach it to a hypothesis on the Board."));

        if (!ev.Discovered)
        {
            var examine = new Button { Text = "Examine it now" };
            examine.Pressed += () =>
            {
                s.Crime.DiscoverEvidence("c_player", evId);
                d.Hide();
                Refresh();
            };
            vb.AddChild(examine);
        }
        GetNode<Main>("/root/Main").AddChild(d);
        d.PopupCentered();
    }
}
