using BadDeduction.Core;
using BadDeduction.Social;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Panel 7 (Relationship/Social Graph): "You" at the center, spokes to every other
/// character colored by their dominant relationship axis (from YOUR perspective).
/// Click a node to inspect them. A functional node-link layout, not a fancy one.
/// </summary>
public partial class RelationGraphPanel : PanelContainer, IPanel
{
    private GraphCanvas _canvas = null!;

    public override void _Ready()
    {
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 6);
        AddChild(root);
        root.AddChild(UiTheme.TitleLabel("Social Graph", 24));

        var legend = new HBoxContainer();
        legend.AddThemeConstantOverride("separation", 12);
        foreach (var (name, color) in LegendItems())
        {
            var l = new Label { Text = "● " + name };
            l.AddThemeColorOverride("font_color", color);
            l.AddThemeFontSizeOverride("font_size", 13);
            legend.AddChild(l);
        }
        root.AddChild(legend);

        _canvas = new GraphCanvas(this) { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(_canvas);
    }

    public void Refresh() => _canvas.QueueRedraw();

    private static (string, Color)[] LegendItems()
    {
        return new (string, Color)[]
        {
            ("Trust", UiTheme.Green), ("Suspicion", UiTheme.Red), ("Fear", UiTheme.Purple),
            ("Resentment", new Color("#e67e22")), ("Affection", new Color("#e84393")),
            ("Loyalty", UiTheme.Blue), ("Respect", UiTheme.Gold),
        };
    }

    private sealed partial class GraphCanvas : Control
    {
        private readonly RelationGraphPanel _panel;
        private readonly Dictionary<string, Vector2> _nodes = new();

        public GraphCanvas(RelationGraphPanel panel) => _panel = panel;

        public override void _Draw()
        {
            var g = GameController.Instance;
            if (!g.HasRun) return;
            var s = g.Session!;
            _nodes.Clear();

            var center = Size / 2f;
            var radius = Mathf.Min(Size.X, Size.Y) / 2f - 70f;
            _nodes["c_player"] = center;

            var others = new System.Collections.Generic.List<string>();
            foreach (var c in s.State.World.Characters.Values)
                if (c.Id != "c_player" && c.IsAlive) others.Add(c.Id);
            others.Sort(StringComparer.Ordinal);

            for (var i = 0; i < others.Count; i++)
            {
                var a = Mathf.Tau * i / Mathf.Max(1, others.Count) - Mathf.Pi / 2f;
                _nodes[others[i]] = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            }

            var font = ThemeDB.FallbackFont;
            foreach (var id in others)
            {
                var view = s.Social.View("c_player", id);
                var (axis, value) = Dominant(view);
                var col = AxisColor(axis);
                if (value < 25) col = col with { A = 0.25f };
                DrawLine(center, _nodes[id], col, value < 25 ? 1f : 1f + value / 40f);
            }

            foreach (var (id, pos) in _nodes)
            {
                var isPlayer = id == "c_player";
                var name = s.View.PublicProfile(id)?.DisplayName ?? id;
                var selected = GameController.Instance.SelectedNpcId == id;
                DrawCircle(pos, selected ? 30f : 26f, isPlayer ? UiTheme.Gold : UiTheme.PanelLight);
                DrawArc(pos, selected ? 30f : 26f, 0, Mathf.Tau, 32, selected ? UiTheme.Gold : UiTheme.GoldDim, 2f);
                DrawString(font, pos + new Vector2(-8, 8), name.Substring(0, 1),
                    HorizontalAlignment.Left, -1, 24, isPlayer ? new Color("#0e1118") : UiTheme.Gold);
                var w = font.GetStringSize(name, HorizontalAlignment.Left, -1, 13).X;
                DrawString(font, pos + new Vector2(-w / 2f, 44), name,
                    HorizontalAlignment.Left, -1, 13, UiTheme.Text);
            }
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                foreach (var (id, pos) in _nodes)
                {
                    if (pos.DistanceTo(mb.Position) <= 32f)
                    {
                        GameController.Instance.SelectedNpcId = id;
                        _panel.GetNode<Main>("/root/Main").ShowPanel("npc");
                        return;
                    }
                }
            }
        }

        private static (RelationshipAxis axis, int value) Dominant(RelationshipView v)
        {
            var best = RelationshipAxis.Trust;
            var bestVal = -1;
            foreach (var a in SocialRules.AllAxes)
            {
                var val = v.Get(a);
                if (val > bestVal) { bestVal = val; best = a; }
            }
            return (best, bestVal);
        }

        private static Color AxisColor(RelationshipAxis axis) => axis switch
        {
            RelationshipAxis.Trust => UiTheme.Green,
            RelationshipAxis.Suspicion => UiTheme.Red,
            RelationshipAxis.Fear => UiTheme.Purple,
            RelationshipAxis.Resentment => new Color("#e67e22"),
            RelationshipAxis.Affection => new Color("#e84393"),
            RelationshipAxis.Loyalty => UiTheme.Blue,
            RelationshipAxis.Respect => UiTheme.Gold,
            _ => UiTheme.Dim,
        };
    }
}
