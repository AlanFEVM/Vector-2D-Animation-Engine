using System.Text;
using System.Text.Json;

namespace VectorAnimationEngine;

internal static class EditorRestartStore
{
    private const int FormatVersion = 2;
    private const int TokenSidecarVersion = 1;
    private const long MaxStateBytes = 512L * 1024 * 1024;
    private const long MaxTokenSidecarBytes = 512;
    private static readonly TimeSpan RestartLifetime = TimeSpan.FromMinutes(5);
    internal const string TokenArgumentPrefix = "--editor-restart-token=";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true
    };

    private static string StatePath => Path.Combine(
        Path.GetTempPath(),
        "Vector2DAnimationEngine",
        "editor-restart-state.json");

    private static string TokenSidecarPath => Path.Combine(
        Path.GetTempPath(),
        "Vector2DAnimationEngine",
        "editor-restart-token.json");

    public static bool TrySave(EditorRestartState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            var directory = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(directory);
            RejectReparsePoint(directory);
            DeletePending();
            if (File.Exists(StatePath) || File.Exists(TokenSidecarPath))
            {
                throw new IOException("A previous editor restart handoff could not be removed.");
            }

            var token = Guid.NewGuid().ToString("N");
            var expiresUtc = DateTimeOffset.UtcNow.Add(RestartLifetime);
            var file = new EditorRestartFile
            {
                Version = FormatVersion,
                Token = token,
                ExpiresUtc = expiresUtc,
                Project = state.Project.CreateRestartSnapshot(),
                ProjectManifestPath = state.ProjectManifestPath,
                ProjectDirty = state.ProjectDirty,
                ActiveSceneIndex = state.ActiveSceneIndex,
                ActiveDrawingObjectIndex = state.ActiveDrawingObjectIndex,
                Workspace = state.Workspace,
                Frame = state.Frame,
                SelectedObjects = state.SelectedObjects.ToArray(),
                StageView = RestartStageView.From(state.StageView),
                Window = RestartWindowBounds.From(state.WindowBounds),
                WindowState = state.WindowState
            };
            WriteJsonAtomically(StatePath, file, MaxStateBytes);
            WriteJsonAtomically(TokenSidecarPath, new EditorRestartTokenFile
            {
                Version = TokenSidecarVersion,
                Token = token,
                ExpiresUtc = expiresUtc
            }, MaxTokenSidecarBytes);
            AppLog.Info("Saved the editor restart state for a development-process restart");
            return true;
        }
        catch (Exception ex)
        {
            DeletePending();
            AppLog.Error("Unable to save the editor restart state", ex);
            return false;
        }
    }

    public static bool TryConsume(string? requestedToken, out EditorRestartState? state)
    {
        try
        {
            return TryPrepareConsume(requestedToken, out state);
        }
        finally
        {
            DeletePending();
        }
    }

    public static bool TryPrepareConsume(string? requestedToken, out EditorRestartState? state)
    {
        state = null;
        var prepared = false;

        try
        {
            if (!IsCanonicalToken(requestedToken) || !File.Exists(StatePath)) return false;
            RejectReparsePoint(StatePath);
            ValidateMaximumFileSize(StatePath, MaxStateBytes);
            var file = JsonSerializer.Deserialize<EditorRestartFile>(File.ReadAllText(StatePath), JsonOptions);
            var nowUtc = DateTimeOffset.UtcNow;
            if (file is null
                || file.Version != FormatVersion
                || file.Project is null
                || !string.Equals(file.Token, requestedToken, StringComparison.Ordinal)
                || !IsValidExpiry(file.ExpiresUtc, nowUtc))
            {
                return false;
            }

            state = new EditorRestartState
            {
                Project = VectorProject.RestoreRestartSnapshot(file.Project),
                ProjectManifestPath = file.ProjectManifestPath ?? "",
                ProjectDirty = file.ProjectDirty,
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
            prepared = true;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Unable to restore the editor restart state", ex);
            return false;
        }
        finally
        {
            if (!prepared) DeletePending();
        }
    }

    public static void CompletePreparedConsume(string? requestedToken)
    {
        if (!IsCanonicalToken(requestedToken)) return;
        DeletePending();
        AppLog.Info("Completed the editor restart-state handoff after the main window became ready");
    }

    public static void DeletePending()
    {
        foreach (var path in new[]
                 {
                     StatePath,
                     StatePath + ".tmp",
                     TokenSidecarPath,
                     TokenSidecarPath + ".tmp"
                 })
        {
            try
            {
                RejectReparsePoint(path);
                File.Delete(path);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Unable to delete the pending editor restart handoff '{Path.GetFileName(path)}'", ex);
            }
        }
    }

    internal static string? GetRequestedToken(IReadOnlyList<string> arguments)
    {
        return arguments
            .LastOrDefault(argument => argument.StartsWith(TokenArgumentPrefix, StringComparison.Ordinal))?
            .Substring(TokenArgumentPrefix.Length);
    }

    internal static (string StatePath, string TokenSidecarPath) GetPendingPathsForRegression()
        => (StatePath, TokenSidecarPath);

    internal static bool TryGetPendingTokenForRegression(out string token)
    {
        token = "";
        try
        {
            if (!File.Exists(TokenSidecarPath)) return false;
            RejectReparsePoint(TokenSidecarPath);
            ValidateMaximumFileSize(TokenSidecarPath, MaxTokenSidecarBytes);
            var sidecar = JsonSerializer.Deserialize<EditorRestartTokenFile>(
                File.ReadAllText(TokenSidecarPath),
                JsonOptions);
            if (sidecar is null || !IsCanonicalToken(sidecar.Token)) return false;
            token = sidecar.Token;
            return true;
        }
        catch
        {
            return false;
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
        public string Token { get; init; } = "";
        public DateTimeOffset ExpiresUtc { get; init; }
        public ProjectRestartSnapshot? Project { get; init; }
        public string? ProjectManifestPath { get; init; }
        public bool ProjectDirty { get; init; }
        public int ActiveSceneIndex { get; init; }
        public int ActiveDrawingObjectIndex { get; init; }
        public WorkspaceView Workspace { get; init; }
        public int Frame { get; init; }
        public int[]? SelectedObjects { get; init; }
        public RestartStageView StageView { get; init; } = new();
        public RestartWindowBounds Window { get; init; } = new();
        public FormWindowState WindowState { get; init; }
    }

    private sealed class EditorRestartTokenFile
    {
        public int Version { get; init; }
        public string Token { get; init; } = "";
        public DateTimeOffset ExpiresUtc { get; init; }
    }

    private static bool IsCanonicalToken(string? value)
    {
        return Guid.TryParseExact(value, "N", out var token)
            && string.Equals(token.ToString("N"), value, StringComparison.Ordinal);
    }

    private static bool IsValidExpiry(DateTimeOffset expiresUtc, DateTimeOffset nowUtc)
    {
        return expiresUtc.Offset == TimeSpan.Zero
            && expiresUtc > nowUtc
            && expiresUtc <= nowUtc.Add(RestartLifetime);
    }

    private static void WriteJsonAtomically<T>(string path, T value, long maximumBytes)
    {
        var temporaryPath = path + ".tmp";
        RejectReparsePoint(path);
        RejectReparsePoint(temporaryPath);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        if (bytes.LongLength > maximumBytes)
        {
            throw new InvalidDataException("The editor restart handoff exceeds its supported size.");
        }

        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void ValidateMaximumFileSize(string path, long maximumBytes)
    {
        if (new FileInfo(path).Length > maximumBytes)
        {
            throw new InvalidDataException("The editor restart handoff exceeds its supported size.");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Editor restart handoff paths cannot use filesystem reparse points.");
        }
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
        public WorldGridType WorldGridType { get; init; }

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
            WorldGridOpacity = state.WorldGridOpacity,
            WorldGridType = state.WorldGridType
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
            WorldGridOpacity,
            WorldGridType);
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
