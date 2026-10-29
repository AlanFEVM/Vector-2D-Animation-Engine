namespace VectorAnimationEngine;

internal sealed class SelectionSummaryPanel : UserControl
{
    private readonly Label _title = new();
    private readonly Label _selection = InspectorModuleStyle.CreateTextLabel();
    private readonly Label _hint = new() { AutoEllipsis = true, UseMnemonic = false };
    private readonly Label _layerCaption = InspectorModuleStyle.CreateFieldLabel("Layer");
    private readonly Label _layer = InspectorModuleStyle.CreateTextLabel();
    private readonly Label _detailCaption = InspectorModuleStyle.CreateFieldLabel("Geometry atoms");
    private readonly Label _detail = InspectorModuleStyle.CreateTextLabel();
    private readonly Label _totalCaption = InspectorModuleStyle.CreateFieldLabel("Drawing objects");
    private readonly Label _total = InspectorModuleStyle.CreateTextLabel();
    private readonly ToolTip _toolTip = new();
    private readonly BitmapSelectionPreview _preview = new();
    private bool _hasImagePreview;
    private bool _hasSelection;
    private bool _layoutReady;

    public SelectionSummaryPanel()
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = Padding.Empty;
        Height = PreferredHeight;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Object summary";
        InspectorModuleStyle.StylePageTitle(_title);
        _title.Text = "Object";
        _selection.Font = Theme.UiFont(11f);
        _hint.Font = Theme.UiFont();
        _hint.ForeColor = Theme.PanelSecondaryText;
        _hint.Text = "Select an object on the stage to inspect its properties.";
        Theme.StyleToolTip(_toolTip);
        Controls.AddRange([_title, _selection, _hint, _layerCaption, _layer,
            _detailCaption, _detail, _totalCaption, _total, _preview]);
        _layoutReady = true;
        SetSummary(null, null, null, null, 0);
        PerformLayout();
    }

    public int PreferredHeight => _hasImagePreview ? 258 : 230;

    public void SetImagePreview(bool hasImage, BitmapImageRaster? raster, BitmapSampling sampling)
    {
        _preview.SetRaster(raster, sampling);
        if (_hasImagePreview == hasImage) return;
        _hasImagePreview = hasImage;
        Height = ScaleMetric(PreferredHeight);
        PerformLayout();
    }

    public void SetSummary(string? selection, string? layer, string? detailCaption, string? detail, int objectCount)
    {
        var hasSelection = !string.IsNullOrEmpty(selection);
        SetText(_selection, hasSelection ? selection! : UiLocalization.T("No object selected"));
        SetText(_layer, layer ?? UiLocalization.T("None"));
        SetText(_detailCaption, UiLocalization.T(detailCaption ?? "Geometry atoms"));
        SetText(_detail, detail ?? "–");
        SetText(_total, CompactFormat.Number(objectCount));
        _toolTip.SetToolTip(_total, UiLocalization.T("Total drawing objects, not the number of selected objects."));
        if (_hasSelection != hasSelection)
        {
            _hasSelection = hasSelection;
            PerformLayout();
        }
    }

    private void SetText(Label label, string value)
    {
        if (label.Text != value) label.Text = value;
        label.AccessibleDescription = value;
        _toolTip.SetToolTip(label, value);
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        if (!_layoutReady) return;
        var inset = ScaleMetric(16);
        var width = Math.Max(1, ClientSize.Width - inset * 2);
        _title.SetBounds(inset + ScaleMetric(24), ScaleMetric(16), Math.Max(1, width - ScaleMetric(24)), ScaleMetric(28));
        var previewWidth = _hasImagePreview ? Math.Min(ScaleMetric(150), (int)Math.Round(width * 0.46)) : 0;
        var summaryWidth = _hasImagePreview ? Math.Max(1, width - previewWidth - ScaleMetric(12)) : width;
        _selection.SetBounds(inset, ScaleMetric(66), summaryWidth, ScaleMetric(32));
        _preview.Visible = _hasSelection && _hasImagePreview;
        _preview.SetBounds(inset + width - previewWidth, ScaleMetric(66), Math.Max(1, previewWidth), ScaleMetric(140));
        _hint.SetBounds(inset, ScaleMetric(110), width, ScaleMetric(56));
        _hint.Visible = !_hasSelection;
        _layerCaption.Visible = _layer.Visible = _hasSelection;
        _detailCaption.Visible = _detail.Visible = _hasSelection;
        void Row(Label caption, Label value, int top)
        {
            caption.Dock = DockStyle.None;
            var labelWidth = (int)Math.Floor(width * 0.48f);
            caption.SetBounds(inset, ScaleMetric(top), Math.Max(1, labelWidth - ScaleMetric(8)), ScaleMetric(32));
            value.SetBounds(inset + labelWidth, ScaleMetric(top), Math.Max(1, width - labelWidth), ScaleMetric(32));
        }
        Row(_layerCaption, _layer, 106);
        Row(_detailCaption, _detail, 146);
        Row(_totalCaption, _total, 186);
        if (_hasImagePreview)
        {
            _layerCaption.SetBounds(inset, ScaleMetric(106), summaryWidth, ScaleMetric(20));
            _layer.SetBounds(inset, ScaleMetric(128), summaryWidth, ScaleMetric(24));
            _detailCaption.SetBounds(inset, ScaleMetric(160), summaryWidth, ScaleMetric(20));
            _detail.SetBounds(inset, ScaleMetric(182), summaryWidth, ScaleMetric(24));
        }
        if (_hasImagePreview) Row(_totalCaption, _total, 218);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        SvgIcons.Draw(eventArgs.Graphics, SvgIconKind.InspectorObject,
            new Rectangle(ScaleMetric(16), ScaleMetric(21), ScaleMetric(18), ScaleMetric(18)), Theme.PanelSecondaryText);
        InspectorModuleStyle.DrawDivider(this, eventArgs.Graphics, ScaleMetric(54));
        InspectorModuleStyle.DrawBottomDivider(this, eventArgs.Graphics);
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        Height = ScaleMetric(PreferredHeight);
        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private int ScaleMetric(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

    private sealed class BitmapSelectionPreview : Control
    {
        private BitmapImageRaster? _raster;
        private BitmapSampling _sampling;

        public BitmapSelectionPreview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleName = "Image preview";
            AccessibleDescription = UiLocalization.T("Image unavailable");
            AccessibleRole = AccessibleRole.Graphic;
            TabStop = false;
        }

        public void SetRaster(BitmapImageRaster? raster, BitmapSampling sampling)
        {
            if (ReferenceEquals(_raster, raster) && _sampling == sampling) return;
            _raster = raster;
            _sampling = sampling;
            AccessibleDescription = raster is null ? UiLocalization.T("Image unavailable")
                : $"{raster.PixelWidth} × {raster.PixelHeight} px";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            UiDrawingHelpers.DrawCheckerboard(eventArgs.Graphics, bounds, Math.Max(1, (int)Math.Round(8 * DeviceDpi / 96f)),
                Theme.Field, Theme.PanelHover);
            if (_raster is { } raster)
            {
                var fit = Math.Min(bounds.Width / (float)raster.PixelWidth, bounds.Height / (float)raster.PixelHeight);
                var imageWidth = Math.Max(1, (int)Math.Round(raster.PixelWidth * fit));
                var imageHeight = Math.Max(1, (int)Math.Round(raster.PixelHeight * fit));
                var target = new Rectangle(bounds.Left + (bounds.Width - imageWidth) / 2,
                    bounds.Top + (bounds.Height - imageHeight) / 2, imageWidth, imageHeight);
                using var lease = raster.AcquireBitmap();
                var state = eventArgs.Graphics.Save();
                try
                {
                    eventArgs.Graphics.InterpolationMode = _sampling == BitmapSampling.Point
                        ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
                        : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    eventArgs.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    eventArgs.Graphics.DrawImage(lease.Bitmap, target, 0, 0, raster.PixelWidth, raster.PixelHeight, GraphicsUnit.Pixel);
                }
                finally { eventArgs.Graphics.Restore(state); }
            }
            else
            {
                var iconSize = Math.Min(24, Math.Min(bounds.Width, bounds.Height));
                SvgIcons.Draw(eventArgs.Graphics, SvgIconKind.Image,
                    new Rectangle(bounds.Left + (bounds.Width - iconSize) / 2,
                        bounds.Top + (bounds.Height - iconSize) / 2, iconSize, iconSize), Theme.DisabledText);
            }
            using var border = new Pen(Theme.ControlOutline);
            eventArgs.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }
}
