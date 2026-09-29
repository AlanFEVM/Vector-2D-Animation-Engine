using System.Text.Json;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool TryExecuteCodexDocumentTool(
        string name,
        JsonElement arguments,
        out object result)
    {
        result = null!;
        switch (name)
        {
            case "workspace_set_active":
                result = CodexDocumentSetWorkspace(arguments);
                return true;
            case "layers_get":
                result = CodexDocumentGetLayers();
                return true;
            case "layer_create":
                result = CodexDocumentCreateLayer(arguments);
                return true;
            case "layer_update":
                result = CodexDocumentUpdateLayer(arguments);
                return true;
            case "layer_reorder":
                result = CodexDocumentReorderLayer(arguments);
                return true;
            case "layer_remove":
                result = CodexDocumentRemoveLayer(arguments);
                return true;
            case "layer_set_active":
                result = CodexDocumentSetActiveLayer(arguments);
                return true;
            case "symbols_get":
                result = CodexDocumentGetSymbols();
                return true;
            case "symbol_create":
                result = CodexDocumentCreateSymbol(arguments);
                return true;
            case "symbol_duplicate":
                result = CodexDocumentDuplicateSymbol(arguments);
                return true;
            case "symbol_rename":
                result = CodexDocumentRenameSymbol(arguments);
                return true;
            case "symbol_remove":
                result = CodexDocumentRemoveSymbol(arguments);
                return true;
            case "symbol_set_active":
                result = CodexDocumentSetActiveSymbol(arguments);
                return true;
            case "scene_create":
                result = CodexDocumentCreateScene(arguments);
                return true;
            case "scene_rename":
                result = CodexDocumentRenameScene(arguments);
                return true;
            case "scene_remove":
                result = CodexDocumentRemoveScene(arguments);
                return true;
            case "scene_set_active":
                result = CodexDocumentSetActiveScene(arguments);
                return true;
            case "scene_instances_get":
                result = CodexDocumentGetSceneInstances();
                return true;
            case "scene_instance_create":
                result = CodexDocumentCreateSceneInstance(arguments);
                return true;
            case "scene_instance_remove":
                result = CodexDocumentRemoveSceneInstance(arguments);
                return true;
            default:
                return false;
        }
    }

    private object CodexDocumentSetWorkspace(JsonElement arguments)
    {
        var workspace = CodexDocumentRequiredString(arguments, "workspace");
        var view = workspace switch
        {
            nameof(WorkspaceView.BasicDrawing) => WorkspaceView.BasicDrawing,
            nameof(WorkspaceView.SceneEditor) => WorkspaceView.SceneEditor,
            nameof(WorkspaceView.ShotDirector) => WorkspaceView.ShotDirector,
            _ => throw new ArgumentException("workspace must be BasicDrawing, SceneEditor or ShotDirector.")
        };

        var changed = _workspaceTabs.SelectedView != view;
        if (!changed)
        {
            return new
            {
                changed = false,
                workspace = _workspaceTabs.SelectedView.ToString(),
                state = GetCodexEditorState()
            };
        }

        CodexDocumentPrepareWrite();
        _workspaceTabs.SelectedView = view;
        return new
        {
            changed = true,
            workspace = _workspaceTabs.SelectedView.ToString(),
            state = GetCodexEditorState()
        };
    }

    private object CodexDocumentGetLayers()
    {
        if (_workspaceTabs.SelectedView == WorkspaceView.BasicDrawing)
        {
            var drawingObject = CodexDocumentActiveDrawingObject();
            var scene = drawingObject.Scene;
            var activeLayer = (uint)scene.ActiveLayer < (uint)scene.LayerCount
                ? scene.LayerIds[scene.ActiveLayer]
                : null;
            return new
            {
                workspace = WorkspaceView.BasicDrawing.ToString(),
                activeLayerId = activeLayer,
                layers = Enumerable.Range(0, scene.LayerCount)
                    .Select(index => new
                    {
                        id = scene.LayerIds[index],
                        index,
                        name = scene.LayerNames[index],
                        visible = scene.LayerVisible[index],
                        locked = scene.LayerLocked[index],
                        opacity = scene.LayerOpacity[index],
                        kind = scene.GetLayerKind(index).ToString(),
                        active = index == scene.ActiveLayer
                    })
                    .ToArray()
            };
        }

        var sceneDefinition = CodexDocumentRequireSceneEditorContext();
        return new
        {
            workspace = _workspaceTabs.SelectedView.ToString(),
            activeLayerId = sceneDefinition.ActiveLayerId,
            layers = sceneDefinition.Layers
                .Select((layer, index) => new
                {
                    id = layer.Id,
                    index,
                    name = layer.Name,
                    visible = layer.Visible,
                    locked = false,
                    opacity = (float?)null,
                    kind = layer.Kind.ToString(),
                    active = string.Equals(layer.Id, sceneDefinition.ActiveLayerId, StringComparison.Ordinal)
                })
                .ToArray()
        };
    }

    private object CodexDocumentCreateLayer(JsonElement arguments)
    {
        var name = CodexDocumentOptionalName(arguments, "name");
        if (_workspaceTabs.SelectedView == WorkspaceView.BasicDrawing)
        {
            var drawingObject = CodexDocumentActiveDrawingObject();
            var scene = drawingObject.Scene;
            CodexDocumentPrepareWrite();
            var snapshot = scene.CreateSnapshot();
            var index = scene.AddLayer(name);
            if ((uint)index >= (uint)scene.LayerCount)
            {
                throw new InvalidOperationException("The drawing layer could not be created.");
            }

            PushUndoSnapshot(snapshot);
            CodexDocumentRefreshLayers(sceneContext: false, selectModelActive: true);
            return new { changed = true, layerId = scene.LayerIds[index], layers = CodexDocumentGetLayers() };
        }

        var sceneDefinition = CodexDocumentRequireSceneEditorContext();
        CodexDocumentPrepareWrite();
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        if (!_project.TryAddSceneLayer(sceneDefinition.Id, name, out var layer) || layer is null)
        {
            throw new InvalidOperationException("The scene layer could not be created.");
        }

        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        CodexDocumentRefreshLayers(sceneContext: true, selectModelActive: true);
        return new { changed = true, layerId = layer.Id, layers = CodexDocumentGetLayers() };
    }

    private object CodexDocumentUpdateLayer(JsonElement arguments)
    {
        var layerId = CodexDocumentRequiredString(arguments, "layerId");
        var hasName = arguments.TryGetProperty("name", out var nameElement);
        var name = hasName ? CodexDocumentNormalizeName(nameElement.GetString(), "name") : null;
        var hasVisible = arguments.TryGetProperty("visible", out var visibleElement);
        var visible = hasVisible && visibleElement.GetBoolean();
        var hasLocked = arguments.TryGetProperty("locked", out var lockedElement);
        var locked = hasLocked && lockedElement.GetBoolean();
        var hasOpacity = arguments.TryGetProperty("opacity", out var opacityElement);
        var opacity = hasOpacity ? CodexDocumentNumber(opacityElement, "opacity", 0, 1) : 0;

        if (_workspaceTabs.SelectedView == WorkspaceView.BasicDrawing)
        {
            var drawingObject = CodexDocumentActiveDrawingObject();
            var scene = drawingObject.Scene;
            CodexDocumentFindDrawingLayer(scene, layerId, out var index);
            var changed = (hasName && !string.Equals(scene.LayerNames[index], name, StringComparison.Ordinal))
                || (hasVisible && scene.LayerVisible[index] != visible)
                || (hasLocked && scene.LayerLocked[index] != locked)
                || (hasOpacity && Math.Abs(scene.LayerOpacity[index] - opacity) > float.Epsilon);
            if (!changed) return new { changed = false, layerId, layers = CodexDocumentGetLayers() };

            if (scene.IsLayerEffectivelyLocked(index)
                && (!hasLocked || locked || hasName || hasVisible || hasOpacity
                    || CodexDocumentHasLockedAncestor(scene, index)))
            {
                throw new InvalidOperationException("The layer is locked. Unlock it before changing its content settings.");
            }

            CodexDocumentPrepareWrite();
            var snapshot = scene.CreateSnapshot();
            if (hasName) scene.RenameLayer(index, name);
            if (hasVisible) scene.SetLayerVisible(index, visible);
            if (hasLocked) scene.SetLayerLocked(index, locked);
            if (hasOpacity) scene.LayerOpacity[index] = opacity;
            PushUndoSnapshot(snapshot);
            CodexDocumentRefreshLayers(sceneContext: false);
            return new { changed = true, layerId, layers = CodexDocumentGetLayers() };
        }

        if (hasLocked) throw new InvalidOperationException("Scene layers do not expose a lock state.");
        if (hasOpacity) throw new InvalidOperationException("Scene layers do not expose opacity.");

        var sceneDefinition = CodexDocumentRequireSceneEditorContext();
        var sceneLayer = sceneDefinition.FindLayer(layerId)
            ?? throw new ArgumentException("The scene layer was not found.");
        var nameChanged = hasName && !string.Equals(sceneLayer.Name, name, StringComparison.Ordinal);
        var sceneChanged = nameChanged || (hasVisible && sceneLayer.Visible != visible);
        if (!sceneChanged) return new { changed = false, layerId, layers = CodexDocumentGetLayers() };

        CodexDocumentPrepareWrite();
        var sceneTimelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var sceneLayerSnapshot = sceneDefinition.CreateLayerSnapshot();
        if (nameChanged && !_project.TryRenameSceneLayer(sceneDefinition.Id, layerId, name))
        {
            throw new InvalidOperationException("The scene layer could not be renamed.");
        }
        if (hasVisible) sceneDefinition.SetLayerVisible(layerId, visible);
        PushSceneTimelineUndo(sceneDefinition, sceneTimelineSnapshot, sceneLayerSnapshot);
        CodexDocumentRefreshLayers(sceneContext: true);
        return new { changed = true, layerId, layers = CodexDocumentGetLayers() };
    }

    private object CodexDocumentReorderLayer(JsonElement arguments)
    {
        var layerId = CodexDocumentRequiredString(arguments, "layerId");
        var destinationIndex = CodexDocumentInteger(arguments, "index", 0, int.MaxValue);
        if (_workspaceTabs.SelectedView == WorkspaceView.BasicDrawing)
        {
            var drawingObject = CodexDocumentActiveDrawingObject();
            var scene = drawingObject.Scene;
            CodexDocumentFindDrawingLayer(scene, layerId, out var sourceIndex);
            if (destinationIndex >= scene.LayerCount) throw new ArgumentException("index is outside the layer collection.");
            if (sourceIndex == destinationIndex)
            {
                return new { changed = false, layerId, layers = CodexDocumentGetLayers() };
            }
            if (scene.IsLayerEffectivelyLocked(sourceIndex))
            {
                throw new InvalidOperationException("The layer is locked. Unlock it before reordering.");
            }
            CodexDocumentPrepareWrite();
            var snapshot = scene.CreateSnapshot();
            var changed = scene.MoveLayer(sourceIndex, destinationIndex);
            if (!changed) return new { changed = false, layerId, layers = CodexDocumentGetLayers() };
            PushUndoSnapshot(snapshot);
            CodexDocumentRefreshLayers(sceneContext: false);
            return new { changed = true, layerId, layers = CodexDocumentGetLayers() };
        }

        var sceneDefinition = CodexDocumentRequireSceneEditorContext();
        var sceneLayer = sceneDefinition.FindLayer(layerId)
            ?? throw new ArgumentException("The scene layer was not found.");
        if (destinationIndex >= sceneDefinition.Layers.Count)
        {
            throw new ArgumentException("index is outside the layer collection.");
        }
        var source = sceneDefinition.Layers
            .Select((layer, index) => (layer, index))
            .First(item => ReferenceEquals(item.layer, sceneLayer))
            .index;
        if (source == destinationIndex) return new { changed = false, layerId, layers = CodexDocumentGetLayers() };

        CodexDocumentPrepareWrite();
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        if (!_project.TryMoveSceneLayer(sceneDefinition.Id, layerId, destinationIndex))
        {
            throw new InvalidOperationException("The scene layer could not be reordered.");
        }
        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        CodexDocumentRefreshLayers(sceneContext: true);
        return new { changed = true, layerId, layers = CodexDocumentGetLayers() };
    }

    private object CodexDocumentRemoveLayer(JsonElement arguments)
    {
        var layerId = CodexDocumentRequiredString(arguments, "layerId");
        if (_workspaceTabs.SelectedView == WorkspaceView.BasicDrawing)
        {
            var drawingObject = CodexDocumentActiveDrawingObject();
            var scene = drawingObject.Scene;
            CodexDocumentFindDrawingLayer(scene, layerId, out var index);
            var removal = scene.ResolveLayerRemovalIndices([layerId]);
            if (removal.Length == 0) throw new ArgumentException("The drawing layer was not found.");
            if (removal.Any(layer => scene.IsLayerEffectivelyLocked(layer)))
            {
                throw new InvalidOperationException("The layer is locked. Unlock it before removing it.");
            }
            if (removal.Length >= scene.LayerCount) throw new InvalidOperationException("At least one layer must remain.");

            CodexDocumentPrepareWrite();
            var snapshot = scene.CreateSnapshot();
            var drawingInstanceSnapshot = drawingObject.CreateInstanceSnapshot();
            if (!_project.TryRemoveDrawingObjectLayers(drawingObject.Id, [layerId]))
            {
                throw new InvalidOperationException("The drawing layer could not be removed.");
            }
            PushUndoSnapshot(snapshot, drawingObject, drawingInstanceSnapshot);
            CodexDocumentRefreshLayers(sceneContext: false, clearSelection: true, selectModelActive: true);
            return new { changed = true, layerId, layers = CodexDocumentGetLayers() };
        }

        var sceneDefinition = CodexDocumentRequireSceneEditorContext();
        if (sceneDefinition.FindLayer(layerId) is null) throw new ArgumentException("The scene layer was not found.");
        if (sceneDefinition.Layers.Count <= 1) throw new InvalidOperationException("At least one layer must remain.");
        CodexDocumentPrepareWrite();
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        var instanceSnapshot = sceneDefinition.CreateInstanceSnapshot();
        if (!_project.TryRemoveSceneLayers(sceneDefinition.Id, [layerId]))
        {
            throw new InvalidOperationException("The scene layer could not be removed. A content layer may be required.");
        }
        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot, instanceSnapshot);
        CodexDocumentRefreshLayers(sceneContext: true, clearSelection: true, selectModelActive: true);
        return new { changed = true, layerId, layers = CodexDocumentGetLayers() };
    }

    private object CodexDocumentSetActiveLayer(JsonElement arguments)
    {
        var layerId = CodexDocumentRequiredString(arguments, "layerId");
        var sceneDefinition = _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing
            ? null
            : CodexDocumentRequireSceneEditorContext();
        var currentId = CodexDocumentCurrentLayerId();
        var exists = _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing
            ? CodexDocumentActiveDrawingObject().Scene.LayerIds.Contains(layerId, StringComparer.Ordinal)
            : sceneDefinition!.FindLayer(layerId) is not null;
        if (!exists) throw new ArgumentException("The layer was not found in the active workspace context.");
        if (string.Equals(currentId, layerId, StringComparison.Ordinal))
        {
            return new { changed = false, layerId, layers = CodexDocumentGetLayers() };
        }
        if (!_timeline.SelectSingleLayerTarget(layerId))
        {
            throw new InvalidOperationException("The layer is not selectable in the active timeline context.");
        }
        CodexDocumentPrepareWrite();
        CodexDocumentRefreshLayers(
            sceneContext: _workspaceTabs.SelectedView != WorkspaceView.BasicDrawing,
            selectModelActive: false);
        return new { changed = true, layerId, layers = CodexDocumentGetLayers() };
    }

    private object CodexDocumentGetSymbols()
    {
        return new
        {
            symbols = _drawingObjects
                .Select((drawingObject, index) => new
                {
                    id = drawingObject.Id,
                    index,
                    name = drawingObject.Name,
                    kind = drawingObject.Kind,
                    frameCount = drawingObject.FrameCount,
                    objectCount = drawingObject.Scene.ObjectCount,
                    layerCount = drawingObject.Scene.LayerCount,
                    active = index == _activeDrawingObjectIndex
                })
                .ToArray()
        };
    }

    private object CodexDocumentCreateSymbol(JsonElement arguments)
    {
        var name = CodexDocumentOptionalName(arguments, "name");
        CodexDocumentPrepareWrite();
        var drawingObject = _project.AddDrawingObject(name);
        var index = CodexDocumentDrawingObjectIndex(drawingObject.Id);
        SelectDrawingObject(index);
        return new { changed = true, symbolId = drawingObject.Id, symbols = CodexDocumentGetSymbols() };
    }

    private object CodexDocumentDuplicateSymbol(JsonElement arguments)
    {
        var symbolId = CodexDocumentRequiredString(arguments, "symbolId");
        if (_drawingObjects.All(item => !string.Equals(item.Id, symbolId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The symbol was not found.");
        }
        CodexDocumentPrepareWrite();
        if (!_project.TryDuplicateDrawingObject(symbolId, out var duplicate) || duplicate is null)
        {
            throw new InvalidOperationException("The symbol could not be duplicated.");
        }
        SelectDrawingObject(CodexDocumentDrawingObjectIndex(duplicate.Id));
        return new { changed = true, symbolId = duplicate.Id, symbols = CodexDocumentGetSymbols() };
    }

    private object CodexDocumentRenameSymbol(JsonElement arguments)
    {
        var symbolId = CodexDocumentRequiredString(arguments, "symbolId");
        var name = CodexDocumentNormalizeName(
            arguments.GetProperty("name").GetString(),
            "name");
        var symbol = _drawingObjects.FirstOrDefault(item => string.Equals(item.Id, symbolId, StringComparison.Ordinal))
            ?? throw new ArgumentException("The symbol was not found.");
        if (string.Equals(symbol.Name, name, StringComparison.Ordinal))
        {
            return new { changed = false, symbolId, symbols = CodexDocumentGetSymbols() };
        }
        CodexDocumentPrepareWrite();
        if (!_project.TryRenameDrawingObject(symbolId, name))
        {
            throw new InvalidOperationException("The symbol could not be renamed.");
        }
        RefreshDrawingObjectAssetPresentation();
        return new { changed = true, symbolId, symbols = CodexDocumentGetSymbols() };
    }

    private object CodexDocumentRemoveSymbol(JsonElement arguments)
    {
        var symbolId = CodexDocumentRequiredString(arguments, "symbolId");
        var index = CodexDocumentDrawingObjectIndex(symbolId);
        if (_drawingObjects.Count <= 1) throw new InvalidOperationException("A project must retain at least one symbol.");
        var wasSceneBuildingContext = IsSceneBuildingContext();
        CodexDocumentPrepareWrite();
        if (!_project.TryRemoveDrawingObject(symbolId, out _))
        {
            throw new InvalidOperationException("The symbol could not be removed.");
        }

        if (_activeDrawingObjectIndex > index) _activeDrawingObjectIndex--;
        else if (_activeDrawingObjectIndex == index) _activeDrawingObjectIndex = Math.Min(index, _drawingObjects.Count - 1);
        ResetEditHistory();
        if (wasSceneBuildingContext) BindSceneEditStage(resetView: false);
        else BindActiveDrawingObjectScene(resetView: false);
        BuildDrawingObjectTabs();
        UpdateInspector();
        UpdateStatusBar();
        return new { changed = true, symbolId, symbols = CodexDocumentGetSymbols() };
    }

    private object CodexDocumentSetActiveSymbol(JsonElement arguments)
    {
        var symbolId = CodexDocumentRequiredString(arguments, "symbolId");
        var index = CodexDocumentDrawingObjectIndex(symbolId);
        var changed = _activeDrawingObjectIndex != index;
        if (!changed)
        {
            return new { changed = false, symbolId, symbols = CodexDocumentGetSymbols() };
        }

        CodexDocumentPrepareWrite();
        SelectDrawingObject(index);
        return new { changed, symbolId, symbols = CodexDocumentGetSymbols() };
    }

    private object CodexDocumentCreateScene(JsonElement arguments)
    {
        var name = CodexDocumentOptionalName(arguments, "name");
        CodexDocumentPrepareWrite();
        var scene = _project.AddScene(name);
        SelectScene(CodexDocumentSceneIndex(scene.Id));
        return new { changed = true, sceneId = scene.Id, project = GetCodexProjectInfo() };
    }

    private object CodexDocumentRenameScene(JsonElement arguments)
    {
        var sceneId = CodexDocumentRequiredString(arguments, "sceneId");
        var name = CodexDocumentNormalizeName(arguments.GetProperty("name").GetString(), "name");
        var scene = _scenes.FirstOrDefault(item => string.Equals(item.Id, sceneId, StringComparison.Ordinal))
            ?? throw new ArgumentException("The scene was not found.");
        if (string.Equals(scene.Name, name, StringComparison.Ordinal))
        {
            return new { changed = false, sceneId, project = GetCodexProjectInfo() };
        }
        CodexDocumentPrepareWrite();
        scene.Name = name;
        MarkProjectDirty();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        UpdateStatusBar();
        return new { changed = true, sceneId, project = GetCodexProjectInfo() };
    }

    private object CodexDocumentRemoveScene(JsonElement arguments)
    {
        var sceneId = CodexDocumentRequiredString(arguments, "sceneId");
        if (_scenes.Count <= 1) throw new InvalidOperationException("A project must retain at least one scene.");
        var removedIndex = CodexDocumentSceneIndex(sceneId);
        var activeSceneId = ActiveScene()?.Id;
        CodexDocumentPrepareWrite();
        if (!_project.TryRemoveScene(sceneId, out _))
        {
            throw new InvalidOperationException("The scene could not be removed.");
        }

        _activeSceneIndex = _scenes
            .Select((scene, index) => (scene, index))
            .FirstOrDefault(item => string.Equals(item.scene.Id, activeSceneId, StringComparison.Ordinal),
                (scene: _scenes[Math.Min(removedIndex, _scenes.Count - 1)], index: Math.Min(removedIndex, _scenes.Count - 1)))
            .index;
        _sceneViewDimensions.Remove(sceneId);
        ResetEditHistory();
        ClearSelection();
        BuildDrawingObjectTabs();
        if (_workspaceTabs.SelectedView == WorkspaceView.BasicDrawing)
        {
            BindActiveDrawingObjectScene(resetView: false);
        }
        else
        {
            BindSceneEditStage(resetView: false);
        }
        _libraryVaultPanel.BindProject(_project, () => _frame);
        _hierarchyPanel.RefreshScene();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        SetProjectDirty(true);
        UpdateInspector();
        UpdateStatusBar();
        return new { changed = true, sceneId, project = GetCodexProjectInfo() };
    }

    private object CodexDocumentSetActiveScene(JsonElement arguments)
    {
        var sceneId = CodexDocumentRequiredString(arguments, "sceneId");
        var index = CodexDocumentSceneIndex(sceneId);
        var changed = _activeSceneIndex != index;
        if (!changed)
        {
            return new { changed = false, sceneId, project = GetCodexProjectInfo() };
        }

        CodexDocumentPrepareWrite();
        SelectScene(index);
        return new { changed, sceneId, project = GetCodexProjectInfo() };
    }

    private object CodexDocumentGetSceneInstances()
    {
        var scene = CodexDocumentRequireSceneEditorContext();
        var frame = Math.Max(0, _frame);
        return new
        {
            sceneId = scene.Id,
            frame,
            instances = scene.Instances
                .Select((instance, index) =>
                {
                    var state = instance.EvaluateState(frame);
                    var exposure = scene.Timeline.EvaluateTargetExposure(instance.SceneLayerId, frame);
                    return new
                    {
                        id = instance.Id,
                        index,
                        symbolId = instance.DrawingObjectId,
                        layerId = instance.SceneLayerId,
                        name = instance.Name,
                        frame,
                        hasContent = exposure.HasContent,
                        sourceKeyframe = exposure.SourceKeyframeFrame,
                        exposureEndFrame = exposure.EndFrame,
                        visible = state.Visible,
                        x = state.X,
                        y = state.Y,
                        z = state.Z,
                        rotationX = state.RotationX,
                        rotationY = state.RotationY,
                        rotationZ = state.RotationZ,
                        scaleX = state.ScaleX,
                        scaleY = state.ScaleY,
                        scaleZ = state.ScaleZ
                    };
                })
                .ToArray()
        };
    }

    private object CodexDocumentCreateSceneInstance(JsonElement arguments)
    {
        var scene = CodexDocumentRequireSceneEditorContext();
        var symbolId = CodexDocumentRequiredString(arguments, "symbolId");
        if (_drawingObjects.All(item => !string.Equals(item.Id, symbolId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The symbol was not found.");
        }
        string? layerId = null;
        if (arguments.TryGetProperty("layerId", out var layerElement))
        {
            layerId = CodexDocumentStringValue(layerElement, "layerId");
            if (scene.FindLayer(layerId) is not { Kind: SceneLayerKind.Content })
            {
                throw new ArgumentException("layerId must identify a scene content layer.");
            }
        }
        var x = arguments.TryGetProperty("x", out var xElement)
            ? CodexDocumentNumber(xElement, "x", -5_000_000, 5_000_000)
            : 0;
        var y = arguments.TryGetProperty("y", out var yElement)
            ? CodexDocumentNumber(yElement, "y", -5_000_000, 5_000_000)
            : 0;
        CodexDocumentPrepareWrite();
        var timelineSnapshot = scene.Timeline.CreateSnapshot();
        var instanceSnapshot = scene.CreateInstanceSnapshot();
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        if (!_project.TryAddSceneInstance(
                scene.Id,
                symbolId,
                new PointF(x, y),
                0,
                layerId,
                out var instance)
            || instance is null)
        {
            throw new InvalidOperationException("The scene instance could not be created.");
        }

        PushSceneTimelineUndo(
            scene,
            timelineSnapshot,
            instanceSnapshot: instanceSnapshot,
            playheadFrame: _frame,
            timelineSelection: timelineSelection);
        _timeline.RefreshTimeline();
        SetSceneInstanceSelection(instance);
        RebuildSceneComposition();
        _hierarchyPanel.RefreshScene();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        UpdateInspector();
        _stage.Invalidate();
        return new { changed = true, instanceId = instance.Id, instances = CodexDocumentGetSceneInstances() };
    }

    private object CodexDocumentRemoveSceneInstance(JsonElement arguments)
    {
        var scene = CodexDocumentRequireSceneEditorContext();
        var instanceId = CodexDocumentRequiredString(arguments, "instanceId");
        var instance = scene.Instances.FirstOrDefault(item => string.Equals(item.Id, instanceId, StringComparison.Ordinal))
            ?? throw new ArgumentException("The scene instance was not found.");
        CodexDocumentPrepareWrite();
        var timelineSnapshot = scene.Timeline.CreateSnapshot();
        var instanceSnapshot = scene.CreateInstanceSnapshot();
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        if (!_project.TryRemoveSceneInstance(scene.Id, instance.Id, out _))
        {
            throw new InvalidOperationException("The scene instance could not be removed.");
        }

        PushSceneTimelineUndo(
            scene,
            timelineSnapshot,
            instanceSnapshot: instanceSnapshot,
            playheadFrame: _frame,
            timelineSelection: timelineSelection);
        ClearSelection();
        _timeline.RefreshTimeline();
        RebuildSceneComposition();
        _hierarchyPanel.RefreshScene();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        UpdateInspector();
        _stage.Invalidate();
        return new { changed = true, instanceId, instances = CodexDocumentGetSceneInstances() };
    }

    private void CodexDocumentPrepareWrite()
    {
        StopPlayback();
    }

    private void CodexDocumentRefreshLayers(
        bool sceneContext,
        bool clearSelection = false,
        bool selectModelActive = false)
    {
        _timeline.RefreshTimeline();
        if (clearSelection) ClearSelection();
        if (selectModelActive) _timeline.SelectModelActiveTrack();
        if (sceneContext) RebuildSceneComposition();
        else RebuildDrawingObjectUnderlay();
        _hierarchyPanel.RefreshScene();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        UpdateInspector();
        UpdateStatusBar();
        RefreshToolButtons();
        _stage.Invalidate();
    }

    private DrawingObjectDefinition CodexDocumentActiveDrawingObject()
    {
        return ActiveDrawingObject()
            ?? throw new InvalidOperationException("There is no active symbol.");
    }

    private SceneDefinition CodexDocumentActiveScene()
    {
        return ActiveScene()
            ?? throw new InvalidOperationException("There is no active scene.");
    }

    private SceneDefinition CodexDocumentRequireSceneEditorContext()
    {
        var scene = ActiveScene();
        if (!IsSceneWorkspaceSelected
            || scene is null
            || !IsSceneCompositionContext()
            || !ReferenceEquals(_timeline.Context, scene))
        {
            throw new InvalidOperationException(
                "This operation requires the active Scene Editor composition context.");
        }

        return scene;
    }

    private static bool CodexDocumentHasLockedAncestor(VectorScene scene, int layer)
    {
        var parent = scene.GetLayerParentIndex(layer);
        while (parent >= 0)
        {
            if ((uint)parent < (uint)scene.LayerLocked.Length && scene.LayerLocked[parent]) return true;
            parent = scene.GetLayerParentIndex(parent);
        }

        return false;
    }

    private static void CodexDocumentFindDrawingLayer(
        VectorScene scene,
        string layerId,
        out int index)
    {
        index = Array.IndexOf(scene.LayerIds, layerId);
        if (index < 0) throw new ArgumentException("The drawing layer was not found.");
    }

    private string? CodexDocumentCurrentLayerId()
    {
        if (_workspaceTabs.SelectedView == WorkspaceView.BasicDrawing)
        {
            var scene = CodexDocumentActiveDrawingObject().Scene;
            return (uint)scene.ActiveLayer < (uint)scene.LayerCount ? scene.LayerIds[scene.ActiveLayer] : null;
        }

        return CodexDocumentActiveScene().ActiveLayerId;
    }

    private int CodexDocumentDrawingObjectIndex(string drawingObjectId)
    {
        var index = _drawingObjects
            .Select((drawingObject, index) => (drawingObject, index))
            .FirstOrDefault(item => string.Equals(item.drawingObject.Id, drawingObjectId, StringComparison.Ordinal),
                (null!, -1))
            .index;
        if (index < 0) throw new ArgumentException("The symbol was not found.");
        return index;
    }

    private int CodexDocumentSceneIndex(string sceneId)
    {
        var index = _scenes
            .Select((scene, index) => (scene, index))
            .FirstOrDefault(item => string.Equals(item.scene.Id, sceneId, StringComparison.Ordinal),
                (null!, -1))
            .index;
        if (index < 0) throw new ArgumentException("The scene was not found.");
        return index;
    }

    private static string CodexDocumentRequiredString(JsonElement arguments, string property)
    {
        if (!arguments.TryGetProperty(property, out var value))
        {
            throw new ArgumentException($"Missing argument: {property}.");
        }
        return CodexDocumentStringValue(value, property);
    }

    private static string CodexDocumentStringValue(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Invalid value for {property}.");
        }
        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException($"{property} must not be empty.");
        return text;
    }

    private static string? CodexDocumentOptionalName(JsonElement arguments, string property)
    {
        return arguments.TryGetProperty(property, out var value)
            ? CodexDocumentNormalizeName(value.GetString(), property)
            : null;
    }

    private static string CodexDocumentNormalizeName(string? value, string property)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new ArgumentException($"{property} must not be empty.");
        if (normalized.Length > 80) throw new ArgumentException($"{property} is too long.");
        return normalized;
    }

    private static int CodexDocumentInteger(JsonElement arguments, string property, int minimum, int maximum)
    {
        if (!arguments.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number)
            || number < minimum
            || number > maximum)
        {
            throw new ArgumentException($"Invalid value for {property}.");
        }
        return number;
    }

    private static float CodexDocumentNumber(JsonElement value, string property, double minimum, double maximum)
    {
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number)
            || number < minimum
            || number > maximum)
        {
            throw new ArgumentException($"Invalid value for {property}.");
        }
        return (float)number;
    }
}
