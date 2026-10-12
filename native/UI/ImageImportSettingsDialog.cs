using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

/// <summary>Result-oriented image import with precise, optional controls and lossless legacy metadata preservation.</summary>
internal sealed class ImageImportSettingsDialog : ModernDialogForm
{
    private readonly string _sourceName;
    private readonly string? _sourcePath;
    private readonly int _sourcePixelWidth;
    private readonly int _sourcePixelHeight;
    private readonly float _naturalPixelsPerUnit;
    private readonly BitmapImageImportSettings _initialSettings;
    private readonly bool _isEditing;
    private readonly int _batchCount;
    private readonly ComboBox _displayMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ModernNumericUpDown _placementScale = Numeric("Placement scale", 0.01m, 409600, 100, 2, 1);
    private readonly ModernNumericUpDown _maxSize = Numeric("Maximum resolution", 16, 8192, 8192, 0, 1);
    private readonly CheckBox _keepTransparency = new()
    {
        Text = "Keep transparent background", Checked = true, AutoSize = true,
        ForeColor = Theme.Text, BackColor = Theme.Panel, FlatStyle = FlatStyle.Flat,
        Font = Theme.UiFont(), AccessibleName = "Keep transparent background",
        Margin = new Padding(0, 3, 0, 3)
    };
    private readonly Label _resultSummary = new();
    private readonly Label _validation = new();
    private readonly Panel _preview = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 10) };
    private readonly SvgIconButton _advancedToggle = new(SvgIconKind.ChevronDown);
    private readonly SvgIconButton _editImage = new(SvgIconKind.EditImage);
    private readonly ToolTip _toolTip = new();
    private BitmapImageImportSettings _cropSettings;
    private readonly TableLayoutPanel _settingsLayout = CreateSettingsLayout();
    private readonly TableLayoutPanel _advancedSettings = CreateSettingsLayout();
    private readonly ThemedScrollPanel _scroll = new();
    private readonly CancellationTokenSource _previewCancellation = new();
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 250 };
    private Bitmap? _previewSource;
    private Bitmap? _previewOpaque;
    private string _previewStatus = "Loading preview...";
    private int _advancedRow;
    private bool _advancedExpanded;
    private bool _updating;
    private bool _scaleChanged;
    private int _previewVersion;
    private bool _previewDisposed;

    public ImageImportSettingsDialog(string title, string sourceName, int sourcePixelWidth,
        int sourcePixelHeight, float naturalPixelsPerUnit, BitmapImageImportSettings initialSettings,
        string? sourcePath = null, bool isEditing = false, int batchCount = 1)
        : base(title, new Size(560, 460))
    {
        _sourceName = sourceName;
        _sourcePath = sourcePath;
        _sourcePixelWidth = Math.Max(1, sourcePixelWidth);
        _sourcePixelHeight = Math.Max(1, sourcePixelHeight);
        _naturalPixelsPerUnit = float.IsFinite(naturalPixelsPerUnit) && naturalPixelsPerUnit > 0 ? naturalPixelsPerUnit : 96f;
        _initialSettings = initialSettings.IsValid ? initialSettings : BitmapImageImportSettings.Default;
        _cropSettings = _initialSettings;
        _isEditing = isEditing;
        _batchCount = Math.Max(1, batchCount);
        MinimumSize = new Size(480, 430);
        AccessibleName = title;
        _displayMode.Items.AddRange([UiLocalization.T("Smooth"), UiLocalization.T("Pixel art")]);
        _displayMode.AccessibleName = "Image display";
        Theme.StyleComboBox(_displayMode);
        BuildUi();
        SetSettings();
        WireEvents();
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _ = LoadPreviewAsync(); };
        var apply = AddDialogAction(isEditing ? "Apply" : "Import", DialogResult.OK,
            DialogActionStyle.Primary, () => ValidateSettings().IsValid);
        AcceptButton = apply;
        CancelButton = AddDialogAction("Cancel", DialogResult.Cancel);
        UiLocalization.Watch(this);
    }

    public BitmapImageImportSettings Settings => ReadSettings();

    private void BuildUi()
    {
        DialogContent.Padding = Padding.Empty;
        _scroll.Dock = DockStyle.Fill;
        _scroll.BackColor = Theme.Panel;
        _scroll.AccessibleName = "Image import settings";
        _scroll.ContentPadding = new Padding(20, 12, 20, 14);
        DialogContent.Controls.Add(_scroll);
        var layout = _settingsLayout;
        _scroll.Content.Controls.Add(layout);
        AddText(layout, _sourceName, 24, Theme.Text);
        AddText(layout, $"{_sourcePixelWidth} × {_sourcePixelHeight} px", 24, Theme.Muted);
        _preview.AccessibleName = "Image preview";
        _preview.TabStop = false;
        _preview.Paint += PaintPreview;
        _preview.Resize += (_, _) => { _editImage.Location = new Point(Math.Max(0, _preview.Width - _editImage.Width - 8), Math.Max(0, _preview.Height - _editImage.Height - 8)); _preview.Invalidate(true); };
        _editImage.AccessibleName = "Edit image";
        _editImage.AccessibleDescription = "Crop the image while keeping the original file";
        _editImage.Size = new Size(34, 34);
        _editImage.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _editImage.IsOverlayIcon = true;
        Theme.StyleToolbarButton(_editImage);
        _toolTip.SetToolTip(_editImage, UiLocalization.T("Edit image"));
        _editImage.Enabled = !string.IsNullOrWhiteSpace(_sourcePath) && File.Exists(_sourcePath);
        _editImage.Click += async (_, _) => await EditImageAsync();
        _preview.Controls.Add(_editImage);
        AddFullWidthRow(layout, _preview, 184);
        AddText(layout, _isEditing
            ? "Appearance settings keep object sizes. Cropping or changing placement scale updates all uses of this image."
            : _batchCount > 1
                ? string.Format(UiLocalization.T("Import {0} images. These settings apply to the whole batch; the preview shows the first image."), _batchCount)
                : "Ready to import. You can move and resize the image on stage later.", 44, Theme.Muted);
        ConfigureLabel(_validation, Theme.Warning);
        AddFullWidthRow(layout, _validation, 24);

        _advancedToggle.Text = "Advanced settings";
        _advancedToggle.AccessibleName = "Advanced settings";
        _advancedToggle.AccessibleDescription = "Show or hide advanced image import settings";
        _advancedToggle.AccessibleRole = AccessibleRole.CheckButton;
        _advancedToggle.IsDisclosureHeader = true;
        _advancedToggle.Dock = DockStyle.Fill;
        _advancedToggle.Margin = new Padding(0, 2, 0, 0);
        Theme.StyleToolbarButton(_advancedToggle);
        AddFullWidthRow(layout, _advancedToggle, 32);
        _advancedRow = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        layout.Controls.Add(_advancedSettings, 0, _advancedRow);
        layout.SetColumnSpan(_advancedSettings, 2);
        _advancedSettings.Visible = false;

        AddField(_advancedSettings, "Image display", _displayMode,
            "Smooth for photos and artwork; Pixel art keeps hard pixel edges.");
        AddField(_advancedSettings, "Maximum resolution", _maxSize,
            "Limits decoded pixels, keeps the source file and does not resize placed objects.");
        _maxSize.Suffix = "px";
        AddFullWidthRow(_advancedSettings, _keepTransparency, 32);
        AddField(_advancedSettings, "Placement scale", _placementScale, _isEditing
            ? "100% keeps the current import scale. Changes scale all uses of this image proportionally."
            : "100% uses the default import size. Image resolution and placement size are independent.");
        _placementScale.Suffix = "%";
        ConfigureLabel(_resultSummary, Theme.AccentLabel);
        AddFullWidthRow(_advancedSettings, _resultSummary, 48);
        if (_initialSettings.NonPowerOfTwoScale)
            AddText(_advancedSettings, "This image keeps its legacy power-of-two sizing setting.", 36, Theme.Warning);
    }

    private void SetSettings()
    {
        _updating = true;
        try
        {
            _displayMode.SelectedIndex = _initialSettings.FilterMode == ImageFilterMode.Point ? 1 : 0;
            _maxSize.Value = _initialSettings.MaxSize;
            _keepTransparency.Checked = _initialSettings.AlphaSource == ImageAlphaSource.FromInput;
            _placementScale.Minimum = 100m / _initialSettings.PixelsPerUnit;
            _placementScale.Maximum = 409600m / _initialSettings.PixelsPerUnit;
            _placementScale.Value = 100;
        }
        finally { _updating = false; }
        RefreshSummary();
    }

    private void WireEvents()
    {
        _advancedToggle.Click += (_, _) => SetAdvancedExpanded(!_advancedExpanded);
        _displayMode.SelectedIndexChanged += (_, _) => { RefreshSummary(); QueuePreview(); };
        _maxSize.ValueChanged += (_, _) => { RefreshSummary(); QueuePreview(); };
        _keepTransparency.CheckedChanged += (_, _) => RefreshSummary();
        _placementScale.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            _scaleChanged = true;
            RefreshSummary();
        };
    }

    private BitmapImageImportSettings ReadSettings() => _initialSettings with
    {
        FilterMode = _displayMode.SelectedIndex == 1 ? ImageFilterMode.Point
            : _initialSettings.FilterMode == ImageFilterMode.Trilinear ? ImageFilterMode.Trilinear : ImageFilterMode.Bilinear,
        MaxSize = (int)_maxSize.Value,
        AlphaSource = _keepTransparency.Checked ? ImageAlphaSource.FromInput : ImageAlphaSource.None,
        PixelsPerUnit = _scaleChanged
            ? Math.Clamp((int)Math.Round(_initialSettings.PixelsPerUnit * _placementScale.Value / 100m), 1, 4096)
            : _initialSettings.PixelsPerUnit,
        CropX = _cropSettings.CropX, CropY = _cropSettings.CropY,
        CropWidth = _cropSettings.CropWidth, CropHeight = _cropSettings.CropHeight
    };

    private (bool IsValid, string Message) ValidateSettings()
    {
        var settings = ReadSettings();
        if (!settings.IsValid) return (false, "The import settings are out of range.");
        var decoded = settings.ResolveStoredPixelSize(new SizeF(_sourcePixelWidth, _sourcePixelHeight));
        return BitmapImageFormats.TryValidateDecodedBudget((int)decoded.Width, (int)decoded.Height, out var error)
            ? (true, "") : (false, error);
    }

    private void RefreshSummary()
    {
        if (_updating) return;
        var settings = ReadSettings();
        var decoded = settings.ResolveStoredPixelSize(new SizeF(_sourcePixelWidth, _sourcePixelHeight));
        var placed = settings.ResolvePlacedSize(new SizeF(_sourcePixelWidth, _sourcePixelHeight), _naturalPixelsPerUnit);
        _resultSummary.Text = string.Format(UiLocalization.T("Decoded: {0} × {1} px\nBase placement: {2} × {3} vu"),
            (int)decoded.Width, (int)decoded.Height, placed.Width.ToString("0.##"), placed.Height.ToString("0.##"));
        var validation = ValidateSettings();
        _validation.Text = validation.IsValid ? "" : UiLocalization.T(validation.Message);
        _preview.Invalidate(true);
    }

    private void SetAdvancedExpanded(bool expanded)
    {
        _advancedExpanded = expanded;
        _settingsLayout.SuspendLayout();
        try
        {
            _advancedSettings.Visible = expanded;
            _settingsLayout.RowStyles[_advancedRow].SizeType = expanded ? SizeType.AutoSize : SizeType.Absolute;
            _settingsLayout.RowStyles[_advancedRow].Height = 0;
            _advancedToggle.Icon = expanded ? SvgIconKind.ChevronUp : SvgIconKind.ChevronDown;
            Theme.StyleToolbarButton(_advancedToggle, expanded);
        }
        finally { _settingsLayout.ResumeLayout(true); }
        var area = Screen.FromControl(this).WorkingArea;
        var height = (int)Math.Round((expanded ? 670 : 460) * DeviceDpi / 96f);
        ClientSize = new Size(ClientSize.Width, Math.Min(height, area.Height));
        if (Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
        _scroll.RestoreScrollPosition(0);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = LoadPreviewAsync();
    }

    private void QueuePreview()
    {
        if (!IsHandleCreated || IsDisposed) return;
        _previewVersion++;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private async Task LoadPreviewAsync()
    {
        try
        {
            var token = _previewCancellation.Token;
            var version = ++_previewVersion;
            var settings = ReadSettings();
            var preview = await Task.Run(() => LoadThumbnail(_sourcePath, settings, token));
            if (IsDisposed || token.IsCancellationRequested || version != _previewVersion) { preview?.Dispose(); return; }
            _previewSource?.Dispose();
            _previewOpaque?.Dispose();
            _previewOpaque = null;
            _previewSource = preview;
            if (preview is not null)
            {
                _previewOpaque = new Bitmap(preview.Width, preview.Height, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(_previewOpaque);
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0, Matrix43 = 1 });
                graphics.DrawImage(preview, new Rectangle(0, 0, preview.Width, preview.Height),
                    0, 0, preview.Width, preview.Height, GraphicsUnit.Pixel, attributes);
            }
            _previewStatus = "Preview unavailable";
            _preview.Invalidate(true);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or OutOfMemoryException or OperationCanceledException or System.Runtime.InteropServices.ExternalException)
        {
            if (!IsDisposed) { _previewStatus = "Preview unavailable"; _preview.Invalidate(true); }
        }
    }

    private static Bitmap? LoadThumbnail(string? path, BitmapImageImportSettings settings, CancellationToken token)
        => LoadThumbnailCore(path, settings, token, 512);

    private static Bitmap? LoadThumbnailCore(string? path, BitmapImageImportSettings settings, CancellationToken token, int maximumEdge)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > BitmapImageFormats.MaximumSourceBytes) return null;
        token.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var source = Image.FromStream(stream, true, true);
        if (!BitmapImageFormats.TryValidateDecodedBudget(source.Width, source.Height, out _)) return null;
        var decoded = settings.ResolveStoredPixelSize(new SizeF(source.Width, source.Height));
        var scale = Math.Min(1f, maximumEdge / (float)Math.Max(decoded.Width, decoded.Height));
        var thumbnail = new Bitmap(Math.Max(1, (int)Math.Round(decoded.Width * scale)),
            Math.Max(1, (int)Math.Round(decoded.Height * scale)), PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(thumbnail);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = settings.FilterMode == ImageFilterMode.Point
                ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(0, 0, thumbnail.Width, thumbnail.Height),
                settings.ResolveCropPixelBounds(source.Width, source.Height), GraphicsUnit.Pixel);
            token.ThrowIfCancellationRequested();
            return thumbnail;
        }
        catch { thumbnail.Dispose(); throw; }
    }

    private async Task EditImageAsync()
    {
        _editImage.Enabled = false;
        try
        {
            var token = _previewCancellation.Token;
            var fullSettings = ReadSettings() with { CropX = 0, CropY = 0, CropWidth = 1, CropHeight = 1,
                MaxSize = 8192, NonPowerOfTwoScale = false };
            using var image = await Task.Run(() => LoadThumbnailCore(_sourcePath, fullSettings, token, 2048));
            if (IsDisposed || token.IsCancellationRequested) return;
            if (image is null) { _validation.Text = UiLocalization.T("The image could not be opened for editing."); return; }
            using var editor = new ImageCropDialog(image, new Size(_sourcePixelWidth, _sourcePixelHeight), ReadSettings(), _batchCount > 1);
            if (editor.ShowDialog(this) != DialogResult.OK) return;
            var crop = editor.Selection;
            _cropSettings = _cropSettings with { CropX = crop.X / (float)_sourcePixelWidth, CropY = crop.Y / (float)_sourcePixelHeight,
                CropWidth = crop.Width / (float)_sourcePixelWidth, CropHeight = crop.Height / (float)_sourcePixelHeight };
            RefreshSummary();
            QueuePreview();
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or OutOfMemoryException or OperationCanceledException or System.Runtime.InteropServices.ExternalException)
        {
            if (!IsDisposed) _validation.Text = UiLocalization.T("The image could not be opened for editing.");
        }
        finally { if (!IsDisposed) _editImage.Enabled = true; }
    }

    private void PaintPreview(object? sender, PaintEventArgs e)
    {
        var bounds = _preview.ClientRectangle;
        var tile = Math.Max(8, (int)Math.Round(12 * DeviceDpi / 96f));
        using var light = new SolidBrush(Theme.PanelStrong);
        using var dark = new SolidBrush(Theme.Field);
        for (var y = 0; y < bounds.Height; y += tile)
            for (var x = 0; x < bounds.Width; x += tile)
                e.Graphics.FillRectangle((x / tile + y / tile) % 2 == 0 ? light : dark,
                    x, y, Math.Min(tile, bounds.Width - x), Math.Min(tile, bounds.Height - y));
        var bitmap = _keepTransparency.Checked ? _previewSource : _previewOpaque;
        if (bitmap is null)
        {
            TextRenderer.DrawText(e.Graphics, UiLocalization.T(_previewStatus), Theme.UiFont(), bounds,
                Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }
        var scale = Math.Min((bounds.Width - 16f) / bitmap.Width, (bounds.Height - 16f) / bitmap.Height);
        var size = new Size(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)));
        e.Graphics.InterpolationMode = _displayMode.SelectedIndex == 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(bitmap, new Rectangle((bounds.Width - size.Width) / 2, (bounds.Height - size.Height) / 2, size.Width, size.Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_previewDisposed)
        {
            _previewDisposed = true;
            _previewCancellation.Cancel();
            _previewTimer.Dispose();
            _toolTip.Dispose();
            _previewSource?.Dispose();
            _previewOpaque?.Dispose();
            _previewCancellation.Dispose();
        }
        base.Dispose(disposing);
    }

    private static TableLayoutPanel CreateSettingsLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Panel,
            ColumnCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        return layout;
    }

    private static void AddFullWidthRow(TableLayoutPanel layout, Control control, int height)
    {
        layout.Controls.Add(control, 0, layout.RowCount++);
        layout.SetColumnSpan(control, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
    }

    private static void ConfigureLabel(Label label, Color color)
    {
        label.Dock = DockStyle.Fill;
        label.BackColor = Theme.Panel;
        label.ForeColor = color;
        label.Font = Theme.UiFont();
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Margin = new Padding(0, 0, 0, 2);
    }

    private static void AddText(TableLayoutPanel layout, string text, int height, Color color)
    {
        var label = new Label();
        ConfigureLabel(label, color);
        label.Text = text;
        label.AutoEllipsis = true;
        AddFullWidthRow(layout, label, height);
    }

    private void AddField(TableLayoutPanel layout, string text, Control input, string description)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        var label = new Label();
        ConfigureLabel(label, Theme.Text);
        label.Text = text;
        label.AccessibleDescription = UiLocalization.T(description);
        input.AccessibleDescription = UiLocalization.T(description);
        _toolTip.SetToolTip(label, UiLocalization.T(description));
        label.AutoEllipsis = true;
        label.Margin = new Padding(0, 3, 10, 3);
        layout.Controls.Add(label, 0, row);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 3, 0, 3);
        layout.Controls.Add(input, 1, row);
    }

    private static ModernNumericUpDown Numeric(string name, decimal min, decimal max, decimal value, int decimals, decimal increment)
        => new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals,
            Increment = increment, AccessibleName = name, AccessibleDescription = name };
}
