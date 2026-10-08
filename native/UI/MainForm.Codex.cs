using System.Text.Json;
using System.Text.Json.Nodes;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    internal ApplicationSettings CodexApplicationSettings => _applicationSettings;

    internal async Task<object> ExecuteCodexToolAsync(string name, JsonElement arguments, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clonedArguments = arguments.Clone();
        using var registration = cancellation.Register(() => completion.TrySetCanceled(cancellation));
        void Execute()
        {
            // A timed-out request must not be applied later when the UI unblocks.
            if (cancellation.IsCancellationRequested) return;
            try { completion.TrySetResult(ExecuteCodexTool(name, clonedArguments)); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        if (IsDisposed || Disposing || !IsHandleCreated)
            throw new InvalidOperationException("The editor window is unavailable.");
        if (InvokeRequired) BeginInvoke((Action)Execute);
        else Execute();
        return await completion.Task.ConfigureAwait(false);
    }

    internal object ExecuteCodexTool(string name, JsonElement arguments)
    {
        if (InvokeRequired) throw new InvalidOperationException("Editor commands require the UI thread.");
        if (IsDisposed || Disposing || _restartRequested) throw new InvalidOperationException("The editor is restarting or closing.");
        var definition = CodexBridgeProtocol.Tools.FirstOrDefault(t => t.Name == name)
            ?? throw new ArgumentException("Unknown tool.");
        CodexBridgeProtocol.ValidateArguments(arguments, definition.InputSchema);
        if (!_applicationSettings.CodexIntegrationEnabled) throw new InvalidOperationException("MCP integration is disabled.");
        if (!definition.ReadOnly)
        {
            if (!_applicationSettings.CodexIntegrationAllowChanges)
                throw new InvalidOperationException("Write access is disabled. Enable Allow editor changes in Settings > Codex / MCP.");
            if (CodexEditorBusy())
                throw new InvalidOperationException("The editor is busy with an interaction or dialog. Finish it before retrying.");
        }
        if (TryExecuteCodexDocumentTool(name, arguments, out var documentResult)) return documentResult;
        if (TryExecuteCodexSpatialTool(name, arguments, out var spatialResult)) return spatialResult;
        if (TryExecuteCodexAnimationTool(name, arguments, out var animationResult)) return animationResult;
        switch (name)
        {
            case "editor_get_state": return GetCodexEditorState();
            case "project_get_info": return GetCodexProjectInfo();
            case "tools_get": return new
            {
                activeTool = _tool.ToString(),
                tools = Enum.GetValues<ToolMode>().Select(t => new { name = t.ToString(), available = CanActivateTool(t) }).ToArray(),
                drawingSettings = JsonSerializer.SerializeToElement(_drawSettings, CodexBridgeProtocol.JsonOptions)
            };
            case "tool_set_active":
                var toolName = arguments.GetProperty("tool").GetString()!;
                if (!Enum.GetNames<ToolMode>().Contains(toolName)) throw new ArgumentException("Use an exact tool name from tools_get.");
                var tool = Enum.Parse<ToolMode>(toolName);
                if (!CanActivateTool(tool)) throw new InvalidOperationException("This tool is unavailable in the active workspace or layer.");
                StopPlayback();
                ActivateTool(tool);
                return GetCodexEditorState();
            case "timeline_set_frame":
                var frame = arguments.GetProperty("frame").GetInt32();
                var maximum = Math.Min(_playbackSettings.EndFrame, _timeline.Context.FrameCount - 1);
                var minimum = Math.Min(_playbackSettings.StartFrame, maximum);
                if (frame < minimum || frame > maximum) throw new ArgumentException($"Frame must be between {minimum} and {maximum} (zero-based).");
                StopPlayback();
                SetFrame(frame);
                return GetCodexEditorState();
            case "timeline_set_playback":
                var playing = arguments.GetProperty("playing").GetBoolean();
                if (playing != _playing) TogglePlayback();
                return GetCodexEditorState();
            case "editor_undo":
                StopPlayback();
                if (!UndoLastEdit()) throw new InvalidOperationException("There is no edit to undo in the current context.");
                return GetCodexEditorState();
            case "settings_get": return GetCodexSettings();
            case "settings_update":
                var updated = PatchCodexSettings(_applicationSettings, arguments);
                if (!ApplicationSettingsStore.TrySave(updated)) throw new IOException("Settings could not be saved. See the application log.");
                _applicationSettings = updated;
                ApplyThemePreview(updated.ColorTheme, updated.ThemeHueDegrees, updated.ThemeSaturationPercent,
                    updated.ThemeBrightnessPercent, updated.AccentHueDegrees, updated.AccentSaturationPercent,
                    updated.AccentBrightnessPercent);
                UiLocalization.SetLanguage(updated.Language);
                _timeline.FrameWidth = updated.TimelineFrameWidth;
                _timeline.FrameHeightPreset = updated.TimelineFrameHeight;
                _timeline.AutoKeyframeEnabled = updated.TimelineAutoKeyframeEnabled;
                _stage.BackColor = Color.FromArgb(updated.WorkspaceColorArgb);
                _dashDock.SetWorkspaceColor(_stage.BackColor);
                RefreshSceneOpticsText();
                UpdateSceneDimensionButton();
                _stage.Invalidate();
                return GetCodexSettings();
            case "project_open":
                RequireCodexProjectReplacement(arguments);
                var path = CodexProjectPath(arguments.GetProperty("path").GetString()!);
                var project = ProjectVaultStore.Load(path);
                StopPlayback();
                FinishPointerInteractionForFrameChange();
                LoadProjectDocument(project, path);
                return GetCodexProjectInfo();
            case "project_new":
                RequireCodexProjectReplacement(arguments);
                StopPlayback();
                FinishPointerInteractionForFrameChange();
                CreateNewProject();
                return GetCodexProjectInfo();
            case "project_save":
                var destination = arguments.TryGetProperty("path", out var supplied)
                    ? supplied.GetString()! : _projectManifestPath;
                destination = CodexProjectPath(destination);
                StopPlayback();
                FinishPointerInteractionForFrameChange();
                RefreshPendingSceneLightTweenMaterializations();
                // Use the store directly so errors become tool results without
                // opening a modal file-error dialog on an unattended request.
                _projectManifestPath = ProjectVaultStore.Save(_project, destination);
                SetProjectDirty(false);
                CaptureActiveSceneOpticsSaveCheckpoint();
                return GetCodexProjectInfo();
            default: throw new ArgumentException("Unknown tool.");
        }
    }

    private bool CodexEditorBusy() => !Enabled || MouseButtons != MouseButtons.None || HasActiveCanvasPointerInteraction()
        || IsTextEditActive() || _randomFractureDialogObject >= 0 || _sceneOpticsEditSnapshot is not null
        || Application.OpenForms.Cast<Form>().Any(form => form.Modal);

    private object GetCodexEditorState() => new
    {
        processId = Environment.ProcessId,
        workspace = _workspaceTabs.SelectedView.ToString(),
        activeTool = _tool.ToString(), frame = _frame, playing = _playing,
        frameCount = _timeline.Context.FrameCount,
        playbackStartFrame = _playbackSettings.StartFrame, playbackEndFrame = _playbackSettings.EndFrame,
        activeSceneId = ActiveScene()?.Id, activeDrawingObjectId = ActiveDrawingObject()?.Id,
        selectedObjects = _selectedObjects.ToArray(), selectedInstanceIds = _selectedSceneInstanceIds.ToArray(),
        busy = CodexEditorBusy(), writeAccess = _applicationSettings.CodexIntegrationAllowChanges,
        projectDirty = _projectDirty,
        render = new
        {
            frameMilliseconds = _stage.LastFrameRenderMilliseconds,
            direct2D = _stage.LastFrameUsedDirect2D,
            direct2DCommandMilliseconds = _stage.LastDirect2DCommandMilliseconds,
            direct2DPresentMilliseconds = _stage.LastDirect2DPresentMilliseconds,
            cacheMaintenanceMilliseconds = _stage.LastDirect2DCacheMaintenanceMilliseconds,
            reference3DGridMilliseconds = _stage.LastDirect2DReference3DGridMilliseconds,
            reference3DSceneMilliseconds = _stage.LastDirect2DReference3DSceneMilliseconds,
            reference3DPlanLookupMilliseconds = _stage.LastDirect2DReference3DPlanLookupMilliseconds,
            reference3DOverlayMilliseconds = _stage.LastDirect2DReference3DOverlayMilliseconds,
            playbackGdiStatus = _stage.LastDirect2DReference3DPlaybackGdiStatus,
            cpuRasterFallbackReason = _stage.LastDirect2DReference3DCpuRasterFallbackReason,
            gpuAcceleration = _stage.GpuAccelerationActive,
            immediateGpuPresentation = _stage.ImmediateGpuPresentationEnabled
        }
    };

    private object GetCodexProjectInfo() => new
    {
        _project.Id, _project.Name, path = _projectManifestPath, dirty = _projectDirty,
        scenes = _scenes.Select(s => new { s.Id, s.Name, s.Dimension, s.FrameCount }).ToArray(),
        drawingObjects = _drawingObjects.Select(d => new { d.Id, d.Name, d.Kind, d.FrameCount,
            objectCount = d.Scene.ObjectCount, layerCount = d.Scene.LayerCount }).ToArray()
    };

    private object GetCodexSettings() => new
    {
        values = new
        {
            _applicationSettings.Language, _applicationSettings.ColorTheme,
            _applicationSettings.ThemeHueDegrees, _applicationSettings.ThemeSaturationPercent, _applicationSettings.ThemeBrightnessPercent,
            _applicationSettings.AccentHueDegrees, _applicationSettings.AccentSaturationPercent, _applicationSettings.AccentBrightnessPercent,
            _applicationSettings.TimelineFrameWidth, _applicationSettings.TimelineFrameHeight, _applicationSettings.TimelineAutoKeyframeEnabled,
            _applicationSettings.FreeTransformShiftProportionalEnabled,
            _applicationSettings.WorkspaceColorArgb, _applicationSettings.ActiveShortcutProfileId
        },
        writableSchema = CodexBridgeProtocol.SettingsSchema,
        connection = new
        {
            enabled = _applicationSettings.CodexIntegrationEnabled,
            allowChanges = _applicationSettings.CodexIntegrationAllowChanges,
            endpoint = CodexBridgeServer.EndpointFor(_applicationSettings.CodexIntegrationPort),
            authenticationRequired = _applicationSettings.CodexIntegrationAuthToken.Length > 0
        }
    };

    internal static ApplicationSettings PatchCodexSettings(ApplicationSettings settings, JsonElement arguments)
    {
        CodexBridgeProtocol.ValidateArguments(arguments, CodexBridgeProtocol.SettingsSchema);
        var merged = JsonSerializer.SerializeToNode(settings, CodexBridgeProtocol.JsonOptions)!.AsObject();
        foreach (var property in arguments.EnumerateObject())
            merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        return ApplicationSettingsStore.Normalize(merged.Deserialize<ApplicationSettings>(CodexBridgeProtocol.JsonOptions)!);
    }

    private void RequireCodexProjectReplacement(JsonElement arguments)
    {
        if (_projectDirty && (!arguments.TryGetProperty("discardUnsaved", out var discard) || !discard.GetBoolean()))
            throw new InvalidOperationException("The project has unsaved changes. Save it first or explicitly set discardUnsaved to true.");
    }

    private static string CodexProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ProjectVaultStore.ProjectExtension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Provide an absolute .v2dProject manifest path in a dedicated project folder.");
        return Path.GetFullPath(path);
    }
}
