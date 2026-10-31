namespace VectorAnimationEngine;

internal sealed partial class DocumentInfoPanel
{
    private bool _playbackViewExpanded;
    private int PlaybackViewHeight => _playbackViewExpanded ? 272 : 48;
    private readonly SvgIconButton _enterPlaybackTitle = new(SvgIconKind.ChevronRight)
        { IsDisclosureHeader = true, Text = "Playback settings", AccessibleName = "Playback settings" };
    private readonly Label _enterFitCaption = Caption("Playback fit mode");
    private readonly TableLayoutPanel _enterFitRow = new()
    {
        ColumnCount = 2, RowCount = 1, Margin = Padding.Empty,
        BackColor = Theme.Panel
    };
    private readonly ComboBox _enterFit = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _enterScaleCaption = Caption("Playback scale (%)");
    private readonly Label _enterMarginCaption = Caption("Playback margin (px)");
    private readonly ModernNumericUpDown _enterScale = new()
        { Minimum = 10, Maximum = 400, Value = 100, DecimalPlaces = 0, Suffix = "%", ShowSuffix = true };
    private readonly ModernNumericUpDown _enterMargin = new()
        { Minimum = 0, Maximum = 160, Value = 24, DecimalPlaces = 0, Suffix = "px", ShowSuffix = true };
    private readonly ModernToggleSwitch _enterRestore = new() { Text = "Restore view after playback", Checked = true };
    private readonly Label _enterViewHint = new()
    {
        Text = "Playback view preferences apply only to Enter.",
        Font = Theme.UiFont(), ForeColor = Theme.PanelSecondaryText,
        BackColor = Color.Transparent, TextAlign = ContentAlignment.TopLeft, UseMnemonic = false
    };
    private bool _updatingPlaybackView;

    internal event EventHandler? PlaybackViewChanged;
    internal EnterPlaybackViewSettings PlaybackView => new()
    {
        Mode = (EnterPlaybackFitMode)Math.Max(0, _enterFit.SelectedIndex),
        ScalePercent = (int)_enterScale.Value,
        Margin = (int)_enterMargin.Value,
        RestoreView = _enterRestore.Checked
    };

    private void BuildPlaybackViewControls()
    {
        Theme.StyleStandardButton(_enterPlaybackTitle);
        _enterPlaybackTitle.TextAlign = ContentAlignment.MiddleLeft;
        _enterPlaybackTitle.Click += (_, _) =>
        {
            _playbackViewExpanded = !_playbackViewExpanded;
            UpdatePlaybackViewDisclosure();
        };
        Theme.StyleComboBox(_enterFit);
        Theme.StyleComboBoxSelection(_enterFit, Theme.InspectorLabelFontSize, Theme.ControlHeight);
        _enterFitRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        _enterFitRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        _enterFitRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _enterFitCaption.Dock = DockStyle.Fill;
        _enterFitCaption.Margin = Padding.Empty;
        _enterFit.Dock = DockStyle.Fill;
        _enterFit.Margin = Padding.Empty;
        _enterFitRow.Controls.Add(_enterFitCaption, 0, 0);
        _enterFitRow.Controls.Add(_enterFit, 1, 0);
        _enterFit.Items.AddRange(new[] { "Keep current view", "Fit playback view", "Fill playback view", "Custom playback scale" }
            .Select(text => new PlaybackFitChoice(text)).ToArray());
        _enterFit.SelectedIndex = 0;
        Theme.StyleNumeric(_enterScale);
        Theme.StyleNumeric(_enterMargin);
        _enterScale.Font = Theme.UiFont(11f);
        _enterMargin.Font = Theme.UiFont(11f);
        _enterFit.AccessibleName = "Playback fit mode";
        _enterScale.AccessibleName = "Playback scale (%)";
        _enterMargin.AccessibleName = "Playback margin (px)";
        _enterRestore.AccessibleName = "Restore view after playback";
        _enterViewHint.AutoEllipsis = false;
        Controls.AddRange([_enterPlaybackTitle, _enterFitRow, _enterScaleCaption,
            _enterMarginCaption, _enterScale, _enterMargin, _enterRestore, _enterViewHint]);
        _enterFit.SelectedIndexChanged += (_, _) => RaisePlaybackViewChanged();
        _enterScale.ValueChanged += (_, _) => RaisePlaybackViewChanged();
        _enterMargin.ValueChanged += (_, _) => RaisePlaybackViewChanged();
        _enterRestore.CheckedChanged += (_, _) => RaisePlaybackViewChanged();
        UpdatePlaybackViewEnabled();
        UpdatePlaybackViewDisclosure();
    }

    private void UpdatePlaybackViewDisclosure()
    {
        SuspendLayout();
        try
        {
            foreach (var control in new Control[] { _enterFitRow, _enterScaleCaption,
                _enterMarginCaption, _enterScale, _enterMargin, _enterRestore, _enterViewHint })
                control.Visible = _playbackViewExpanded;
            _enterPlaybackTitle.Icon = _playbackViewExpanded ? SvgIconKind.ChevronDown : SvgIconKind.ChevronRight;
            _enterPlaybackTitle.AccessibleDescription = UiLocalization.T(_playbackViewExpanded ? "Expanded" : "Collapsed");
            Height = PreferredHeight;
        }
        finally { ResumeLayout(performLayout: true); }
        Invalidate();
    }

    internal void SetPlaybackView(EnterPlaybackViewSettings settings)
    {
        settings = settings.Normalized();
        _updatingPlaybackView = true;
        try
        {
            _enterFit.SelectedIndex = (int)settings.Mode;
            _enterScale.Value = settings.ScalePercent;
            _enterMargin.Value = settings.Margin;
            _enterRestore.Checked = settings.RestoreView;
            UpdatePlaybackViewEnabled();
        }
        finally { _updatingPlaybackView = false; }
    }

    private void RaisePlaybackViewChanged()
    {
        UpdatePlaybackViewEnabled();
        if (!_updatingPlaybackView) PlaybackViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePlaybackViewEnabled()
    {
        _enterScale.Enabled = _enterFit.SelectedIndex != (int)EnterPlaybackFitMode.KeepCurrent;
        _enterMargin.Enabled = _enterFit.SelectedIndex is (int)EnterPlaybackFitMode.Fit or (int)EnterPlaybackFitMode.Fill;
    }

    private void LayoutPlaybackViewControls(int inset, int width, int top)
    {
        var gap = ScaleLogical(16);
        var half = Math.Max(1, (width - gap) / 2);
        void Place(Control control, int offset, int height, int left = 0, int? controlWidth = null) =>
            control.SetBounds(inset + left, ScaleLogical(top + offset), controlWidth ?? width, ScaleLogical(height));
        Place(_enterPlaybackTitle, 0, Theme.ControlHeight);
        _enterFitRow.Padding = new Padding(ScaleLogical(4), 0, 0, 0);
        Place(_enterFitRow, 40, Theme.ControlHeight);
        Place(_enterScaleCaption, 84, 24, controlWidth: half);
        Place(_enterMarginCaption, 84, 24, half + gap, half);
        Place(_enterScale, 112, Theme.ControlHeight, controlWidth: half);
        Place(_enterMargin, 112, Theme.ControlHeight, half + gap, half);
        Place(_enterRestore, 160, Theme.ControlHeight);
        Place(_enterViewHint, 204, 48);
    }

    private sealed record PlaybackFitChoice(string Text)
    {
        public override string ToString() => UiLocalization.T(Text);
    }
}
