namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private static ToolStripControlHost CreateTimelineFrameStatus()
    {
        var readout = new TimelineFrameReadout();
        var host = new ToolStripControlHost(readout)
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 10, 0),
            Padding = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        };
        readout.SizeRequested += size =>
        {
            if (host.Size != size) host.Size = size;
        };
        return host;
    }

    private void UpdateTimelineFrameStatus()
    {
        var readout = (TimelineFrameReadout)_timelineFrameStatus.Control;
        var text = _timeline.FrameStatusText;
        readout.SetReadout(text, _timeline.FrameStatusSizingText);
        if (_timelineFrameStatus.Size != readout.Size) _timelineFrameStatus.Size = readout.Size;
        _timelineFrameStatus.AccessibleName = text;
    }

    private sealed class TimelineFrameReadout : Control
    {
        private string _readout = "";
        private string _sizingText = "";
        private (string Text, Font Font, int Dpi)? _sizeKey;
        private Size _preferredSize = new(1, 1);

        public TimelineFrameReadout()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            CausesValidation = false;
            AccessibleRole = AccessibleRole.StaticText;
            BackColor = Theme.Top;
            ForeColor = Theme.SecondaryText;
            Size = new Size(1, 1);
        }

        internal string Readout => _readout;

        internal event Action<Size>? SizeRequested;

        public override Size GetPreferredSize(Size proposedSize) => _preferredSize;

        internal void SetReadout(string text, string sizingText)
        {
            _sizingText = sizingText;
            var key = (sizingText, Font, DeviceDpi);
            if (_sizeKey != key)
            {
                var measured = TextRenderer.MeasureText(sizingText, Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                _preferredSize = new Size(Math.Max(1, measured.Width), Math.Max(1, measured.Height));
                _sizeKey = key;
                SizeRequested?.Invoke(_preferredSize);
                if (Size != _preferredSize) Size = _preferredSize;
            }
            if (_readout == text) return;
            _readout = text;
            AccessibleName = text;
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            _sizeKey = null;
            SetReadout(_readout, _sizingText);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            _sizeKey = null;
            SetReadout(_readout, _sizingText);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var background = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(background, e.ClipRectangle);
            TextRenderer.DrawText(e.Graphics, _readout, Font, ClientRectangle,
                Enabled ? ForeColor : Theme.DisabledText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.PreserveGraphicsClipping);
            base.OnPaint(e);
        }
    }
}
