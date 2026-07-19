using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal enum DialogActionStyle
{
    Secondary,
    Primary,
    Danger
}

internal class ModernDialogForm : Form
{
    private const int MotionDurationMilliseconds = 170;
    private readonly System.Windows.Forms.Timer _motionTimer = new() { Interval = 15 };
    private readonly Stopwatch _motionWatch = new();
    private readonly Panel _header = new();
    private readonly Label _titleLabel = new();
    private readonly SvgIconButton _closeButton = new(SvgIconKind.Close);
    private bool _shown;
    private bool _closing;
    private bool _allowClose;
    private Point _settledLocation;
    private Point _motionOrigin;
    private DialogResult _pendingDialogResult = DialogResult.Cancel;

    protected ModernDialogForm(string title, Size clientSize)
    {
        Text = title;
        ClientSize = clientSize;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Border;
        DoubleBuffered = true;
        Font = Theme.UiFont();
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        Padding = new Padding(1);
        Opacity = 0;

        var surface = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        surface.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        surface.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        surface.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        surface.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        Controls.Add(surface);

        _header.Dock = DockStyle.Fill;
        _header.BackColor = Theme.Top;
        _header.Margin = Padding.Empty;
        _header.Padding = Padding.Empty;
        _header.Paint += (_, e) =>
        {
            using var border = new Pen(Theme.Border);
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.DrawLine(border, 0, _header.Height - 1, _header.Width, _header.Height - 1);
            e.Graphics.FillRectangle(accent, 0, 0, 3, _header.Height - 1);
        };
        surface.Controls.Add(_header, 0, 0);

        var headerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Top,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _header.Controls.Add(headerLayout);

        _titleLabel.Text = title;
        _titleLabel.Dock = DockStyle.Fill;
        _titleLabel.Padding = new Padding(16, 0, 0, 0);
        _titleLabel.ForeColor = Theme.Text;
        _titleLabel.BackColor = Theme.Top;
        _titleLabel.Font = Theme.UiFont(10.5f, FontStyle.Bold);
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _titleLabel.AutoEllipsis = true;
        _titleLabel.Margin = Padding.Empty;
        headerLayout.Controls.Add(_titleLabel, 0, 0);

        Theme.StyleButton(_closeButton);
        _closeButton.AccessibleName = "Close";
        _closeButton.Dock = DockStyle.Fill;
        _closeButton.Margin = Padding.Empty;
        _closeButton.Click += (_, _) => RequestDialogResult(DialogResult.Cancel);
        headerLayout.Controls.Add(_closeButton, 1, 0);

        DialogContent.Dock = DockStyle.Fill;
        DialogContent.BackColor = Theme.Panel;
        DialogContent.Margin = Padding.Empty;
        DialogContent.Padding = new Padding(20, 18, 20, 18);
        surface.Controls.Add(DialogContent, 0, 1);

        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelStrong,
            Margin = Padding.Empty,
            Padding = new Padding(20, 12, 20, 12)
        };
        footer.Paint += (_, e) =>
        {
            using var border = new Pen(Theme.Border);
            e.Graphics.DrawLine(border, 0, 0, footer.Width, 0);
        };
        surface.Controls.Add(footer, 0, 2);

        DialogActions.Dock = DockStyle.Fill;
        DialogActions.BackColor = Theme.PanelStrong;
        DialogActions.FlowDirection = FlowDirection.RightToLeft;
        DialogActions.WrapContents = false;
        DialogActions.Margin = Padding.Empty;
        DialogActions.Padding = Padding.Empty;
        footer.Controls.Add(DialogActions);

        _header.MouseDown += BeginTitleDrag;
        _titleLabel.MouseDown += BeginTitleDrag;
        _motionTimer.Tick += (_, _) => TickDialogMotion();
    }

    protected Panel DialogContent { get; } = new();
    protected FlowLayoutPanel DialogActions { get; } = new();
    internal bool IsDialogMotionRunning => _motionTimer.Enabled;
    internal bool IsDialogClosing => _closing;

    protected Button AddDialogAction(
        string text,
        DialogResult result,
        DialogActionStyle style = DialogActionStyle.Secondary,
        Func<bool>? canClose = null)
    {
        var button = new Button
        {
            Text = text,
            Width = 92,
            Height = 34,
            Margin = new Padding(8, 0, 0, 0),
            DialogResult = DialogResult.None
        };
        StyleDialogAction(button, style);
        button.Click += (_, _) =>
        {
            if (canClose?.Invoke() == false) return;
            RequestDialogResult(result);
        };
        DialogActions.Controls.Add(button);
        return button;
    }

    protected void RequestDialogResult(DialogResult result)
    {
        if (_closing) return;
        _pendingDialogResult = result;
        DialogResult = result;
        Close();
    }

    protected void SetDialogTitle(string title)
    {
        Text = title;
        _titleLabel.Text = title;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            const int windowCornerPreference = 33;
            var preference = 2;
            DwmSetWindowAttribute(Handle, windowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // Older Windows versions keep the square border and class shadow.
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _shown = true;
        _settledLocation = Location;
        _motionOrigin = new Point(Location.X, Location.Y + 12);
        Location = _motionOrigin;
        Opacity = 0;
        _motionWatch.Restart();
        _motionTimer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_allowClose
            || !_shown
            || !Visible
            || e.CloseReason is CloseReason.ApplicationExitCall or CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            base.OnFormClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        if (DialogResult != DialogResult.None) _pendingDialogResult = DialogResult;
        _motionOrigin = Location;
        _motionWatch.Restart();
        _motionTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _motionTimer.Dispose();
        base.Dispose(disposing);
    }

    private void TickDialogMotion()
    {
        if (IsDisposed)
        {
            _motionTimer.Stop();
            return;
        }

        var progress = Math.Clamp(
            _motionWatch.Elapsed.TotalMilliseconds / MotionDurationMilliseconds,
            0,
            1);
        if (_closing)
        {
            var eased = progress * progress;
            Opacity = Math.Max(0, 1 - eased);
            Location = new Point(_motionOrigin.X, _motionOrigin.Y + (int)Math.Round(8 * eased));
            if (progress < 1) return;

            _motionTimer.Stop();
            _allowClose = true;
            DialogResult = _pendingDialogResult;
            Close();
            return;
        }

        var openingEase = 1 - Math.Pow(1 - progress, 3);
        Opacity = openingEase;
        Location = new Point(
            _settledLocation.X,
            _motionOrigin.Y + (int)Math.Round((_settledLocation.Y - _motionOrigin.Y) * openingEase));
        if (progress < 1) return;

        Opacity = 1;
        Location = _settledLocation;
        _motionTimer.Stop();
    }

    private static void StyleDialogAction(Button button, DialogActionStyle style)
    {
        if (style == DialogActionStyle.Primary)
        {
            Theme.StyleActiveButton(button);
            return;
        }

        Theme.StyleButton(button);
        if (style != DialogActionStyle.Danger) return;
        button.ForeColor = Color.FromArgb(255, 226, 226);
        button.FlatAppearance.BorderColor = Theme.Danger;
        UiMotion.ConfigureButton(
            button,
            Color.FromArgb(72, 42, 44),
            Color.FromArgb(98, 48, 51),
            Color.FromArgb(58, 34, 36),
            active: false);
    }

    private void BeginTitleDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _closing) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}

internal sealed class ModernMessageDialog : ModernDialogForm
{
    private readonly Label _message = new();
    private readonly Panel _glyph = new();
    private readonly SvgIconKind _glyphKind;
    private readonly Color _glyphColor;

    private ModernMessageDialog(
        string message,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton)
        : base(caption, ResolveSize(message))
    {
        AccessibleName = caption;
        (_glyphKind, _glyphColor) = ResolveGlyph(icon);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        DialogContent.Controls.Add(layout);

        _glyph.Dock = DockStyle.Top;
        _glyph.Height = 44;
        _glyph.Margin = Padding.Empty;
        _glyph.BackColor = Theme.Panel;
        _glyph.Paint += (_, e) => SvgIcons.Draw(e.Graphics, _glyphKind, new Rectangle(0, 0, 44, 44), _glyphColor);
        layout.Controls.Add(_glyph, 0, 0);

        _message.Text = message;
        _message.Dock = DockStyle.Fill;
        _message.ForeColor = Theme.Text;
        _message.BackColor = Theme.Panel;
        _message.Font = Theme.UiFont(9.7f);
        _message.TextAlign = ContentAlignment.MiddleLeft;
        _message.AutoEllipsis = false;
        _message.Padding = new Padding(4, 0, 0, 0);
        layout.Controls.Add(_message, 1, 0);

        ConfigureButtons(buttons, icon, defaultButton);
        UiLocalization.Watch(this);
    }

    public static DialogResult Show(
        IWin32Window? owner,
        string message,
        string caption,
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        using var dialog = new ModernMessageDialog(message, caption, buttons, icon, defaultButton);
        return owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }

    private void ConfigureButtons(
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton)
    {
        var definitions = buttons switch
        {
            MessageBoxButtons.OKCancel => new[] { ("OK", DialogResult.OK), ("Cancel", DialogResult.Cancel) },
            MessageBoxButtons.YesNo => new[] { ("Yes", DialogResult.Yes), ("No", DialogResult.No) },
            MessageBoxButtons.YesNoCancel => new[] { ("Yes", DialogResult.Yes), ("No", DialogResult.No), ("Cancel", DialogResult.Cancel) },
            MessageBoxButtons.RetryCancel => new[] { ("Retry", DialogResult.Retry), ("Cancel", DialogResult.Cancel) },
            MessageBoxButtons.AbortRetryIgnore => new[] { ("Abort", DialogResult.Abort), ("Retry", DialogResult.Retry), ("Ignore", DialogResult.Ignore) },
            _ => new[] { ("OK", DialogResult.OK) }
        };
        var requestedDefaultIndex = defaultButton switch
        {
            MessageBoxDefaultButton.Button2 => 1,
            MessageBoxDefaultButton.Button3 => 2,
            _ => 0
        };
        var defaultIndex = Math.Clamp(requestedDefaultIndex, 0, definitions.Length - 1);
        Button? defaultAction = null;
        Button? cancelAction = null;
        for (var index = definitions.Length - 1; index >= 0; index--)
        {
            var definition = definitions[index];
            var destructive = icon == MessageBoxIcon.Warning
                && definition.Item2 is DialogResult.Yes or DialogResult.Abort;
            var style = destructive
                ? DialogActionStyle.Danger
                : index == defaultIndex ? DialogActionStyle.Primary : DialogActionStyle.Secondary;
            var action = AddDialogAction(definition.Item1, definition.Item2, style);
            if (index == defaultIndex) defaultAction = action;
            if (definition.Item2 is DialogResult.Cancel or DialogResult.No or DialogResult.OK) cancelAction ??= action;
        }
        AcceptButton = defaultAction;
        CancelButton = cancelAction;
    }

    private static Size ResolveSize(string message)
    {
        var measured = TextRenderer.MeasureText(
            message,
            Theme.UiFont(9.7f),
            new Size(430, 1000),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return new Size(520, Math.Clamp(154 + measured.Height, 220, 390));
    }

    private static (SvgIconKind Kind, Color Color) ResolveGlyph(MessageBoxIcon icon)
    {
        return icon switch
        {
            MessageBoxIcon.Warning => (SvgIconKind.Warning, Theme.Warning),
            MessageBoxIcon.Error => (SvgIconKind.Error, Theme.Danger),
            MessageBoxIcon.Question => (SvgIconKind.Question, Theme.Accent),
            _ => (SvgIconKind.Info, Theme.Accent)
        };
    }
}
