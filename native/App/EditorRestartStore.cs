using System.Text.Json;

namespace VectorAnimationEngine;

internal static class EditorRestartStore
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true
    };

    private static string StatePath => Path.Combine(
        Path.GetTempPath(),
        "Vector2DAnimationEngine",
        "editor-restart-state.json");

    public static bool TrySave(EditorRestartState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            var directory = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(directory);
            var file = new EditorRestartFile
            {
                Version = FormatVersion,
                Project = state.Project.CreateRestartSnapshot(),
                ActiveSceneIndex = state.ActiveSceneIndex,
                ActiveDrawingObjectIndex = state.ActiveDrawingObjectIndex,
                Workspace = state.Workspace,
                Frame = state.Frame,
                SelectedObjects = state.SelectedObjects.ToArray(),
                StageView = RestartStageView.From(state.StageView),
                Window = RestartWindowBounds.From(state.WindowBounds),
                WindowState = state.WindowState
            };
            var temporaryPath = $"{StatePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temporaryPath, StatePath, overwrite: true);
            AppLog.Info("Saved the editor restart state for a development-process restart");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Unable to save the editor restart state", ex);
            return false;
        }
    }

    public static bool TryConsume(out EditorRestartState? state)
    {
        state = null;
        if (!File.Exists(StatePath)) return false;

        try
        {
            var file = JsonSerializer.Deserialize<EditorRestartFile>(File.ReadAllText(StatePath), JsonOptions);
            if (file is null || file.Version != FormatVersion || file.Project is null) return false;

            state = new EditorRestartState
            {
                Project = VectorProject.RestoreRestartSnapshot(file.Project),
                ActiveSceneIndex = file.ActiveSceneIndex,
                ActiveDrawingObjectIndex = file.ActiveDrawingObjectIndex,
                Workspace = file.Workspace,
                Frame = Math.Max(0, file.Frame),
                SelectedObjects = file.SelectedObjects ?? [],
                StageView = file.StageView.ToStageViewState(),
                WindowBounds = file.Window.ToRectangle(),
                WindowState = file.WindowState
            };
            AppLog.Info("Loaded the preserved editor restart state");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Unable to restore the editor restart state", ex);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(StatePath);
            }
            catch
            {
                // The state is one-shot. A later process can safely overwrite a stale file.
            }
        }
    }

    internal static ProjectRestartSnapshot RoundTripProjectSnapshot(ProjectRestartSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        return JsonSerializer.Deserialize<ProjectRestartSnapshot>(json, JsonOptions)
            ?? throw new InvalidOperationException("Project restart snapshot JSON did not deserialize.");
    }

    private sealed class EditorRestartFile
    {
        public int Version { get; init; }
        public ProjectRestartSnapshot? Project { get; init; }
        public int ActiveSceneIndex { get; init; }
        public int ActiveDrawingObjectIndex { get; init; }
        public WorkspaceView Workspace { get; init; }
        public int Frame { get; init; }
        public int[]? SelectedObjects { get; init; }
        public RestartStageView StageView { get; init; } = new();
        public RestartWindowBounds Window { get; init; } = new();
        public FormWindowState WindowState { get; init; }
    }

    private sealed class RestartStageView
    {
        public float CameraX { get; init; }
        public float CameraY { get; init; }
        public float Zoom { get; init; }
        public float ReferenceYaw { get; init; }
        public float ReferencePitch { get; init; }
        public float ReferenceDistance { get; init; }
        public float ReferenceZoomScale { get; init; }
        public float ReferenceTargetX { get; init; }
        public float ReferenceTargetY { get; init; }
        public float ReferenceTargetZ { get; init; }
        public float WorldGridOpacity { get; init; } = 0.1f;

        public static RestartStageView From(StageViewState state) => new()
        {
            CameraX = state.CameraX,
            CameraY = state.CameraY,
            Zoom = state.Zoom,
            ReferenceYaw = state.ReferenceYaw,
            ReferencePitch = state.ReferencePitch,
            ReferenceDistance = state.ReferenceDistance,
            ReferenceZoomScale = state.ReferenceZoomScale,
            ReferenceTargetX = state.ReferenceTargetX,
            ReferenceTargetY = state.ReferenceTargetY,
            ReferenceTargetZ = state.ReferenceTargetZ,
            WorldGridOpacity = state.WorldGridOpacity
        };

        public StageViewState ToStageViewState() => new(
            CameraX,
            CameraY,
            Zoom,
            ReferenceYaw,
            ReferencePitch,
            ReferenceDistance,
            ReferenceZoomScale,
            ReferenceTargetX,
            ReferenceTargetY,
            ReferenceTargetZ,
            WorldGridOpacity);
    }

    private sealed class RestartWindowBounds
    {
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        public static RestartWindowBounds From(Rectangle bounds) => new()
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height
        };

        public Rectangle ToRectangle() => new(X, Y, Math.Max(1, Width), Math.Max(1, Height));
    }
}
