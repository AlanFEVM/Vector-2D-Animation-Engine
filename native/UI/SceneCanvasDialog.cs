using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;

namespace VectorAnimationEngine;

internal sealed class SceneCanvasDialog : ModernDialogForm
{
    private const float MaximumImageOffset = 5_000_000f;
    private readonly ModernTextInput _width = NumberInput("Canvas width in pixels");
    private readonly ModernTextInput _height = NumberInput("Canvas height in pixels");
    private readonly ModernTextInput _colorOpacity = NumberInput("Color opacity percentage");
    private readonly ModernTextInput _imageScale = NumberInput("Uniform image scale percentage");
    private readonly ModernTextInput _offsetX = NumberInput("Image X offset in pixels");
    private readonly ModernTextInput _offsetY = NumberInput("Image Y offset in pixels");
    private readonly ModernTextInput _imageOpacity = NumberInput("Image opacity percentage");
    private readonly ModernTextInput _displayWidth = NumberInput("Displayed image width in pixels");
    private readonly ModernTextInput _displayHeight = NumberInput("Displayed image height in pixels");
    private readonly ComboBox _background = Selector("Background");
    private readonly ComboBox _projectImage = Selector("Project image");
    private readonly ComboBox _imageFit = Selector("Image fit");
    private readonly CheckBox _lockAspect = Toggle("Lock aspect ratio");
    private readonly CheckBox _dimOutside = Toggle("Dim outside canvas");
    private readonly CanvasContentButton _colorButton = ActionButton("Choose color…");
    private readonly Button _externalButton = ActionButton("Choose external image…");
    private readonly Button _centerButton = ActionButton("Center image");
    private readonly Button _matchButton = ActionButton("Match image size");
    private readonly Label _source = ValueLabel();
    private readonly Label _originalSize = ValueLabel();
    private readonly Label _validation = ValueLabel();
    private readonly CanvasPreview _preview = new();
    private readonly ToolTip _toolTip = new();
    private readonly List<(Button Button, Size Size)> _presets = [];
    private readonly List<Control> _imageControls = [];
    private readonly List<(TableLayoutPanel Panel, int Row, int Height)> _imageRows = [];
    private readonly List<(Button Button, int Horizontal, int Vertical)> _alignmentButtons = [];
    private readonly Func<ImageAssetDefinition, string?> _resolveImagePath;
    private readonly Button _apply;
    private SceneCanvasSettings _draft;
    private Bitmap? _thumbnail;
    private Size _imagePixelSize;
    private Color _color;
    private string? _pendingExternalPath;
    private string? _loadedImagePath;
    private string? _imageError;
    private bool _updating;
    private bool? _imageRowsVisible;
    private bool _displaySizeInvalid;
    private ModernTextInput? _displayEditSource;
    private double _aspectRatio;

    public SceneCanvasDialog(
        SceneCanvasSettings? settings,
        IReadOnlyList<ImageAssetDefinition> images,
        Func<ImageAssetDefinition, string?> resolveImagePath)
        : base("Canvas Background", new Size(900, 680))
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(resolveImagePath);
        _resolveImagePath = resolveImagePath;
        Settings = settings ?? new SceneCanvasSettings();
        _draft = Settings;
        _color = Color.FromArgb(Settings.ColorArgb);
        MinimumSize = new Size(720, 520);
        AccessibleName = "Canvas Background";

        _background.Items.AddRange(new object[]
        {
            new Choice<SceneBackgroundKind>(SceneBackgroundKind.None, "None"),
            new Choice<SceneBackgroundKind>(SceneBackgroundKind.Solid, "Solid color"),
            new Choice<SceneBackgroundKind>(SceneBackgroundKind.Image, "Image")
        });
        _imageFit.Items.AddRange(new object[]
        {
            new Choice<CanvasImageFit>(CanvasImageFit.Contain, "Contain"),
            new Choice<CanvasImageFit>(CanvasImageFit.Cover, "Cover"),
            new Choice<CanvasImageFit>(CanvasImageFit.Original, "Original")
        });
        _projectImage.Items.Add(new ImageChoice(null, "Choose a project image…"));
        foreach (var image in images) _projectImage.Items.Add(new ImageChoice(image, null));
        Theme.UpdateComboBoxDropDownHeight(_background);
        Theme.UpdateComboBoxDropDownHeight(_imageFit);
        Theme.UpdateComboBoxDropDownHeight(_projectImage);

        BuildUi();
        _apply = AddDialogAction("Apply", DialogResult.OK, DialogActionStyle.Primary, CommitSettings);
        var cancel = AddDialogAction("Cancel", DialogResult.Cancel);
        AcceptButton = _apply;
        CancelButton = cancel;
        BindSettings();
        WireEvents();
        UiLocalization.Watch(this);
        LoadSelectedImage();
        UpdateDraft();
    }

    public SceneCanvasSettings Settings { get; private set; }
    public string? SelectedImagePath { get; private set; }

    private void BuildUi()
    {
        DialogContent.Padding = new Padding(16, 12, 16, 12);
        var columns = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, BackColor = Theme.Panel
        };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        DialogContent.Controls.Add(columns);

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Margin = new Padding(0, 0, 16, 0), BackColor = Theme.Panel
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 252));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        columns.Controls.Add(left, 0, 0);
        left.Controls.Add(Heading("Canvas presets"), 0, 0);
        var presets = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3,
            Margin = Padding.Empty, BackColor = Theme.Panel
        };
        presets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        presets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < 3; row++)
            presets.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
        string[] names = ["Standard", "HD", "Full HD", "4K", "Portrait", "Square"];
        Size[] sizes = [new(640, 480), new(1280, 720), new(1920, 1080),
            new(3840, 2160), new(1080, 1920), new(1080, 1080)];
        for (var index = 0; index < sizes.Length; index++)
        {
            var size = sizes[index];
            var name = names[index];
            var button = ActionButton("");
            button.AccessibleName = name;
            button.AccessibleDescription = $"{size.Width} × {size.Height} px";
            button.Padding = new Padding(4, 28, 4, 22);
            button.Margin = new Padding(index % 2 == 0 ? 0 : 4, 0, index % 2 == 0 ? 4 : 0, 8);
            button.ContentPaint += (_, e) => PaintPresetCard(button, size, name, e);
            button.Click += (_, _) => SetCanvasSize(size.Width, size.Height);
            presets.Controls.Add(button, index % 2, index / 2);
            _presets.Add((button, size));
        }
        left.Controls.Add(presets, 0, 1);
        left.Controls.Add(Heading("Preview"), 0, 2);
        _preview.Dock = DockStyle.Fill;
        _preview.Margin = Padding.Empty;
        _preview.AccessibleName = "Canvas background preview";
        _preview.Paint += PaintPreview;
        left.Controls.Add(_preview, 0, 3);

        var scroll = new ThemedScrollPanel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty,
            ContentPadding = new Padding(0, 0, 8, 8), AccessibleName = "Canvas background settings"
        };
        columns.Controls.Add(scroll, 1, 0);
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 0, BackColor = Theme.Panel, Margin = Padding.Empty
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Content.Controls.Add(fields);

        AddRow(fields, "Width × height (px)", Pair(_width, _height));
        var swap = ActionButton("Swap dimensions");
        swap.Click += (_, _) =>
        {
            if (TryDimension(_width, out var width) && TryDimension(_height, out var height))
                SetCanvasSize(height, width);
        };
        AddWideRow(fields, Pair(_lockAspect, swap));
        AddRow(fields, "Background", _background);
        _colorButton.Text = "";
        _colorButton.ContentPaint += PaintColorSwatch;
        AddRow(fields, "Color", _colorButton);
        AddRow(fields, "Quick colors", CreateQuickColors(), 80);
        AddRow(fields, "Color opacity (%)", _colorOpacity);
        AddImageRow(fields, "Project image", _projectImage);
        AddImageRow(fields, "External image", _externalButton);
        _source.AccessibleName = "Selected image source";
        AddImageRow(fields, "Source", _source);
        AddImageRow(fields, "Original size (px)", _originalSize);
        AddImageRow(fields, "Image fit", _imageFit);
        AddImageRow(fields, "Uniform scale (%)", _imageScale);
        AddImageRow(fields, "Display W × H (px)", Pair(_displayWidth, _displayHeight));
        AddImageRow(fields, "Offset X / Y (px)", Pair(_offsetX, _offsetY));
        AddImageRow(fields, "Image opacity (%)", _imageOpacity);
        AddImageRow(fields, "Image alignment", CreateImageAlignment(), 120);
        var placement = Pair(_centerButton, _matchButton);
        _imageRows.Add((fields, fields.RowCount, 40));
        AddWideRow(fields, placement);
        _imageControls.Add(placement);
        AddWideRow(fields, _dimOutside);
        _validation.ForeColor = Theme.Danger;
        _validation.AccessibleName = "Canvas settings validation";
        AddWideRow(fields, _validation, 62);

        _toolTip.SetToolTip(_imageScale, UiLocalization.T("Uniform scale is applied after image fit."));
        _toolTip.SetToolTip(_offsetX, UiLocalization.T("Offset from the centered image, in canvas pixels."));
        _toolTip.SetToolTip(_offsetY, UiLocalization.T("Offset from the centered image, in canvas pixels."));
        _toolTip.SetToolTip(_externalButton, UiLocalization.T("The image is imported only after you apply these settings."));
        _toolTip.SetToolTip(_displayWidth, UiLocalization.T("Edit either display dimension to scale the image uniformly."));
        _toolTip.SetToolTip(_displayHeight, UiLocalization.T("Edit either display dimension to scale the image uniformly."));
        KeepFocusedEditorsVisible(fields, scroll);
    }

    private TableLayoutPanel CreateQuickColors()
    {
        var palette = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
            BackColor = Theme.Panel, Margin = Padding.Empty
        };
        palette.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        palette.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        palette.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        palette.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        (string Name, Color Color)[] colors =
        [
            ("White", Color.White), ("Black", Color.Black),
            ("Light gray", Color.FromArgb(224, 224, 224)), ("Transparent", Color.Transparent)
        ];
        for (var index = 0; index < colors.Length; index++)
        {
            var (name, color) = colors[index];
            var button = ActionButton(name);
            button.Text = "";
            button.Margin = new Padding(0, 0, index % 2 == 0 ? 4 : 0, index < 2 ? 4 : 0);
            button.ContentPaint += (_, e) => PaintSwatch(button, color, e);
            button.Click += (_, _) =>
            {
                _updating = true;
                try
                {
                    _color = color;
                    _colorOpacity.Text = FormatNumber(color.A * 100d / 255);
                    if (_background.SelectedItem is not Choice<SceneBackgroundKind> { Value: SceneBackgroundKind.Image })
                        _background.SelectedIndex = FindChoice(_background, SceneBackgroundKind.Solid);
                }
                finally { _updating = false; }
                UpdateDraft();
            };
            palette.Controls.Add(button, index % 2, index / 2);
        }
        return palette;
    }

    private TableLayoutPanel CreateImageAlignment()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3,
            BackColor = Theme.Panel, Margin = Padding.Empty
        };
        string[] symbols = ["↖", "↑", "↗", "←", "•", "→", "↙", "↓", "↘"];
        string[] names =
        [
            "Align top left", "Align top center", "Align top right",
            "Align middle left", "Align center", "Align middle right",
            "Align bottom left", "Align bottom center", "Align bottom right"
        ];
        for (var index = 0; index < 3; index++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
        }
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
            {
                var index = row * 3 + column;
                var horizontal = column - 1;
                var vertical = row - 1;
                var button = ActionButton(symbols[index]);
                button.AccessibleName = names[index];
                button.Margin = new Padding(0, 0, column == 2 ? 0 : 4, row == 2 ? 0 : 4);
                _toolTip.SetToolTip(button, UiLocalization.T(names[index]));
                button.Click += (_, _) => AlignImage(horizontal, vertical);
                grid.Controls.Add(button, column, row);
                _alignmentButtons.Add((button, horizontal, vertical));
            }
        return grid;
    }

    private void AlignImage(int horizontal, int vertical)
    {
        if (_displaySizeInvalid || !TryImageLayout(out var settings, out var bounds)) return;
        var offsetX = (settings.Width - (double)bounds.Width) * horizontal / 2;
        var offsetY = (settings.Height - (double)bounds.Height) * vertical / 2;
        if (Math.Abs(offsetX) > MaximumImageOffset || Math.Abs(offsetY) > MaximumImageOffset) return;
        _updating = true;
        try
        {
            _offsetX.Text = FormatNumber(offsetX);
            _offsetY.Text = FormatNumber(offsetY);
        }
        finally { _updating = false; }
        UpdateDraft();
    }

    private bool TryImageLayout(out SceneCanvasSettings settings, out RectangleF bounds, bool unitScale = false)
    {
        settings = _draft;
        bounds = RectangleF.Empty;
        if (_thumbnail is null || _imagePixelSize.IsEmpty
            || !TryDimension(_width, out var width) || !TryDimension(_height, out var height)
            || _imageFit.SelectedItem is not Choice<CanvasImageFit> fit) return false;
        var percent = 100f;
        if (!unitScale && !TryNumber(_imageScale, 0.1f, 10000, out percent)) return false;
        settings = _draft with
        {
            Width = width, Height = height, ImageFit = fit.Value,
            ImageScale = percent / 100, ImageOffsetX = 0, ImageOffsetY = 0
        };
        bounds = SceneCanvasLayout.ResolveImageBounds(settings, new RectangleF(0, 0, width, height), _imagePixelSize);
        return bounds.Width > 0 && bounds.Height > 0 && float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height);
    }

    private void ResetDisplaySizeAndUpdate()
    {
        if (_updating) return;
        _displaySizeInvalid = false;
        UpdateDraft();
    }

    private void DisplayDimensionChanged(ModernTextInput input, bool widthChanged)
    {
        if (_updating) return;
        _displayEditSource = input;
        try
        {
            _displaySizeInvalid = true;
            if (TryNumber(input, float.Epsilon, float.MaxValue, out var desired)
                && TryImageLayout(out _, out var baseline, unitScale: true))
            {
                var scale = (float)(desired / (double)(widthChanged ? baseline.Width : baseline.Height));
                if (float.IsFinite(scale) && scale is >= 0.001f and <= 100f)
                {
                    _displaySizeInvalid = false;
                    _updating = true;
                    try { _imageScale.Text = FormatNumber(scale * 100d); }
                    finally { _updating = false; }
                }
            }
            UpdateDraft();
        }
        finally { _displayEditSource = null; }
    }

    private void RefreshDisplayDimensions(bool isImage)
    {
        var invalid = isImage && _displaySizeInvalid;
        var hint = UiLocalization.T(invalid
            ? "Enter a positive display size within the supported uniform scale range."
            : "Edit either display dimension to scale the image uniformly.");
        _displayWidth.ForeColor = _displayHeight.ForeColor = invalid ? Theme.Danger : Theme.Text;
        _toolTip.SetToolTip(_displayWidth, hint);
        _toolTip.SetToolTip(_displayHeight, hint);
        if (_displaySizeInvalid) return;
        var bounds = RectangleF.Empty;
        var available = isImage && TryImageLayout(out _, out bounds);
        _updating = true;
        try
        {
            if (_displayEditSource != _displayWidth)
                _displayWidth.Text = available ? FormatNumber(bounds.Width) : string.Empty;
            if (_displayEditSource != _displayHeight)
                _displayHeight.Text = available ? FormatNumber(bounds.Height) : string.Empty;
        }
        finally { _updating = false; }
    }

    private void BindSettings()
    {
        _updating = true;
        try
        {
            _width.Text = Settings.Width.ToString(CultureInfo.CurrentCulture);
            _height.Text = Settings.Height.ToString(CultureInfo.CurrentCulture);
            _aspectRatio = Settings.Height > 0 ? Settings.Width / (double)Settings.Height : 16d / 9;
            _colorOpacity.Text = FormatNumber(_color.A * 100d / 255);
            _imageScale.Text = FormatNumber(Settings.ImageScale * 100d);
            _offsetX.Text = FormatNumber(Settings.ImageOffsetX);
            _offsetY.Text = FormatNumber(Settings.ImageOffsetY);
            _imageOpacity.Text = FormatNumber(Settings.ImageOpacity * 100d);
            _dimOutside.Checked = Settings.DimOutside;
            _background.SelectedIndex = FindChoice(_background, Settings.BackgroundKind);
            _imageFit.SelectedIndex = FindChoice(_imageFit, Settings.ImageFit);
            _projectImage.SelectedIndex = 0;
            for (var index = 1; index < _projectImage.Items.Count; index++)
            {
                if (_projectImage.Items[index] is ImageChoice { Asset: { } asset }
                    && string.Equals(asset.Id, Settings.ImageAssetId, StringComparison.Ordinal))
                {
                    _projectImage.SelectedIndex = index;
                    break;
                }
            }
            if (!string.IsNullOrWhiteSpace(Settings.ImageAssetId) && _projectImage.SelectedIndex == 0)
            {
                _projectImage.Items.Add(new ImageChoice(null, "Missing project image", Settings.ImageAssetId));
                _projectImage.SelectedIndex = _projectImage.Items.Count - 1;
                Theme.UpdateComboBoxDropDownHeight(_projectImage);
            }
        }
        finally { _updating = false; }
    }

    private void WireEvents()
    {
        _width.TextChanged += (_, _) => DimensionChanged(widthChanged: true);
        _height.TextChanged += (_, _) => DimensionChanged(widthChanged: false);
        _lockAspect.CheckedChanged += (_, _) =>
        {
            if (TryDimension(_width, out var width) && TryDimension(_height, out var height))
                _aspectRatio = width / (double)height;
        };
        ModernTextInput[] inputs = [_colorOpacity, _offsetX, _offsetY, _imageOpacity];
        foreach (var input in inputs) input.TextChanged += (_, _) => UpdateDraft();
        _imageScale.TextChanged += (_, _) => ResetDisplaySizeAndUpdate();
        _background.SelectedIndexChanged += (_, _) => ResetDisplaySizeAndUpdate();
        _imageFit.SelectedIndexChanged += (_, _) => ResetDisplaySizeAndUpdate();
        _displayWidth.TextChanged += (_, _) => DisplayDimensionChanged(_displayWidth, widthChanged: true);
        _displayHeight.TextChanged += (_, _) => DisplayDimensionChanged(_displayHeight, widthChanged: false);
        _dimOutside.CheckedChanged += (_, _) => UpdateDraft();
        _projectImage.SelectedIndexChanged += (_, _) =>
        {
            if (_updating) return;
            _pendingExternalPath = null;
            LoadSelectedImage();
            UpdateDraft();
        };
        _externalButton.Click += (_, _) => ChooseExternalImage();
        _centerButton.Click += (_, _) =>
        {
            _updating = true;
            try { _offsetX.Text = "0"; _offsetY.Text = "0"; }
            finally { _updating = false; }
            UpdateDraft();
        };
        _matchButton.Click += (_, _) =>
        {
            if (_imagePixelSize.IsEmpty) return;
            _updating = true;
            try { _imageScale.Text = "100"; _offsetX.Text = "0"; _offsetY.Text = "0"; }
            finally { _updating = false; }
            SetCanvasSize(_imagePixelSize.Width, _imagePixelSize.Height);
        };
        _colorButton.Click += (_, _) =>
        {
            using var picker = new ProfessionalColorPickerDialog(_color, "Canvas color");
            picker.ColorChanged += (_, _) =>
            {
                _color = Color.FromArgb(_color.A, picker.Color);
                _colorButton.Invalidate();
                UpdateDraft();
            };
            picker.ShowDialog(this);
        };
    }

    private void DimensionChanged(bool widthChanged)
    {
        if (_updating) return;
        _displaySizeInvalid = false;
        if (_lockAspect.Checked && _aspectRatio > 0
            && TryDimension(widthChanged ? _width : _height, out var dimension))
        {
            var other = Math.Round(widthChanged ? dimension / _aspectRatio : dimension * _aspectRatio);
            _updating = true;
            try
            {
                (widthChanged ? _height : _width).Text = other is >= 1 and <= 32768
                    ? other.ToString("0", CultureInfo.CurrentCulture) : string.Empty;
            }
            finally { _updating = false; }
        }
        UpdateDraft();
    }

    private void SetCanvasSize(int width, int height)
    {
        _displaySizeInvalid = false;
        _updating = true;
        try
        {
            _width.Text = width.ToString(CultureInfo.CurrentCulture);
            _height.Text = height.ToString(CultureInfo.CurrentCulture);
            _aspectRatio = width / (double)height;
        }
        finally { _updating = false; }
        UpdateDraft();
    }

    private void ChooseExternalImage()
    {
        using var dialog = new OpenFileDialog
        {
            Title = UiLocalization.T("Choose background image"),
            Filter = BitmapImageFormats.BuildDialogFilter(),
            CheckFileExists = true, Multiselect = false, RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _pendingExternalPath = dialog.FileName;
        _updating = true;
        try { _projectImage.SelectedIndex = 0; }
        finally { _updating = false; }
        LoadSelectedImage();
        UpdateDraft();
    }

    private void LoadSelectedImage()
    {
        _displaySizeInvalid = false;
        _thumbnail?.Dispose();
        _thumbnail = null;
        _imagePixelSize = Size.Empty;
        _loadedImagePath = null;
        _imageError = null;
        var sourcePixelSize = Size.Empty;
        var choice = _projectImage.SelectedItem as ImageChoice;
        _source.Text = _pendingExternalPath ?? choice?.Asset?.Name ?? choice?.ToString() ?? "—";
        _toolTip.SetToolTip(_source, _source.Text);
        try
        {
            var path = _pendingExternalPath;
            if (path is null && choice?.Asset is { } asset) path = _resolveImagePath(asset);
            if (string.IsNullOrWhiteSpace(path))
            {
                _imageError = UiLocalization.T("Choose an available project image or an external file.");
                return;
            }
            if (!BitmapImageFormats.IsSupportedExtension(Path.GetExtension(path)))
            {
                _imageError = UiLocalization.T("The image file format is not supported.");
                return;
            }
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > BitmapImageFormats.MaximumSourceBytes)
            {
                _imageError = UiLocalization.T("The image is missing, empty, or exceeds the image file size limit.");
                return;
            }
            if (_pendingExternalPath is null && choice?.Asset is { } projectImage)
            {
                var raster = BitmapImageRasterizer.Decode(path, projectImage.ImportSettings);
                using var bitmap = raster.AcquireBitmap();
                _thumbnail = CreateThumbnail(bitmap.Bitmap);
                _imagePixelSize = new Size(raster.PixelWidth, raster.PixelHeight);
                sourcePixelSize = new Size(projectImage.PixelWidth, projectImage.PixelHeight);
            }
            else
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var source = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
                if (!BitmapImageFormats.TryValidateDecodedBudget(source.Width, source.Height, out var error))
                {
                    _imageError = error;
                    return;
                }
                _thumbnail = CreateThumbnail(source);
                _imagePixelSize = source.Size;
                sourcePixelSize = source.Size;
            }
            _loadedImagePath = path;
        }
        catch (Exception exception)
        {
            _imageError = string.Format(CultureInfo.CurrentCulture,
                UiLocalization.T("Unable to open background image: {0}"), exception.Message);
        }
        finally
        {
            _originalSize.Text = sourcePixelSize.Width > 0 && sourcePixelSize.Height > 0
                ? PixelSizeText(sourcePixelSize.Width, sourcePixelSize.Height) : "—";
            _preview.Invalidate();
        }
    }

    private static Bitmap CreateThumbnail(Image source)
    {
        var factor = Math.Min(1d, 960d / Math.Max(source.Width, source.Height));
        var thumbnail = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * factor)),
            Math.Max(1, (int)Math.Round(source.Height * factor)), PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(thumbnail);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(Point.Empty, thumbnail.Size),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
            return thumbnail;
        }
        catch { thumbnail.Dispose(); throw; }
    }

    private void UpdateDraft()
    {
        if (_updating) return;
        var kind = (_background.SelectedItem as Choice<SceneBackgroundKind>)?.Value;
        var isImage = kind == SceneBackgroundKind.Image;
        var hasBackground = kind is SceneBackgroundKind.Solid or SceneBackgroundKind.Image;
        _colorButton.Enabled = _colorOpacity.Enabled = hasBackground;
        foreach (var control in _imageControls) control.Enabled = isImage;
        SetImageRowsVisible(isImage);
        _centerButton.Enabled = _matchButton.Enabled = isImage && _thumbnail is not null;

        var valid = TryReadSettings(out var candidate, out var error);
        _apply.Enabled = valid;
        _validation.Text = error;
        _toolTip.SetToolTip(_validation, error);
        if (valid) _draft = candidate;
        RefreshDisplayDimensions(isImage);
        var alignmentSettings = _draft;
        var imageBounds = RectangleF.Empty;
        var canAlign = isImage && !_displaySizeInvalid && TryImageLayout(out alignmentSettings, out imageBounds);
        foreach (var (button, horizontal, vertical) in _alignmentButtons)
        {
            button.Enabled = canAlign
                && Math.Abs((alignmentSettings.Width - (double)imageBounds.Width) * horizontal / 2) <= MaximumImageOffset
                && Math.Abs((alignmentSettings.Height - (double)imageBounds.Height) * vertical / 2) <= MaximumImageOffset;
        }
        foreach (var (button, size) in _presets)
        {
            Theme.StyleStandardButton(button, TryDimension(_width, out var width)
                && TryDimension(_height, out var height) && size.Width == width && size.Height == height);
        }
        _colorButton.Invalidate();
        _preview.Invalidate();
    }

    private bool TryReadSettings(out SceneCanvasSettings candidate, out string error)
    {
        candidate = _draft;
        error = UiLocalization.T("Enter canvas dimensions between 1 and 32768 pixels.");
        if (!TryDimension(_width, out var width) || !TryDimension(_height, out var height)) return false;
        error = UiLocalization.T("Choose a background type and image fit.");
        if (_background.SelectedItem is not Choice<SceneBackgroundKind> background
            || _imageFit.SelectedItem is not Choice<CanvasImageFit> fit) return false;
        var alpha = Color.FromArgb(_draft.ColorArgb).A;
        var scale = _draft.ImageScale;
        var offsetX = _draft.ImageOffsetX;
        var offsetY = _draft.ImageOffsetY;
        var opacity = _draft.ImageOpacity;
        if (background.Value != SceneBackgroundKind.None)
        {
            error = UiLocalization.T("Color opacity must be between 0 and 100 percent.");
            if (!TryNumber(_colorOpacity, 0, 100, out var percent)) return false;
            alpha = (byte)Math.Round(percent * 255d / 100);
        }
        if (background.Value == SceneBackgroundKind.Image)
        {
            error = UiLocalization.T("Enter a positive display size within the supported uniform scale range.");
            if (_displaySizeInvalid) return false;
            error = UiLocalization.T("Uniform scale must be between 0.1 and 10000 percent.");
            if (!TryNumber(_imageScale, 0.1f, 10000, out var percent)) return false;
            scale = percent / 100;
            error = UiLocalization.T("Image offsets must be between -5000000 and 5000000 pixels.");
            if (!TryNumber(_offsetX, -MaximumImageOffset, MaximumImageOffset, out offsetX)
                || !TryNumber(_offsetY, -MaximumImageOffset, MaximumImageOffset, out offsetY)) return false;
            error = UiLocalization.T("Image opacity must be between 0 and 100 percent.");
            if (!TryNumber(_imageOpacity, 0, 100, out var imagePercent)) return false;
            opacity = imagePercent / 100;
            error = _imageError ?? UiLocalization.T("The selected background image is no longer available.");
            if (_thumbnail is null || _imageError is not null || !File.Exists(_loadedImagePath)) return false;
        }
        candidate = new SceneCanvasSettings
        {
            Width = width, Height = height, BackgroundKind = background.Value,
            ColorArgb = Color.FromArgb(alpha, _color).ToArgb(),
            ImageAssetId = _pendingExternalPath is not null ? string.Empty
                : (_projectImage.SelectedItem as ImageChoice)?.Asset?.Id
                    ?? (_projectImage.SelectedItem as ImageChoice)?.MissingId ?? string.Empty,
            ImageFit = fit.Value, ImageScale = scale, ImageOffsetX = offsetX, ImageOffsetY = offsetY,
            ImageOpacity = opacity, DimOutside = _dimOutside.Checked
        };
        error = UiLocalization.T("The canvas settings are outside the supported range.");
        var validationSettings = background.Value == SceneBackgroundKind.Image && _pendingExternalPath is not null
            ? candidate with { ImageAssetId = "pending" } : candidate;
        if (!validationSettings.IsValid) return false;
        error = string.Empty;
        return true;
    }

    private bool CommitSettings()
    {
        if (!TryReadSettings(out var settings, out var error))
        {
            _validation.Text = error;
            _toolTip.SetToolTip(_validation, error);
            _apply.Enabled = false;
            return false;
        }
        Settings = settings;
        SelectedImagePath = settings.BackgroundKind == SceneBackgroundKind.Image ? _pendingExternalPath : null;
        return true;
    }

    private void PaintPreview(object? sender, PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(Theme.Stage);
        var inset = 12 * _preview.DeviceDpi / 96;
        var area = Rectangle.Inflate(_preview.ClientRectangle, -inset, -inset);
        if (area.Width <= 0 || area.Height <= 0 || _draft.Width <= 0 || _draft.Height <= 0) return;
        if (_draft.DimOutside)
        {
            using var dim = new SolidBrush(Color.FromArgb(90, Color.Black));
            graphics.FillRectangle(dim, _preview.ClientRectangle);
        }
        var zoom = Math.Min(area.Width / (double)_draft.Width, area.Height / (double)_draft.Height) * 0.9;
        var canvas = new RectangleF((float)(area.Left + (area.Width - _draft.Width * zoom) / 2),
            (float)(area.Top + (area.Height - _draft.Height * zoom) / 2),
            (float)(_draft.Width * zoom), (float)(_draft.Height * zoom));
        var state = graphics.Save();
        try
        {
            graphics.SetClip(canvas);
            using var checkerLight = new SolidBrush(Theme.Panel);
            using var checkerDark = new SolidBrush(Theme.PanelHover);
            graphics.FillRectangle(checkerLight, canvas);
            var cell = Math.Max(4, 8 * _preview.DeviceDpi / 96);
            for (var row = 0; row * cell < canvas.Height; row++)
                for (var column = 0; column * cell < canvas.Width; column++)
                    if ((row + column) % 2 == 0)
                        graphics.FillRectangle(checkerDark, canvas.Left + column * cell, canvas.Top + row * cell, cell, cell);
            if (_draft.BackgroundKind != SceneBackgroundKind.None)
            {
                using var fill = new SolidBrush(Color.FromArgb(_draft.ColorArgb));
                graphics.FillRectangle(fill, canvas);
            }
            if (_draft.BackgroundKind == SceneBackgroundKind.Image && _thumbnail is not null && !_imagePixelSize.IsEmpty)
            {
                var target = SceneCanvasLayout.ResolveImageBounds(_draft, canvas, _imagePixelSize);
                if (target.IntersectsWith(canvas) && float.IsFinite(target.X) && float.IsFinite(target.Y)
                    && float.IsFinite(target.Right) && float.IsFinite(target.Bottom))
                {
                    using var attributes = new ImageAttributes();
                    attributes.SetColorMatrix(new ColorMatrix { Matrix33 = _draft.ImageOpacity });
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    PointF[] corners = [new(target.Left, target.Top), new(target.Right, target.Top), new(target.Left, target.Bottom)];
                    graphics.DrawImage(_thumbnail, corners, new RectangleF(0, 0, _thumbnail.Width, _thumbnail.Height),
                        GraphicsUnit.Pixel, attributes);
                }
            }
        }
        finally { graphics.Restore(state); }
        using var border = new Pen(Theme.Border);
        graphics.DrawRectangle(border, canvas.X, canvas.Y, canvas.Width, canvas.Height);
    }

    private static void PaintPresetCard(Button button, Size size, string name, PaintEventArgs e)
    {
        var dpi = button.DeviceDpi / 96f;
        var frameScale = Math.Min(36 * dpi / size.Width, 18 * dpi / size.Height);
        var frame = new RectangleF((button.Width - size.Width * frameScale) / 2,
            7 * dpi, size.Width * frameScale, size.Height * frameScale);
        var state = e.Graphics.Save();
        try
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(Color.FromArgb(24, button.ForeColor));
            using var border = new Pen(button.ForeColor, Math.Max(1, dpi));
            e.Graphics.FillRectangle(fill, frame);
            e.Graphics.DrawRectangle(border, frame.X, frame.Y, frame.Width, frame.Height);
        }
        finally { e.Graphics.Restore(state); }
        var inset = (int)Math.Ceiling(4 * dpi);
        var title = new Rectangle(inset, (int)Math.Ceiling(28 * dpi),
            Math.Max(0, button.Width - inset * 2), (int)Math.Ceiling(24 * dpi));
        TextRenderer.DrawText(e.Graphics, UiLocalization.T(name), button.Font, title, button.ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
        var dimensions = new Rectangle(inset, button.Height - (int)Math.Ceiling(22 * dpi),
            Math.Max(0, button.Width - inset * 2), (int)Math.Ceiling(18 * dpi));
        TextRenderer.DrawText(e.Graphics, $"{size.Width} × {size.Height}", button.Font, dimensions,
            button.ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
    }

    private void PaintColorSwatch(object? sender, PaintEventArgs e)
    {
        var alpha = TryNumber(_colorOpacity, 0, 100, out var percent)
            ? (int)Math.Round(percent * 255d / 100) : _color.A;
        PaintSwatch(_colorButton, Color.FromArgb(alpha, _color), e);
    }

    private static void PaintSwatch(Button button, Color color, PaintEventArgs e)
    {
        var inset = Math.Max(4, 6 * button.DeviceDpi / 96);
        var edge = Math.Max(8, Math.Min(button.Height - inset * 2, 16 * button.DeviceDpi / 96));
        var rect = new Rectangle(inset, (button.Height - edge) / 2, edge, edge);
        using var checker = new HatchBrush(HatchStyle.LargeCheckerBoard, Theme.PanelHover, Theme.Panel);
        using var fill = new SolidBrush(color);
        using var border = new Pen(Theme.Border);
        e.Graphics.FillRectangle(checker, rect);
        e.Graphics.FillRectangle(fill, rect);
        e.Graphics.DrawRectangle(border, rect);
        var textBounds = new Rectangle(rect.Right + inset, 0,
            Math.Max(0, button.Width - rect.Right - inset * 2), button.Height);
        TextRenderer.DrawText(e.Graphics, UiLocalization.T(button.AccessibleName), button.Font, textBounds,
            button.Enabled ? button.ForeColor : Theme.DisabledText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
    }

    protected override void OnLoad(EventArgs e)
    {
        var working = Screen.FromControl(Owner ?? this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, working.Width), Math.Min(MinimumSize.Height, working.Height));
        Size = new Size(Math.Min(Width, working.Width), Math.Min(Height, working.Height));
        base.OnLoad(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnail?.Dispose();
            _thumbnail = null;
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private static bool TryDimension(ModernTextInput input, out int value) =>
        int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value is >= 1 and <= 32768;

    private static bool TryNumber(ModernTextInput input, float minimum, float maximum, out float value) =>
        float.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        && float.IsFinite(value) && value >= minimum && value <= maximum;

    private static string FormatNumber(double value) => value.ToString("0.#########", CultureInfo.CurrentCulture);

    private static string PixelSizeText(double width, double height) =>
        $"{width.ToString("0.##", CultureInfo.CurrentCulture)} × {height.ToString("0.##", CultureInfo.CurrentCulture)} px";

    private static void KeepFocusedEditorsVisible(Control parent, ThemedScrollPanel scroll)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is ModernTextInput or ComboBox or Button or CheckBox)
            {
                child.Enter += (_, _) =>
                {
                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        var top = scroll.PointToClient(child.PointToScreen(Point.Empty)).Y;
                        if (top >= 0 && top + child.Height <= scroll.ClientSize.Height) break;
                        var position = scroll.ScrollPosition;
                        scroll.ScrollWheel(top < 0 ? 120 : -120);
                        if (scroll.ScrollPosition == position) break;
                    }
                };
            }
            else KeepFocusedEditorsVisible(child, scroll);
        }
    }

    private static int FindChoice<T>(ComboBox box, T value) where T : struct, Enum
    {
        for (var index = 0; index < box.Items.Count; index++)
            if (box.Items[index] is Choice<T> choice && EqualityComparer<T>.Default.Equals(choice.Value, value)) return index;
        return -1;
    }

    private static ModernTextInput NumberInput(string name)
    {
        var input = new ModernTextInput { Dock = DockStyle.Fill, AccessibleName = name, Margin = Padding.Empty, MaxLength = 48 };
        Theme.StyleTextBox(input);
        input.Font = Theme.UiFont(11);
        return input;
    }

    private static ComboBox Selector(string name)
    {
        var box = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = name, Margin = Padding.Empty
        };
        Theme.StyleComboBox(box);
        return box;
    }

    private static CheckBox Toggle(string text) => new()
    {
        Text = text, AccessibleName = text, Dock = DockStyle.Fill, AutoSize = false,
        ForeColor = Theme.Text, BackColor = Theme.Panel, Margin = Padding.Empty,
        FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false
    };

    private static CanvasContentButton ActionButton(string text)
    {
        var button = new CanvasContentButton
        {
            Text = text, AccessibleName = text, Dock = DockStyle.Fill, AutoEllipsis = true,
            Margin = Padding.Empty, MinimumSize = new Size(0, Theme.ControlHeight)
        };
        Theme.StyleStandardButton(button);
        return button;
    }

    private static Label ValueLabel() => new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        ForeColor = Theme.Muted, BackColor = Theme.Panel, Margin = Padding.Empty, UseMnemonic = false
    };

    private static Label Heading(string text)
    {
        var label = ValueLabel();
        label.Text = text;
        label.ForeColor = Theme.Text;
        label.Font = Theme.UiFont(9.5f, FontStyle.Bold);
        return label;
    }

    private static TableLayoutPanel Pair(Control first, Control second)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            BackColor = Theme.Panel, Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        first.Margin = new Padding(0, 0, 4, 0);
        second.Margin = new Padding(4, 0, 0, 0);
        panel.Controls.Add(first, 0, 0);
        panel.Controls.Add(second, 1, 0);
        return panel;
    }

    private static void AddRow(TableLayoutPanel panel, string text, Control editor, int height = 40)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var label = ValueLabel();
        label.Text = text;
        label.ForeColor = Theme.Text;
        label.Margin = new Padding(0, 0, 8, 8);
        editor.Margin = new Padding(0, 0, 0, 8);
        panel.Controls.Add(label, 0, row);
        panel.Controls.Add(editor, 1, row);
    }

    private void AddImageRow(TableLayoutPanel panel, string text, Control editor, int height = 40)
    {
        _imageRows.Add((panel, panel.RowCount, height));
        AddRow(panel, text, editor, height);
        _imageControls.Add(editor);
    }

    private void SetImageRowsVisible(bool visible)
    {
        if (_imageRowsVisible == visible) return;
        _imageRowsVisible = visible;
        foreach (var panel in _imageRows.Select(row => row.Panel).Distinct())
        {
            panel.SuspendLayout();
            try
            {
                foreach (var row in _imageRows.Where(row => ReferenceEquals(row.Panel, panel)))
                {
                    panel.RowStyles[row.Row].Height = visible ? row.Height : 0;
                    foreach (Control control in panel.Controls)
                        if (panel.GetRow(control) == row.Row) control.Visible = visible;
                }
            }
            finally
            {
                panel.ResumeLayout(performLayout: true);
            }
        }
    }

    private static void AddWideRow(TableLayoutPanel panel, Control control, int height = 40)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        control.Margin = new Padding(0, 0, 0, 8);
        panel.Controls.Add(control, 0, row);
        panel.SetColumnSpan(control, 2);
    }

    private sealed record Choice<T>(T Value, string Text) where T : struct, Enum
    {
        public override string ToString() => UiLocalization.T(Text);
    }

    private sealed record ImageChoice(ImageAssetDefinition? Asset, string? Text, string? MissingId = null)
    {
        public override string ToString() => Asset?.Name ?? UiLocalization.T(Text);
    }

    private sealed class CanvasContentButton : Button
    {
        public event PaintEventHandler? ContentPaint;

        public CanvasContentButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            ContentPaint?.Invoke(this, e);
        }
    }

    private sealed class CanvasPreview : Panel
    {
        public CanvasPreview()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            TabStop = false;
        }
    }
}
