using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

/// <summary>Visual crop editor. The frame stays still while the image pans/zooms beneath it.</summary>
internal sealed class ImageCropDialog : UserControl
{
    private readonly Bitmap _image;
    private readonly ImageFilterMode _filterMode;
    private readonly Size _sourceSize;
    private readonly CropCanvas _canvas = new() { Dock = DockStyle.Fill, TabStop = true, BackColor = Color.FromArgb(24, 24, 24) };
    private readonly ModernNumericUpDown[] _values = new ModernNumericUpDown[4];
    private readonly SegmentedButton[] _ratioButtons = new SegmentedButton[7];
    private int _ratioIndex;
    private readonly Label _sizeLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.DisabledText };
    private readonly TableLayoutPanel _layout = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
    private readonly TableLayoutPanel _fields = new() { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Visible = false };
    private readonly SvgIconButton _precision = new(SvgIconKind.ChevronDown);
    private readonly SvgIconButton _flipHorizontal = new(SvgIconKind.FlipHorizontal);
    private readonly SvgIconButton _flipVertical = new(SvgIconKind.FlipVertical);
    private readonly ToolTip _toolTip = new();
    private Rectangle _selection;
    private RectangleF _imageBounds;
    private RectangleF _frame;
    private RectangleF _startImage;
    private RectangleF _startFrame;
    private Rectangle _startSelection;
    private Point _anchor;
    private int _dragMode; // 0=idle, 1=pan, bitmask: left=2,right=4,top=8,bottom=16
    private bool _updating;
    private bool _preciseExpanded;
    private float AspectRatio => _ratioIndex switch { 1 => _sourceSize.Width / (float)_sourceSize.Height,
        2 => 1f, 3 => 4f / 3, 4 => 3f / 4, 5 => 16f / 9, 6 => 9f / 16, _ => 0f };

    public ImageCropDialog(Bitmap image, Size sourceSize, BitmapImageImportSettings settings, bool batch)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Panel;
        Font = Theme.UiFont();
        Dock = DockStyle.Fill;
        Padding = new Padding(20, 12, 20, 12);
        _image = image;
        _filterMode = settings.FilterMode;
        _sourceSize = sourceSize;
        _selection = settings.ResolveCropPixelBounds(sourceSize.Width, sourceSize.Height);
        if (_selection.IsEmpty) _selection = new Rectangle(Point.Empty, sourceSize);
        FlipHorizontal = settings.FlipHorizontal;
        FlipVertical = settings.FlipVertical;
        if (FlipHorizontal) _selection.X = _sourceSize.Width - _selection.Right;
        if (FlipVertical) _selection.Y = _sourceSize.Height - _selection.Bottom;
        AccessibleName = "Edit image";
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, batch ? 54 : 44));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        Controls.Add(_layout);
        _layout.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Theme.DisabledText, Text = batch
            ? "Drag the edges to crop. Drag the image to compose; scroll to zoom. Edits apply only to this image."
            : "Drag the edges to crop. Drag the image to compose; scroll to zoom." }, 0, 0);
        _canvas.AccessibleName = "Crop selection";
        _canvas.Paint += PaintCanvas;
        _canvas.Resize += (_, _) => { EndDrag(true); FitView(); };
        _canvas.MouseDown += BeginDrag;
        _canvas.MouseMove += ContinueDrag;
        _canvas.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) EndDrag(false); };
        _canvas.MouseCaptureChanged += (_, _) => { if (!_canvas.Capture) EndDrag(true); };
        _canvas.MouseWheel += (_, e) => { if (_dragMode == 0) Zoom((float)Math.Pow(1.12, e.Delta / 120d)); };
        _layout.Controls.Add(_canvas, 0, 1);
        var tools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 8, 0, 0) };
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        tools.RowCount = 2;
        tools.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        tools.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var ratios = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1 };
        string[] ratioNames = ["Free crop", "Original ratio", "1:1", "4:3", "3:4", "16:9", "9:16"];
        for (var i = 0; i < 7; i++)
        {
            ratios.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, i == 0 ? 20 : i == 1 ? 24 : 11.2f));
            var index = i;
            var button = new SegmentedButton { Dock = DockStyle.Fill, Text = ratioNames[i], AccessibleName = ratioNames[i], Margin = new Padding(1), CompactText = true };
            _ratioButtons[i] = button;
            Theme.StyleSegmentedButton(button, i == 0);
            button.Click += (_, _) => SetRatio(index);
            ratios.Controls.Add(button, i, 0);
        }
        tools.Controls.Add(ratios, 0, 0); tools.SetColumnSpan(ratios, 4);
        var zoomOut = new SvgIconButton(SvgIconKind.ZoomOut) { Dock = DockStyle.Fill, AccessibleName = "Zoom out" };
        var zoomIn = new SvgIconButton(SvgIconKind.ZoomIn) { Dock = DockStyle.Fill, AccessibleName = "Zoom in" };
        Theme.StyleToolbarButton(zoomOut); Theme.StyleToolbarButton(zoomIn);
        zoomOut.Click += (_, _) => Zoom(1 / 1.2f); zoomIn.Click += (_, _) => Zoom(1.2f);
        _flipHorizontal.AccessibleName = "Flip horizontally";
        _flipVertical.AccessibleName = "Flip vertically";
        _toolTip.SetToolTip(_flipHorizontal, UiLocalization.T("Flip horizontally"));
        _toolTip.SetToolTip(_flipVertical, UiLocalization.T("Flip vertically"));
        Theme.StyleToolbarButton(_flipHorizontal, FlipHorizontal);
        Theme.StyleToolbarButton(_flipVertical, FlipVertical);
        _flipHorizontal.Click += (_, _) => ToggleFlip(true);
        _flipVertical.Click += (_, _) => ToggleFlip(false);
        var flips = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        _flipHorizontal.Size = _flipVertical.Size = new Size(34, 30);
        flips.Controls.Add(_flipHorizontal);
        flips.Controls.Add(_flipVertical);
        tools.Controls.Add(flips, 0, 1);
        tools.Controls.Add(zoomOut, 1, 1); tools.Controls.Add(zoomIn, 2, 1); tools.Controls.Add(_sizeLabel, 3, 1);
        _layout.Controls.Add(tools, 0, 2);
        var secondary = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        secondary.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        secondary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); secondary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var reset = new Button { Text = "Restore full image", Dock = DockStyle.Fill, AccessibleName = "Restore full image" };
        Theme.StyleButton(reset);
        reset.Click += (_, _) => { EndDrag(true); SetRatio(0); SetSelection(new Rectangle(Point.Empty, _sourceSize)); };
        _precision.Text = "Precise crop"; _precision.AccessibleName = "Precise crop"; _precision.IsDisclosureHeader = true; _precision.Dock = DockStyle.Fill;
        Theme.StyleToolbarButton(_precision);
        _precision.Click += (_, _) =>
        {
            EndDrag(true); _preciseExpanded = !_preciseExpanded;
            _fields.Visible = _preciseExpanded; _layout.RowStyles[4].Height = _preciseExpanded ? 66 * DeviceDpi / 96f : 0;
            _precision.Icon = _preciseExpanded ? SvgIconKind.ChevronUp : SvgIconKind.ChevronDown;
        };
        secondary.Controls.Add(reset, 0, 0); secondary.Controls.Add(_precision, 1, 0); _layout.Controls.Add(secondary, 0, 3);
        string[] names = ["Crop left", "Crop top", "Crop width", "Crop height"];
        for (var i = 0; i < 4; i++)
        {
            _fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            _fields.Controls.Add(new Label { Text = names[i], Dock = DockStyle.Fill, ForeColor = Theme.Text, AutoEllipsis = true }, i, 0);
            _values[i] = new ModernNumericUpDown { Minimum = i < 2 ? 0 : 1, Maximum = i % 2 == 0 ? sourceSize.Width : sourceSize.Height,
                Dock = DockStyle.Fill, Suffix = "px", AccessibleName = names[i] };
            var index = i; _values[i].ValueChanged += (_, _) => UpdateFromFields(index); _fields.Controls.Add(_values[i], i, 1);
        }
        _layout.Controls.Add(_fields, 0, 4);
        SetSelection(_selection);
        UiLocalization.Watch(this);
    }

    public Rectangle Selection => new(FlipHorizontal ? _sourceSize.Width - _selection.Right : _selection.X,
        FlipVertical ? _sourceSize.Height - _selection.Bottom : _selection.Y, _selection.Width, _selection.Height);
    public bool FlipHorizontal { get; private set; }
    public bool FlipVertical { get; private set; }
    internal void CancelInteraction() => EndDrag(true);

    private void ToggleFlip(bool horizontal)
    {
        EndDrag(true);
        if (horizontal)
        {
            FlipHorizontal = !FlipHorizontal;
            _selection.X = _sourceSize.Width - _selection.Right;
            _imageBounds.X = _frame.Left + _frame.Right - _imageBounds.Right;
        }
        else
        {
            FlipVertical = !FlipVertical;
            _selection.Y = _sourceSize.Height - _selection.Bottom;
            _imageBounds.Y = _frame.Top + _frame.Bottom - _imageBounds.Bottom;
        }
        Theme.StyleToolbarButton(_flipHorizontal, FlipHorizontal);
        Theme.StyleToolbarButton(_flipVertical, FlipVertical);
        SyncFields();
        _canvas.Invalidate();
    }

    protected override void OnLeave(EventArgs e)
    {
        EndDrag(true);
        base.OnLeave(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }
    private RectangleF ImageBounds() => _imageBounds;
    private RectangleF SelectionBounds() => _frame;

    private void FitView()
    {
        if (_canvas.Width < 40 || _canvas.Height < 40) return;
        var padding = 28f * DeviceDpi / 96f;
        var scale = Math.Max(0.001f, Math.Min((_canvas.Width - padding * 2) / _sourceSize.Width, (_canvas.Height - padding * 2) / _sourceSize.Height));
        var width = _sourceSize.Width * scale; var height = _sourceSize.Height * scale;
        _imageBounds = new RectangleF((_canvas.Width - width) / 2, (_canvas.Height - height) / 2, width, height);
        _frame = new RectangleF(_imageBounds.X + _selection.X * scale, _imageBounds.Y + _selection.Y * scale, _selection.Width * scale, _selection.Height * scale);
        _canvas.Invalidate();
    }

    private int HitTest(Point point)
    {
        var tolerance = 12f * DeviceDpi / 96f;
        var expanded = RectangleF.Inflate(_frame, tolerance, tolerance);
        if (!expanded.Contains(point)) return 0;
        var mode = 0;
        if (Math.Abs(point.X - _frame.Left) <= tolerance) mode |= 2;
        else if (Math.Abs(point.X - _frame.Right) <= tolerance) mode |= 4;
        if (Math.Abs(point.Y - _frame.Top) <= tolerance) mode |= 8;
        else if (Math.Abs(point.Y - _frame.Bottom) <= tolerance) mode |= 16;
        return mode == 0 && _frame.Contains(point) ? 1 : mode;
    }

    private void BeginDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var mode = HitTest(e.Location); if (mode == 0) return;
        _canvas.Focus(); _anchor = e.Location; _startFrame = _frame; _startImage = _imageBounds; _startSelection = _selection;
        _dragMode = mode; _canvas.Capture = true;
    }

    private void ContinueDrag(object? sender, MouseEventArgs e)
    {
        if (_dragMode == 0)
        {
            _canvas.Cursor = HitTest(e.Location) switch { 1 => Cursors.Hand, 2 or 4 => Cursors.SizeWE, 8 or 16 => Cursors.SizeNS,
                10 or 20 => Cursors.SizeNWSE, 12 or 18 => Cursors.SizeNESW, _ => Cursors.Default };
            return;
        }
        var dx = e.X - _anchor.X; var dy = e.Y - _anchor.Y;
        if (_dragMode == 1)
        {
            _imageBounds = _startImage; _imageBounds.Offset(dx, dy); ClampImageToFrame();
        }
        else _frame = ResizeFrame(_startFrame, new PointF(dx, dy), _dragMode);
        UpdateSelectionFromView();
    }

    private RectangleF ResizeFrame(RectangleF start, PointF delta, int mode)
    {
        var bounds = RectangleF.Intersect(_imageBounds, new RectangleF(12, 12, Math.Max(1, _canvas.Width - 24), Math.Max(1, _canvas.Height - 24)));
        var min = Math.Min(28f * DeviceDpi / 96f, Math.Min(start.Width, start.Height));
        var left = (mode & 2) != 0 ? Math.Clamp(start.Left + delta.X, bounds.Left, start.Right - min) : start.Left;
        var right = (mode & 4) != 0 ? Math.Clamp(start.Right + delta.X, start.Left + min, bounds.Right) : start.Right;
        var top = (mode & 8) != 0 ? Math.Clamp(start.Top + delta.Y, bounds.Top, start.Bottom - min) : start.Top;
        var bottom = (mode & 16) != 0 ? Math.Clamp(start.Bottom + delta.Y, start.Top + min, bounds.Bottom) : start.Bottom;
        var ratio = AspectRatio;
        if (ratio <= 0) return RectangleF.FromLTRB(left, top, right, bottom);
        var horizontal = (mode & 6) != 0; var vertical = (mode & 24) != 0;
        var anchorX = horizontal ? (mode & 2) != 0 ? start.Right : start.Left : start.X + start.Width / 2;
        var anchorY = vertical ? (mode & 8) != 0 ? start.Bottom : start.Top : start.Y + start.Height / 2;
        var width = horizontal ? right - left : (bottom - top) * ratio;
        if (horizontal && vertical && Math.Abs(delta.Y) > Math.Abs(delta.X) / ratio) width = (bottom - top) * ratio;
        var maxWidth = horizontal ? (mode & 2) != 0 ? anchorX - bounds.Left : bounds.Right - anchorX
            : 2 * Math.Min(anchorX - bounds.Left, bounds.Right - anchorX);
        var maxHeight = vertical ? (mode & 8) != 0 ? anchorY - bounds.Top : bounds.Bottom - anchorY
            : 2 * Math.Min(anchorY - bounds.Top, bounds.Bottom - anchorY);
        width = Math.Clamp(width, Math.Min(min * Math.Max(1, ratio), Math.Min(maxWidth, maxHeight * ratio)), Math.Min(maxWidth, maxHeight * ratio));
        var height = width / ratio;
        return new RectangleF(horizontal ? (mode & 2) != 0 ? anchorX - width : anchorX : anchorX - width / 2,
            vertical ? (mode & 8) != 0 ? anchorY - height : anchorY : anchorY - height / 2, width, height);
    }

    private void ClampImageToFrame()
    {
        _imageBounds.X = Math.Clamp(_imageBounds.X, _frame.Right - _imageBounds.Width, _frame.Left);
        _imageBounds.Y = Math.Clamp(_imageBounds.Y, _frame.Bottom - _imageBounds.Height, _frame.Top);
    }

    private void Zoom(float factor)
    {
        if (_dragMode != 0 || _imageBounds.Width <= 0) return;
        var minimum = Math.Max(_frame.Width / _imageBounds.Width, _frame.Height / _imageBounds.Height);
        // Never zoom beyond roughly one source pixel per crop; zoom never adds source pixels.
        factor = Math.Clamp(factor, minimum, Math.Max(minimum, Math.Min(_frame.Width * _sourceSize.Width / _imageBounds.Width, _frame.Height * _sourceSize.Height / _imageBounds.Height)));
        var center = new PointF(_frame.X + _frame.Width / 2, _frame.Y + _frame.Height / 2);
        _imageBounds = new RectangleF(center.X + (_imageBounds.X - center.X) * factor, center.Y + (_imageBounds.Y - center.Y) * factor,
            _imageBounds.Width * factor, _imageBounds.Height * factor);
        ClampImageToFrame(); UpdateSelectionFromView();
    }

    private void UpdateSelectionFromView()
    {
        var scale = _sourceSize.Width / _imageBounds.Width;
        var left = Math.Clamp((int)MathF.Round((_frame.Left - _imageBounds.Left) * scale), 0, _sourceSize.Width - 1);
        var top = Math.Clamp((int)MathF.Round((_frame.Top - _imageBounds.Top) * scale), 0, _sourceSize.Height - 1);
        var right = Math.Clamp((int)MathF.Round((_frame.Right - _imageBounds.Left) * scale), left + 1, _sourceSize.Width);
        var bottom = Math.Clamp((int)MathF.Round((_frame.Bottom - _imageBounds.Top) * scale), top + 1, _sourceSize.Height);
        var selection = Rectangle.FromLTRB(left, top, right, bottom);
        if (_selection != selection) { _selection = selection; SyncFields(); }
        _canvas.Invalidate();
    }

    private void EndDrag(bool cancel)
    {
        if (_dragMode == 0) return;
        _dragMode = 0;
        if (cancel) { _imageBounds = _startImage; _frame = _startFrame; _selection = _startSelection; SyncFields(); _canvas.Invalidate(); }
        _canvas.Capture = false;
    }

    private void ApplyRatio()
    {
        EndDrag(true); var ratio = AspectRatio; if (ratio <= 0) return;
        // Preset ratios always reset to the full source image, never continue from the
        // current (possibly already cropped) selection.
        var width = Math.Min(_sourceSize.Width, _sourceSize.Height * ratio); var height = width / ratio;
        SetSelection(new Rectangle((int)MathF.Round((_sourceSize.Width - width) / 2),
            (int)MathF.Round((_sourceSize.Height - height) / 2), Math.Max(1, (int)MathF.Round(width)), Math.Max(1, (int)MathF.Round(height))));
    }

    private void SetRatio(int index)
    {
        _ratioIndex = index;
        for (var i = 0; i < _ratioButtons.Length; i++) Theme.StyleSegmentedButton(_ratioButtons[i], i == index);
        ApplyRatio();
    }

    private void UpdateFromFields(int index)
    {
        if (_updating) return;
        EndDrag(true); SetRatio(0);
        var r = _selection; var value = (int)_values[index].Value;
        if (index == 0) { r.X = Math.Min(value, _sourceSize.Width - 1); r.Width = Math.Min(r.Width, _sourceSize.Width - r.X); }
        if (index == 1) { r.Y = Math.Min(value, _sourceSize.Height - 1); r.Height = Math.Min(r.Height, _sourceSize.Height - r.Y); }
        if (index == 2) r.Width = Math.Min(value, _sourceSize.Width - r.X);
        if (index == 3) r.Height = Math.Min(value, _sourceSize.Height - r.Y);
        SetSelection(r);
    }

    private void SetSelection(Rectangle selection)
    {
        var x = Math.Clamp(selection.X, 0, _sourceSize.Width - 1); var y = Math.Clamp(selection.Y, 0, _sourceSize.Height - 1);
        _selection = new Rectangle(x, y, Math.Clamp(selection.Width, 1, _sourceSize.Width - x), Math.Clamp(selection.Height, 1, _sourceSize.Height - y));
        SyncFields(); FitView();
    }

    private void SyncFields()
    {
        _updating = true;
        try
        {
            int[] values = [_selection.X, _selection.Y, _selection.Width, _selection.Height];
            for (var i = 0; i < 4; i++)
                if (_values[i].Value != values[i]) _values[i].Value = values[i];
        }
        finally { _updating = false; }
        _sizeLabel.Text = $"{_selection.Width} × {_selection.Height} px";
        _canvas.AccessibleDescription = $"{UiLocalization.T("Crop selection")}: {_selection.X}, {_selection.Y}, {_selection.Width} × {_selection.Height} px";
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape && _dragMode != 0) { EndDrag(true); return true; }
        if (_canvas.Focused)
        {
            var step = (keyData & Keys.Shift) != 0 ? 10 : 1;
            var delta = (keyData & Keys.KeyCode) switch { Keys.Left => new Point(-step, 0), Keys.Right => new Point(step, 0),
                Keys.Up => new Point(0, -step), Keys.Down => new Point(0, step), _ => Point.Empty };
            if (!delta.IsEmpty) { var r = _selection; r.X = Math.Clamp(r.X + delta.X, 0, _sourceSize.Width - r.Width);
                r.Y = Math.Clamp(r.Y + delta.Y, 0, _sourceSize.Height - r.Height); SetSelection(r); return true; }
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private sealed class CropCanvas : Panel
    {
        public CropCanvas()
        {
            // The form's buffer does not cover child HWNDs. Compose image and overlays
            // in this control's buffer without exposing a separate background erase.
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { } // PaintCanvas fills the entire surface.
    }

    private void PaintCanvas(object? sender, PaintEventArgs e)
    {
        if (_imageBounds.IsEmpty) FitView();
        var g = e.Graphics; g.Clear(_canvas.BackColor);
        using var tile = new HatchBrush(HatchStyle.LargeCheckerBoard, Color.FromArgb(62, 62, 62), Color.FromArgb(46, 46, 46));
        g.FillRectangle(tile, _imageBounds);
        var pointFilter = _filterMode == ImageFilterMode.Point;
        g.InterpolationMode = pointFilter ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = pointFilter ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
        var imageState = g.Save();
        try
        {
            g.TranslateTransform(FlipHorizontal ? _imageBounds.Left + _imageBounds.Right : 0,
                FlipVertical ? _imageBounds.Top + _imageBounds.Bottom : 0);
            g.ScaleTransform(FlipHorizontal ? -1 : 1, FlipVertical ? -1 : 1);
            g.DrawImage(_image, _imageBounds, new RectangleF(0, 0, _image.Width, _image.Height), GraphicsUnit.Pixel);
        }
        finally { g.Restore(imageState); }
        using var outside = new Region(_canvas.ClientRectangle); outside.Exclude(_frame);
        using var shade = new SolidBrush(Color.FromArgb(165, Color.Black)); g.FillRegion(shade, outside);
        using var border = new Pen(Color.White, Math.Max(1f, DeviceDpi / 96f));
        g.DrawRectangle(border, _frame.X, _frame.Y, _frame.Width, _frame.Height);
        using var grid = new Pen(Color.FromArgb(100, Color.White));
        for (var i = 1; i < 3; i++) { g.DrawLine(grid, _frame.X + _frame.Width * i / 3, _frame.Top, _frame.X + _frame.Width * i / 3, _frame.Bottom);
            g.DrawLine(grid, _frame.Left, _frame.Y + _frame.Height * i / 3, _frame.Right, _frame.Y + _frame.Height * i / 3); }
        using var handle = new Pen(Color.White, 3f * DeviceDpi / 96f); var length = Math.Min(18f * DeviceDpi / 96f, Math.Min(_frame.Width, _frame.Height) / 3);
        foreach (var corner in new[] { new PointF(_frame.Left, _frame.Top), new PointF(_frame.Right, _frame.Top), new PointF(_frame.Left, _frame.Bottom), new PointF(_frame.Right, _frame.Bottom) })
        {
            g.DrawLine(handle, corner, new PointF(corner.X + (corner.X == _frame.Left ? length : -length), corner.Y));
            g.DrawLine(handle, corner, new PointF(corner.X, corner.Y + (corner.Y == _frame.Top ? length : -length)));
        }
        g.DrawLine(handle, _frame.Left, _frame.Y + _frame.Height / 2 - length / 2, _frame.Left, _frame.Y + _frame.Height / 2 + length / 2);
        g.DrawLine(handle, _frame.Right, _frame.Y + _frame.Height / 2 - length / 2, _frame.Right, _frame.Y + _frame.Height / 2 + length / 2);
        g.DrawLine(handle, _frame.X + _frame.Width / 2 - length / 2, _frame.Top, _frame.X + _frame.Width / 2 + length / 2, _frame.Top);
        g.DrawLine(handle, _frame.X + _frame.Width / 2 - length / 2, _frame.Bottom, _frame.X + _frame.Width / 2 + length / 2, _frame.Bottom);
        if (_canvas.Focused) Theme.DrawKeyboardFocus(g, Rectangle.Inflate(_canvas.ClientRectangle, -3, -3));
    }
}
