using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSceneToolPaletteVisibilityRegression()
    {
        RunSceneMaskSelectionRegression();
        RunReferenceWheelDollyDirectionRegression();
        RunReferenceCameraTransitionRegression();
        var basicVisibleTools = Enum.GetValues<ToolMode>()
            .Where(tool => MainForm.IsToolVisibleInWorkspace(WorkspaceView.BasicDrawing, tool))
            .ToHashSet();
        var sceneVisibleTools = Enum.GetValues<ToolMode>()
            .Where(tool => MainForm.IsToolVisibleInWorkspace(WorkspaceView.SceneEditor, tool))
            .ToHashSet();
        if (!basicVisibleTools.SetEquals(Enum.GetValues<ToolMode>().Where(tool => tool != ToolMode.Transform3D))
            || !sceneVisibleTools.SetEquals(
                [ToolMode.Select, ToolMode.Transform, ToolMode.Transform3D, ToolMode.Distort, ToolMode.Hand])
            || !MainForm.AllowsPressureBrushPointerInput(WorkspaceView.BasicDrawing, sceneMaskEditing: false)
            || !MainForm.AllowsPressureBrushPointerInput(WorkspaceView.SceneEditor, sceneMaskEditing: true)
            || MainForm.AllowsPressureBrushPointerInput(WorkspaceView.SceneEditor, sceneMaskEditing: false)
            || Enum.GetValues<ToolMode>().Any(tool =>
                MainForm.IsToolVisibleInWorkspace(WorkspaceView.Animation, tool)
                != MainForm.IsToolVisibleInWorkspace(WorkspaceView.SceneEditor, tool)))
        {
            throw new InvalidOperationException(
                "Workspace tool visibility did not preserve all Basic Drawing tools while limiting Scene Building to scene-edit tools.");
        }

        const System.Reflection.BindingFlags privateInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var toolPaletteField = typeof(MainForm).GetField("_toolPalette", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the tool palette.");
        var workspaceTabsField = typeof(MainForm).GetField("_workspaceTabs", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the workspace tabs.");
        var vaultButtonField = typeof(MainForm).GetField("_vaultButton", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the Vault button.");
        var shapeFlyoutField = typeof(MainForm).GetField("_shapeToolFlyout", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the shape flyout.");
        var selectionGroupField = typeof(MainForm).GetField("_selectionToolGroup", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the selection group.");
        var paintGroupField = typeof(MainForm).GetField("_paintToolGroup", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the paint group.");
        var toolField = typeof(MainForm).GetField("_tool", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not inspect the active tool.");
        var settingsField = typeof(MainForm).GetField("_applicationSettings", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not configure shortcuts.");
        var drawSettingsField = typeof(MainForm).GetField("_drawSettings", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not inspect draw settings.");
        var projectField = typeof(MainForm).GetField("_project", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not inspect the project.");
        var sceneField = typeof(MainForm).GetField("_scene", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not inspect the drawing scene.");
        var stageField = typeof(MainForm).GetField("_stage", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not inspect the Stage.");
        var zoomMetricField = typeof(MainForm).GetField("_zoom", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the zoom metric.");
        var sceneDimensionButtonField = typeof(MainForm).GetField("_sceneDimensionButton", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the dimension button.");
        var sceneProjectionButtonField = typeof(MainForm).GetField("_sceneProjectionButton", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the projection button.");
        var sceneEditorPanelField = typeof(MainForm).GetField("_sceneEditorPanel", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the Scene editor panel.");
        var cameraProjectionField = typeof(SceneEditorPanel).GetField("_cameraProjection", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the camera projection selector.");
        var referenceViewPadField = typeof(MainForm).GetField("_referenceViewPad", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the reference-view pad.");
        var spatialTransformPanelField = typeof(MainForm).GetField("_spatialTransformPanel", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the spatial Transform panel.");
        var sceneWorkflowControlsField = typeof(MainForm).GetField("_sceneWorkflowControls", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the Scene workflow controls.");
        var hierarchyPanelField = typeof(MainForm).GetField("_hierarchyPanel", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the hierarchy panel.");
        var basicInspectorPageField = typeof(MainForm).GetField("_basicInspectorPage", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the Basic Drawing inspector page.");
        var sceneInspectorPageField = typeof(MainForm).GetField("_sceneEditPage", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the Scene inspector page.");
        var materialEditorField = typeof(MainForm).GetField("_materialEditor", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the material editor.");
        var brushTipPanelField = typeof(MainForm).GetField("_brushTipPanel", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the brush-tip panel.");
        var mixingBrushPanelField = typeof(MainForm).GetField("_mixingBrushSettingsPanel", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the mixing-brush panel.");
        var textSettingsPanelField = typeof(MainForm).GetField("_textSettingsPanel", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the text-settings panel.");
        var drawSettingsPanelField = typeof(MainForm).GetField("_drawSettingsPanel", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the drawing-settings panel.");
        var shapeSettingsPanelField = typeof(MainForm).GetField("_shapeSettingsPanel", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the shape-settings panel.");
        var eraserOptionsStripField = typeof(MainForm).GetField("_eraserOptionsStrip", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not find the eraser options.");
        var projectDirtyField = typeof(MainForm).GetField("_projectDirty", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not suppress save prompts.");
        var sceneInstanceMoveActiveField = typeof(MainForm).GetField("_sceneInstanceMoveActive", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not inspect projected movement.");
        var spatialTransformEditSessionField = typeof(MainForm).GetField("_spatialTransformEditSession", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not inspect spatial edit cleanup.");
        var sceneTimelineUndoStackField = typeof(MainForm).GetField("_sceneTimelineUndoStack", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not inspect Scene undo state.");
        var showShapeFlyout = typeof(MainForm).GetMethod("ShowShapeToolFlyout", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not open the shape flyout.");
        var showToolPairFlyout = typeof(MainForm).GetMethod("ShowToolPairFlyout", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not open a paired-tool flyout.");
        var hideToolFlyouts = typeof(MainForm).GetMethod("HideToolFlyouts", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not close tool flyouts.");
        var refreshToolButtons = typeof(MainForm).GetMethod("RefreshToolButtons", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not refresh tool buttons.");
        var tryActivateShortcut = typeof(MainForm).GetMethod("TryActivateConfiguredToolShortcut", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not invoke configured shortcuts.");
        var handleSpatialTransformShortcut = typeof(MainForm).GetMethod("HandleSpatialTransformShortcut", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not invoke 3D Transform shortcuts.");
        var handleSceneLayerEditingContextChanged = typeof(MainForm).GetMethod(
            "HandleSceneLayerEditingContextChanged",
            privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not switch Scene Mask editing context.");
        var addTimelineMaskLayer = typeof(MainForm).GetMethod("AddTimelineMaskLayer", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not invoke Scene Mask creation.");
        var undoLastEdit = typeof(MainForm).GetMethod("UndoLastEdit", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not undo Scene Mask creation.");
        var invalidateSceneCompositionCache = typeof(MainForm).GetMethod(
            "InvalidateSceneCompositionCache",
            privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not invalidate Scene composition.");
        var rebuildSceneComposition = typeof(MainForm).GetMethod("RebuildSceneComposition", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not rebuild Scene composition.");
        var stageMouseDown = typeof(MainForm).GetMethod("StageMouseDown", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not begin projected input.");
        var stageMouseMove = typeof(MainForm).GetMethod("StageMouseMove", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not update projected input.");
        var stageMouseUp = typeof(MainForm).GetMethod("StageMouseUp", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not complete projected input.");
        var activateTool = typeof(MainForm).GetMethod(
            "ActivateTool",
            privateInstance,
            binder: null,
            types: [typeof(ToolMode)],
            modifiers: null)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not activate a drawing tool.");
        var setSceneInstanceSelection = typeof(MainForm).GetMethod(
            "SetSceneInstanceSelection",
            privateInstance,
            binder: null,
            types: [typeof(DrawingObjectInstanceDefinition), typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not select a Scene instance.");
        var setSelection = typeof(MainForm).GetMethod(
            "SetSelection",
            privateInstance,
            binder: null,
            types: [typeof(int), typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not select a Mask object.");
        var applyTextSettingsChange = typeof(MainForm).GetMethod("ApplyTextSettingsChange", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not apply Mask text settings.");
        var copySelectedObjects = typeof(MainForm).GetMethod("CopySelectedObjects", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not copy a Mask object.");
        var pasteCopiedObjects = typeof(MainForm).GetMethod("PasteCopiedObjects", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not paste a Mask object.");
        var deleteSelectedObject = typeof(MainForm).GetMethod("DeleteSelectedObject", privateInstance)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not delete a Mask object.");
        var importSvgFile = typeof(MainForm).GetMethod(
            "ImportSvgFile",
            privateInstance,
            binder: null,
            types: [typeof(string), typeof(PointF?)],
            modifiers: null)
            ?? throw new InvalidOperationException("Scene tool-palette regression could not import an SVG into a Mask.");

        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            Size = new Size(1280, 800)
        };
        try
        {
            var palette = toolPaletteField.GetValue(form) as FlowLayoutPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the tool palette.");
            var workspaceTabs = workspaceTabsField.GetValue(form) as WorkspaceTabs
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the workspace tabs.");
            var vaultButton = vaultButtonField.GetValue(form) as Button
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the Vault button.");
            var shapeFlyout = shapeFlyoutField.GetValue(form) as FlowLayoutPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the shape flyout.");
            var selectionGroup = selectionGroupField.GetValue(form)
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the selection group.");
            var paintGroup = paintGroupField.GetValue(form)
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the paint group.");
            var flyoutProperty = selectionGroup.GetType().GetProperty("Flyout")
                ?? throw new InvalidOperationException("Scene tool-palette regression could not inspect paired-tool flyouts.");
            var selectionFlyout = flyoutProperty.GetValue(selectionGroup) as FlowLayoutPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the selection flyout.");
            var paintFlyout = flyoutProperty.GetValue(paintGroup) as FlowLayoutPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the paint flyout.");
            var settings = settingsField.GetValue(form) as ApplicationSettings
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain application settings.");
            settingsField.SetValue(form, settings with
            {
                ActiveShortcutProfileId = ShortcutProfiles.TraditionalFlashProfileId,
                CustomShortcutProfiles = []
            });
            var drawSettings = drawSettingsField.GetValue(form) as DrawSettings
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain draw settings.");
            var project = projectField.GetValue(form) as VectorProject
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the project.");
            var stage = stageField.GetValue(form) as StageControl
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the Stage.");
            var zoomMetric = zoomMetricField.GetValue(form) as Label
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the zoom metric.");
            var dimensionButton = sceneDimensionButtonField.GetValue(form) as Button
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the dimension button.");
            var projectionButton = sceneProjectionButtonField.GetValue(form) as Button
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the projection button.");
            var sceneEditorPanel = sceneEditorPanelField.GetValue(form) as SceneEditorPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the Scene editor panel.");
            var cameraProjection = cameraProjectionField.GetValue(sceneEditorPanel) as ComboBox
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the camera projection selector.");
            var zoomOutButton = projectionButton.Parent?.Controls
                .OfType<SvgIconButton>()
                .SingleOrDefault(button => button.Icon == SvgIconKind.ZoomOut)
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the zoom-out button.");
            var referenceViewPad = referenceViewPadField.GetValue(form) as ReferenceViewPad
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the reference-view pad.");
            var spatialTransformPanel = spatialTransformPanelField.GetValue(form) as SpatialTransformPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the spatial Transform panel.");
            var sceneWorkflowControls = sceneWorkflowControlsField.GetValue(form) as TableLayoutPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the Scene workflow controls.");
            var hierarchyPanel = hierarchyPanelField.GetValue(form) as HierarchyPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the hierarchy panel.");
            var basicInspectorPage = basicInspectorPageField.GetValue(form) as ThemedScrollPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the Basic Drawing inspector page.");
            var sceneInspectorPage = sceneInspectorPageField.GetValue(form) as ThemedScrollPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the Scene inspector page.");
            var materialEditor = materialEditorField.GetValue(form) as MaterialEditorPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the material editor.");
            var brushTipPanel = brushTipPanelField.GetValue(form) as BrushTipPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the brush-tip panel.");
            var mixingBrushPanel = mixingBrushPanelField.GetValue(form) as MixingBrushSettingsPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the mixing-brush panel.");
            var textSettingsPanel = textSettingsPanelField.GetValue(form) as TextSettingsPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the text-settings panel.");
            var drawSettingsPanel = drawSettingsPanelField.GetValue(form) as DrawSettingsPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the drawing-settings panel.");
            var shapeSettingsPanel = shapeSettingsPanelField.GetValue(form) as ShapeSettingsPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the shape-settings panel.");
            var eraserOptionsStrip = eraserOptionsStripField.GetValue(form) as FlowLayoutPanel
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the eraser options.");
            Control[] drawingParameterPanels =
            [
                materialEditor,
                brushTipPanel,
                mixingBrushPanel,
                textSettingsPanel,
                drawSettingsPanel,
                shapeSettingsPanel
            ];

            ToolMode[] VisiblePaletteTools() => palette.Controls
                .Cast<Control>()
                .Where(control => control.Visible && control.Tag is ToolMode)
                .Select(control => (ToolMode)control.Tag!)
                .ToArray();
            static ToolMode[] VisibleFlyoutTools(FlowLayoutPanel flyout) => flyout.Controls
                .Cast<Control>()
                .Where(control => control.Visible && control.Tag is ToolMode)
                .Select(control => (ToolMode)control.Tag!)
                .ToArray();
            static bool IsSelectionTool(ToolMode tool) =>
                tool is ToolMode.Select or ToolMode.Transform or ToolMode.Transform3D or ToolMode.Distort;
            static bool IsShapeTool(ToolMode tool) =>
                tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star;
            static bool IsLineTool(ToolMode tool) =>
                tool is ToolMode.Line or ToolMode.Pen or ToolMode.SimplePen or ToolMode.Pencil;
            static bool IsBrushTool(ToolMode tool) =>
                tool is ToolMode.Brush or ToolMode.PressureBrush or ToolMode.MixingBrush;
            static bool HasFullDrawingPalette(IReadOnlyCollection<ToolMode> tools) =>
                tools.Count == 9
                && tools.Count(IsSelectionTool) == 1
                && tools.Count(IsShapeTool) == 1
                && tools.Count(IsLineTool) == 1
                && tools.Count(IsBrushTool) == 1
                && tools.Contains(ToolMode.Text)
                && tools.Count(tool => tool is ToolMode.Fill or ToolMode.InkBottle) == 1
                && tools.Contains(ToolMode.Eyedropper)
                && tools.Contains(ToolMode.Gradient)
                && tools.Contains(ToolMode.Eraser);
            static bool HasScenePalette(IReadOnlyCollection<ToolMode> tools) =>
                tools.Count == 1 && IsSelectionTool(tools.Single());
            bool TryShortcut(Keys keys) => tryActivateShortcut.Invoke(form, [keys]) is true;
            int SceneUndoCount()
            {
                var stack = sceneTimelineUndoStackField.GetValue(form)
                    ?? throw new InvalidOperationException("Scene tool-palette regression lost the Scene undo stack.");
                return stack.GetType().GetProperty("Count")?.GetValue(stack) is int count
                    ? count
                    : throw new InvalidOperationException("Scene tool-palette regression could not read the Scene undo count.");
            }

            form.Show();
            Application.DoEvents();
            var basicPaletteSize = palette.Size;
            if (workspaceTabs.SelectedView != WorkspaceView.BasicDrawing
                || !HasFullDrawingPalette(VisiblePaletteTools())
                || !vaultButton.Visible
                || projectionButton.Visible
                || projectionButton.Enabled)
            {
                throw new InvalidOperationException(
                    "Basic Drawing did not initially show the complete drawing toolbar and Vault.");
            }

            showShapeFlyout.Invoke(form, null);
            Application.DoEvents();
            if (!shapeFlyout.Visible
                || shapeFlyout.Controls.Cast<Control>().Count(control => control.Visible) != 5)
            {
                throw new InvalidOperationException("Basic Drawing could not open the complete shape flyout.");
            }

            workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
            Application.DoEvents();
            var scenePaletteSize = palette.Size;
            var projectionScene = project.Scenes[0];
            if (workspaceTabs.SelectedView != WorkspaceView.SceneEditor
                || !HasScenePalette(VisiblePaletteTools())
                || !vaultButton.Visible
                || !projectionButton.Visible
                || !projectionButton.Enabled
                || projectionButton.Text != UiLocalization.T("Orthographic")
                || projectionScene.Camera.Projection != CameraProjection.Orthographic
                || cameraProjection.SelectedIndex != 0
                || projectionButton.Right >= dimensionButton.Left
                || shapeFlyout.Visible
                || !palette.AutoSize
                || palette.WrapContents
                || scenePaletteSize.Height >= basicPaletteSize.Height)
            {
                throw new InvalidOperationException(
                    "Scene Building 2D did not hide drawing-only tools, close flyouts, or shrink the tool palette.");
            }

            projectionButton.PerformClick();
            Application.DoEvents();
            if (dimensionButton.Text != "2D"
                || projectionScene.Camera.Projection != CameraProjection.Perspective
                || projectionScene.Camera.Depth < 1000
                || projectionButton.Text != UiLocalization.T("Perspective")
                || cameraProjection.SelectedIndex != 1)
            {
                throw new InvalidOperationException(
                    "The 2D Scene projection shortcut did not update the saved camera mode and inspector together.");
            }
            projectionButton.PerformClick();
            Application.DoEvents();
            if (projectionScene.Camera.Projection != CameraProjection.Orthographic
                || projectionButton.Text != UiLocalization.T("Orthographic")
                || cameraProjection.SelectedIndex != 0)
            {
                throw new InvalidOperationException(
                    "The 2D Scene projection shortcut did not restore orthographic mode across the camera and inspector.");
            }

            if (TryShortcut(Keys.R)
                || toolField.GetValue(form) is not ToolMode.Select
                || !TryShortcut(Keys.Q)
                || toolField.GetValue(form) is not ToolMode.Transform
                || !TryShortcut(Keys.V)
                || toolField.GetValue(form) is not ToolMode.Select)
            {
                throw new InvalidOperationException(
                    "Scene Building tool shortcuts activated a hidden drawing tool or blocked available selection tools.");
            }

            drawSettings.ShapeKind = ShapeKind.Ellipse;
            drawSettings.NotifyChanged();
            Application.DoEvents();
            if (toolField.GetValue(form) is not ToolMode.Select
                || !HasScenePalette(VisiblePaletteTools()))
            {
                throw new InvalidOperationException(
                    "A Scene Building draw-settings change bypassed tool availability and re-armed a hidden shape tool.");
            }

            showToolPairFlyout.Invoke(form, [paintGroup]);
            Application.DoEvents();
            if (paintFlyout.Visible)
            {
                throw new InvalidOperationException("Scene Building opened an empty drawing-only paint flyout.");
            }
            showToolPairFlyout.Invoke(form, [selectionGroup]);
            Application.DoEvents();
            if (!selectionFlyout.Visible
                || selectionFlyout.Controls.Cast<Control>().Count(control => control.Visible) != 3)
            {
                throw new InvalidOperationException(
                    "Scene Building did not retain Select, Free Transform, and Distort in the selection flyout.");
            }

            dimensionButton.PerformClick();
            Application.DoEvents();
            showToolPairFlyout.Invoke(form, [selectionGroup]);
            Application.DoEvents();
            if (dimensionButton.Text != "3D"
                || !HasScenePalette(VisiblePaletteTools())
                || !VisibleFlyoutTools(selectionFlyout).ToHashSet().SetEquals(
                    [ToolMode.Select, ToolMode.Transform3D])
                || !spatialTransformPanel.Visible
                || referenceViewPad.Visible
                || !ReferenceEquals(spatialTransformPanel.Parent, hierarchyPanel.Parent)
                || spatialTransformPanel.Top < sceneWorkflowControls.Bottom
                || spatialTransformPanel.Bottom > hierarchyPanel.Top
                || TryShortcut(Keys.B)
                || !TryShortcut(Keys.Q)
                || toolField.GetValue(form) is not ToolMode.Transform3D
                || handleSpatialTransformShortcut.Invoke(form, [Keys.G]) is not true
                || handleSpatialTransformShortcut.Invoke(form, [Keys.R]) is not true
                || handleSpatialTransformShortcut.Invoke(form, [Keys.S]) is not true
                || toolField.GetValue(form) is not ToolMode.Transform3D
                || selectionGroup.GetType().GetProperty("ActiveTool")?.GetValue(selectionGroup) is not ToolMode.Transform3D
                || selectionGroup.GetType().GetProperty("ParentButton")?.GetValue(selectionGroup) is not Button transform3DButton
                || transform3DButton.Tag is not ToolMode.Transform3D)
            {
                throw new InvalidOperationException(
                    "Scene Building 3D did not expose only Select/3D Transform or synchronize its Transform panel and shortcuts.");
            }

            if (!projectionButton.Visible
                || !projectionButton.Enabled
                || projectionButton.Text != UiLocalization.T("Orthographic")
                || projectionScene.Camera.Projection != CameraProjection.Orthographic
                || cameraProjection.SelectedIndex != 0
                || projectionButton.Right >= dimensionButton.Left
                || dimensionButton.Left - projectionButton.Right > 12)
            {
                throw new InvalidOperationException(
                    "Scene Building 3D did not place or synchronize the orthographic/perspective shortcut beside the 3D button.");
            }

            projectionButton.PerformClick();
            Application.DoEvents();
            if (projectionScene.Camera.Projection != CameraProjection.Perspective
                || projectionButton.Text != UiLocalization.T("Perspective")
                || cameraProjection.SelectedIndex != 1)
            {
                throw new InvalidOperationException(
                    "The projection shortcut did not switch the scene, button, and inspector to perspective together.");
            }

            projectionButton.PerformClick();
            Application.DoEvents();
            if (projectionScene.Camera.Projection != CameraProjection.Orthographic
                || projectionButton.Text != UiLocalization.T("Orthographic")
                || cameraProjection.SelectedIndex != 0)
            {
                throw new InvalidOperationException(
                    "The projection shortcut did not restore orthographic mode across the scene, button, and inspector.");
            }
            Console.WriteLine("scene_projection_shortcut=ok");

            var regularFormSize = form.Size;
            form.Size = form.MinimumSize;
            Application.DoEvents();
            if (!projectionButton.Visible
                || projectionButton.Right >= dimensionButton.Left
                || zoomOutButton.Right >= projectionButton.Left
                || zoomMetric.Visible && zoomMetric.Right + 6 > zoomOutButton.Left)
            {
                throw new InvalidOperationException(
                    "The minimum-width Scene toolbar overlapped its projection, dimension, zoom, or metric controls.");
            }
            form.Size = regularFormSize;
            Application.DoEvents();

            dimensionButton.PerformClick();
            Application.DoEvents();
            showToolPairFlyout.Invoke(form, [selectionGroup]);
            Application.DoEvents();
            if (dimensionButton.Text != "2D"
                || !projectionButton.Visible
                || !projectionButton.Enabled
                || toolField.GetValue(form) is not ToolMode.Transform
                || spatialTransformPanel.Visible
                || !referenceViewPad.Visible
                || !VisibleFlyoutTools(selectionFlyout).ToHashSet().SetEquals(
                    [ToolMode.Select, ToolMode.Transform, ToolMode.Distort])
                || selectionGroup.GetType().GetProperty("ParentButton")?.GetValue(selectionGroup) is not Button transform2DButton
                || transform2DButton.Tag is ToolMode.Transform3D)
            {
                throw new InvalidOperationException(
                    "Returning from 3D did not restore the 2D Transform tool, view pad, and hidden-tool state.");
            }

            stage.SetReferenceViewDirection(ReferenceViewDirection.Right);
            refreshToolButtons.Invoke(form, null);
            showToolPairFlyout.Invoke(form, [selectionGroup]);
            Application.DoEvents();
            var sideViewTools = VisibleFlyoutTools(selectionFlyout);
            var sideViewTransformShortcutActivated = TryShortcut(Keys.Q);
            if (!sideViewTools.SequenceEqual([ToolMode.Select])
                || sideViewTransformShortcutActivated)
            {
                throw new InvalidOperationException(
                    "A 2D side reference view exposed a transform tool that cannot operate in that projection: "
                    + $"tools=[{string.Join(',', sideViewTools)}], shortcut={sideViewTransformShortcutActivated}, "
                    + $"dimension={stage.ReferenceDimension}, direction={stage.Reference2DViewDirection}, "
                    + $"projection={stage.UsesReferenceProjection}, active={toolField.GetValue(form)}.");
            }
            stage.SetReferenceViewDirection(ReferenceViewDirection.Front);
            refreshToolButtons.Invoke(form, null);

            dimensionButton.PerformClick();
            Application.DoEvents();
            if (dimensionButton.Text != "3D")
            {
                throw new InvalidOperationException("Scene Mask regression could not start from the 3D Scene view.");
            }

            var activeScene = project.Scenes[0];
            var contentLayer = activeScene.Layers.First(layer => layer.Kind == SceneLayerKind.Content);
            var unrelatedDrawingObject = project.DrawingObjects[0];
            var unrelatedDrawingObjectName = unrelatedDrawingObject.Name;
            var sourceDrawingObject = project.AddDrawingObject("Masked source");
            sourceDrawingObject.Scene.AddObject(
                sourceDrawingObject.Scene.ActiveLayer,
                PointF.Empty,
                new SizeF(320, 240),
                0,
                0,
                Color.CornflowerBlue,
                4,
                ShapeKind.Rectangle);
            if (!project.TryAddSceneInstance(
                    activeScene.Id,
                    sourceDrawingObject.Id,
                    PointF.Empty,
                    0,
                    contentLayer.Id,
                    out var sourceSceneInstance)
                || sourceSceneInstance is null)
            {
                throw new InvalidOperationException(
                    "Scene tool-palette regression could not create masked Scene content.");
            }
            sourceSceneInstance.Z = 500;
            sourceSceneInstance.RotationY = 25;
            invalidateSceneCompositionCache.Invoke(form, null);
            rebuildSceneComposition.Invoke(form, [false, false]);
            sourceSceneInstance = RunSpatialTransformKeyboardRegression(
                form,
                stage,
                activeScene,
                sourceSceneInstance.Id);

            dimensionButton.PerformClick();
            Application.DoEvents();
            stage.CompleteReferenceCameraTransition();
            Application.DoEvents();
            showToolPairFlyout.Invoke(form, [selectionGroup]);
            Application.DoEvents();
            if (!stage.UsesReferenceProjection
                || !VisibleFlyoutTools(selectionFlyout).ToHashSet().SetEquals(
                    [ToolMode.Select, ToolMode.Transform, ToolMode.Distort])
                || !stage.TransformBoundsVisible)
            {
                throw new InvalidOperationException(
                    "A spatial Scene instance lost Free Transform or Distort after switching to the projected 2D Front view.");
            }
            hideToolFlyouts.Invoke(form, null);

            var projectedTransformStart = sourceSceneInstance.EvaluateState(0);
            var projectedTransformUndo = SceneUndoCount();
            var projectedTransformGeometry = stage.GetTransformOverlayScreenGeometry();
            var projectedScaleHandle = Point.Round(projectedTransformGeometry.BottomRight);
            var projectedScaleHit = stage.HitTestTransformHandle(projectedScaleHandle);
            if (projectedScaleHit != TransformHandleKind.BottomRight)
            {
                throw new InvalidOperationException(
                    $"Projected 2D Free Transform could not hit its visible resize handle: {projectedScaleHit}.");
            }
            stageMouseDown.Invoke(
                form,
                [stage, new MouseEventArgs(MouseButtons.Left, 1, projectedScaleHandle.X, projectedScaleHandle.Y, 0)]);
            var projectedScaleTarget = new Point(projectedScaleHandle.X + 24, projectedScaleHandle.Y + 16);
            stageMouseMove.Invoke(
                form,
                [stage, new MouseEventArgs(MouseButtons.Left, 0, projectedScaleTarget.X, projectedScaleTarget.Y, 0)]);
            var projectedPreviewGeometry = stage.GetTransformOverlayScreenGeometry();
            stageMouseUp.Invoke(
                form,
                [stage, new MouseEventArgs(MouseButtons.Left, 1, projectedScaleTarget.X, projectedScaleTarget.Y, 0)]);
            Application.DoEvents();
            var projectedTransformMoved = sourceSceneInstance.EvaluateState(0);
            if (Math.Abs(projectedTransformMoved.ScaleX - projectedTransformStart.ScaleX) <= 0.001f
                && Math.Abs(projectedTransformMoved.ScaleY - projectedTransformStart.ScaleY) <= 0.001f
                || SceneUndoCount() != projectedTransformUndo + 1
                || System.Numerics.Vector2.Distance(
                    new System.Numerics.Vector2(
                        projectedPreviewGeometry.BottomRight.X,
                        projectedPreviewGeometry.BottomRight.Y),
                    new System.Numerics.Vector2(
                        stage.GetTransformOverlayScreenGeometry().BottomRight.X,
                        stage.GetTransformOverlayScreenGeometry().BottomRight.Y)) > 1f)
            {
                throw new InvalidOperationException(
                    "Projected 2D Free Transform did not resize the spatial Scene instance as one undoable, stable preview edit: "
                    + $"start=({projectedTransformStart.ScaleX:0.###},{projectedTransformStart.ScaleY:0.###}), "
                    + $"moved=({projectedTransformMoved.ScaleX:0.###},{projectedTransformMoved.ScaleY:0.###}), "
                    + $"undo={projectedTransformUndo}->{SceneUndoCount()}.");
            }
            if (undoLastEdit.Invoke(form, null) is not true)
            {
                throw new InvalidOperationException("Projected 2D Free Transform could not be undone.");
            }
            sourceSceneInstance = activeScene.Instances.First(instance =>
                string.Equals(instance.Id, sourceSceneInstance.Id, StringComparison.Ordinal));
            setSceneInstanceSelection.Invoke(form, [sourceSceneInstance, false]);

            activateTool.Invoke(form, [ToolMode.Distort]);
            Application.DoEvents();
            if (!stage.DistortBoundsVisible)
            {
                throw new InvalidOperationException("Projected 2D Distort did not expose its editable envelope.");
            }
            var projectedDistortUndo = SceneUndoCount();
            var projectedDistortGeometry = stage.GetDistortOverlayScreenGeometry();
            var projectedDistortHandle = projectedDistortGeometry.VisualHandles
                .First(handle => handle.Reference.Kind == DistortHandleKind.Anchor)
                .Point;
            var projectedDistortStart = Point.Round(projectedDistortHandle);
            var projectedDistortTarget = new Point(projectedDistortStart.X + 22, projectedDistortStart.Y + 13);
            stageMouseDown.Invoke(
                form,
                [stage, new MouseEventArgs(MouseButtons.Left, 1, projectedDistortStart.X, projectedDistortStart.Y, 0)]);
            stageMouseMove.Invoke(
                form,
                [stage, new MouseEventArgs(MouseButtons.Left, 0, projectedDistortTarget.X, projectedDistortTarget.Y, 0)]);
            stageMouseUp.Invoke(
                form,
                [stage, new MouseEventArgs(MouseButtons.Left, 1, projectedDistortTarget.X, projectedDistortTarget.Y, 0)]);
            Application.DoEvents();
            if (sourceSceneInstance.EvaluateState(0).Distortion is not { IsValid: true }
                || SceneUndoCount() != projectedDistortUndo + 1)
            {
                throw new InvalidOperationException(
                    "Projected 2D Distort did not persist its envelope as one undoable Scene instance edit.");
            }
            if (undoLastEdit.Invoke(form, null) is not true)
            {
                throw new InvalidOperationException("Projected 2D Distort could not be undone.");
            }
            sourceSceneInstance = activeScene.Instances.First(instance =>
                string.Equals(instance.Id, sourceSceneInstance.Id, StringComparison.Ordinal));
            activateTool.Invoke(form, [ToolMode.Select]);
            var projectedScene = sceneField.GetValue(form) as VectorScene
                ?? throw new InvalidOperationException("Scene projected-move regression did not obtain the composition scene.");
            var compositionResult = stage.SceneCompositionResult
                ?? throw new InvalidOperationException("Scene projected-move regression did not obtain composition ownership.");
            var projectedObject = Enumerable.Range(0, projectedScene.ObjectCount)
                .Where(index =>
                {
                    if (!compositionResult.TryGetOwner(index, out var owner)) return false;
                    var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId)
                        ? owner.InstanceId
                        : owner.RootInstanceId;
                    return string.Equals(rootInstanceId, sourceSceneInstance.Id, StringComparison.Ordinal);
                })
                .DefaultIfEmpty(-1)
                .First();
            var projectedCenterSource = projectedObject >= 0
                ? new PointF(projectedScene.X[projectedObject], projectedScene.Y[projectedObject])
                : PointF.Empty;
            if (dimensionButton.Text != "2D"
                || !projectionButton.Visible
                || !projectionButton.Enabled
                || !stage.UsesSpatialFrontProjection
                || projectedObject < 0
                || !stage.TryProjectScenePoint(projectedObject, projectedCenterSource, out var projectedCenter, out _))
            {
                throw new InvalidOperationException(
                    "Scene projected-move regression could not prepare a spatial object in the 2D Front view.");
            }

            var frontDelta = MainForm.ConstrainProjectedSceneMoveDelta(
                ReferenceViewDirection.Front,
                new System.Numerics.Vector3(3, 4, 5));
            var sideDelta = MainForm.ConstrainProjectedSceneMoveDelta(
                ReferenceViewDirection.Right,
                new System.Numerics.Vector3(3, 4, 5));
            var topDelta = MainForm.ConstrainProjectedSceneMoveDelta(
                ReferenceViewDirection.Top,
                new System.Numerics.Vector3(3, 4, 5));
            if (frontDelta != new System.Numerics.Vector3(3, 4, 0)
                || sideDelta != new System.Numerics.Vector3(0, 4, 5)
                || topDelta != new System.Numerics.Vector3(3, 0, 5))
            {
                throw new InvalidOperationException(
                    "Scene projected Select movement did not lock the axis normal to each 2D reference view.");
            }

            var projectedStart = Point.Round(projectedCenter);
            var projectedEnd = new Point(projectedStart.X + 28, projectedStart.Y + 18);
            var instanceStart = sourceSceneInstance.EvaluateState(0);
            var undoCountBeforeMove = SceneUndoCount();
            stageMouseDown.Invoke(
                form,
                [stage, new MouseEventArgs(MouseButtons.Left, 1, projectedStart.X, projectedStart.Y, 0)]);
            if (sceneInstanceMoveActiveField.GetValue(form) is not true
                || spatialTransformEditSessionField.GetValue(form) is null)
            {
                throw new InvalidOperationException(
                    "Select did not start a movable Scene instance session in the projected 2D Front view.");
            }
            stageMouseMove.Invoke(
                form,
                [stage, new MouseEventArgs(MouseButtons.Left, 0, projectedEnd.X, projectedEnd.Y, 0)]);
            stageMouseUp.Invoke(
                form,
                [stage, new MouseEventArgs(MouseButtons.Left, 1, projectedEnd.X, projectedEnd.Y, 0)]);
            var instanceMoved = sourceSceneInstance.EvaluateState(0);
            if (Math.Abs(instanceMoved.X - instanceStart.X) <= 0.001f
                || Math.Abs(instanceMoved.Y - instanceStart.Y) <= 0.001f
                || Math.Abs(instanceMoved.Z - instanceStart.Z) > 0.001f
                || SceneUndoCount() != undoCountBeforeMove + 1
                || sceneInstanceMoveActiveField.GetValue(form) is true
                || spatialTransformEditSessionField.GetValue(form) is not null
                || stage.Capture)
            {
                throw new InvalidOperationException(
                    "Projected 2D Select movement did not update XY, preserve Z, create one undo, or clean up its pointer session.");
            }
            if (undoLastEdit.Invoke(form, null) is not true)
            {
                throw new InvalidOperationException("Projected 2D Select movement could not be undone.");
            }
            var restoredSceneInstance = activeScene.Instances.FirstOrDefault(instance =>
                string.Equals(instance.Id, sourceSceneInstance.Id, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Undo removed the projected 2D Scene instance.");
            var instanceRestored = restoredSceneInstance.EvaluateState(0);
            if (Math.Abs(instanceRestored.X - instanceStart.X) > 0.001f
                || Math.Abs(instanceRestored.Y - instanceStart.Y) > 0.001f
                || Math.Abs(instanceRestored.Z - instanceStart.Z) > 0.001f)
            {
                throw new InvalidOperationException("Undo did not restore the projected 2D Scene instance pose.");
            }
            Console.WriteLine("scene_2d_projected_select_move=ok");

            dimensionButton.PerformClick();
            Application.DoEvents();
            if (dimensionButton.Text != "3D")
            {
                throw new InvalidOperationException(
                    "Scene projected-move regression did not restore the 3D view before Mask editing.");
            }
            contentLayer = activeScene.FindLayer(contentLayer.Id)
                ?? throw new InvalidOperationException("Scene Mask regression lost its content layer after undo.");
            addTimelineMaskLayer.Invoke(form, null);
            var sceneMask = activeScene.FindLayer(contentLayer.MaskLayerId);
            if (sceneMask is not { Kind: SceneLayerKind.Mask, MaskScene: not null }
                || !ReferenceEquals(sceneField.GetValue(form), sceneMask.MaskScene))
            {
                throw new InvalidOperationException(
                    "The Scene timeline command did not create, select, and bind an editable Scene Mask.");
            }
            var firstMaskScene = sceneMask.MaskScene;
            if (undoLastEdit.Invoke(form, null) is not true)
            {
                throw new InvalidOperationException("Scene Mask creation could not be undone.");
            }
            Application.DoEvents();
            contentLayer = activeScene.Layers.First(layer => layer.Kind == SceneLayerKind.Content);
            if (!string.IsNullOrWhiteSpace(contentLayer.MaskLayerId)
                || activeScene.Layers.Any(layer => layer.Kind == SceneLayerKind.Mask)
                || ReferenceEquals(sceneField.GetValue(form), firstMaskScene)
                || !dimensionButton.Enabled
                || dimensionButton.Text != "3D"
                || !projectionButton.Visible
                || !projectionButton.Enabled)
            {
                throw new InvalidOperationException(
                    "Undoing Scene Mask creation did not restore the content layer, Stage binding, and saved 3D view.");
            }

            addTimelineMaskLayer.Invoke(form, null);
            sceneMask = activeScene.FindLayer(contentLayer.MaskLayerId);
            if (sceneMask is not { Kind: SceneLayerKind.Mask, MaskScene: not null })
            {
                throw new InvalidOperationException("Scene Mask could not be recreated after undo.");
            }
            Application.DoEvents();
            showToolPairFlyout.Invoke(form, [selectionGroup]);
            Application.DoEvents();
            var maskPaletteTools = VisiblePaletteTools();
            var maskSelectionTools = VisibleFlyoutTools(selectionFlyout);
            var maskBrushShortcut = TryShortcut(Keys.B);
            var maskActiveTool = toolField.GetValue(form);
            if (!HasFullDrawingPalette(maskPaletteTools)
                || !vaultButton.Visible
                || dimensionButton.Enabled
                || dimensionButton.Text != "2D"
                || projectionButton.Visible
                || projectionButton.Enabled
                || referenceViewPad.Visible
                || spatialTransformPanel.Visible
                || maskSelectionTools.Contains(ToolMode.Transform3D)
                || !maskBrushShortcut
                || maskActiveTool is not ToolMode.Brush
                || drawingParameterPanels.Any(panel => !ReferenceEquals(panel.Parent, sceneInspectorPage.Content))
                || !materialEditor.Visible
                || !brushTipPanel.Visible)
            {
                throw new InvalidOperationException(
                    "Scene Mask editing did not enable Basic Drawing tools while disabling 3D-only controls: "
                    + $"palette=[{string.Join(',', maskPaletteTools)}], selection=[{string.Join(',', maskSelectionTools)}], "
                    + $"vault={vaultButton.Visible}, dimension={dimensionButton.Text}, dimensionEnabled={dimensionButton.Enabled}, "
                    + $"referencePad={referenceViewPad.Visible}, spatialPanel={spatialTransformPanel.Visible}, "
                    + $"brushShortcut={maskBrushShortcut}, activeTool={maskActiveTool}.");
            }

            activateTool.Invoke(form, [ToolMode.MixingBrush]);
            Application.DoEvents();
            if (!mixingBrushPanel.Visible || !brushTipPanel.Visible)
            {
                throw new InvalidOperationException(
                    "Scene Mask mixing-brush parameters were not visible with the tool active.");
            }
            activateTool.Invoke(form, [ToolMode.Pencil]);
            Application.DoEvents();
            if (!drawSettingsPanel.Visible || brushTipPanel.Visible)
            {
                throw new InvalidOperationException(
                    "Scene Mask pencil parameters did not replace the brush parameters.");
            }
            activateTool.Invoke(form, [ToolMode.Polygon]);
            Application.DoEvents();
            if (!shapeSettingsPanel.Visible)
            {
                throw new InvalidOperationException(
                    "Scene Mask polygon parameters were not visible with the tool active.");
            }
            activateTool.Invoke(form, [ToolMode.Text]);
            Application.DoEvents();
            if (!textSettingsPanel.Visible)
            {
                throw new InvalidOperationException(
                    "Scene Mask text parameters were not visible with the tool active.");
            }
            activateTool.Invoke(form, [ToolMode.Eraser]);
            Application.DoEvents();
            if (!eraserOptionsStrip.Visible || !brushTipPanel.Visible)
            {
                throw new InvalidOperationException(
                    "Scene Mask eraser did not expose its brush and stroke/fill parameters.");
            }

            var maskScene = activeScene.FindMaskScene(sceneMask.Id)
                ?? throw new InvalidOperationException("Scene tool-palette regression lost the editable Mask scene.");
            var maskTrack = activeScene.Timeline.FindTrackByTargetId(sceneMask.Id)
                ?? throw new InvalidOperationException("Scene tool-palette regression lost the outer Mask track.");
            bool MaskStateMatches(bool populated)
            {
                var matchingClips = stage.SceneCompositionMaskClips
                    .Where(clip => ReferenceEquals(clip.MaskScene, maskScene))
                    .ToArray();
                return maskTrack.EvaluateExposure(0).HasContent == populated
                    && matchingClips.Length > 0
                    && matchingClips.All(clip => populated ? clip.Frame == 0 : clip.Frame < 0);
            }

            if (!MaskStateMatches(populated: false))
            {
                throw new InvalidOperationException(
                    "A new empty Scene Mask did not start with a blank outer Cel and clip.");
            }

            activateTool.Invoke(form, [ToolMode.Text]);
            var maskText = TextGeometry.NormalizeForAuthoring(new TextObjectData(
                "Mask text",
                TextGeometry.FallbackFontFamilyName,
                24,
                TextFontStyle.Regular,
                TextHorizontalAlignment.Left,
                new SizeF(2400, 1200)));
            var maskTextObject = maskScene.AddTextObject(
                maskScene.ActiveLayer,
                PointF.Empty,
                maskText,
                Color.White);
            setSelection.Invoke(form, [maskTextObject, false]);
            Application.DoEvents();
            textSettingsPanel.SetSettings(
                textSettingsPanel.FontFamilyName,
                36,
                TextFontStyle.Bold,
                TextHorizontalAlignment.Center);
            applyTextSettingsChange.Invoke(form, null);
            Application.DoEvents();
            if (!maskScene.TryGetTextObjectData(maskTextObject, out var updatedMaskText)
                || Math.Abs(updatedMaskText.FontSizePoints - 36) > 0.001f
                || updatedMaskText.FontStyle != TextFontStyle.Bold
                || updatedMaskText.Alignment != TextHorizontalAlignment.Center
                || !MaskStateMatches(populated: true))
            {
                throw new InvalidOperationException(
                    "Editing an existing Scene Mask text object did not update its parameters, outer Cel, and clip.");
            }

            if (copySelectedObjects.Invoke(form, null) is not true
                || deleteSelectedObject.Invoke(form, null) is not true
                || !MaskStateMatches(populated: false))
            {
                throw new InvalidOperationException(
                    "Deleting the final copied Scene Mask object did not make its outer Cel and clip blank.");
            }
            if (pasteCopiedObjects.Invoke(form, null) is not true
                || !MaskStateMatches(populated: true))
            {
                throw new InvalidOperationException(
                    "Pasting into an empty Scene Mask did not populate its outer Cel and clip.");
            }
            if (deleteSelectedObject.Invoke(form, null) is not true
                || !MaskStateMatches(populated: false))
            {
                throw new InvalidOperationException(
                    "Deleting the pasted Scene Mask object did not restore a blank outer Cel and clip.");
            }

            var svgPath = Path.Combine(Path.GetTempPath(), $"v2d-mask-{Guid.NewGuid():N}.svg");
            try
            {
                File.WriteAllText(
                    svgPath,
                    "<svg xmlns='http://www.w3.org/2000/svg' width='24' height='24'>"
                    + "<rect width='24' height='24' fill='white'/></svg>");
                importSvgFile.Invoke(form, [svgPath, null]);
                Application.DoEvents();
                if (!MaskStateMatches(populated: true)
                    || !string.Equals(
                        unrelatedDrawingObject.Name,
                        unrelatedDrawingObjectName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Importing SVG into an empty Scene Mask did not populate its outer Cel and clip, "
                        + "or renamed an unrelated empty Drawing Object.");
                }
            }
            finally
            {
                File.Delete(svgPath);
            }
            if (deleteSelectedObject.Invoke(form, null) is not true
                || !MaskStateMatches(populated: false))
            {
                throw new InvalidOperationException(
                    "Deleting the imported Scene Mask SVG did not restore a blank outer Cel and clip.");
            }

            workspaceTabs.SelectedView = WorkspaceView.BasicDrawing;
            Application.DoEvents();
            workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
            Application.DoEvents();
            if (dimensionButton.Enabled
                || dimensionButton.Text != "2D"
                || projectionButton.Visible
                || projectionButton.Enabled
                || !ReferenceEquals(sceneField.GetValue(form), sceneMask.MaskScene))
            {
                throw new InvalidOperationException(
                    "Returning to Scene Building with a Mask selected did not restore its 2D-only view controls.");
            }

            activeScene.SetActiveLayer(contentLayer.Id);
            handleSceneLayerEditingContextChanged.Invoke(form, null);
            Application.DoEvents();
            if (!dimensionButton.Enabled
                || dimensionButton.Text != "3D"
                || !projectionButton.Visible
                || !projectionButton.Enabled
                || !spatialTransformPanel.Visible
                || referenceViewPad.Visible
                || eraserOptionsStrip.Visible
                || drawingParameterPanels.Any(panel => !ReferenceEquals(panel.Parent, basicInspectorPage.Content)))
            {
                throw new InvalidOperationException(
                    "Leaving Scene Mask editing did not restore the saved 3D Scene view and spatial controls.");
            }

            workspaceTabs.SelectedView = WorkspaceView.BasicDrawing;
            Application.DoEvents();
            if (!HasFullDrawingPalette(VisiblePaletteTools())
                || !vaultButton.Visible
                || projectionButton.Visible
                || projectionButton.Enabled
                || selectionFlyout.Visible
                || !TryShortcut(Keys.B)
                || toolField.GetValue(form) is not ToolMode.Brush)
            {
                throw new InvalidOperationException(
                    "Returning to Basic Drawing did not restore the toolbar, flyout state, and drawing shortcuts.");
            }

            showShapeFlyout.Invoke(form, null);
            Application.DoEvents();
            if (!shapeFlyout.Visible
                || shapeFlyout.Controls.Cast<Control>().Count(control => control.Visible) != 5)
            {
                throw new InvalidOperationException(
                    "Returning to Basic Drawing did not restore all shape-flyout tools.");
            }
            hideToolFlyouts.Invoke(form, null);

            var drawingScene = sceneField.GetValue(form) as VectorScene
                ?? throw new InvalidOperationException("Scene tool-palette regression did not obtain the restored drawing scene.");
            drawingScene.SetLayerLocked(drawingScene.ActiveLayer, true);
            refreshToolButtons.Invoke(form, null);
            Application.DoEvents();
            var lockedTools = palette.Controls
                .Cast<Control>()
                .Where(control => control.Visible
                    && control.Tag is ToolMode tool
                    && !IsSelectionTool(tool))
                .ToArray();
            if (!HasFullDrawingPalette(VisiblePaletteTools())
                || lockedTools.Length != 8
                || lockedTools.Any(control => control.Enabled))
            {
                throw new InvalidOperationException(
                    "Locking a Basic Drawing layer hid drawing tools instead of keeping them visible and disabled.");
            }
            drawingScene.SetLayerLocked(drawingScene.ActiveLayer, false);
            refreshToolButtons.Invoke(form, null);
            hideToolFlyouts.Invoke(form, null);

            Console.WriteLine("scene_tool_palette_visibility=ok");
        }
        finally
        {
            projectDirtyField.SetValue(form, false);
            form.Hide();
        }
    }

    private static DrawingObjectInstanceDefinition RunSpatialTransformKeyboardRegression(
        MainForm form,
        StageControl stage,
        SceneDefinition scene,
        string instanceId)
    {
        const System.Reflection.BindingFlags privateInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var keyboardActiveField = typeof(MainForm).GetField("_spatialTransformKeyboardActive", privateInstance)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not inspect modal state.");
        var keyboardAxisField = typeof(MainForm).GetField("_spatialTransformKeyboardAxis", privateInstance)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not inspect the constrained axis.");
        var pointerSessionField = typeof(MainForm).GetField("_spatialTransformPointerSession", privateInstance)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not inspect pointer state.");
        var editSessionField = typeof(MainForm).GetField("_spatialTransformEditSession", privateInstance)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not inspect edit state.");
        var undoStackField = typeof(MainForm).GetField("_sceneTimelineUndoStack", privateInstance)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not inspect Scene undo state.");
        var processCmdKey = typeof(MainForm).GetMethod(
                "ProcessCmdKey",
                privateInstance,
                binder: null,
                types: [typeof(Message).MakeByRefType(), typeof(Keys)],
                modifiers: null)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not invoke ProcessCmdKey.");
        var setSceneInstanceSelection = typeof(MainForm).GetMethod(
                "SetSceneInstanceSelection",
                privateInstance,
                binder: null,
                types: [typeof(DrawingObjectInstanceDefinition), typeof(bool)],
                modifiers: null)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not select a Scene instance.");
        var clearSelection = typeof(MainForm).GetMethod(
                "ClearSelection",
                privateInstance,
                binder: null,
                types: [typeof(bool)],
                modifiers: null)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not clear Scene selection.");
        var stageMouseDown = typeof(MainForm).GetMethod("StageMouseDown", privateInstance)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not invoke Stage mouse down.");
        var stageMouseMove = typeof(MainForm).GetMethod("StageMouseMove", privateInstance)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not invoke Stage mouse move.");
        var stageMouseUp = typeof(MainForm).GetMethod("StageMouseUp", privateInstance)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not invoke Stage mouse up.");
        var activateTool = typeof(MainForm).GetMethod(
                "ActivateTool",
                privateInstance,
                binder: null,
                types: [typeof(ToolMode)],
                modifiers: null)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not switch tools.");
        var setSpatialTransformMode = typeof(MainForm).GetMethod(
                "SetSpatialTransformMode",
                privateInstance,
                binder: null,
                types: [typeof(SpatialTransformMode), typeof(bool)],
                modifiers: null)
            ?? throw new InvalidOperationException("Spatial pointer regression could not switch gizmo modes.");
        var finishFrameInteraction = typeof(MainForm).GetMethod(
                "FinishPointerInteractionForFrameChange",
                privateInstance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not simulate a frame switch.");
        var finishContextInteraction = typeof(MainForm).GetMethod(
                "FinishPointerInteractionForContextChange",
                privateInstance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not simulate a Scene context switch.");
        var undoLastEdit = typeof(MainForm).GetMethod(
                "UndoLastEdit",
                privateInstance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not undo a committed transform.");
        var onDeactivate = typeof(Form).GetMethod(
                "OnDeactivate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Spatial keyboard regression could not raise form deactivation.");

        DrawingObjectInstanceDefinition CurrentInstance() => scene.Instances.FirstOrDefault(instance =>
                string.Equals(instance.Id, instanceId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Spatial keyboard regression lost its Scene instance.");
        InstanceFrameState CurrentState() => CurrentInstance().EvaluateState(0);
        int UndoCount()
        {
            var stack = undoStackField.GetValue(form)
                ?? throw new InvalidOperationException("Spatial keyboard regression lost the Scene undo stack.");
            return stack.GetType().GetProperty("Count")?.GetValue(stack) is int count
                ? count
                : throw new InvalidOperationException("Spatial keyboard regression could not read the Scene undo count.");
        }
        bool InvokeKey(Keys keyData)
        {
            object[] arguments =
            [
                Message.Create(form.Handle, 0, IntPtr.Zero, IntPtr.Zero),
                keyData
            ];
            return processCmdKey.Invoke(form, arguments) is true;
        }
        void MovePointer(Point point) => stageMouseMove.Invoke(
            form,
            [stage, new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0)]);
        void DragPointer(Point point) => stageMouseMove.Invoke(
            form,
            [stage, new MouseEventArgs(MouseButtons.Left, 0, point.X, point.Y, 0)]);
        void PressPointer(MouseButtons button, Point point) => stageMouseDown.Invoke(
            form,
            [stage, new MouseEventArgs(button, 1, point.X, point.Y, 0)]);
        void ReleasePointer(MouseButtons button, Point point) => stageMouseUp.Invoke(
            form,
            [stage, new MouseEventArgs(button, 1, point.X, point.Y, 0)]);
        void SelectInstance()
        {
            setSceneInstanceSelection.Invoke(form, [CurrentInstance(), false]);
            stage.Select();
            stage.Focus();
            Application.DoEvents();
        }
        static bool SameTransform(InstanceFrameState left, InstanceFrameState right)
        {
            const float epsilon = 0.001f;
            return Math.Abs(left.X - right.X) <= epsilon
                && Math.Abs(left.Y - right.Y) <= epsilon
                && Math.Abs(left.Z - right.Z) <= epsilon
                && Math.Abs(left.RotationX - right.RotationX) <= epsilon
                && Math.Abs(left.RotationY - right.RotationY) <= epsilon
                && Math.Abs(left.RotationZ - right.RotationZ) <= epsilon
                && Math.Abs(left.ScaleX - right.ScaleX) <= epsilon
                && Math.Abs(left.ScaleY - right.ScaleY) <= epsilon
                && Math.Abs(left.ScaleZ - right.ScaleZ) <= epsilon;
        }
        bool ModalActive() => keyboardActiveField.GetValue(form) is true;
        SpatialTransformAxis ModalAxis() => keyboardAxisField.GetValue(form) is SpatialTransformAxis axis
            ? axis
            : SpatialTransformAxis.None;
        StageControl.SpatialGizmoScreenGeometry GizmoGeometry()
        {
            if (!stage.TryGetSpatialGizmoScreenGeometry(out var geometry))
            {
                throw new InvalidOperationException("Spatial keyboard regression could not resolve gizmo screen geometry.");
            }
            return geometry;
        }
        static Point AxisDragTarget(
            StageControl.SpatialGizmoScreenGeometry geometry,
            SpatialTransformAxis axis)
        {
            var origin = Point.Round(geometry.Origin);
            var endpoint = geometry.Endpoints[(int)axis - 1];
            var dx = endpoint.X - geometry.Origin.X;
            var dy = endpoint.Y - geometry.Origin.Y;
            return MathF.Sqrt(dx * dx + dy * dy) >= 4f
                ? Point.Round(endpoint)
                : new Point(origin.X, origin.Y - 72);
        }
        void BeginMovedPointerGesture()
        {
            SelectInstance();
            var start = CurrentState();
            if (!InvokeKey(Keys.G) || !InvokeKey(Keys.X))
            {
                throw new InvalidOperationException("Spatial keyboard regression could not begin a lifecycle gesture.");
            }
            var geometry = GizmoGeometry();
            MovePointer(Point.Round(geometry.Origin));
            MovePointer(Point.Round(geometry.Endpoints[(int)SpatialTransformAxis.X - 1]));
            if (!ModalActive()
                || pointerSessionField.GetValue(form) is null
                || SameTransform(CurrentState(), start))
            {
                throw new InvalidOperationException("Spatial keyboard regression did not establish a changed lifecycle preview.");
            }
        }

        if (MainForm.ApplySpatialThicknessDelta(0, 250) <= 0
            || MainForm.ApplySpatialThicknessDelta(0, 0.6f) != 1
            || MainForm.ApplySpatialThicknessDelta(25, -100) != 0
            || MainForm.ApplySpatialThicknessDelta(5_000_000, 100) != 5_000_000)
        {
            throw new InvalidOperationException("Scale Z thickness did not clamp and grow additively in vector units.");
        }

        var savedCursorPosition = Cursor.Position;
        try
        {
            // Keep axis selection in its waiting state until the synthetic Stage move establishes a baseline.
            Cursor.Position = Point.Empty;
            clearSelection.Invoke(form, [false]);
            stage.Select();
            stage.Focus();
            Application.DoEvents();
            if (!InvokeKey(Keys.G)
                || !InvokeKey(Keys.R)
                || !InvokeKey(Keys.S)
                || ModalActive()
                || pointerSessionField.GetValue(form) is not null
                || editSessionField.GetValue(form) is not null)
            {
                throw new InvalidOperationException(
                    "G/R/S without a selected Scene instance did not remain mode-only shortcuts.");
            }

            SelectInstance();
            var initial = CurrentState();
            var undoStart = UndoCount();
            if (Math.Abs(initial.ScaleZ) > 0.001f
                || !InvokeKey(Keys.G)
                || !ModalActive()
                || ModalAxis() != SpatialTransformAxis.None
                || editSessionField.GetValue(form) is null)
            {
                throw new InvalidOperationException(
                    "G did not enter a zero-thickness 3D transform session that waits for an axis.");
            }

            var waitingEdit = editSessionField.GetValue(form);
            if (!InvokeKey(Keys.G)
                || !ReferenceEquals(waitingEdit, editSessionField.GetValue(form))
                || !InvokeKey(Keys.X)
                || ModalAxis() != SpatialTransformAxis.X)
            {
                throw new InvalidOperationException("Repeated G or the X constraint restarted an idempotent modal session.");
            }

            var moveGeometry = GizmoGeometry();
            MovePointer(Point.Round(moveGeometry.Origin));
            var movePointer = pointerSessionField.GetValue(form);
            if (movePointer is null || !SameTransform(CurrentState(), initial))
            {
                throw new InvalidOperationException(
                    "The first no-button mouse move did not establish an unchanged modal transform baseline.");
            }
            MovePointer(Point.Round(moveGeometry.Endpoints[(int)SpatialTransformAxis.X - 1]));
            var moved = CurrentState();
            if (Math.Abs(moved.X - initial.X) <= 0.001f
                || UndoCount() != undoStart
                || !InvokeKey(Keys.X)
                || !ReferenceEquals(movePointer, pointerSessionField.GetValue(form)))
            {
                throw new InvalidOperationException(
                    "G X did not update from no-button mouse movement or repeated X restarted the gesture.");
            }
            if (!InvokeKey(Keys.Enter)
                || ModalActive()
                || pointerSessionField.GetValue(form) is not null
                || editSessionField.GetValue(form) is not null
                || UndoCount() != undoStart + 1)
            {
                throw new InvalidOperationException("Enter did not commit the modal transform as exactly one undo edit.");
            }
            if (undoLastEdit.Invoke(form, null) is not true || !SameTransform(CurrentState(), initial))
            {
                throw new InvalidOperationException("Undo did not restore the Enter-committed spatial transform.");
            }

            SelectInstance();
            var axisRestartStart = CurrentState();
            var undoBeforeAxisRestart = UndoCount();
            if (!InvokeKey(Keys.G) || !InvokeKey(Keys.X))
            {
                throw new InvalidOperationException("Spatial keyboard regression could not begin its axis-restart gesture.");
            }
            moveGeometry = GizmoGeometry();
            MovePointer(Point.Round(moveGeometry.Origin));
            MovePointer(Point.Round(moveGeometry.Endpoints[(int)SpatialTransformAxis.X - 1]));
            var firstAxisEdit = editSessionField.GetValue(form);
            if (SameTransform(CurrentState(), axisRestartStart)
                || !InvokeKey(Keys.Y)
                || !ModalActive()
                || ModalAxis() != SpatialTransformAxis.Y
                || ReferenceEquals(firstAxisEdit, editSessionField.GetValue(form))
                || !SameTransform(CurrentState(), axisRestartStart)
                || !InvokeKey(Keys.Space)
                || !InvokeKey(Keys.Escape)
                || ModalActive()
                || !SameTransform(CurrentState(), axisRestartStart)
                || UndoCount() != undoBeforeAxisRestart)
            {
                throw new InvalidOperationException(
                    "Changing axes did not restore and restart the modal edit, or Space/Escape broke cancel semantics.");
            }

            SelectInstance();
            var rightCancelStart = CurrentState();
            var undoBeforeRightCancel = UndoCount();
            if (!InvokeKey(Keys.G) || !InvokeKey(Keys.X))
            {
                throw new InvalidOperationException("Spatial keyboard regression could not begin its right-click gesture.");
            }
            moveGeometry = GizmoGeometry();
            MovePointer(Point.Round(moveGeometry.Origin));
            MovePointer(Point.Round(moveGeometry.Endpoints[(int)SpatialTransformAxis.X - 1]));
            PressPointer(MouseButtons.Right, Point.Round(moveGeometry.Endpoints[(int)SpatialTransformAxis.X - 1]));
            if (ModalActive()
                || !SameTransform(CurrentState(), rightCancelStart)
                || UndoCount() != undoBeforeRightCancel)
            {
                throw new InvalidOperationException("Right-click did not cancel and restore the modal transform without undo.");
            }

            SelectInstance();
            var depthMoveStart = CurrentState();
            var undoBeforeDepthMove = UndoCount();
            if (!InvokeKey(Keys.G))
            {
                throw new InvalidOperationException("G Z could not begin a depth-move gesture.");
            }
            var depthMoveGeometry = GizmoGeometry();
            var depthMoveOrigin = Point.Round(depthMoveGeometry.Origin);
            Cursor.Position = stage.PointToScreen(depthMoveOrigin);
            if (!InvokeKey(Keys.Z) || ModalAxis() != SpatialTransformAxis.Z)
            {
                throw new InvalidOperationException("G Z did not constrain movement to the world Z axis.");
            }
            MovePointer(depthMoveOrigin);
            MovePointer(AxisDragTarget(depthMoveGeometry, SpatialTransformAxis.Z));
            if (Math.Abs(CurrentState().Z - depthMoveStart.Z) <= 0.001f
                || !InvokeKey(Keys.Escape)
                || ModalActive()
                || !SameTransform(CurrentState(), depthMoveStart)
                || UndoCount() != undoBeforeDepthMove)
            {
                throw new InvalidOperationException(
                    "G Z did not move along a camera-aligned axis or Escape failed to restore it without undo.");
            }

            SelectInstance();
            var rotateStart = CurrentState();
            var undoBeforeRotate = UndoCount();
            if (!InvokeKey(Keys.R) || !InvokeKey(Keys.Z) || ModalAxis() != SpatialTransformAxis.Z)
            {
                throw new InvalidOperationException("R Z did not begin a Z-axis rotation gesture.");
            }
            var rotateGeometry = GizmoGeometry();
            var rotateRing = rotateGeometry.Rings[(int)SpatialTransformAxis.Z - 1];
            if (rotateRing.Length < 4)
            {
                throw new InvalidOperationException("R Z did not expose a usable projected rotation ring.");
            }
            MovePointer(Point.Round(rotateRing[0]));
            MovePointer(Point.Round(rotateRing[rotateRing.Length / 4]));
            if (Math.Abs(CurrentState().RotationZ - rotateStart.RotationZ) <= 0.001f
                || !InvokeKey(Keys.Escape)
                || ModalActive()
                || !SameTransform(CurrentState(), rotateStart)
                || UndoCount() != undoBeforeRotate)
            {
                throw new InvalidOperationException("R Z did not preview rotation or Escape failed to restore it without undo.");
            }

            SelectInstance();
            var thicknessStart = CurrentState();
            var undoBeforeThickness = UndoCount();
            if (Math.Abs(thicknessStart.ScaleZ) > 0.001f
                || !InvokeKey(Keys.S))
            {
                throw new InvalidOperationException("S Z did not begin from the 2D instance's zero thickness.");
            }
            var scaleGeometry = GizmoGeometry();
            var scaleOrigin = Point.Round(scaleGeometry.Origin);
            Cursor.Position = stage.PointToScreen(scaleOrigin);
            if (!InvokeKey(Keys.Z) || ModalAxis() != SpatialTransformAxis.Z)
            {
                throw new InvalidOperationException("S Z did not constrain the thickness gesture to its world axis.");
            }
            MovePointer(scaleOrigin);
            var scaleZEndpoint = AxisDragTarget(scaleGeometry, SpatialTransformAxis.Z);
            MovePointer(scaleZEndpoint);
            if (CurrentState().ScaleZ <= 0)
            {
                throw new InvalidOperationException("S Z did not extend the selected 2D instance thickness in vector units.");
            }
            ReleasePointer(MouseButtons.Left, scaleZEndpoint);
            if (ModalActive()
                || editSessionField.GetValue(form) is not null
                || UndoCount() != undoBeforeThickness + 1)
            {
                throw new InvalidOperationException("Left click did not commit Scale Z thickness as exactly one undo edit.");
            }
            if (undoLastEdit.Invoke(form, null) is not true || !SameTransform(CurrentState(), thicknessStart))
            {
                throw new InvalidOperationException("Undo did not restore zero thickness after an S Z commit.");
            }

            SelectInstance();
            var deactivateStart = CurrentState();
            var undoBeforeDeactivate = UndoCount();
            if (!InvokeKey(Keys.G) || !InvokeKey(Keys.X))
            {
                throw new InvalidOperationException("Spatial keyboard regression could not begin its deactivate gesture.");
            }
            moveGeometry = GizmoGeometry();
            MovePointer(Point.Round(moveGeometry.Origin));
            MovePointer(Point.Round(moveGeometry.Endpoints[(int)SpatialTransformAxis.X - 1]));
            onDeactivate.Invoke(form, [EventArgs.Empty]);
            if (ModalActive()
                || editSessionField.GetValue(form) is not null
                || !SameTransform(CurrentState(), deactivateStart)
                || UndoCount() != undoBeforeDeactivate)
            {
                throw new InvalidOperationException(
                    "Form deactivation did not cancel and restore the active spatial keyboard transform.");
            }

            var lifecycleStart = CurrentState();
            var undoBeforeLifecycle = UndoCount();
            BeginMovedPointerGesture();
            activateTool.Invoke(form, [ToolMode.Select]);
            if (ModalActive()
                || editSessionField.GetValue(form) is not null
                || !SameTransform(CurrentState(), lifecycleStart)
                || UndoCount() != undoBeforeLifecycle)
            {
                throw new InvalidOperationException("A tool switch did not cancel the spatial keyboard transform.");
            }
            BeginMovedPointerGesture();
            finishFrameInteraction.Invoke(form, null);
            if (ModalActive()
                || editSessionField.GetValue(form) is not null
                || !SameTransform(CurrentState(), lifecycleStart)
                || UndoCount() != undoBeforeLifecycle)
            {
                throw new InvalidOperationException("A frame switch did not cancel the spatial keyboard transform.");
            }
            BeginMovedPointerGesture();
            finishContextInteraction.Invoke(form, null);
            if (ModalActive()
                || editSessionField.GetValue(form) is not null
                || !SameTransform(CurrentState(), lifecycleStart)
                || UndoCount() != undoBeforeLifecycle)
            {
                throw new InvalidOperationException("A Scene context switch did not cancel the spatial keyboard transform.");
            }
            BeginMovedPointerGesture();
            clearSelection.Invoke(form, [false]);
            if (ModalActive()
                || editSessionField.GetValue(form) is not null
                || !SameTransform(CurrentState(), lifecycleStart)
                || UndoCount() != undoBeforeLifecycle)
            {
                throw new InvalidOperationException(
                    "Tool, frame, Scene context, or selection transitions did not preserve cancel-without-undo semantics.");
            }

            SelectInstance();
            var focusCancelStart = CurrentState();
            var undoBeforeFocusCancel = UndoCount();
            if (!InvokeKey(Keys.R) || !ModalActive())
            {
                throw new InvalidOperationException("Spatial keyboard regression could not begin its focus-cancel gesture.");
            }
            using var editor = new TextBox
            {
                Parent = form,
                Location = new Point(8, 8),
                Size = new Size(120, 24)
            };
            editor.BringToFront();
            editor.Select();
            editor.Focus();
            Application.DoEvents();
            InvokeKey(Keys.X);
            if (!editor.Focused
                || ModalActive()
                || editSessionField.GetValue(form) is not null
                || !SameTransform(CurrentState(), focusCancelStart)
                || UndoCount() != undoBeforeFocusCancel)
            {
                throw new InvalidOperationException(
                    "A focused editor did not receive its key after canceling the active spatial transform.");
            }
            InvokeKey(Keys.G);
            if (ModalActive() || editSessionField.GetValue(form) is not null)
            {
                throw new InvalidOperationException("A focused editor allowed G to restart a spatial transform.");
            }
        }
        finally
        {
            Cursor.Position = savedCursorPosition;
        }

        SelectInstance();
        activateTool.Invoke(form, [ToolMode.Transform3D]);
        setSpatialTransformMode.Invoke(form, [SpatialTransformMode.Move, true]);
        var savedYaw = stage.ReferenceYaw;
        var savedPitch = stage.ReferencePitch;
        try
        {
            stage.SetReferenceCameraOrientation(0.62f, -0.38f);
            SelectInstance();
            var physicalStart = CurrentState();
            var physicalUndoStart = UndoCount();
            var physicalGeometry = GizmoGeometry();
            var axisEndpoint = physicalGeometry.Endpoints[(int)SpatialTransformAxis.X - 1];
            var axisVector = new System.Numerics.Vector2(
                axisEndpoint.X - physicalGeometry.Origin.X,
                axisEndpoint.Y - physicalGeometry.Origin.Y);
            if (axisVector.LengthSquared() <= 16f)
            {
                throw new InvalidOperationException("The physical X gizmo axis collapsed in the oblique test view.");
            }
            axisVector = System.Numerics.Vector2.Normalize(axisVector);
            var axisStart = Point.Round(axisEndpoint);
            if (stage.HitTestSpatialTransformGizmo(axisStart).Axis != SpatialTransformAxis.X)
            {
                throw new InvalidOperationException("The visible X gizmo axis was not hit-testable.");
            }
            PressPointer(MouseButtons.Left, axisStart);
            var physicalX = new List<float>();
            for (var step = 1; step <= 3; step++)
            {
                var target = Point.Round(new PointF(
                    axisEndpoint.X + axisVector.X * step * 18f,
                    axisEndpoint.Y + axisVector.Y * step * 18f));
                DragPointer(target);
                physicalX.Add(CurrentState().X);
            }
            var axisFinish = Point.Round(new PointF(
                axisEndpoint.X + axisVector.X * 54f,
                axisEndpoint.Y + axisVector.Y * 54f));
            ReleasePointer(MouseButtons.Left, axisFinish);
            var physicalDirection = Math.Sign(physicalX[0] - physicalStart.X);
            if (physicalDirection == 0
                || physicalX.Zip(physicalX.Skip(1), (left, right) =>
                        (right - left) * physicalDirection > 0.001f)
                    .Any(monotonic => !monotonic)
                || UndoCount() != physicalUndoStart + 1)
            {
                throw new InvalidOperationException(
                    "A physical axis drag fed the moving gizmo origin back into its solver or broke single-undo commit semantics: "
                    + $"start={physicalStart.X:0.###}, samples=[{string.Join(',', physicalX.Select(value => value.ToString("0.###")))}].");
            }
            if (undoLastEdit.Invoke(form, null) is not true || !SameTransform(CurrentState(), physicalStart))
            {
                throw new InvalidOperationException("Undo did not restore the physical axis drag.");
            }

            SelectInstance();
            physicalGeometry = GizmoGeometry();
            axisEndpoint = physicalGeometry.Endpoints[(int)SpatialTransformAxis.X - 1];
            axisVector = System.Numerics.Vector2.Normalize(new System.Numerics.Vector2(
                axisEndpoint.X - physicalGeometry.Origin.X,
                axisEndpoint.Y - physicalGeometry.Origin.Y));
            axisStart = Point.Round(axisEndpoint);
            var roundTripStart = CurrentState();
            var roundTripUndoStart = UndoCount();
            PressPointer(MouseButtons.Left, axisStart);
            var roundTripTarget = Point.Round(new PointF(
                axisEndpoint.X + axisVector.X * 36f,
                axisEndpoint.Y + axisVector.Y * 36f));
            DragPointer(roundTripTarget);
            if (SameTransform(CurrentState(), roundTripStart))
            {
                throw new InvalidOperationException("The spatial round-trip fixture did not leave its start state.");
            }
            DragPointer(axisStart);
            if (!SameTransform(CurrentState(), roundTripStart))
            {
                throw new InvalidOperationException(
                    "Returning a spatial axis drag to its press point retained the previous preview transform.");
            }
            ReleasePointer(MouseButtons.Left, axisStart);
            if (UndoCount() != roundTripUndoStart || !SameTransform(CurrentState(), roundTripStart))
            {
                throw new InvalidOperationException(
                    "A net-zero spatial axis drag committed an empty transform or undo entry.");
            }

            foreach (var planeAxis in new[]
                     {
                         SpatialTransformAxis.XY,
                         SpatialTransformAxis.XZ,
                         SpatialTransformAxis.YZ
                     })
            {
                SelectInstance();
                physicalGeometry = GizmoGeometry();
                var planeIndex = planeAxis switch
                {
                    SpatialTransformAxis.XY => 0,
                    SpatialTransformAxis.XZ => 1,
                    _ => 2
                };
                var polygon = physicalGeometry.PlaneHandles[planeIndex];
                if (polygon.Length != 4)
                {
                    throw new InvalidOperationException($"The {planeAxis} move plane was not visible in an oblique view.");
                }
                var planeCenter = new Point(
                    (int)MathF.Round(polygon.Average(point => point.X)),
                    (int)MathF.Round(polygon.Average(point => point.Y)));
                if (stage.HitTestSpatialTransformGizmo(planeCenter).Axis != planeAxis)
                {
                    throw new InvalidOperationException($"The visible {planeAxis} move plane was not hit-testable.");
                }

                var planeStart = CurrentState();
                var planeUndoStart = UndoCount();
                PressPointer(MouseButtons.Left, planeCenter);
                var planeTarget = new Point(planeCenter.X + 21, planeCenter.Y + 14);
                DragPointer(planeTarget);
                var moved = CurrentState();
                ReleasePointer(MouseButtons.Left, planeTarget);
                var changedX = Math.Abs(moved.X - planeStart.X) > 0.001f;
                var changedY = Math.Abs(moved.Y - planeStart.Y) > 0.001f;
                var changedZ = Math.Abs(moved.Z - planeStart.Z) > 0.001f;
                var constrained = planeAxis switch
                {
                    SpatialTransformAxis.XY => !changedZ && (changedX || changedY),
                    SpatialTransformAxis.XZ => !changedY && (changedX || changedZ),
                    SpatialTransformAxis.YZ => !changedX && (changedY || changedZ),
                    _ => false
                };
                if (!constrained || UndoCount() != planeUndoStart + 1)
                {
                    throw new InvalidOperationException(
                        $"The {planeAxis} handle did not keep movement on its fixed world plane: "
                        + $"delta=({moved.X - planeStart.X:0.###},{moved.Y - planeStart.Y:0.###},{moved.Z - planeStart.Z:0.###}).");
                }
                if (undoLastEdit.Invoke(form, null) is not true || !SameTransform(CurrentState(), planeStart))
                {
                    throw new InvalidOperationException($"Undo did not restore the {planeAxis} plane drag.");
                }

                SelectInstance();
                var planeRoundTripUndoStart = UndoCount();
                PressPointer(MouseButtons.Left, planeCenter);
                DragPointer(planeTarget);
                if (SameTransform(CurrentState(), planeStart))
                {
                    throw new InvalidOperationException(
                        $"The {planeAxis} round-trip fixture did not leave its start state.");
                }
                DragPointer(planeCenter);
                if (!SameTransform(CurrentState(), planeStart))
                {
                    throw new InvalidOperationException(
                        $"Returning a {planeAxis} plane drag to its press point retained the previous preview.");
                }
                ReleasePointer(MouseButtons.Left, planeCenter);
                if (UndoCount() != planeRoundTripUndoStart || !SameTransform(CurrentState(), planeStart))
                {
                    throw new InvalidOperationException(
                        $"A net-zero {planeAxis} plane drag committed an empty transform or undo entry.");
                }
            }
        }
        finally
        {
            stage.SetReferenceCameraOrientation(savedYaw, savedPitch);
        }

        SelectInstance();
        Console.WriteLine("scene_3d_spatial_keyboard_transform=ok");
        Console.WriteLine("scene_3d_spatial_pointer_planes=ok");
        return CurrentInstance();
    }

    private static void RunReferenceWheelDollyDirectionRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene);
        stage.Size = new Size(800, 600);
        stage.ConfigureReferenceView(null, SceneDimension.ThreeD);
        var initialDistance = stage.ReferenceDistance;
        var initialZoom = stage.ReferenceZoomScale;

        stage.DollyReferenceCamera(120);
        var closerDistance = stage.ReferenceDistance;
        stage.ResetReferenceCameraView();
        stage.DollyReferenceCamera(-120);
        var fartherDistance = stage.ReferenceDistance;
        stage.ResetReferenceCameraView();
        stage.DollyReferenceCamera(0);
        var zeroDistance = stage.ReferenceDistance;
        for (var index = 0; index < 128; index++) stage.DollyReferenceCamera(120);
        var minimumDistance = stage.ReferenceDistance;
        for (var index = 0; index < 256; index++) stage.DollyReferenceCamera(-120);
        var maximumDistance = stage.ReferenceDistance;

        if (closerDistance >= initialDistance
            || fartherDistance <= initialDistance
            || zeroDistance != initialDistance
            || maximumDistance != 80_000
            || minimumDistance != 2_000
            || stage.ReferenceZoomScale != initialZoom)
        {
            throw new InvalidOperationException(
                "The 3D mouse wheel did not preserve the expected camera-distance direction, clamps, and zoom scale.");
        }

        var perspectiveScene = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        perspectiveScene.Camera.Projection = CameraProjection.Perspective;
        stage.ConfigureReferenceView(perspectiveScene, SceneDimension.ThreeD);
        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(0, 0);
        var initialPerspectiveSpan = ReferenceProjectedHorizontalSpan(stage, sceneZ: 0);
        stage.Size = new Size(800, 900);
        var resizedPerspectiveSpan = ReferenceProjectedHorizontalSpan(stage, sceneZ: 0);
        stage.Size = new Size(800, 600);

        stage.DollyReferenceCamera(120);
        var closerCameraSpan = ReferenceProjectedHorizontalSpan(stage, sceneZ: 0);
        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(0, 0);
        stage.DollyReferenceCamera(-120);
        var fartherCameraSpan = ReferenceProjectedHorizontalSpan(stage, sceneZ: 0);

        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(0, 0);
        var nearerObjectSpan = ReferenceProjectedHorizontalSpan(stage, sceneZ: -2_000);
        var fartherObjectSpan = ReferenceProjectedHorizontalSpan(stage, sceneZ: 2_000);
        var projectedScenePoint = new System.Numerics.Vector3(750, -300, 1_000);
        if (!stage.TryProjectScenePosition(projectedScenePoint, out var projectedScreenPoint, out _)
            || !stage.TryGetReferenceRay(Point.Round(projectedScreenPoint), out var projectedRay)
            || projectedRay.Direction.Z <= 0)
        {
            throw new InvalidOperationException("The 3D perspective projection did not produce a matching pick ray.");
        }
        var rayAmount = (projectedScenePoint.Z - projectedRay.Origin.Z) / projectedRay.Direction.Z;
        var rayHit = projectedRay.Origin + projectedRay.Direction * rayAmount;
        stage.SetSpatialTransformGizmo(System.Numerics.Vector3.Zero, SpatialTransformMode.Move);
        if (!stage.TryGetSpatialGizmoScreenGeometry(out var gizmoGeometry))
        {
            throw new InvalidOperationException("The 3D perspective camera could not project the Transform gizmo.");
        }
        var gizmoSpan = MathF.Sqrt(
            MathF.Pow(gizmoGeometry.Endpoints[0].X - gizmoGeometry.Origin.X, 2)
            + MathF.Pow(gizmoGeometry.Endpoints[0].Y - gizmoGeometry.Origin.Y, 2));

        perspectiveScene.Camera.Projection = CameraProjection.Orthographic;
        stage.ConfigureReferenceView(perspectiveScene, SceneDimension.ThreeD);
        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(0, 0);
        var initialOrthographicSpan = ReferenceProjectedHorizontalSpan(stage, sceneZ: 0);
        stage.DollyReferenceCamera(120);
        var closerOrthographicSpan = ReferenceProjectedHorizontalSpan(stage, sceneZ: 0);

        if (closerCameraSpan <= initialPerspectiveSpan
            || fartherCameraSpan >= initialPerspectiveSpan
            || nearerObjectSpan <= fartherObjectSpan
            || Math.Abs(resizedPerspectiveSpan - initialPerspectiveSpan) > 0.01f
            || System.Numerics.Vector2.Distance(
                new System.Numerics.Vector2(rayHit.X, rayHit.Y),
                new System.Numerics.Vector2(projectedScenePoint.X, projectedScenePoint.Y)) > 25f
            || Math.Abs(gizmoSpan - 72f) > 1f
            || Math.Abs(closerOrthographicSpan - initialOrthographicSpan) > 0.01f)
        {
            throw new InvalidOperationException(
                "The 3D reference camera did not preserve perspective Dolly scaling, depth foreshortening, pick rays, and orthographic size.");
        }
        Console.WriteLine("scene_reference_wheel_dolly_direction=ok");
        Console.WriteLine("scene_reference_perspective_dolly_scale=ok");
    }

    private static float ReferenceProjectedHorizontalSpan(StageControl stage, float sceneZ)
    {
        if (!stage.TryProjectScenePosition(
                new System.Numerics.Vector3(-500, 0, sceneZ),
                out var left,
                out _)
            || !stage.TryProjectScenePosition(
                new System.Numerics.Vector3(500, 0, sceneZ),
                out var right,
                out _))
        {
            throw new InvalidOperationException("The 3D reference camera could not project the regression span.");
        }
        return Math.Abs(right.X - left.X);
    }

    private static void RunSceneMaskSelectionRegression()
    {
        var composition = new VectorScene();
        composition.CreateEmpty(frameCount: 1);
        var layer = composition.ActiveLayer;
        var lowerObject = composition.AddObject(
            layer,
            PointF.Empty,
            new SizeF(100, 100),
            0,
            0,
            Color.SteelBlue,
            4,
            ShapeKind.Ellipse);
        var topObject = composition.AddObject(
            layer,
            PointF.Empty,
            new SizeF(100, 100),
            0,
            0,
            Color.IndianRed,
            4,
            ShapeKind.Rectangle);
        var lowerInstance = new DrawingObjectInstanceDefinition
        {
            Id = "mask-selection-lower",
            DrawingObjectId = "mask-selection-lower-object"
        };
        var topInstance = new DrawingObjectInstanceDefinition
        {
            Id = "mask-selection-top",
            DrawingObjectId = "mask-selection-top-object"
        };
        DrawingObjectInstanceDefinition[] instances = [lowerInstance, topInstance];
        var compositionResult = new SceneCompositionResult(
        [
            new SceneCompositionObjectOwner(lowerInstance.Id, lowerInstance.DrawingObjectId),
            new SceneCompositionObjectOwner(topInstance.Id, topInstance.DrawingObjectId)
        ]);

        var emptyMask = new VectorScene();
        emptyMask.CreateEmpty(frameCount: 1);
        var centerMask = new VectorScene();
        centerMask.CreateEmpty(frameCount: 1);
        centerMask.AddObject(
            centerMask.ActiveLayer,
            PointF.Empty,
            new SizeF(40, 40),
            0,
            0,
            Color.White,
            4,
            ShapeKind.Rectangle);
        var hiddenTopClip = new SceneCompositionMaskClip(
            composition,
            emptyMask,
            0,
            new HashSet<int> { topObject });
        var clippedLowerClip = new SceneCompositionMaskClip(
            composition,
            centerMask,
            0,
            new HashSet<int> { lowerObject });

        if (!MainForm.TryResolveCompositionInstance(
                composition,
                compositionResult,
                instances,
                PointF.Empty,
                0,
                1,
                out var unmaskedCenter)
            || !ReferenceEquals(unmaskedCenter, topInstance)
            || !MainForm.TryResolveCompositionInstance(
                composition,
                compositionResult,
                instances,
                PointF.Empty,
                0,
                1,
                out var maskedCenter,
                [hiddenTopClip])
            || !ReferenceEquals(maskedCenter, lowerInstance))
        {
            throw new InvalidOperationException(
                "Scene Mask point selection did not skip a clipped top instance while preserving unmasked stack order.");
        }

        if (MainForm.TryResolveCompositionInstance(
                composition,
                compositionResult,
                instances,
                new PointF(45, 45),
                0,
                1,
                out _,
                [hiddenTopClip]))
        {
            throw new InvalidOperationException(
                "Scene Mask point selection used a lower object's bounds instead of its precise visible geometry.");
        }

        var unclippedMarquee = MainForm.FindCompositionInstancesInsideBounds(
            composition,
            compositionResult,
            instances,
            new RectangleF(-21, -21, 42, 42));
        var clippedMarquee = MainForm.FindCompositionInstancesInsideBounds(
            composition,
            compositionResult,
            instances,
            new RectangleF(-21, -21, 42, 42),
            [hiddenTopClip, clippedLowerClip]);
        if (unclippedMarquee.Count != 0
            || clippedMarquee.Count != 1
            || !ReferenceEquals(clippedMarquee[0], lowerInstance))
        {
            throw new InvalidOperationException(
                "Scene Mask marquee selection did not use the fully enclosed visible clipped geometry.");
        }
    }

    private static void RunMarqueeToolPolicyRegression()
    {
        var supportedTools = Enum.GetValues<ToolMode>()
            .Where(MainForm.SupportsMarqueeSelection)
            .ToHashSet();
        AssertTimeline(
            supportedTools.SetEquals([ToolMode.Select, ToolMode.Transform, ToolMode.Distort])
            && MainForm.ShouldShowFillEdgeBezierOverlay(ToolMode.Select, 1, hasEditableFillBoundarySelection: true)
            && !MainForm.ShouldShowFillEdgeBezierOverlay(ToolMode.Select, 2, hasEditableFillBoundarySelection: true)
            && !MainForm.ShouldShowFillEdgeBezierOverlay(ToolMode.Select, 1, hasEditableFillBoundarySelection: false)
            && !MainForm.ShouldShowFillEdgeBezierOverlay(ToolMode.Transform, 1, hasEditableFillBoundarySelection: true)
            && !ShortcutProfiles.Commands.Any(command =>
                string.Equals(command.Id, "tool.fill-edge-bezier", StringComparison.Ordinal))
            && MainForm.ShouldBeginTransformMarquee(TransformHandleKind.None, hasSelectableTarget: false)
            && !MainForm.ShouldBeginTransformMarquee(TransformHandleKind.None, hasSelectableTarget: true)
            && !MainForm.ShouldBeginTransformMarquee(TransformHandleKind.Move, hasSelectableTarget: false)
            && !MainForm.ShouldBeginTransformMarquee(TransformHandleKind.TopLeft, hasSelectableTarget: false)
            && !MainForm.HasActiveDistortHandle(TransformHandleKind.None, visualHandleActive: false)
            && MainForm.HasActiveDistortHandle(TransformHandleKind.None, visualHandleActive: true)
            && MainForm.HasActiveDistortHandle(TransformHandleKind.Move, visualHandleActive: false),
            "Selection tools did not preserve marquee priority or the integrated fill-edge Bezier policy.");

        var source = TransformOverlayFrame.FromBounds(new RectangleF(-100, -60, 200, 120));
        var identity = DistortEnvelope.FromBounds(source.Bounds);
        var mappedCenter = identity.Map(source, source.Center);
        var warped = identity.WithHandle(
            TransformHandleKind.TopRight,
            identity.TopRight,
            new PointF(identity.TopRight.X + 40, identity.TopRight.Y + 25));
        var unrestricted = identity.WithHandle(
            TransformHandleKind.TopLeft,
            identity.TopLeft,
            new PointF(identity.BottomRight.X + 5, identity.BottomRight.Y + 5));
        var flattened = new DistortEnvelope(
            new PointF(source.Bounds.Left, source.Bounds.Bottom),
            new PointF(source.Bounds.Right, source.Bounds.Bottom),
            new PointF(source.Bounds.Right, source.Bounds.Bottom),
            new PointF(source.Bounds.Left, source.Bounds.Bottom));
        var unrestrictedSourceSample = new PointF(-38, -21);
        var unrestrictedMappedSample = unrestricted.Map(source, unrestrictedSourceSample);
        var unrestrictedInverse = unrestricted.TryInverseMap(
            source,
            unrestrictedMappedSample,
            out var unrestrictedResolvedSample);
        var unrestrictedRemappedSample = unrestricted.Map(source, unrestrictedResolvedSample);
        var unrestrictedRemapError = DistortPointDistance(
            unrestrictedMappedSample,
            unrestrictedRemappedSample);
        AssertTimeline(
            identity.IsValid
            && Math.Abs(mappedCenter.X - source.Center.X) <= 0.001f
            && Math.Abs(mappedCenter.Y - source.Center.Y) <= 0.001f
            && warped.IsValid
            && warped.TopRight.X > identity.TopRight.X
            && unrestricted.IsValid
            && unrestricted != identity
            && unrestricted.TopLeft.X > identity.BottomRight.X
            && unrestricted.TopLeft.Y > identity.BottomRight.Y
            && flattened.IsValid
            && flattened.Bounds.Width > 0
            && flattened.Bounds.Height <= 0.001f
            && unrestrictedInverse
            && unrestrictedRemapError <= 0.05f,
            "Distort envelope mapping did not preserve unrestricted crossing, folding, flattening, or stable inverse mapping: "
                + $"crossedValid={unrestricted.IsValid}, crossedChanged={unrestricted != identity}, "
                + $"crossedTopLeft={unrestricted.TopLeft}, flattenedValid={flattened.IsValid}, "
                + $"flattenedBounds={flattened.Bounds}, inverse={unrestrictedInverse}, "
                + $"remapError={unrestrictedRemapError:0.######}.");
        static float DistortPointDistance(PointF first, PointF second)
        {
            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        static float DistortDistanceToSegment(PointF point, PointF start, PointF end)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0.000001f) return DistortPointDistance(point, start);
            var amount = Math.Clamp(
                ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
                0f,
                1f);
            return DistortPointDistance(
                point,
                new PointF(start.X + dx * amount, start.Y + dy * amount));
        }

        var persistentWarp = new DistortWarp(source, identity);
        AssertTimeline(
            identity.TryInsertAnchor(
                DistortSide.Top,
                0.5f,
                out var insertedEnvelope,
                out var insertedHandle),
            "Ctrl insertion could not split a valid distort boundary.");
        var insertionPreservedBoundary = Enumerable.Range(0, 41).All(index =>
        {
            var t = index / 40f;
            var before = identity.BoundaryPoint(DistortSide.Top, t);
            var after = insertedEnvelope.BoundaryPoint(DistortSide.Top, t);
            return DistortPointDistance(before, after) <= 0.001f;
        });
        var controlHandle = insertedHandle with { Kind = DistortHandleKind.OutgoingControl };
        insertedEnvelope.TryGetHandlePosition(controlHandle, out var controlStart);
        var curvedEnvelope = insertedEnvelope.WithHandle(
            controlHandle,
            controlStart,
            new PointF(controlStart.X + 8, controlStart.Y - 12),
            preserveSmoothTangent: false);
        var curvedWarp = persistentWarp.WithEnvelope(curvedEnvelope);
        var sourceSample = new PointF(25, 18);
        var mappedSample = curvedWarp.Map(sourceSample);
        AssertTimeline(
            insertionPreservedBoundary
            && insertedEnvelope.GetAnchors(DistortSide.Top).Length == 3
            && curvedEnvelope.IsValid
            && mappedSample != sourceSample
            && curvedWarp.TryInverseMap(mappedSample, out var inverseSample)
            && DistortPointDistance(sourceSample, inverseSample) <= 0.05f,
            "Persistent cubic distortion did not preserve insertion geometry, editable handles, or stable inverse mapping.");

        var persistentScene = new VectorScene();
        persistentScene.CreateEmpty();
        var persistentObject = persistentScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 120),
            0,
            8,
            Color.Coral,
            Color.White,
            4,
            ShapeKind.Rectangle);
        var rawBounds = persistentScene.GetRawObjectWorldBounds(persistentObject);
        persistentScene.SetObjectDistortions(persistentObject, [curvedWarp]);
        var hasDistortedRectangleGeometry = persistentScene.TryBuildDistortedVectorGeometry(
            persistentObject,
            [curvedWarp],
            out var distortedRectangleGeometry);
        var rebuiltRectangleBoundary = persistentScene.GetDistortedObjectBoundaryContours(persistentObject)
            .FirstOrDefault() ?? [];
        var sourceRectangleBoundary = persistentScene.GetObjectBoundaryContours(persistentObject)
            .FirstOrDefault() ?? [];
        var maximumRectangleBoundaryError = 0f;
        for (var segment = 0; segment < sourceRectangleBoundary.Length - 1; segment++)
        {
            for (var sample = 0; sample <= 16; sample++)
            {
                var amount = sample / 16f;
                var sourcePoint = new PointF(
                    sourceRectangleBoundary[segment].X
                        + (sourceRectangleBoundary[segment + 1].X - sourceRectangleBoundary[segment].X) * amount,
                    sourceRectangleBoundary[segment].Y
                        + (sourceRectangleBoundary[segment + 1].Y - sourceRectangleBoundary[segment].Y) * amount);
                var expected = curvedWarp.Map(sourcePoint);
                var nearest = float.MaxValue;
                for (var edge = 0; edge < rebuiltRectangleBoundary.Length - 1; edge++)
                {
                    nearest = Math.Min(
                        nearest,
                        DistortDistanceToSegment(
                            expected,
                            rebuiltRectangleBoundary[edge],
                            rebuiltRectangleBoundary[edge + 1]));
                }
                maximumRectangleBoundaryError = Math.Max(maximumRectangleBoundaryError, nearest);
            }
        }
        var persistentSnapshot = persistentScene.CreateSnapshot();
        var restoredPersistentScene = new VectorScene();
        restoredPersistentScene.RestoreSnapshot(persistentSnapshot);
        var instanceWithDistortion = new DrawingObjectInstanceDefinition
        {
            DrawingObjectId = "distort-source",
            SceneLayerId = "distort-layer",
            Distortion = curvedWarp
        };
        var clonedInstance = instanceWithDistortion.Clone();
        AssertTimeline(
            restoredPersistentScene.TryGetObjectDistortions(persistentObject, out var restoredDistortions)
            && restoredDistortions.Length == 1
            && restoredDistortions[0] == curvedWarp
            && restoredPersistentScene.GetRawObjectWorldBounds(persistentObject) == rawBounds
            && restoredPersistentScene.ShapeKind[persistentObject] == ShapeKind.Rectangle
            && hasDistortedRectangleGeometry
            && distortedRectangleGeometry.ClosedContours.Length == 1
            && distortedRectangleGeometry.ClosedContours[0].Length >= 4
            && !distortedRectangleGeometry.HasOpenStroke
            && maximumRectangleBoundaryError <= 0.75f
            && clonedInstance.Distortion == curvedWarp,
            "Distortion parameters did not survive object snapshots and packaged-instance cloning without replacing source geometry.");

        var lineScene = new VectorScene();
        lineScene.CreateEmpty();
        var lineObject = lineScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 80),
            0,
            0,
            Color.Transparent,
            Color.White,
            10,
            ShapeKind.Line);
        var lineSource = TransformOverlayFrame.FromBounds(lineScene.GetRawObjectWorldBounds(lineObject));
        var lineEnvelope = DistortEnvelope.FromBounds(lineSource.Bounds);
        var lineWarp = new DistortWarp(
            lineSource,
            lineEnvelope.WithHandle(
                TransformHandleKind.TopRight,
                lineEnvelope.TopRight,
                new PointF(lineEnvelope.TopRight.X + 36, lineEnvelope.TopRight.Y - 28)));
        lineScene.SetObjectDistortions(lineObject, [lineWarp]);
        var lineStart = new PointF(
            lineScene.X[lineObject] - lineScene.Width[lineObject] * 0.5f,
            lineScene.Y[lineObject]);
        var lineEnd = new PointF(
            lineScene.X[lineObject] + lineScene.Width[lineObject] * 0.5f,
            lineScene.Y[lineObject]);
        var hasDistortedLineGeometry = lineScene.TryBuildDistortedVectorGeometry(
            lineObject,
            [lineWarp],
            out var distortedLineGeometry);
        var distortedLineContours = lineScene.GetDistortedObjectBoundaryContours(lineObject);
        using var distortedLineStage = new StageControl(lineScene) { Size = new Size(800, 600) };
        var lineProbeSegment = distortedLineGeometry.OpenStrokeSegments[
            distortedLineGeometry.OpenStrokeSegments.Length / 2];
        var lineProbe = new PointF(
            (lineProbeSegment.Start.X
                + 3 * lineProbeSegment.Control1.X
                + 3 * lineProbeSegment.Control2.X
                + lineProbeSegment.End.X) / 8,
            (lineProbeSegment.Start.Y
                + 3 * lineProbeSegment.Control1.Y
                + 3 * lineProbeSegment.Control2.Y
                + lineProbeSegment.End.Y) / 8);
        var rawLineHit = new DrawingElementHit(
            new DrawingElementKey(lineObject, DrawingElementKind.Stroke, 0),
            0,
            0,
            1);
        var presentedLineStart = PointF.Empty;
        var presentedLineControl1 = PointF.Empty;
        var presentedLineControl2 = PointF.Empty;
        var presentedLineEnd = PointF.Empty;
        var resolvedLineHover = distortedLineStage.TryResolveEditableBezierHit(
            Point.Round(distortedLineStage.WorldToScreen(lineProbe.X, lineProbe.Y)),
            rawLineHit,
            out var presentedLineHit);
        var exposedLineHover = resolvedLineHover
            && distortedLineStage.TryGetEditableBezierWorldPoints(
                presentedLineHit,
                out presentedLineStart,
                out presentedLineControl1,
                out presentedLineControl2,
                out presentedLineEnd);
        var selectedPresentedLine = exposedLineHover
            && (uint)presentedLineHit.PresentedBezierSegmentIndex
                < distortedLineGeometry.OpenStrokeSegments.Length
                ? distortedLineGeometry.OpenStrokeSegments[presentedLineHit.PresentedBezierSegmentIndex]
                : default;
        var sourceLineHover = StageControl.ResolvePresentedBezierSourceHit(presentedLineHit);
        var selectedLineProvenance = exposedLineHover
            && (uint)presentedLineHit.PresentedBezierSegmentIndex
                < distortedLineGeometry.OpenStrokeProvenance.Length
                ? distortedLineGeometry.OpenStrokeProvenance[presentedLineHit.PresentedBezierSegmentIndex]
                : default;
        var presentedLineControlHit = exposedLineHover
            ? distortedLineStage.HitTestLineElementHandle(
                Point.Round(distortedLineStage.WorldToScreen(
                    presentedLineControl1.X,
                    presentedLineControl1.Y)),
                presentedLineHit)
            : EditHandleKind.None;

        var fillPieces = persistentScene.GetExposedFillBezierSegmentPieces(
            persistentObject,
            0,
            includeCoincidentStrokes: true);
        using var distortedFillStage = new StageControl(persistentScene) { Size = new Size(800, 600) };
        distortedFillStage.SetFillEdgeBezierOverlay(
            persistentObject,
            fillPieces.Select(piece => new FillEdgeBezierOverlaySegment(
                piece.PieceIndex,
                piece.Start,
                piece.Control1,
                piece.Control2,
                piece.End)).ToArray(),
            fillPieces.Length > 0 ? fillPieces[0].PieceIndex : -1);
        var presentedFillEdges = distortedFillStage.FillEdgeBezierOverlaySegments.ToArray();
        var expectedFirstFillEdges = fillPieces.Length > 0
            ? VectorScene.BuildDistortedBezierSegmentsWithProvenance(
                [new CubicBoundarySegment(
                    fillPieces[0].Start,
                    fillPieces[0].Control1,
                    fillPieces[0].Control2,
                    fillPieces[0].End)],
                [curvedWarp])
            : [];
        var fillEdgeHit = presentedFillEdges.Length > 0
            ? distortedFillStage.HitTestFillEdgeBezierOverlay(
                Point.Round(distortedFillStage.WorldToScreen(
                    presentedFillEdges[0].Start.X,
                    presentedFillEdges[0].Start.Y)))
            : FillEdgeBezierOverlayHit.None;
        var inverseDisplayPoint = persistentScene.TryInverseMapObjectPoint(
            persistentObject,
            curvedWarp.Map(sourceSample),
            out var inverseObjectPoint);
        var sourceBoundaryMidpoint = sourceRectangleBoundary.Length >= 2
            ? new PointF(
                (sourceRectangleBoundary[0].X + sourceRectangleBoundary[1].X) * 0.5f,
                (sourceRectangleBoundary[0].Y + sourceRectangleBoundary[1].Y) * 0.5f)
            : PointF.Empty;
        var presentedBoundaryProbe = curvedWarp.Map(sourceBoundaryMidpoint);
        var rawBoundaryHit = persistentScene.HitTestElement(presentedBoundaryProbe, 0, toleranceWorld: 2);
        var hasSourceBoundary = persistentScene.TryGetBoundaryBezierSegment(
            rawBoundaryHit,
            0,
            out var sourceBoundarySegment);
        var expectedPresentedBoundary = hasSourceBoundary
            ? VectorScene.BuildDistortedBezierSegmentsWithProvenance(
                [sourceBoundarySegment],
                [curvedWarp])
            : [];
        var resolvedBoundaryHover = distortedFillStage.TryResolveEditableBezierHit(
            Point.Round(distortedFillStage.WorldToScreen(
                presentedBoundaryProbe.X,
                presentedBoundaryProbe.Y)),
            rawBoundaryHit,
            out var presentedBoundaryHit);
        var presentedBoundarySegments = distortedFillStage.GetPresentedEditableBezierWorldSegments(
            rawBoundaryHit);
        var presentedBoundaryControl1 = PointF.Empty;
        var exposedBoundaryHover = resolvedBoundaryHover
            && distortedFillStage.TryGetEditableBezierWorldPoints(
                presentedBoundaryHit,
                out _,
                out presentedBoundaryControl1,
                out _,
                out _);
        var boundaryControlHit = exposedBoundaryHover
            ? distortedFillStage.HitTestLineElementHandle(
                Point.Round(distortedFillStage.WorldToScreen(
                    presentedBoundaryControl1.X,
                    presentedBoundaryControl1.Y)),
                presentedBoundaryHit)
            : EditHandleKind.None;
        var sourceBoundaryHover = StageControl.ResolvePresentedBezierSourceHit(presentedBoundaryHit);
        var selectedBoundaryProvenance = resolvedBoundaryHover
            && (uint)presentedBoundaryHit.PresentedBezierSegmentIndex < expectedPresentedBoundary.Length
                ? expectedPresentedBoundary[presentedBoundaryHit.PresentedBezierSegmentIndex]
                : default;
        var expectedBoundarySourceStart = rawBoundaryHit.StartT
            + (rawBoundaryHit.EndT - rawBoundaryHit.StartT) * selectedBoundaryProvenance.SourceStartT;
        var expectedBoundarySourceEnd = rawBoundaryHit.StartT
            + (rawBoundaryHit.EndT - rawBoundaryHit.StartT) * selectedBoundaryProvenance.SourceEndT;
        AssertTimeline(
            hasDistortedLineGeometry
            && !distortedLineGeometry.HasClosedContours
            && distortedLineGeometry.OpenStrokeSegments.Length > 0
            && distortedLineGeometry.OpenStrokeProvenance.Length
                == distortedLineGeometry.OpenStrokeSegments.Length
            && DistortPointDistance(
                distortedLineGeometry.OpenStrokeSegments[0].Start,
                lineWarp.Map(lineStart)) <= 0.01f
            && DistortPointDistance(
                distortedLineGeometry.OpenStrokeSegments[^1].End,
                lineWarp.Map(lineEnd)) <= 0.01f
            && distortedLineContours.Length == 1
            && distortedLineContours[0].Length >= 2
            && distortedLineContours[0][0] != distortedLineContours[0][^1]
            && exposedLineHover
            && selectedPresentedLine == new CubicBoundarySegment(
                presentedLineStart,
                presentedLineControl1,
                presentedLineControl2,
                presentedLineEnd)
            && Math.Abs(sourceLineHover.StartT - selectedLineProvenance.SourceStartT) <= 0.0001f
            && Math.Abs(sourceLineHover.EndT - selectedLineProvenance.SourceEndT) <= 0.0001f
            && presentedLineControlHit != EditHandleKind.None
            && fillPieces.Length > 0
            && presentedFillEdges.Length >= fillPieces.Length
            && expectedFirstFillEdges.Length > 0
            && presentedFillEdges[0] == new FillEdgeBezierOverlaySegment(
                fillPieces[0].PieceIndex,
                expectedFirstFillEdges[0].Curve.Start,
                expectedFirstFillEdges[0].Curve.Control1,
                expectedFirstFillEdges[0].Curve.Control2,
                expectedFirstFillEdges[0].Curve.End,
                expectedFirstFillEdges[0].SourceStartT,
                expectedFirstFillEdges[0].SourceEndT)
            && fillEdgeHit.IsValid
            && fillEdgeHit.PartIndex == fillPieces[0].PieceIndex
            && inverseDisplayPoint
            && DistortPointDistance(sourceSample, inverseObjectPoint) <= 0.05f
            && rawBoundaryHit.Key.Kind == DrawingElementKind.BoundaryStroke
            && expectedPresentedBoundary.Length > 0
            && presentedBoundarySegments.SequenceEqual(
                expectedPresentedBoundary.Select(segment => segment.Curve))
            && exposedBoundaryHover
            && boundaryControlHit != EditHandleKind.None
            && Math.Abs(sourceBoundaryHover.StartT - expectedBoundarySourceStart) <= 0.0001f
            && Math.Abs(sourceBoundaryHover.EndT - expectedBoundarySourceEnd) <= 0.0001f,
            $"Distorted vector editing did not share rebuilt Bezier geometry across rendering, hover, fill-edge controls, boundary strokes, and inverse-mapped edits: hover={resolvedLineHover}/{exposedLineHover}, index={presentedLineHit.PresentedBezierSegmentIndex}, source={sourceLineHover.StartT:0.0000}-{sourceLineHover.EndT:0.0000}, expected={selectedLineProvenance.SourceStartT:0.0000}-{selectedLineProvenance.SourceEndT:0.0000}, handle={presentedLineControlHit}, fill={fillPieces.Length}/{presentedFillEdges.Length}/{expectedFirstFillEdges.Length}, fillHit={fillEdgeHit}, boundary={rawBoundaryHit.Key.Kind}/{resolvedBoundaryHover}/{presentedBoundarySegments.Length}/{expectedPresentedBoundary.Length}/{boundaryControlHit}, boundarySource={sourceBoundaryHover.StartT:0.0000}-{sourceBoundaryHover.EndT:0.0000}/{expectedBoundarySourceStart:0.0000}-{expectedBoundarySourceEnd:0.0000}, inverse={inverseDisplayPoint}.");
        Console.WriteLine("distort_envelope_regression=ok");
    }

    private static void RunDistortPointerRegression()
    {
        const System.Reflection.BindingFlags privateInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var sceneField = typeof(MainForm).GetField("_scene", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not inspect the scene.");
        var projectField = typeof(MainForm).GetField("_project", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not inspect the project.");
        var stageField = typeof(MainForm).GetField("_stage", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not inspect the Stage.");
        var visualHandleActiveField = typeof(MainForm).GetField("_distortVisualHandleActive", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not inspect the visual handle state.");
        var currentEnvelopeField = typeof(MainForm).GetField("_distortCurrentEnvelope", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not inspect the current envelope.");
        var startEnvelopeField = typeof(MainForm).GetField("_distortStartEnvelope", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not inspect the start envelope.");
        var startPointerField = typeof(MainForm).GetField("_drawingTransformStartPointer", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not inspect the start pointer.");
        var pendingPointerField = typeof(MainForm).GetField("_pendingDrawingTransformWorld", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not inspect the queued pointer.");
        var setSelection = typeof(MainForm).GetMethod(
            "SetSelection",
            privateInstance,
            binder: null,
            types: [typeof(int), typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("Distort pointer regression could not select an object.");
        var activateTool = typeof(MainForm).GetMethod(
            "ActivateTool",
            privateInstance,
            binder: null,
            types: [typeof(ToolMode)],
            modifiers: null)
            ?? throw new InvalidOperationException("Distort pointer regression could not activate the tool.");
        var updateOverlay = typeof(MainForm).GetMethod("UpdateTransformOverlay", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not update the overlay.");
        var updateSelectionHighlightAnimation = typeof(StageControl).GetMethod(
            "UpdateSelectionHighlightAnimation",
            privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not update selection animation.");
        var setInstanceSelection = typeof(MainForm).GetMethod(
            "SetSceneInstanceSelection",
            privateInstance,
            binder: null,
            types: [typeof(DrawingObjectInstanceDefinition), typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("Distort pointer regression could not select an instance.");
        var rebuildUnderlay = typeof(MainForm).GetMethod("RebuildDrawingObjectUnderlay", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not rebuild the instance underlay.");
        var mouseDown = typeof(MainForm).GetMethod("StageMouseDown", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not begin input.");
        var mouseMove = typeof(MainForm).GetMethod("StageMouseMove", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not update input.");
        var mouseUp = typeof(MainForm).GetMethod("StageMouseUp", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not finish input.");
        var applyDistortPointer = typeof(MainForm).GetMethod("ApplyDistortFromPointer", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not drive the preview path.");
        var drawGdi = typeof(StageControl).GetMethod("DrawGdi", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not render the preview.");
        var drawBufferedGdi = typeof(StageControl).GetMethod("DrawBufferedGdi", privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not render the cached GDI frame.");
        var basePresentationPendingField = typeof(StageControl).GetField(
            "_basePresentationInvalidationPending",
            privateInstance)
            ?? throw new InvalidOperationException("Distort pointer regression could not inspect GDI cache invalidation.");

        using var form = new MainForm { Size = new Size(1280, 800) };
        form.CreateControl();
        form.PerformLayout();
        var scene = sceneField.GetValue(form) as VectorScene
            ?? throw new InvalidOperationException("Distort pointer regression did not find the scene.");
        var stage = stageField.GetValue(form) as StageControl
            ?? throw new InvalidOperationException("Distort pointer regression did not find the Stage.");
        stage.Size = new Size(960, 640);
        stage.CreateControl();

        var objectIndex = scene.AppendObject(
            scene.ActiveLayer,
            PointF.Empty,
            new SizeF(240, 160),
            0,
            2,
            Color.CornflowerBlue,
            Color.Black,
            8,
            ShapeKind.Rectangle);
        scene.CompleteDeferredBuild();
        setSelection.Invoke(form, [objectIndex, false]);
        activateTool.Invoke(form, [ToolMode.Distort]);
        updateOverlay.Invoke(form, null);
        if (!stage.DistortBoundsVisible || !stage.DistortFrame.IsValid)
        {
            throw new InvalidOperationException("Distort pointer regression did not expose the selected-object envelope.");
        }

        var startWorld = stage.DistortFrame.TopLeft;
        var endWorld = new PointF(startWorld.X - 32, startWorld.Y - 24);
        var start = Point.Round(stage.WorldToScreen(startWorld.X, startWorld.Y));
        var end = Point.Round(stage.WorldToScreen(endWorld.X, endWorld.Y));
        if (!stage.TryHitTestDistortVisualHandle(start, out var hitHandle)
            || hitHandle.Kind != DistortHandleKind.Anchor)
        {
            throw new InvalidOperationException("The visible Distort anchor was not hit-testable.");
        }

        mouseDown.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0)]);
        if (visualHandleActiveField.GetValue(form) is not true)
        {
            throw new InvalidOperationException("The hit Distort anchor did not start a pointer session.");
        }

        var geometryBuildsBeforePreview = scene.GeometryIndexBuildCount;
        mouseMove.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 0, end.X, end.Y, 0)]);
        var movedEnvelope = currentEnvelopeField.GetValue(form) is DistortEnvelope current
            ? current
            : default;
        if (!movedEnvelope.IsValid || movedEnvelope.TopLeft == startWorld)
        {
            var startEnvelope = startEnvelopeField.GetValue(form) is DistortEnvelope captured
                ? captured
                : default;
            var startPointer = startPointerField.GetValue(form) is PointF pointer
                ? pointer
                : PointF.Empty;
            var currentPointer = stage.ScreenToWorld(end);
            var candidate = startEnvelope.WithHandle(hitHandle, startPointer, currentPointer);
            var oppositeCandidate = startEnvelope.WithHandle(
                hitHandle,
                startPointer,
                new PointF(startPointer.X + 32, startPointer.Y + 24));
            var genericCandidate = startEnvelope.WithHandle(
                TransformHandleKind.TopLeft,
                startPointer,
                currentPointer);
            var pending = pendingPointerField.GetValue(form) is PointF;
            throw new InvalidOperationException(
                $"Dragging a Distort anchor did not produce an updated envelope. "
                + $"candidateChanged={candidate != startEnvelope}, "
                + $"oppositeChanged={oppositeCandidate != startEnvelope}, "
                + $"genericChanged={genericCandidate != startEnvelope}, pending={pending}, "
                + $"start=({start.X},{start.Y}), end=({end.X},{end.Y}), "
                + $"worldDelta=({currentPointer.X - startPointer.X:0.###},{currentPointer.Y - startPointer.Y:0.###}).");
        }

        if (scene.TryGetObjectDistortions(objectIndex, out _)
            || scene.GeometryIndexBuildCount != geometryBuildsBeforePreview
            || !stage.HasDistortPreview)
        {
            throw new InvalidOperationException(
                "Distort preview mutated the object model or rebuilt the geometry index before commit.");
        }

        var previewUpdateStarted = Stopwatch.GetTimestamp();
        const int previewUpdateCount = 64;
        for (var update = 0; update < previewUpdateCount; update++)
        {
            applyDistortPointer.Invoke(
                form,
                [new PointF(
                    endWorld.X - update % 8 * 0.5f,
                    endWorld.Y - update % 5 * 0.5f)]);
        }
        var previewUpdateAverage = Stopwatch.GetElapsedTime(previewUpdateStarted).TotalMilliseconds
            / previewUpdateCount;
        if (previewUpdateAverage > 8)
        {
            throw new InvalidOperationException(
                $"Distort pointer preview exceeded its update budget: {previewUpdateAverage:0.000} ms.");
        }

        var previewRasterBuilds = 0;
        var previewRasterReuses = 0;
        var previewRasterReuseMilliseconds = 0d;
        using (var previewBitmap = new Bitmap(
            stage.Width,
            stage.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using (var previewGraphics = Graphics.FromImage(previewBitmap))
        {
            drawGdi.Invoke(stage, [previewGraphics]);
            var buildsAfterFirstFrame = stage.DistortPreviewRasterBuildCount;
            applyDistortPointer.Invoke(
                form,
                [new PointF(endWorld.X - 4, endWorld.Y - 3)]);
            var rasterReuseStarted = Stopwatch.GetTimestamp();
            drawGdi.Invoke(stage, [previewGraphics]);
            previewRasterReuseMilliseconds = Stopwatch.GetElapsedTime(rasterReuseStarted).TotalMilliseconds;
            if (buildsAfterFirstFrame != 0
                || stage.DistortPreviewRasterBuildCount != 0
                || stage.DistortPreviewRasterReuseCount != 0)
            {
                throw new InvalidOperationException(
                    "A solid primitive Distort preview unexpectedly created a source raster instead of rebuilt Bezier geometry.");
            }
            previewRasterBuilds = stage.DistortPreviewRasterBuildCount;
            previewRasterReuses = stage.DistortPreviewRasterReuseCount;
        }

        mouseUp.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, end.X, end.Y, 0)]);

        if (!scene.TryGetObjectDistortions(objectIndex, out var distortions)
            || distortions.Length != 1
            || distortions[0].Envelope == DistortEnvelope.FromBounds(scene.GetRawObjectWorldBounds(objectIndex)))
        {
            throw new InvalidOperationException("Dragging a visible Distort anchor did not persist an object warp.");
        }
        if (scene.GeometryIndexBuildCount != geometryBuildsBeforePreview + 1)
        {
            throw new InvalidOperationException(
                "Distort commit did not rebuild the object geometry index exactly once.");
        }
        updateSelectionHighlightAnimation.Invoke(stage, null);
        if (stage.SelectionHighlightAnimating)
        {
            throw new InvalidOperationException(
                "Persistent Distort rendering left the continuous selection highlight timer running.");
        }

        var persistentIdleAverageMilliseconds = 0d;
        var persistentRasterBuilds = 0;
        var persistentRasterReuses = 0;
        var persistentBaseFrameReuses = 0;
        using (var persistentBitmap = new Bitmap(
            stage.Width,
            stage.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using (var persistentGraphics = Graphics.FromImage(persistentBitmap))
        {
            drawBufferedGdi.Invoke(stage, [persistentGraphics]);
            basePresentationPendingField.SetValue(stage, false);
            if (stage.LastGdiBaseFrameCacheBuilds != 1
                || stage.LastGdiBaseFrameCacheReuses != 0
                || stage.LastGdiDistortRasterBuilds != 0)
            {
                throw new InvalidOperationException(
                    "The first persistent primitive Distort frame did not build a vector-backed reusable base frame.");
            }
            persistentRasterBuilds = stage.LastGdiDistortRasterBuilds;

            drawGdi.Invoke(stage, [persistentGraphics]);
            if (stage.LastGdiDistortRasterBuilds != 0
                || stage.LastGdiDistortRasterReuses != 0)
            {
                throw new InvalidOperationException(
                    "Persistent primitive Distort rendering unexpectedly used a source raster.");
            }
            persistentRasterReuses = stage.LastGdiDistortRasterReuses;

            const int persistentIdleFrameCount = 8;
            var idleStarted = Stopwatch.GetTimestamp();
            for (var frame = 0; frame < persistentIdleFrameCount; frame++)
            {
                stage.InvalidateOverlay();
                drawBufferedGdi.Invoke(stage, [persistentGraphics]);
                basePresentationPendingField.SetValue(stage, false);
                persistentBaseFrameReuses += stage.LastGdiBaseFrameCacheReuses;
                if (stage.LastGdiBaseFrameCacheBuilds != 0
                    || stage.LastGdiBaseFrameCacheReuses != 1
                    || stage.LastGdiDistortRasterBuilds != 0)
                {
                    throw new InvalidOperationException(
                        "A persistent Distort overlay frame rebuilt the warped scene instead of reusing its GDI base frame.");
                }
            }
            persistentIdleAverageMilliseconds = Stopwatch.GetElapsedTime(idleStarted).TotalMilliseconds
                / persistentIdleFrameCount;
            if (persistentIdleAverageMilliseconds > 12)
            {
                throw new InvalidOperationException(
                    $"Persistent Distort idle redraw exceeded its 12 ms budget: {persistentIdleAverageMilliseconds:0.000} ms.");
            }

            scene.Argb[objectIndex] = Color.MediumPurple.ToArgb();
            stage.Invalidate();
            drawBufferedGdi.Invoke(stage, [persistentGraphics]);
            basePresentationPendingField.SetValue(stage, false);
            if (stage.LastGdiBaseFrameCacheBuilds != 1
                || stage.LastGdiDistortRasterBuilds != 0)
            {
                throw new InvalidOperationException(
                    "A persistent primitive Distort material change did not rebuild its vector base frame.");
            }

            var changedEnvelope = distortions[0].Envelope.Transform(point =>
                new PointF(point.X + 1, point.Y));
            scene.SetObjectDistortions(objectIndex, [distortions[0].WithEnvelope(changedEnvelope)]);
            stage.Invalidate();
            drawBufferedGdi.Invoke(stage, [persistentGraphics]);
            basePresentationPendingField.SetValue(stage, false);
            if (stage.LastGdiBaseFrameCacheBuilds != 1
                || stage.LastGdiBaseFrameCacheReuses != 0
                || stage.LastGdiDistortRasterBuilds != 0)
            {
                throw new InvalidOperationException(
                    "A persistent primitive Distort geometry change did not rebuild the vector GDI base frame exactly once.");
            }

            scene.SetLinearGradient(
                objectIndex,
                Color.CornflowerBlue,
                Color.MediumPurple,
                new PointF(-120, 0),
                new PointF(120, 0));
            stage.Invalidate();
            drawGdi.Invoke(stage, [persistentGraphics]);
            persistentRasterBuilds = stage.LastGdiDistortRasterBuilds;
            drawGdi.Invoke(stage, [persistentGraphics]);
            persistentRasterReuses = stage.LastGdiDistortRasterReuses;
            if (persistentRasterBuilds <= 0 || persistentRasterReuses < persistentRasterBuilds)
            {
                throw new InvalidOperationException(
                    "A distorted gradient primitive did not retain its clipped source-raster fallback and cache reuse.");
            }
        }

        var project = projectField.GetValue(form) as VectorProject
            ?? throw new InvalidOperationException("Distort pointer regression did not find the project.");
        var container = project.DrawingObjects[0];
        var child = project.AddDrawingObject("Distort Regression Child");
        child.Scene.AppendObject(
            child.Scene.ActiveLayer,
            PointF.Empty,
            new SizeF(180, 120),
            0,
            2,
            Color.MediumSeaGreen,
            Color.Black,
            8,
            ShapeKind.Rectangle);
        child.Scene.CompleteDeferredBuild();
        if (!project.TryAddDrawingObjectInstance(
                container.Id,
                child.Id,
                new PointF(320, 0),
                out var instance)
            || instance is null)
        {
            throw new InvalidOperationException("Distort pointer regression could not create a drawing-object instance.");
        }

        rebuildUnderlay.Invoke(form, null);
        setInstanceSelection.Invoke(form, [instance, false]);
        updateOverlay.Invoke(form, null);
        if (!stage.DistortBoundsVisible || !stage.DistortFrame.IsValid)
        {
            throw new InvalidOperationException("The selected drawing-object instance did not expose a Distort envelope.");
        }

        var instanceStartWorld = stage.DistortFrame.TopRight;
        var instanceEndWorld = new PointF(instanceStartWorld.X + 28, instanceStartWorld.Y - 20);
        var instanceStart = Point.Round(stage.WorldToScreen(instanceStartWorld.X, instanceStartWorld.Y));
        var instanceEnd = Point.Round(stage.WorldToScreen(instanceEndWorld.X, instanceEndWorld.Y));
        mouseDown.Invoke(
            form,
            [stage, new MouseEventArgs(MouseButtons.Left, 1, instanceStart.X, instanceStart.Y, 0)]);
        mouseMove.Invoke(
            form,
            [stage, new MouseEventArgs(MouseButtons.Left, 0, instanceEnd.X, instanceEnd.Y, 0)]);
        if (instance.EvaluateState(0).Distortion is not null || !stage.HasDistortPreview)
        {
            throw new InvalidOperationException(
                "Drawing-object Distort preview persisted or recomposed the instance before pointer commit.");
        }
        mouseUp.Invoke(
            form,
            [stage, new MouseEventArgs(MouseButtons.Left, 1, instanceEnd.X, instanceEnd.Y, 0)]);
        if (instance.EvaluateState(0).Distortion is not { IsValid: true } instanceDistortion
            || instanceDistortion.Envelope == DistortEnvelope.FromBounds(instanceDistortion.Source.Bounds))
        {
            throw new InvalidOperationException("Dragging a drawing-object instance anchor did not persist its warp.");
        }

        Console.WriteLine($"distort_preview_raster_builds={previewRasterBuilds}");
        Console.WriteLine($"distort_preview_raster_reuses={previewRasterReuses}");
        Console.WriteLine($"distort_preview_update_avg_ms={previewUpdateAverage:0.000}");
        Console.WriteLine($"distort_preview_cached_frame_ms={previewRasterReuseMilliseconds:0.000}");
        Console.WriteLine($"distort_persistent_raster_builds={persistentRasterBuilds}");
        Console.WriteLine($"distort_persistent_raster_reuses={persistentRasterReuses}");
        Console.WriteLine($"distort_persistent_base_frame_reuses={persistentBaseFrameReuses}");
        Console.WriteLine($"distort_persistent_idle_frame_avg_ms={persistentIdleAverageMilliseconds:0.000}");
        Console.WriteLine("distort_pointer_regression=ok");
    }

    private static void RunImportedSvgRasterizerRegression()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
              <defs><linearGradient id="g"><stop stop-color="#ff3355"/><stop offset="1" stop-color="#2277ee" stop-opacity=".45"/></linearGradient></defs>
              <rect x="4" y="4" width="112" height="72" rx="10" fill="url(#g)"/>
            </svg>
            """;
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            $"imported-svg-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var validPath = Path.Combine(temporaryRoot, "valid.svg");
            File.WriteAllText(validPath, source);
            var droppedSvg = new DataObject(DataFormats.FileDrop, new[] { validPath });
            var droppedMultiple = new DataObject(DataFormats.FileDrop, new[] { validPath, validPath });
            var droppedNonSvg = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(temporaryRoot, "valid.png") });
            AssertTimeline(
                MainForm.TryResolveDroppedSvgFile(droppedSvg, out var droppedPath)
                && droppedPath == validPath
                && MainForm.ImportedSvgDisplayName(validPath) == "valid"
                && !MainForm.TryResolveDroppedSvgFile(droppedMultiple, out _)
                && !MainForm.TryResolveDroppedSvgFile(droppedNonSvg, out _),
                "Stage file-drop routing did not accept exactly one SVG file.");
            var loaded = ImportedSvgRasterizer.Load(validPath);
            var raster = ImportedSvgRasterizer.Rasterize(loaded.Source, 240, 160);
            foreach (var requestedSize in new[]
                     {
                         new SizeF(15, 10),
                         new SizeF(60, 40),
                         new SizeF(300, 200),
                         new SizeF(12_000, 8_000)
                     })
            {
                var zoomRaster = ImportedSvgRasterizer.Rasterize(
                    loaded.Source,
                    requestedSize.Width,
                    requestedSize.Height);
                var aspectCrossError = Math.Abs(
                    zoomRaster.PixelWidth * (double)requestedSize.Height
                    - zoomRaster.PixelHeight * (double)requestedSize.Width);
                AssertTimeline(
                    aspectCrossError <= Math.Max(requestedSize.Width, requestedSize.Height)
                    && zoomRaster.PixelWidth <= ImportedSvgRasterizer.MaxRasterDimension
                    && zoomRaster.PixelHeight <= ImportedSvgRasterizer.MaxRasterDimension
                    && (long)zoomRaster.PixelWidth * zoomRaster.PixelHeight <= ImportedSvgRasterizer.MaxRasterPixels,
                    $"Imported SVG raster quantization changed the target aspect ratio at {requestedSize.Width}x{requestedSize.Height}: " +
                    $"{zoomRaster.PixelWidth}x{zoomRaster.PixelHeight}.");
            }
            var visiblePixels = 0;
            var visibleLeft = raster.PixelWidth;
            var visibleTop = raster.PixelHeight;
            var visibleRight = -1;
            var visibleBottom = -1;
            for (var y = 0; y < raster.PixelHeight; y++)
            {
                for (var x = 0; x < raster.PixelWidth; x++)
                {
                    var alphaOffset = y * raster.Stride + x * 4 + 3;
                    if (raster.Pixels[alphaOffset] == 0) continue;
                    visiblePixels++;
                    visibleLeft = Math.Min(visibleLeft, x);
                    visibleTop = Math.Min(visibleTop, y);
                    visibleRight = Math.Max(visibleRight, x);
                    visibleBottom = Math.Max(visibleBottom, y);
                }
            }
            var visibleWidth = Math.Max(0, visibleRight - visibleLeft + 1);
            var visibleHeight = Math.Max(0, visibleBottom - visibleTop + 1);

            var scene = new VectorScene();
            scene.CreateEmpty();
            scene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.Black, 3, ShapeKind.Rectangle);
            var imported = scene.AddImportedSvgObject(
                0,
                new PointF(30, 40),
                new SizeF(120, 80),
                0.2f,
                loaded.Source,
                "valid");
            var snapshot = scene.CreateSnapshot();
            AssertTimeline(
                imported == 1
                && scene.ObjectCount == 2
                && scene.ShapeKind[imported] == ShapeKind.ImportedSvg
                && scene.TryGetImportedSvgSource(imported, out var addedSource)
                && addedSource == loaded.Source
                && scene.TryGetImportedSvgName(imported, out var addedName)
                && addedName == "valid"
                && visiblePixels > 0
                && visibleWidth >= raster.PixelWidth * 0.85f
                && visibleHeight >= raster.PixelHeight * 0.85f,
                $"Imported SVG load did not create one opaque object or a target-sized visible raster: " +
                $"raster={raster.PixelWidth}x{raster.PixelHeight}, visible={visibleWidth}x{visibleHeight}.");

            scene.RemoveObjectAt(0);
            AssertTimeline(
                scene.ObjectCount == 1
                && scene.ShapeKind[0] == ShapeKind.ImportedSvg
                && scene.TryGetImportedSvgSource(0, out var remappedSource)
                && remappedSource == loaded.Source
                && scene.TryGetImportedSvgName(0, out var remappedName)
                && remappedName == "valid",
                "Imported SVG payload did not follow object-index compaction.");
            scene.RestoreSnapshot(snapshot);
            AssertTimeline(
                scene.TryGetImportedSvgSource(imported, out var restoredSource)
                && restoredSource == loaded.Source
                && scene.TryGetImportedSvgName(imported, out var restoredName)
                && restoredName == "valid",
                "Imported SVG payload did not survive snapshot restore.");
            var legacySnapshot = scene.CreateSnapshot();
            legacySnapshot.ImportedSvgNames.Clear();
            scene.RestoreSnapshot(legacySnapshot);
            AssertTimeline(
                scene.TryGetImportedSvgSource(imported, out _)
                && !scene.TryGetImportedSvgName(imported, out _),
                "An older imported SVG snapshot without name metadata was rejected.");
            scene.RestoreSnapshot(snapshot);

            var emptyDrawingObject = VectorProject.CreateEmpty().DrawingObjects[0];
            AssertTimeline(
                MainForm.ShouldRenameEmptyDrawingObjectForSvgImport(emptyDrawingObject),
                "A completely empty drawing object was not eligible for SVG import naming.");
            emptyDrawingObject.Scene.AddObject(
                0,
                PointF.Empty,
                new SizeF(20, 20),
                0,
                0,
                Color.Black,
                3,
                ShapeKind.Rectangle);
            AssertTimeline(
                !MainForm.ShouldRenameEmptyDrawingObjectForSvgImport(emptyDrawingObject),
                "A populated drawing object remained eligible for SVG import naming.");

            var project = VectorProject.CreateEmpty();
            var drawingObject = project.DrawingObjects[0];
            drawingObject.Scene.CreateEmpty();
            drawingObject.Scene.AddImportedSvgObject(
                0,
                new PointF(20, -10),
                new SizeF(120, 80),
                0.2f,
                loaded.Source,
                "Composed Logo");
            AssertTimeline(
                project.TryAddSceneInstance(project.Scenes[0].Id, drawingObject.Id, PointF.Empty, 0, out var instance)
                && instance is not null,
                "Imported SVG composition setup rejected a valid instance.");
            var instanceState = instance!.EvaluateState(0);
            instance.SetStateAtFrame(0, instanceState with
            {
                ScaleX = -1.2f,
                ScaleY = 0.8f,
                SkewX = 24
            });
            var compositionScene = new VectorScene();
            var composition = SceneCompositionBuilder.Build(
                compositionScene,
                project.Scenes[0],
                project.DrawingObjects,
                0);
            var composedImported = Enumerable.Range(0, compositionScene.ObjectCount)
                .Single(index => compositionScene.ShapeKind[index] == ShapeKind.ImportedSvg);
            AssertTimeline(
                compositionScene.TryGetImportedSvgSource(composedImported, out var composedSource)
                && compositionScene.TryGetImportedSvgName(composedImported, out var composedName)
                && composedName == "Composed Logo",
                "Imported SVG composition lost its transformed payload.");
            var transformedRaster = ImportedSvgRasterizer.Rasterize(composedSource, 240, 160);
            AssertTimeline(
                composition.ObjectOwners.Count == 1
                && composedSource != loaded.Source
                && composedSource.Contains("matrix(", StringComparison.Ordinal)
                && transformedRaster.Pixels
                    .Where((_, offset) => offset % 4 == 3)
                    .Any(alpha => alpha > 0),
                "Imported SVG composition did not preserve a reflected skew transform as visible content.");

            var tint = Color.FromArgb(255, 128, 192, 224);
            instance.SetStateAtFrame(0, instance.EvaluateState(0) with
            {
                Alpha = 0.5f,
                TintArgb = tint.ToArgb()
            });
            var appearanceScene = new VectorScene();
            SceneCompositionBuilder.Build(
                appearanceScene,
                project.Scenes[0],
                project.DrawingObjects,
                0);
            var appearanceImported = Enumerable.Range(0, appearanceScene.ObjectCount)
                .Single(index => appearanceScene.ShapeKind[index] == ShapeKind.ImportedSvg);
            AssertTimeline(
                appearanceScene.TryGetImportedSvgSource(appearanceImported, out var appearanceSource),
                "Imported SVG appearance composition lost its wrapped payload.");
            var appearanceRaster = ImportedSvgRasterizer.Rasterize(appearanceSource, 240, 160);
            var comparisonPixel = -1;
            for (var pixel = 0; pixel < transformedRaster.PixelWidth * transformedRaster.PixelHeight; pixel++)
            {
                if (transformedRaster.Pixels[pixel * 4 + 3] < 240) continue;
                comparisonPixel = pixel;
                break;
            }

            var appearanceMatches = comparisonPixel >= 0
                && appearanceRaster.PixelWidth == transformedRaster.PixelWidth
                && appearanceRaster.PixelHeight == transformedRaster.PixelHeight;
            var actualAppearance = new byte[4];
            var expectedAppearance = new int[4];
            if (appearanceMatches)
            {
                var offset = comparisonPixel * 4;
                for (var channel = 0; channel < 4; channel++) actualAppearance[channel] = appearanceRaster.Pixels[offset + channel];
                expectedAppearance[0] = (transformedRaster.Pixels[offset] * tint.B + 127) / 255;
                expectedAppearance[1] = (transformedRaster.Pixels[offset + 1] * tint.G + 127) / 255;
                expectedAppearance[2] = (transformedRaster.Pixels[offset + 2] * tint.R + 127) / 255;
                expectedAppearance[3] = transformedRaster.Pixels[offset + 3];
                appearanceMatches = Math.Abs(actualAppearance[3] - expectedAppearance[3]) <= 2
                    && Math.Abs(actualAppearance[0] - expectedAppearance[0]) <= 3
                    && Math.Abs(actualAppearance[1] - expectedAppearance[1]) <= 3
                    && Math.Abs(actualAppearance[2] - expectedAppearance[2]) <= 3;
            }
            var expectedObjectArgb = ApplyInstanceAppearanceForRegression(
                Color.FromArgb(128, 128, 128).ToArgb(),
                0.5f,
                tint.ToArgb());
            AssertTimeline(
                appearanceMatches
                && appearanceScene.Argb[appearanceImported] == expectedObjectArgb,
                "Imported SVG composition did not apply instance Alpha and multiply tint to its rendered appearance: "
                + $"pixel={comparisonPixel}, actualBGRA=[{string.Join(',', actualAppearance)}], "
                + $"expectedBGRA=[{string.Join(',', expectedAppearance)}], "
                + $"object=0x{appearanceScene.Argb[appearanceImported]:X8}, expectedObject=0x{expectedObjectArgb:X8}.");

            var dtdPath = Path.Combine(temporaryRoot, "dtd.svg");
            File.WriteAllText(
                dtdPath,
                "<!DOCTYPE svg [<!ENTITY xxe SYSTEM 'file:///does-not-exist'>]><svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><text>&xxe;</text></svg>");
            var dtdRejected = false;
            try
            {
                _ = ImportedSvgRasterizer.Load(dtdPath);
            }
            catch (InvalidDataException)
            {
                dtdRejected = true;
            }

            var malformedPath = Path.Combine(temporaryRoot, "malformed.svg");
            File.WriteAllText(malformedPath, "<svg xmlns='http://www.w3.org/2000/svg'><path>");
            var malformedRejected = false;
            try
            {
                _ = ImportedSvgRasterizer.Load(malformedPath);
            }
            catch (InvalidDataException)
            {
                malformedRejected = true;
            }

            AssertTimeline(
                dtdRejected && malformedRejected,
                "Imported SVG validation accepted DTD or malformed XML content.");
            Console.WriteLine("imported_svg_file_drop_regression=ok");
            Console.WriteLine("imported_svg_zoom_alignment_regression=ok");
            Console.WriteLine("imported_svg_target_size_regression=ok");
            Console.WriteLine("imported_svg_rasterizer_regression=ok");
        }
        finally
        {
            ImportedSvgRasterizer.ClearCache();
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static void RunImportedSvgBreakApartRegression()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
              <path fill="#d93654" fill-rule="evenodd" d="M5 5 H115 V75 H5 Z M35 25 H85 V55 H35 Z"/>
              <path d="M10 65 C35 10 85 10 110 65" fill="none" stroke="#2474c6" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>
            </svg>
            """;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var breakLayerColor = Color.FromArgb(255, 42, 156, 124);
        scene.SetLayerColor(0, breakLayerColor);
        var imported = scene.AddImportedSvgObject(
            0,
            new PointF(80, 40),
            new SizeF(240, 160),
            0.2f,
            source,
            "Logo Sheet");
        var original = scene.CreateSnapshot();
        var result = scene.BreakApartImportedSvgObjects([imported]);
        if (result.ProducedObjects.Length != 2)
        {
            throw new InvalidOperationException(
                $"SVG Break Apart produced unexpected parts: {string.Join(',', result.ProducedObjects.Select(index => scene.ShapeKind[index]))}.");
        }
        var fill = result.ProducedObjects.Single(index => scene.Argb[index] == Color.FromArgb(unchecked((int)0xffd93654)).ToArgb());
        var strokeOutline = result.ProducedObjects.Single(index => scene.Argb[index] == Color.FromArgb(unchecked((int)0xff2474c6)).ToArgb());
        var basicResultLayers = result.ProducedObjects.Select(index => (int)scene.ObjectLayer[index]).Distinct().ToArray();
        AssertTimeline(
            result.ProducedObjects.Length == 2
            && result.Approximations == ImportedSvgBreakApproximation.None
            && scene.LayerCount == 3
            && basicResultLayers.Length == 1
            && scene.LayerNames[basicResultLayers[0]] == "SVG Layer 01"
            && scene.GetLayerKind(1) == DrawingLayerKind.Folder
            && scene.LayerNames[1] == "Logo Sheet"
            && scene.LayerParentIds[basicResultLayers[0]] == scene.LayerIds[1]
            && scene.GetLayerColor(basicResultLayers[0]).ToArgb() == breakLayerColor.ToArgb()
            && !Enumerable.Range(0, scene.ObjectCount).Any(index => scene.ShapeKind[index] == ShapeKind.ImportedSvg)
            && scene.TryGetPathWorldContours(fill, out var contours)
            && contours.Length == 2
            && scene.TryGetPathWorldContours(strokeOutline, out var strokeContours)
            && strokeContours.Length > 0
            && scene.Argb[fill] == Color.FromArgb(unchecked((int)0xffd93654)).ToArgb()
            && scene.Argb[strokeOutline] == Color.FromArgb(unchecked((int)0xff2474c6)).ToArgb()
            && scene.Stroke[fill] == 0
            && scene.Stroke[strokeOutline] == 0
            && scene.ObjectSubOrder[fill] < scene.ObjectSubOrder[strokeOutline]
            && result.ProducedObjects.All(index => scene.ShapeKind[index] == ShapeKind.Path)
            && result.ProducedObjects.All(index => scene.FillAutoMergeProtected[index]),
            "SVG Break Apart did not preserve editable fill and expanded stroke-outline geometry.");

        scene.RestoreSnapshot(original);
        AssertTimeline(
            scene.ObjectCount == 1
            && scene.LayerCount == 1
            && scene.GetLayerColor(0).ToArgb() == breakLayerColor.ToArgb()
            && scene.ShapeKind[0] == ShapeKind.ImportedSvg
            && scene.TryGetImportedSvgSource(0, out var restoredSource)
            && restoredSource == source
            && scene.TryGetImportedSvgName(0, out var restoredName)
            && restoredName == "Logo Sheet",
            "Undo snapshot did not restore an opaque imported SVG after Break Apart.");

        const string paintOrderSource = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="40" viewBox="0 0 100 40">
              <g stroke="#111111" stroke-linecap="butt" stroke-linejoin="miter" stroke-miterlimit="10">
                <rect x="8" y="6" width="50" height="28" fill="#d93654" stroke-width="8"/>
                <rect x="8" y="6" width="50" height="28" fill="#f5f5f5" stroke-width="0.1"/>
              </g>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(2_500, 1_000), paintOrderSource);
        var paintOrderResult = scene.BreakApartImportedSvgObjects([0]);
        var paintOrderObjects = paintOrderResult.ProducedObjects
            .OrderBy(index => scene.ObjectOrder[index])
            .ThenBy(index => scene.ObjectSubOrder[index])
            .ToArray();
        var paintOrderColors = paintOrderObjects.Select(index => scene.Argb[index]).ToArray();
        var wideOutlineBounds = scene.GetObjectWorldBounds(paintOrderObjects[1]);
        var thinOutlineBounds = scene.GetObjectWorldBounds(paintOrderObjects[3]);
        AssertTimeline(
            paintOrderObjects.Length == 4
            && paintOrderResult.Approximations == ImportedSvgBreakApproximation.None
            && paintOrderObjects.All(index => scene.ShapeKind[index] == ShapeKind.Path)
            && paintOrderObjects.All(index => scene.Stroke[index] == 0)
            && paintOrderObjects.All(index => scene.FillAutoMergeProtected[index])
            && paintOrderObjects.Select(index => scene.ObjectLayer[index]).Distinct().Count() == 1
            && paintOrderColors.SequenceEqual(
            [
                Color.FromArgb(unchecked((int)0xffd93654)).ToArgb(),
                Color.FromArgb(unchecked((int)0xff111111)).ToArgb(),
                Color.FromArgb(unchecked((int)0xfff5f5f5)).ToArgb(),
                Color.FromArgb(unchecked((int)0xff111111)).ToArgb()
            ])
            && wideOutlineBounds.Width > thinOutlineBounds.Width
            && wideOutlineBounds.Height > thinOutlineBounds.Height
            && Math.Abs(thinOutlineBounds.Width - 1_253f) <= 2f
            && Math.Abs(thinOutlineBounds.Height - 703f) <= 2f,
            "SVG Break Apart did not preserve per-element fill/stroke paint order with exact stroke outlines.");

        const string nonZeroFillSource = """
            <svg xmlns="http://www.w3.org/2000/svg" width="60" height="60" viewBox="0 0 60 60">
              <path fill="#2474c6" d="M5 5 H55 V55 H5 Z M15 15 H45 V45 H15 Z"/>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(1_500, 1_500), nonZeroFillSource);
        var nonZeroFillResult = scene.BreakApartImportedSvgObjects([0]);
        AssertTimeline(
            nonZeroFillResult.ProducedObjects.Length == 1
            && nonZeroFillResult.Approximations == ImportedSvgBreakApproximation.None
            && scene.TryGetPathWorldContours(nonZeroFillResult.ProducedObjects[0], out var nonZeroContours)
            && nonZeroContours.Length == 1,
            "SVG Break Apart did not normalize nonzero fill geometry to an equivalent editable contour.");

        const string layeredSource = """
            <svg xmlns="http://www.w3.org/2000/svg"
                 xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape"
                 width="100" height="60" viewBox="0 0 100 60">
              <g inkscape:groupmode="layer" inkscape:label="Back plate">
                <rect x="5" y="5" width="70" height="50" fill="#d93654"/>
              </g>
              <g inkscape:groupmode="layer" inkscape:label="Front plate">
                <rect x="35" y="15" width="60" height="30" fill="#2474c6"/>
              </g>
            </svg>
            """;
        scene.CreateEmpty();
        scene.SetLayerColor(0, breakLayerColor);
        AssertTimeline(
            scene.InsertTimelineBlankKeyframe(0, 4),
            "Layered SVG regression could not prepare its nonzero source keyframe.");
        scene.EditFrame = 4;
        var layeredImported = scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(2_500, 1_500), layeredSource);
        var layeredSnapshot = scene.CreateSnapshot();
        var layeredResult = scene.BreakApartImportedSvgObjects([layeredImported]);
        var redLayerObject = layeredResult.ProducedObjects.Single(index => scene.Argb[index] == Color.FromArgb(unchecked((int)0xffd93654)).ToArgb());
        var blueLayerObject = layeredResult.ProducedObjects.Single(index => scene.Argb[index] == Color.FromArgb(unchecked((int)0xff2474c6)).ToArgb());
        var redLayer = scene.ObjectLayer[redLayerObject];
        var blueLayer = scene.ObjectLayer[blueLayerObject];
        AssertTimeline(
            layeredResult.ProducedObjects.Length == 2
            && scene.LayerCount == 4
            && blueLayer < redLayer
            && scene.LayerNames[blueLayer] == "Front plate"
            && scene.LayerNames[redLayer] == "Back plate"
            && scene.GetLayerColor(blueLayer).ToArgb() == breakLayerColor.ToArgb()
            && scene.GetLayerColor(redLayer).ToArgb() == breakLayerColor.ToArgb()
            && scene.ActiveLayer == blueLayer
            && scene.GetLayerKind(1) == DrawingLayerKind.Folder
            && scene.LayerNames[1] == "SVG"
            && scene.LayerParentIds[blueLayer] == scene.LayerIds[1]
            && scene.LayerParentIds[redLayer] == scene.LayerIds[1]
            && !scene.Timeline.EvaluateTargetExposure(scene.LayerIds[blueLayer], 0).HasContent
            && !scene.Timeline.EvaluateTargetExposure(scene.LayerIds[redLayer], 0).HasContent
            && scene.Timeline.EvaluateTargetExposure(scene.LayerIds[blueLayer], 4).SourceKeyframeFrame == 4
            && scene.Timeline.EvaluateTargetExposure(scene.LayerIds[redLayer], 4).SourceKeyframeFrame == 4,
            "SVG Break Apart did not preserve source layers, stacking, layer color, or nonzero-frame ownership.");
        scene.RestoreSnapshot(layeredSnapshot);
        AssertTimeline(
            scene.LayerCount == 1
            && scene.ObjectCount == 1
            && scene.ShapeKind[0] == ShapeKind.ImportedSvg
            && scene.ObjectKeyframeFrame[0] == 4,
            "Undo snapshot did not restore SVG layer creation atomically.");

        const string ordinaryLayerSource = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60">
              <g id="rear"><rect x="5" y="5" width="70" height="50" fill="#d93654"/></g>
              <path d="M20 10 H80 V50 H20 Z" fill="#59a14f"/>
              <g id="front"><rect x="35" y="15" width="60" height="30" fill="#2474c6"/></g>
            </svg>
            """;
        scene.CreateEmpty();
        scene.SetLayerColor(0, breakLayerColor);
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(2_500, 1_500), ordinaryLayerSource);
        var ordinaryLayerResult = scene.BreakApartImportedSvgObjects([0]);
        var ordinaryResultLayers = ordinaryLayerResult.ProducedObjects
            .Select(index => (int)scene.ObjectLayer[index])
            .Distinct()
            .Order()
            .ToArray();
        AssertTimeline(
            ordinaryLayerResult.ProducedObjects.Length == 3
            && scene.LayerCount == 5
            && ordinaryResultLayers.SequenceEqual([2, 3, 4])
            && scene.GetLayerKind(1) == DrawingLayerKind.Folder
            && scene.LayerNames[2] == "front"
            && scene.LayerNames[3] == "SVG Layer 01"
            && scene.LayerNames[4] == "rear"
            && ordinaryResultLayers.All(layer => scene.GetLayerColor(layer).ToArgb() == breakLayerColor.ToArgb()),
            "SVG Break Apart did not create deterministic layers for ordinary groups and loose geometry.");

        const string nonOverlappingLayerSource = """
            <svg xmlns="http://www.w3.org/2000/svg"
                 xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape"
                 width="120" height="40" viewBox="0 0 120 40">
              <g inkscape:groupmode="layer" inkscape:label="Left">
                <rect x="0" y="5" width="30" height="30" fill="#d93654"/>
              </g>
              <g inkscape:groupmode="layer" inkscape:label="Middle">
                <circle cx="60" cy="20" r="14" fill="#59a14f"/>
              </g>
              <g inkscape:groupmode="layer" inkscape:label="Right">
                <rect x="90" y="5" width="30" height="30" fill="#2474c6"/>
              </g>
            </svg>
            """;
        scene.CreateEmpty();
        scene.SetLayerColor(0, breakLayerColor);
        scene.AddImportedSvgObject(
            0,
            PointF.Empty,
            new SizeF(3_000, 1_000),
            nonOverlappingLayerSource,
            "Separated Parts");
        var nonOverlappingResult = scene.BreakApartImportedSvgObjects([0]);
        var nonOverlappingLayers = nonOverlappingResult.ProducedObjects
            .Select(index => (int)scene.ObjectLayer[index])
            .Distinct()
            .ToArray();
        AssertTimeline(
            nonOverlappingResult.ProducedObjects.Length == 3
            && scene.LayerCount == 3
            && scene.GetLayerKind(1) == DrawingLayerKind.Folder
            && scene.LayerNames[1] == "Separated Parts"
            && nonOverlappingLayers.SequenceEqual([2])
            && scene.LayerNames[2] == "Left + Middle + Right"
            && scene.LayerParentIds[2] == scene.LayerIds[1]
            && scene.GetLayerColor(2).ToArgb() == breakLayerColor.ToArgb(),
            "SVG Break Apart did not coalesce non-overlapping source layers into one named folder layer.");

        const string rootGradientSource = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100">
              <linearGradient id="paint" gradientUnits="userSpaceOnUse" x1="10" y1="50" x2="90" y2="50">
                <stop offset="0%" stop-color="#ff0000"/>
                <stop offset="100%" stop-color="#0000ff"/>
              </linearGradient>
              <circle cx="50" cy="50" r="38" fill="none" stroke="url(#paint)" stroke-width="12"/>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(100, 100), rootGradientSource);
        var gradientResult = scene.BreakApartImportedSvgObjects([0]);
        var gradientColors = gradientResult.ProducedObjects
            .Select(index => Color.FromArgb(scene.Argb[index]))
            .Where(color => color.A > 0)
            .Distinct()
            .ToArray();
        AssertTimeline(
            gradientResult.ProducedObjects.Length >= 8
            && gradientResult.Approximations.HasFlag(ImportedSvgBreakApproximation.RasterizedContent)
            && gradientResult.ProducedObjects.Select(index => scene.ObjectLayer[index]).Distinct().Count() == 1
            && scene.LayerNames[scene.ObjectLayer[gradientResult.ProducedObjects[0]]] == "SVG Rasterized"
            && gradientResult.ProducedObjects.All(index => scene.ShapeKind[index] == ShapeKind.Path)
            && gradientResult.ProducedObjects.All(index => scene.FillAutoMergeProtected[index])
            && gradientColors.Length >= 8
            && gradientColors.Any(color => color.R >= 204 && color.B <= 68)
            && gradientColors.Any(color => color.B >= 204 && color.R <= 68),
            "SVG Break Apart collapsed a gradient stroke to a representative solid color.");

        const string adjacentFillSource = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="40" viewBox="0 0 100 40">
              <rect x="0" y="0" width="50" height="40" fill="#d93654"/>
              <rect x="50" y="0" width="50" height="40" fill="#d93654"/>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(100, 40), adjacentFillSource);
        var adjacentResult = scene.BreakApartImportedSvgObjects([0]);
        var adjacentFills = adjacentResult.ProducedObjects
            .Where(index => scene.ShapeKind[index] == ShapeKind.Path)
            .ToArray();
        var protectedMergeCount = scene.ObjectCount;
        var protectedMerge = scene.MergeSameColorFillsAround(adjacentFills[0], connectNearby: true, frame: 0);
        var ordinaryFill = scene.AddPathObjectContours(
            0,
            [
                [
                    new PointF(50, -20),
                    new PointF(70, -20),
                    new PointF(70, 20),
                    new PointF(50, 20)
                ]
            ],
            0,
            Color.FromArgb(unchecked((int)0xffd93654)),
            Color.Transparent,
            4);
        var reverseMergeCount = scene.ObjectCount;
        var reverseMerge = scene.MergeSameColorFillsAround(ordinaryFill, connectNearby: true, frame: 0);
        var protectedSnapshot = scene.CreateSnapshot();
        scene.RestoreSnapshot(protectedSnapshot);
        AssertTimeline(
            adjacentFills.Length == 2
            && adjacentFills.All(index => scene.FillAutoMergeProtected[index])
            && protectedMerge == adjacentFills[0]
            && protectedMergeCount == adjacentFills.Length
            && reverseMerge == ordinaryFill
            && scene.ObjectCount == reverseMergeCount
            && protectedSnapshot.FillAutoMergeProtected.Length == protectedSnapshot.ObjectCount
            && adjacentFills.All(index => protectedSnapshot.FillAutoMergeProtected[index]),
            "SVG Break Apart fills were unexpectedly merged during later geometry or material normalization.");

        const string degeneratePartSource = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="40" viewBox="0 0 100 40">
              <rect x="0" y="0" width="40" height="40" fill="#d93654"/>
              <path d="M80 20 L85 20 L90 20 L85 20 Z" fill="#2474c6"/>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(2_500, 1_000), degeneratePartSource);
        var degeneratePartResult = scene.BreakApartImportedSvgObjects([0]);
        AssertTimeline(
            degeneratePartResult.ProducedObjects.Length == 1
            && degeneratePartResult.Approximations.HasFlag(ImportedSvgBreakApproximation.SkippedInvalidGeometry)
            && scene.ShapeKind[degeneratePartResult.ProducedObjects[0]] == ShapeKind.Path
            && scene.Argb[degeneratePartResult.ProducedObjects[0]] == Color.FromArgb(unchecked((int)0xffd93654)).ToArgb()
            && !Enumerable.Range(0, scene.ObjectCount).Any(index => scene.ShapeKind[index] == ShapeKind.ImportedSvg),
            "SVG Break Apart rejected valid geometry because another SVG part was degenerate.");

        const string textSource = """
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="48" viewBox="0 0 120 48">
              <text x="6" y="36" font-family="Arial" font-size="32" fill="#3a7bd5"><tspan>SVG</tspan></text>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(120, 48), textSource);
        var textResult = scene.BreakApartImportedSvgObjects([0]);
        AssertTimeline(
            textResult.ProducedObjects.Length > 0
            && !textResult.Approximations.HasFlag(ImportedSvgBreakApproximation.RasterizedContent)
            && textResult.ProducedObjects.All(index => scene.ShapeKind[index] == ShapeKind.Path)
            && textResult.ProducedObjects.All(index => scene.TryGetPathWorldContours(index, out var glyphs) && glyphs.Length > 0),
            "SVG Break Apart did not preserve text as editable vector glyph outlines.");

        var embeddedImage = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <rect width="24" height="24" rx="5" fill="#f05a47"/>
              <circle cx="12" cy="12" r="6" fill="#ffd166"/>
            </svg>
            """));
        var complexSource = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
              <defs>
                <path id="tile" d="M0 0 H28 V28 H0 Z"/>
                <clipPath id="crop"><ellipse cx="60" cy="40" rx="54" ry="34"/></clipPath>
              </defs>
              <g clip-path="url(#crop)">
                <use href="#tile" x="8" y="8" fill="#4e79a7"/>
                <path d="M8 64 C36 8 84 8 112 64" fill="none" stroke="#59a14f" stroke-width="6" stroke-dasharray="9 5"/>
                <image x="72" y="18" width="36" height="36" href="data:image/svg+xml;base64,{embeddedImage}"/>
              </g>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(240, 160), complexSource);
        var fallbackResult = scene.BreakApartImportedSvgObjects([0]);
        AssertTimeline(
            fallbackResult.ProducedObjects.Length > 0
            && fallbackResult.Approximations.HasFlag(ImportedSvgBreakApproximation.RasterizedContent)
            && !Enumerable.Range(0, scene.ObjectCount).Any(index => scene.ShapeKind[index] == ShapeKind.ImportedSvg)
            && fallbackResult.ProducedObjects.All(index => scene.ShapeKind[index] == ShapeKind.Path)
            && fallbackResult.ProducedObjects.Any(index => Color.FromArgb(scene.Argb[index]).A > 0)
            && fallbackResult.ProducedObjects.All(index => scene.TryGetPathWorldContours(index, out var regions) && regions.Length > 0),
            "Complex SVG content did not fall back to editable raster-traced compound paths.");

        const string transparentSource = """
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="20">
              <rect width="40" height="20" fill="none"/>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(40, 20), transparentSource);
        var transparentResult = scene.BreakApartImportedSvgObjects([0]);
        AssertTimeline(
            transparentResult.ProducedObjects.Length == 1
            && transparentResult.Approximations.HasFlag(ImportedSvgBreakApproximation.RasterizedContent)
            && scene.ShapeKind[transparentResult.ProducedObjects[0]] == ShapeKind.Path
            && Color.FromArgb(scene.Argb[transparentResult.ProducedObjects[0]]).A == 0,
            "A valid transparent SVG could not complete Break Apart.");

        var project = VectorProject.CreateEmpty();
        var svgAsset = project.DrawingObjects[0];
        svgAsset.Scene.CreateEmpty();
        svgAsset.Scene.LayerOpacity[0] = 0.5f;
        svgAsset.Scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(120, 80), source);
        var nestedContainer = project.AddDrawingObject("Nested opacity host");
        nestedContainer.Scene.CreateEmpty();
        nestedContainer.Scene.LayerOpacity[0] = 0.5f;
        AssertTimeline(
            project.TryAddDrawingObjectInstance(nestedContainer.Id, svgAsset.Id, PointF.Empty, out _),
            "Break Apart regression could not create its nested opacity source.");
        var container = project.AddDrawingObject("Break Apart host");
        container.Scene.CreateEmpty();
        AssertTimeline(
            project.TryAddDrawingObjectInstance(container.Id, nestedContainer.Id, new PointF(300, 180), out var instance)
            && instance is not null,
            "Break Apart regression could not create a nested SVG instance.");
        instance!.ScaleX = 1.5f;
        instance.ScaleY = 0.75f;
        instance.RotationZ = 18;

        var flattened = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectInstanceForBreakApart(
            flattened,
            container,
            instance,
            project.DrawingObjects,
            0);
        var existing = container.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(20, 20),
            0,
            0,
            Color.Black,
            3,
            ShapeKind.Rectangle);
        var materialized = container.Scene.AppendFlattenedSceneToLayer(flattened, 0, 0);
        AssertTimeline(
            flattened.ObjectCount == 2
            && !Enumerable.Range(0, flattened.ObjectCount).Any(index => flattened.ShapeKind[index] == ShapeKind.ImportedSvg)
            && Enumerable.Range(0, flattened.ObjectCount)
                .Where(index => flattened.ShapeKind[index] == ShapeKind.Path)
                .All(index => flattened.FillAutoMergeProtected[index])
            && materialized.Length == 2
            && materialized.All(index => container.Scene.ObjectLayer[index] == 0)
            && materialized.All(index => container.Scene.ObjectOrder[index] < container.Scene.ObjectOrder[existing])
            && materialized
                .Where(index => container.Scene.ShapeKind[index] == ShapeKind.Path)
                .All(index => container.Scene.FillAutoMergeProtected[index])
            && materialized.Any(index => ((container.Scene.Argb[index] >>> 24) & 0xff) is >= 62 and <= 64)
            && materialized.Select(container.Scene.GetObjectWorldBounds).Aggregate(RectangleF.Union).Contains(300, 180),
            "Nested-instance Break Apart did not preserve transform, opacity, host layer, or underlay stack placement.");

        var frameSafetySceneSnapshot = container.Scene.CreateSnapshot();
        var frameSafetyInstanceSnapshot = container.CreateInstanceSnapshot();
        var objectCountBeforeFrameSafety = container.Scene.ObjectCount;
        var hostTrack = container.Scene.Timeline.FindTrackByTargetId(container.Scene.LayerIds[0])
            ?? throw new InvalidOperationException("Break Apart frame-safety regression lost its host track.");
        container.Scene.Timeline.SetTrackDuration(hostTrack.Id, 8);
        AssertTimeline(
            container.Scene.MaterializeAutoKeyframeInPlace(0, 5),
            "Break Apart did not materialize an isolated current-frame host cel.");
        var currentFrameObjects = container.Scene.AppendFlattenedSceneToLayer(flattened, 0, 5);
        AssertTimeline(
            instance.SetStateAtFrame(5, instance.EvaluateState(5) with { Visible = false })
            && instance.EvaluateState(4).Visible
            && !instance.EvaluateState(5).Visible
            && currentFrameObjects.All(index => container.Scene.ObjectKeyframeFrame[index] == 5),
            "Current-frame Break Apart changed an earlier instance frame or wrote into its held source cel.");
        container.RestoreInstanceSnapshot(frameSafetyInstanceSnapshot);
        container.Scene.RestoreSnapshot(frameSafetySceneSnapshot);
        AssertTimeline(
            container.Instances.Single().EvaluateState(5).Visible
            && container.Scene.ObjectCount == objectCountBeforeFrameSafety,
            "Break Apart undo snapshots did not restore instance visibility and current-frame geometry atomically.");
        Console.WriteLine("imported_svg_break_apart_regression=ok");
        Console.WriteLine("nested_instance_break_apart_regression=ok");
    }

    private static void RunTemporaryCanvasPanRegression()
    {
        AssertTimeline(
            MainForm.CanStartTemporaryCanvasPan(
                editorFocused: false,
                stageFocused: false,
                pointerOverStage: true,
                pointerInteractionActive: false),
            "Temporary canvas pan did not activate while the pointer was over the stage.");
        AssertTimeline(
            MainForm.CanStartTemporaryCanvasPan(
                editorFocused: false,
                stageFocused: true,
                pointerOverStage: false,
                pointerInteractionActive: false),
            "Temporary canvas pan did not activate for a focused stage.");
        AssertTimeline(
            !MainForm.CanStartTemporaryCanvasPan(true, true, true, false)
            && !MainForm.CanStartTemporaryCanvasPan(false, false, false, false)
            && !MainForm.CanStartTemporaryCanvasPan(false, true, true, true),
            "Temporary canvas pan captured space from an editor or active pointer interaction.");

        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene)
        {
            Size = new Size(640, 480)
        };
        var originBefore = stage.WorldToScreen(0, 0);
        stage.Pan(37, -19);
        var originAfter = stage.WorldToScreen(0, 0);
        AssertTimeline(
            Math.Abs(originAfter.X - originBefore.X - 37) < 0.01f
            && Math.Abs(originAfter.Y - originBefore.Y + 19) < 0.01f,
            "Stage pan did not preserve the pointer drag delta in screen space.");
    }

    private static void RunImmediateMarqueeOverlayRegression()
    {
        AssertTimeline(
            !StageControl.ShouldUseMarqueeLodPreview(4, 120, 40, 20, 0)
            && StageControl.ShouldUseMarqueeLodPreview(9, 1, 0, 0, 0)
            && StageControl.ShouldUseMarqueeLodPreview(2, 1_000, 600, 400, 0),
            "Marquee fallback LOD did not distinguish light, slow, and dense Stage frames.");

        var scene = new VectorScene();
        scene.Generate(40, 2_400, 240_000);
        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(640, 420)
        };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill };
        form.Controls.Add(stage);
        form.Show();
        Application.DoEvents();
        stage.SetVisibleWorldWidth(1_600);
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D || stage.LastStats.TileLod)
        {
            throw new InvalidOperationException("Marquee preview setup did not begin on the detailed Direct2D object path.");
        }

        var renderedFrames = 0;
        EventHandler rendered = (_, _) => renderedFrames++;
        stage.FrameRendered += rendered;
        stage.Capture = true;
        var overlayUpdatesBefore = stage.MarqueeOverlayUpdateCount;
        var initialWatch = Stopwatch.StartNew();
        stage.SetMarquee(new Point(40, 50), new Point(420, 300));
        initialWatch.Stop();
        var expectedTopLeft = stage.PointToScreen(new Point(40, 50));
        var expectedBounds = new Rectangle(expectedTopLeft, new Size(380, 250));
        if (!stage.MarqueeOverlayActive
            || stage.MarqueeLodPreviewActive
            || stage.MarqueeOverlayScreenBounds != expectedBounds
            || stage.MarqueeOverlayUpdateCount != overlayUpdatesBefore + 1
            || renderedFrames != 0)
        {
            throw new InvalidOperationException("The marquee did not enter the synchronous independent overlay path.");
        }

        const int samples = 24;
        var updateWatch = Stopwatch.StartNew();
        for (var sample = 0; sample < samples; sample++)
        {
            stage.SetMarquee(
                new Point(40, 50),
                new Point(420 + (sample + 1) * 3, 300 + (sample + 1) % 5));
        }
        updateWatch.Stop();
        var averageUpdateMilliseconds = updateWatch.Elapsed.TotalMilliseconds / samples;
        const double updateBudgetMilliseconds = 3;
        if (renderedFrames != 0
            || stage.MarqueeOverlayUpdateCount != overlayUpdatesBefore + samples + 1
            || averageUpdateMilliseconds > updateBudgetMilliseconds)
        {
            throw new InvalidOperationException(
                $"Marquee overlay updates were not immediate: frames={renderedFrames}, " +
                $"averageMs={averageUpdateMilliseconds:0.000}, updates={stage.MarqueeOverlayUpdateCount - overlayUpdatesBefore}.");
        }

        stage.SetSelection([0], 0);
        if (renderedFrames != 0)
        {
            throw new InvalidOperationException("A selection change forced a Stage frame while the immediate marquee overlay was active.");
        }
        stage.ClearMarquee();
        stage.Update();
        Application.DoEvents();
        stage.FrameRendered -= rendered;
        stage.Capture = false;
        if (stage.MarqueeOverlayActive
            || stage.MarqueeOverlayScreenBounds != Rectangle.Empty
            || stage.MarqueeLodPreviewActive
            || stage.LastStats.TileLod
            || renderedFrames != 1)
        {
            throw new InvalidOperationException(
                $"Completing a marquee did not remove its overlay and flush exactly one final Stage frame: frames={renderedFrames}.");
        }

        using var fallbackStage = new StageControl(scene) { Size = new Size(640, 420) };
        fallbackStage.SetMarquee(new Point(40, 50), new Point(420, 300));
        using (var gdiBitmap = new Bitmap(fallbackStage.ClientSize.Width, fallbackStage.ClientSize.Height))
        using (var gdiGraphics = Graphics.FromImage(gdiBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI marquee fallback entry point could not be located.");
            drawGdi.Invoke(fallbackStage, [gdiGraphics]);
            if (fallbackStage.MarqueeOverlayActive
                || !fallbackStage.MarqueeLodPreviewActive
                || !fallbackStage.LastStats.TileLod)
            {
                throw new InvalidOperationException("An unavailable marquee overlay did not preserve the GDI fallback.");
            }
        }
        fallbackStage.ClearMarquee();
        form.Close();
        Console.WriteLine("marquee_immediate_overlay=ok");
        Console.WriteLine("marquee_overlay_stage_renders=0");
        Console.WriteLine($"marquee_overlay_initial_ms={initialWatch.Elapsed.TotalMilliseconds:0.000}");
        Console.WriteLine($"marquee_overlay_update_avg_ms={averageUpdateMilliseconds:0.000}");
        Console.WriteLine($"marquee_overlay_update_budget_ms={updateBudgetMilliseconds:0.000}");
        Console.WriteLine("marquee_overlay_update_budget_met=true");
        Console.WriteLine("marquee_overlay_gdi_fallback=ok");
    }

}
