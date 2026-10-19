using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

/// <summary>One file of a multi-image import batch, probed before the settings dialog opens.</summary>
internal sealed record ImageBatchFile(string Path, string Name, int PixelWidth, int PixelHeight,
    float NaturalPixelsPerUnit, ImageFilterMode SuggestedFilter = ImageFilterMode.Bilinear);

internal sealed record ImageBatchImportSelection(BitmapImageImportSettings Settings,
    IReadOnlyList<string> RemainingFiles, bool AutomaticDisplay = false,
    IReadOnlyDictionary<string, BitmapImageImportSettings>? PerFileSettings = null);

/// <summary>Result-oriented image import with precise, optional controls and lossless legacy metadata preservation.</summary>
internal sealed class ImageImportSettingsDialog : ModernDialogForm
{
    private string _sourceName;
    private string? _sourcePath;
    private int _sourcePixelWidth;
    private int _sourcePixelHeight;
    private float _naturalPixelsPerUnit;
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
    private const int BottomContentPadding = 16;
    private readonly PreviewPanel _preview = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 10) };
    private readonly SvgIconButton _advancedToggle = new(SvgIconKind.ChevronDown);
    private readonly SvgIconButton _editImage = new(SvgIconKind.EditImage);
    private readonly ToolTip _toolTip = new();
    private readonly Dictionary<string, BitmapImageImportSettings> _cropSettingsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ImageBatchFile> _batchFiles;
    private readonly List<BatchThumbItem> _thumbItems = new();
    private readonly Dictionary<string, Bitmap> _stripThumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BatchThumbItem> _thumbsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageFilterMode> _detectedFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Label _nameLabel = new();
    private readonly Label _sizeHeaderLabel = new();
    private Label _batchInfoLabel = new();
    private readonly PreviewNavButton _prevButton = new(SvgIconKind.ChevronLeft, "Previous image");
    private readonly PreviewNavButton _nextButton = new(SvgIconKind.ChevronRight, "Next image");
    private readonly SvgIconButton _stripToggle = new(SvgIconKind.Swatches);
    // ContentFocusable keeps programmatic focus inside the grid so wheel input still
    // reaches the grid after a removal disposes the control that used to hold focus.
    private readonly ThemedScrollPanel _stripScroll = new()
    {
        Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        BackColor = Theme.Panel, ContentFocusable = true
    };
    private readonly FlowLayoutPanel _stripFlow = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Theme.Panel };
    private readonly HashSet<int> _thumbSelection = new();
    private int _thumbSelectionAnchor;
    private bool _stripMode;
    private bool _stripRevealPending;
    private int _batchIndex;
    private int _stripVersion;
    // Delete-fill animation: surviving thumbnails slide into the slots the removal freed.
    private const int StripFillDurationMs = 240;
    private readonly System.Windows.Forms.Timer _stripFillTimer = new() { Interval = 15 };
    private readonly List<StripFillMove> _stripFillMoves = new();
    private long _stripFillStartedAt;
    private readonly TableLayoutPanel _settingsLayout = CreateSettingsLayout();
    private readonly TableLayoutPanel _advancedSettings = CreateSettingsLayout();
    private readonly ThemedScrollPanel _scroll = new();
    private readonly CancellationTokenSource _previewCancellation = new();
    private CancellationTokenSource? _activePreviewCancellation;
    private int _editReturnScrollPosition;
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 90 };
    private readonly Dictionary<string, Bitmap> _previewCache = new(StringComparer.Ordinal);
    private readonly List<string> _previewCacheOrder = new();
    private string? _previewCacheKey;
    private Bitmap? _previewSource;
    private Bitmap? _previewOpaque;
    private string _previewStatus = "Loading preview...";
    private int _advancedRow;
    private bool _advancedExpanded;
    private bool _updating;
    private bool _scaleChanged;
    private int _previewVersion;
    private bool _previewDisposed;
    private readonly string _dialogTitle;
    private readonly Button _applyAction;
    private readonly Button _cancelAction;
    private ImageCropDialog? _cropEditor;
    private Bitmap? _cropEditorImage;
    private int _validationRow;

    public ImageImportSettingsDialog(string title, string sourceName, int sourcePixelWidth,
        int sourcePixelHeight, float naturalPixelsPerUnit, BitmapImageImportSettings initialSettings,
        string? sourcePath = null, bool isEditing = false, int batchCount = 1,
        IReadOnlyList<ImageBatchFile>? batchFiles = null)
        : base(title, new Size(820, 460))
    {
        _dialogTitle = title;
        _sourceName = sourceName;
        _sourcePath = sourcePath;
        _sourcePixelWidth = Math.Max(1, sourcePixelWidth);
        _sourcePixelHeight = Math.Max(1, sourcePixelHeight);
        _naturalPixelsPerUnit = float.IsFinite(naturalPixelsPerUnit) && naturalPixelsPerUnit > 0 ? naturalPixelsPerUnit : 96f;
        _initialSettings = initialSettings.IsValid ? initialSettings : BitmapImageImportSettings.Default;
        _isEditing = isEditing;
        _batchCount = Math.Max(1, batchCount);
        // Keep every provided file, including a single one: an empty list makes
        // RemainingBatchFiles report "nothing left" and the caller imports zero files.
        _batchFiles = batchFiles is { Count: > 0 } ? new List<ImageBatchFile>(batchFiles) : new List<ImageBatchFile>();
        MinimumSize = new Size(480, 430);
        AccessibleName = title;
        foreach (var file in _batchFiles) _detectedFilters[file.Path] = file.SuggestedFilter;
        _displayMode.Items.AddRange([UiLocalization.T("Smooth"), UiLocalization.T("Pixel art"), UiLocalization.T("Automatic")]);
        _displayMode.AccessibleName = "Image display";
        Theme.StyleComboBox(_displayMode);
        BuildUi();
        SetSettings();
        UpdateBatchChrome();
        if (_batchFiles.Count > 0) SetBatchFile(0);
        WireEvents();
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _ = LoadPreviewAsync(); };
        _stripFillTimer.Tick += (_, _) => AdvanceStripFill();
        _applyAction = AddDialogAction(isEditing ? "Apply" : "Import", DialogResult.OK,
            DialogActionStyle.Primary, () =>
            {
                if (_cropEditor is null) return ValidateSettings().IsValid;
                FinishImageEdit(true);
                return false;
            });
        AcceptButton = _applyAction;
        _applyAction.AutoSize = true;
        _applyAction.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _applyAction.MinimumSize = new Size(92, 34);
        _cancelAction = AddDialogAction("Cancel", DialogResult.Cancel, canClose: () =>
        {
            if (_cropEditor is null) return true;
            FinishImageEdit(false);
            return false;
        });
        CancelButton = _cancelAction;
        Deactivate += (_, _) => _cropEditor?.CancelInteraction();
        UiLocalization.Watch(this);
    }

    public BitmapImageImportSettings Settings => ReadSettings();

    public BitmapImageImportSettings SharedSettings => ReadSettingsForPath(null);

    public IReadOnlyDictionary<string, BitmapImageImportSettings> BatchSettings =>
        _batchFiles.ToDictionary(file => file.Path, file => ReadSettingsForPath(file.Path), StringComparer.OrdinalIgnoreCase);

    public bool AutomaticDisplay => _displayMode.SelectedIndex == 2;

    /// <summary>Files left in the batch after in-dialog removals; callers must import only these.</summary>
    public IReadOnlyList<string> RemainingBatchFiles =>
        _batchFiles.Count > 0 ? _batchFiles.Select(file => file.Path).ToArray() : [];

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
        ConfigureLabel(_nameLabel, Theme.DisabledText);
        _nameLabel.Text = _sourceName;
        _nameLabel.AutoEllipsis = true;
        AddFullWidthRow(layout, _nameLabel, 24);
        ConfigureLabel(_sizeHeaderLabel, Theme.DisabledText);
        _sizeHeaderLabel.Text = $"{_sourcePixelWidth} × {_sourcePixelHeight} px";
        AddFullWidthRow(layout, _sizeHeaderLabel, 24);
        _preview.AccessibleName = "Image preview";
        _preview.TabStop = false;
        _preview.Paint += PaintPreview;
        _preview.Resize += (_, _) => LayoutPreviewOverlays();
        _toolTip.SetToolTip(_nameLabel, _sourceName);
        _prevButton.Click += (_, _) => { if (_batchIndex > 0) SetBatchFile(_batchIndex - 1); };
        _nextButton.Click += (_, _) =>
        {
            if (_batchIndex < _batchFiles.Count - 1) SetBatchFile(_batchIndex + 1);
            else _toolTip.Show(UiLocalization.T("Already at the last image"), _nextButton,
                new Point(0, _nextButton.Height + 4), 1800);
        };
        // Subscribe before the preview row joins the settings scroll host, so a wheel that
        // reaches the preview surface pages the open thumbnail grid instead of the list
        // behind it.
        _preview.MouseWheel += (_, e) =>
        {
            if (!_stripMode || e.Delta == 0) return;
            _stripScroll.ScrollWheel(e.Delta);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
        };
        _stripToggle.AccessibleName = "All images";
        _stripToggle.Size = new Size(34, 34);
        _stripToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _stripToggle.IsOverlayIcon = true;
        Theme.StyleToolbarButton(_stripToggle);
        _toolTip.SetToolTip(_stripToggle, UiLocalization.T("All images"));
        _stripToggle.Click += (_, _) => SetStripMode(!(_stripMode || _stripRevealPending));
        // Focus stays on the overlay button after opening the grid, and the button lives
        // outside the scroll container, so the wheel would never reach the grid.
        _stripToggle.MouseWheel += (_, e) =>
        {
            if (!_stripMode) return;
            _stripScroll.ScrollWheel(e.Delta);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
        };
        _stripScroll.Visible = false;
        _stripScroll.AccessibleName = "All images";
        _stripScroll.ContentPadding = new Padding(4);
        _stripScroll.Content.Controls.Add(_stripFlow);
        _preview.Controls.Add(_prevButton);
        _preview.Controls.Add(_nextButton);
        _preview.Controls.Add(_stripToggle);
        _preview.Controls.Add(_stripScroll);
        _stripScroll.BringToFront();
        _stripToggle.BringToFront();
        _editImage.Location = new Point(Math.Max(0, _preview.Width - _editImage.Width - 8), Math.Max(0, _preview.Height - _editImage.Height - 8));
        _editImage.AccessibleName = "Edit image";
        _editImage.AccessibleDescription = "Crop or flip the image while keeping the original file";
        _editImage.Size = new Size(34, 34);
        _editImage.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _editImage.IsOverlayIcon = true;
        Theme.StyleToolbarButton(_editImage);
        _toolTip.SetToolTip(_editImage, UiLocalization.T("Edit image"));
        _editImage.Enabled = !string.IsNullOrWhiteSpace(_sourcePath) && File.Exists(_sourcePath);
        _editImage.Click += async (_, _) => await EditImageAsync();
        _preview.Controls.Add(_editImage);
        AddFullWidthRow(layout, _preview, 460);
        _batchInfoLabel = AddText(layout, _isEditing
            ? "Appearance settings keep object sizes. Cropping or changing placement scale updates all uses of this image."
            : _batchFiles.Count > 1
                ? string.Format(UiLocalization.T("Import {0} images. Common settings apply to the batch; crop and flip affect only the current image. Use the arrows or grid to review images."), _batchFiles.Count)
                : _batchCount > 1
                    ? string.Format(UiLocalization.T("Import {0} images. Common settings apply to the batch; crop and flip affect only the current image. Use the arrows or grid to review images."), _batchCount)
                    : "Ready to import. You can move and resize the image on stage later.", 44, Theme.DisabledText);
        ConfigureLabel(_validation, Theme.Warning);
        _validationRow = layout.RowCount;
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

        _advancedSettings.ColumnStyles[0].Width = 50;
        _advancedSettings.ColumnStyles[1].Width = 50;
        _advancedSettings.RowCount = 5;
        _advancedSettings.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        _advancedSettings.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        _advancedSettings.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        _advancedSettings.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        _advancedSettings.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        AddField(_advancedSettings, "Image display", _displayMode,
            "Smooth for photos and artwork; Pixel art keeps hard pixel edges.", 0, 0);
        AddField(_advancedSettings, "Maximum resolution", _maxSize,
            "Limits decoded pixels, keeps the source file and does not resize placed objects.", 1, 0);
        _maxSize.Suffix = "px";
        _keepTransparency.AutoSize = false;
        _keepTransparency.Dock = DockStyle.Fill;
        _keepTransparency.Margin = new Padding(10, 3, 0, 8);
        _advancedSettings.Controls.Add(_keepTransparency, 1, 3);
        AddField(_advancedSettings, "Placement scale", _placementScale, _isEditing
            ? "100% keeps the current import scale. Changes scale all uses of this image proportionally."
            : "100% uses the default import size. Image resolution and placement size are independent.", 0, 1);
        _placementScale.Suffix = "%";
        ConfigureLabel(_resultSummary, Theme.DisabledText);
        _resultSummary.Padding = new Padding(0, 6, 0, 0);
        _advancedSettings.Controls.Add(_resultSummary, 0, 4);
        _advancedSettings.SetColumnSpan(_resultSummary, 2);
        if (_initialSettings.NonPowerOfTwoScale)
            AddText(_advancedSettings, "This image keeps its legacy power-of-two sizing setting.", 36, Theme.Warning);
    }

    private void SetSettings()
    {
        _updating = true;
        try
        {
            _displayMode.SelectedIndex = !_isEditing && _initialSettings == BitmapImageImportSettings.Default
                && !string.IsNullOrWhiteSpace(_sourcePath) ? 2
                : _initialSettings.FilterMode == ImageFilterMode.Point ? 1 : 0;
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
        _displayMode.SelectedIndexChanged += (_, _) =>
        {
            if (_updating) return;
            UpdateThumbnailFilter();
            if (_stripMode || _stripRevealPending) _ = LoadStripThumbnailsAsync();
            RefreshSummary();
            QueuePreview();
        };
        _maxSize.ValueChanged += (_, _) =>
        {
            RefreshSummary();
            QueuePreview();
            UpdateThumbnailFilter();
            if (_stripMode || _stripRevealPending) _ = LoadStripThumbnailsAsync();
        };
        _keepTransparency.CheckedChanged += (_, _) => RefreshSummary();
        _placementScale.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            _scaleChanged = true;
            RefreshSummary();
        };
    }

    private BitmapImageImportSettings ReadSettings() => ReadSettingsForPath(_sourcePath);

    private BitmapImageImportSettings ReadSettingsForPath(string? path)
    {
        var cropSettings = _cropSettingsByPath.TryGetValue(path ?? string.Empty, out var edited) ? edited : _initialSettings;
        return _initialSettings with
        {
            FilterMode = AutomaticDisplay ? ResolveDisplayFilter(path) : _displayMode.SelectedIndex == 1 ? ImageFilterMode.Point
                : _initialSettings.FilterMode == ImageFilterMode.Trilinear ? ImageFilterMode.Trilinear : ImageFilterMode.Bilinear,
            MaxSize = (int)_maxSize.Value,
            AlphaSource = _keepTransparency.Checked ? ImageAlphaSource.FromInput : ImageAlphaSource.None,
            PixelsPerUnit = _scaleChanged
                ? Math.Clamp((int)Math.Round(_initialSettings.PixelsPerUnit * _placementScale.Value / 100m), 1, 4096)
                : _initialSettings.PixelsPerUnit,
            CropX = cropSettings.CropX, CropY = cropSettings.CropY,
            CropWidth = cropSettings.CropWidth, CropHeight = cropSettings.CropHeight,
            FlipHorizontal = cropSettings.FlipHorizontal, FlipVertical = cropSettings.FlipVertical
        };
    }

    private (bool IsValid, string Message) ValidateSettings()
    {
        if (AutomaticDisplay && _sourcePath is { } path && !_detectedFilters.ContainsKey(path))
            return (false, "Detecting image display...");
        var settings = ReadSettings();
        if (!settings.IsValid) return (false, "The import settings are out of range.");
        if (_batchFiles.Count > 0)
        {
            foreach (var file in _batchFiles)
            {
                var size = settings.ResolveStoredPixelSize(new SizeF(file.PixelWidth, file.PixelHeight));
                if (!BitmapImageFormats.TryValidateDecodedBudget((int)size.Width, (int)size.Height, out var fileError))
                    return (false, fileError);
            }
            return (true, "");
        }
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
        _validation.Visible = !validation.IsValid;
        _settingsLayout.RowStyles[_validationRow].Height = validation.IsValid ? 0 : 24 * DeviceDpi / 96f;
        _preview.Invalidate();
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
        FitWindowToContent();
    }

    /// <summary>Fits the window height to the actual content instead of a fixed height.</summary>
    private void FitWindowToContent()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var chrome = ClientSize.Height - DialogContent.Height;
        // Measure on demand: the panel's actual Height is driven by its parent and lags behind
        // row changes (e.g. expanding advanced settings), which previously clipped the last row.
        _settingsLayout.PerformLayout();
        var contentHeight = _settingsLayout.PreferredSize.Height
            + _scroll.ContentPadding.Top + _scroll.ContentPadding.Bottom
            + BottomContentPadding;
        var height = Math.Min(area.Height, Math.Max(MinimumSize.Height, chrome + contentHeight));
        ClientSize = new Size(ClientSize.Width, height);
        if (Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
        _scroll.RestoreScrollPosition(0);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitWindowToContent();
        _ = LoadPreviewAsync();
    }

    private void QueuePreview(bool immediate = false)
    {
        if (!IsHandleCreated || IsDisposed) return;
        _activePreviewCancellation?.Cancel();
        _previewVersion++;
        _previewSource = null;
        _previewOpaque?.Dispose();
        _previewOpaque = null;
        _previewStatus = "Loading preview...";
        _preview.Invalidate();
        _previewTimer.Stop();
        if (immediate)
        {
            _ = LoadPreviewAsync();
            return;
        }
        _previewTimer.Start();
    }

    private void LayoutPreviewOverlays()
    {
        // Keep the round nav buttons on the same physical scale as the themed icon buttons.
        var navSize = Math.Max(28, (int)Math.Round(36f * DeviceDpi / 96f));
        if (_prevButton.Width != navSize)
        {
            _prevButton.Size = new Size(navSize, navSize);
            _nextButton.Size = new Size(navSize, navSize);
        }
        var edge = Math.Max(6, (int)Math.Round(8f * DeviceDpi / 96f));
        var middle = Math.Max(0, (_preview.Height - _prevButton.Height) / 2);
        _prevButton.Location = new Point(edge, middle);
        _nextButton.Location = new Point(Math.Max(edge, _preview.Width - _nextButton.Width - edge), middle);
        _stripToggle.Location = new Point(Math.Max(0, _preview.Width - _stripToggle.Width - edge), edge);
        var headerHeight = _stripToggle.Bottom + edge;
        _stripScroll.Bounds = new Rectangle(0, headerHeight, _preview.Width, Math.Max(0, _preview.Height - headerHeight));
        _editImage.Location = new Point(Math.Max(0, _preview.Width - _editImage.Width - 8), Math.Max(0, _preview.Height - _editImage.Height - 8));
        _preview.Invalidate();
    }

    private void UpdateBatchChrome()
    {
        var batch = _batchFiles.Count > 1;
        _stripToggle.Visible = batch;
        if (!batch) SetStripMode(false);
        _prevButton.Visible = batch && !_stripMode;
        _nextButton.Visible = batch && !_stripMode;
        _editImage.Visible = !_stripMode;
        LayoutPreviewOverlays();
    }

    private void SetBatchFile(int index)
    {
        if (_batchFiles.Count == 0) return;
        _batchIndex = Math.Clamp(index, 0, _batchFiles.Count - 1);
        var file = _batchFiles[_batchIndex];
        _sourceName = file.Name;
        _sourcePath = file.Path;
        _sourcePixelWidth = Math.Max(1, file.PixelWidth);
        _sourcePixelHeight = Math.Max(1, file.PixelHeight);
        _naturalPixelsPerUnit = float.IsFinite(file.NaturalPixelsPerUnit) && file.NaturalPixelsPerUnit > 0 ? file.NaturalPixelsPerUnit : 96f;
        _editImage.Enabled = File.Exists(file.Path);
        _nameLabel.Text = _batchFiles.Count > 1 ? $"{_batchIndex + 1} / {_batchFiles.Count}  ·  {file.Name}" : file.Name;
        _toolTip.SetToolTip(_nameLabel, file.Name);
        _sizeHeaderLabel.Text = $"{_sourcePixelWidth} × {_sourcePixelHeight} px";
        _prevButton.Enabled = _batchIndex > 0;
        _nextButton.Enabled = _batchFiles.Count > 1;
        _toolTip.Hide(_nextButton);
        _toolTip.SetToolTip(_nextButton, UiLocalization.T(_batchIndex < _batchFiles.Count - 1
            ? "Next image" : "Already at the last image"));
        _thumbSelection.Clear();
        _thumbSelection.Add(_batchIndex);
        _thumbSelectionAnchor = _batchIndex;
        RefreshThumbSelection();
        _stripFlow.Invalidate(true);
        RefreshSummary();
        QueuePreview(true);
    }

    /// <summary>
    /// Toggles the preview area between the large preview and a wrapping thumbnail grid.
    /// Both modes share the same preview row so the dialog layout never shifts.
    /// </summary>
    private void SetStripMode(bool gridMode)
    {
        if (_batchFiles.Count <= 1) gridMode = false;
        if (!gridMode)
        {
            _stripRevealPending = false;
            ApplyStripMode(false);
            return;
        }
        if (_stripMode || _stripRevealPending) return;
        FinishStripFill();
        _stripRevealPending = true;
        if (_thumbItems.Count != _batchFiles.Count) RebuildStrip();
        _sizeHeaderLabel.Text = string.Format(UiLocalization.T("Loading previews: {0} / {1}"), 0, _batchFiles.Count);
        _toolTip.SetToolTip(_stripToggle, UiLocalization.T("Loading preview..."));
        _ = LoadStripThumbnailsAsync();
        TryRevealStrip();
    }

    private void TryRevealStrip()
    {
        if (!_stripRevealPending || IsDisposed || _thumbItems.Count == 0) return;
        var cell = _thumbItems[0];
        var columns = Math.Max(1, (_stripScroll.Content.ClientSize.Width - _stripScroll.ContentPadding.Horizontal)
            / Math.Max(1, cell.Width + cell.Margin.Horizontal));
        var rows = Math.Max(1, (int)Math.Ceiling(_stripScroll.ClientSize.Height
            / (double)Math.Max(1, cell.Height + cell.Margin.Vertical)));
        var visibleCount = Math.Min(_thumbItems.Count, columns * rows);
        if (_thumbItems.Take(visibleCount).Any(item => item.Loading)) return;
        _stripRevealPending = false;
        ApplyStripMode(true);
    }

    private void ApplyStripMode(bool gridMode)
    {
        _stripMode = gridMode;
        if (!gridMode) _sizeHeaderLabel.Text = $"{_sourcePixelWidth} × {_sourcePixelHeight} px";
        _stripScroll.Visible = gridMode;
        FinishStripFill();
        _prevButton.Visible = !gridMode && _batchFiles.Count > 1;
        _nextButton.Visible = !gridMode && _batchFiles.Count > 1;
        _editImage.Visible = !gridMode;
        _stripToggle.Icon = gridMode ? SvgIconKind.Image : SvgIconKind.Swatches;
        // Overlay mode repaints the parent surface, which ghosts over the scrollable grid; use an opaque button there.
        _stripToggle.IsOverlayIcon = !gridMode;
        Theme.StyleToolbarButton(_stripToggle, gridMode);
        _toolTip.SetToolTip(_stripToggle, UiLocalization.T(gridMode ? "Edit image" : "All images"));
        _stripToggle.AccessibleName = UiLocalization.T(gridMode ? "Edit image" : "All images");
        if (gridMode)
        {
            // Park keyboard/wheel focus inside the grid so wheel gestures land on its scroll host.
            FocusStripContent();
        }
        _preview.Refresh();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Chrome outside the settings list (Cancel/Import buttons, the form itself) bubbles
        // the wheel up here; with the grid open it should page the grid instead of doing nothing.
        if (_stripMode && _stripScroll.Visible && e.Delta != 0)
        {
            _stripScroll.ScrollWheel(e.Delta);
            return;
        }
        base.OnMouseWheel(e);
    }

    private void RebuildStrip()
    {
        FinishStripFill();
        _stripFlow.SuspendLayout();
        try
        {
            _stripFlow.Controls.Clear();
            foreach (var item in _thumbItems) item.Dispose();
            _thumbItems.Clear();
            _thumbsByPath.Clear();
            var size = Math.Max(64, 78 * DeviceDpi / 96);
            // The remove badge scales with the thumbnails; a fixed 20 px badge reads as a
            // tiny smudge on a high-DPI strip.
            var badgeSize = Math.Max(18, (int)Math.Round(20f * DeviceDpi / 96f));
            var badgeInset = Math.Max(2, (int)Math.Round(3f * DeviceDpi / 96f));
            for (var i = 0; i < _batchFiles.Count; i++)
            {
                var file = _batchFiles[i];
                var item = new BatchThumbItem(file.Path, size)
                {
                    Selected = i == _batchIndex, Margin = new Padding(3),
                    PointFilter = ResolveDisplayFilter(file.Path) == ImageFilterMode.Point
                };
                if (_stripThumbs.TryGetValue(ThumbnailKey(file.Path), out var thumb)) item.Thumbnail = thumb;
                else item.Loading = true;
                var captured = item;
                item.Click += (_, _) => HandleThumbClick(_thumbItems.IndexOf(captured));
                item.DoubleClick += (_, _) => SetStripMode(false);
                var remove = new ThumbRemoveButton
                {
                    Bounds = new Rectangle(item.Width - badgeSize - badgeInset, badgeInset, badgeSize, badgeSize)
                };
                _toolTip.SetToolTip(remove, UiLocalization.T("Remove image"));
                remove.Click += (_, _) => RemoveThumb(_thumbItems.IndexOf(captured));
                item.AttachRemoveButton(remove);
                _thumbItems.Add(item);
                _thumbsByPath[file.Path] = item;
                _stripFlow.Controls.Add(item);
            }
        }
        finally { _stripFlow.ResumeLayout(true); }
        RefreshThumbSelection();
    }

    /// <summary>Applies plain, Ctrl (toggle) and Shift (range) selection to the batch thumbnails.</summary>
    private void HandleThumbClick(int index)
    {
        if (index < 0 || index >= _batchFiles.Count) return;
        var modifiers = Control.ModifierKeys;
        if (modifiers.HasFlag(Keys.Control))
        {
            if (!_thumbSelection.Remove(index)) _thumbSelection.Add(index);
        }
        else if (modifiers.HasFlag(Keys.Shift))
        {
            _thumbSelection.Clear();
            for (var i = Math.Min(_thumbSelectionAnchor, index); i <= Math.Max(_thumbSelectionAnchor, index); i++)
                _thumbSelection.Add(i);
        }
        else
        {
            SetBatchFile(index);
        }
        RefreshThumbSelection();
    }

    private void RefreshThumbSelection()
    {
        for (var i = 0; i < _thumbItems.Count; i++)
        {
            var selected = _thumbSelection.Contains(i);
            if (_thumbItems[i].Selected != selected)
            {
                _thumbItems[i].Selected = selected;
                _thumbItems[i].Invalidate();
            }
        }
    }

    /// <summary>Mirrors the Smooth / Pixel art choice onto the strip thumbnails so they
    /// stay as crisp as the preview instead of being blurred by the quality filter.</summary>
    private void UpdateThumbnailFilter()
    {
        foreach (var item in _thumbItems)
        {
            var pointFilter = ResolveDisplayFilter(item.FilePath) == ImageFilterMode.Point;
            item.PointFilter = pointFilter;
            item.Loading = !_stripThumbs.TryGetValue(ThumbnailKey(item.FilePath), out var thumb);
            if (thumb is not null) item.Thumbnail = thumb;
            item.Invalidate();
        }
    }

    private void RemoveThumb(int index)
    {
        if (index < 0 || index >= _batchFiles.Count) return;
        RemoveBatchFiles(_thumbSelection.Contains(index) && _thumbSelection.Count > 1
            ? _thumbSelection.OrderBy(i => i).ToArray()
            : new[] { index });
    }

    private void RemoveBatchFiles(IReadOnlyList<int> indices)
    {
        var targets = indices.Where(index => index >= 0 && index < _batchFiles.Count).Distinct().ToList();
        if (targets.Count == 0) return;
        if (targets.Count >= _batchFiles.Count)
        {
            var keep = Math.Clamp(_batchIndex, 0, _batchFiles.Count - 1);
            targets = Enumerable.Range(0, _batchFiles.Count).Where(index => index != keep).ToList();
            if (targets.Count == 0) return;
        }
        // Land any slide still in flight first: a second removal inside the animation window
        // would otherwise read half-slid coordinates as the starting slots.
        FinishStripFill();
        // Remember where every thumbnail sits now: the survivors slide from these slots
        // to the ones the removal frees.
        var previousLocations = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        if (_stripMode)
            foreach (var item in _thumbItems) previousLocations.TryAdd(item.FilePath, item.Location);
        var activePath = _batchFiles[_batchIndex].Path;
        foreach (var index in targets.OrderByDescending(index => index)) _batchFiles.RemoveAt(index);
        _thumbSelection.Clear();
        var activeIndex = _batchFiles.FindIndex(file => string.Equals(file.Path, activePath, StringComparison.OrdinalIgnoreCase));
        _batchIndex = activeIndex >= 0 ? activeIndex : Math.Clamp(_batchIndex, 0, _batchFiles.Count - 1);
        _thumbSelectionAnchor = _batchIndex;
        _batchInfoLabel.Text = _isEditing
            ? _batchInfoLabel.Text
            : _batchFiles.Count == 1
                ? UiLocalization.T("Ready to import. You can move and resize the image on stage later.")
                : string.Format(UiLocalization.T("Import {0} images. Common settings apply to the batch; crop and flip affect only the current image. Use the arrows or grid to review images."), _batchFiles.Count);
        if (_thumbItems.Count == _batchFiles.Count + targets.Count) RemoveThumbControls(targets);
        UpdateBatchChrome();
        SetBatchFile(_batchIndex);
        if (_stripMode)
        {
            // Removing N thumbnails must not rebuild every control: with hundreds of
            // images the full recreate is what made deletion stutter.
            if (_thumbItems.Count != _batchFiles.Count) RebuildStrip();
            AnimateStripReflow(previousLocations);
            // A removal disposes the control that held focus (the remove badge or the
            // thumbnail itself). Without moving focus back the next wheel gesture lands
            // outside the grid and the strip stops scrolling.
            FocusStripContent();
        }
        RefreshThumbSelection();
    }

    /// <summary>Removes only the deleted thumbnails and lets the flow layout re-pack.</summary>
    private void RemoveThumbControls(IReadOnlyList<int> indices)
    {
        _stripFlow.SuspendLayout();
        try
        {
            foreach (var index in indices.OrderByDescending(index => index))
            {
                if (index < 0 || index >= _thumbItems.Count) continue;
                var item = _thumbItems[index];
                _stripFlow.Controls.Remove(item);
                _thumbItems.RemoveAt(index);
                _thumbsByPath.Remove(item.FilePath);
                foreach (var key in _stripThumbs.Keys.Where(key => key.StartsWith(item.FilePath + "|", StringComparison.OrdinalIgnoreCase)).ToArray())
                    if (_stripThumbs.Remove(key, out var thumb)) thumb.Dispose();
                item.Dispose();
            }
        }
        finally { _stripFlow.ResumeLayout(true); }
        _stripFlow.Invalidate(true);
    }

    /// <summary>
    /// Slides every surviving thumbnail from the slot it occupied before a removal to the
    /// slot the flow layout assigned afterwards (ease-out, ~240 ms).
    /// </summary>
    private void AnimateStripReflow(IReadOnlyDictionary<string, Point> previousLocations)
    {
        // A removal landing while the previous slide is still running must first put that
        // slide to rest, otherwise the layouts it suspended stay suspended.
        FinishStripFill();
        if (!UiMotion.AnimationsEnabled || previousLocations.Count == 0 || _thumbItems.Count == 0) return;

        foreach (var item in _thumbItems)
        {
            if (!previousLocations.TryGetValue(item.FilePath, out var from)) continue;
            var to = item.Location;
            if (from == to) continue;
            var visible = new Rectangle(0, _stripScroll.ScrollPosition, _stripScroll.Width, _stripScroll.Height);
            if (!visible.IntersectsWith(new Rectangle(from, item.Size))
                && !visible.IntersectsWith(new Rectangle(to, item.Size))) continue;
            _stripFillMoves.Add(new StripFillMove(item, from, to));
        }
        if (_stripFillMoves.Count == 0) return;

        // A flow layout snaps a child straight back to its slot the instant Location is
        // written, so the slide is only visible while the flow's own layout stays suspended.
        _stripFlow.SuspendLayout();
        foreach (var move in _stripFillMoves) move.Item.Location = move.From;
        _stripFillStartedAt = Environment.TickCount64;
        _stripFillTimer.Start();
    }

    private void AdvanceStripFill()
    {
        var elapsed = Environment.TickCount64 - _stripFillStartedAt;
        var progress = Math.Clamp(elapsed / (double)StripFillDurationMs, 0d, 1d);
        // Ease-out cubic: fast start, gentle settle.
        var eased = 1d - Math.Pow(1d - progress, 3d);
        foreach (var move in _stripFillMoves)
        {
            if (move.Item.IsDisposed) continue;
            move.Item.Location = new Point(
                move.From.X + (int)Math.Round((move.To.X - move.From.X) * eased),
                move.From.Y + (int)Math.Round((move.To.Y - move.From.Y) * eased));
        }
        if (progress >= 1d) FinishStripFill();
    }

    /// <summary>Lands every in-flight slide on its final slot and releases the flow layout.</summary>
    private void FinishStripFill()
    {
        if (_stripFillMoves.Count == 0) return;
        _stripFillTimer.Stop();
        foreach (var move in _stripFillMoves)
            if (!move.Item.IsDisposed) move.Item.Location = move.To;
        _stripFillMoves.Clear();
        // Releasing the layout is what hands the final positions back to the flow panel.
        _stripFlow.ResumeLayout(true);
    }

    /// <summary>
    /// Moves focus back into the grid. A removal disposes the control that had it, and a
    /// wheel gesture is delivered to the focused control, so without this the strip stops
    /// scrolling as soon as one image is deleted.
    /// </summary>
    private void FocusStripContent()
    {
        if (!_stripMode || !Visible || IsDisposed) return;
        if (_stripScroll.CanFocus && _stripScroll.Focus()) return;
        if (_batchIndex >= 0 && _batchIndex < _thumbItems.Count
            && _thumbItems[_batchIndex].CanFocus && _thumbItems[_batchIndex].Focus()) return;
        if (_stripToggle.CanFocus) _stripToggle.Focus();
    }

    private ImageFilterMode ResolveDisplayFilter(string? path)
    {
        if (_displayMode.SelectedIndex == 1) return ImageFilterMode.Point;
        if (AutomaticDisplay) return path is not null && _detectedFilters.TryGetValue(path, out var detected)
            ? detected : ImageFilterMode.Bilinear;
        return _initialSettings.FilterMode == ImageFilterMode.Trilinear
            ? ImageFilterMode.Trilinear : ImageFilterMode.Bilinear;
    }

    private string ThumbnailKey(string path)
    {
        var settings = ReadSettingsForPath(path);
        return FormattableString.Invariant(
            $"{path}|{settings.FilterMode}|{settings.CropX:R}|{settings.CropY:R}|{settings.CropWidth:R}|{settings.CropHeight:R}|{settings.FlipHorizontal}|{settings.FlipVertical}|{settings.MaxSize}");
    }

    private async Task LoadStripThumbnailsAsync()
    {
        if (_batchFiles.Count == 0 || IsDisposed || _previewDisposed) return;
        var token = _previewCancellation.Token;
        var version = ++_stripVersion;
        var snapshot = _batchFiles.Select(file => (file.Path, Key: ThumbnailKey(file.Path),
            Settings: ReadSettingsForPath(file.Path))).ToArray();
        var completed = 0;
        foreach (var file in snapshot)
        {
            if (version != _stripVersion || token.IsCancellationRequested || IsDisposed) return;
            if (!_thumbsByPath.TryGetValue(file.Path, out var item)) continue;
            if (!_stripThumbs.TryGetValue(file.Key, out var thumb))
            {
                item.Loading = true;
                try
                {
                    thumb = await Task.Run(() => LoadThumbnailCore(file.Path, file.Settings, token, 112), token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception exception) when (exception is IOException or ArgumentException or OutOfMemoryException or NotSupportedException
                    or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
                {
                    AppLog.Warn($"Batch thumbnail unavailable: {file.Path}: {exception.Message}");
                }
                if (version != _stripVersion || token.IsCancellationRequested || IsDisposed)
                {
                    thumb?.Dispose();
                    return;
                }
                if (!_thumbsByPath.ContainsKey(file.Path)) { thumb?.Dispose(); continue; }
                if (thumb is not null) _stripThumbs[file.Key] = thumb;
            }
            item.Thumbnail = thumb;
            item.Loading = false;
            item.Invalidate();
            completed++;
            if (_stripMode || _stripRevealPending)
                _sizeHeaderLabel.Text = string.Format(UiLocalization.T("Loading previews: {0} / {1}"), completed, _batchFiles.Count);
            TryRevealStrip();
        }
        _sizeHeaderLabel.Text = $"{_sourcePixelWidth} × {_sourcePixelHeight} px";
    }

    private async Task LoadPreviewAsync()
    {
        if (IsDisposed || _previewDisposed) return;
        _previewTimer.Stop();
        _activePreviewCancellation?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_previewCancellation.Token);
        _activePreviewCancellation = request;
        var token = request.Token;
        var version = ++_previewVersion;
        var path = _sourcePath;
        try
        {
            if (AutomaticDisplay && path is not null && !_detectedFilters.ContainsKey(path))
            {
                var detected = await Task.Run(() => ImageDisplayDetector.Detect(path, token), token);
                if (IsDisposed || token.IsCancellationRequested || version != _previewVersion) return;
                _detectedFilters[path] = detected;
                RefreshSummary();
            }
            var settings = ReadSettings();
            var key = $"{_sourcePath}|{settings.FilterMode}|{settings.MaxSize}|{settings.CropX:0.####}|{settings.CropY:0.####}|{settings.CropWidth:0.####}|{settings.CropHeight:0.####}|{settings.FlipHorizontal}|{settings.FlipVertical}";
            Bitmap? preview;
            if (_previewCache.TryGetValue(key, out var cached))
            {
                preview = cached;
                if (IsDisposed || version != _previewVersion) return;
            }
            else
            {
                preview = await Task.Run(() => LoadThumbnail(path, settings, token), token);
                if (IsDisposed || token.IsCancellationRequested || version != _previewVersion) { preview?.Dispose(); return; }
                if (preview is not null) StoreCachedPreview(key, preview);
            }
            _previewCacheKey = key;
            _previewSource = preview;
            _previewOpaque?.Dispose();
            _previewOpaque = null;
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
            _preview.Invalidate();
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or OutOfMemoryException
            or OperationCanceledException or UnauthorizedAccessException or NotSupportedException
            or System.Runtime.InteropServices.ExternalException)
        {
            if (!IsDisposed && !token.IsCancellationRequested && version == _previewVersion)
            {
                if (AutomaticDisplay && path is not null) _detectedFilters.TryAdd(path, ImageFilterMode.Bilinear);
                _previewStatus = "Preview unavailable";
                RefreshSummary();
                _preview.Invalidate();
            }
        }
        finally
        {
            if (ReferenceEquals(_activePreviewCancellation, request)) _activePreviewCancellation = null;
        }
    }

    /// <summary>Keeps decoded previews for the batch so switching images does not re-decode files.</summary>
    private void StoreCachedPreview(string key, Bitmap preview)
    {
        _previewCache[key] = preview;
        _previewCacheOrder.Remove(key);
        _previewCacheOrder.Add(key);
        while (_previewCacheOrder.Count > 10)
        {
            var oldest = _previewCacheOrder.FirstOrDefault(candidate => candidate != _previewCacheKey);
            if (oldest is null) break;
            _previewCacheOrder.Remove(oldest);
            if (_previewCache.Remove(oldest, out var evicted)) evicted.Dispose();
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
            graphics.PixelOffsetMode = settings.FilterMode == ImageFilterMode.Point
                ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            graphics.TranslateTransform(settings.FlipHorizontal ? thumbnail.Width : 0, settings.FlipVertical ? thumbnail.Height : 0);
            graphics.ScaleTransform(settings.FlipHorizontal ? -1 : 1, settings.FlipVertical ? -1 : 1);
            graphics.DrawImage(source, new Rectangle(0, 0, thumbnail.Width, thumbnail.Height),
                settings.ResolveCropPixelBounds(source.Width, source.Height), GraphicsUnit.Pixel);
            token.ThrowIfCancellationRequested();
            return thumbnail;
        }
        catch { thumbnail.Dispose(); throw; }
    }

    private async Task EditImageAsync()
    {
        if (_cropEditor is not null || !_editImage.Enabled) return;
        _editImage.Enabled = false;
        _applyAction.Enabled = false;
        try
        {
            var token = _previewCancellation.Token;
            var path = _sourcePath;
            var fullSettings = ReadSettings() with { CropX = 0, CropY = 0, CropWidth = 1, CropHeight = 1,
                FlipHorizontal = false, FlipVertical = false,
                MaxSize = 8192, NonPowerOfTwoScale = false };
            var image = await Task.Run(() => LoadThumbnailCore(path, fullSettings, token, 2048));
            if (IsDisposed || token.IsCancellationRequested || !string.Equals(path, _sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                image?.Dispose();
                return;
            }
            if (image is null) { ShowImageEditError(); return; }
            _cropEditorImage = image;
            _cropEditor = new ImageCropDialog(image, new Size(_sourcePixelWidth, _sourcePixelHeight), ReadSettings(), _batchFiles.Count > 1 || _batchCount > 1);
            SuspendLayout();
            try
            {
                DialogContent.Controls.Add(_cropEditor);
                _cropEditor.BringToFront();
                _editReturnScrollPosition = _scroll.ScrollPosition;
                _scroll.Visible = false;
                SetDialogTitle(UiLocalization.T("Edit image"));
                _applyAction.Text = "Confirm edits";
                _cancelAction.Text = "Back";
                _toolTip.SetToolTip(_cancelAction, UiLocalization.T("Return to preview without importing"));
            }
            finally { ResumeLayout(true); }
            _cropEditor.SelectNextControl(null, true, true, true, false);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or OutOfMemoryException or OperationCanceledException or System.Runtime.InteropServices.ExternalException)
        {
            if (!IsDisposed)
            {
                if (_cropEditor is not null) FinishImageEdit(false);
                _cropEditorImage?.Dispose();
                _cropEditorImage = null;
                ShowImageEditError();
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _editImage.Enabled = true;
                _applyAction.Enabled = true;
            }
        }
    }

    private void ShowImageEditError()
    {
        _validation.Text = UiLocalization.T("The image could not be opened for editing.");
        _validation.Visible = true;
        _settingsLayout.RowStyles[_validationRow].Height = 24 * DeviceDpi / 96f;
    }

    private void FinishImageEdit(bool apply)
    {
        if (_cropEditor is not { } editor) return;
        editor.CancelInteraction();
        if (apply)
        {
            var crop = editor.Selection;
            _cropSettingsByPath[_sourcePath ?? string.Empty] = ReadSettings() with
            {
                CropX = crop.X / (float)_sourcePixelWidth, CropY = crop.Y / (float)_sourcePixelHeight,
                CropWidth = crop.Width / (float)_sourcePixelWidth, CropHeight = crop.Height / (float)_sourcePixelHeight,
                FlipHorizontal = editor.FlipHorizontal, FlipVertical = editor.FlipVertical
            };
        }
        SuspendLayout();
        DialogContent.Controls.Remove(editor);
        _cropEditor = null;
        editor.Dispose();
        _cropEditorImage?.Dispose();
        _cropEditorImage = null;
        _scroll.Visible = true;
        SetDialogTitle(_dialogTitle);
        _applyAction.Text = _isEditing ? "Apply" : "Import";
        _cancelAction.Text = "Cancel";
        _toolTip.SetToolTip(_cancelAction, null);
        ResumeLayout(true);
        _scroll.RestoreScrollPosition(_editReturnScrollPosition);
        if (apply)
        {
            _stripVersion++;
            foreach (var item in _thumbItems)
            {
                item.Thumbnail = null;
                item.Loading = true;
                item.Invalidate();
            }
            foreach (var thumbnail in _stripThumbs.Values) thumbnail.Dispose();
            _stripThumbs.Clear();
            RefreshSummary();
            QueuePreview();
            if (_stripMode || _stripRevealPending) _ = LoadStripThumbnailsAsync();
        }
        _editImage.Focus();
    }

    private void PaintPreview(object? sender, PaintEventArgs e)
    {
        if (_stripMode)
        {
            using var background = new SolidBrush(Theme.Panel);
            e.Graphics.FillRectangle(background, _preview.ClientRectangle);
            return;
        }
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
        var pointFilter = ReadSettings().FilterMode == ImageFilterMode.Point;
        e.Graphics.InterpolationMode = pointFilter ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        // Half keeps nearest-neighbour pixel art aligned to the pixel grid; the quality mode
        // is what keeps a scaled photo free of the half-pixel softness Half would add.
        e.Graphics.PixelOffsetMode = pointFilter ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
        e.Graphics.DrawImage(bitmap, new Rectangle((bounds.Width - size.Width) / 2, (bounds.Height - size.Height) / 2, size.Width, size.Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_previewDisposed)
        {
            _previewDisposed = true;
            _activePreviewCancellation?.Cancel();
            _previewCancellation.Cancel();
            _previewTimer.Dispose();
            _stripFillTimer.Dispose();
            _toolTip.Dispose();
            _cropEditor?.Dispose();
            _cropEditorImage?.Dispose();
            _previewOpaque?.Dispose();
            foreach (var bitmap in _previewCache.Values) bitmap.Dispose();
            _previewCache.Clear();
            _previewCacheOrder.Clear();
            _previewCancellation.Dispose();
            foreach (var item in _thumbItems) item.Dispose();
            foreach (var thumb in _stripThumbs.Values) thumb.Dispose();
            _stripThumbs.Clear();
        }
        base.Dispose(disposing);
    }

    /// <summary>Double-buffered preview surface; the paint handler covers the whole area so background erase is skipped.</summary>
    private sealed class PreviewPanel : Panel
    {
        public PreviewPanel() { DoubleBuffered = true; }

        protected override void OnPaintBackground(PaintEventArgs e) { /* PaintPreview draws the full checkerboard. */ }
    }

    /// <summary>Circular navigation button for the batch preview; keeps a visible surface on any backdrop.</summary>
    private sealed class PreviewNavButton : Control
    {
        private readonly SvgIconKind _icon;
        private bool _hovered;

        public PreviewNavButton(SvgIconKind icon, string accessibleName)
        {
            _icon = icon;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Size = new Size(36, 36);
            AccessibleName = accessibleName;
            AccessibleRole = AccessibleRole.PushButton;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Focus(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Enabled && e.KeyCode is Keys.Enter or Keys.Space) { e.SuppressKeyPress = true; OnClick(EventArgs.Empty); }
        }

        private Rectangle CircleBounds()
        {
            var inset = Math.Max(1, (int)Math.Round(2f * DeviceDpi / 96f));
            return new Rectangle(inset, inset, Math.Max(1, Width - inset * 2), Math.Max(1, Height - inset * 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            // Paint the preview underneath the button instead of clipping the button with a
            // Region. A region is a hard binary clip, so it cuts the anti-aliased circle edge
            // and leaves the visible stair-stepping the navigation buttons used to show.
            if (Parent is { } parent)
            {
                var state = graphics.Save();
                try
                {
                    graphics.TranslateTransform(-Left, -Top);
                    var parentArgs = new PaintEventArgs(graphics, Bounds);
                    InvokePaintBackground(parent, parentArgs);
                    InvokePaint(parent, parentArgs);
                }
                finally { graphics.Restore(state); }
            }
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var circle = CircleBounds();
            using (var surface = new SolidBrush(_hovered && Enabled ? Theme.PanelHover : Theme.PanelStrong))
                graphics.FillEllipse(surface, circle);
            using (var border = new Pen(_hovered && Enabled ? Theme.Accent : Theme.Border, Math.Max(1f, DeviceDpi / 96f)))
                graphics.DrawEllipse(border, circle);
            var color = !Enabled ? Theme.DisabledText : _hovered ? Theme.AccentLabel : Theme.Text;
            // Sized from the shared icon metrics and blitted 1:1 so the chevron stays crisp.
            var iconSize = Math.Max(8, (int)Math.Round(Theme.IconButtonSize * DeviceDpi / 96f));
            var bounds = new Rectangle((Width - iconSize) / 2, (Height - iconSize) / 2, iconSize, iconSize);
            using var bitmap = SvgIcons.CreateBitmap(_icon, new Size(iconSize, iconSize), color, DeviceDpi);
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImage(bitmap, bounds);
            if (Focused) Theme.DrawKeyboardFocus(graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
        }
    }

    /// <summary>One thumbnail sliding to a new slot during the delete-fill animation.</summary>
    private readonly record struct StripFillMove(Control Item, Point From, Point To);

    /// <summary>One selectable thumbnail in the batch strip; owns no image, shares the strip cache.</summary>
    private sealed class BatchThumbItem : Control
    {
        private Bitmap? _thumbnail;
        private ThumbRemoveButton? _removeButton;
        private bool _hovered;

        public BatchThumbItem(string filePath, int size)
        {
            FilePath = filePath;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            Size = new Size(size, size);
            BackColor = Theme.Panel;
            Cursor = Cursors.Hand;
            AccessibleName = System.IO.Path.GetFileName(filePath);
        }

        public string FilePath { get; }
        public bool Selected { get; set; }
        public bool Loading { get; set; }
        /// <summary>Pixel-art sources keep hard edges; smooth sources use the quality filter.</summary>
        public bool PointFilter { get; set; }
        public Bitmap? Thumbnail { get => _thumbnail; set { _thumbnail = value; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Focus(); }

        public void AttachRemoveButton(ThumbRemoveButton button)
        {
            _removeButton = button;
            button.Visible = false;
            Controls.Add(button);
            button.MouseEnter += (_, _) => { _hovered = true; RefreshRemoveVisibility(); };
            button.MouseLeave += (_, _) => RefreshHover();
            button.Enter += (_, _) => RefreshRemoveVisibility();
            button.Leave += (_, _) => RefreshHover();
            RefreshRemoveVisibility();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            RefreshRemoveVisibility();
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); RefreshHover(); }
        protected override void OnEnter(EventArgs e) { base.OnEnter(e); RefreshRemoveVisibility(); }
        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            RefreshHover();
            if (IsHandleCreated && !IsDisposed && !Disposing) BeginInvoke(RefreshRemoveVisibility);
        }

        private void RefreshHover()
        {
            _hovered = IsHandleCreated && ClientRectangle.Contains(PointToClient(Cursor.Position));
            RefreshRemoveVisibility();
        }

        private void RefreshRemoveVisibility()
        {
            if (IsDisposed || _removeButton is null || _removeButton.IsDisposed) return;
            _removeButton.Visible = _hovered || ContainsFocus;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            // The strip's panel colour shows through the rounded corners. Filling the square
            // corners with it reproduces what a clip did, while the rounded edge itself keeps
            // its anti-aliasing instead of being sheared off by a Region.
            using (var background = new SolidBrush(Parent?.BackColor ?? Theme.Panel))
                graphics.FillRectangle(background, ClientRectangle);
            var radius = Theme.UsesRoundedCorners ? 5f * DeviceDpi / 96f : 0f;
            var outlineBounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using var outline = CreateOutline(outlineBounds, radius);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (var field = new SolidBrush(Theme.Field))
            {
                if (radius > 0) graphics.FillPath(field, outline);
                else graphics.FillRectangle(field, 0, 0, Width, Height);
            }
            if (_thumbnail is not null)
            {
                // Pixel art keeps hard, readable edges; everything else gets the quality
                // resampler with a sub-pixel offset so the scaled image stays smooth
                // instead of picking up stair-stepped edges.
                graphics.InterpolationMode = PointFilter
                    ? InterpolationMode.NearestNeighbor
                    : InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PointFilter ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
                var scale = Math.Min((Width - 8f) / _thumbnail.Width, (Height - 8f) / _thumbnail.Height);
                var size = new Size(Math.Max(1, (int)(_thumbnail.Width * scale)), Math.Max(1, (int)(_thumbnail.Height * scale)));
                var target = new Rectangle((Width - size.Width) / 2, (Height - size.Height) / 2, size.Width, size.Height);
                if (radius > 0)
                {
                    // Same outline the frame uses, so the bitmap cannot spill over the corners.
                    var clip = graphics.Save();
                    graphics.SetClip(outline, CombineMode.Intersect);
                    graphics.DrawImage(_thumbnail, target);
                    graphics.Restore(clip);
                }
                else graphics.DrawImage(_thumbnail, target);
            }
            else if (Loading)
            {
                // Placeholder feedback while the batch thumbnail decodes in the background.
                using var dot = new SolidBrush(Theme.Muted);
                var dotRadius = Math.Max(2, (int)Math.Round(Width / 22f));
                var startX = (Width - dotRadius * 5) / 2;
                for (var i = 0; i < 3; i++)
                    graphics.FillEllipse(dot, startX + i * dotRadius * 2, Height / 2 - dotRadius, dotRadius, dotRadius);
            }
            else
            {
                TextRenderer.DrawText(graphics, UiLocalization.T("Preview unavailable"), Font, ClientRectangle,
                    Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var border = new Pen(Selected ? Theme.Accent : Theme.Border, Selected ? 2f : 1f);
            if (radius > 0) graphics.DrawPath(border, outline);
            else graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        private static System.Drawing.Drawing2D.GraphicsPath CreateOutline(RectangleF bounds, float radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            var diameter = radius * 2f;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class ThumbRemoveButton : Control
    {
        private bool _hovered;

        public ThumbRemoveButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Size = new Size(20, 20);
            AccessibleName = "Remove image";
            AccessibleRole = AccessibleRole.PushButton;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; Invalidate(); }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Focus(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Enabled && e.KeyCode is Keys.Enter or Keys.Space) { e.SuppressKeyPress = true; OnClick(EventArgs.Empty); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            // Repaint what sits behind the badge first: the control is a plain rectangle, so
            // this is what makes the round shape read as transparent on the thumbnail.
            if (Parent is { } parent)
            {
                var state = graphics.Save();
                try
                {
                    graphics.TranslateTransform(-Left, -Top);
                    var parentArgs = new PaintEventArgs(graphics, Bounds);
                    InvokePaintBackground(parent, parentArgs);
                    InvokePaint(parent, parentArgs);
                }
                finally { graphics.Restore(state); }
            }
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
            using (var surface = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window
                : _hovered || Capture ? Theme.PanelHover : Theme.PanelStrong))
            using (var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText
                : _hovered ? Theme.Accent : Theme.ControlOutline, Math.Max(1f, DeviceDpi / 96f)))
                Theme.PaintUiSurface(graphics, surface, border, bounds);
            var color = !Enabled ? Theme.DisabledText : _hovered ? Theme.AccentLabel : Theme.Text;
            var arm = Math.Min(Width, Height) * 0.18f;
            var centerX = Width / 2f;
            var centerY = Height / 2f;
            using var cross = new Pen(color, Math.Max(1f, 1.2f * DeviceDpi / 96f))
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(cross, centerX - arm, centerY - arm, centerX + arm, centerY + arm);
            graphics.DrawLine(cross, centerX + arm, centerY - arm, centerX - arm, centerY + arm);
            if (Focused) Theme.DrawKeyboardFocus(graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
        }
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
        label.UseMnemonic = false;
    }

    private static Label AddText(TableLayoutPanel layout, string text, int height, Color color)
    {
        var label = new Label();
        ConfigureLabel(label, color);
        label.Text = text;
        label.AutoEllipsis = true;
        AddFullWidthRow(layout, label, height);
        return label;
    }

    private void AddField(TableLayoutPanel layout, string text, Control input, string description, int column, int row)
    {
        var label = new Label();
        ConfigureLabel(label, Theme.DisabledText);
        label.Text = text;
        label.AccessibleDescription = UiLocalization.T(description);
        input.AccessibleDescription = UiLocalization.T(description);
        _toolTip.SetToolTip(label, UiLocalization.T(description));
        label.AutoEllipsis = true;
        label.Margin = new Padding(column == 0 ? 0 : 10, 0, column == 0 ? 10 : 0, 0);
        layout.Controls.Add(label, column, row * 2);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(column == 0 ? 0 : 10, 3, column == 0 ? 10 : 0, 8);
        if (input is ComboBox)
        {
            var host = new Panel { Dock = DockStyle.Fill, Margin = input.Margin, BackColor = Theme.Panel };
            input.Dock = DockStyle.None;
            input.Margin = Padding.Empty;
            host.Controls.Add(input);
            host.Layout += (_, _) => input.SetBounds(0, Math.Max(0, (host.Height - input.Height) / 2), host.Width, input.Height);
            layout.Controls.Add(host, column, row * 2 + 1);
        }
        else layout.Controls.Add(input, column, row * 2 + 1);
    }

    private static ModernNumericUpDown Numeric(string name, decimal min, decimal max, decimal value, int decimals, decimal increment)
        => new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals,
            Increment = increment, AccessibleName = name, AccessibleDescription = name };
}
