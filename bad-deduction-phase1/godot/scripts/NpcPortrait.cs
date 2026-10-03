using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Deterministic procedural avatar (the portraits pipeline is deferred — see
/// ADR-096). Draws a symmetric geometric face — circle head, eyes, mouth —
/// tinted by an FNV-1a hash of the NPC id, plus the NPC's initials. The same id
/// renders the same face on every run and every platform.
///
/// Uses FNV-1a over the string's UTF-16 code units (stable by spec).
/// <c>string.GetHashCode()</c> is NOT stable across runs and must never be used
/// for anything the player sees persist.
/// </summary>
public partial class NpcPortrait : Control
{
    private string _initials = "?";
    private Color _accent = UiTheme.GoldDim;
    private Color _head = UiTheme.PanelLight;
    private Color _ink = UiTheme.Inset;
    private bool _smile;

    private Label _initialsLabel = null!;

    public NpcPortrait()
    {
        CustomMinimumSize = new Vector2(110, 136);
    }

    public override void _Ready()
    {
        _initialsLabel = new Label
        {
            Text = _initials,
            HorizontalAlignment = HorizontalAlignment.Center,
            Position = new Vector2(0, 100),
            Size = new Vector2(110, 30),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _initialsLabel.AddThemeFontSizeOverride("font_size", 22);
        _initialsLabel.AddThemeColorOverride("font_color", UiTheme.Dim);
        AddChild(_initialsLabel);
    }

    /// <summary>Stable 32-bit FNV-1a hash. Public so panels can share the palette.</summary>
    public static uint Fnv1a32(string s)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619u;
            }
            return h;
        }
    }

    public void SetNpc(string npcId, string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _initials = parts.Length switch
        {
            0 => "?",
            1 => parts[0].Length > 0 ? parts[0].Substring(0, 1).ToUpperInvariant() : "?",
            _ => (parts[0].Substring(0, 1) + parts[^1].Substring(0, 1)).ToUpperInvariant(),
        };
        var h = Fnv1a32(npcId);
        _accent = Color.FromHsv((h % 360) / 360f, 0.45f, 0.62f);
        _head = Color.FromHsv(((h >> 9) % 360) / 360f, 0.22f, 0.74f);
        _ink = Color.FromHsv(((h >> 17) % 360) / 360f, 0.35f, 0.16f);
        _smile = (h & 1) == 0;
        if (_initialsLabel is not null)
            _initialsLabel.Text = _initials;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var w = Size.X;
        var frame = new StyleBoxFlat { BgColor = UiTheme.Inset };
        frame.SetCornerRadiusAll(6);
        DrawStyleBox(frame, new Rect2(Vector2.Zero, new Vector2(w, Size.Y)));

        var c = new Vector2(w / 2f, 50f);
        const float r = 34f;

        // Hash-tinted halo ring, then the head disc.
        DrawArc(c, r + 5f, 0, Mathf.Tau, 48, _accent, 3f);
        DrawCircle(c, r, _head);
        DrawArc(c, r, 0, Mathf.Tau, 48, _accent.Darkened(0.25f), 1.5f);

        // Symmetric eyes.
        DrawCircle(c + new Vector2(-12f, -7f), 4.5f, _ink);
        DrawCircle(c + new Vector2(12f, -7f), 4.5f, _ink);

        // Mouth: smile or flat line, picked by the hash's lowest bit.
        if (_smile)
            DrawArc(c + new Vector2(0, 4f), 11f, Mathf.Pi * 0.15f, Mathf.Pi * 0.85f, 24, _ink, 2.5f);
        else
            DrawLine(c + new Vector2(-10f, 9f), c + new Vector2(10f, 9f), _ink, 2.5f);
    }
}
