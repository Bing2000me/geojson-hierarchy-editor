using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace GeoJsonEditor.Ui;

/// <summary>界面用到的语义色，按明暗主题取值。</summary>
public static class UiColors
{
    public static Color Surface(Theme t) => t.IsDark ? Color.FromRgb(0x22, 0x25, 0x2B) : Color.FromRgb(0xFF, 0xFF, 0xFF);
    public static Color Chrome(Theme t) => t.IsDark ? Color.FromRgb(0x1A, 0x1C, 0x21) : Color.FromRgb(0xF7, 0xF8, 0xFA);
    public static Color Divider(Theme t) => t.IsDark ? Color.FromRgb(0x33, 0x37, 0x40) : Color.FromRgb(0xE3, 0xE6, 0xEB);
    public static Color Subtle(Theme t) => t.IsDark ? Color.FromRgb(0x9A, 0xA3, 0xB2) : Color.FromRgb(0x6B, 0x72, 0x80);
    public static Color Faint(Theme t) => t.IsDark ? Color.FromRgb(0x6B, 0x72, 0x80) : Color.FromRgb(0x9C, 0xA3, 0xAF);
    public static Color Hover(Theme t) => t.IsDark ? Color.FromRgb(0x2E, 0x32, 0x3A) : Color.FromRgb(0xEE, 0xF0, 0xF3);
    public static Color Pressed(Theme t) => t.IsDark ? Color.FromRgb(0x38, 0x3D, 0x47) : Color.FromRgb(0xE2, 0xE6, 0xEB);
    public static Color AccentSoft(Theme t) => t.Palette.Accent.WithAlpha(t.IsDark ? (byte)56 : (byte)30);
    public static Color Card(Theme t) => t.IsDark ? Color.FromRgb(0x27, 0x2A, 0x31) : Color.FromRgb(0xF7, 0xF8, 0xFA);
    public static Color Danger(Theme t) => t.IsDark ? Color.FromRgb(0xF8, 0x71, 0x71) : Color.FromRgb(0xDC, 0x26, 0x26);
    public static Color Warning(Theme t) => t.IsDark ? Color.FromRgb(0xFB, 0xBF, 0x24) : Color.FromRgb(0xB4, 0x53, 0x09);
}

/// <summary>应用级样式表：工具按钮、图标按钮、标签按钮等。</summary>
public static class AppStyles
{
    public const string Tool = "app-tool";
    public const string IconButton = "app-icon";
    public const string Chip = "app-chip";
    public const string Ghost = "app-ghost";
    public const string Primary = "app-primary";
    public const string Danger = "app-danger";
    public const string DropDown = "app-dropdown";

    public static StyleSheet Create()
    {
        var sheet = new StyleSheet();
        sheet.Define(Tool, CreateToolStyle);
        sheet.Define(IconButton, CreateIconStyle);
        sheet.Define(Chip, CreateChipStyle);
        sheet.Define(Ghost, CreateGhostStyle);
        sheet.Define(Primary, CreatePrimaryStyle);
        sheet.Define(Danger, CreateDangerStyle);
        sheet.Define(DropDown, CreateDropDownStyle);
        return sheet;
    }

    private static Transition[] ColorTransitions =>
    [
        Transition.Create(Control.BackgroundProperty),
        Transition.Create(TextElement.ForegroundProperty),
    ];

    /// <summary>顶部工具栏里的工具切换按钮：平时透明，选中时浅色强调底。</summary>
    private static Style CreateToolStyle() => Style.DeriveFromDefault<ToggleButton>(
        transitions: ColorTransitions,
        setters:
        [
            Setter.Create(Control.BackgroundProperty, t => t.Palette.WindowBackground.WithAlpha(0)),
            Setter.Create(Control.BorderThicknessProperty, 0.0),
            Setter.Create(Control.CornerRadiusProperty, 7.0),
            Setter.Create(Control.PaddingProperty, new Thickness(9, 5, 11, 5)),
            Setter.Create(TextElement.ForegroundProperty, t => t.Palette.WindowText),
        ],
        triggers:
        [
            new StateTrigger
            {
                Match = VisualStateFlags.Hot,
                Setters = [Setter.Create(Control.BackgroundProperty, UiColors.Hover)],
            },
            new StateTrigger
            {
                Match = VisualStateFlags.Pressed,
                Setters = [Setter.Create(Control.BackgroundProperty, UiColors.Pressed)],
            },
            new StateTrigger
            {
                Match = VisualStateFlags.Checked,
                Setters =
                [
                    Setter.Create(Control.BackgroundProperty, UiColors.AccentSoft),
                    Setter.Create(TextElement.ForegroundProperty, t => t.Palette.Accent),
                ],
            },
            new StateTrigger
            {
                Exclude = VisualStateFlags.Enabled,
                Setters = [Setter.Create(TextElement.ForegroundProperty, t => t.Palette.DisabledText)],
            },
        ]);

    /// <summary>只有图标的扁平按钮。</summary>
    private static Style CreateIconStyle() => Style.DeriveFromDefault<Button>(
        transitions: ColorTransitions,
        setters:
        [
            Setter.Create(Control.BackgroundProperty, t => t.Palette.WindowBackground.WithAlpha(0)),
            Setter.Create(Control.BorderThicknessProperty, 0.0),
            Setter.Create(Control.CornerRadiusProperty, 6.0),
            Setter.Create(Control.PaddingProperty, new Thickness(5)),
            Setter.Create(TextElement.ForegroundProperty, UiColors.Subtle),
        ],
        triggers:
        [
            new StateTrigger
            {
                Match = VisualStateFlags.Hot,
                Setters =
                [
                    Setter.Create(Control.BackgroundProperty, UiColors.Hover),
                    Setter.Create(TextElement.ForegroundProperty, t => t.Palette.WindowText),
                ],
            },
            new StateTrigger
            {
                Match = VisualStateFlags.Pressed,
                Setters = [Setter.Create(Control.BackgroundProperty, UiColors.Pressed)],
            },
            new StateTrigger
            {
                Exclude = VisualStateFlags.Enabled,
                Setters = [Setter.Create(TextElement.ForegroundProperty, t => t.Palette.DisabledText.WithAlpha(120))],
            },
        ]);

    /// <summary>带文字的扁平按钮（面板里的次要操作）。</summary>
    private static Style CreateGhostStyle() => Style.DeriveFromDefault<Button>(
        transitions: ColorTransitions,
        setters:
        [
            Setter.Create(Control.BackgroundProperty, t => t.Palette.WindowBackground.WithAlpha(0)),
            Setter.Create(Control.BorderThicknessProperty, 1.0),
            Setter.Create(Control.BorderBrushProperty, UiColors.Divider),
            Setter.Create(Control.CornerRadiusProperty, 6.0),
            Setter.Create(Control.PaddingProperty, new Thickness(10, 5)),
            Setter.Create(TextElement.ForegroundProperty, t => t.Palette.WindowText),
        ],
        triggers:
        [
            new StateTrigger
            {
                Match = VisualStateFlags.Hot,
                Setters = [Setter.Create(Control.BackgroundProperty, UiColors.Hover)],
            },
            new StateTrigger
            {
                Match = VisualStateFlags.Pressed,
                Setters = [Setter.Create(Control.BackgroundProperty, UiColors.Pressed)],
            },
            new StateTrigger
            {
                Exclude = VisualStateFlags.Enabled,
                Setters = [Setter.Create(TextElement.ForegroundProperty, t => t.Palette.DisabledText)],
            },
        ]);

    /// <summary>主要操作按钮：强调色实底。</summary>
    private static Style CreatePrimaryStyle() => Style.DeriveFromDefault<Button>(
        transitions: ColorTransitions,
        setters:
        [
            Setter.Create(Control.BackgroundProperty, t => t.Palette.Accent),
            Setter.Create(Control.BorderThicknessProperty, 0.0),
            Setter.Create(Control.CornerRadiusProperty, 6.0),
            Setter.Create(Control.PaddingProperty, new Thickness(12, 6)),
            Setter.Create(TextElement.ForegroundProperty, t => t.Palette.AccentText),
        ],
        triggers:
        [
            new StateTrigger
            {
                Match = VisualStateFlags.Hot,
                Setters = [Setter.Create(Control.BackgroundProperty, t => t.Palette.Accent.Lerp(Color.Black, 0.1))],
            },
            new StateTrigger
            {
                Match = VisualStateFlags.Pressed,
                Setters = [Setter.Create(Control.BackgroundProperty, t => t.Palette.Accent.Lerp(Color.Black, 0.2))],
            },
            new StateTrigger
            {
                Exclude = VisualStateFlags.Enabled,
                Setters =
                [
                    Setter.Create(Control.BackgroundProperty, t => t.Palette.DisabledAccent),
                    Setter.Create(TextElement.ForegroundProperty, t => t.Palette.AccentText.WithAlpha(180)),
                ],
            },
        ]);

    private static Style CreateDangerStyle() => Style.DeriveFromDefault<Button>(
        transitions: ColorTransitions,
        setters:
        [
            Setter.Create(Control.BackgroundProperty, t => t.Palette.WindowBackground.WithAlpha(0)),
            Setter.Create(Control.BorderThicknessProperty, 1.0),
            Setter.Create(Control.BorderBrushProperty, t => UiColors.Danger(t).WithAlpha(90)),
            Setter.Create(Control.CornerRadiusProperty, 6.0),
            Setter.Create(Control.PaddingProperty, new Thickness(10, 5)),
            Setter.Create(TextElement.ForegroundProperty, UiColors.Danger),
        ],
        triggers:
        [
            new StateTrigger
            {
                Match = VisualStateFlags.Hot,
                Setters = [Setter.Create(Control.BackgroundProperty, t => UiColors.Danger(t).WithAlpha(24))],
            },
            new StateTrigger
            {
                Match = VisualStateFlags.Pressed,
                Setters = [Setter.Create(Control.BackgroundProperty, t => UiColors.Danger(t).WithAlpha(44))],
            },
        ]);

    /// <summary>扁平的下拉按钮（文件菜单、绘制目标选择）。</summary>
    private static Style CreateDropDownStyle() => Style.DeriveFromDefault<DropDownButton>(
        transitions: ColorTransitions,
        setters:
        [
            Setter.Create(Control.BackgroundProperty, t => t.Palette.WindowBackground.WithAlpha(0)),
            Setter.Create(Control.BorderThicknessProperty, 1.0),
            Setter.Create(Control.BorderBrushProperty, UiColors.Divider),
            Setter.Create(Control.CornerRadiusProperty, 6.0),
            Setter.Create(TextElement.ForegroundProperty, t => t.Palette.WindowText),
        ],
        triggers:
        [
            new StateTrigger
            {
                Match = VisualStateFlags.Hot,
                Setters = [Setter.Create(Control.BackgroundProperty, UiColors.Hover)],
            },
            new StateTrigger
            {
                Match = VisualStateFlags.Pressed,
                Setters = [Setter.Create(Control.BackgroundProperty, UiColors.Pressed)],
            },
        ]);

    /// <summary>小号圆角标签按钮（级别快捷选择等）。</summary>
    private static Style CreateChipStyle() => Style.DeriveFromDefault<ToggleButton>(
        transitions: ColorTransitions,
        setters:
        [
            Setter.Create(Control.BackgroundProperty, t => t.Palette.WindowBackground.WithAlpha(0)),
            Setter.Create(Control.BorderThicknessProperty, 1.0),
            Setter.Create(Control.BorderBrushProperty, UiColors.Divider),
            Setter.Create(Control.CornerRadiusProperty, 11.0),
            Setter.Create(Control.PaddingProperty, new Thickness(10, 2)),
            Setter.Create(TextElement.ForegroundProperty, UiColors.Subtle),
            Setter.Create(TextElement.FontSizeProperty, 12.0),
        ],
        triggers:
        [
            new StateTrigger
            {
                Match = VisualStateFlags.Hot,
                Setters =
                [
                    Setter.Create(Control.BackgroundProperty, UiColors.Hover),
                    Setter.Create(TextElement.ForegroundProperty, t => t.Palette.WindowText),
                ],
            },
            new StateTrigger
            {
                Match = VisualStateFlags.Checked,
                Setters =
                [
                    Setter.Create(Control.BackgroundProperty, UiColors.AccentSoft),
                    Setter.Create(Control.BorderBrushProperty, t => t.Palette.Accent.WithAlpha(120)),
                    Setter.Create(TextElement.ForegroundProperty, t => t.Palette.Accent),
                ],
            },
        ]);
}
