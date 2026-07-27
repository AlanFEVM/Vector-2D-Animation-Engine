using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class StartupBannerForm : Form
{
    private const int MinimumVisibleMilliseconds = 520;
    private readonly Stopwatch _visibleFor = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _dismissTimer = new();

    public StartupBannerForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = false;
        BackColor = Theme.IsLight ? Theme.App : Color.FromArgb(15, 18, 21);
        ClientSize = new Size(520, 228);
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
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

        using var border = new Pen(Theme.IsLight ? Theme.BorderHover : Color.FromArgb(74, 91, 106, 110));
        graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        using var accent = new SolidBrush(Theme.Accent);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Text);
        using var accentLine = new Pen(Color.FromArgb(180, Theme.Accent), 2f);
        using var dimLine = new Pen(Color.FromArgb(92, Theme.Muted), 1f);

        graphics.FillRectangle(accent, 32, 40, 8, 86);
        graphics.DrawString("V2", Theme.UiFont(24, FontStyle.Bold), text, 58, 42);
        graphics.DrawString("Vector 2D Animation Engine", Theme.UiFont(13, FontStyle.Bold), text, 60, 86);
        graphics.DrawString(UiLocalization.T("Starting workspace"), Theme.UiFont(9), muted, 61, 116);

        var lineY = 176;
        graphics.DrawLine(dimLine, 32, lineY, ClientSize.Width - 32, lineY);
        var ticks = new[] { 32, 94, 156, 218, 280, 342, 404, 466 };
        foreach (var x in ticks) graphics.DrawLine(dimLine, x, lineY - 5, x, lineY + 5);
        graphics.DrawLine(accentLine, 32, lineY, 244, lineY);
        graphics.FillEllipse(accent, 237, lineY - 6, 12, 12);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dismissTimer.Dispose();
        base.Dispose(disposing);
    }
}
