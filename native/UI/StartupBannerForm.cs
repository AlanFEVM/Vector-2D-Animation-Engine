using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace VectorAnimationEngine;

internal sealed class StartupBannerForm : Form
{
    private const int MinimumVisibleMilliseconds = 520;
    private const int BannerWidth = 620;
    private const int BannerHeight = 320;
    // Summarized from docs/USER_GUIDE.md; keep keyboard focus and workspace qualifiers.
    private static readonly StartupTip[] Tips =
    [
        // User Guide §3: workspaces and panels.
        new("Use Ctrl+1 / 2 / 3 with the top-row number keys to switch Basic Drawing, Scene & Animation, and Shots & Directing.", "用键盘上方数字键按 Ctrl+1 / 2 / 3，切换基础绘制、场景与动画、镜头与导演。"),
        new("Press F1 to toggle the Inspector or F2 to toggle the Timeline.", "按 F1 显示或隐藏检查器，按 F2 显示或隐藏时间轴。"),
        // User Guide §4–6: view navigation, drawing and selection.
        new("Hold Space and drag with the left mouse button to pan the 2D stage.", "按住 Space 再用鼠标左键拖动，可临时平移 2D 舞台。"),
        new("Press Esc while drawing to cancel an operation that has not been committed.", "绘制时按 Esc，可取消尚未提交的操作。"),
        new("With the drawing or scene stage focused, Ctrl+A selects visible, unlocked content on the current frame.", "绘制或场景舞台聚焦后按 Ctrl+A，选择当前帧可见且未锁定的内容。"),
        new("While drawing a shape, hold Shift to keep proportions or Ctrl to draw from the center.", "拖动绘制基础形状时，Shift 保持等比，Ctrl 从中心绘制。"),
        new("With Pencil, Brush, or Eraser active, press [ / ] to adjust width.", "使用铅笔、笔刷或橡皮擦时，按 [ / ] 调整宽度。"),
        new("With the Text tool, click to create or double-click to edit; Ctrl+Enter commits and Esc cancels.", "文本工具单击创建、双击编辑；Ctrl+Enter 提交，Esc 取消。"),
        // User Guide §9: timeline keyboard commands.
        new("In the Timeline, F6 copies held content into a keyframe; F7 inserts a blank keyframe.", "时间轴中按 F6 复制保持内容并插入关键帧，按 F7 插入空白关键帧。"),
        new("In the Timeline, press Enter to play or pause; use , and . for the previous or next frame.", "时间轴中按 Enter 播放或暂停，按逗号和句点切换上一帧或下一帧。"),
        // User Guide §10: Vault and hierarchy navigation.
        new("In Vault, search symbols by name, type, description, or assigned tag.", "在 Vault 中，可按元件名称、类型、说明或已分配的标签搜索。"),
        new("Drag a Vault symbol into Scene & Animation for a scene instance, or into Basic Drawing for a nested instance.", "将 Vault 元件拖到场景与动画，创建场景实例；拖到基础绘制，创建嵌套实例。"),
        new("Double-click an object in Hierarchy to focus it, or a layer to frame its visible content on the current frame.", "在 Hierarchy 中双击对象可聚焦；双击图层可聚焦该层当前帧的可见内容。"),
        // User Guide §12: saving projects.
        new("Press Ctrl+S to save. On the first save, choose a dedicated project folder.", "按 Ctrl+S 保存工程；首次保存请选择专用工程文件夹。")
    ];
    private readonly Stopwatch _visibleFor = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _dismissTimer = new();
    private readonly StartupTip _tip;
    private readonly string _versionText;

    public StartupBannerForm()
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = false;
        BackColor = Theme.App;
        ClientSize = new Size(BannerWidth, BannerHeight);
        DoubleBuffered = true;
        ResizeRedraw = true;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AccessibleName = UiLocalization.CurrentLanguage == UiLanguage.SimplifiedChinese
            ? "Vector 2D Animation Engine 启动"
            : "Vector 2D Animation Engine startup";
        AccessibleRole = AccessibleRole.Window;
        _tip = Tips[Random.Shared.Next(Tips.Length)];
        _versionText = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            UiLocalization.T("Version {0}"),
            ApplicationVersion);
        AccessibleDescription = $"{_versionText}. {_tip.For(UiLocalization.CurrentLanguage)}";
        _dismissTimer.Tick += (_, _) =>
        {
            _dismissTimer.Stop();
            Close();
        };
    }

    public void DismissWhenReady()
    {
        if (IsDisposed || _dismissTimer.Enabled) return;
        var remaining = MinimumVisibleMilliseconds - (int)_visibleFor.ElapsedMilliseconds;
        if (remaining <= 0)
        {
            Close();
            return;
        }

        _dismissTimer.Interval = remaining;
        _dismissTimer.Start();
    }

    public void ShowAbove(Form owner)
    {
        if (IsDisposed) return;
        Owner = owner;
        TopMost = true;
        BringToFront();
        Activate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(BackColor);

        var background = Theme.EffectiveBackground(this, BackColor);
        using var border = new Pen(Theme.ReadableUiColor(background, Theme.BorderHover));
        graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96f);
        Rectangle BoundsAt(int x, int y, int width, int height) =>
            new(Scale(x), Scale(y), Scale(width), Scale(height));
        var textColor = Theme.ReadableText(background, Theme.Text);
        var accentColor = Theme.ReadableText(background, Theme.Accent);
        var mutedColor = Theme.ReadableText(background, Theme.Muted);
        using var accent = new SolidBrush(Theme.ReadableUiColor(background, Theme.Accent));
        using var accentLine = new Pen(accent.Color, Scale(2));
        using var dimLine = new Pen(Theme.ReadableUiColor(background, Theme.Muted), Scale(1));
        using var logoFont = CreateBannerFont(25, FontStyle.Bold);
        using var titleFont = CreateBannerFont(13, FontStyle.Bold);
        using var bodyFont = CreateBannerFont(9.5f);
        using var labelFont = CreateBannerFont(9, FontStyle.Bold);
        using var statusFont = CreateBannerFont(9);
        const TextFormatFlags singleLine = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding
            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

        graphics.FillRectangle(accent, BoundsAt(32, 34, 8, 112));
        TextRenderer.DrawText(graphics, "V2", logoFont, BoundsAt(62, 28, 120, 44), textColor, singleLine);
        TextRenderer.DrawText(graphics, "Vector 2D Animation Engine", titleFont,
            BoundsAt(62, 77, BannerWidth - 94, 26), textColor, singleLine);
        TextRenderer.DrawText(graphics, _versionText, bodyFont,
            BoundsAt(63, 107, BannerWidth - 95, 22), accentColor, singleLine);
        TextRenderer.DrawText(graphics, UiLocalization.T("Starting workspace"), statusFont,
            BoundsAt(63, 132, BannerWidth - 95, 22), mutedColor, singleLine);

        var tipBounds = new Rectangle(Scale(32), Scale(174), ClientSize.Width - Scale(64), Scale(102));
        using var tipSurface = new SolidBrush(Theme.PanelStrong);
        using var tipBorder = new Pen(Theme.ReadableUiColor(tipSurface.Color, Theme.Border), 1f);
        graphics.FillRectangle(tipSurface, tipBounds);
        graphics.DrawRectangle(tipBorder, tipBounds);
        var tipLabel = UiLocalization.CurrentLanguage == UiLanguage.SimplifiedChinese ? "操作技巧 · 用户指南" : "Tip · User Guide";
        TextRenderer.DrawText(graphics, tipLabel, labelFont,
            new Rectangle(tipBounds.X + Scale(16), tipBounds.Y + Scale(12), tipBounds.Width - Scale(32), Scale(22)),
            Theme.ReadableText(tipSurface.Color, Theme.Accent), singleLine);
        var tipText = _tip.For(UiLocalization.CurrentLanguage);
        TextRenderer.DrawText(graphics, tipText, bodyFont,
            new Rectangle(tipBounds.X + Scale(16), tipBounds.Y + Scale(37),
                tipBounds.Width - Scale(32), tipBounds.Height - Scale(49)),
            Theme.ReadableText(tipSurface.Color, Theme.Text),
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);

        var lineY = ClientSize.Height - Scale(24);
        var left = Scale(32);
        var right = ClientSize.Width - Scale(32);
        graphics.DrawLine(dimLine, left, lineY, right, lineY);
        for (var tick = 0; tick < 8; tick++)
        {
            var x = left + (right - left) * tick / 7;
            graphics.DrawLine(dimLine, x, lineY - Scale(5), x, lineY + Scale(5));
        }
        var markerX = left + (right - left) * 0.46f;
        graphics.DrawLine(accentLine, left, lineY, markerX, lineY);
        graphics.FillEllipse(accent, markerX - Scale(6), lineY - Scale(6), Scale(12), Scale(12));
    }

    private Font CreateBannerFont(float pointSize, FontStyle style = FontStyle.Regular)
    {
        // GDI text needs explicit pixel sizing to follow the same per-monitor DPI as the layout.
        using var themeFont = Theme.UiFont(pointSize, style);
        return new Font(themeFont.FontFamily, pointSize * DeviceDpi / 72f, style, GraphicsUnit.Pixel);
    }

    private static string ApplicationVersion
    {
        get
        {
            var informationalVersion = typeof(StartupBannerForm).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?.Split('+', 2)[0];
            return string.IsNullOrWhiteSpace(informationalVersion)
                ? typeof(StartupBannerForm).Assembly.GetName().Version?.ToString(3) ?? "unknown"
                : informationalVersion;
        }
    }

    private readonly record struct StartupTip(string English, string SimplifiedChinese)
    {
        public string For(UiLanguage language) =>
            language == UiLanguage.SimplifiedChinese ? SimplifiedChinese : English;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dismissTimer.Dispose();
        base.Dispose(disposing);
    }
}
