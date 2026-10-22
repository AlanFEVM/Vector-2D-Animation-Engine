namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private const int InspectorRailWidth = 38;
    private const int InspectorRailButtonHeight = 34;
    private const int InspectorRailButtonInset = 3;
    private const int InspectorRailButtonGap = 2;
    private const int InspectorRailMinHostWidth = 220;

    private enum InspectorCategory
    {
        None,
        Color,
        Physics,
        Camera,
        Scene
    }

    private readonly Panel _inspectorRail = new();
    private readonly ThemedScrollPanel _colorInspectorPage = new();
    private readonly Dictionary<InspectorCategory, Button> _inspectorCategoryButtons = [];
    private InspectorCategory _inspectorCategory = InspectorCategory.None;
    private InspectorCategory? _presentedInspectorCategory;
    private Padding _inspectorRailBasePadding;
    private bool _layingOutInspectorRail;
    private bool _colorMaterialAvailable = true;
    private VectorScene? _inspectorSelectionScene;
    private int[] _inspectorSelectionObjects = [];
    private DrawingElementKey[] _inspectorSelectionElements = [];
    private DrawingElementKey _inspectorPrimaryElement = DrawingElementKey.None;
    private int _inspectorPrimaryObject = -1;

    private void FollowInspectorMaterialSelection()
    {
        var objects = _selectedObjects.ToArray();
        var elements = _selectedElements.Select(hit => hit.Key).ToArray();
        var primary = _selectedElement.Key;
        var changed = !ReferenceEquals(_inspectorSelectionScene, _scene)
            || !_inspectorSelectionObjects.AsSpan().SequenceEqual(objects)
            || !_inspectorSelectionElements.AsSpan().SequenceEqual(elements)
            || _inspectorPrimaryElement != primary
            || _inspectorPrimaryObject != _selectedObject;
        _inspectorSelectionScene = _scene;
        _inspectorSelectionObjects = objects;
        _inspectorSelectionElements = elements;
        _inspectorPrimaryElement = primary;
        _inspectorPrimaryObject = _selectedObject;
        if (!changed || !_inspectorPanelOpen || !IsDrawingInspectorContext()
            || SelectedSceneInstance() is not null || objects.Length == 0) return;
        if (objects.Any(index => (uint)index >= (uint)_scene.ObjectCount
            || IsImportedSvgObject(index) || _scene.ShapeKind[index] == ShapeKind.Bitmap)) return;
        _inspectorCategory = InspectorCategory.Color;
        _inspectorTransition?.Cancel();
    }
    // A category may drag the workspace along (Color needs drawing, Camera/Scene need
    // the scene). Remember where the user came from so Properties can hand it back.
    private WorkspaceView? _workspaceBeforeInspectorCategory;
    private WorkspaceView? _workspaceForcedByInspectorCategory;

    private void BuildInspectorCategoryRail(Control inspector)
    {
        _inspectorRailBasePadding = inspector.Padding;
        _inspectorRail.BackColor = Theme.Panel;
        _inspectorRail.AutoScroll = true;
        _inspectorRail.AccessibleRole = AccessibleRole.PageTabList;
        _inspectorRail.AccessibleName = "Inspector categories";
        PaintLeftBorder(_inspectorRail);
        inspector.Controls.Add(_inspectorRail);
        foreach (var category in Enum.GetValues<InspectorCategory>())
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
        _colorInspectorPage.Dock = DockStyle.Fill;
        _colorInspectorPage.BackColor = Theme.Panel;
        _colorInspectorPage.ShowScrollBar = false;
        _colorInspectorPage.ContentPadding = new Padding(0, 0, 2, 0);
        _colorInspectorPage.Visible = false;
        inspector.Controls.Add(_colorInspectorPage);
        inspector.Layout += (_, _) => LayoutInspectorCategoryRail();
        inspector.VisibleChanged += (_, _) => LayoutInspectorCategoryRail();
        LayoutInspectorCategoryRail();
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
            host.Padding = new Padding(_inspectorRailBasePadding.Left, _inspectorRailBasePadding.Top,
                _inspectorRailBasePadding.Right + width, _inspectorRailBasePadding.Bottom);
            _inspectorRail.Visible = available;
            _inspectorRail.SetBounds(host.ClientSize.Width - width, 0, width, host.ClientSize.Height);
            var inset = ScaleResponsive(InspectorRailButtonInset, scale);
            var top = inset;
            foreach (var button in _inspectorCategoryButtons.Values)
            {
                button.SetBounds(inset, top, Math.Max(1, width - inset * 2), ScaleResponsive(InspectorRailButtonHeight, scale));
                top += button.Height + ScaleResponsive(InspectorRailButtonGap, scale);
            }
            ApplyInspectorPageVisibility();
            var presentedCategory = _inspectorCategory == InspectorCategory.None && _shotDirectorVisible
                ? InspectorCategory.Camera : _inspectorCategory;
            if (_presentedInspectorCategory != presentedCategory)
            {
                foreach (var (id, button) in _inspectorCategoryButtons)
                {
                    var active = id == presentedCategory;
                    Theme.StyleStandardButton(button, active);
                    button.AccessibleDescription = id == InspectorCategory.Physics
                        ? UiLocalization.T("Physics is not available.")
                        : $"{UiLocalization.T(InspectorCategoryLabel(id))} {UiLocalization.T(active ? "Visible" : "Hidden")}";
                }
                _presentedInspectorCategory = presentedCategory;
            }
            if (available) _inspectorRail.BringToFront();
        }
        finally
        {
            host.ResumeLayout(true);
            _layingOutInspectorRail = false;
        }
    }

    private void ApplyInspectorPageVisibility()
    {
        var automatic = _inspectorCategory == InspectorCategory.None;
        var color = _inspectorCategory == InspectorCategory.Color;
        _materialEditor.Visible = color;
        var camera = _inspectorCategory == InspectorCategory.Camera || automatic && _shotDirectorVisible;
        var scene = _inspectorCategory == InspectorCategory.Scene
            || automatic && !camera && _workspaceTabs.SelectedView == WorkspaceView.SceneEditor && !IsSymbolEditing;
        _colorInspectorPage.Visible = color;
        _basicInspectorPage.Visible = automatic && !camera && !scene;
        _sceneEditPage.Visible = scene;
        _animationPage.Visible = false;
        _shotDirectorPanel.Visible = camera;
        _layerBlendModePanel.Visible = automatic && !camera;
        if (!automatic || camera) _documentInfoPanel.Visible = false;
        if (color)
        {
            _materialEditor.Enabled = _colorMaterialAvailable && !IsSceneCompositionContext();
            _colorInspectorPage.BringToFront();
        }
        else
        {
            _materialEditor.Enabled = true;
            if (camera) _shotDirectorPanel.BringToFront();
            else if (scene) _sceneEditPage.BringToFront();
            else _basicInspectorPage.BringToFront();
        }
    }

    private void ToggleInspectorCategory(InspectorCategory category)
    {
        // DIAGNOSTIC (temporary): trace every rail click through its guards.
        _inspectorCategoryButtons.TryGetValue(category, out var probe);
        AppLog.Info($"[rail] click={category} open={_inspectorPanelOpen} railVisible={_inspectorRail.Visible}"
            + $" current={_inspectorCategory} enabled={probe?.Enabled} textEdit={IsTextEditActive()}"
            + $" view={_workspaceTabs.SelectedView} drawingContext={IsDrawingInspectorContext()}"
            + $" selection={_selectedObjects.Count}/{_selectedElement.IsValid}");
        if (!_inspectorPanelOpen || !_inspectorRail.Visible
            || !_inspectorCategoryButtons.TryGetValue(category, out var button) || !button.Enabled) return;
        if (!CommitTextEdit()) return;
        if (_inspectorCategory == category)
        {
            _inspectorCategory = InspectorCategory.None;
        }
        else
        {
            if (!PrepareInspectorCategoryWorkspace(category)) return;
            _inspectorCategory = category;
        }
        ApplyInspectorCategoryPresentation();
        AppLog.Info($"[rail] presented={_inspectorCategory} view={_workspaceTabs.SelectedView}"
            + $" basic={_basicInspectorPage.Visible} scene={_sceneEditPage.Visible}"
            + $" color={_colorInspectorPage.Visible} material={_materialEditor.Visible}"
            + $" materialParent={_materialEditor.Parent?.GetType().Name}");
    }

    /// <summary>
    /// Explicit-action entry: when the user does something that belongs to a domain
    /// (pick a fill color, open the shot page, add a light) the matching category comes
    /// forward instead of quietly editing state the user cannot see. Selection changes
    /// are handled separately without re-entering the inspector refresh chain.
    /// </summary>
    private void RevealInspectorCategory(InspectorCategory category, string reason)
    {
        if (!_inspectorPanelOpen || category == _inspectorCategory) return;
        if (!CanPresentInspectorCategory(category)) return;
        if (!CommitTextEdit()) return;
        if (!PrepareInspectorCategoryWorkspace(category)) return;
        _inspectorCategory = category;
        ApplyInspectorCategoryPresentation();
        AppLog.Info($"Inspector category revealed: {category} ({reason})");
    }

    private bool CanPresentInspectorCategory(InspectorCategory category) => category switch
    {
        InspectorCategory.None => true,
        // The material editor edits the current drawing selection; imported bitmaps have
        // no material, so never drag the user to a blank color page for them.
        InspectorCategory.Color => _colorMaterialAvailable,
        // No physics content yet; the rail keeps the button disabled.
        InspectorCategory.Physics => false,
        // Camera/Scene always have a page once their workspace is prepared.
        InspectorCategory.Camera => true,
        InspectorCategory.Scene => true,
        _ => false
    };

    // A category may need a workspace (Color draws, Camera/Scene live in the scene).
    // Returns false when that workspace could not be reached, so callers stay put.
    private bool PrepareInspectorCategoryWorkspace(InspectorCategory category)
    {
        if (category is not (InspectorCategory.Color or InspectorCategory.Camera or InspectorCategory.Scene))
            return true;
        if (IsSymbolEditing && category is InspectorCategory.Camera or InspectorCategory.Scene
            && !ExitSceneSymbolEditing()) return false;
        var view = _workspaceTabs.SelectedView;
        if (category == InspectorCategory.Color && !IsDrawingInspectorContext())
        {
            RememberInspectorCategoryWorkspace(view, WorkspaceView.BasicDrawing);
            _workspaceTabs.SelectView(WorkspaceView.BasicDrawing);
            return IsDrawingInspectorContext();
        }
        if (category is InspectorCategory.Camera or InspectorCategory.Scene && view != WorkspaceView.SceneEditor)
        {
            RememberInspectorCategoryWorkspace(view, WorkspaceView.SceneEditor);
            _workspaceTabs.SelectView(WorkspaceView.SceneEditor);
            return _workspaceTabs.SelectedView == WorkspaceView.SceneEditor;
        }
        return true;
    }

    // Ctrl+Alt+1..5: Properties, Color, Physics, Camera, Scene. Plain Ctrl+1..9 already
    // belongs to workspace switching (ShortcutProfiles.GetWorkspaceShortcutIndex), so the
    // category shortcut must not take it. Reveals the category (works with a closed rail)
    // and toggles it away when it is already active.
    internal bool HandleInspectorCategoryShortcut(Keys keyData)
    {
        if ((keyData & Keys.Modifiers) != (Keys.Control | Keys.Alt)) return false;
        var key = keyData & Keys.KeyCode;
        if (key is < Keys.D1 or > Keys.D5) return false;
        var category = (InspectorCategory)(key - Keys.D1);
        if (!_inspectorPanelOpen) ToggleInspectorPanel();
        if (_inspectorCategory == category) ToggleInspectorCategory(category);
        else RevealInspectorCategory(category, "keyboard shortcut");
        return true;
    }

    private void ApplyInspectorCategoryPresentation()
    {
        _inspectorTransition?.Cancel();
        // Properties must be honest: if a category dragged the workspace along, hand it
        // back before the pages are chosen, or the scene page stays the automatic page.
        RestoreInspectorWorkspaceIfNeeded();
        UpdateInspector();
        RefreshShotDirector(force: true);
        LayoutInspectorCategoryRail();
    }

    private void RememberInspectorCategoryWorkspace(WorkspaceView current, WorkspaceView target)
    {
        if (current == target) return;
        _workspaceBeforeInspectorCategory ??= current;
        _workspaceForcedByInspectorCategory = target;
    }

    private void RestoreInspectorWorkspaceIfNeeded()
    {
        // Returning to Properties must also return the workspace a category borrowed,
        // otherwise "Properties" keeps presenting the scene inspector it forced open.
        if (_inspectorCategory != InspectorCategory.None) return;
        var previous = _workspaceBeforeInspectorCategory;
        var forced = _workspaceForcedByInspectorCategory;
        _workspaceBeforeInspectorCategory = null;
        _workspaceForcedByInspectorCategory = null;
        if (previous is null || forced is null || previous == forced) return;
        // The user may have moved on by hand; only undo the switch we made ourselves.
        if (_workspaceTabs.SelectedView != forced) return;
        _workspaceTabs.SelectView(previous.Value);
    }

    private void CloseInspectorCategory()
    {
        _inspectorCategory = InspectorCategory.None;
        ApplyInspectorCategoryPresentation();
        _inspectorCategoryButtons[InspectorCategory.None].Focus();
    }

    private bool HandleInspectorCategoryKey(Keys keyData)
    {
        if (!_inspectorRail.ContainsFocus) return false;
        if (keyData is Keys.Enter or Keys.Space)
        {
            var focused = _inspectorCategoryButtons.Values.FirstOrDefault(button => button.Focused);
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
        var buttons = _inspectorCategoryButtons.Values.Where(button => button.Visible && button.Enabled).ToArray();
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
        InspectorCategory.Color => "Color",
        InspectorCategory.Physics => "Physics",
        InspectorCategory.Camera => "Camera",
        InspectorCategory.Scene => "Scene",
        _ => "Properties"
    };

    private static SvgIconKind InspectorCategoryIcon(InspectorCategory category) => category switch
    {
        InspectorCategory.Color => SvgIconKind.InspectorColor,
        InspectorCategory.Physics => SvgIconKind.InspectorPhysics,
        InspectorCategory.Camera => SvgIconKind.InspectorCamera,
        InspectorCategory.Scene => SvgIconKind.InspectorScene,
        _ => SvgIconKind.InspectorProperties
    };
}
