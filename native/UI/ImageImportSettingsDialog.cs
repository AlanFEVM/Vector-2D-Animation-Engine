namespace VectorAnimationEngine;

/// <summary>
/// Unity-style bitmap import settings. Every control maps to one member of
/// <see cref="BitmapImageImportSettings"/>, and the resulting value is re-validated by
/// <see cref="BitmapImageImportSettings.IsValid"/> before the dialog reports success.
/// </summary>
internal sealed class ImageImportSettingsDialog : ModernDialogForm
{
    private readonly string _sourceName;
    private readonly int _sourcePixelWidth;
    private readonly int _sourcePixelHeight;
    private readonly float _naturalPixelsPerUnit;

    private readonly ComboBox _filterMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ModernNumericUpDown _pixelsPerUnit = Numeric(
        "Pixels per unit",
        BitmapImageImportSettings.MinimumPixelsPerUnit,
        BitmapImageImportSettings.MaximumPixelsPerUnit,
        100,
        0,
        1);
    private readonly ModernNumericUpDown _maxSize = Numeric(
        "Max size (pixels)",
        BitmapImageImportSettings.MinimumMaxSize,
        BitmapImageImportSettings.MaximumMaxSize,
        BitmapImageImportSettings.Default.MaxSize,
        0,
        1);
    private readonly ComboBox _nonPowerOfTwo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _compression = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ModernNumericUpDown _compressionQuality = Numeric(
        "Compression quality",
        1,
        100,
        90,
        0,
        1);
    private readonly ComboBox _alphaSource = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _mipmaps = Check(
        "Generate mipmaps",
        "Build reduced-resolution copies for minified drawing",
        false);
    private readonly CheckBox _readWrite = Check(
        "Read/Write enabled",
        "Keep a CPU-readable copy of the decoded pixels",
        true);
    private readonly ModernNumericUpDown _pivotX = Numeric("Pivot X", 0, 1, 0.5m, 3, 0.05m);
    private readonly ModernNumericUpDown _pivotY = Numeric("Pivot Y", 0, 1, 0.5m, 3, 0.05m);
    private readonly Label _sourceSummary = new();
    private readonly Label _resultSummary = new();
    private readonly Label _validation = new();

    public ImageImportSettingsDialog(
        string title,
        string sourceName,
        int sourcePixelWidth,
        int sourcePixelHeight,
        float naturalPixelsPerUnit,
        BitmapImageImportSettings initialSettings)
        : base(title, new Size(560, 680))
    {
        _sourceName = sourceName;
        _sourcePixelWidth = Math.Max(1, sourcePixelWidth);
        _sourcePixelHeight = Math.Max(1, sourcePixelHeight);
        _naturalPixelsPerUnit = float.IsFinite(naturalPixelsPerUnit) && naturalPixelsPerUnit > 0
            ? naturalPixelsPerUnit
            : 96f;

        MinimumSize = new Size(480, 520);
        AccessibleName = title;
        ConfigureCombos();
        BuildUi();
        WireEvents();
        SetSettings(initialSettings);

        var apply = AddDialogAction("Apply", DialogResult.OK, DialogActionStyle.Primary, () => ValidateSettings().IsValid);
        var cancel = AddDialogAction("Cancel", DialogResult.Cancel);
        AcceptButton = apply;
        CancelButton = cancel;
        UiLocalization.Watch(this);
    }

    public BitmapImageImportSettings Settings => ReadSettings();

    private void ConfigureCombos()
    {
        _filterMode.Items.AddRange(["Point (no filter)", "Bilinear", "Trilinear"]);
        _filterMode.AccessibleName = "Filter mode";
        _filterMode.AccessibleDescription = "Choose how the image is sampled when scaled";
        Theme.StyleComboBox(_filterMode);

        _nonPowerOfTwo.Items.AddRange(["None", "ToNearest"]);
        _nonPowerOfTwo.AccessibleName = "Non power of two";
        _nonPowerOfTwo.AccessibleDescription = "Choose how a non-power-of-two image is resized on import";
        Theme.StyleComboBox(_nonPowerOfTwo);

        _compression.Items.AddRange(["Lossless (PNG)", "JPEG", "Raw"]);
        _compression.AccessibleName = "Compression";
        _compression.AccessibleDescription = "Choose the stored format of the managed copy";
        Theme.StyleComboBox(_compression);

        _alphaSource.Items.AddRange(["From input", "None"]);
        _alphaSource.AccessibleName = "Alpha source";
        _alphaSource.AccessibleDescription = "Choose whether the source alpha channel is kept";
        Theme.StyleComboBox(_alphaSource);
    }

    private void BuildUi()
    {
        DialogContent.Padding = Padding.Empty;
        var scroll = new ThemedScrollPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            AccessibleName = "Image import settings"
        };
        scroll.ContentPadding = new Padding(20, 14, 20, 18);
        DialogContent.Controls.Add(scroll);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 0,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Content.Controls.Add(layout);

        ConfigureSummaryLabel(_sourceSummary, Theme.Muted);
        ConfigureSummaryLabel(_resultSummary, Theme.AccentLabel);
        ConfigureSummaryLabel(_validation, Theme.Warning);
        _sourceSummary.Text = $"{_sourceName}  |  {_sourcePixelWidth} x {_sourcePixelHeight} px  |  {_naturalPixelsPerUnit:0.##} PPI";
        layout.Controls.Add(_sourceSummary, 0, layout.RowCount++);
        layout.SetColumnSpan(_sourceSummary, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.Controls.Add(_resultSummary, 0, layout.RowCount++);
        layout.SetColumnSpan(_resultSummary, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.Controls.Add(_validation, 0, layout.RowCount++);
        layout.SetColumnSpan(_validation, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        AddSection(layout, "Texture");
        AddField(layout, "Filter mode", _filterMode);
        AddField(layout, "Pixels per unit", _pixelsPerUnit);
        AddField(layout, "Max size", _maxSize);
        AddField(layout, "Non power of two", _nonPowerOfTwo);
        AddCheckField(layout, _mipmaps);

        AddSection(layout, "Compression");
        AddField(layout, "Format", _compression);
        AddField(layout, "Quality", _compressionQuality);

        AddSection(layout, "Alpha");
        AddField(layout, "Alpha source", _alphaSource);
        AddCheckField(layout, _readWrite);

        AddSection(layout, "Pivot");
        AddField(layout, "Pivot X (0-1)", _pivotX);
        AddField(layout, "Pivot Y (0-1)", _pivotY);
    }

    private void WireEvents()
    {
        foreach (var numeric in new[] { _pixelsPerUnit, _maxSize, _compressionQuality, _pivotX, _pivotY })
        {
            numeric.ValueChanged += (_, _) => RefreshSummary();
        }
        _filterMode.SelectedIndexChanged += (_, _) => RefreshSummary();
        _nonPowerOfTwo.SelectedIndexChanged += (_, _) => RefreshSummary();
        _compression.SelectedIndexChanged += (_, _) =>
        {
            UpdateDependentState();
            RefreshSummary();
        };
        _alphaSource.SelectedIndexChanged += (_, _) => RefreshSummary();
        _mipmaps.CheckedChanged += (_, _) => RefreshSummary();
        _readWrite.CheckedChanged += (_, _) => RefreshSummary();
    }

    private void UpdateDependentState()
    {
        // Raw storage is lossless by definition and JPEG is the only lossy option, so the
        // quality field only carries meaning for JPEG.
        _compressionQuality.Enabled = ReadSettings().Compression == ImageCompression.Jpeg;
    }

    private void SetSettings(BitmapImageImportSettings settings)
    {
        var normalized = settings.IsValid ? settings : BitmapImageImportSettings.Default;
        _filterMode.SelectedIndex = normalized.FilterMode switch
        {
            ImageFilterMode.Point => 0,
            ImageFilterMode.Trilinear => 2,
            _ => 1
        };
        _pixelsPerUnit.Value = normalized.PixelsPerUnit;
        _maxSize.Value = normalized.MaxSize;
        _nonPowerOfTwo.SelectedIndex = normalized.NonPowerOfTwoScale ? 1 : 0;
        _compression.SelectedIndex = normalized.Compression switch
        {
            ImageCompression.Jpeg => 1,
            ImageCompression.Raw => 2,
            _ => 0
        };
        _compressionQuality.Value = normalized.CompressionQuality;
        _alphaSource.SelectedIndex = normalized.AlphaSource == ImageAlphaSource.None ? 1 : 0;
        _mipmaps.Checked = normalized.Mipmaps;
        _readWrite.Checked = normalized.ReadWriteEnabled;
        _pivotX.Value = (decimal)Math.Round(normalized.PivotX, 3);
        _pivotY.Value = (decimal)Math.Round(normalized.PivotY, 3);
        UpdateDependentState();
        RefreshSummary();
    }

    private BitmapImageImportSettings ReadSettings()
    {
        var settings = new BitmapImageImportSettings
        {
            FilterMode = _filterMode.SelectedIndex switch
            {
                0 => ImageFilterMode.Point,
                2 => ImageFilterMode.Trilinear,
                _ => ImageFilterMode.Bilinear
            },
            Mipmaps = _mipmaps.Checked,
            PixelsPerUnit = (int)_pixelsPerUnit.Value,
            NonPowerOfTwoScale = _nonPowerOfTwo.SelectedIndex == 1,
            MaxSize = (int)_maxSize.Value,
            Compression = _compression.SelectedIndex switch
            {
                1 => ImageCompression.Jpeg,
                2 => ImageCompression.Raw,
                _ => ImageCompression.LosslessPng
            },
            CompressionQuality = (int)_compressionQuality.Value,
            ReadWriteEnabled = _readWrite.Checked,
            PivotX = (float)_pivotX.Value,
            PivotY = (float)_pivotY.Value,
            AlphaSource = _alphaSource.SelectedIndex == 1 ? ImageAlphaSource.None : ImageAlphaSource.FromInput,
            GenerateMipmapsForAlpha = _mipmaps.Checked
        };
        return settings;
    }

    private (bool IsValid, string Message) ValidateSettings()
    {
        var settings = ReadSettings();
        if (!settings.IsValid) return (false, "The import settings are out of range.");
        var stored = settings.ResolveStoredPixelSize(new SizeF(_sourcePixelWidth, _sourcePixelHeight));
        if (!BitmapImageFormats.TryValidateDecodedBudget(
                Math.Max(1, (int)stored.Width),
                Math.Max(1, (int)stored.Height),
                out var error))
        {
            return (false, error);
        }

        return (true, "");
    }

    private void RefreshSummary()
    {
        var settings = ReadSettings();
        var stored = settings.ResolveStoredPixelSize(new SizeF(_sourcePixelWidth, _sourcePixelHeight));
        var placed = settings.ResolvePlacedSize(stored, _naturalPixelsPerUnit);
        _resultSummary.Text = UiLocalization.T("Stored") + $": {(int)stored.Width} x {(int)stored.Height} px"
            + "   |   " + UiLocalization.T("Placed") + $": {placed.Width:0.##} x {placed.Height:0.##} u";
        var validation = ValidateSettings();
        _validation.Text = validation.IsValid ? "" : UiLocalization.T(validation.Message);
        _validation.ForeColor = validation.IsValid ? Theme.Muted : Theme.Warning;
        _validation.Invalidate();
    }

    private static void ConfigureSummaryLabel(Label label, Color color)
    {
        label.Dock = DockStyle.Fill;
        label.BackColor = Theme.Panel;
        label.ForeColor = color;
        label.Font = Theme.UiFont(8.8f);
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.AutoEllipsis = true;
        label.Margin = new Padding(0, 0, 0, 4);
    }

    private static void AddSection(TableLayoutPanel layout, string text)
    {
        var heading = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(0, 10, 0, 0),
            Padding = new Padding(0, 0, 0, 4)
        };
        layout.Controls.Add(heading, 0, layout.RowCount++);
        layout.SetColumnSpan(heading, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
    }

    private static void AddField(TableLayoutPanel layout, string label, Control input)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ForeColor = Theme.Muted,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 3, 10, 3)
        }, 0, row);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 3, 0, 3);
        layout.Controls.Add(input, 1, row);
    }

    private static void AddCheckField(TableLayoutPanel layout, CheckBox checkBox)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.Controls.Add(checkBox, 0, row);
        layout.SetColumnSpan(checkBox, 2);
    }

    private static ModernNumericUpDown Numeric(
        string accessibleName,
        decimal minimum,
        decimal maximum,
        decimal value,
        int decimalPlaces,
        decimal increment) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        DecimalPlaces = decimalPlaces,
        Increment = increment,
        Value = value,
        AccessibleName = accessibleName,
        AccessibleDescription = accessibleName,
        Suffix = accessibleName
    };

    private static CheckBox Check(string text, string description, bool value) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        Height = 28,
        ForeColor = Theme.Text,
        BackColor = Theme.Panel,
        FlatStyle = FlatStyle.Flat,
        Font = Theme.UiFont(),
        AccessibleName = text,
        AccessibleDescription = description,
        Margin = new Padding(0, 3, 0, 3)
    };
}
