namespace VectorAnimationEngine;

internal sealed partial class ShotDirectorPanel
{
    private readonly Label _sceneCamerasCaption = DesignCaption("Scene cameras", true);
    private readonly Label _lensHeading = DesignCaption("Lens settings");
    private readonly Label _transformHeading = DesignCaption("Transform");
    private readonly Label _displayHeading = DesignCaption("Display & preview");
    private readonly Label _rotationZoomCaption = DesignCaption("Rotation / zoom", true);
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
            _rotationZoomCaption, _styleCaption, _designStyleEditor]);
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
        var addWidth = ScaleMetric(compactCommands ? 32 : 140);
        _addShotButton.Text = compactCommands ? string.Empty : UiLocalization.T("New camera");
        _sceneCamerasCaption.SetBounds(inset, ScaleMetric(66), Math.Max(1, width - addWidth - ScaleMetric(8)), ScaleMetric(28));
        _addShotButton.SetBounds(Width - inset - addWidth, ScaleMetric(64), addWidth, ScaleMetric(Theme.ControlHeight));
        _cameraList.ItemHeight = ScaleMetric(48);
        var visibleRows = Math.Clamp((Height - ScaleMetric(400)) / ScaleMetric(48), 1, 3);
        var listHeight = ScaleMetric(Math.Clamp(_state.Shots.Length, 1, visibleRows) * 48);
        _cameraList.SetBounds(inset, ScaleMetric(104), width, listHeight);
        _emptyCameras.Bounds = _cameraList.Bounds;
        _emptyCameras.Visible = _state.Shots.Length == 0;
        _cameraList.Visible = _state.Shots.Length > 0;
        if (_emptyCameras.Visible) _emptyCameras.BringToFront();
        var commandsY = _cameraList.Bottom + ScaleMetric(8);
        _locateButton.SetBounds(inset, commandsY, Math.Max(1, width - ScaleMetric(104)), ScaleMetric(28));
        _locateButton.Text = compactCommands ? string.Empty : UiLocalization.T("Locate");
        _moveUpButton.SetBounds(Width - inset - ScaleMetric(92), commandsY, ScaleMetric(28), ScaleMetric(28));
        _moveDownButton.SetBounds(Width - inset - ScaleMetric(60), commandsY, ScaleMetric(28), ScaleMetric(28));
        _removeButton.SetBounds(Width - inset - ScaleMetric(28), commandsY, ScaleMetric(28), ScaleMetric(28));
        var inspectorTop = commandsY + ScaleMetric(46);
        var footerHeight = ScaleMetric(30);
        _inspector.SetBounds(0, inspectorTop, ClientSize.Width, Math.Max(1, Height - inspectorTop - footerHeight));
        _cameraStatus.Text = ActiveShot() is { } shot
            ? $"{shot.Name} · {UiLocalization.T(shot.Framing.Kind == SceneShotCameraKind.TwoD ? "2D Camera" : "3D Camera")}" : UiLocalization.T("No camera yet");
        _cameraStatus.SetBounds(inset, Math.Max(inspectorTop, Height - footerHeight), width, footerHeight);

        var right = Math.Max(inset + 1, _inspector.Content.ClientSize.Width - inset);
        var fieldWidth = Math.Max(1, right - inset);
        var y = ScaleMetric(10);
        var tabIndex = 0;
        void Heading(Label label)
        {
            label.SetBounds(inset, y, fieldWidth, ScaleMetric(28));
            y += ScaleMetric(38);
        }
        void Field(Control editor, bool visible = true)
        {
            editor.Visible = visible;
            editor.TabStop = visible && editor.Enabled;
            if (editor.Tag is Label label) label.Visible = visible;
            if (!visible) return;
            editor.TabIndex = tabIndex++;
            LayoutDesignedField(editor, y, right);
            y += ScaleMetric(RowStep);
        }
        Heading(_lensHeading);
        Field(_nameEditor);
        Field(_aspectRatioEditor);
        y += ScaleMetric(10);
        _resetCameraButton.SetBounds(right - ScaleMetric(88), y, ScaleMetric(88), ScaleMetric(28));
        _resetCameraButton.TabIndex = tabIndex++;
        Heading(_transformHeading);
        _transformHeading.Width = Math.Max(1, fieldWidth - ScaleMetric(96));
        _resetCameraButton.BringToFront();
        var advanced = _experimental3DEnabled || _advancedCameraSettings.Checked;
        if (advanced)
        {
            _positionX.Parent!.TabIndex = tabIndex++;
            LayoutAxisRow(y, right, _positionX, _positionY, _positionZ);
            y += _positionX.Parent!.Height + ScaleMetric(FieldGap);
            _rotationZ.Parent!.TabIndex = tabIndex++;
            LayoutAxisRow(y, right, _rotationX, _rotationY, _rotationZ);
            y += _rotationZ.Parent!.Height + ScaleMetric(FieldGap);
            _rotationZoomCaption.Visible = false;
            Field(_zoomEditor, !_experimental3DEnabled);
        }
        else if (fieldWidth < ScaleMetric(270))
        {
            _rotationZoomCaption.Visible = false;
            _positionX.Parent!.TabIndex = tabIndex++;
            LayoutAxisRow(y, right, _positionX, _positionY, _positionZ);
            y += _positionX.Parent!.Height + ScaleMetric(FieldGap);
            _rotationZ.Parent!.TabIndex = tabIndex++;
            LayoutAxisRow(y, right, _rotationX, _rotationY, _rotationZ);
            y += ScaleMetric(RowStep);
            Field(_zoomEditor);
        }
        else
        {
            _positionX.Parent!.TabIndex = tabIndex++;
            LayoutDesignedPosition(y, right);
            y += ScaleMetric(RowStep);
            _rotationZ.Parent!.TabIndex = tabIndex++;
            _zoomEditor.TabIndex = tabIndex++;
            LayoutDesignedRotation(y, right);
            y += ScaleMetric(RowStep);
        }
        y += ScaleMetric(12);
        Heading(_displayHeading);
        _designStyleEditor.TabIndex = tabIndex++;
        LayoutDesignedField(_designStyleEditor, y, right, _styleCaption);
        y += ScaleMetric(RowStep);
        Field(_previewModeEditor);
        var showParallax = ActiveShot()?.Framing.Kind == SceneShotCameraKind.TwoD;
        ParallaxEditor.Visible = showParallax;
        if (showParallax)
        {
            ParallaxEditor.TabIndex = tabIndex++;
            y += ScaleMetric(16);
            ParallaxEditor.SetBounds(inset, y, fieldWidth, ParallaxEditor.Height);
            y += ParallaxEditor.Height + ScaleMetric(16);
        }
        _advancedCameraSettings.Visible = !_experimental3DEnabled;
        if (!_experimental3DEnabled)
        {
            _advancedCameraSettings.TabIndex = tabIndex++;
            _advancedCameraSettings.SetBounds(inset, y, fieldWidth, ScaleMetric(32));
            y += ScaleMetric(40);
        }
        Field(_detailEditor, _experimental3DEnabled);
        Field(_kindEditor, _experimental3DEnabled);
        Field(_projectionEditor, advanced);
        var orthographic = (_projectionEditor.SelectedItem as ProjectionItem)?.Projection == CameraProjection.Orthographic;
        Field(_focalLength, _experimental3DEnabled || advanced && !orthographic);
        Field(_orthographicSize, _experimental3DEnabled || advanced && orthographic);
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
        var start = left + (int)Math.Round((right - left) * 0.30f);
        caption ??= editor.Tag as Label;
        if (caption is not null)
        {
            var labelHeight = Math.Min(ScaleMetric(RowHeight), caption.Font.Height + ScaleMetric(4));
            caption.SetBounds(left, top + (ScaleMetric(RowHeight) - labelHeight) / 2,
                Math.Max(1, start - left - ScaleMetric(8)), labelHeight);
        }
        editor.SetBounds(start, top, Math.Max(1, right - start), ScaleMetric(RowHeight));
    }

    private void LayoutDesignedPosition(int top, int right)
    {
        var group = _positionX.Parent!;
        var left = ScaleMetric(16);
        var start = left + (int)Math.Round((right - left) * 0.30f);
        if (_positionX.Tag is Label caption)
        {
            caption.Visible = true;
            caption.SetBounds(left, top, Math.Max(1, start - left - ScaleMetric(8)), ScaleMetric(RowHeight));
        }
        group.SetBounds(start, top, Math.Max(1, right - start), ScaleMetric(RowHeight));
        _positionZ.Visible = false;
        var half = group.Width / 2;
        foreach (var editor in new[] { _positionX, _positionY, _positionZ })
        {
            var visible = editor != _positionZ;
            editor.Visible = visible;
            editor.GroupedAppearance = true;
            var column = editor == _positionX ? 0 : half;
            editor.SetBounds(column + ScaleMetric(20), 0, Math.Max(1, (editor == _positionX ? half : group.Width - half) - ScaleMetric(20)), ScaleMetric(RowHeight));
            var axis = group.Controls.OfType<Label>().FirstOrDefault(label => ReferenceEquals(label.Tag, editor));
            if (axis is not null)
            {
                axis.Visible = visible;
                axis.SetBounds(column, 0, ScaleMetric(20), ScaleMetric(RowHeight));
            }
        }
    }

    private void LayoutDesignedRotation(int top, int right)
    {
        var group = _rotationZ.Parent!;
        var left = ScaleMetric(16);
        var start = left + (int)Math.Round((right - left) * 0.30f);
        var half = Math.Max(1, (right - start - ScaleMetric(8)) / 2);
        foreach (var label in group.Controls.OfType<Label>()) label.Visible = false;
        if (_rotationZ.Tag is Label rotationCaption) rotationCaption.Visible = false;
        if (_zoomEditor.Tag is Label zoomCaption) zoomCaption.Visible = false;
        _rotationZoomCaption.Visible = true;
        _rotationZoomCaption.SetBounds(left, top, Math.Max(1, start - left - ScaleMetric(8)), ScaleMetric(RowHeight));
        group.SetBounds(start, top, half, ScaleMetric(RowHeight));
        _rotationX.Visible = _rotationY.Visible = false;
        _rotationZ.Visible = true;
        _rotationZ.GroupedAppearance = false;
        _rotationZ.SetBounds(0, 0, half, ScaleMetric(RowHeight));
        _zoomEditor.Visible = true;
        _zoomEditor.SetBounds(start + half + ScaleMetric(8), top, Math.Max(1, right - start - half - ScaleMetric(8)), ScaleMetric(RowHeight));
    }

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
}
