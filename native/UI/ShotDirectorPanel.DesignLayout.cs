namespace VectorAnimationEngine;

internal sealed partial class ShotDirectorPanel
{
    private readonly Label _sceneCamerasCaption = DesignCaption("Scene cameras", true);
    private readonly Label _lensHeading = DesignCaption("Lens settings");
    private readonly Label _transformHeading = DesignCaption("Transform");
    private readonly Label _displayHeading = DesignCaption("Display & preview");
    private readonly CameraSectionToggle _lensDisclosure = new("Lens settings");
    private readonly CameraSectionToggle _transformDisclosure = new("Transform");
    private readonly CameraSectionToggle _displayDisclosure = new("Display & preview");
    private readonly Label _styleCaption = DesignCaption("Camera style", true);
    private readonly ComboBox _designStyleEditor = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Camera style" };
    private bool _updatingStyleSelection;
    private readonly Label _cameraStatus = DesignCaption("", true);
    private readonly Label _emptyCameras = DesignCaption("No camera yet", true);
    private bool _designedCameraInitialized;
    private bool _layingOutDesignedCamera;

    private void InitializeDesignedCamera()
    {
        _toolbar.Visible = false;
        InspectorModuleStyle.StylePageTitle(_title);
        _inspectorTitle.Visible = false;
        _inspector.ShowScrollBar = true;
        _simpleCameraStyle.Visible = false;
        _designStyleEditor.Items.AddRange([new CameraStyleItem("Simplified"), new CameraStyleItem("Standard")]);
        Theme.StyleComboBox(_designStyleEditor);
        Theme.StyleComboBoxSelection(_designStyleEditor, Font.Size, Theme.ControlHeight);
        _designStyleEditor.SelectedIndex = _simpleCameraStyle.Checked ? 0 : 1;
        _designStyleEditor.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingStyleSelection && _designStyleEditor.SelectedIndex >= 0)
                _simpleCameraStyle.Checked = _designStyleEditor.SelectedIndex == 0;
        };
        _simpleCameraStyle.CheckedChanged += (_, _) =>
        {
            _updatingStyleSelection = true;
            try { _designStyleEditor.SelectedIndex = _simpleCameraStyle.Checked ? 0 : 1; }
            finally { _updatingStyleSelection = false; }
        };
        _inspector.Content.Controls.AddRange([_lensHeading, _transformHeading, _displayHeading,
            _lensDisclosure, _transformDisclosure, _displayDisclosure, _styleCaption, _designStyleEditor]);
        foreach (var (heading, disclosure) in new[]
        {
            (_lensHeading, _lensDisclosure),
            (_transformHeading, _transformDisclosure),
            (_displayHeading, _displayDisclosure)
        })
            InitializeDesignedSection(heading, disclosure);
        Controls.AddRange([_sceneCamerasCaption, _cameraStatus, _emptyCameras,
            _addShotButton, _locateButton, _moveUpButton, _moveDownButton, _removeButton]);
        _emptyCameras.TextAlign = ContentAlignment.MiddleCenter;
        _addShotButton.TabIndex = 0;
        _addShotButton.AccessibleName = "New camera";
        _locateButton.AccessibleName = "Locate";
        _cameraList.TabIndex = 1;
        _locateButton.TabIndex = 2;
        _moveUpButton.TabIndex = 3;
        _moveDownButton.TabIndex = 4;
        _removeButton.TabIndex = 5;
        _previewButton.TabIndex = 6;
        _inspector.TabIndex = 7;
        _inspector.Content.SizeChanged += (_, _) => PerformLayout();
        _inspector.Content.Paint += (_, e) =>
        {
            foreach (var top in new[] { _transformHeading.Top, _displayHeading.Top, ParallaxEditor.Visible ? ParallaxEditor.Top : -1 })
                if (top > ScaleMetric(12)) InspectorModuleStyle.DrawDivider(_inspector.Content, e.Graphics, top - ScaleMetric(10));
        };
        _designedCameraInitialized = true;
    }

    private void InitializeDesignedSection(Label heading, CameraSectionToggle disclosure)
    {
        Theme.StyleButton(disclosure);
        heading.Cursor = Cursors.Hand;
        heading.Click += (_, _) =>
        {
            disclosure.Focus();
            disclosure.PerformClick();
        };
        disclosure.Click += (_, _) => PerformLayout();
    }

    private void LayoutDesignedCamera()
    {
        if (!_designedCameraInitialized || _layingOutDesignedCamera) return;
        _layingOutDesignedCamera = true;
        try
        {
        var inset = ScaleMetric(16);
        var width = Math.Max(1, ClientSize.Width - inset * 2);
        _title.SetBounds(inset + ScaleMetric(24), ScaleMetric(16), Math.Max(1, width - ScaleMetric(58)), ScaleMetric(28));
        _previewButton.SetBounds(Width - inset - ScaleMetric(28), ScaleMetric(16), ScaleMetric(28), ScaleMetric(28));
        _sceneCamerasCaption.Text = $"{UiLocalization.T("Scene cameras")}  {_state.Shots.Length}";
        var compactCommands = width < ScaleMetric(240);
        _addShotButton.Text = compactCommands ? string.Empty : UiLocalization.T("New camera");
        var addWidth = compactCommands ? ScaleMetric(32)
            : Math.Max(ScaleMetric(96), TextRenderer.MeasureText(_addShotButton.Text, _addShotButton.Font).Width
                + ScaleMetric(Theme.ControlHeight) + ScaleMetric(14));
        _sceneCamerasCaption.SetBounds(inset, ScaleMetric(66), Math.Max(1, width - addWidth - ScaleMetric(8)), ScaleMetric(28));
        _addShotButton.SetBounds(Width - inset - addWidth, ScaleMetric(64), addWidth, ScaleMetric(Theme.ControlHeight));
        _cameraList.ItemHeight = ScaleMetric(40);
        var visibleRows = Math.Clamp((Height - ScaleMetric(400)) / _cameraList.ItemHeight, 1, 3);
        var listHeight = Math.Clamp(_state.Shots.Length, 1, visibleRows) * _cameraList.ItemHeight;
        _cameraList.SetBounds(inset, ScaleMetric(104), width, listHeight);
        _emptyCameras.Bounds = _cameraList.Bounds;
        _emptyCameras.Visible = _state.Shots.Length == 0;
        _cameraList.Visible = _state.Shots.Length > 0;
        if (_emptyCameras.Visible) _emptyCameras.BringToFront();
        var commandsY = _cameraList.Bottom + ScaleMetric(4);
        _locateButton.SetBounds(inset, commandsY, compactCommands ? ScaleMetric(28) : Math.Max(1, width - ScaleMetric(104)), ScaleMetric(28));
        _locateButton.Text = compactCommands ? string.Empty : UiLocalization.T("Locate");
        _moveUpButton.SetBounds(Width - inset - ScaleMetric(92), commandsY, ScaleMetric(28), ScaleMetric(28));
        _moveDownButton.SetBounds(Width - inset - ScaleMetric(60), commandsY, ScaleMetric(28), ScaleMetric(28));
        _removeButton.SetBounds(Width - inset - ScaleMetric(28), commandsY, ScaleMetric(28), ScaleMetric(28));
        var inspectorTop = commandsY + ScaleMetric(40);
        var footerHeight = ScaleMetric(30);
        _inspector.SetBounds(0, inspectorTop, ClientSize.Width, Math.Max(1, Height - inspectorTop - footerHeight));
        _cameraStatus.Text = ActiveShot() is { } shot
            ? $"{shot.Name} · {UiLocalization.T(shot.Framing.Kind == SceneShotCameraKind.TwoD ? "2D Camera" : "3D Camera")}" : UiLocalization.T("No camera yet");
        _cameraStatus.SetBounds(inset, Math.Max(inspectorTop, Height - footerHeight), width, footerHeight);

        var right = Math.Max(inset + 1, _inspector.Content.ClientSize.Width - inset);
        var fieldWidth = Math.Max(1, right - inset);
        var y = ScaleMetric(6);
        var tabIndex = 0;
        void Heading(Label label, CameraSectionToggle disclosure, int actionWidth = 0)
        {
            label.SetBounds(inset, y, Math.Max(1, fieldWidth - ScaleMetric(36) - actionWidth), ScaleMetric(28));
            disclosure.SetBounds(right - ScaleMetric(28), y, ScaleMetric(28), ScaleMetric(28));
            disclosure.TabIndex = tabIndex++;
            y += ScaleMetric(36);
        }
        void Field(Control editor, bool visible = true, Label? caption = null)
        {
            editor.Visible = visible;
            editor.TabStop = visible && editor.Enabled;
            caption ??= editor.Tag as Label;
            if (caption is not null) caption.Visible = visible;
            if (!visible) return;
            editor.TabIndex = tabIndex++;
            LayoutDesignedField(editor, y, right, caption);
            y += ScaleMetric(RowStep);
        }
        void AxisRow(params ModernNumericUpDown[] editors)
        {
            var group = editors[0].Parent!;
            group.Visible = _transformDisclosure.Expanded;
            if (editors[0].Tag is Label caption) caption.Visible = _transformDisclosure.Expanded;
            if (!_transformDisclosure.Expanded) return;
            group.TabIndex = tabIndex++;
            LayoutAxisRow(y, right, editors);
            y += group.Height + ScaleMetric(FieldGap);
        }
        Heading(_lensHeading, _lensDisclosure);
        Field(_nameEditor, _lensDisclosure.Expanded);
        Field(_aspectRatioEditor, _lensDisclosure.Expanded);
        Field(_detailEditor, _lensDisclosure.Expanded && _experimental3DEnabled);
        Field(_kindEditor, _lensDisclosure.Expanded && _experimental3DEnabled);
        Field(_projectionEditor, _lensDisclosure.Expanded);
        var orthographic = (_projectionEditor.SelectedItem as ProjectionItem)?.Projection == CameraProjection.Orthographic;
        Field(_focalLength, _lensDisclosure.Expanded && (_experimental3DEnabled || !orthographic));
        Field(_orthographicSize, _lensDisclosure.Expanded && (_experimental3DEnabled || orthographic));
        y += ScaleMetric(8);
        _resetCameraButton.SetBounds(right - ScaleMetric(124), y, ScaleMetric(88), ScaleMetric(28));
        Heading(_transformHeading, _transformDisclosure, ScaleMetric(96));
        _resetCameraButton.TabIndex = tabIndex++;
        _resetCameraButton.BringToFront();
        AxisRow(_positionX, _positionY, _positionZ);
        AxisRow(_rotationX, _rotationY, _rotationZ);
        Field(_zoomEditor, _transformDisclosure.Expanded && !_experimental3DEnabled);
        y += ScaleMetric(8);
        Heading(_displayHeading, _displayDisclosure);
        Field(_designStyleEditor, _displayDisclosure.Expanded, _styleCaption);
        Field(_previewModeEditor, _displayDisclosure.Expanded);
        y += ScaleMetric(8);
        y = LayoutMicroMotion(y, right, ref tabIndex);
        var showParallax = ActiveShot()?.Framing.Kind == SceneShotCameraKind.TwoD;
        ParallaxEditor.Visible = showParallax;
        if (showParallax)
        {
            ParallaxEditor.TabIndex = tabIndex++;
            y += ScaleMetric(8);
            ParallaxEditor.SetBounds(inset, y, fieldWidth, ParallaxEditor.Height);
            y += ParallaxEditor.Height + ScaleMetric(8);
        }
        if (_curveEditor is { Visible: true } curveEditor)
            curveEditor.SetBounds(inset, y, fieldWidth, curveEditor.Height);
        _inspector.Content.PerformLayout();
        _inspector.Content.Invalidate();
        }
        finally { _layingOutDesignedCamera = false; }
    }

    private void LayoutDesignedField(Control editor, int top, int right, Label? caption = null)
    {
        var left = ScaleMetric(16);
        var start = DesignedFieldStart(right);
        caption ??= editor.Tag as Label;
        if (caption is not null)
        {
            var labelHeight = Math.Min(ScaleMetric(RowHeight), caption.Font.Height + ScaleMetric(4));
            caption.SetBounds(left, top + (ScaleMetric(RowHeight) - labelHeight) / 2,
                Math.Max(1, start - left - ScaleMetric(8)), labelHeight);
        }
        editor.SetBounds(start, top, Math.Max(1, right - start), ScaleMetric(RowHeight));
    }

    private int DesignedFieldStart(int right)
        => ScaleMetric(16) + (int)Math.Floor((right - ScaleMetric(16)) * 0.48f);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        SvgIcons.Draw(e.Graphics, SvgIconKind.Camera, new Rectangle(ScaleMetric(16), ScaleMetric(21), ScaleMetric(18), ScaleMetric(18)), Theme.PanelSecondaryText);
        InspectorModuleStyle.DrawDivider(this, e.Graphics, ScaleMetric(54));
        InspectorModuleStyle.DrawDivider(this, e.Graphics, _inspector.Top - ScaleMetric(6));
        InspectorModuleStyle.DrawDivider(this, e.Graphics, _cameraStatus.Top);
    }

    private static Label DesignCaption(string text, bool secondary = false)
    {
        var label = InspectorModuleStyle.CreateTextLabel();
        label.Text = text;
        label.ForeColor = secondary ? Theme.PanelSecondaryText : Theme.Text;
        label.Font = Theme.UiFont(secondary ? Theme.InspectorLabelFontSize : Theme.InspectorSectionFontSize);
        label.BackColor = Color.Transparent;
        return label;
    }

    private sealed record CameraStyleItem(string Name)
    {
        public override string ToString() => UiLocalization.T(Name);
    }

    internal sealed class CameraSectionToggle : SvgIconButton
    {
        public CameraSectionToggle(string name, bool expanded = true)
            : base(expanded ? SvgIconKind.ChevronDown : SvgIconKind.ChevronRight)
        {
            Expanded = expanded;
            IsDisclosureHeader = true;
            AccessibleName = name;
        }

        public bool Expanded { get; private set; }

        protected override void OnClick(EventArgs e)
        {
            Expanded = !Expanded;
            Icon = Expanded ? SvgIconKind.ChevronDown : SvgIconKind.ChevronRight;
            base.OnClick(e);
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new SectionAccessibleObject(this);

        private sealed class SectionAccessibleObject(CameraSectionToggle owner) : ControlAccessibleObject(owner)
        {
            public override AccessibleStates State => base.State
                | (owner.Expanded ? AccessibleStates.Expanded : AccessibleStates.Collapsed);
        }
    }
}
