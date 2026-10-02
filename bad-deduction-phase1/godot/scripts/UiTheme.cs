using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// The Veiled City look: near-black blues, parchment text, gold accents. One shared
/// <see cref="Theme"/> built in code so all ten panels stay coherent without .tres files.
/// </summary>
public static class UiTheme
{
    public static readonly Color Bg = new("#0e1118");
    public static readonly Color Panel = new("#161b26");
    public static readonly Color PanelLight = new("#1f2635");
    public static readonly Color Inset = new("#0a0d13");
    public static readonly Color Gold = new("#d4af37");
    public static readonly Color GoldDim = new("#8a7326");
    public static readonly Color Text = new("#e8e4d8");
    public static readonly Color Dim = new("#9a958a");
    public static readonly Color Faint = new("#5f5b52");
    public static readonly Color Red = new("#c0392b");
    public static readonly Color Green = new("#3fa34d");
    public static readonly Color Blue = new("#5b8dd9");
    public static readonly Color Purple = new("#9b59b6");

    public static Theme Build()
    {
        var theme = new Theme();
        theme.DefaultFontSize = 16;

        var panelSb = new StyleBoxFlat
        {
            BgColor = Panel,
            BorderColor = GoldDim,
        };
        panelSb.SetBorderWidthAll(1);
        panelSb.SetCornerRadiusAll(4);
        panelSb.SetContentMarginAll(10);
        theme.SetStylebox("panel", "PanelContainer", panelSb);

        var insetSb = new StyleBoxFlat { BgColor = Inset };
        insetSb.SetCornerRadiusAll(3);
        insetSb.SetContentMarginAll(8);
        theme.SetStylebox("panel", "TextEdit", insetSb);
        theme.SetStylebox("panel", "LineEdit", insetSb);
        theme.SetStylebox("panel", "RichTextLabel", insetSb);

        var btnNormal = new StyleBoxFlat { BgColor = PanelLight, BorderColor = GoldDim };
        btnNormal.SetBorderWidthAll(1);
        btnNormal.SetCornerRadiusAll(4);
        btnNormal.SetContentMarginAll(8);
        var btnHover = (StyleBoxFlat)btnNormal.Duplicate();
        btnHover.BgColor = new Color("#2a3348");
        btnHover.BorderColor = Gold;
        var btnPressed = (StyleBoxFlat)btnNormal.Duplicate();
        btnPressed.BgColor = new Color("#2e3a55");
        theme.SetStylebox("normal", "Button", btnNormal);
        theme.SetStylebox("hover", "Button", btnHover);
        theme.SetStylebox("pressed", "Button", btnPressed);
        theme.SetStylebox("focus", "Button", new StyleBoxEmpty());
        theme.SetColor("font_color", "Button", Text);
        theme.SetColor("font_hover_color", "Button", Gold);
        theme.SetColor("font_pressed_color", "Button", Gold);

        theme.SetColor("font_color", "Label", Text);
        theme.SetColor("font_color", "LineEdit", Text);
        theme.SetColor("font_color", "TextEdit", Text);
        theme.SetColor("font_color", "RichTextLabel", Text);
        theme.SetColor("font_color", "ItemList", Text);
        theme.SetColor("font_color", "OptionButton", Text);

        var barBg = new StyleBoxFlat { BgColor = Inset };
        barBg.SetCornerRadiusAll(3);
        theme.SetStylebox("background", "ProgressBar", barBg);
        var barFill = new StyleBoxFlat { BgColor = Gold };
        barFill.SetCornerRadiusAll(3);
        theme.SetStylebox("fill", "ProgressBar", barFill);

        return theme;
    }

    public static Label TitleLabel(string text, int size = 26)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", Gold);
        l.AddThemeFontSizeOverride("font_size", size);
        return l;
    }

    public static Label HeaderLabel(string text, int size = 19)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", Gold);
        l.AddThemeFontSizeOverride("font_size", size);
        return l;
    }

    public static Label DimLabel(string text, int size = 14)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", Dim);
        l.AddThemeFontSizeOverride("font_size", size);
        return l;
    }

    public static HSeparator GoldRule()
    {
        var s = new HSeparator();
        s.AddThemeColorOverride("separator_color", GoldDim);
        return s;
    }

    public static Button MenuButton(string text)
    {
        var b = new Button { Text = text, ToggleMode = true };
        return b;
    }

    public static string FmtTime(long totalMinutes)
    {
        var t = new BadDeduction.Core.GameTime(totalMinutes);
        return $"Day {t.Day}  {t.Hour:00}:{t.Minute:00}";
    }
}
