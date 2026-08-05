using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal readonly record struct ThemePalette(
    Color App,
    Color Top,
    Color Panel,
    Color PanelStrong,
    Color PanelHover,
    Color Field,
    Color FieldHover,
    Color FieldFocus,
    Color Stage,
    Color Border,
    Color BorderHover,
    Color Text,
    Color Muted,
    Color DisabledText,
    Color Accent,
    Color AccentSurface,
    Color AccentHoverSurface,
    Color AccentPressedSurface,
    Color DisabledSurface,
    Color AccentText,
    Color AccentLabel,
    Color Warning,
    Color Danger);

internal interface ITreeNodeTrailingColorSource
{
    IReadOnlyList<Color> TrailingColors { get; }
}

internal interface ITreeNodeLeadingIconSource
{
    SvgIconKind LeadingIcon { get; }
}

internal enum ButtonVisualRole
{
    Standard,
    Toolbar,
    Segmented,
    Primary,
    Danger
}

internal static class Theme
{
    private readonly record struct RowInteractionProgress(float Hover, float Selection, float Pressed);

    public const int ControlHeightCompact = 28;
    public const int ControlHeight = 32;
    public const int IconButtonSize = 34;
    public const int ToolFlyoutButtonWidth = 196;
    public const int ToolFlyoutPanelWidth = 208;
    public const int GapXs = 4;
    public const int GapSm = 8;
    public const int GapMd = 12;

    private const double RowHoverDurationMs = 110d;
    private const double RowSelectionDurationMs = 160d;
    private const double RowPressedDurationMs = 70d;

    private static readonly ThemePalette DefaultPalette = new(
        Color.FromArgb(18, 20, 22),
        Color.FromArgb(24, 27, 30),
        Color.FromArgb(30, 34, 37),
        Color.FromArgb(42, 48, 53),
        Color.FromArgb(55, 63, 68),
        Color.FromArgb(15, 17, 19),
        Color.FromArgb(22, 25, 28),
        Color.FromArgb(24, 31, 32),
        Color.FromArgb(13, 15, 17),
        Color.FromArgb(61, 69, 76),
        Color.FromArgb(91, 103, 110),
        Color.FromArgb(242, 246, 245),
        Color.FromArgb(190, 202, 202),
        Color.FromArgb(116, 125, 128),
        Color.FromArgb(79, 179, 162),
        Color.FromArgb(39, 83, 77),
        Color.FromArgb(51, 107, 99),
        Color.FromArgb(31, 68, 63),
        Color.FromArgb(35, 39, 43),
        Color.FromArgb(5, 22, 20),
        Color.FromArgb(209, 247, 238),
        Color.FromArgb(232, 184, 92),
        Color.FromArgb(232, 104, 104));

    private static readonly ThemePalette WhitePalette = new(
        Color.FromArgb(242, 244, 245),
        Color.FromArgb(255, 255, 255),
        Color.FromArgb(248, 249, 250),
        Color.FromArgb(232, 236, 238),
        Color.FromArgb(222, 228, 231),
        Color.FromArgb(255, 255, 255),
        Color.FromArgb(246, 249, 249),
        Color.FromArgb(238, 248, 246),
        Color.FromArgb(237, 240, 242),
        Color.FromArgb(198, 206, 211),
        Color.FromArgb(137, 151, 158),
        Color.FromArgb(31, 39, 43),
        Color.FromArgb(91, 105, 112),
        Color.FromArgb(151, 162, 167),
        Color.FromArgb(22, 132, 113),
        Color.FromArgb(211, 238, 232),
        Color.FromArgb(190, 229, 221),
        Color.FromArgb(169, 219, 209),
        Color.FromArgb(231, 235, 237),
        Color.FromArgb(255, 255, 255),
        Color.FromArgb(7, 89, 75),
        Color.FromArgb(155, 91, 0),
        Color.FromArgb(190, 52, 62));

    private static ThemePalette _palette = DefaultPalette;

    public static ApplicationColorTheme ColorTheme { get; private set; } = ApplicationColorTheme.Dark;
    public static bool IsLight => ColorTheme == ApplicationColorTheme.White;
    public static int ThemeHueDegrees { get; private set; } = ApplicationSettings.DefaultThemeHueDegrees;
    public static int ThemeSaturationPercent { get; private set; } = ApplicationSettings.DefaultThemeSaturationPercent;
    public static int ThemeBrightnessPercent { get; private set; } = ApplicationSettings.DefaultThemeBrightnessPercent;
    public static int AccentHueDegrees { get; private set; } = ApplicationSettings.DefaultAccentHueDegrees;
    public static int AccentSaturationPercent { get; private set; } = ApplicationSettings.DefaultAccentSaturationPercent;
    public static int AccentBrightnessPercent { get; private set; } = ApplicationSettings.DefaultAccentBrightnessPercent;
    internal static ThemePalette CurrentPalette => _palette;
    public static Color App => _palette.App;
    public static Color Top => _palette.Top;
    public static Color Panel => _palette.Panel;
    public static Color PanelStrong => _palette.PanelStrong;
    public static Color PanelHover => _palette.PanelHover;
    public static Color Field => _palette.Field;
    public static Color FieldHover => _palette.FieldHover;
    public static Color FieldFocus => _palette.FieldFocus;
    public static Color Stage => _palette.Stage;
    public static Color Border => _palette.Border;
    public static Color BorderHover => _palette.BorderHover;
    public static Color Text => _palette.Text;
    public static Color Muted => _palette.Muted;
    public static Color DisabledText => _palette.DisabledText;
    public static Color Accent => _palette.Accent;
    public static Color AccentSurface => _palette.AccentSurface;
    public static Color AccentHoverSurface => _palette.AccentHoverSurface;
    public static Color AccentPressedSurface => _palette.AccentPressedSurface;
    public static Color DisabledSurface => _palette.DisabledSurface;
    public static Color AccentText => _palette.AccentText;
    public static Color AccentLabel => _palette.AccentLabel;
    public static Color Warning => _palette.Warning;
    public static Color Danger => _palette.Danger;
    public static Color DangerSurface => IsLight
        ? Mix(PanelStrong, Danger, 0.14f)
        : Color.FromArgb(72, 42, 44);
    public static Color DangerHoverSurface => IsLight
        ? Mix(PanelStrong, Danger, 0.22f)
        : Color.FromArgb(98, 48, 51);
    public static Color DangerPressedSurface => IsLight
        ? Mix(Panel, Danger, 0.28f)
        : Color.FromArgb(58, 34, 36);
    public static Color DangerText => IsLight ? Danger : Color.FromArgb(255, 226, 226);

    private static readonly ConditionalWeakTable<Control, FieldInteractionState> FieldStates = new();
    private static readonly ConditionalWeakTable<Button, ButtonThemeState> StyledButtons = new();
    private static readonly ConditionalWeakTable<ComboBox, object> StyledComboBoxes = new();
    private static readonly ConditionalWeakTable<ListBox, ListBoxInteractionState> StyledListBoxes = new();
    private static readonly ConditionalWeakTable<TreeView, TreeViewInteractionState> StyledTreeViews = new();
    private static readonly ConditionalWeakTable<ListView, ListViewInteractionState> StyledListViews = new();
    private static readonly ConditionalWeakTable<ToolTip, object> StyledToolTips = new();

    public static Font UiFont(float size = 9.5f, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    public static void ConfigureHues(int themeHueDegrees, int accentHueDegrees)
    {
        ConfigureColorAdjustments(
            ColorTheme,
            themeHueDegrees,
            ThemeSaturationPercent,
            ThemeBrightnessPercent,
            accentHueDegrees,
            AccentSaturationPercent,
            AccentBrightnessPercent);
    }

    public static void Configure(
        ApplicationColorTheme colorTheme,
        int themeHueDegrees,
        int accentHueDegrees)
    {
        ConfigureColorAdjustments(
            colorTheme,
            themeHueDegrees,
            ApplicationSettings.DefaultThemeSaturationPercent,
            ApplicationSettings.DefaultThemeBrightnessPercent,
            accentHueDegrees,
            ApplicationSettings.DefaultAccentSaturationPercent,
            ApplicationSettings.DefaultAccentBrightnessPercent);
    }

    public static void ConfigureColorAdjustments(
        ApplicationColorTheme colorTheme,
        int themeHueDegrees,
        int themeSaturationPercent,
        int themeBrightnessPercent,
        int accentHueDegrees,
        int accentSaturationPercent,
        int accentBrightnessPercent)
    {
        ColorTheme = Enum.IsDefined(colorTheme) ? colorTheme : ApplicationColorTheme.Dark;
        ThemeHueDegrees = ApplicationSettingsStore.NormalizeHueDegrees(themeHueDegrees);
        ThemeSaturationPercent = ApplicationSettingsStore.NormalizeSaturationPercent(themeSaturationPercent);
        ThemeBrightnessPercent = ApplicationSettingsStore.NormalizeBrightnessPercent(themeBrightnessPercent);
        AccentHueDegrees = ApplicationSettingsStore.NormalizeHueDegrees(accentHueDegrees);
        AccentSaturationPercent = ApplicationSettingsStore.NormalizeSaturationPercent(accentSaturationPercent);
        AccentBrightnessPercent = ApplicationSettingsStore.NormalizeBrightnessPercent(accentBrightnessPercent);
        _palette = PaletteForAdjustments(
            ColorTheme,
            ThemeHueDegrees,
            ThemeSaturationPercent,
            ThemeBrightnessPercent,
            AccentHueDegrees,
            AccentSaturationPercent,
            AccentBrightnessPercent);
    }

    internal static void RefreshControlTree(Control root, ThemePalette previousPalette)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.SuspendLayout();
        try
        {
            var nativeThemeChanged = IsLightPalette(previousPalette) != IsLightPalette(_palette);
            RefreshControl(root, previousPalette, _palette, nativeThemeChanged);
        }
        finally
        {
            root.ResumeLayout(performLayout: false);
        }
        root.Invalidate(invalidateChildren: true);
    }

    private static void RefreshControl(
        Control control,
        ThemePalette previousPalette,
        ThemePalette nextPalette,
        bool nativeThemeChanged)
    {
        if (control is not StageControl)
        {
            control.BackColor = IsFieldControl(control)
                ? RemapFieldColor(control.BackColor, previousPalette, nextPalette)
                : RemapBackgroundColor(control.BackColor, previousPalette, nextPalette);
        }
        control.ForeColor = RemapForegroundColor(control.ForeColor, previousPalette, nextPalette);

        if (control is Button button)
        {
            var dialogActionRefreshed = button.FindForm() is ModernDialogForm dialog
                && dialog.RefreshDialogActionTheme(button);
            if (!dialogActionRefreshed) ReapplyButtonStyle(button);
        }
        if (control is ModernNumericUpDown modernNumeric) modernNumeric.RefreshTheme();
        if (nativeThemeChanged && control is ListBox or TreeView or ListView) StyleNativeScrollBars(control);
        if (control is TreeView tree) RefreshTreeNodes(tree.Nodes, previousPalette, nextPalette);
        if (control is ListView list)
        {
            foreach (ListViewItem item in list.Items)
            {
                item.ForeColor = RemapForegroundColor(item.ForeColor, previousPalette, nextPalette);
            }
        }
        if (control is ToolStrip strip)
        {
            foreach (ToolStripItem item in strip.Items) RefreshToolStripItem(item, previousPalette, nextPalette);
        }

        foreach (Control child in control.Controls)
        {
            RefreshControl(child, previousPalette, nextPalette, nativeThemeChanged);
        }
    }

    private static bool IsLightPalette(ThemePalette palette)
    {
        return palette.App.GetBrightness() > palette.Text.GetBrightness();
    }

    private static void RefreshTreeNodes(
        TreeNodeCollection nodes,
        ThemePalette previousPalette,
        ThemePalette nextPalette)
    {
        foreach (TreeNode node in nodes)
        {
            node.ForeColor = RemapForegroundColor(node.ForeColor, previousPalette, nextPalette);
            RefreshTreeNodes(node.Nodes, previousPalette, nextPalette);
        }
    }

    private static void RefreshToolStripItem(
        ToolStripItem item,
        ThemePalette previousPalette,
        ThemePalette nextPalette)
    {
        item.BackColor = RemapBackgroundColor(item.BackColor, previousPalette, nextPalette);
        item.ForeColor = RemapForegroundColor(item.ForeColor, previousPalette, nextPalette);
        if (item is not ToolStripDropDownItem dropDown) return;
        foreach (ToolStripItem child in dropDown.DropDownItems)
        {
            RefreshToolStripItem(child, previousPalette, nextPalette);
        }
    }

    private static bool IsFieldControl(Control control)
    {
        return control is TextBoxBase or ComboBox or NumericUpDown or ModernNumericUpDown;
    }

    private static Color RemapFieldColor(Color color, ThemePalette from, ThemePalette to)
    {
        if (SameRgb(color, from.Field)) return WithAlpha(to.Field, color.A);
        if (SameRgb(color, from.FieldHover)) return WithAlpha(to.FieldHover, color.A);
        if (SameRgb(color, from.FieldFocus)) return WithAlpha(to.FieldFocus, color.A);
        if (SameRgb(color, from.DisabledSurface)) return WithAlpha(to.DisabledSurface, color.A);
        return RemapBackgroundColor(color, from, to);
    }

    private static Color RemapBackgroundColor(Color color, ThemePalette from, ThemePalette to)
    {
        if (color.IsEmpty || color.A == 0) return color;
        if (SameRgb(color, from.App)) return WithAlpha(to.App, color.A);
        if (SameRgb(color, from.Top)) return WithAlpha(to.Top, color.A);
        if (SameRgb(color, from.Panel)) return WithAlpha(to.Panel, color.A);
        if (SameRgb(color, from.PanelStrong)) return WithAlpha(to.PanelStrong, color.A);
        if (SameRgb(color, from.PanelHover)) return WithAlpha(to.PanelHover, color.A);
        if (SameRgb(color, from.Field)) return WithAlpha(to.Field, color.A);
        if (SameRgb(color, from.FieldHover)) return WithAlpha(to.FieldHover, color.A);
        if (SameRgb(color, from.FieldFocus)) return WithAlpha(to.FieldFocus, color.A);
        if (SameRgb(color, from.Stage)) return WithAlpha(to.Stage, color.A);
        if (SameRgb(color, from.Border)) return WithAlpha(to.Border, color.A);
        if (SameRgb(color, from.BorderHover)) return WithAlpha(to.BorderHover, color.A);
        if (SameRgb(color, from.Accent)) return WithAlpha(to.Accent, color.A);
        if (SameRgb(color, from.AccentSurface)) return WithAlpha(to.AccentSurface, color.A);
        if (SameRgb(color, from.AccentHoverSurface)) return WithAlpha(to.AccentHoverSurface, color.A);
        if (SameRgb(color, from.AccentPressedSurface)) return WithAlpha(to.AccentPressedSurface, color.A);
        if (SameRgb(color, from.DisabledSurface)) return WithAlpha(to.DisabledSurface, color.A);
        return color;
    }

    private static Color RemapForegroundColor(Color color, ThemePalette from, ThemePalette to)
    {
        if (color.IsEmpty || color.A == 0) return color;
        if (SameRgb(color, from.Text)) return WithAlpha(to.Text, color.A);
        if (SameRgb(color, from.Muted)) return WithAlpha(to.Muted, color.A);
        if (SameRgb(color, from.DisabledText)) return WithAlpha(to.DisabledText, color.A);
        if (SameRgb(color, from.Accent)) return WithAlpha(to.Accent, color.A);
        if (SameRgb(color, from.AccentText)) return WithAlpha(to.AccentText, color.A);
        if (SameRgb(color, from.AccentLabel)) return WithAlpha(to.AccentLabel, color.A);
        if (SameRgb(color, from.Warning)) return WithAlpha(to.Warning, color.A);
        if (SameRgb(color, from.Danger)) return WithAlpha(to.Danger, color.A);
        if (SameRgb(color, from.Border)) return WithAlpha(to.Border, color.A);
        if (SameRgb(color, from.BorderHover)) return WithAlpha(to.BorderHover, color.A);
        return color;
    }

    private static bool SameRgb(Color left, Color right)
    {
        return left.R == right.R && left.G == right.G && left.B == right.B;
    }

    private static Color WithAlpha(Color color, int alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    internal static ThemePalette PaletteForHues(int themeHueDegrees, int accentHueDegrees)
    {
        return PaletteFor(ApplicationColorTheme.Dark, themeHueDegrees, accentHueDegrees);
    }

    internal static ThemePalette PaletteFor(
        ApplicationColorTheme colorTheme,
        int themeHueDegrees,
        int accentHueDegrees)
    {
        return PaletteForAdjustments(
            colorTheme,
            themeHueDegrees,
            ApplicationSettings.DefaultThemeSaturationPercent,
            ApplicationSettings.DefaultThemeBrightnessPercent,
            accentHueDegrees,
            ApplicationSettings.DefaultAccentSaturationPercent,
            ApplicationSettings.DefaultAccentBrightnessPercent);
    }

    internal static ThemePalette PaletteForAdjustments(
        ApplicationColorTheme colorTheme,
        int themeHueDegrees,
        int themeSaturationPercent,
        int themeBrightnessPercent,
        int accentHueDegrees,
        int accentSaturationPercent,
        int accentBrightnessPercent)
    {
        var basePalette = colorTheme == ApplicationColorTheme.White ? WhitePalette : DefaultPalette;
        var themeShift = ApplicationSettingsStore.NormalizeHueDegrees(themeHueDegrees)
            - ApplicationSettings.DefaultThemeHueDegrees;
        var accentShift = ApplicationSettingsStore.NormalizeHueDegrees(accentHueDegrees)
            - ApplicationSettings.DefaultAccentHueDegrees;
        themeSaturationPercent = ApplicationSettingsStore.NormalizeSaturationPercent(themeSaturationPercent);
        themeBrightnessPercent = ApplicationSettingsStore.NormalizeBrightnessPercent(themeBrightnessPercent);
        accentSaturationPercent = ApplicationSettingsStore.NormalizeSaturationPercent(accentSaturationPercent);
        accentBrightnessPercent = ApplicationSettingsStore.NormalizeBrightnessPercent(accentBrightnessPercent);
        if (themeShift == 0
            && accentShift == 0
            && themeSaturationPercent == ApplicationSettings.DefaultThemeSaturationPercent
            && themeBrightnessPercent == ApplicationSettings.DefaultThemeBrightnessPercent
            && accentSaturationPercent == ApplicationSettings.DefaultAccentSaturationPercent
            && accentBrightnessPercent == ApplicationSettings.DefaultAccentBrightnessPercent)
        {
            return basePalette;
        }

        return new ThemePalette(
            AdjustColor(basePalette.App, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.Top, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.Panel, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.PanelStrong, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.PanelHover, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.Field, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.FieldHover, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.FieldFocus, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.Stage, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.Border, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.BorderHover, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.Text, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.Muted, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.DisabledText, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.Accent, accentShift, accentSaturationPercent, accentBrightnessPercent),
            AdjustColor(basePalette.AccentSurface, accentShift, accentSaturationPercent, accentBrightnessPercent),
            AdjustColor(basePalette.AccentHoverSurface, accentShift, accentSaturationPercent, accentBrightnessPercent),
            AdjustColor(basePalette.AccentPressedSurface, accentShift, accentSaturationPercent, accentBrightnessPercent),
            AdjustColor(basePalette.DisabledSurface, themeShift, themeSaturationPercent, themeBrightnessPercent),
            AdjustColor(basePalette.AccentText, accentShift, accentSaturationPercent, accentBrightnessPercent),
            AdjustColor(basePalette.AccentLabel, accentShift, accentSaturationPercent, accentBrightnessPercent),
            basePalette.Warning,
            basePalette.Danger);
    }

    internal static Color HueSpectrumColor(float amount)
    {
        return ColorFromHsl(amount * 360f, 0.76f, 0.56f, 255);
    }

    internal static Color HslPreviewColor(float hue, float saturation, float lightness)
    {
        return ColorFromHsl(hue, saturation, lightness, 255);
    }

    private static Color AdjustColor(
        Color color,
        int shiftDegrees,
        int saturationPercent,
        int brightnessPercent)
    {
        if (ApplicationSettingsStore.NormalizeHueDegrees(shiftDegrees) == 0
            && saturationPercent == 100
            && brightnessPercent == 100)
        {
            return color;
        }
        return ColorFromHsl(
            color.GetHue() + shiftDegrees,
            color.GetSaturation() * saturationPercent / 100f,
            color.GetBrightness() * brightnessPercent / 100f,
            color.A);
    }

    private static Color ColorFromHsl(float hue, float saturation, float lightness, int alpha)
    {
        hue = ((hue % 360f) + 360f) % 360f;
        saturation = Math.Clamp(saturation, 0f, 1f);
        lightness = Math.Clamp(lightness, 0f, 1f);
        var chroma = (1f - MathF.Abs(2f * lightness - 1f)) * saturation;
        var secondary = chroma * (1f - MathF.Abs(hue / 60f % 2f - 1f));
        var offset = lightness - chroma / 2f;
        var (red, green, blue) = hue switch
        {
            < 60f => (chroma, secondary, 0f),
            < 120f => (secondary, chroma, 0f),
            < 180f => (0f, chroma, secondary),
            < 240f => (0f, secondary, chroma),
            < 300f => (secondary, 0f, chroma),
            _ => (chroma, 0f, secondary)
        };
        return Color.FromArgb(
            alpha,
            (int)Math.Clamp(MathF.Round((red + offset) * 255f), 0, 255),
            (int)Math.Clamp(MathF.Round((green + offset) * 255f), 0, 255),
            (int)Math.Clamp(MathF.Round((blue + offset) * 255f), 0, 255));
    }

    public static void StyleButton(Button button)
    {
        var state = ButtonThemeStateFor(button);
        ApplyButtonStyle(button, state.Role, active: false);
    }

    public static void StyleActiveButton(Button button)
    {
        var state = ButtonThemeStateFor(button);
        ApplyButtonStyle(button, state.Role, active: true);
    }

    public static void StyleStandardButton(Button button, bool active = false)
    {
        ApplyButtonStyle(button, ButtonVisualRole.Standard, active);
    }

    public static void StyleToolbarButton(Button button, bool active = false)
    {
        ApplyButtonStyle(button, ButtonVisualRole.Toolbar, active);
    }

    public static void StyleSegmentedButton(Button button, bool active = false)
    {
        ApplyButtonStyle(button, ButtonVisualRole.Segmented, active);
    }

    public static void StylePrimaryButton(Button button)
    {
        ApplyButtonStyle(button, ButtonVisualRole.Primary, active: false);
    }

    public static void StyleDangerButton(Button button)
    {
        ApplyButtonStyle(button, ButtonVisualRole.Danger, active: false);
    }

    internal static ButtonVisualRole ButtonRoleFor(Button button)
    {
        return ButtonThemeStateFor(button).Role;
    }

    public static Color Mix(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(from.A + (to.A - from.A) * amount),
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }

    internal static float AdvanceRowMotion(float from, float to, double elapsedMs, double durationMs)
    {
        if (durationMs <= 0d || elapsedMs >= durationMs) return to;
        if (elapsedMs <= 0d) return from;
        var linear = Math.Clamp((float)(elapsedMs / durationMs), 0f, 1f);
        var eased = 1f - MathF.Pow(1f - linear, 3f);
        return from + (to - from) * eased;
    }

    internal static Color AnimatedRowBackgroundColor(
        Color rowColor,
        float hoverProgress,
        float selectionProgress,
        float pressedProgress)
    {
        hoverProgress = Math.Clamp(hoverProgress, 0f, 1f);
        selectionProgress = Math.Clamp(selectionProgress, 0f, 1f);
        pressedProgress = Math.Clamp(pressedProgress, 0f, 1f);
        var hoverAmount = 0.68f * hoverProgress * (1f - selectionProgress);
        var result = Mix(rowColor, PanelHover, hoverAmount);
        result = Mix(result, AccentSurface, 0.72f * selectionProgress);
        return Mix(result, AccentPressedSurface, 0.32f * pressedProgress);
    }

    private static void ReapplyButtonStyle(Button button)
    {
        var state = ButtonThemeStateFor(button);
        ApplyButtonStyle(button, state.Role, state.Active);
    }

    private static ButtonThemeState ButtonThemeStateFor(Button button)
    {
        return StyledButtons.GetValue(
            button,
            static control => new ButtonThemeState(
                control,
                control is SvgIconButton ? ButtonVisualRole.Toolbar : ButtonVisualRole.Standard));
    }

    private static void ApplyButtonStyle(Button button, ButtonVisualRole role, bool active)
    {
        var state = ButtonThemeStateFor(button);
        state.Role = role;
        state.Active = active;

        var normalColor = role switch
        {
            ButtonVisualRole.Toolbar => button.Parent?.BackColor ?? Top,
            ButtonVisualRole.Segmented => active ? AccentSurface : Panel,
            ButtonVisualRole.Primary => Accent,
            ButtonVisualRole.Danger => DangerSurface,
            _ => active ? AccentSurface : PanelStrong
        };
        var hoverColor = role switch
        {
            ButtonVisualRole.Toolbar => active ? AccentHoverSurface : PanelStrong,
            ButtonVisualRole.Segmented => active ? AccentHoverSurface : PanelStrong,
            ButtonVisualRole.Primary => Mix(Accent, IsLight ? Color.Black : Color.White, 0.09f),
            ButtonVisualRole.Danger => DangerHoverSurface,
            _ => active ? AccentHoverSurface : PanelHover
        };
        var pressedColor = role switch
        {
            ButtonVisualRole.Toolbar => active ? AccentPressedSurface : Panel,
            ButtonVisualRole.Segmented => active ? AccentPressedSurface : Top,
            ButtonVisualRole.Primary => Mix(Accent, Color.Black, 0.18f),
            ButtonVisualRole.Danger => DangerPressedSurface,
            _ => active ? AccentPressedSurface : Panel
        };
        var foreColor = role switch
        {
            ButtonVisualRole.Primary => AccentText,
            ButtonVisualRole.Danger => DangerText,
            _ => active ? AccentLabel : Text
        };
        var borderColor = role switch
        {
            ButtonVisualRole.Primary => Accent,
            ButtonVisualRole.Danger => Danger,
            _ => active ? Accent : Border
        };
        var borderSize = role is ButtonVisualRole.Toolbar or ButtonVisualRole.Segmented ? 0 : 1;

        if (SystemInformation.HighContrast)
        {
            var emphasized = active || role is ButtonVisualRole.Primary or ButtonVisualRole.Danger;
            normalColor = emphasized ? SystemColors.Highlight : SystemColors.Control;
            hoverColor = SystemColors.Highlight;
            pressedColor = SystemColors.Highlight;
            foreColor = emphasized ? SystemColors.HighlightText : SystemColors.ControlText;
            borderColor = emphasized ? SystemColors.HighlightText : SystemColors.ControlText;
            borderSize = role == ButtonVisualRole.Toolbar && !emphasized ? 0 : 1;
        }
        if (!button.Enabled)
        {
            foreColor = SystemInformation.HighContrast ? SystemColors.GrayText : DisabledText;
        }

        if (button.UseVisualStyleBackColor) button.UseVisualStyleBackColor = false;
        if (button.FlatStyle != FlatStyle.Flat) button.FlatStyle = FlatStyle.Flat;
        if (button.ForeColor != foreColor) button.ForeColor = foreColor;
        if (button.FlatAppearance.BorderColor != borderColor) button.FlatAppearance.BorderColor = borderColor;
        if (button.FlatAppearance.BorderSize != borderSize) button.FlatAppearance.BorderSize = borderSize;
        // Empty lets FlatButtonAdapter use the animated BackColor instead of replacing it with a native hot color.
        if (!button.FlatAppearance.MouseOverBackColor.IsEmpty) button.FlatAppearance.MouseOverBackColor = Color.Empty;
        if (!button.FlatAppearance.MouseDownBackColor.IsEmpty) button.FlatAppearance.MouseDownBackColor = Color.Empty;
        if (!string.Equals(button.Font.Name, "Segoe UI", StringComparison.Ordinal)
            || Math.Abs(button.Font.Size - 9.5f) > 0.01f
            || button.Font.Style != FontStyle.Regular)
        {
            button.Font = UiFont();
        }

        if (button.TextAlign != ContentAlignment.MiddleCenter) button.TextAlign = ContentAlignment.MiddleCenter;
        if (role == ButtonVisualRole.Segmented
            && button.AccessibleRole == AccessibleRole.RadioButton
            && button.TabStop != active)
        {
            button.TabStop = active;
        }
        UiMotion.ConfigureButton(
            button,
            normalColor,
            hoverColor,
            pressedColor,
            active);
    }

    private sealed class ButtonThemeState
    {
        public ButtonThemeState(Button owner, ButtonVisualRole role)
        {
            Role = role;
            owner.EnabledChanged += (_, _) => ReapplyButtonStyle(owner);
        }

        public ButtonVisualRole Role { get; set; }
        public bool Active { get; set; }
    }

    public static void StyleTextBox(TextBox box)
    {
        box.BackColor = Field;
        box.ForeColor = Text;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = UiFont();
        ConfigureFieldInteraction(box);
    }

    public static void StyleComboBox(ComboBox box)
    {
        box.BackColor = SystemInformation.HighContrast ? SystemColors.Window : Field;
        box.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Text;
        box.FlatStyle = FlatStyle.Flat;
        box.Font = UiFont();
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = 26;
        if (StyledComboBoxes.TryGetValue(box, out _)) return;
        StyledComboBoxes.Add(box, new object());
        box.DrawItem += DrawComboBoxItem;
        ConfigureFieldInteraction(box);
    }

    public static void StyleNumeric(NumericUpDown input)
    {
        input.BackColor = Field;
        input.ForeColor = Text;
        input.BorderStyle = BorderStyle.FixedSingle;
        input.Font = UiFont();
        input.MinimumSize = new Size(0, ControlHeightCompact);
        ConfigureFieldInteraction(input);
    }

    public static void StyleNumeric(ModernNumericUpDown input)
    {
        input.BackColor = Field;
        input.ForeColor = Text;
        input.Font = UiFont();
        input.MinimumSize = new Size(0, ControlHeightCompact);
    }

    public static void StyleListBox(ListBox list)
    {
        list.BackColor = Panel;
        list.ForeColor = Text;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.Font = UiFont(9.2f);
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.ItemHeight = 28;
        list.IntegralHeight = false;
        StyleNativeScrollBars(list);
        if (StyledListBoxes.TryGetValue(list, out _)) return;
        var state = new ListBoxInteractionState(list);
        StyledListBoxes.Add(list, state);
        list.DrawItem += DrawListBoxItem;
    }

    public static void StyleTreeView(
        TreeView tree,
        bool useCustomExpandButtons = false,
        bool animateRows = false,
        bool useSolidFocusCue = false)
    {
        tree.BackColor = Panel;
        tree.ForeColor = Text;
        tree.BorderStyle = BorderStyle.None;
        tree.Font = UiFont(9.5f);
        tree.HideSelection = false;
        tree.FullRowSelect = true;
        tree.ShowLines = false;
        tree.ShowRootLines = false;
        tree.ShowPlusMinus = !useCustomExpandButtons;
        tree.ItemHeight = 28;
        tree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
        tree.HotTracking = true;
        StyleNativeScrollBars(tree);
        if (StyledTreeViews.TryGetValue(tree, out var existingState))
        {
            existingState.ConfigureRowMotion(animateRows);
            existingState.UseSolidFocusCue = useSolidFocusCue;
            return;
        }
        EnableTreeViewDoubleBuffering(tree);
        var state = new TreeViewInteractionState(tree, useCustomExpandButtons, animateRows, useSolidFocusCue);
        StyledTreeViews.Add(tree, state);
        tree.DrawNode += DrawTreeNode;
    }

    public static void StyleListView(ListView list, bool animateRows = false)
    {
        list.BackColor = Panel;
        list.ForeColor = Text;
        list.BorderStyle = BorderStyle.None;
        list.Font = UiFont(9.2f);
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.OwnerDraw = true;
        StyleNativeScrollBars(list);
        if (StyledListViews.TryGetValue(list, out var existingState))
        {
            existingState.ConfigureRowMotion(animateRows);
            return;
        }
        var state = new ListViewInteractionState(list, animateRows);
        StyledListViews.Add(list, state);
        list.DrawColumnHeader += DrawListViewHeader;
        list.DrawItem += DrawListViewItem;
        list.DrawSubItem += DrawListViewSubItem;
    }

    public static void StyleStatusStrip(StatusStrip strip)
    {
        strip.BackColor = Top;
        strip.ForeColor = Muted;
        strip.Font = UiFont(9);
        strip.RenderMode = ToolStripRenderMode.Professional;
        strip.Renderer = new ModernStatusStripRenderer();
    }

    /// <summary>
    /// Applies the dark Explorer theme to stock control scrollbars. Complex
    /// scrolling surfaces use <see cref="ThemedScrollPanel"/> for the fully
    /// custom accent treatment.
    /// </summary>
    private static void StyleNativeScrollBars(Control control)
    {
        void ApplyTheme()
        {
            if (control.IsDisposed || !control.IsHandleCreated) return;
            try
            {
                SetWindowTheme(control.Handle, IsLight ? "Explorer" : "DarkMode_Explorer", null);
            }
            catch (EntryPointNotFoundException)
            {
                // Older Windows versions keep their native scrollbar rendering.
            }
        }

        if (control.IsHandleCreated) ApplyTheme();
        else control.HandleCreated += (_, _) => ApplyTheme();
    }

    private static void EnableTreeViewDoubleBuffering(TreeView tree)
    {
        var doubleBuffered = typeof(Control).GetProperty(
            "DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        doubleBuffered?.SetValue(tree, true);
    }

    public static void StyleToolTip(ToolTip toolTip)
    {
        toolTip.BackColor = PanelStrong;
        toolTip.ForeColor = Text;
        toolTip.AutoPopDelay = 6000;
        toolTip.InitialDelay = 420;
        toolTip.ReshowDelay = 90;
        toolTip.ShowAlways = true;
        toolTip.OwnerDraw = true;
        if (StyledToolTips.TryGetValue(toolTip, out _)) return;
        StyledToolTips.Add(toolTip, new object());
        toolTip.Popup += (_, e) =>
        {
            var text = e.AssociatedControl is null
                ? string.Empty
                : UiLocalization.T(toolTip.GetToolTip(e.AssociatedControl));
            using var font = UiFont(9);
            var size = TextRenderer.MeasureText(text, font, new Size(360, 0), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
            e.ToolTipSize = new Size(Math.Max(64, size.Width + 18), Math.Max(28, size.Height + 12));
        };
        toolTip.Draw += (_, e) =>
        {
            using var background = new SolidBrush(PanelStrong);
            using var border = new Pen(BorderHover);
            e.Graphics.FillRectangle(background, e.Bounds);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, e.Bounds.Width - 1), Math.Max(0, e.Bounds.Height - 1));
            var bounds = Rectangle.Inflate(e.Bounds, -9, -6);
            using var font = UiFont(9);
            TextRenderer.DrawText(
                e.Graphics,
                UiLocalization.T(e.ToolTipText),
                font,
                bounds,
                Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        };
    }

    public static Label Label(string text, int left, int top, int width, Color color, Font? font = null)
    {
        return new Label
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = 24,
            ForeColor = color,
            BackColor = Color.Transparent,
            Font = font ?? UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
    }

    private static void ConfigureFieldInteraction(Control control)
    {
        if (FieldStates.TryGetValue(control, out _)) return;
        FieldStates.Add(control, new FieldInteractionState(control));
    }

    private static void DrawComboBoxItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox box) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var focused = (e.State & DrawItemState.Focus) != 0;
        var highContrast = SystemInformation.HighContrast;
        var background = highContrast
            ? selected ? SystemColors.Highlight : SystemColors.Window
            : selected ? AccentSurface : box.BackColor;
        using var backgroundBrush = new SolidBrush(background);
        e.Graphics.FillRectangle(backgroundBrush, e.Bounds);

        if (selected)
        {
            using var accent = new SolidBrush(highContrast ? SystemColors.HighlightText : Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height);
        }

        var text = e.Index >= 0 && e.Index < box.Items.Count
            ? box.GetItemText(box.Items[e.Index])
            : box.Text;
        text = UiLocalization.T(text);
        var textBounds = Rectangle.Inflate(e.Bounds, -10, 0);
        TextRenderer.DrawText(
            e.Graphics,
            text,
            box.Font,
            textBounds,
            highContrast
                ? box.Enabled ? selected ? SystemColors.HighlightText : SystemColors.WindowText : SystemColors.GrayText
                : box.Enabled ? Text : DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (focused && e.Index >= 0)
        {
            using var focus = new Pen(highContrast ? SystemColors.HighlightText : Accent);
            var focusBounds = Rectangle.Inflate(e.Bounds, -1, -1);
            e.Graphics.DrawRectangle(focus, focusBounds);
        }
    }

    private static void DrawListBoxItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list || e.Index < 0 || e.Index >= list.Items.Count) return;
        StyledListBoxes.TryGetValue(list, out var state);
        var selected = (e.State & DrawItemState.Selected) != 0;
        var hovered = state?.HoverIndex == e.Index;
        var background = selected ? AccentSurface : hovered ? PanelHover : Panel;
        using var backgroundBrush = new SolidBrush(background);
        e.Graphics.FillRectangle(backgroundBrush, e.Bounds);
        if (selected)
        {
            using var accent = new SolidBrush(Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height);
        }

        var textBounds = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 14), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            UiLocalization.T(list.GetItemText(list.Items[e.Index])),
            list.Font,
            textBounds,
            list.Enabled ? Text : DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0 && list.Focused)
        {
            ControlPaint.DrawFocusRectangle(
                e.Graphics,
                Rectangle.Inflate(e.Bounds, -2, -2),
                SystemInformation.HighContrast ? SystemColors.HighlightText : AccentLabel,
                background);
        }
    }

    private static void DrawTreeNode(object? sender, DrawTreeNodeEventArgs e)
    {
        if (sender is not TreeView tree || e.Node is null) return;
        StyledTreeViews.TryGetValue(tree, out var state);
        var selected = tree.SelectedNode == e.Node;
        var hovered = state?.HoverNode == e.Node;
        var progress = state?.ProgressFor(e.Node, selected, hovered)
            ?? new RowInteractionProgress(hovered ? 1f : 0f, selected ? 1f : 0f, 0f);
        var row = new Rectangle(0, e.Bounds.Top, tree.ClientSize.Width, e.Bounds.Height);
        var nodeBackground = e.Node.BackColor.IsEmpty
            ? Panel
            : e.Node.BackColor;
        var backgroundColor = AnimatedRowBackgroundColor(
            nodeBackground,
            progress.Hover,
            progress.Selection,
            progress.Pressed);
        using var background = new SolidBrush(backgroundColor);
        e.Graphics.FillRectangle(background, row);
        DrawRowSelectionAccent(e.Graphics, row, progress.Selection);

        var customExpandButtons = state?.UseCustomExpandButtons == true;
        var indent = Math.Max(16, tree.Indent);
        var contentLeft = customExpandButtons
            ? Math.Max(e.Bounds.Left, 6 + e.Node.Level * indent)
            : Math.Max(e.Bounds.Left, 8 + e.Node.Level * Math.Max(12, tree.Indent));
        if (e.Node.Nodes.Count > 0)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (customExpandButtons)
            {
                var buttonBounds = TreeExpandGlyphBounds(tree, e.Node);
                var expandHovered = state?.HoverExpandNode == e.Node;
                var expandPressed = state?.PressedExpandNode == e.Node;
                var buttonColor = expandPressed
                    ? AccentPressedSurface
                    : expandHovered
                        ? AccentHoverSurface
                        : selected
                            ? AccentPressedSurface
                            : PanelStrong;
                var borderColor = expandHovered || selected ? Accent : Border;
                using (var button = new SolidBrush(buttonColor))
                using (var border = new Pen(borderColor))
                {
                    e.Graphics.FillRectangle(button, buttonBounds);
                    e.Graphics.DrawRectangle(border, buttonBounds.X, buttonBounds.Y, buttonBounds.Width - 1, buttonBounds.Height - 1);
                }

                var centerX = buttonBounds.Left + buttonBounds.Width / 2f;
                var centerY = buttonBounds.Top + buttonBounds.Height / 2f;
                PointF[] chevron = e.Node.IsExpanded
                    ? [new(centerX - 4, centerY - 2), new(centerX, centerY + 2), new(centerX + 4, centerY - 2)]
                    : [new(centerX - 2, centerY - 4), new(centerX + 2, centerY), new(centerX - 2, centerY + 4)];
                using var glyph = new Pen(selected || expandHovered ? AccentLabel : Muted, 1.6f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round
                };
                e.Graphics.DrawLines(glyph, chevron);
                contentLeft = Math.Max(contentLeft, buttonBounds.Right + 6);
            }
            else
            {
                var centerX = Math.Max(8, contentLeft - 10);
                var centerY = e.Bounds.Top + e.Bounds.Height / 2f;
                PointF[] points = e.Node.IsExpanded
                    ? [new(centerX - 4, centerY - 2), new(centerX + 4, centerY - 2), new(centerX, centerY + 3)]
                    : [new(centerX - 2, centerY - 4), new(centerX - 2, centerY + 4), new(centerX + 3, centerY)];
                using var glyph = new SolidBrush(selected ? AccentLabel : Muted);
                e.Graphics.FillPolygon(glyph, points);
            }
        }

        var color = e.Node.ForeColor.IsEmpty ? tree.ForeColor : e.Node.ForeColor;
        if (!tree.Enabled) color = DisabledText;
        if (e.Node.Tag is ITreeNodeLeadingIconSource iconSource)
        {
            var iconSize = Math.Min(22, Math.Max(12, e.Bounds.Height - 4));
            var iconBounds = new Rectangle(
                contentLeft,
                e.Bounds.Top + Math.Max(0, (e.Bounds.Height - iconSize) / 2),
                iconSize,
                iconSize);
            var iconColor = selected ? AccentLabel : Mix(color, Muted, 0.36f);
            SvgIcons.Draw(e.Graphics, iconSource.LeadingIcon, iconBounds, iconColor);
            contentLeft = iconBounds.Right + 6;
        }
        var bounds = new Rectangle(contentLeft, e.Bounds.Top, Math.Max(0, tree.ClientSize.Width - contentLeft - 6), e.Bounds.Height);
        var markerColors = e.Node.Tag is ITreeNodeTrailingColorSource source
            ? source.TrailingColors
            : [];
        const int markerDiameter = 7;
        const int markerSpacing = 3;
        const int markerGap = 6;
        const int minimumTextWidth = 36;
        var availableMarkerWidth = Math.Max(0, bounds.Width - minimumTextWidth - markerGap);
        var markerCount = Math.Min(
            markerColors.Count,
            (availableMarkerWidth + markerSpacing) / (markerDiameter + markerSpacing));
        var markerWidth = markerCount > 0
            ? markerCount * markerDiameter + (markerCount - 1) * markerSpacing
            : 0;
        var textBounds = new Rectangle(
            bounds.Left,
            bounds.Top,
            Math.Max(0, bounds.Width - markerWidth - (markerCount > 0 ? markerGap : 0)),
            bounds.Height);
        var text = UiLocalization.T(e.Node.Text);
        TextRenderer.DrawText(
            e.Graphics,
            text,
            tree.Font,
            textBounds,
            color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (markerCount > 0)
        {
            var measuredTextWidth = TextRenderer.MeasureText(
                e.Graphics,
                text,
                tree.Font,
                new Size(int.MaxValue, bounds.Height),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
            var markerLeft = Math.Min(textBounds.Right, textBounds.Left + measuredTextWidth) + markerGap;
            markerLeft = Math.Min(markerLeft, bounds.Right - markerWidth);
            var markerTop = bounds.Top + Math.Max(0, (bounds.Height - markerDiameter) / 2);
            var markerBorderColor = SystemInformation.HighContrast
                ? SystemColors.WindowText
                : Mix(backgroundColor, Text, 0.42f);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var markerBorder = new Pen(markerBorderColor);
            for (var index = 0; index < markerCount; index++)
            {
                var markerBounds = new Rectangle(
                    markerLeft + index * (markerDiameter + markerSpacing),
                    markerTop,
                    markerDiameter,
                    markerDiameter);
                using var marker = new SolidBrush(markerColors[index]);
                e.Graphics.FillEllipse(marker, markerBounds);
                e.Graphics.DrawEllipse(markerBorder, markerBounds);
            }
        }
        if ((e.State & TreeNodeStates.Focused) != 0)
        {
            var focusBounds = new Rectangle(4, row.Top + 1, Math.Max(0, row.Width - 8), Math.Max(0, row.Height - 2));
            if (state?.UseSolidFocusCue == true && !SystemInformation.HighContrast)
            {
                using var focus = new Pen(Mix(backgroundColor, Accent, 0.72f));
                e.Graphics.DrawRectangle(
                    focus,
                    focusBounds.X,
                    focusBounds.Y,
                    Math.Max(0, focusBounds.Width - 1),
                    Math.Max(0, focusBounds.Height - 1));
            }
            else
            {
                ControlPaint.DrawFocusRectangle(e.Graphics, focusBounds, AccentLabel, background.Color);
            }
        }
    }

    internal static bool IsTreeExpandGlyphHit(TreeView tree, TreeNode node, Point location)
    {
        return node.Nodes.Count > 0 && TreeExpandGlyphBounds(tree, node).Contains(location);
    }

    internal static void RefreshTreeViewRowInteraction(TreeView tree)
    {
        if (StyledTreeViews.TryGetValue(tree, out var state)) state.RowsChanged();
    }

    internal static void RefreshListViewRowInteraction(ListView list)
    {
        if (StyledListViews.TryGetValue(list, out var state)) state.RowsChanged();
    }

    private static Rectangle TreeExpandGlyphBounds(TreeView tree, TreeNode node)
    {
        const int buttonSize = 16;
        var indent = Math.Max(16, tree.Indent);
        var x = 4 + node.Level * indent;
        var y = node.Bounds.Top + Math.Max(0, (node.Bounds.Height - buttonSize) / 2);
        return new Rectangle(x, y, buttonSize, buttonSize);
    }

    private static TreeNode? TreeNodeAtRow(TreeView tree, int y)
    {
        for (var node = tree.TopNode; node is not null; node = node.NextVisibleNode)
        {
            if (node.Bounds.Top > y) return null;
            if (y >= node.Bounds.Top && y < node.Bounds.Bottom) return node;
        }

        return null;
    }

    private static void DrawListViewHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var background = new SolidBrush(PanelStrong);
        using var border = new Pen(Border);
        e.Graphics.FillRectangle(background, e.Bounds);
        e.Graphics.DrawLine(border, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
        e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        var bounds = Rectangle.Inflate(e.Bounds, -8, 0);
        TextRenderer.DrawText(
            e.Graphics,
            UiLocalization.T(e.Header?.Text ?? string.Empty),
            e.Font ?? SystemFonts.MessageBoxFont,
            bounds,
            Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static void DrawListViewItem(object? sender, DrawListViewItemEventArgs e)
    {
        if (sender is not ListView list || e.Item is null) return;
        var item = e.Item;
        StyledListViews.TryGetValue(list, out var state);
        var selected = item.Selected;
        var hovered = ReferenceEquals(state?.HoverItem, item);
        var progress = state?.ProgressFor(item, selected, hovered)
            ?? new RowInteractionProgress(hovered ? 1f : 0f, selected ? 1f : 0f, 0f);
        var itemBackground = item.BackColor is { IsEmpty: false } customBackground
            ? customBackground
            : Panel;
        var backgroundColor = AnimatedRowBackgroundColor(
            itemBackground,
            progress.Hover,
            progress.Selection,
            progress.Pressed);
        using var background = new SolidBrush(backgroundColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        DrawRowSelectionAccent(e.Graphics, e.Bounds, progress.Selection);
    }

    private static void DrawListViewSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (sender is not ListView list || e.Item is null) return;
        var item = e.Item;
        StyledListViews.TryGetValue(list, out var state);
        var selected = item.Selected;
        var hovered = ReferenceEquals(state?.HoverItem, item);
        var progress = state?.ProgressFor(item, selected, hovered)
            ?? new RowInteractionProgress(hovered ? 1f : 0f, selected ? 1f : 0f, 0f);
        var itemBackground = item.BackColor is { IsEmpty: false } customBackground
            ? customBackground
            : Panel;
        var backgroundColor = AnimatedRowBackgroundColor(
            itemBackground,
            progress.Hover,
            progress.Selection,
            progress.Pressed);
        using var background = new SolidBrush(backgroundColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        if (e.ColumnIndex == 0) DrawRowSelectionAccent(e.Graphics, e.Bounds, progress.Selection);

        var bounds = new Rectangle(e.Bounds.Left + 8, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            UiLocalization.T(e.SubItem?.Text ?? string.Empty),
            list.Font,
            bounds,
            list.Enabled ? Text : DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (e.ColumnIndex == list.Columns.Count - 1 && item.Focused && list.Focused)
        {
            var focusBounds = Rectangle.Inflate(item.Bounds, -4, -2);
            var graphicsState = e.Graphics.Save();
            e.Graphics.SetClip(list.ClientRectangle, System.Drawing.Drawing2D.CombineMode.Replace);
            ControlPaint.DrawFocusRectangle(e.Graphics, focusBounds, AccentLabel, backgroundColor);
            e.Graphics.Restore(graphicsState);
        }
    }

    private static void DrawRowSelectionAccent(Graphics graphics, Rectangle row, float selectionProgress)
    {
        selectionProgress = Math.Clamp(selectionProgress, 0f, 1f);
        if (selectionProgress <= 0.001f || row.Width <= 0 || row.Height <= 0) return;
        var height = Math.Clamp((int)MathF.Ceiling(row.Height * selectionProgress), 1, row.Height);
        var top = row.Top + (row.Height - height) / 2;
        var alpha = Math.Clamp((int)MathF.Round(Accent.A * selectionProgress), 1, 255);
        using var accent = new SolidBrush(Color.FromArgb(alpha, Accent));
        graphics.FillRectangle(accent, row.Left, top, Math.Min(3, row.Width), height);
    }

    private sealed class FieldInteractionState
    {
        private readonly Control _control;
        private bool _hovered;

        public FieldInteractionState(Control control)
        {
            _control = control;
            control.MouseEnter += (_, _) =>
            {
                _hovered = true;
                Refresh();
            };
            control.MouseLeave += (_, _) =>
            {
                _hovered = false;
                Refresh();
            };
            control.Enter += (_, _) => Refresh();
            control.Leave += (_, _) => Refresh();
            control.EnabledChanged += (_, _) => Refresh();
        }

        private void Refresh()
        {
            var color = !_control.Enabled ? DisabledSurface : _control.ContainsFocus ? FieldFocus : _hovered ? FieldHover : Field;
            if (_control.BackColor != color) _control.BackColor = color;
            if (_control.ForeColor != (_control.Enabled ? Text : DisabledText)) _control.ForeColor = _control.Enabled ? Text : DisabledText;
            _control.Invalidate();
        }
    }

    private sealed class ListBoxInteractionState
    {
        private readonly ListBox _list;

        public ListBoxInteractionState(ListBox list)
        {
            _list = list;
            list.MouseMove += (_, e) => SetHover(list.IndexFromPoint(e.Location));
            list.MouseLeave += (_, _) => SetHover(-1);
        }

        public int HoverIndex { get; private set; } = -1;

        private void SetHover(int index)
        {
            if (HoverIndex == index) return;
            var previous = HoverIndex;
            HoverIndex = index;
            InvalidateItem(previous);
            InvalidateItem(HoverIndex);
        }

        private void InvalidateItem(int index)
        {
            if (index >= 0 && index < _list.Items.Count) _list.Invalidate(_list.GetItemRectangle(index));
        }
    }

    private sealed class TreeViewInteractionState
    {
        private readonly TreeView _tree;
        private readonly RowMotionTimeline<TreeNode> _rowMotion;
        private TreeNode? _selectedNode;
        private TreeNode? _pressedRowNode;
        private Point _lastPointerLocation;
        private bool _pointerInside;
        private bool _hoverReconcileQueued;

        public TreeViewInteractionState(
            TreeView tree,
            bool useCustomExpandButtons,
            bool animateRows,
            bool useSolidFocusCue)
        {
            _tree = tree;
            UseCustomExpandButtons = useCustomExpandButtons;
            UseSolidFocusCue = useSolidFocusCue;
            _selectedNode = tree.SelectedNode;
            _rowMotion = new RowMotionTimeline<TreeNode>(tree, InvalidateNode, animateRows);
            if (_selectedNode is not null)
            {
                _rowMotion.Set(_selectedNode, RowMotionChannel.Selection, active: true, immediate: true);
            }
            tree.MouseMove += (_, e) => UpdateHoverFromPointer(e.Location);
            tree.MouseLeave += (_, _) =>
            {
                _pointerInside = false;
                ReconcileHover();
            };
            tree.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || e.Clicks != 1) return;
                var node = TreeNodeAtRow(tree, e.Y);
                SetPressedRow(node);
                if (!UseCustomExpandButtons) return;
                if (node is null || !IsTreeExpandGlyphHit(tree, node, e.Location)) return;
                PressedExpandNode = node;
                InvalidateNode(node);
                if (node.IsExpanded) node.Collapse(ignoreChildren: true);
                else node.Expand();
            };
            tree.MouseUp += (_, _) => ClearPressedNodes();
            tree.MouseCaptureChanged += (_, _) => ClearPressedNodes();
            tree.AfterSelect += (_, e) =>
            {
                SetSelectedNode(e.Node);
                QueueHoverReconciliation();
            };
            tree.MouseWheel += (_, _) => QueueHoverReconciliation();
            tree.KeyDown += (_, _) => QueueHoverReconciliation();
            tree.AfterExpand += (_, _) => QueueHoverReconciliation();
            tree.AfterCollapse += (_, _) => QueueHoverReconciliation();
            tree.SizeChanged += (_, _) => ReconcileHover();
        }

        public TreeNode? HoverNode { get; private set; }
        public TreeNode? HoverExpandNode { get; private set; }
        public TreeNode? PressedExpandNode { get; private set; }
        public bool UseCustomExpandButtons { get; }
        public bool UseSolidFocusCue { get; set; }

        public void ConfigureRowMotion(bool animateRows)
        {
            _rowMotion.Configure(animateRows);
            if (_selectedNode is not null)
            {
                _rowMotion.Set(_selectedNode, RowMotionChannel.Selection, active: true, immediate: true);
            }
        }

        public RowInteractionProgress ProgressFor(TreeNode node, bool selected, bool hovered)
        {
            return _rowMotion.ProgressFor(
                node,
                hovered,
                selected,
                ReferenceEquals(_pressedRowNode, node));
        }

        public void RowsChanged()
        {
            SetSelectedNode(_tree.SelectedNode);
            if (_pressedRowNode is not null && !ReferenceEquals(_pressedRowNode.TreeView, _tree))
            {
                SetPressedRow(null);
            }
            if (PressedExpandNode is not null && !ReferenceEquals(PressedExpandNode.TreeView, _tree))
            {
                PressedExpandNode = null;
            }
            ReconcileHover();
        }

        private void UpdateHoverFromPointer(Point location)
        {
            _lastPointerLocation = location;
            _pointerInside = _tree.ClientRectangle.Contains(location);
            ReconcileHover();
        }

        private void ReconcileHover()
        {
            var node = _pointerInside
                ? TreeNodeAtRow(_tree, _lastPointerLocation.Y)
                : null;
            SetHover(node, _lastPointerLocation);
        }

        private void QueueHoverReconciliation()
        {
            if (_hoverReconcileQueued || _tree.IsDisposed || !_tree.IsHandleCreated) return;
            _hoverReconcileQueued = true;
            try
            {
                _tree.BeginInvoke((Action)(() =>
                {
                    _hoverReconcileQueued = false;
                    if (!_tree.IsDisposed) ReconcileHover();
                }));
            }
            catch (InvalidOperationException)
            {
                _hoverReconcileQueued = false;
            }
        }

        private void SetHover(TreeNode? node, Point location)
        {
            var expandNode = UseCustomExpandButtons
                && node is not null
                && IsTreeExpandGlyphHit(_tree, node, location)
                    ? node
                    : null;
            if (HoverNode == node && HoverExpandNode == expandNode) return;
            var previousNode = HoverNode;
            var previousExpandNode = HoverExpandNode;
            HoverNode = node;
            HoverExpandNode = expandNode;
            if (previousNode is not null)
            {
                _rowMotion.Set(previousNode, RowMotionChannel.Hover, active: false);
            }
            if (HoverNode is not null)
            {
                _rowMotion.Set(HoverNode, RowMotionChannel.Hover, active: true);
            }
            if (ReferenceEquals(previousNode, HoverNode)
                && !ReferenceEquals(previousExpandNode, HoverExpandNode)
                && HoverNode is not null)
            {
                InvalidateNode(HoverNode);
            }
        }

        private void SetSelectedNode(TreeNode? node)
        {
            if (ReferenceEquals(_selectedNode, node)) return;
            var previous = _selectedNode;
            _selectedNode = node;
            if (previous is not null)
            {
                _rowMotion.Set(previous, RowMotionChannel.Selection, active: false);
            }
            if (_selectedNode is not null)
            {
                _rowMotion.Set(_selectedNode, RowMotionChannel.Selection, active: true);
            }
        }

        private void SetPressedRow(TreeNode? node)
        {
            if (ReferenceEquals(_pressedRowNode, node)) return;
            var previous = _pressedRowNode;
            _pressedRowNode = node;
            if (previous is not null)
            {
                _rowMotion.Set(previous, RowMotionChannel.Pressed, active: false);
            }
            if (_pressedRowNode is not null)
            {
                _rowMotion.Set(_pressedRowNode, RowMotionChannel.Pressed, active: true);
            }
        }

        private void ClearPressedNodes()
        {
            SetPressedRow(null);
            var previousExpandNode = PressedExpandNode;
            PressedExpandNode = null;
            if (previousExpandNode is not null) InvalidateNode(previousExpandNode);
        }

        private void InvalidateNode(TreeNode node)
        {
            if (_tree.IsDisposed || !ReferenceEquals(node.TreeView, _tree)) return;
            var bounds = node.Bounds;
            if (bounds.Height <= 0) return;
            _tree.Invalidate(new Rectangle(0, bounds.Top, _tree.ClientSize.Width, bounds.Height));
        }
    }

    private sealed class ListViewInteractionState
    {
        private readonly ListView _list;
        private readonly ImageList? _rowHeightImages;
        private readonly RowMotionTimeline<ListViewItem> _rowMotion;
        private ListViewItem? _selectedItem;
        private ListViewItem? _pressedItem;
        private Point _lastPointerLocation;
        private bool _pointerInside;
        private bool _hoverReconcileQueued;

        public ListViewInteractionState(ListView list, bool animateRows)
        {
            _list = list;
            if (list.SmallImageList is null)
            {
                _rowHeightImages = new ImageList
                {
                    ColorDepth = ColorDepth.Depth32Bit,
                    ImageSize = new Size(1, 28)
                };
                list.SmallImageList = _rowHeightImages;
            }
            _selectedItem = SelectedItem();
            _rowMotion = new RowMotionTimeline<ListViewItem>(list, InvalidateItem, animateRows);
            if (_selectedItem is not null)
            {
                _rowMotion.Set(_selectedItem, RowMotionChannel.Selection, active: true, immediate: true);
            }
            list.MouseMove += (_, e) => UpdateHoverFromPointer(e.Location);
            list.MouseLeave += (_, _) =>
            {
                _pointerInside = false;
                ReconcileHover();
            };
            list.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left && e.Clicks == 1)
                {
                    SetPressed(list.GetItemAt(e.X, e.Y));
                }
            };
            list.MouseUp += (_, _) => SetPressed(null);
            list.MouseCaptureChanged += (_, _) => SetPressed(null);
            list.SelectedIndexChanged += (_, _) => SetSelected(SelectedItem());
            list.MouseWheel += (_, _) => QueueHoverReconciliation();
            list.KeyDown += (_, _) => QueueHoverReconciliation();
            list.SizeChanged += (_, _) => ReconcileHover();
            list.Disposed += (_, _) => _rowHeightImages?.Dispose();
        }

        public ListViewItem? HoverItem { get; private set; }

        public void ConfigureRowMotion(bool animateRows)
        {
            _rowMotion.Configure(animateRows);
            if (_selectedItem is not null)
            {
                _rowMotion.Set(_selectedItem, RowMotionChannel.Selection, active: true, immediate: true);
            }
        }

        public RowInteractionProgress ProgressFor(ListViewItem item, bool selected, bool hovered)
        {
            return _rowMotion.ProgressFor(item, hovered, selected, ReferenceEquals(_pressedItem, item));
        }

        public void RowsChanged()
        {
            SetSelected(SelectedItem());
            if (_pressedItem is not null && !ReferenceEquals(_pressedItem.ListView, _list)) SetPressed(null);
            ReconcileHover();
        }

        private void UpdateHoverFromPointer(Point location)
        {
            _lastPointerLocation = location;
            _pointerInside = _list.ClientRectangle.Contains(location);
            ReconcileHover();
        }

        private void ReconcileHover()
        {
            SetHover(_pointerInside ? _list.GetItemAt(_lastPointerLocation.X, _lastPointerLocation.Y) : null);
        }

        private void QueueHoverReconciliation()
        {
            if (_hoverReconcileQueued || _list.IsDisposed || !_list.IsHandleCreated) return;
            _hoverReconcileQueued = true;
            try
            {
                _list.BeginInvoke((Action)(() =>
                {
                    _hoverReconcileQueued = false;
                    if (!_list.IsDisposed) ReconcileHover();
                }));
            }
            catch (InvalidOperationException)
            {
                _hoverReconcileQueued = false;
            }
        }

        private void SetHover(ListViewItem? item)
        {
            if (ReferenceEquals(HoverItem, item)) return;
            var previous = HoverItem;
            HoverItem = item;
            if (previous is not null) _rowMotion.Set(previous, RowMotionChannel.Hover, active: false);
            if (HoverItem is not null) _rowMotion.Set(HoverItem, RowMotionChannel.Hover, active: true);
        }

        private void SetSelected(ListViewItem? item)
        {
            if (ReferenceEquals(_selectedItem, item)) return;
            var previous = _selectedItem;
            _selectedItem = item;
            if (previous is not null) _rowMotion.Set(previous, RowMotionChannel.Selection, active: false);
            if (_selectedItem is not null) _rowMotion.Set(_selectedItem, RowMotionChannel.Selection, active: true);
        }

        private void SetPressed(ListViewItem? item)
        {
            if (ReferenceEquals(_pressedItem, item)) return;
            var previous = _pressedItem;
            _pressedItem = item;
            if (previous is not null) _rowMotion.Set(previous, RowMotionChannel.Pressed, active: false);
            if (_pressedItem is not null) _rowMotion.Set(_pressedItem, RowMotionChannel.Pressed, active: true);
        }

        private ListViewItem? SelectedItem()
        {
            return _list.SelectedItems.Count > 0 ? _list.SelectedItems[0] : null;
        }

        private void InvalidateItem(ListViewItem item)
        {
            if (_list.IsDisposed || !ReferenceEquals(item.ListView, _list)) return;
            _list.Invalidate(item.Bounds);
        }
    }

    private enum RowMotionChannel
    {
        Hover,
        Selection,
        Pressed
    }

    private sealed class RowMotionTimeline<TKey> : IDisposable where TKey : notnull
    {
        private readonly Control _owner;
        private readonly Action<TKey> _invalidate;
        private readonly List<RowMotionEntry<TKey>> _entries = [];
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
        private bool _requested;
        private bool _enabled;
        private bool _disposed;

        public RowMotionTimeline(Control owner, Action<TKey> invalidate, bool enabled)
        {
            _owner = owner;
            _invalidate = invalidate;
            _timer.Tick += (_, _) => Tick();
            _owner.Disposed += (_, _) => Dispose();
            Configure(enabled);
        }

        public void Configure(bool enabled)
        {
            if (_disposed) return;
            _requested = enabled;
            var next = enabled && UiMotion.AnimationsEnabled;
            if (_enabled == next) return;
            _enabled = next;
            _timer.Stop();
            _entries.Clear();
            if (!_owner.IsDisposed) _owner.Invalidate();
        }

        public void Set(TKey key, RowMotionChannel channel, bool active, bool immediate = false)
        {
            if (_disposed) return;
            if (!_enabled)
            {
                _invalidate(key);
                return;
            }

            var entry = FindEntry(key) ?? AddEntry(key);
            var value = entry.ValueFor(channel);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (immediate) value.SetImmediate(active ? 1f : 0f);
            else value.Retarget(active ? 1f : 0f, now, DurationFor(channel));
            _invalidate(key);
            if (entry.IsAnimating && !_timer.Enabled) _timer.Start();
        }

        public RowInteractionProgress ProgressFor(TKey key, bool hovered, bool selected, bool pressed)
        {
            if (!_enabled)
            {
                return new RowInteractionProgress(
                    hovered ? 1f : 0f,
                    selected ? 1f : 0f,
                    _requested && pressed ? 1f : 0f);
            }

            var entry = FindEntry(key);
            if (entry is null)
            {
                return new RowInteractionProgress(hovered ? 1f : 0f, selected ? 1f : 0f, pressed ? 1f : 0f);
            }

            return new RowInteractionProgress(
                entry.Hover.ResolveCurrent(hovered),
                entry.Selection.ResolveCurrent(selected),
                entry.Pressed.ResolveCurrent(pressed));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            _entries.Clear();
        }

        private void Tick()
        {
            if (_disposed) return;
            if (_owner.IsDisposed)
            {
                Dispose();
                return;
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var anyAnimating = false;
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.Advance(now)) _invalidate(entry.Key);
                if (entry.IsAnimating) anyAnimating = true;
                else if (entry.IsDormant) _entries.RemoveAt(i);
            }

            if (!anyAnimating) _timer.Stop();
        }

        private RowMotionEntry<TKey>? FindEntry(TKey key)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (EqualityComparer<TKey>.Default.Equals(_entries[i].Key, key)) return _entries[i];
            }
            return null;
        }

        private RowMotionEntry<TKey> AddEntry(TKey key)
        {
            var entry = new RowMotionEntry<TKey>(key);
            _entries.Add(entry);
            return entry;
        }

        private static double DurationFor(RowMotionChannel channel)
        {
            return channel switch
            {
                RowMotionChannel.Hover => RowHoverDurationMs,
                RowMotionChannel.Selection => RowSelectionDurationMs,
                _ => RowPressedDurationMs
            };
        }
    }

    private sealed class RowMotionEntry<TKey>(TKey key) where TKey : notnull
    {
        public TKey Key { get; } = key;
        public AnimatedRowValue Hover { get; } = new();
        public AnimatedRowValue Selection { get; } = new();
        public AnimatedRowValue Pressed { get; } = new();
        public bool IsAnimating => Hover.IsAnimating || Selection.IsAnimating || Pressed.IsAnimating;
        public bool IsDormant => Hover.IsDormant && Selection.IsDormant && Pressed.IsDormant;

        public AnimatedRowValue ValueFor(RowMotionChannel channel)
        {
            return channel switch
            {
                RowMotionChannel.Hover => Hover,
                RowMotionChannel.Selection => Selection,
                _ => Pressed
            };
        }

        public bool Advance(long timestamp)
        {
            return Hover.Advance(timestamp) | Selection.Advance(timestamp) | Pressed.Advance(timestamp);
        }
    }

    private sealed class AnimatedRowValue
    {
        private float _start;
        private long _startedAt;
        private double _durationMs;

        public float Current { get; private set; }
        public float Target { get; private set; }
        public bool IsAnimating => Math.Abs(Current - Target) > 0.0001f;
        public bool IsDormant => !IsAnimating && Target <= 0.0001f;

        public void SetImmediate(float value)
        {
            Current = Math.Clamp(value, 0f, 1f);
            Target = Current;
            _start = Current;
            _durationMs = 0d;
        }

        public void Retarget(float target, long timestamp, double durationMs)
        {
            Advance(timestamp);
            target = Math.Clamp(target, 0f, 1f);
            if (Math.Abs(Target - target) <= 0.0001f) return;
            _start = Current;
            Target = target;
            _startedAt = timestamp;
            _durationMs = durationMs;
        }

        public bool Advance(long timestamp)
        {
            if (!IsAnimating) return false;
            var previous = Current;
            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt, timestamp).TotalMilliseconds;
            Current = AdvanceRowMotion(_start, Target, elapsedMs, _durationMs);
            if (Math.Abs(Current - Target) <= 0.0001f) Current = Target;
            return Math.Abs(Current - previous) > 0.0001f;
        }

        public float ResolveCurrent(bool active)
        {
            var expected = active ? 1f : 0f;
            return Math.Abs(Target - expected) <= 0.0001f ? Current : expected;
        }
    }

    private sealed class ModernStatusStripRenderer : ToolStripProfessionalRenderer
    {
        public ModernStatusStripRenderer()
            : base(new ModernStatusColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var border = new Pen(Border);
            e.Graphics.DrawLine(border, 0, 0, e.ToolStrip.Width, 0);
        }
    }

    private sealed class ModernStatusColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Top;
        public override Color ToolStripGradientMiddle => Top;
        public override Color ToolStripGradientEnd => Top;
        public override Color StatusStripGradientBegin => Top;
        public override Color StatusStripGradientEnd => Top;
        public override Color ToolStripBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => PanelStrong;
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);
}
