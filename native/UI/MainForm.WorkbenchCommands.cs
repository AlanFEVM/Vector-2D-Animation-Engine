using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class MainForm : Form
{
    private int _playbackDiagnosticTickCount;

    private void ActivateTool(ToolMode tool)
    {
        if (!CanActivateTool(tool)) return;
        var toolChanged = tool != _tool;
        if (toolChanged && !CommitTextEdit()) return;
        if (toolChanged)
        {
            FinishPointerInteractionForContextChange();
            SessionBreadcrumbs.Record("Tool", $"Activated {tool}");
        }
        CancelTemporaryCanvasPan();
        CancelGradientPointer(restore: true);
        HideBrushColorPalette();
        // A tool switch ends any in-flight anchor drag or box selection: their pointer-up and
        // hover state must not survive into a tool that does not understand anchors.
        AbortMotionTrackPointerSession();
        _tool = tool;
        _stage.ClearDrawingPreview();
        if (tool != ToolMode.SnapPoint) ClearSnapPointPresentation();
        if (tool != ToolMode.SimplePen) CancelPenCurve();
        if (tool != ToolMode.Pen) CancelTraditionalPenPath();
        CancelFreehandStroke();
        if (!IsShapeTool(tool)) HideShapeToolFlyout();
        if (!IsLineTool(tool)) HideLineToolFlyout();
        if (!IsBrushTool(tool)) HideBrushToolFlyout();
        if (!_selectionToolGroup.Contains(tool)) HideToolPairFlyout(_selectionToolGroup);
        if (!_paintToolGroup.Contains(tool)) HideToolPairFlyout(_paintToolGroup);
        if (_selectionToolGroup.Contains(tool)) _selectionToolGroup.ActiveTool = tool;
        else if (_paintToolGroup.Contains(tool)) _paintToolGroup.ActiveTool = tool;
        else if (IsShapeTool(tool)) _activeShapeTool = tool;
        else if (IsLineTool(tool)) _activeLineTool = tool;
        else if (IsBrushTool(tool)) _activeBrushTool = tool;
        if (_selectedObject < 0 && UsesStrokeGradient(tool)) _materialEditor.SetGradientPreviewTarget(strokeTarget: true);
        ApplyToolStrokeWidth(tool);
        if (IsBrushTool(tool) || tool == ToolMode.Eraser) SyncBrushTipSettings();
        if (ToolShapeKind(tool) is { } shape)
        {
            _drawSettings.ShapeKind = shape;
            _drawSettings.NotifyChanged();
        }

        if (IsTextEditActive()) _textEditor.Focus();
        else _stage.Focus();
        RefreshInteractionCursorAtPointer();
        QueueDeferredPresentationRefresh(
            DeferredPresentationRefresh.All,
            refreshLineEndpointStyles: false);
        RefreshToolButtons();
        UpdateEraserOptionsPresentation();
        UpdatePencilSettingsPanelPresentation();
        UpdateShapeSettingsPanelPresentation();
        _brushTipPanel.Visible = IsBrushTool(_tool) || _tool == ToolMode.Eraser;
        _mixingBrushSettingsPanel.Visible = _tool == ToolMode.MixingBrush;
        UpdateSnapPointOverlay();
    }

    private void ImportBrushTip()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = $"{UiLocalization.T("Brush tip images")}|*.png;*.bmp;*.jpg;*.jpeg;*.gif|{UiLocalization.T("All files")}|*.*",
            CheckFileExists = true,
            Multiselect = false,
            Title = UiLocalization.T("Import 128x128 Brush Tip")
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!BrushShape.TryLoad(dialog.FileName, out var brushShape, out var error) || brushShape is null)
        {
            ModernMessageDialog.Show(this, UiLocalization.T(error), UiLocalization.T("Brush Tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _brushShape = brushShape;
        _brushTipPanel.SetBrushShape(_brushShape);
        RefreshInteractionCursorAtPointer();
    }

    private bool IsTextEditActive()
    {
        return _textEditData is not null && _textEditor.Visible;
    }

    private void BeginTextToolEdit(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || DrawingToolsBlocked()) return;

        var world = _stage.ScreenToWorld(e.Location);
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (hit.IsValid
            && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
            && _scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Text
            && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame))
        {
            BeginExistingTextEdit(hit.Key.ObjectIndex);
            return;
        }

        BeginNewTextEdit(world);
    }

    private void BeginExistingTextEdit(int objectIndex)
    {
        if (!_scene.TryGetTextObjectData(objectIndex, out var data)) return;

        _textEditScene = _scene;
        _textEditData = data;
        _textEditObject = objectIndex;
        _textEditLayer = _scene.ObjectLayer[objectIndex];
        _textEditStackKey = new DrawingStackKey(_scene.ObjectOrder[objectIndex], _scene.ObjectSubOrder[objectIndex]);
        _textEditCenter = new PointF(_scene.X[objectIndex], _scene.Y[objectIndex]);
        _textEditTopLeft = null;
        _textEditScaleX = Math.Max(float.Epsilon, _scene.Width[objectIndex] / data.LayoutSize.Width);
        _textEditScaleY = Math.Max(float.Epsilon, _scene.Height[objectIndex] / data.LayoutSize.Height);
        _textEditColor = Color.FromArgb(_scene.Argb[objectIndex]);
        SetSelection(objectIndex);
        _textSettingsPanel.SetSettings(data.FontFamilyName, data.FontSizePoints, data.FontStyle, data.Alignment);
        SetTextEditorContent(data.Content);
        _stage.SetEditingTextObject(objectIndex);
        ShowTextEditor();
    }

    private void BeginNewTextEdit(PointF topLeft)
    {
        var width = Math.Clamp(
            _stage.ScreenLengthToWorld(DefaultTextAreaWidthPixels),
            1f,
            TextGeometry.MaximumLayoutDimension);
        var initialHeight = Math.Clamp(
            _stage.ScreenLengthToWorld(DefaultTextAreaHeightPixels),
            1f,
            TextGeometry.MaximumLayoutDimension);
        var data = TextGeometry.NormalizeForAuthoring(new TextObjectData(
            "",
            _textSettingsPanel.FontFamilyName,
            _textSettingsPanel.FontSizePoints,
            _textSettingsPanel.FontStyle,
            _textSettingsPanel.Alignment,
            new SizeF(width, initialHeight)));

        _textEditScene = _scene;
        _textEditData = data;
        _textEditObject = -1;
        _textEditLayer = _scene.ActiveLayer;
        _textEditStackKey = null;
        _textEditTopLeft = topLeft;
        _textEditScaleX = 1f;
        _textEditScaleY = 1f;
        _textEditCenter = new PointF(
            topLeft.X + data.LayoutSize.Width * 0.5f,
            topLeft.Y + data.LayoutSize.Height * 0.5f);
        _textEditColor = ActiveColor();
        ClearSelection();
        SetTextEditorContent("");
        ShowTextEditor();
    }

    private void ShowTextEditor()
    {
        _textEditor.Visible = true;
        _textEditor.BringToFront();
        UpdateInspector();
        UpdateTextEditorPresentation();
        _textEditor.Focus();
        _textEditor.SelectionStart = _textEditor.TextLength;
        _textEditor.SelectionLength = 0;
    }

    private void SetTextEditorContent(string content)
    {
        _updatingTextEditor = true;
        try
        {
            _textEditor.Text = content;
        }
        finally
        {
            _updatingTextEditor = false;
        }
    }

    private void UpdateTextDraftFromEditor()
    {
        if (_updatingTextEditor || _textEditData is not { } current) return;

        try
        {
            var updated = TextGeometry.NormalizeForAuthoring(current with
            {
                Content = _textEditor.Text,
                FontFamilyName = _textSettingsPanel.FontFamilyName,
                FontSizePoints = _textSettingsPanel.FontSizePoints,
                FontStyle = _textSettingsPanel.FontStyle,
                Alignment = _textSettingsPanel.Alignment
            });
            _textEditData = updated;
            UpdateNewTextCenter(updated);
            UpdateTextEditorPresentation();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            AppLog.Error("Unable to update the text edit preview", exception);
            SetTextEditorContent(current.Content);
            _textEditor.SelectionStart = _textEditor.TextLength;
        }
    }

    private void ApplyTextSettingsChange()
    {
        if (IsTextEditActive())
        {
            UpdateTextDraftFromEditor();
            return;
        }
        if (!IsDrawingInspectorContext()
            || TryGetSelectedTextObject(out var objectIndex, out var current) == false)
        {
            return;
        }

        TextObjectData updated;
        try
        {
            updated = TextGeometry.NormalizeForAuthoring(current with
            {
                FontFamilyName = _textSettingsPanel.FontFamilyName,
                FontSizePoints = _textSettingsPanel.FontSizePoints,
                FontStyle = _textSettingsPanel.FontStyle,
                Alignment = _textSettingsPanel.Alignment
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            AppLog.Error("Unable to apply text settings", exception);
            UpdateInspector();
            return;
        }
        if (updated == current) return;

        var key = new DrawingStackKey(_scene.ObjectOrder[objectIndex], _scene.ObjectSubOrder[objectIndex]);
        var snapshot = CreateCanvasMutationSnapshot([objectIndex]);
        var materializedObject = FindActiveObjectByStackKey(key);
        if (materializedObject < 0 || !_scene.UpdateTextObjectData(materializedObject, updated))
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return;
        }

        PushUndoSnapshot(snapshot);
        SetSelection(materializedObject);
        _hierarchyPanel.RefreshScene();
        if (IsSceneMaskEditing()) RebuildDrawingObjectUnderlay();
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool TryGetSelectedTextObject(out int objectIndex, out TextObjectData data)
    {
        objectIndex = -1;
        data = null!;
        var targets = SelectedActiveDrawingObjectIndices();
        if (targets.Length != 1 || _scene.ShapeKind[targets[0]] != ShapeKind.Text) return false;
        objectIndex = targets[0];
        return _scene.TryGetTextObjectData(objectIndex, out data);
    }

    private bool ApplyMaterialToTextDraft(MaterialChangedEventArgs material)
    {
        if (!IsTextEditActive()) return false;
        if (material.ApplyAll || material.FillChanged || material.OpacityChanged)
        {
            var color = material.ApplyAll || material.FillChanged
                ? material.Fill
                : _textEditColor;
            if (material.ApplyAll || material.OpacityChanged)
            {
                color = Color.FromArgb((int)Math.Clamp(material.Opacity * 255, 0, 255), color);
            }
            _textEditColor = color;
            UpdateTextEditorPresentation();
        }
        return true;
    }

    private bool CommitTextEdit()
    {
        if (!IsTextEditActive()) return true;
        if (!ReferenceEquals(_textEditScene, _scene) || _textEditData is null)
        {
            CancelTextEdit();
            return true;
        }

        UpdateTextDraftFromEditor();
        var data = _textEditData;
        if (data is null) return false;
        var hasDrawableText = !string.IsNullOrWhiteSpace(data.Content);

        if (_textEditObject < 0 && !hasDrawableText)
        {
            EndTextEditSession();
            UpdateInspector();
            _stage.Invalidate();
            return true;
        }

        if (_textEditObject >= 0
            && hasDrawableText
            && _scene.TryGetTextObjectData(_textEditObject, out var current)
            && current == data
            && _scene.Argb[_textEditObject] == _textEditColor.ToArgb())
        {
            EndTextEditSession();
            UpdateInspector();
            _stage.Invalidate();
            return true;
        }

        var snapshot = _textEditObject >= 0
            ? CreateCanvasMutationSnapshot([_textEditObject])
            : CreateCanvasMutationSnapshot(affectedLayers: [_textEditLayer]);
        try
        {
            int selectedObject;
            if (_textEditObject >= 0)
            {
                selectedObject = _textEditStackKey is { } key ? FindActiveObjectByStackKey(key) : -1;
                if (selectedObject < 0 || _scene.ShapeKind[selectedObject] != ShapeKind.Text)
                {
                    throw new InvalidOperationException("The edited text object is no longer available.");
                }

                if (!hasDrawableText)
                {
                    if (_scene.RemoveObjects([selectedObject]) != 1)
                    {
                        throw new InvalidOperationException("The cleared text object could not be removed.");
                    }
                    selectedObject = -1;
                }
                else
                {
                    _scene.Argb[selectedObject] = _textEditColor.ToArgb();
                    if (!_scene.UpdateTextObjectData(selectedObject, data))
                    {
                        throw new InvalidOperationException("The text object could not be updated.");
                    }
                }
            }
            else
            {
                var center = _textEditCenter;
                selectedObject = _scene.AddTextObject(
                    _textEditLayer,
                    center,
                    data,
                    _textEditColor);
            }

            EndTextEditSession();
            PushUndoSnapshot(snapshot);
            if (selectedObject >= 0) SetSelection(selectedObject);
            else ClearSelection();
            _hierarchyPanel.RefreshScene();
            if (IsSceneMaskEditing()) RebuildDrawingObjectUnderlay();
            UpdateInspector();
            _stage.Invalidate();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            RestoreCanvasMutationSnapshot(snapshot);
            AppLog.Error("Unable to commit the text edit", exception);
            if (_textEditObject >= 0
                && (uint)_textEditObject < _scene.ObjectCount
                && _scene.ShapeKind[_textEditObject] == ShapeKind.Text)
            {
                _stage.SetEditingTextObject(_textEditObject);
            }
            _textEditor.Visible = true;
            UpdateTextEditorPresentation();
            _textEditor.Focus();
            return false;
        }
    }

    private void CancelTextEdit()
    {
        if (!IsTextEditActive()) return;
        EndTextEditSession();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void EndTextEditSession()
    {
        _stage.ClearEditingTextObject();
        _textEditor.Visible = false;
        _textEditScene = null;
        _textEditData = null;
        _textEditObject = -1;
        _textEditLayer = -1;
        _textEditStackKey = null;
        _textEditTopLeft = null;
        _textEditScaleX = 1f;
        _textEditScaleY = 1f;
    }

    private void UpdateNewTextCenter(TextObjectData data)
    {
        if (_textEditTopLeft is not { } topLeft) return;
        _textEditCenter = new PointF(
            topLeft.X + data.LayoutSize.Width * _textEditScaleX * 0.5f,
            topLeft.Y + data.LayoutSize.Height * _textEditScaleY * 0.5f);
    }

    private void UpdateTextEditorPresentation()
    {
        if (!IsTextEditActive()
            || _textEditData is not { } data
            || !ReferenceEquals(_textEditScene, _scene))
        {
            return;
        }

        UpdateNewTextCenter(data);
        var center = _stage.WorldToScreen(_textEditCenter.X, _textEditCenter.Y);
        var displayWidth = data.LayoutSize.Width * _textEditScaleX;
        var displayHeight = data.LayoutSize.Height * _textEditScaleY;
        var width = Math.Clamp((int)MathF.Ceiling(_stage.WorldLengthToScreen(displayWidth)) + 4, 48, 32_000);
        var height = Math.Clamp((int)MathF.Ceiling(_stage.WorldLengthToScreen(displayHeight)) + 6, 32, 32_000);
        _textEditor.SetBounds(
            (int)MathF.Round(center.X - width * 0.5f),
            (int)MathF.Round(center.Y - height * 0.5f),
            width,
            height);
        _textEditor.TextAlign = data.Alignment switch
        {
            TextHorizontalAlignment.Center => HorizontalAlignment.Center,
            TextHorizontalAlignment.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left
        };
        _textEditor.ForeColor = Color.FromArgb(_textEditColor.R, _textEditColor.G, _textEditColor.B);

        var dpiScale = 96f / Math.Max(1, _stage.DeviceDpi);
        var fontSize = Math.Clamp(data.FontSizePoints * _textEditScaleY * _stage.Zoom * dpiScale, 1f, 512f);
        var style = data.FontStyle switch
        {
            TextFontStyle.Bold => FontStyle.Bold,
            TextFontStyle.Italic => FontStyle.Italic,
            TextFontStyle.BoldItalic => FontStyle.Bold | FontStyle.Italic,
            _ => FontStyle.Regular
        };
        if (_textEditorOwnedFont is not null
            && string.Equals(_textEditorOwnedFont.FontFamily.Name, data.FontFamilyName, StringComparison.OrdinalIgnoreCase)
            && _textEditorOwnedFont.Style == style
            && Math.Abs(_textEditorOwnedFont.SizeInPoints - fontSize) < 0.05f)
        {
            return;
        }

        var nextFont = CreateTextEditorFont(data.FontFamilyName, fontSize, style);
        var previousFont = _textEditorOwnedFont;
        _textEditorOwnedFont = nextFont;
        _textEditor.Font = nextFont;
        previousFont?.Dispose();
    }

    private static Font CreateTextEditorFont(string familyName, float sizePoints, FontStyle style)
    {
        try
        {
            return new Font(familyName, sizePoints, style, GraphicsUnit.Point);
        }
        catch (ArgumentException)
        {
            try
            {
                return new Font(TextGeometry.FallbackFontFamilyName, sizePoints, style, GraphicsUnit.Point);
            }
            catch (ArgumentException)
            {
                return new Font(FontFamily.GenericSansSerif, sizePoints, FontStyle.Regular, GraphicsUnit.Point);
            }
        }
    }

    private void RestoreDefaultBrushTip()
    {
        _brushShape = BrushShape.CreateSoftRound();
        _brushTipPanel.SetBrushShape(_brushShape);
        RefreshInteractionCursorAtPointer();
    }

    private void SelectTraditionalBrushTip()
    {
        _brushShape = BrushShape.CreateTraditionalBrush();
        _brushTipPanel.SetBrushShape(_brushShape);
        RefreshInteractionCursorAtPointer();
    }

    private void UpdateTraditionalBrushSettings()
    {
        if (!_brushShape.IsTraditionalBrush) return;
        _brushShape = BrushShape.CreateTraditionalBrush(
            _brushTipPanel.TraditionalTipKind,
            _brushTipPanel.TraditionalWidthPercent,
            _brushTipPanel.TraditionalDirectionDegrees);
        _brushTipPanel.SetBrushShape(_brushShape);
        RefreshInteractionCursorAtPointer();
    }

    private void UpdateBrushTipPanelHeight()
    {
        var height = _brushTipPanel.PreferredHeight;
        if (_brushTipPanel.Height == height) return;
        _brushTipPanel.Height = height;
    }

    private void UpdateDrawSettingsPanelHeight()
    {
        var height = _drawSettingsPanel.PreferredHeight;
        if (_drawSettingsPanel.Height == height) return;
        _drawSettingsPanel.Height = height;
    }

    private void UpdateDefaultBrushHardness()
    {
        if (!_brushShape.IsDefaultSoftRound) return;
        _brushShape = BrushShape.CreateSoftRound(_brushTipPanel.Hardness);
        _brushTipPanel.SetBrushShape(_brushShape);
        RefreshInteractionCursorAtPointer();
    }

    private void UpdateBrushStrokeSettings()
    {
        _drawSettings.BrushFrequency = _brushTipPanel.Frequency;
        _drawSettings.BrushContinuous = _brushTipPanel.Continuous;
        _drawSettings.PressureBrushSmoothing = _brushTipPanel.PressureSmoothing;
        _drawSettings.NotifyChanged();
    }

    private void UpdateMixingBrushSettings()
    {
        _drawSettings.MixingBrushSettings = _mixingBrushSettingsPanel.Settings;
        _drawSettings.NotifyChanged();
    }

    private void UpdateBrushTipSize()
    {
        if (!IsBrushTool(_tool) && _tool != ToolMode.Eraser) return;
        var sizePoints = _brushTipPanel.SizePoints;
        if (IsBrushTool(_tool)) _brushStrokeWidthPoints = sizePoints;
        else _eraserStrokeWidthPoints = sizePoints;
        _materialEditor.SetMaterial(_materialEditor.Fill, _materialEditor.Stroke, sizePoints, _materialEditor.Opacity);
        RefreshInteractionCursorAtPointer();
    }

    private float ActiveBrushTipSizePoints() => _tool == ToolMode.Eraser
        ? _eraserStrokeWidthPoints
        : _brushStrokeWidthPoints;

    private void SyncBrushTipSettings()
    {
        _brushTipPanel.SetBrushStrokeSettings(
            _drawSettings.BrushFrequency,
            _drawSettings.BrushContinuous,
            ActiveBrushTipSizePoints(),
            _drawSettings.PressureBrushSmoothing);
    }

    private void ApplyToolStrokeWidth(ToolMode tool)
    {
        float? width = tool switch
        {
            ToolMode.Pencil => _pencilStrokeWidthPoints,
            ToolMode.Eraser => _eraserStrokeWidthPoints,
            _ when IsStandardStrokeTool(tool) => _standardStrokeWidthPoints,
            _ when IsBrushTool(tool) => _brushStrokeWidthPoints,
            _ => null
        };
        if (width is null || Math.Abs(_materialEditor.StrokeWidth - width.Value) < 0.001f) return;

        _materialEditor.SetMaterial(_materialEditor.Fill, _materialEditor.Stroke, width.Value, _materialEditor.Opacity);
    }

    private void ApplyToolCursor()
    {
        if (ApplyTemporaryCanvasPanCursor())
        {
            ClearShotFramingFeedback();
            _stage.SetSceneLightGizmoHover(SceneLightGizmoHandleHit.None);
            return;
        }
        var screen = _stage.PointToClient(Cursor.Position);
        if (_stage.ClientRectangle.Contains(screen) && UpdateShotFramingGizmoCursor(screen)) return;
        if (IsShotDirectorContext())
        {
            _stage.SetSceneLightGizmoHover(SceneLightGizmoHandleHit.None);
            _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = _tool == ToolMode.Hand ? Cursors.SizeAll : Cursors.Default;
            return;
        }
        if (_stage.ClientRectangle.Contains(screen) && UpdateSceneLightGizmoCursor(screen)) return;
        if (_stage.ClientRectangle.Contains(screen) && UpdateSpatialTransformCursor(screen)) return;
        if (IsLassoTool(_tool))
        {
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = Cursors.Cross;
            return;
        }
        if (IsSceneReferenceView() && !IsProjectedScene2DTransformView())
        {
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = _tool == ToolMode.Hand ? Cursors.SizeAll : Cursors.Default;
            return;
        }
        if (_tool == ToolMode.Text)
        {
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = Cursors.IBeam;
            return;
        }
        if (IsBrushTool(_tool) || _tool == ToolMode.Eraser)
        {
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = Cursors.Cross;
            return;
        }

        if (_tool == ToolMode.Fill)
        {
            _stage.ClearBrushTipCursor();
            if (_stage.ClientRectangle.Contains(screen))
            {
                _stage.SetFillToolCursor(screen, ActiveColor());
                UpdateFillHoverPreview(screen);
            }
            else
            {
                _stage.ClearFillToolCursor();
                ClearFillHoverPreview();
            }
            _stage.Cursor = Cursors.Cross;
            return;
        }

        if (_tool is ToolMode.InkBottle or ToolMode.Eyedropper or ToolMode.Gradient or ToolMode.SnapPoint)
        {
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = Cursors.Cross;
            return;
        }

        _stage.ClearBrushTipCursor();
        _stage.ClearFillToolCursor();
        ClearFillHoverPreview();
        _stage.Cursor = IsDrawingTool(_tool)
            ? Cursors.Cross
            : _tool == ToolMode.Hand ? Cursors.SizeAll : Cursors.Default;
    }

    private void UpdateInteractionCursor(Point screen)
    {
        if (ApplyTemporaryCanvasPanCursor())
        {
            ClearShotFramingFeedback();
            _stage.SetSceneLightGizmoHover(SceneLightGizmoHandleHit.None);
            _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
            return;
        }
        if (_viewPanning || _viewZooming || _viewOrbiting || _viewReferencePanning || _viewReferenceZooming)
        {
            ClearShotFramingFeedback();
            _stage.SetSceneLightGizmoHover(SceneLightGizmoHandleHit.None);
            _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
            _stage.ClearBrushTipCursor();
            return;
        }

        if (UpdateShotFramingGizmoCursor(screen)) return;
        if (IsShotDirectorContext())
        {
            _stage.SetSceneLightGizmoHover(SceneLightGizmoHandleHit.None);
            _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = _tool == ToolMode.Hand ? Cursors.SizeAll : Cursors.Default;
            return;
        }
        if (UpdateSceneLightGizmoCursor(screen)) return;
        if (UpdateSpatialTransformCursor(screen)) return;
        if (IsLassoTool(_tool))
        {
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = Cursors.Cross;
            return;
        }
        if (IsSceneReferenceView())
        {
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = _tool == ToolMode.Hand ? Cursors.SizeAll : Cursors.Default;
            return;
        }

        if (_tool == ToolMode.Text)
        {
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = Cursors.IBeam;
            return;
        }

        if (_tool == ToolMode.SnapPoint)
        {
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            UpdateSnapPointHover(screen);
            return;
        }

        if (IsBrushTool(_tool) || _tool == ToolMode.Eraser)
        {
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.SetBrushTipCursor(screen, _brushShape, ActiveStrokeUnits(), _tool == ToolMode.Eraser);
            _stage.Cursor = Cursors.Cross;
            return;
        }

        _stage.ClearBrushTipCursor();

        if (_tool == ToolMode.Fill)
        {
            _stage.SetFillToolCursor(screen, ActiveColor());
            UpdateFillHoverPreview(screen);
            _stage.Cursor = Cursors.Cross;
            return;
        }

        if (_tool == ToolMode.InkBottle)
        {
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = Cursors.Cross;
            return;
        }

        if (_tool == ToolMode.Eyedropper)
        {
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = Cursors.Cross;
            return;
        }

        if (_tool == ToolMode.Gradient)
        {
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = _stage.HitTestGradientOverlay(screen).Kind == GradientHandleKind.None
                ? Cursors.Cross
                : Cursors.SizeAll;
            return;
        }

        _stage.ClearFillToolCursor();

        if (_tool == ToolMode.Select)
        {
            var fillEdgeHit = _stage.HitTestFillEdgeBezierOverlay(screen);
            if (fillEdgeHit.IsValid)
            {
                ClearFillHoverPreview();
                _stage.Cursor = fillEdgeHit.Handle == EditHandleKind.None
                    ? Cursors.Cross
                    : Cursors.SizeAll;
                return;
            }
        }

        if (!IsScene3DView() && _tool == ToolMode.Select && ActiveEditableInstances().Count > 0)
        {
            var world = _stage.ScreenToWorld(screen);
            var localHit = IsSceneCompositionContext()
                ? DrawingElementHit.None
                : _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
            var localObjectHasPriority = localHit.IsValid
                && _scene.IsObjectSelectable(localHit.Key.ObjectIndex, _frame);
            _stage.Cursor = !localObjectHasPriority
                && (TryResolveSceneInstanceAt(world, out _) || IsPointerInsideSelectedSceneInstance(world))
                ? Cursors.SizeAll
                : Cursors.Default;
            return;
        }

        if (_tool == ToolMode.Distort)
        {
            if (_distortVisualHandleActive
                || _stage.TryHitTestDistortVisualHandle(screen, out _))
            {
                _stage.Cursor = Cursors.SizeAll;
                return;
            }

            var handle = _activeTransformHandle != TransformHandleKind.None
                ? _activeTransformHandle
                : _stage.HitTestDistortHandle(screen);
            _stage.Cursor = CursorForTransformHandle(handle);
            return;
        }

        if (_tool == ToolMode.Transform)
        {
            var handle = _activeTransformHandle != TransformHandleKind.None
                ? _activeTransformHandle
                : _stage.HitTestTransformHandle(screen);
            _stage.Cursor = CursorForTransformHandle(handle);
            return;
        }

        if (_tool == ToolMode.Select)
        {
            var handle = _activeHandle != EditHandleKind.None
                ? _activeHandle
                : _stage.HitTestHoveredLineHandle(screen);
            if (handle == EditHandleKind.None && _selectedObject >= 0)
            {
                handle = _stage.HitTestHandle(screen, _selectedObject);
            }

            _stage.Cursor = CursorForEditHandle(handle);
            return;
        }

        ApplyToolCursor();
    }

    private void RefreshInteractionCursorAtPointer()
    {
        var screen = _stage.PointToClient(Cursor.Position);
        if (_stage.ClientRectangle.Contains(screen)) UpdateInteractionCursor(screen);
        else
        {
            _stage.SetSceneLightGizmoHover(SceneLightGizmoHandleHit.None);
            _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
            ApplyToolCursor();
        }
    }

    private void UpdateFillHoverPreview(Point screen)
    {
        if (_tool != ToolMode.Fill || IsSceneCompositionContext() || IsScene3DView())
        {
            ClearFillHoverPreview();
            return;
        }

        var world = _stage.ScreenToWorld(screen);
        var color = ActiveColor();
        if (ReferenceEquals(_fillHoverPreviewScene, _scene)
            && _fillHoverPreviewRevision == _scene.GeometryRevision
            && _fillHoverPreviewFrame == _frame
            && _fillHoverPreviewContours.Length > 0
            && PointInFillPreview(world, _fillHoverPreviewContours))
        {
            _stage.SetFillPreview(_fillHoverPreviewContours, color);
            return;
        }

        PointF[][] contours;
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (hit.IsValid && hit.Key.Kind == DrawingElementKind.Fill)
        {
            contours = _scene.GetFillPartContours(hit, _frame);
        }
        else if (!_scene.TryGetClosedStrokeFillRegion(world, _frame, out contours))
        {
            ClearFillHoverPreview();
            return;
        }

        if (contours.Length == 0)
        {
            ClearFillHoverPreview();
            return;
        }

        _fillHoverPreviewContours = contours;
        _fillHoverPreviewScene = _scene;
        _fillHoverPreviewRevision = _scene.GeometryRevision;
        _fillHoverPreviewFrame = _frame;
        _stage.SetFillPreview(contours, color);
    }

    private void ClearFillHoverPreview()
    {
        _fillHoverPreviewContours = [];
        _fillHoverPreviewScene = null;
        _fillHoverPreviewRevision = -1;
        _fillHoverPreviewFrame = -1;
        _stage.ClearFillPreview();
    }

    private static bool PointInFillPreview(PointF point, IReadOnlyList<PointF[]> contours)
    {
        var inside = false;
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            if (PointOnFillPreviewBoundary(point, contour)) return true;
            if (PointInFillPreviewContour(point, contour)) inside = !inside;
        }

        return inside;
    }

    private static bool PointInFillPreviewContour(PointF point, IReadOnlyList<PointF> contour)
    {
        var inside = false;
        for (int index = 0, previous = contour.Count - 1; index < contour.Count; previous = index++)
        {
            var currentPoint = contour[index];
            var previousPoint = contour[previous];
            if ((currentPoint.Y > point.Y) == (previousPoint.Y > point.Y)) continue;
            var denominator = previousPoint.Y - currentPoint.Y;
            if (Math.Abs(denominator) < 0.0001f) continue;
            var crossingX = (previousPoint.X - currentPoint.X) * (point.Y - currentPoint.Y) / denominator + currentPoint.X;
            if (point.X < crossingX) inside = !inside;
        }

        return inside;
    }

    private static bool PointOnFillPreviewBoundary(PointF point, IReadOnlyList<PointF> contour)
    {
        for (var index = 0; index < contour.Count; index++)
        {
            var start = contour[index];
            var end = contour[(index + 1) % contour.Count];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0.0001f) continue;
            var parameter = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0f, 1f);
            var closest = new PointF(start.X + dx * parameter, start.Y + dy * parameter);
            var distanceX = point.X - closest.X;
            var distanceY = point.Y - closest.Y;
            if (distanceX * distanceX + distanceY * distanceY <= 0.75f * 0.75f) return true;
        }

        return false;
    }

    private static Cursor CursorForTransformHandle(TransformHandleKind handle)
    {
        return handle switch
        {
            TransformHandleKind.TopLeft or TransformHandleKind.BottomRight => Cursors.SizeNWSE,
            TransformHandleKind.TopRight or TransformHandleKind.BottomLeft => Cursors.SizeNESW,
            TransformHandleKind.Top or TransformHandleKind.Bottom => Cursors.SizeNS,
            TransformHandleKind.Left or TransformHandleKind.Right => Cursors.SizeWE,
            TransformHandleKind.Move => Cursors.SizeAll,
            TransformHandleKind.Rotate
                or TransformHandleKind.RotateTopLeft
                or TransformHandleKind.RotateTopRight
                or TransformHandleKind.RotateBottomRight
                or TransformHandleKind.RotateBottomLeft => Cursors.Hand,
            TransformHandleKind.SkewTop or TransformHandleKind.SkewBottom => Cursors.SizeWE,
            TransformHandleKind.SkewLeft or TransformHandleKind.SkewRight => Cursors.SizeNS,
            TransformHandleKind.Focus => Cursors.Cross,
            _ => Cursors.Default
        };
    }

    private static Cursor CursorForEditHandle(EditHandleKind handle)
    {
        return handle switch
        {
            EditHandleKind.BoundsTopLeft or EditHandleKind.BoundsBottomRight => Cursors.SizeNWSE,
            EditHandleKind.BoundsTopRight or EditHandleKind.BoundsBottomLeft => Cursors.SizeNESW,
            EditHandleKind.TextAreaLeft or EditHandleKind.TextAreaRight => Cursors.SizeWE,
            EditHandleKind.LineStart or EditHandleKind.LineEnd or EditHandleKind.BezierControl or EditHandleKind.BezierControl2 => Cursors.Cross,
            _ => Cursors.Default
        };
    }

    private void HookEvents()
    {
        HookSceneOpticsEvents();
        _workspaceTabs.SelectedViewChanged += (_, e) => ShowWorkspace(e.SelectedView);
        _workspaceTabs.WorldGridOpacityChanged += (_, _) => _stage.WorldGridOpacity = _workspaceTabs.WorldGridOpacity / 100f;
        _workspaceTabs.WorldGridTypeChanged += (_, _) => _stage.WorldGridType = _workspaceTabs.WorldGridType;
        _brushTipPanel.ImportRequested += (_, _) => ImportBrushTip();
        _brushTipPanel.SoftRoundRequested += (_, _) => RestoreDefaultBrushTip();
        _brushTipPanel.TraditionalBrushRequested += (_, _) => SelectTraditionalBrushTip();
        _brushTipPanel.FrequencyChanged += (_, _) => UpdateBrushStrokeSettings();
        _brushTipPanel.ContinuousChanged += (_, _) => UpdateBrushStrokeSettings();
        _brushTipPanel.HardnessChanged += (_, _) => UpdateDefaultBrushHardness();
        _brushTipPanel.TraditionalSettingsChanged += (_, _) => UpdateTraditionalBrushSettings();
        _brushTipPanel.PreferredHeightChanged += (_, _) => UpdateBrushTipPanelHeight();
        _drawSettingsPanel.PreferredHeightChanged += (_, _) => UpdateDrawSettingsPanelHeight();
        _brushTipPanel.PressureSmoothingChanged += (_, _) => UpdateBrushStrokeSettings();
        _brushTipPanel.BrushSizeChanged += (_, _) => UpdateBrushTipSize();
        _mixingBrushSettingsPanel.SettingsChanged += (_, _) => UpdateMixingBrushSettings();
        _textSettingsPanel.SettingsChanged += (_, _) => ApplyTextSettingsChange();
        _textEditor.TextChanged += (_, _) => UpdateTextDraftFromEditor();
        _textEditor.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                if (CommitTextEdit()) _stage.Focus();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                CancelTextEdit();
                _stage.Focus();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        _timeline.FrameSelectionChanged += (_, _) => SynchronizeSelectionFromTimelineFrames();
        _timeline.CurrentFrameChanged += (_, _) =>
        {
            if (_syncingFrame) return;
            StopPlayback();
            SetFrame(_timeline.CurrentFrame);
        };
        _timeline.ActiveLayerChanged += (_, _) =>
        {
            if (!CommitTextEdit()) return;
            var cameraSelected = IsSceneBuildingContext()
                && SynchronizeSceneShotSelectionFromTimeline();
            if (IsSceneBuildingContext()
                && !cameraSelected
                && !SynchronizeSceneLightSelectionFromTimeline())
            {
                ClearSceneOpticsLightSelection();
                HandleSceneLayerEditingContextChanged();
            }
            MarkProjectDirty();
            if (_lastDrawingToolsBlocked != DrawingToolsBlocked()) RefreshToolButtons();
            RefreshLayerBlendModePanel();
            UpdateInspectorForTimelineLayerChange();
            _stage.Invalidate();
        };
        _timeline.LayerVisibilityChanged += (_, _) =>
        {
            MarkProjectDirty();
            if (IsSceneBuildingContext()) RebuildSceneComposition();
            else RebuildDrawingObjectUnderlay();
            ClearInactiveSelection();
            UpdateInspector();
            _stage.Invalidate();
        };
        _timeline.AddLayerRequested += (_, _) => AddTimelineLayer();
        _timeline.TabGroupEditStarting += (_, _) => BeginTimelineTabGroupEdit();
        _timeline.TabGroupEditCompleted += (_, _) => CompleteTimelineTabGroupEdit();
        _timeline.AddFolderLayerRequested += (_, _) => AddTimelineFolderLayer();
        _timeline.AddMaskLayerRequested += (_, _) => AddTimelineMaskLayer();
        _timeline.MoveLayerOutOfMaskRequested += (_, _) => MoveTimelineLayerOutOfMask();
        _timeline.FrameWidthCommitted += (_, _) => PersistTimelineFrameWidth();
        _timeline.FrameHeightCommitted += (_, _) => PersistTimelineFrameHeight();
        _timeline.AutoKeyframeChanged += (_, _) => PersistTimelineAutoKeyframe();
        _timeline.SelectedTweenChanged += (_, _) => RefreshTweenCurveInspector();
        _timeline.CommandRequested += (_, e) => HandleTimelineCommand(e.Command, e.Cells);
        _timeline.FrameTransformRequested += (_, e) => TransformTimelineFrames(e);
        _timeline.LayerMoveRequested += (_, e) => MoveTimelineLayer(
            e.TrackId,
            e.TargetTrackId,
            e.Placement,
            e.MoveOutOfMask);
        _timeline.RemoveLayersRequested += (_, _) => RemoveTimelineLayers();
        _timeline.LayerRenameRequested += (_, _) => RenameTimelineLayer();
        _timeline.LayerColorRequested += (_, _) => ChooseTimelineLayerColor();
        _timeline.LayerLockRequested += (_, _) => ToggleTimelineLayerLock();
        _timeline.LayerOutlineRequested += (_, _) => ToggleTimelineLayerOutline();
        _timeline.AllLayerVisibilityRequested += (_, _) => ToggleAllTimelineLayerVisibility();
        _timeline.AllLayerLocksRequested += (_, _) => ToggleAllTimelineLayerLocks();
        _timeline.AllLayerOutlinesRequested += (_, _) => ToggleAllTimelineLayerOutlines();
        _timeline.OnionSkinToggleRequested += (_, _) => ToggleTimelineOnionSkin();
        _timeline.MotionTrackToggleRequested += (_, _) => ToggleTimelineMotionTrack();
        _timeline.MotionTrackRangeChanged += (_, e) => SetMotionTrackRange(e.FirstFrame, e.LastFrame);
        _timeline.OnionSkinRangeChanged += (_, e) => SetTimelineOnionSkinRange(e.PreviousFrames, e.NextFrames);
        _timeline.OnionSkinRangeInteractionStarted += (_, _) => BeginTimelineOnionSkinRangeEdit();
        _timeline.OnionSkinRangeInteractionCompleted += (_, _) => CompleteTimelineOnionSkinRangeEdit();
        _timeline.OnionSkinRangeInteractionCanceled += (_, _) => CancelTimelineOnionSkinRangeEdit();
        _tweenCurveEditorPanel.InteractionStarted += (_, _) => BeginTweenCurveEdit();
        _tweenCurveEditorPanel.CurveCommitted += (_, e) => ApplyTweenCurve(e.Anchors);
        _tweenCurveEditorPanel.InteractionCompleted += (_, _) => CompleteTweenCurveEdit();
        _tweenCurveEditorPanel.InteractionCanceled += (_, _) => CancelTweenCurveEdit();
        _layerBlendModePanel.BlendModeChanged += (_, e) => ApplySelectedLayerBlendMode(e.BlendMode);
        _sceneEditorPanel.AddSceneRequested += (_, _) => AddScene();
        _sceneEditorPanel.AddSceneInstanceRequested += (_, _) => AddSceneInstanceFromActiveDrawingObject();
        _sceneEditorPanel.SceneSelectionChanged += (_, e) => SelectScene(e.Index);
        _sceneEditorPanel.DrawingObjectOpenRequested += (_, e) => OpenDrawingObjectEditor(e.DrawingObjectId);
        _libraryVaultPanel.DrawingObjectOpenRequested += (_, e) => OpenDrawingObjectEditor(e.DrawingObjectId);
        _libraryVaultPanel.DrawingObjectRenameRequested += (_, e) => RenameDrawingObject(e.DrawingObjectId);
        _libraryVaultPanel.DrawingObjectDuplicateRequested += (_, e) => DuplicateDrawingObject(e.DrawingObjectId);
        _libraryVaultPanel.DrawingObjectDeleteRequested += (_, e) => DeleteDrawingObject(e.DrawingObjectId);
        _libraryVaultPanel.DrawingObjectSvgExportRequested += (_, e) => ExportDrawingObjectSvg(e.DrawingObjectId);
        _libraryVaultPanel.DrawingObjectSymbolExportRequested += (_, e) => ExportDrawingObjectSymbolPackage(e.DrawingObjectId);
        _libraryVaultPanel.DrawingObjectSymbolImportRequested += (_, e) => ImportDrawingObjectSymbolPackages(e.FileNames);
        _libraryVaultPanel.DrawingObjectAssetTagsRequested += (_, e) => EditDrawingObjectAssetTags(e.DrawingObjectId);
        _libraryVaultPanel.DrawingObjectAssetTagAssignmentRequested += (_, e) =>
            SetDrawingObjectAssetTagAssignment(e.DrawingObjectId, e.TagId, e.Assigned);
        _libraryVaultPanel.ExternalSvgAssetAddRequested += (_, _) => AddExternalSvgAssetLink();
        _libraryVaultPanel.ExternalSvgAssetFilesDropped += (_, e) => AddExternalSvgAssetLinksFromFiles(e.FileNames);
        _libraryVaultPanel.ExternalSvgAssetUseRequested += (_, e) => UseExternalSvgAssetLink(e.AssetId);
        _libraryVaultPanel.ExternalSvgAssetRelocateRequested += (_, e) => RelocateExternalSvgAssetLink(e.AssetId);
        _libraryVaultPanel.ExternalSvgAssetDeleteRequested += (_, e) => DeleteExternalSvgAssetLink(e.AssetId);
        _libraryVaultPanel.SetExternalSvgAssetAvailabilityProvider(asset =>
            ResolveExternalSvgAssetPath(asset) is not null);
        _libraryVaultPanel.ImageAssetAddRequested += (_, _) => ImportImageAsset();
        _libraryVaultPanel.ImageAssetFilesDropped += (_, e) => ImportImageAssetsFromFiles(e.FileNames);
        _libraryVaultPanel.ImageAssetPlaceRequested += (_, e) => PlaceImageAsset(e.AssetId);
        _libraryVaultPanel.ImageAssetImportSettingsRequested += (_, e) => EditImageAssetImportSettings(e.AssetId);
        _libraryVaultPanel.ImageAssetRelinkRequested += (_, e) => RelinkImageAsset(e.AssetId);
        _libraryVaultPanel.ImageAssetDeleteRequested += (_, e) => DeleteImageAsset(e.AssetId);
        _libraryVaultPanel.SetImageAssetAvailabilityProvider(asset => ResolveImageAssetPath(asset) is not null);
        _libraryVaultPanel.SetImageAssetPreviewProvider(asset =>
        {
            var path = ResolveImageAssetPath(asset);
            if (path is null) return null;
            try
            {
                return BitmapImageRasterizer.Decode(path, asset.ImportSettings);
            }
            catch (Exception exception) when (exception is InvalidDataException or OutOfMemoryException)
            {
                return null;
            }
        });
        _libraryVaultPanel.AssetFolderCreateRequested += (_, e) => CreateProjectAssetFolder(e.ParentFolderId);
        _libraryVaultPanel.AssetFolderRenameRequested += (_, e) => RenameProjectAssetFolder(e.FolderId);
        _libraryVaultPanel.AssetFolderDuplicateRequested += (_, e) => DuplicateProjectAssetFolder(e.FolderId);
        _libraryVaultPanel.AssetFolderMoveRequested += (_, e) => MoveProjectAssetFolder(e.AssetId, e.TargetFolderId);
        _libraryVaultPanel.DrawingObjectMoveRequested += (_, e) => MoveDrawingObjectToAssetFolder(e.AssetId, e.TargetFolderId);
        _sceneEditorPanel.SceneSettingsChanged += (_, e) => UpdateSceneSettings(e);
        _sceneDimensionButton.Click += (_, _) => ToggleSceneDimension();
        _sceneDimensionButton.MouseEnter += (_, _) => _toolTip.ShowFor(_sceneDimensionButton, "Switch scene view");
        _sceneDimensionButton.MouseLeave += (_, _) => _toolTip.HideTip();
        _sceneProjectionButton.Click += (_, _) => ToggleActiveSceneProjection();
        _sceneProjectionButton.MouseEnter += (_, _) =>
            _toolTip.ShowFor(_sceneProjectionButton, SceneProjectionButtonToolTip());
        _sceneProjectionButton.MouseLeave += (_, _) => _toolTip.HideTip();
        _topPlaybackFps.ValueChanged += (_, _) =>
        {
            if (!_updatingTopPlaybackFps) _playbackSettings.Fps = _topPlaybackFps.Value;
        };
        _playbackSettings.FpsChanged += (_, _) =>
        {
            _playbackAccumulator = 0;
            SyncTopPlaybackFps();
            _timeline.PlaybackFps = _playbackSettings.Fps;
            RebuildEditableInstanceComposition();
            UpdateStatusBar();
            PersistProjectPlaybackFps();
        };
        _playbackSettings.LoopPlaybackChanged += (_, _) => PersistProjectLoopPlayback();
        _playbackSettings.FrameRangeChanged += (_, _) =>
        {
            var timelineLastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
            var clampedEnd = Math.Clamp(_playbackSettings.EndFrame, 0, timelineLastFrame);
            var clampedStart = Math.Clamp(_playbackSettings.StartFrame, 0, clampedEnd);
            if (clampedStart != _playbackSettings.StartFrame || clampedEnd != _playbackSettings.EndFrame)
            {
                _playbackSettings.SetFrameRange(clampedStart, clampedEnd);
                return;
            }

            _timeline.StartFrame = _playbackSettings.StartFrame;
            _timeline.EndFrame = _playbackSettings.EndFrame;
            SyncTimelineFrameRange();
            SetFrame(_frame);
            PersistProjectPlaybackRange();
        };
        _materialEditor.ContinuousEditStarted += (_, _) => BeginMaterialContinuousEdit();
        _materialEditor.ContinuousEditCompleted += (_, _) => CompleteMaterialContinuousEdit();
        _materialEditor.ContinuousEditCanceled += (_, _) => CancelMaterialContinuousEdit();
        _materialEditor.LineEndpointStyleChanged += (_, e) => ApplyLineEndpointStyle(e.EndpointStyle, e.StartEndpoint);
        _materialEditor.GradientChanged += (_, e) => ApplyGradientToSelection(e);
        _drawingObjectInstancePanel.PlaybackSettingsChanged += (_, e) =>
            ApplyDrawingObjectPlaybackSettings(e);
        _drawingObjectInstancePanel.AnchorChanged += (_, e) =>
            ApplySelectedDrawingObjectAnchor(e.Anchor);
        _drawingObjectInstancePanel.AppearanceInteractionStarted += (_, _) =>
            BeginInstanceAppearanceEdit();
        _drawingObjectInstancePanel.AppearanceChanged += (_, e) =>
            ApplySelectedDrawingObjectAppearance(e);
        _drawingObjectInstancePanel.AppearanceInteractionCompleted += (_, _) =>
            CompleteInstanceAppearanceEdit();
        _drawingObjectInstancePanel.AppearanceInteractionCanceled += (_, _) =>
            CancelInstanceAppearanceEdit();
        _drawingObjectInstancePanel.FiltersPanel.InteractionStarted += (_, _) => BeginInstanceAppearanceEdit();
        _drawingObjectInstancePanel.FiltersPanel.FiltersChanged += (_, e) => ApplySelectedDrawingObjectFilters(e);
        _drawingObjectInstancePanel.FiltersPanel.InteractionCompleted += (_, _) => CompleteInstanceAppearanceEdit();
        _drawingObjectInstancePanel.FiltersPanel.InteractionCanceled += (_, _) => CancelInstanceAppearanceEdit();
        _drawingObjectInstancePanel.RestoreOriginalSizeRequested += (_, _) =>
            RestoreSelectedDrawingObjectOriginalSize();
        _materialEditor.MaterialChanged += (_, e) =>
        {
            if (IsSceneCompositionContext()) return;
            if (ApplyMaterialToTextDraft(e)) return;
            if (e.StrokeWidthChanged)
            {
                if (_tool == ToolMode.Pencil) _pencilStrokeWidthPoints = e.StrokeWidth;
                else if (IsBrushTool(_tool)) _brushStrokeWidthPoints = e.StrokeWidth;
                else if (_tool == ToolMode.Eraser) _eraserStrokeWidthPoints = e.StrokeWidth;
                else if (IsStandardStrokeTool(_tool)) _standardStrokeWidthPoints = e.StrokeWidth;
            }
            if (IsBrushTool(_tool) || _tool == ToolMode.Eraser) SyncBrushTipSettings();

            if (_selectedElements.Count > 0)
            {
                ApplyMaterialToSelectedElements(e, VectorUnits.StrokePointsToUnits(e.StrokeWidth));
                return;
            }

            if (_selectedObjects.Count > 1)
            {
                ApplyMaterialToSelectedObjects(e, VectorUnits.StrokePointsToUnits(e.StrokeWidth));
                return;
            }

            if (_selectedObject >= 0 && _selectedObject < _scene.ObjectCount)
            {
                var strokeUnits = VectorUnits.StrokePointsToUnits(e.StrokeWidth);
                var selectedKind = _selectedElement.IsValid && _selectedElement.Key.ObjectIndex == _selectedObject
                    ? _selectedElement.Key.Kind
                    : DrawingElementKind.None;
                if (!MaterialChangeAffectsSelection(_selectedObject, selectedKind, e, strokeUnits)) return;
                var snapshot = CreateMaterialUndoSnapshot();
                var editedObject = _selectedObject;
                var hierarchyChanged = false;
                if (selectedKind != DrawingElementKind.None)
                {
                    var selectedElement = _selectedElement;
                    var materialized = _scene.MaterializeSelectedParts(new[] { selectedElement.Key }, _frame);
                    if (!materialized.Success || materialized.Parts.Length != 1)
                    {
                        RestoreCanvasMutationSnapshot(snapshot);
                        return;
                    }

                    var materializedHit = materialized.Changed
                        ? new DrawingElementHit(materialized.Parts[0].Result, -1, 0, 1)
                        : selectedElement;
                    SetSelection(materializedHit);
                    editedObject = materializedHit.Key.ObjectIndex;
                    hierarchyChanged = materialized.Changed;
                }

                var geometryChanged = false;
                var shape = _scene.ShapeKind[editedObject];
                if (selectedKind == DrawingElementKind.Fill)
                {
                    _scene.Argb[editedObject] = TargetFillArgb(editedObject, e);
                    _scene.DisableLinearGradient(editedObject);
                    if (_scene.Stroke[editedObject] != 0)
                    {
                        _scene.Stroke[editedObject] = 0;
                        geometryChanged = true;
                    }

                    hierarchyChanged |= MergeSelectedFillsAfterMaterialChange();
                }
                else if (selectedKind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)
                {
                    if (e.ApplyAll || e.StrokeChanged || e.OpacityChanged)
                    {
                        var color = TargetStrokeColor(editedObject, e);
                        _scene.Argb[editedObject] = Color.FromArgb(0, color).ToArgb();
                        _scene.StrokeArgb[editedObject] = color.ToArgb();
                    }

                    if (e.ApplyAll || e.StrokeWidthChanged)
                    {
                        var targetStrokeUnits = TargetStrokeUnits(editedObject, e, strokeUnits);
                        if (Math.Abs(_scene.Stroke[editedObject] - targetStrokeUnits) > 0.001f)
                        {
                            if (IsFreehandShape(shape))
                            {
                                _scene.UpdateFreehandStrokeWidth(editedObject, targetStrokeUnits, rebuildGeometryIndex: false);
                            }
                            else
                            {
                                _scene.Stroke[editedObject] = targetStrokeUnits;
                                _scene.Height[editedObject] = Math.Max(VectorUnits.FromPixels(3), targetStrokeUnits + VectorUnits.FromPixels(2));
                            }

                            geometryChanged = true;
                        }
                    }
                }
                else
                {
                    ApplyMaterialToWholeObject(editedObject, e, strokeUnits, out geometryChanged);
                    if ((e.ApplyAll || e.FillChanged || e.OpacityChanged) && IsFillShape(shape))
                    {
                        hierarchyChanged |= MergeSelectedFillsAfterMaterialChange();
                    }
                }

                if (geometryChanged) _scene.RebuildGeometryIndex();
                hierarchyChanged |= MergeCompatibleLinesAfterMaterialChange();
                PushMaterialUndoSnapshot(snapshot);
                RefreshHierarchyAfterMaterialChange(hierarchyChanged);
                RefreshInspectorAfterMaterialChange();
                _stage.Invalidate();
            }
        };
        _hierarchyPanel.HierarchySelectionChanged += (_, e) =>
        {
            if (_spatialTransformKeyboardActive) CancelSpatialTransformKeyboard();
            if (!CommitTextEdit()) return;
            if (e.Kind == HierarchyNodeKind.Layer && e.Index >= 0 && e.Index < _scene.LayerCount)
            {
                _scene.ActiveLayer = e.Index;
                RefreshLayers();
                _timeline.SelectModelActiveTrack();
                UpdateInspector();
            }
            else if (e.Kind == HierarchyNodeKind.Object && e.Index >= 0 && e.Index < _scene.ObjectCount)
            {
                SetSelection(e.Index);
                UpdateInspector();
                _stage.Invalidate();
            }
        };
        _hierarchyPanel.HierarchyFocusRequested += (_, e) => HandleHierarchyFocusRequested(e);
        _drawSettings.Changed += (_, _) =>
        {
            SyncBrushTipSettings();
            SyncEraserOptions();
            if (_drawSettings.ShapeKind == _lastSettingsShape) return;
            _lastSettingsShape = _drawSettings.ShapeKind;
            if (ToolModeForShape(_drawSettings.ShapeKind) is { } tool)
            {
                if (CanActivateTool(tool)) _tool = tool;
                if (IsShapeTool(tool))
                {
                    _activeShapeTool = tool;
                    HideLineToolFlyout();
                }
                else if (IsLineTool(tool))
                {
                    _activeLineTool = tool;
                    HideShapeToolFlyout();
                }
                else HideToolFlyouts();
                UpdatePencilSettingsPanelPresentation();
                UpdateShapeSettingsPanelPresentation();
                RefreshToolButtons();
            }
        };

        _stage.MouseWheel += (_, e) =>
        {
            if (ReferenceCameraRightLookSessionActive) return;
            if (_lassoPointerActive) return;
            if (IsSceneReferenceView())
            {
                if (_stage.ReferenceDimension == SceneDimension.TwoD || IsControlPressed())
                {
                    _stage.ZoomReferenceCamera(MathF.Pow(1f / StageControl.ReferenceWheelDollyBase, e.Delta / 120f));
                }
                else _stage.DollyReferenceCamera(e.Delta);
                UpdateStatusBar();
                return;
            }

            _stage.ZoomAt(e.Location, e.Delta > 0 ? 1.12f : 0.89f, interactivePreview: true);
            UpdateStatusBar();
        };
        _stage.FrameRendered += StageFrameRendered;
        _stage.ViewChanged += (_, _) => UpdateTextEditorPresentation();
        _stage.PenPointerInput += StagePenPointerInput;
        HookReferenceCameraKeyboardNavigation();
        _stage.MouseDown += StageMouseDown;
        _stage.MouseDoubleClick += StageMouseDoubleClick;
        _stage.MouseMove += StageMouseMove;
        _stage.MouseLeave += (_, _) =>
        {
            ClearShotFramingFeedback();
            _stage.SetSceneLightGizmoHover(SceneLightGizmoHandleHit.None);
            _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
            _stage.ClearHoveredLineElement();
            if (!_stage.Capture) _stage.ClearPenAnchorGuides();
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            if (!_stage.Capture) ApplyToolCursor();
        };
        _stage.MouseUp += StageMouseUp;
        _stage.MouseCaptureChanged += StageMouseCaptureChanged;
        _stage.DragEnter += StageDragEnter;
        _stage.DragOver += StageDragOver;
        _stage.DragLeave += StageDragLeave;
        _stage.DragDrop += StageDragDrop;
        Deactivate += (_, _) =>
        {
            CancelReferenceCameraRightLook();
            CancelReferenceCameraKeyboardNavigation();
            CommitTextEdit();
            HideBrushColorPalette();
            if (_spatialTransformKeyboardActive) CancelSpatialTransformKeyboard();
            else FinishLostPointerCapture();
            CancelTemporaryCanvasPan();
            ClearShotFramingFeedback();
        };
    }

    private void Generate()
    {
        if (IsSceneCompositionContext())
        {
            AppLog.Info("Stress generation is disabled for non-drawable scene compositions.");
            return;
        }

        AppLog.Info("Generating stress scene");
        Cursor = Cursors.WaitCursor;
        try
        {
            _scene.Generate(1000, 100000, 100000000);
            MarkProjectDirty();
            ResetEditHistory();
            _playbackSettings.SetFrameRange(0, _scene.FrameCount - 1);
            SyncTimelineFrameRange();
            SetFrame(0);
            ClearSelection();
            RefreshLayers();
            _hierarchyPanel.BindScene(_scene);
            _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
            _stage.ResetDefaultView();
            UpdateInspector();
            UpdateStatusBar();
            AppLog.Info($"Stress scene generated. Layers: {_scene.LayerCount}, Objects: {_scene.ObjectCount}, Atoms: {_scene.VirtualAtomCount}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to generate stress scene", ex);
            throw;
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void CreateNewProject()
    {
        AppLog.Info("Creating new empty project");
        AssignProjectDocument(VectorProject.CreateEmpty(), "", dirty: false);
        _libraryVaultPanel.BindProject(_project, () => _frame);
        _sceneEditStage.CreateEmpty();
        _drawingObjectUnderlayStage.CreateEmpty();
        _sceneCompositionResult = SceneCompositionResult.Empty;
        _drawingObjectUnderlayResult = SceneCompositionResult.Empty;
        _activeSceneIndex = 0;
        _activeDrawingObjectIndex = 0;
        _scene = _drawingObjects[0].Scene;
        ResetEditHistory();
        BuildDrawingObjectTabs();
        BindActiveDrawingObjectScene(resetView: true);
        SetProjectDirty(false);
        AppLog.Info("New empty project created with one empty symbol");
    }

    private void CreateNewProjectFromCommand()
    {
        if (!CommitTextEdit()) return;
        if (!TryContinueAfterUnsavedChanges()) return;
        StopPlayback();
        FinishPointerInteractionForFrameChange();
        CreateNewProject();
    }

    private void OpenProjectFromDialog()
    {
        if (!CommitTextEdit()) return;
        if (!TryContinueAfterUnsavedChanges()) return;

        using var dialog = new OpenFileDialog
        {
            Title = UiLocalization.T("Open Project"),
            Filter = $"{UiLocalization.T("Vector 2D Project")} (*{ProjectVaultStore.ProjectExtension})|*{ProjectVaultStore.ProjectExtension}|{UiLocalization.T("All files")} (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(_projectManifestPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_projectManifestPath) ?? "";
        }
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        Cursor = Cursors.WaitCursor;
        try
        {
            SessionBreadcrumbs.Record("Project", $"Opening {dialog.FileName}");
            var project = ProjectVaultStore.Load(dialog.FileName);
            StopPlayback();
            FinishPointerInteractionForFrameChange();
            LoadProjectDocument(project, Path.GetFullPath(dialog.FileName));
            AppLog.Info($"Opened project asset library: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Unable to open project: {dialog.FileName}", ex);
            ShowProjectFileError("The project could not be opened.", ex);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private bool SaveProject()
    {
        if (!CommitTextEdit()) return false;
        return string.IsNullOrWhiteSpace(_projectManifestPath)
            ? SaveProjectAs()
            : SaveProjectTo(_projectManifestPath);
    }

    private bool SaveProjectAs()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = UiLocalization.T("Choose or create a folder for the project asset library."),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = !string.IsNullOrWhiteSpace(_projectManifestPath)
                ? Path.GetDirectoryName(_projectManifestPath) ?? ""
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return false;

        var currentDirectory = string.IsNullOrWhiteSpace(_projectManifestPath)
            ? ""
            : Path.GetDirectoryName(_projectManifestPath) ?? "";
        var fileName = string.Equals(
                Path.GetFullPath(dialog.SelectedPath),
                Path.GetFullPath(string.IsNullOrWhiteSpace(currentDirectory) ? dialog.SelectedPath : currentDirectory),
                StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_projectManifestPath)
                ? Path.GetFileName(_projectManifestPath)
                : SafeProjectFileName(_project.Name) + ProjectVaultStore.ProjectExtension;
        return SaveProjectTo(Path.Combine(dialog.SelectedPath, fileName));
    }

    private bool SaveProjectTo(string manifestPath)
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            SessionBreadcrumbs.Record("Project", $"Saving {manifestPath}");
            FinishPointerInteractionForFrameChange();
            RefreshPendingSceneLightTweenMaterializations();
            _projectManifestPath = ProjectVaultStore.Save(_project, manifestPath);
            SetProjectDirty(false);
            CaptureActiveSceneOpticsSaveCheckpoint();
            AppLog.Info($"Saved project asset library: {_projectManifestPath}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Unable to save project: {manifestPath}", ex);
            ShowProjectFileError("The project could not be saved.", ex);
            return false;
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void LoadProjectDocument(VectorProject project, string manifestPath)
    {
        AssignProjectDocument(project, manifestPath, dirty: false);
        _libraryVaultPanel.BindProject(_project, () => _frame);
        _sceneEditStage.CreateEmpty();
        _drawingObjectUnderlayStage.CreateEmpty();
        _sceneCompositionResult = SceneCompositionResult.Empty;
        _drawingObjectUnderlayResult = SceneCompositionResult.Empty;
        _activeSceneIndex = 0;
        _activeDrawingObjectIndex = 0;
        _frame = 0;
        _scene = _drawingObjects[0].Scene;
        ResetEditHistory();
        BuildDrawingObjectTabs();
        BindActiveDrawingObjectScene(resetView: true);
        ClearSelection();
        UpdateInspector();
        UpdateStatusBar();
        SetProjectDirty(false);
    }

    private void AssignProjectDocument(VectorProject project, string manifestPath, bool dirty)
    {
        ArgumentNullException.ThrowIfNull(project);
        InvalidateSceneCompositionCache();
        _project.Changed -= ProjectChanged;
        _project = project;
        _project.Changed += ProjectChanged;
        _projectManifestPath = manifestPath;
        _projectDirty = dirty;
        UpdateProjectTitle();
    }

    private void ProjectChanged(object? sender, EventArgs e) => MarkProjectDirty();

    private void MarkProjectDirty()
    {
        if (_playing) StopPlaybackCompositionPreload();
        InvalidateSceneCompositionCache();
        SetProjectDirty(true);
    }

    private void SetProjectDirty(bool dirty)
    {
        if (_projectDirty == dirty) return;
        _projectDirty = dirty;
        UpdateProjectTitle();
    }

    private void UpdateProjectTitle()
    {
        var name = string.IsNullOrWhiteSpace(_project.Name) ? UiLocalization.T("Untitled Project") : _project.Name;
        var dirtySuffix = _projectDirty ? " *" : "";
        Text = $"{name}{dirtySuffix} - Vector 2D Animation Engine";
        if (_projectTitleLabel is not null) _projectTitleLabel.Text = name + dirtySuffix;
    }

    private bool TryContinueAfterUnsavedChanges()
    {
        if (!_projectDirty) return true;
        var result = ModernMessageDialog.Show(
            this,
            UiLocalization.T("Save changes to the current project before continuing?"),
            UiLocalization.T("Unsaved Project"),
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        return result switch
        {
            DialogResult.Yes => SaveProject(),
            DialogResult.No => true,
            _ => false
        };
    }

    private void ShowProjectFileError(string message, Exception error)
    {
        ModernMessageDialog.Show(
            this,
            $"{UiLocalization.T(message)}\r\n\r\n{error.Message}",
            UiLocalization.T("Project"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static string SafeProjectFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var normalized = new string(name.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "Untitled Project" : normalized;
    }

    private void RestoreRestartState(EditorRestartState state)
    {
        AppLog.Info("Restoring editor restart session");
        AssignProjectDocument(state.Project, state.ProjectManifestPath, state.ProjectDirty);
        _libraryVaultPanel.BindProject(_project, () => _frame);
        _sceneEditStage.CreateEmpty();
        _drawingObjectUnderlayStage.CreateEmpty();
        _sceneCompositionResult = SceneCompositionResult.Empty;
        _drawingObjectUnderlayResult = SceneCompositionResult.Empty;
        _activeSceneIndex = Math.Clamp(state.ActiveSceneIndex, 0, Math.Max(0, _scenes.Count - 1));
        _activeDrawingObjectIndex = Math.Clamp(state.ActiveDrawingObjectIndex, 0, Math.Max(0, _drawingObjects.Count - 1));
        _frame = Math.Max(0, state.Frame);
        _scene = _drawingObjects[_activeDrawingObjectIndex].Scene;
        ResetEditHistory();
        BuildDrawingObjectTabs();
        _workspaceTabs.WorldGridOpacity = (int)Math.Clamp(MathF.Round(state.StageView.WorldGridOpacity * 100), 0, 100);
        _workspaceTabs.WorldGridType = state.StageView.WorldGridType;
        if (_workspaceTabs.SelectedView == state.Workspace) ShowWorkspace(state.Workspace);
        else _workspaceTabs.SelectedView = state.Workspace;
        var selected = state.SelectedObjects
            .Where(index => index >= 0 && index < _scene.ObjectCount && _scene.IsObjectActive(index, _frame))
            .ToArray();
        if (selected.Length > 0) SetSelection(selected);
        else ClearSelection();
        SetProjectDirty(state.ProjectDirty);
    }

    private void RestoreRestartPresentation(EditorRestartState state)
    {
        if (state.WindowState == FormWindowState.Maximized)
        {
            ApplyMaximizedBounds();
            WindowState = FormWindowState.Maximized;
        }

        UpdateWindowChromeState();
        _stage.RestoreViewState(state.StageView);
        UpdateStatusBar();
        AppLog.Info("Editor restart completed with the current project restored");
    }

    private void RequestEditorRestart()
    {
        if (_restartRequested) return;
        if (!CommitTextEdit()) return;
        StopPlayback();
        FinishPointerInteractionForFrameChange();
        HideToolFlyouts();
        var state = CaptureEditorRestartState();

        if (IsModuleHotReloadEnabled())
        {
            var stateSaved = EditorRestartStore.TrySave(state);
            if (stateSaved && LauncherShutdownSignal.RequestEditorRestart())
            {
                _restartRequested = true;
                AppLog.Info("Editor process restart requested; preserving the current project for the new process");
                ProcessRestartRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            EditorRestartStore.DeletePending();
        }

        var handler = RestartRequested;
        if (handler is null) return;
        _restartRequested = true;
        AppLog.Info("Editor window restart requested; preserving the current project in memory");
        handler(this, new EditorRestartRequestedEventArgs(state));
    }

    private EditorRestartState CaptureEditorRestartState()
    {
        var restartWindowState = NormalizeRestartWindowState(WindowState);
        var restartWindowBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        return new EditorRestartState
        {
            Project = _project,
            ProjectManifestPath = _projectManifestPath,
            ProjectDirty = _projectDirty,
            ActiveSceneIndex = _activeSceneIndex,
            ActiveDrawingObjectIndex = _activeDrawingObjectIndex,
            Workspace = _workspaceTabs.SelectedView,
            Frame = _frame,
            SelectedObjects = _selectedObjects.ToArray(),
            StageView = _stage.CaptureViewState(),
            WindowBounds = restartWindowBounds,
            WindowState = restartWindowState
        };
    }

    internal EditorRestartState CaptureEditorRestartStateForRecovery() => CaptureEditorRestartState();

    private static string RestartEditorToolTip() => IsModuleHotReloadEnabled()
        ? "Restart the editor process, apply pending code changes, and preserve the current project"
        : "Restart editor and preserve the current project";

    private void ResetEditHistory()
    {
        ResetUndoHistory();
        _clipboardObjects.Clear();
    }

    private void ResetUndoHistory()
    {
        ResetCanvasUndoHistory();
        _sceneTimelineUndoStack.Clear();
    }

    private void ResetCanvasUndoHistory()
    {
        _undoStack.Clear();
        _undoCapturedForPointerEdit = false;
        _pointerUndoSnapshot = null;
        _pointerUndoSnapshotWithSharedGeometry = null;
        _marqueeMaterializationSession = null;
    }

    private void RefreshLayers()
    {
        _timeline.RefreshTimeline();
        RefreshShotDirector();
    }

    private void Tick()
    {
        var measuredElapsed = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
        var elapsed = _playing
            ? Math.Min(measuredElapsed, MaxFrameSeconds)
            : Math.Min(measuredElapsed, 1.0);
        _clock.Restart();

        if (_playing)
        {
            var updateCount = _updateBatcher.Consume(elapsed);
            TracePlaybackDiagnostic(
                $"tick_begin elapsed={elapsed:0.0000} updates={updateCount} frame={_frame}");
            if (updateCount > 0)
            {
                _updatesThisSample += UpdateSimulation(updateCount, _updateBatcher.StepSeconds);
            }
            TracePlaybackDiagnostic($"tick_end frame={_frame}");
        }
        else
        {
            _updateBatcher.Reset();
            TickWorkspacePreRender();
        }

        var metricsElapsed = _metricsClock.Elapsed.TotalSeconds;
        if (metricsElapsed >= MetricsRefreshSeconds)
        {
            var rates = CalculatePerformanceRateSample(
                _rendersThisSample,
                _updatesThisSample,
                metricsElapsed,
                _playing);
            _hasRenderRateSample = rates.HasRenderRate;
            _hasUpdateRateSample = rates.HasUpdateRate;
            _renderFps = rates.RenderFps;
            _updatesPerSecond = rates.UpdatesPerSecond;
            _rendersThisSample = 0;
            _updatesThisSample = 0;
            _metricsClock.Restart();
            UpdatePerformanceMetrics();
        }
    }

    internal static PerformanceRateSample CalculatePerformanceRateSample(
        int completedRenders,
        int processedUpdates,
        double elapsedSeconds,
        bool playing)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
        {
            return default;
        }

        // A completed presentation is already a valid sample. Only an empty
        // window has no render rate to report.
        var hasRenderRate = completedRenders > 0;
        var hasUpdateRate = playing && processedUpdates > 0;
        return new PerformanceRateSample(
            hasRenderRate,
            hasRenderRate ? completedRenders / elapsedSeconds : 0,
            hasUpdateRate,
            hasUpdateRate ? processedUpdates / elapsedSeconds : 0);
    }

    private int UpdateSimulation(int updateCount, double fixedDeltaSeconds)
    {
        if (!_playing || updateCount <= 0) return 0;

        var frameStep = PlaybackFrameStepSeconds(_playbackSettings.Fps);
        var start = _playbackSettings.StartFrame;
        var end = _playbackSettings.EndFrame;
        var processedUpdates = updateCount;
        _playbackAccumulator += updateCount * fixedDeltaSeconds;
        var framesToAdvance = (int)(_playbackAccumulator / frameStep);
        if (framesToAdvance <= 0) return processedUpdates;

        var remainingAccumulator = _playbackAccumulator - framesToAdvance * frameStep;
        var currentFrame = Math.Clamp(_frame, start, end);
        var nextFrame = currentFrame;
        var stopAtEnd = false;
        if (_playbackSettings.LoopPlayback)
        {
            var span = Math.Max(1, end - start + 1);
            nextFrame = start + (int)(((long)currentFrame - start + framesToAdvance) % span);
        }
        else
        {
            nextFrame = (int)Math.Min(end, (long)currentFrame + framesToAdvance);
            stopAtEnd = nextFrame >= end;
        }

        if (nextFrame == currentFrame)
        {
            _playbackAccumulator = remainingAccumulator;
            if (stopAtEnd) StopPlayback();
            return processedUpdates;
        }

        TracePlaybackDiagnostic(
            $"set_frame_begin current={currentFrame} next={nextFrame} updates={updateCount}");
        var frameApplied = SetFrame(nextFrame, invalidate: false);
        TracePlaybackDiagnostic($"set_frame_end frame={_frame}");
        if (frameApplied)
        {
            _playbackAccumulator = remainingAccumulator;
            if (stopAtEnd) StopPlayback();
        }
        else
        {
            // Keep the current target frame stable while its immutable
            // composition is being prepared. Discarding the blocked frame's
            // whole step prevents the accumulator from racing past the
            // preloader and requesting a different frame on every tick.
            _playbackAccumulator = remainingAccumulator;
        }

        return processedUpdates;
    }

    private void TracePlaybackDiagnostic(string message)
    {
        if (Environment.GetEnvironmentVariable("VECTOR_BENCH_PLAYBACK_TRACE") != "1") return;
        var sequence = Interlocked.Increment(ref _playbackDiagnosticTickCount);
        if (sequence > 500) return;
        Console.WriteLine($"playback_trace seq={sequence} {message}");
        Console.Out.Flush();
    }

    internal static double PlaybackFrameStepSeconds(decimal playbackFps)
    {
        return 1.0 / (double)Math.Max(1m, playbackFps);
    }

    private void TogglePlayback()
    {
        if (_playing)
        {
            StopPlayback();
            return;
        }

        if (!CommitTextEdit()) return;
        if (_spatialTransformKeyboardActive
            || _spatialTransformPointerSession is not null
            || _spatialTransformEditSession is not null)
        {
            FinishPointerInteractionForContextChange();
        }

        StartPlaybackCompositionPreload();
        _playing = true;
        BeginPlaybackTimerResolution();
        _stage.SetReference3DPlaybackActive(active: true);
        if (_stage.OnionSkinScene is not null) _stage.BindOnionSkinScene(null);
        _updateBatcher.Reset();
        _playbackAccumulator = 0;
        _playbackWarmupPending = true;
        _timer.Interval = PlaybackTimerIntervalMs;
        _clock.Restart();
        // Establish the playback background immediately. The composition
        // preloader may need a snapshot restore before the next frame is
        // available, but the current frame is already renderable.
        _stage.Invalidate();
        _timeline.IsPlaying = true;
        UpdateSpatialTransformPanelState();
        ResetPerformanceRateSample();
        StartPlaybackScheduler();
    }

    private void StopPlayback()
    {
        var hadPlaybackState = _playing
            || _stage.PlaybackActive
            || _stage.Reference3DPlaybackActive
            || _stage.Reference3DOpticalInteractionPreviewActive
            || _playbackSchedulerState is not null
            || _playbackCompositionPreloader is not null
            || _playbackCompositionSceneActive;
        if (!hadPlaybackState)
        {
            StopPlaybackScheduler();
            EndPlaybackTimerResolution();
            return;
        }
        _playing = false;
        StopPlaybackScheduler();
        EndPlaybackTimerResolution();
        _stage.SetReference3DPlaybackActive(active: false);
        _stage.EndReference3DOpticalInteractionPreview();
        var restoreComposition = _playbackCompositionSceneActive;
        StopPlaybackCompositionPreload();
        _updateBatcher.Reset();
        _playbackAccumulator = 0;
        _playbackWarmupPending = false;
        _timer.Interval = IdleTimerIntervalMs;
        _clock.Restart();
        _timeline.IsPlaying = false;
        UpdateSpatialTransformPanelState();
        ResetPerformanceRateSample();
        if (restoreComposition && IsSceneBuildingContext())
        {
            RebuildSceneComposition(refreshScenePanels: false);
        }
        RebuildOnionSkinPreview();
    }

    private void ResetPerformanceRateSample()
    {
        _metricsClock.Restart();
        _rendersThisSample = 0;
        _updatesThisSample = 0;
        _renderFps = 0;
        _updatesPerSecond = 0;
        _hasRenderRateSample = false;
        _hasUpdateRateSample = false;
        UpdatePerformanceMetrics();
    }

    private void UpdatePerformanceMetrics()
    {
        var stats = _stage.LastStats;
        SetLabelText(_fps, _hasRenderRateSample ? $"FPS {_renderFps:0}" : "FPS --");
        SetLabelText(
            _draw,
            stats.TileLod
                ? stats.VisibleObjects > 0 || stats.DrawnObjects > 0
                    ? $"Draw {CompactFormat.Number(stats.DrawnObjects)} / {CompactFormat.Number(stats.VisibleObjects)} + Tiles {CompactFormat.Number(stats.TileDraws)}"
                    : $"Tiles {CompactFormat.Number(stats.TileDraws)}"
                : $"Draw {CompactFormat.Number(stats.DrawnObjects)} / {CompactFormat.Number(stats.VisibleObjects)}");
        var totalAtoms = _scene.VirtualAtomCount;
        if (_stage.UnderlayScene is { } underlay && !ReferenceEquals(underlay, _scene)) totalAtoms += underlay.VirtualAtomCount;
        SetLabelText(_atoms, $"Atoms {CompactFormat.Number(stats.VisibleAtoms)} / {CompactFormat.Number(totalAtoms)}");
        SetLabelText(_zoom, $"Zoom {_stage.ActiveViewZoom * 100:0}%");
        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        SetToolStripText(
            _renderFpsStatus,
            _hasRenderRateSample ? $"Render FPS {_renderFps:0}" : "Render FPS --");
        SetToolStripText(
            _animationFpsStatus,
            $"UPS {(_hasUpdateRateSample ? $"{_updatesPerSecond:0}" : "--")}/{TargetUps:0}  Animation FPS {FormatPlaybackFps(_playbackSettings.Fps)}");
        SetToolStripText(_zoomStatus, $"Zoom {_stage.ActiveViewZoom * 100:0}%");
    }

    private static string FormatPlaybackFps(decimal playbackFps)
    {
        return playbackFps.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
    }

    private void SyncTopPlaybackFps()
    {
        if (_topPlaybackFps.Value == _playbackSettings.Fps) return;
        _updatingTopPlaybackFps = true;
        try
        {
            _topPlaybackFps.Value = _playbackSettings.Fps;
        }
        finally
        {
            _updatingTopPlaybackFps = false;
        }
    }

    private static void SetLabelText(Label label, string text)
    {
        if (!string.Equals(label.Text, text, StringComparison.Ordinal)) label.Text = text;
    }

    private static void SetToolStripText(ToolStripItem item, string text)
    {
        if (!string.Equals(item.Text, text, StringComparison.Ordinal)) item.Text = text;
    }

    private bool SetFrame(
        int frame,
        bool invalidate = true,
        bool refreshScenePanels = true,
        bool reuseCachedSceneComposition = false)
    {
        var timelineLastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        var maximum = Math.Min(_playbackSettings.EndFrame, timelineLastFrame);
        var minimum = Math.Min(_playbackSettings.StartFrame, maximum);
        var next = Math.Clamp(frame, minimum, maximum);
        var frameChanged = next != _frame;
        if (frameChanged)
        {
            if (!_playing)
            {
                if (!CommitTextEdit()) return false;
                HideBrushColorPalette();
                CancelTraditionalPenPath();
                CancelPenCurve();
                FinishPointerInteractionForFrameChange();
                // Only user-initiated navigation is worth a breadcrumb; playback would flood the ring.
                SessionBreadcrumbs.Record("Frame", $"Moved {_frame} -> {next}");
            }
        }
        _syncingFrame = true;
        try
        {
            var playbackCompositionApplied = false;
            if (frameChanged
                && _playing
                && IsSceneCompositionContext()
                && _playbackCompositionPreloader is not null)
            {
                playbackCompositionApplied = TryApplyPlaybackComposition(next, out var appliedFrame);
                if (!playbackCompositionApplied && _playbackCompositionPreloader is not null)
                {
                    // A background composition miss must never rebuild the
                    // entire scene on the UI thread. Keep presenting the
                    // last completed frame until this one is ready.
                    TracePlaybackDiagnostic(
                        $"set_frame_deferred frame={next} current={_frame}");
                    return false;
                }
                if (appliedFrame != next)
                {
                    TracePlaybackDiagnostic(
                        $"set_frame_drop requested={next} applied={appliedFrame}");
                    next = appliedFrame;
                    frameChanged = next != _frame;
                }
            }

            _frame = next;
            _shotDirectorPanel.SetPlayhead(next);
            PushShotDirectorFraming();
            UpdateShotFramingGizmo();
            RefreshShotPreview();
            TracePlaybackDiagnostic(
                $"set_frame_apply next={next} changed={frameChanged} scene_building={IsSceneBuildingContext()}");
            if (IsSceneBuildingContext())
            {
                if (IsSceneMaskEditing()) _scene.EditFrame = next;
                TracePlaybackDiagnostic(
                    $"set_frame_composition applied={playbackCompositionApplied} frame={next}");
                if (playbackCompositionApplied)
                {
                    // The preloader already installed the immutable playback
                    // scene and composition. Rebuilding here would restore the
                    // editable scene and put the full composition cost back on
                    // the UI thread.
                }
                else if (frameChanged)
                {
                    RestoreEditableCompositionScene();
                    RebuildSceneCompositionPreservingWorkspaceFrameCache(
                        refreshScenePanels,
                        reuseCachedSceneComposition);
                }
                else
                {
                    RebuildSceneComposition(refreshScenePanels, reuseCachedSceneComposition);
                }
            }
            else
            {
                _scene.EditFrame = next;
                RebuildDrawingObjectUnderlay();
            }
            _timeline.CurrentFrame = next;
            TracePlaybackDiagnostic($"set_frame_timeline frame={next}");
            if (playbackCompositionApplied && _stage.Reference3DPlaybackActive)
            {
                _stage.SetReference3DPlaybackFrame(next);
                _stage.QueueReference3DPlaybackRaster();
                // A worker may reject a frame (for example when an imported
                // vector item uses a feature outside the playback raster
                // subset) without publishing a result. Always enqueue one
                // coalesced paint so the Direct2D path can present the frame
                // and keep input/view controls responsive.
                _stage.Invalidate();
            }
            else
            {
                _stage.Frame = next;
            }
            TracePlaybackDiagnostic($"set_frame_stage frame={next}");
            if (_playing && frameChanged) _playbackCompositionPreloader?.NotifyCurrentFrame(next);
            if (IsSceneCompositionContext() && !_playing)
            {
                RefreshSceneLightGizmo();
                if (_sceneLightingPanel.Visible && ActiveScene() is { } activeScene)
                {
                    _sceneLightingPanel.SetLights(
                        activeScene.Lights.Select(light => ToEditorState(activeScene, light, next)).ToArray(),
                        _selectedSceneLightId);
                }
            }

            // The shot camera handle stays on screen while playing, so it has to be re-evaluated for the
            // frame that is now displayed. Without this it would freeze at the playback start frame and
            // silently disagree with the animated shot.
            if (frameChanged && _playing) UpdateShotFramingGizmo();
            if (frameChanged && (_selectedElements.Count > 0 || !_playing && IsSceneBuildingContext())) ClearSelection();
            if (!_playing)
            {
                ClearInactiveSelection();
                if (_timeline.HasFrameSelection) SynchronizeSelectionFromTimelineFrames();
                UpdateFillEdgeBezierOverlay();
            }
        }
        finally
        {
            _syncingFrame = false;
        }

        if (invalidate)
        {
            if (frameChanged) _stage.InvalidatePreservingWorkspaceFrameCache();
            else _stage.Invalidate();
        }
        return true;
    }

    internal static int ResolveDrawingObjectStackShortcutDirection(
        Keys keyData,
        bool canvasShortcutsEnabled,
        bool hierarchyTreeFocused,
        bool focusedEditor,
        bool materialEditActive)
    {
        if (materialEditActive
            || focusedEditor
            || (!canvasShortcutsEnabled && !hierarchyTreeFocused))
        {
            return 0;
        }

        return keyData switch
        {
            Keys.Control | Keys.Up => 1,
            Keys.Control | Keys.Down => -1,
            _ => 0
        };
    }

    private static bool ContainsFocusedTreeView(Control control)
    {
        if (!control.ContainsFocus) return false;
        if (control is TreeView) return true;
        foreach (Control child in control.Controls)
        {
            if (ContainsFocusedTreeView(child)) return true;
        }

        return false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F1)
        {
            ToggleInspectorPanel();
            return true;
        }
        if (keyData == Keys.F2)
        {
            ToggleTimelinePanel();
            return true;
        }
        if (_workspaceTabs.TrySelectWorkspaceShortcut(keyData)) return true;
        var focusedEditor = ContainsFocusedEditor(this);
        var interactiveControlFocused = ContainsFocusedInteractiveControl(this);
        var commandButtonFocused = ContainsFocusedButton(this);
        if (keyData == Keys.Escape && ReferenceCameraRightLookSessionActive)
        {
            CancelReferenceCameraRightLook();
            return true;
        }
        if (ReferenceCameraRightLookSessionActive
            && (keyData == Keys.F
                || (keyData & Keys.KeyCode) is Keys.Home or Keys.NumPad1 or Keys.NumPad3 or Keys.NumPad5 or Keys.NumPad7))
        {
            CancelReferenceCameraRightLook();
        }
        if (HandleLassoCommandKey(keyData)) return true;
        if (keyData == Keys.Escape && _shotFramingPointerSession is not null)
        {
            CancelShotFramingPointer();
            return true;
        }
        if (keyData == Keys.Escape && _sceneLightGizmoPointerSession is not null)
        {
            CancelSceneLightGizmoPointer();
            return true;
        }
        if (_spatialTransformKeyboardActive)
        {
            if (focusedEditor || interactiveControlFocused)
            {
                CancelSpatialTransformKeyboard();
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (HandleSpatialTransformShortcut(keyData)) return true;
        }
        if (keyData == Keys.Escape
            && (_spatialTransformPointerSession is not null || _spatialTransformEditSession is not null))
        {
            if (_projectedSceneMoveStartRayOrigin is not null) CancelProjectedSceneMovePointer();
            else if (_spatialTransformPointerSession is not null) CancelSpatialTransformPointer();
            else
            {
                CancelSpatialTransformEdit();
                FinishPointerInteraction();
            }
            return true;
        }
        if (keyData == Keys.Escape && _snapPointEditSession is not null)
        {
            CancelSnapPointPointer(restore: true);
            FinishPointerInteraction();
            return true;
        }
        if (HasActiveCanvasPointerInteraction()
            && (BlocksModelCommandDuringPointerInteraction(keyData)
                || IsScene3DView() && IsReferenceCameraKeyboardShortcut(keyData)))
        {
            return true;
        }
        if (keyData == (Keys.Control | Keys.N))
        {
            CreateNewProjectFromCommand();
            return true;
        }
        if (keyData == (Keys.Control | Keys.O))
        {
            OpenProjectFromDialog();
            return true;
        }
        if (keyData == (Keys.Control | Keys.S))
        {
            SaveProject();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.S))
        {
            SaveProjectAs();
            return true;
        }
        var canvasShortcutsEnabled = _materialEditSession is null
            && !focusedEditor
            && !IsShotDirectorContext()
            && (!interactiveControlFocused || commandButtonFocused);
        var drawingObjectStackDirection = ResolveDrawingObjectStackShortcutDirection(
            keyData,
            canvasShortcutsEnabled,
            ContainsFocusedTreeView(_hierarchyPanel),
            focusedEditor,
            _materialEditSession is not null);
        var stageDeleteEnabled = _materialEditSession is null
            && !_timeline.ContainsFocus
            && !_shotDirectorPanel.ContainsFocus
            && !IsShotDirectorContext()
            && !ContainsFocusedTextEditor(this);
        if (_timeline.ContainsFocus)
        {
            if (keyData == (Keys.Control | Keys.A))
            {
                _timeline.SelectAllVisibleLayerTracks();
                return true;
            }
            if (keyData == (Keys.Control | Keys.C))
            {
                CopyTimelineFrames();
                return true;
            }
            if (keyData == (Keys.Control | Keys.V))
            {
                PasteTimelineFrames();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Z))
            {
                UndoLastEdit();
                return true;
            }
            if (HandleTimelineShortcut(keyData)) return true;
        }
        // Director mode disables geometry shortcuts, but still owns scene history.
        // Text/numeric editors retain local undo; active gestures remain guarded above.
        if (IsShotDirectorContext() && !focusedEditor && _materialEditSession is null
            && keyData == (Keys.Control | Keys.Z) && UndoLastEdit()) return true;
        if (keyData == Keys.Space && TryHoldTemporaryCanvasPan()) return true;
        if (keyData == Keys.Escape && (_spacePanHeld || _spacePanPointerActive))
        {
            CancelTemporaryCanvasPan();
            return true;
        }
        if (canvasShortcutsEnabled && IsTimelineEditShortcut(keyData) && HandleTimelineShortcut(keyData)) return true;
        if (canvasShortcutsEnabled)
        {
            if (keyData == Keys.F && IsScene3DView())
            {
                FocusSelectedSceneInstance();
                return true;
            }
            if (HandleSpatialTransformShortcut(keyData)) return true;
            if (IsSceneCompositionContext() && (keyData & Keys.KeyCode) == Keys.NumPad5)
            {
                ToggleActiveSceneProjection();
                return true;
            }
            if (IsScene3DView() && HandleBlender3DShortcut(keyData)) return true;
            if (keyData == Keys.Escape && _freehandDrawing)
            {
                CancelFreehandStroke();
                FinishPointerInteraction();
                return true;
            }
            if (keyData == Keys.Escape && _gradientEditSnapshot is not null)
            {
                CancelGradientPointer(restore: true);
                FinishPointerInteraction();
                return true;
            }
            if (keyData == Keys.Escape
                && (_fillEdgeBezierArmedSession is not null || _fillEdgeBezierEditSession is not null))
            {
                CancelFillEdgeBezierPointer(restore: true);
                FinishPointerInteraction();
                return true;
            }
            if (keyData == Keys.Escape && _lineBranchDragSession is not null)
            {
                CancelLineBranchDrag(restore: true);
                FinishPointerInteraction();
                return true;
            }
            if (keyData == Keys.Escape && _distortPreviewChanged)
            {
                CancelDistortPointerPreview();
                FinishPointerInteraction();
                return true;
            }
            if (keyData == Keys.Escape
                && _tool is ToolMode.Select or ToolMode.Transform or ToolMode.Distort
                && _undoCapturedForPointerEdit
                && (_pointerUndoSnapshot is not null
                    || IsSceneCompositionContext() && _sceneTimelineUndoStack.Count > 0))
            {
                _pointerUndoSnapshotWithSharedGeometry = null;
                _pointerUndoSnapshot = null;
                _undoCapturedForPointerEdit = false;
                _stage.ClearSelectionDragPreview();
                UndoLastEdit();
                FinishPointerInteraction();
                return true;
            }
            if (keyData == Keys.Escape && _motionTrackDragSession is not null)
            {
                CancelMotionTrackDrag();
                FinishPointerInteraction();
                return true;
            }
            if (keyData == Keys.Escape && _motionTrackMarqueeActive)
            {
                AbortMotionTrackPointerSession();
                FinishPointerInteraction();
                return true;
            }
            if (keyData == Keys.Escape && CancelTraditionalPenPath()) return true;
            if (keyData == Keys.Escape && CancelPenCurve()) return true;
            if (!(commandButtonFocused && keyData == Keys.Enter) && HandleTimelineShortcut(keyData)) return true;
            if (TryActivateConfiguredToolShortcut(keyData)) return true;
            if (keyData == Keys.OemOpenBrackets && AdjustFreehandWidth(increase: false)) return true;
            if (keyData == Keys.Oem6 && AdjustFreehandWidth(increase: true)) return true;
            if (!commandButtonFocused && keyData == Keys.Tab && CycleActiveToolGroup(reverse: false)) return true;
            if (!commandButtonFocused && keyData == (Keys.Shift | Keys.Tab) && CycleActiveToolGroup(reverse: true)) return true;
            if (keyData == (Keys.Control | Keys.Z) && UndoLastEdit()) return true;
            if (keyData == (Keys.Control | Keys.A)
                && _motionTrackEnabled
                && _stage.MotionTrackVisible)
            {
                // While a motion track is on Stage, Select All targets every sampled anchor: that is
                // the selection the transform box and the frame drag operate on.
                SelectAllMotionTrackAnchors();
                return true;
            }

            if (keyData == (Keys.Control | Keys.A) && SelectAllObjectsInCurrentFrame()) return true;
            if (keyData == Keys.F8
                && (ConvertSelectedSceneInstancesToSpatialComponent()
                    || ConvertSelectedDrawingObjectsToSymbol())) return true;
            if (keyData == (Keys.Control | Keys.X) && CutSelectedObjects()) return true;
            if (keyData == (Keys.Control | Keys.C) && CopySelectedObjects()) return true;
            if (keyData == (Keys.Control | Keys.V) && PasteCopiedObjects()) return true;
        }

        if (drawingObjectStackDirection != 0
            && MoveSelectedDrawingObjectsInStack(drawingObjectStackDirection)) return true;

        if (keyData == Keys.Delete && stageDeleteEnabled && DeleteSelectedObject()) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

}
