namespace VectorAnimationEngine;

internal sealed class SettingsDialog : ModernDialogForm
{
    private readonly Button _englishLanguage = new() { Text = "English" };
    private readonly Button _chineseLanguage = new() { Text = "简体中文" };
    private readonly Button _darkColorTheme = new() { Text = "Dark" };
    private readonly Button _whiteColorTheme = new() { Text = "White" };
    private readonly ColorAdjustmentControl _themeColorAdjustment;
    private readonly ColorAdjustmentControl _highlightColorAdjustment;
    private readonly ShortcutProfileEditorPanel _shortcutProfiles;
    private UiLanguage _selectedLanguage;
    private ApplicationColorTheme _selectedColorTheme;
    private int _selectedThemeHueDegrees;
    private int _selectedThemeSaturationPercent;
    private int _selectedThemeBrightnessPercent;
    private int _selectedAccentHueDegrees;
    private int _selectedAccentSaturationPercent;
    private int _selectedAccentBrightnessPercent;

    public SettingsDialog()
        : this(
            ShortcutProfiles.TraditionalFlashProfileId,
            [],
            UiLanguage.English,
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultAccentHueDegrees)
    {
    }

    public SettingsDialog(
        string activeShortcutProfileId,
        IEnumerable<ShortcutProfileRecord>? customShortcutProfiles,
        UiLanguage selectedLanguage)
        : this(
            activeShortcutProfileId,
            customShortcutProfiles,
            selectedLanguage,
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultAccentHueDegrees)
    {
    }

    public SettingsDialog(
        string activeShortcutProfileId,
        IEnumerable<ShortcutProfileRecord>? customShortcutProfiles,
        UiLanguage selectedLanguage,
        int selectedThemeHueDegrees,
        int selectedAccentHueDegrees)
        : this(
            activeShortcutProfileId,
            customShortcutProfiles,
            selectedLanguage,
            Theme.ColorTheme,
            selectedThemeHueDegrees,
            selectedAccentHueDegrees)
    {
    }

    public SettingsDialog(
        string activeShortcutProfileId,
        IEnumerable<ShortcutProfileRecord>? customShortcutProfiles,
        UiLanguage selectedLanguage,
        ApplicationColorTheme selectedColorTheme,
        int selectedThemeHueDegrees,
        int selectedAccentHueDegrees)
        : this(
            activeShortcutProfileId,
            customShortcutProfiles,
            selectedLanguage,
            selectedColorTheme,
            selectedThemeHueDegrees,
            ApplicationSettings.DefaultThemeSaturationPercent,
            ApplicationSettings.DefaultThemeBrightnessPercent,
            selectedAccentHueDegrees,
            ApplicationSettings.DefaultAccentSaturationPercent,
            ApplicationSettings.DefaultAccentBrightnessPercent)
    {
    }

    public SettingsDialog(
        string activeShortcutProfileId,
        IEnumerable<ShortcutProfileRecord>? customShortcutProfiles,
        UiLanguage selectedLanguage,
        ApplicationColorTheme selectedColorTheme,
        int selectedThemeHueDegrees,
        int selectedThemeSaturationPercent,
        int selectedThemeBrightnessPercent,
        int selectedAccentHueDegrees,
        int selectedAccentSaturationPercent,
        int selectedAccentBrightnessPercent)
        : base("Settings", new Size(760, 700))
    {
        _selectedLanguage = selectedLanguage;
        _selectedColorTheme = Enum.IsDefined(selectedColorTheme)
            ? selectedColorTheme
            : ApplicationColorTheme.Dark;
        _selectedThemeHueDegrees = ApplicationSettingsStore.NormalizeHueDegrees(selectedThemeHueDegrees);
        _selectedThemeSaturationPercent = ApplicationSettingsStore.NormalizeSaturationPercent(selectedThemeSaturationPercent);
        _selectedThemeBrightnessPercent = ApplicationSettingsStore.NormalizeBrightnessPercent(selectedThemeBrightnessPercent);
        _selectedAccentHueDegrees = ApplicationSettingsStore.NormalizeHueDegrees(selectedAccentHueDegrees);
        _selectedAccentSaturationPercent = ApplicationSettingsStore.NormalizeSaturationPercent(selectedAccentSaturationPercent);
        _selectedAccentBrightnessPercent = ApplicationSettingsStore.NormalizeBrightnessPercent(selectedAccentBrightnessPercent);
        _themeColorAdjustment = new ColorAdjustmentControl(
            "Theme color",
            "Application theme color",
            _selectedThemeHueDegrees,
            _selectedThemeSaturationPercent,
            _selectedThemeBrightnessPercent);
        _highlightColorAdjustment = new ColorAdjustmentControl(
            "Highlight color",
            "Application highlight color",
            _selectedAccentHueDegrees,
            _selectedAccentSaturationPercent,
            _selectedAccentBrightnessPercent);
        _themeColorAdjustment.ValueChanged += (_, _) => ThemeColorAdjustmentChanged();
        _highlightColorAdjustment.ValueChanged += (_, _) => HighlightColorAdjustmentChanged();
        _shortcutProfiles = new ShortcutProfileEditorPanel(activeShortcutProfileId, customShortcutProfiles)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };

        AccessibleName = "Application settings";
        DialogContent.Padding = Padding.Empty;

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 7
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        DialogContent.Controls.Add(content);

        content.Controls.Add(SectionHeading("Appearance"), 0, 0);
        var colorThemeSelector = CreateColorThemeSelector();
        ConfigureSelectionButton(_darkColorTheme, "Use the dark color theme");
        ConfigureSelectionButton(_whiteColorTheme, "Use the white color theme");
        _darkColorTheme.Margin = new Padding(0, 2, 2, 6);
        _whiteColorTheme.Margin = new Padding(2, 2, 0, 6);
        _darkColorTheme.Click += (_, _) => SelectColorTheme(ApplicationColorTheme.Dark);
        _whiteColorTheme.Click += (_, _) => SelectColorTheme(ApplicationColorTheme.White);
        colorThemeSelector.Controls.Add(_darkColorTheme, 1, 0);
        colorThemeSelector.Controls.Add(_whiteColorTheme, 2, 0);
        content.Controls.Add(colorThemeSelector, 0, 1);
        content.Controls.Add(CreateColorAdjustmentLayout(), 0, 2);

        content.Controls.Add(SectionHeading("Language"), 0, 3);
        var languageSelector = CreateSegmentedSelector();
        ConfigureSelectionButton(_englishLanguage, "Use the English interface");
        ConfigureSelectionButton(_chineseLanguage, "使用简体中文界面");
        _englishLanguage.Margin = new Padding(0, 0, 2, 0);
        _chineseLanguage.Margin = new Padding(2, 0, 0, 0);
        _englishLanguage.Click += (_, _) => SelectLanguage(UiLanguage.English);
        _chineseLanguage.Click += (_, _) => SelectLanguage(UiLanguage.SimplifiedChinese);
        languageSelector.Controls.Add(_englishLanguage, 0, 0);
        languageSelector.Controls.Add(_chineseLanguage, 1, 0);
        content.Controls.Add(languageSelector, 0, 4);

        content.Controls.Add(SectionHeading("Shortcut profiles"), 0, 5);
        content.Controls.Add(_shortcutProfiles, 0, 6);

        var save = AddDialogAction("Save", DialogResult.OK, DialogActionStyle.Primary);
        var cancel = AddDialogAction("Cancel", DialogResult.Cancel);
        AcceptButton = save;
        CancelButton = cancel;

        SelectColorTheme(_selectedColorTheme);
        SelectLanguage(_selectedLanguage);
        UiLocalization.Watch(this);
    }

    // Retained for callers that still construct settings from the legacy preset enum.
    public SettingsDialog(
        ToolShortcutPreset selectedPreset,
        UiLanguage selectedLanguage,
        int selectedThemeHueDegrees,
        int selectedAccentHueDegrees)
        : this(
            ShortcutProfiles.BuiltInProfileId(selectedPreset),
            [],
            selectedLanguage,
            selectedThemeHueDegrees,
            selectedAccentHueDegrees)
    {
    }

    public string SelectedShortcutProfileId => _shortcutProfiles.ActiveProfileId;
    public ShortcutProfileRecord[] CustomShortcutProfiles => _shortcutProfiles.CustomProfiles;
    public UiLanguage SelectedLanguage => _selectedLanguage;
    public ApplicationColorTheme SelectedColorTheme => _selectedColorTheme;
    public int SelectedThemeHueDegrees => _selectedThemeHueDegrees;
    public int SelectedThemeSaturationPercent => _selectedThemeSaturationPercent;
    public int SelectedThemeBrightnessPercent => _selectedThemeBrightnessPercent;
    public int SelectedAccentHueDegrees => _selectedAccentHueDegrees;
    public int SelectedAccentSaturationPercent => _selectedAccentSaturationPercent;
    public int SelectedAccentBrightnessPercent => _selectedAccentBrightnessPercent;
    internal event EventHandler? ThemePreviewChanged;
    public ToolShortcutPreset SelectedPreset => ShortcutProfiles.LegacyPresetForProfile(
        SelectedShortcutProfileId,
        CustomShortcutProfiles);

    private TableLayoutPanel CreateColorAdjustmentLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _themeColorAdjustment.Margin = new Padding(0, 0, 4, 6);
        _highlightColorAdjustment.Margin = new Padding(4, 0, 0, 6);
        layout.Controls.Add(_themeColorAdjustment, 0, 0);
        layout.Controls.Add(_highlightColorAdjustment, 1, 0);
        return layout;
    }

    private static Label SectionHeading(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Theme.Text,
        BackColor = Theme.Panel,
        Font = Theme.UiFont(10.5f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static TableLayoutPanel CreateColorThemeSelector()
    {
        var selector = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        selector.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selector.Controls.Add(new Label
        {
            Text = "Color theme",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 0, 0);
        return selector;
    }

    private static TableLayoutPanel CreateSegmentedSelector()
    {
        var selector = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 2, 0, 6)
        };
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        selector.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return selector;
    }

    private static void ConfigureSelectionButton(Button button, string accessibleDescription)
    {
        button.Dock = DockStyle.Fill;
        button.AccessibleName = button.Text;
        button.AccessibleDescription = accessibleDescription;
        Theme.StyleButton(button);
    }

    private void SelectLanguage(UiLanguage language)
    {
        _selectedLanguage = language;
        if (language == UiLanguage.English)
        {
            Theme.StyleActiveButton(_englishLanguage);
            Theme.StyleButton(_chineseLanguage);
        }
        else
        {
            Theme.StyleButton(_englishLanguage);
            Theme.StyleActiveButton(_chineseLanguage);
        }
    }

    private void SelectColorTheme(ApplicationColorTheme colorTheme)
    {
        var changed = _selectedColorTheme != colorTheme;
        _selectedColorTheme = colorTheme;
        if (colorTheme == ApplicationColorTheme.White)
        {
            Theme.StyleButton(_darkColorTheme);
            Theme.StyleActiveButton(_whiteColorTheme);
        }
        else
        {
            Theme.StyleActiveButton(_darkColorTheme);
            Theme.StyleButton(_whiteColorTheme);
        }
        UpdateColorPreviews();
        if (changed) ThemePreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ThemeColorAdjustmentChanged()
    {
        _selectedThemeHueDegrees = _themeColorAdjustment.Hue;
        _selectedThemeSaturationPercent = _themeColorAdjustment.Saturation;
        _selectedThemeBrightnessPercent = _themeColorAdjustment.Brightness;
        UpdateColorPreviews();
        ThemePreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HighlightColorAdjustmentChanged()
    {
        _selectedAccentHueDegrees = _highlightColorAdjustment.Hue;
        _selectedAccentSaturationPercent = _highlightColorAdjustment.Saturation;
        _selectedAccentBrightnessPercent = _highlightColorAdjustment.Brightness;
        UpdateColorPreviews();
        ThemePreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateColorPreviews()
    {
        var palette = Theme.PaletteForAdjustments(
            _selectedColorTheme,
            _selectedThemeHueDegrees,
            _selectedThemeSaturationPercent,
            _selectedThemeBrightnessPercent,
            _selectedAccentHueDegrees,
            _selectedAccentSaturationPercent,
            _selectedAccentBrightnessPercent);
        _themeColorAdjustment.PreviewColor = palette.PanelStrong;
        _highlightColorAdjustment.PreviewColor = palette.Accent;
    }

    private sealed class ColorAdjustmentControl : UserControl
    {
        private enum Channel
        {
            Hue,
            Saturation,
            Brightness
        }

        private readonly ColorComponentSlider _hueSlider = new();
        private readonly ColorComponentSlider _saturationSlider = new();
        private readonly ColorComponentSlider _brightnessSlider = new();
        private readonly ModernNumericUpDown _hueInput = new();
        private readonly ModernNumericUpDown _saturationInput = new();
        private readonly ModernNumericUpDown _brightnessInput = new();
        private readonly HuePreviewSwatch _preview = new();
        private int _hue;
        private int _saturation;
        private int _brightness;
        private bool _updating;

        public ColorAdjustmentControl(
            string title,
            string accessiblePrefix,
            int hue,
            int saturation,
            int brightness)
        {
            _hue = ApplicationSettingsStore.NormalizeHueDegrees(hue);
            _saturation = ApplicationSettingsStore.NormalizeSaturationPercent(saturation);
            _brightness = ApplicationSettingsStore.NormalizeBrightnessPercent(brightness);
            Dock = DockStyle.Fill;
            BackColor = Theme.Panel;
            AccessibleName = title;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Controls.Add(layout);

            layout.Controls.Add(CreateHeader(title, accessiblePrefix), 0, 0);
            ConfigureSlider(
                _hueSlider,
                0,
                359,
                _hue,
                $"{accessiblePrefix} hue in degrees",
                "Adjust hue",
                Theme.HueSpectrumColor);
            ConfigureSlider(
                _saturationSlider,
                ApplicationSettings.MinimumSaturationPercent,
                ApplicationSettings.MaximumSaturationPercent,
                _saturation,
                $"{accessiblePrefix} saturation in percent",
                "Adjust saturation",
                amount => Theme.HslPreviewColor(
                    _hue,
                    amount,
                    Math.Clamp(_brightness / 200f, 0.18f, 0.82f)));
            ConfigureSlider(
                _brightnessSlider,
                ApplicationSettings.MinimumBrightnessPercent,
                ApplicationSettings.MaximumBrightnessPercent,
                _brightness,
                $"{accessiblePrefix} brightness in percent",
                "Adjust brightness",
                amount => Theme.HslPreviewColor(
                    _hue,
                    Math.Clamp(_saturation / 100f, 0f, 1f),
                    0.08f + amount * 0.84f));
            ConfigureInput(_hueInput, 0, 359, _hue, "°", $"{accessiblePrefix} hue in degrees", "Adjust hue");
            ConfigureInput(
                _saturationInput,
                ApplicationSettings.MinimumSaturationPercent,
                ApplicationSettings.MaximumSaturationPercent,
                _saturation,
                "%",
                $"{accessiblePrefix} saturation in percent",
                "Adjust saturation");
            ConfigureInput(
                _brightnessInput,
                ApplicationSettings.MinimumBrightnessPercent,
                ApplicationSettings.MaximumBrightnessPercent,
                _brightness,
                "%",
                $"{accessiblePrefix} brightness in percent",
                "Adjust brightness");
            layout.Controls.Add(CreateChannelRow("H", "Hue", _hueSlider, _hueInput, "°"), 0, 1);
            layout.Controls.Add(CreateChannelRow("S", "Saturation", _saturationSlider, _saturationInput, "%"), 0, 2);
            layout.Controls.Add(CreateChannelRow("B", "Brightness", _brightnessSlider, _brightnessInput, "%"), 0, 3);

            _hueSlider.ValueChanged += (_, _) => SetChannel(Channel.Hue, _hueSlider.Value);
            _saturationSlider.ValueChanged += (_, _) => SetChannel(Channel.Saturation, _saturationSlider.Value);
            _brightnessSlider.ValueChanged += (_, _) => SetChannel(Channel.Brightness, _brightnessSlider.Value);
            _hueInput.ValueChanged += (_, _) => SetChannel(Channel.Hue, (int)_hueInput.Value);
            _saturationInput.ValueChanged += (_, _) => SetChannel(Channel.Saturation, (int)_saturationInput.Value);
            _brightnessInput.ValueChanged += (_, _) => SetChannel(Channel.Brightness, (int)_brightnessInput.Value);
        }

        public event EventHandler? ValueChanged;
        public int Hue => _hue;
        public int Saturation => _saturation;
        public int Brightness => _brightness;
        public Color PreviewColor
        {
            get => _preview.SwatchColor;
            set => _preview.SwatchColor = value;
        }

        private TableLayoutPanel CreateHeader(string title, string accessiblePrefix)
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.Controls.Add(new Label
            {
                Text = title,
                Dock = DockStyle.Fill,
                ForeColor = Theme.Text,
                BackColor = Theme.Panel,
                Font = Theme.UiFont(9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = Padding.Empty
            }, 0, 0);
            _preview.AccessibleName = $"{accessiblePrefix} preview";
            _preview.Margin = new Padding(4, 3, 0, 3);
            header.Controls.Add(_preview, 1, 0);
            return header;
        }

        private static TableLayoutPanel CreateChannelRow(
            string symbol,
            string channelName,
            ColorComponentSlider slider,
            ModernNumericUpDown input,
            string unit)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                ColumnCount = 4,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.Controls.Add(new Label
            {
                Text = symbol,
                AccessibleName = channelName,
                Dock = DockStyle.Fill,
                ForeColor = Theme.Muted,
                BackColor = Theme.Panel,
                Font = Theme.UiFont(8.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty
            }, 0, 0);
            row.Controls.Add(slider, 1, 0);
            row.Controls.Add(input, 2, 0);
            row.Controls.Add(new Label
            {
                Text = unit,
                Dock = DockStyle.Fill,
                ForeColor = Theme.Muted,
                BackColor = Theme.Panel,
                Font = Theme.UiFont(8.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty
            }, 3, 0);
            return row;
        }

        private static void ConfigureSlider(
            ColorComponentSlider slider,
            int minimum,
            int maximum,
            int value,
            string accessibleName,
            string accessibleDescription,
            Func<float, Color> gradient)
        {
            slider.Minimum = minimum;
            slider.Maximum = maximum;
            slider.Value = value;
            slider.GradientColor = gradient;
            slider.Dock = DockStyle.Fill;
            slider.Margin = new Padding(3, 1, 3, 1);
            slider.AccessibleName = accessibleName;
            slider.AccessibleDescription = accessibleDescription;
        }

        private static void ConfigureInput(
            ModernNumericUpDown input,
            int minimum,
            int maximum,
            int value,
            string suffix,
            string accessibleName,
            string accessibleDescription)
        {
            input.Minimum = minimum;
            input.Maximum = maximum;
            input.Value = value;
            input.DecimalPlaces = 0;
            input.Increment = 1;
            input.Suffix = suffix;
            input.Dock = DockStyle.Fill;
            input.Margin = new Padding(3, 1, 0, 1);
            input.AccessibleName = accessibleName;
            input.AccessibleDescription = accessibleDescription;
            Theme.StyleNumeric(input);
        }

        private void SetChannel(Channel channel, int value)
        {
            if (_updating) return;
            var next = channel switch
            {
                Channel.Hue => ApplicationSettingsStore.NormalizeHueDegrees(value),
                Channel.Saturation => ApplicationSettingsStore.NormalizeSaturationPercent(value),
                _ => ApplicationSettingsStore.NormalizeBrightnessPercent(value)
            };
            var current = channel switch
            {
                Channel.Hue => _hue,
                Channel.Saturation => _saturation,
                _ => _brightness
            };
            if (current == next) return;

            switch (channel)
            {
                case Channel.Hue:
                    _hue = next;
                    break;
                case Channel.Saturation:
                    _saturation = next;
                    break;
                default:
                    _brightness = next;
                    break;
            }

            _updating = true;
            try
            {
                _hueSlider.Value = _hue;
                _hueInput.Value = _hue;
                _saturationSlider.Value = _saturation;
                _saturationInput.Value = _saturation;
                _brightnessSlider.Value = _brightness;
                _brightnessInput.Value = _brightness;
            }
            finally
            {
                _updating = false;
            }
            _saturationSlider.RefreshGradient();
            _brightnessSlider.RefreshGradient();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class HuePreviewSwatch : Control
    {
        private Color _swatchColor = Theme.Accent;

        public HuePreviewSwatch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Dock = DockStyle.Fill;
            AccessibleRole = AccessibleRole.Graphic;
        }

        public Color SwatchColor
        {
            get => _swatchColor;
            set
            {
                if (_swatchColor == value) return;
                _swatchColor = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            using var fill = new SolidBrush(_swatchColor);
            using var border = new Pen(Theme.BorderHover);
            e.Graphics.FillRectangle(fill, bounds);
            e.Graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        }
    }
}
