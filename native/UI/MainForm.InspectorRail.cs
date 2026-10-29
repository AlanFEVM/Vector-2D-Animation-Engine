namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private const int InspectorRailWidth = 38;
    private const int InspectorRailButtonHeight = 34;
    private const int InspectorRailButtonInset = 3;
    private const int InspectorRailButtonGap = 2;
    private const int InspectorRailMinHostWidth = 324;

    private enum InspectorCategory
    {
        None,
        Color,
        Physics,
        Camera,
        Scene,
        Tools,
        Document
    }

    private readonly Panel _inspectorRail = new();
    private readonly ComboBox _inspectorCategorySelector = new();
    private static readonly InspectorCategory[] InspectorCategoryOrder =
        [InspectorCategory.None, InspectorCategory.Color,
            InspectorCategory.Physics, InspectorCategory.Camera, InspectorCategory.Scene];
    private static readonly InspectorCategory[] InspectorPropertyOrder =
        [InspectorCategory.None, InspectorCategory.Tools, InspectorCategory.Document];
    private readonly Panel _inspectorPropertyNavigation = new();
    private readonly Dictionary<InspectorCategory, Button> _inspectorPropertyButtons = [];
    private InspectorCategory _inspectorPropertyCategory = InspectorCategory.None;
    private readonly HashSet<string> _inspectorSelectedInstanceIds = new(StringComparer.Ordinal);
    private bool _objectInspectorSelectionPending;
    private readonly ThemedScrollPanel _colorInspectorPage = new();
    private readonly ThemedScrollPanel _toolInspectorPage = new();
    private readonly TextSettingsPanel _textToolSettingsPanel = new();
    private readonly ThemedScrollPanel _inspectorUnavailablePage = new();
    private readonly ToolContextPanel _toolContextPanel = new();
    private readonly Label _appearanceInspectorSummary = new();
    private readonly Label _inspectorUnavailableMessage = new();
    private readonly Button _inspectorContextButton = new();
    private readonly Dictionary<InspectorCategory, Button> _inspectorCategoryButtons = [];
    private InspectorCategory _inspectorCategory = InspectorCategory.None;
    private InspectorCategory? _presentedInspectorCategory;
    private Padding _inspectorRailBasePadding;
    private bool _layingOutInspectorRail;
    private bool _colorMaterialAvailable = true;

    private void BuildInspectorCategoryRail(Control inspector)
    {
        _inspectorRailBasePadding = inspector.Padding;
        _inspectorRail.BackColor = Theme.Panel;
        _inspectorRail.AutoScroll = true;
        _inspectorRail.AccessibleRole = AccessibleRole.PageTabList;
        _inspectorRail.AccessibleName = "Inspector categories";
        PaintLeftBorder(_inspectorRail);
        inspector.Controls.Add(_inspectorRail);
        foreach (var category in InspectorCategoryOrder)
        {
            var label = InspectorCategoryLabel(category);
            var button = new SvgIconButton(InspectorCategoryIcon(category))
            {
                AccessibleName = label,
                AccessibleRole = AccessibleRole.PageTab,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Tag = category,
                Enabled = category != InspectorCategory.Physics
            };
            Theme.StyleStandardButton(button);
            button.Click += (_, _) => ToggleInspectorCategory((InspectorCategory)button.Tag!);
            button.MouseEnter += (_, _) => _toolTip.ShowFor(button,
                category == InspectorCategory.Physics ? "Physics is not available." : label);
            button.MouseLeave += (_, _) => _toolTip.HideTip();
            _inspectorCategoryButtons[category] = button;
            _inspectorRail.Controls.Add(button);
        }
        _inspectorPropertyNavigation.AccessibleRole = AccessibleRole.PageTabList;
        _inspectorPropertyNavigation.AccessibleName = "Properties pages";
        _inspectorRail.Controls.Add(_inspectorPropertyNavigation);
        foreach (var category in InspectorPropertyOrder)
        {
            var button = new SvgIconButton(InspectorPropertyIcon(category))
            {
                AccessibleRole = AccessibleRole.PageTab,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                UseMnemonic = false
            };
            Theme.StyleToolbarButton(button);
            button.Click += (_, _) => RevealInspectorCategory(category, "properties page");
            button.MouseEnter += (_, _) => _toolTip.ShowFor(button, InspectorPropertyLabel(category));
            button.MouseLeave += (_, _) => _toolTip.HideTip();
            _inspectorPropertyButtons[category] = button;
            _inspectorPropertyNavigation.Controls.Add(button);
        }
        _colorInspectorPage.Dock = DockStyle.Fill;
        _colorInspectorPage.BackColor = Theme.Panel;
        _colorInspectorPage.ShowScrollBar = false;
        _colorInspectorPage.ContentPadding = new Padding(0, 0, 2, 0);
        _colorInspectorPage.Visible = false;
        inspector.Controls.Add(_colorInspectorPage);
        _inspectorCategorySelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _inspectorCategorySelector.AccessibleName = "Inspector categories";
        Theme.StyleComboBox(_inspectorCategorySelector);
        _inspectorCategorySelector.SelectedIndexChanged += (_, _) =>
        {
            if (_layingOutInspectorRail || _inspectorCategorySelector.SelectedIndex < 0) return;
            var category = InspectorCategoryOrder[_inspectorCategorySelector.SelectedIndex];
            RevealInspectorCategory(category == InspectorCategory.None ? _inspectorPropertyCategory : category,
                "category selector");
            LayoutInspectorCategoryRail();
        };
        inspector.Controls.Add(_inspectorCategorySelector);
        BuildInspectorContextPages(inspector);
        inspector.Layout += (_, _) => LayoutInspectorCategoryRail();
        inspector.VisibleChanged += (_, _) => LayoutInspectorCategoryRail();
        LayoutInspectorCategoryRail();
    }

    private void BuildInspectorContextPages(Control inspector)
    {
        foreach (var page in new[] { _toolInspectorPage, _inspectorUnavailablePage })
        {
            page.Dock = DockStyle.Fill;
            page.BackColor = Theme.Panel;
            page.ShowScrollBar = true;
            page.ContentPadding = new Padding(0, 0, 2, 12);
            page.Visible = false;
            inspector.Controls.Add(page);
        }
        foreach (var label in new[] { _appearanceInspectorSummary, _inspectorUnavailableMessage })
        {
            label.AutoSize = true;
            label.Padding = new Padding(4, 12, 4, 12);
            label.ForeColor = Theme.PanelSecondaryText;
            label.Font = Theme.UiFont(9f);
            label.UseMnemonic = false;
        }
        UiComposition.MountVertical(_colorInspectorPage.Content,
            [new(_appearanceInspectorSummary), new(_materialEditor)]);
        UiComposition.MountVertical(_toolInspectorPage.Content,
        [
            new(_toolContextPanel),
            new(_textToolSettingsPanel, _textToolSettingsPanel.PreferredHeight),
            new(_brushTipPanel, _brushTipPanel.PreferredHeight),
            new(_mixingBrushSettingsPanel, _mixingBrushSettingsPanel.PreferredPanelHeight),
            new(_drawSettingsPanel, _drawSettingsPanel.PreferredHeight),
            new(_shapeSettingsPanel, _shapeSettingsPanel.PreferredHeight)
        ]);
        Theme.StyleStandardButton(_inspectorContextButton);
        _inspectorContextButton.UseMnemonic = false;
        _inspectorContextButton.Height = Theme.ControlHeight;
        _inspectorContextButton.Click += (_, _) =>
        {
            var view = _inspectorCategory is InspectorCategory.Camera or InspectorCategory.Scene
                ? WorkspaceView.SceneEditor : WorkspaceView.BasicDrawing;
            if (!CommitTextEdit()) return;
            if (IsSymbolEditing && !ExitSceneSymbolEditing()) return;
            _workspaceTabs.SelectView(view);
            ApplyInspectorCategoryPresentation();
        };
        UiComposition.MountVertical(_inspectorUnavailablePage.Content,
            [new(_inspectorUnavailableMessage), new(_inspectorContextButton)]);
        _colorInspectorPage.Content.SizeChanged += (_, _) =>
            _appearanceInspectorSummary.MaximumSize = new Size(Math.Max(1, _colorInspectorPage.Content.ClientSize.Width), 0);
        _inspectorUnavailablePage.Content.SizeChanged += (_, _) =>
            _inspectorUnavailableMessage.MaximumSize = new Size(Math.Max(1, _inspectorUnavailablePage.Content.ClientSize.Width), 0);
        BindInspectorCoverageEvents();
    }

    private void UpdateToolInspectorSummary()
    {
        _toolContextPanel.Visible = _tool != ToolMode.Text;
        var toolName = IsShapeTool(_tool) ? ShapeToolName(_tool)
            : IsLineTool(_tool) ? LineToolName(_tool)
            : IsBrushTool(_tool) ? BrushToolName(_tool)
            : _tool switch
            {
                ToolMode.Text => "Text Tool",
                ToolMode.Eraser => "Eraser Tool",
                ToolMode.Gradient => "Gradient Tool",
                ToolMode.Eyedropper => "Eyedropper Tool",
                ToolMode.Hand => "Hand Tool",
                ToolMode.SnapPoint => "Snap Point Tool",
                _ => ToolPairName(_tool)
            };
        _toolContextPanel.SetContext(_tool, UiLocalization.T(toolName), ToolIconKind(_tool), CanActivateTool(_tool), _drawSettings);
    }

    private void LayoutInspectorCategoryRail()
    {
        var host = _inspectorHost;
        if (_layingOutInspectorRail || !ReferenceEquals(_inspectorRail.Parent, host)) return;
        _layingOutInspectorRail = true;
        host.SuspendLayout();
        try
        {
            var scale = host.DeviceDpi / 96d;
            var available = _inspectorPanelOpen
                && host.ClientSize.Width >= ScaleResponsive(InspectorRailMinHostWidth, scale);
            var width = available ? ScaleResponsive(InspectorRailWidth, scale) : 0;
            var compact = _inspectorPanelOpen && !available;
            var properties = IsInspectorPropertyCategory(_inspectorCategory);
            var propertyHeight = ScaleResponsive(InspectorRailButtonHeight, scale);
            var gap = ScaleResponsive(InspectorRailButtonGap, scale);
            var selectorHeight = compact ? ScaleResponsive(Theme.ControlHeight + 8, scale)
                + (properties ? propertyHeight + gap : 0) : 0;
            host.Padding = new Padding(_inspectorRailBasePadding.Left, _inspectorRailBasePadding.Top + selectorHeight,
                _inspectorRailBasePadding.Right + width, _inspectorRailBasePadding.Bottom);
            _inspectorCategorySelector.Visible = compact;
            if (compact)
            {
                var labels = InspectorCategoryOrder.Select(category => UiLocalization.T(InspectorCategoryLabel(category))).ToArray();
                if (!_inspectorCategorySelector.Items.Cast<string>().SequenceEqual(labels))
                {
                    _inspectorCategorySelector.Items.Clear();
                    _inspectorCategorySelector.Items.AddRange(labels);
                }
                _inspectorCategorySelector.SelectedIndex = Array.IndexOf(InspectorCategoryOrder,
                    properties ? InspectorCategory.None : _inspectorCategory);
                _inspectorCategorySelector.SetBounds(_inspectorRailBasePadding.Left, _inspectorRailBasePadding.Top,
                    Math.Max(1, host.ClientSize.Width - _inspectorRailBasePadding.Horizontal), ScaleResponsive(Theme.ControlHeight, scale));
            }
            _inspectorRail.Visible = available;
            _inspectorRail.SetBounds(host.ClientSize.Width - width, 0, width, host.ClientSize.Height);
            var inset = ScaleResponsive(InspectorRailButtonInset, scale);
            var propertyParent = compact ? host : _inspectorRail;
            if (!ReferenceEquals(_inspectorPropertyNavigation.Parent, propertyParent))
                propertyParent.Controls.Add(_inspectorPropertyNavigation);
            _inspectorPropertyNavigation.BackColor = SystemInformation.HighContrast
                ? SystemColors.Control : Theme.PanelStrong;
            _inspectorPropertyNavigation.Visible = _inspectorPanelOpen && properties;
            _inspectorPropertyNavigation.AccessibleName = UiLocalization.T("Properties");
            if (compact && properties)
            {
                _inspectorPropertyNavigation.SetBounds(_inspectorRailBasePadding.Left,
                    _inspectorRailBasePadding.Top + ScaleResponsive(Theme.ControlHeight + 8, scale),
                    Math.Max(1, host.ClientSize.Width - _inspectorRailBasePadding.Horizontal), propertyHeight);
            }
            var top = inset;
            foreach (var category in InspectorCategoryOrder)
            {
                var button = _inspectorCategoryButtons[category];
                button.SetBounds(inset, top, Math.Max(1, width - inset * 2), ScaleResponsive(InspectorRailButtonHeight, scale));
                top += button.Height + gap;
                if (category == InspectorCategory.None && properties && available)
                {
                    var groupHeight = propertyHeight * InspectorPropertyOrder.Length + gap * 2 + inset * 2;
                    _inspectorPropertyNavigation.SetBounds(0, top, Math.Max(1, width), groupHeight);
                    top += groupHeight + gap;
                }
            }
            for (var index = 0; index < InspectorPropertyOrder.Length; index++)
            {
                var category = InspectorPropertyOrder[index];
                var button = _inspectorPropertyButtons[category];
                var label = InspectorPropertyLabel(category);
                button.AccessibleName = UiLocalization.T(label);
                var buttonWidth = ScaleResponsive(InspectorRailWidth - InspectorRailButtonInset * 2, scale);
                if (compact)
                {
                    var left = _inspectorPropertyNavigation.Width * index / InspectorPropertyOrder.Length;
                    var right = _inspectorPropertyNavigation.Width * (index + 1) / InspectorPropertyOrder.Length;
                    button.SetBounds(left + (right - left - buttonWidth) / 2, 0, buttonWidth, propertyHeight);
                }
                else
                {
                    button.SetBounds(inset, inset + index * (propertyHeight + gap), buttonWidth, propertyHeight);
                }
            }
            ApplyInspectorPageVisibility();
            var presentedCategory = _inspectorCategory;
            if (_presentedInspectorCategory != presentedCategory)
            {
                foreach (var (id, button) in _inspectorCategoryButtons)
                {
                    var active = id == InspectorCategory.None ? properties : id == presentedCategory;
                    Theme.StyleStandardButton(button, active);
                    button.AccessibleDescription = id == InspectorCategory.Physics
                        ? UiLocalization.T("Physics is not available.")
                        : $"{UiLocalization.T(InspectorCategoryLabel(id))} {UiLocalization.T(active ? "Visible" : "Hidden")}";
                }
                foreach (var (id, button) in _inspectorPropertyButtons)
                {
                    var active = id == presentedCategory;
                    Theme.StyleToolbarButton(button, active);
                    button.AccessibleDescription = UiLocalization.T(active ? "Visible" : "Hidden");
                }
                _presentedInspectorCategory = presentedCategory;
            }
            if (available) _inspectorRail.BringToFront();
            if (compact) _inspectorCategorySelector.BringToFront();
            if (compact && properties) _inspectorPropertyNavigation.BringToFront();
        }
        finally
        {
            host.ResumeLayout(true);
            _layingOutInspectorRail = false;
        }
    }

    private void ApplyInspectorPageVisibility()
    {
        var properties = _inspectorCategory == InspectorCategory.None;
        var color = _inspectorCategory == InspectorCategory.Color;
        var tools = _inspectorCategory == InspectorCategory.Tools;
        var camera = _inspectorCategory == InspectorCategory.Camera;
        var scene = _inspectorCategory == InspectorCategory.Scene;
        var drawingContext = IsDrawingInspectorContext();
        var sceneContext = IsSceneWorkspaceSelected;
        var unavailable = color && !drawingContext
            || tools && !drawingContext && !sceneContext
            || (camera || scene) && !sceneContext;
        var document = IsDocumentInspectorContext;
        _materialEditor.Visible = color && !unavailable;
        _materialEditor.Enabled = _colorMaterialAvailable && drawingContext;
        _appearanceInspectorSummary.Text = !_colorMaterialAvailable
            ? UiLocalization.T("This selection has no editable fill or stroke.")
            : _selectedObjects.Count == 0 && !_selectedElement.IsValid
                ? UiLocalization.T("No selection: current drawing appearance.")
                : UiLocalization.T("Editing the appearance of the current selection.");
        _colorInspectorPage.Visible = color && !unavailable;
        _toolInspectorPage.Visible = tools && !unavailable;
        _inspectorUnavailablePage.Visible = unavailable;
        _documentInspectorPage.Visible = document;
        _documentInfoPanel.Visible = document;
        _basicInspectorPage.Visible = properties && !document;
        _sceneEditPage.Visible = scene && !unavailable;
        _animationPage.Visible = false;
        _shotDirectorPanel.Visible = camera && !unavailable;
        _layerBlendModePanel.Visible = properties && !document;
        _objectInspector.Visible = properties && !document;
        if (unavailable)
        {
            _inspectorUnavailableMessage.Text = UiLocalization.T(camera || scene
                ? "Open Scene & Animation to edit this category. Switching tabs does not change the workspace."
                : "Tool and drawing appearance settings require a drawing workspace or scene mask. Switching tabs does not change the workspace.");
            _inspectorContextButton.Text = UiLocalization.T(camera || scene
                ? "Open Scene & Animation" : "Open Basic Drawing");
            _inspectorUnavailablePage.BringToFront();
        }
        else if (color) _colorInspectorPage.BringToFront();
        else if (tools) _toolInspectorPage.BringToFront();
        else if (camera) _shotDirectorPanel.BringToFront();
        else if (document) _documentInspectorPage.BringToFront();
        else if (scene) _sceneEditPage.BringToFront();
        else _basicInspectorPage.BringToFront();
    }

    private void ToggleInspectorCategory(InspectorCategory category)
    {
        if (!_inspectorPanelOpen || !_inspectorRail.Visible
            || !_inspectorCategoryButtons.TryGetValue(category, out var button) || !button.Enabled) return;
        if (category == InspectorCategory.None)
            category = IsInspectorPropertyCategory(_inspectorCategory) ? InspectorCategory.None : _inspectorPropertyCategory;
        RevealInspectorCategory(category, "category button");
    }

    /// <summary>
    /// Explicit-action entry: when the user does something that belongs to a domain
    /// (pick a fill color, open the shot page, add a light) the matching category comes
    /// forward instead of quietly editing state the user cannot see. Selection changes
    /// are handled separately without re-entering the inspector refresh chain.
    /// </summary>
    private void RevealInspectorCategory(InspectorCategory category, string reason)
    {
        _objectInspectorSelectionPending = false;
        if (!_inspectorPanelOpen) return;
        if (category == _inspectorCategory)
        {
            if (category == InspectorCategory.None) _basicInspectorPage.RestoreScrollPosition(0);
            return;
        }
        if (!CanPresentInspectorCategory(category)) return;
        if (!CommitTextEdit()) return;
        _inspectorCategory = category;
        if (IsInspectorPropertyCategory(category)) _inspectorPropertyCategory = category;
        ApplyInspectorCategoryPresentation();
        if (category == InspectorCategory.None) _basicInspectorPage.RestoreScrollPosition(0);
        AppLog.Info($"Inspector category revealed: {category} ({reason})");
    }

    private bool CanPresentInspectorCategory(InspectorCategory category) => category switch
    {
        InspectorCategory.None => true,
        InspectorCategory.Color => true,
        InspectorCategory.Tools => true,
        InspectorCategory.Document => true,
        // No physics content yet; the rail keeps the button disabled.
        InspectorCategory.Physics => false,
        InspectorCategory.Camera => true,
        InspectorCategory.Scene => true,
        _ => false
    };

    internal bool HandleInspectorCategoryShortcut(Keys keyData)
    {
        if ((keyData & Keys.Modifiers) != (Keys.Control | Keys.Alt)) return false;
        var key = keyData & Keys.KeyCode;
        if (key is < Keys.D1 or > Keys.D7) return false;
        var category = (InspectorCategory)(key - Keys.D1);
        if (!_inspectorPanelOpen) ToggleInspectorPanel();
        RevealInspectorCategory(category, "keyboard shortcut");
        return true;
    }

    private void ApplyInspectorCategoryPresentation()
    {
        _inspectorTransition?.Cancel();
        UpdateInspector();
        RefreshShotDirector(force: true);
        LayoutInspectorCategoryRail();
    }

    private void NotifyInspectorSelectionChanged(bool drawingSelectionChanged)
    {
        var instancesChanged = !_inspectorSelectedInstanceIds.SetEquals(_selectedSceneInstanceIds);
        if (instancesChanged)
        {
            _inspectorSelectedInstanceIds.Clear();
            _inspectorSelectedInstanceIds.UnionWith(_selectedSceneInstanceIds);
        }
        if (drawingSelectionChanged || instancesChanged) RequestObjectInspectorForSelection();
    }

    private void RequestObjectInspectorForSelection()
    {
        _objectInspectorSelectionPending = _inspectorPanelOpen && !_playing && !_syncingFrame
            && (_selectedObjects.Count > 0 || _selectedSceneInstanceIds.Count > 0);
    }

    private void ApplyPendingObjectInspectorSelection()
    {
        if (!_objectInspectorSelectionPending) return;
        _objectInspectorSelectionPending = false;
        if (!_inspectorPanelOpen || _playing || _syncingFrame
            || _selectedObjects.Count == 0 && _selectedSceneInstanceIds.Count == 0) return;
        _inspectorTransition?.Cancel();
        _inspectorCategory = InspectorCategory.None;
        _inspectorPropertyCategory = InspectorCategory.None;
        _basicInspectorPage.RestoreScrollPosition(0);
    }

    private void CloseInspectorCategory()
    {
        _objectInspectorSelectionPending = false;
        _inspectorCategory = InspectorCategory.None;
        _inspectorPropertyCategory = InspectorCategory.None;
        ApplyInspectorCategoryPresentation();
        _inspectorCategoryButtons[InspectorCategory.None].Focus();
    }

    private bool HandleInspectorCategoryKey(Keys keyData)
    {
        if (!_inspectorRail.ContainsFocus && !_inspectorPropertyNavigation.ContainsFocus) return false;
        var buttons = InspectorCategoryOrder.SelectMany(category => category == InspectorCategory.None
            ? new[] { _inspectorCategoryButtons[category] }.Concat(_inspectorPropertyButtons.Values)
            : new[] { _inspectorCategoryButtons[category] }).Where(button => button.Visible && button.Enabled).ToArray();
        if (keyData is Keys.Enter or Keys.Space)
        {
            var focused = buttons.FirstOrDefault(button => button.Focused);
            if (focused is null) return false;
            focused.PerformClick();
            return true;
        }
        if (keyData == Keys.Escape && _inspectorCategory != InspectorCategory.None)
        {
            CloseInspectorCategory();
            return true;
        }
        if (keyData is not (Keys.Up or Keys.Down or Keys.Home or Keys.End)) return false;
        if (buttons.Length == 0) return false;
        var index = Array.FindIndex(buttons, button => button.Focused);
        var next = keyData switch
        {
            Keys.Home => 0,
            Keys.End => buttons.Length - 1,
            Keys.Up => (index + buttons.Length - 1) % buttons.Length,
            _ => (index + 1) % buttons.Length
        };
        buttons[next].Focus();
        return true;
    }

    private static string InspectorCategoryLabel(InspectorCategory category) => category switch
    {
        InspectorCategory.Color => "Appearance",
        InspectorCategory.Tools => "Tool Settings",
        InspectorCategory.Physics => "Physics",
        InspectorCategory.Camera => "Camera",
        InspectorCategory.Scene => "Scene",
        _ => "Properties"
    };

    private static bool IsInspectorPropertyCategory(InspectorCategory category) =>
        category is InspectorCategory.None or InspectorCategory.Tools or InspectorCategory.Document;

    private static string InspectorPropertyLabel(InspectorCategory category) => category switch
    {
        InspectorCategory.Tools => "Tool",
        InspectorCategory.Document => "Document",
        _ => "Object"
    };

    private static SvgIconKind InspectorPropertyIcon(InspectorCategory category) => category switch
    {
        InspectorCategory.Tools => SvgIconKind.InspectorTools,
        InspectorCategory.Document => SvgIconKind.InspectorProperties,
        _ => SvgIconKind.InspectorObject
    };

    private static SvgIconKind InspectorCategoryIcon(InspectorCategory category) => category switch
    {
        InspectorCategory.Color => SvgIconKind.InspectorColor,
        InspectorCategory.Tools => SvgIconKind.Brush,
        InspectorCategory.Physics => SvgIconKind.InspectorPhysics,
        InspectorCategory.Camera => SvgIconKind.InspectorCamera,
        InspectorCategory.Scene => SvgIconKind.InspectorScene,
        _ => SvgIconKind.InspectorSettings
    };
}
