using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VectorAnimationEngine;

internal static class ProjectVaultStore
{
    internal const string ProjectExtension = ".v2dProject";

    private const int FormatVersion = 1;
    private const string VaultDirectoryName = ".Vault";
    private const string TimelineDirectoryName = ".TimeLine";
    private const string DrawingTimelineDirectoryName = "Drawings";
    private const string SceneTimelineDirectoryName = "Scenes";
    private const string SaveJournalFileName = ".v2d-save-journal.json";
    private const int SaveJournalVersion = 1;
    private const string GeneratorSoftware = "Vector 2D Animation Engine";
    private const long MaxManifestBytes = 64L * 1024 * 1024;
    private const long MaxTimelineBytes = 128L * 1024 * 1024;
    private const long MaxSvgAssetBytes = 128L * 1024 * 1024;
    private const int MaxAssetFolders = 100_000;
    private const int MaxAssetTags = VectorProject.MaxAssetTagCount;
    private const int MaxDrawingObjects = 100_000;
    private const int MaxScenes = 100_000;
    private const long MaxProjectBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxSaveJournalBytes = 16L * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        MaxDepth = 256,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static string Save(VectorProject project, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        var fullManifestPath = NormalizeManifestPath(manifestPath);
        var projectRoot = Path.GetDirectoryName(fullManifestPath)!;
        Directory.CreateDirectory(projectRoot);
        RejectReparsePoint(projectRoot);
        RecoverInterruptedSave(projectRoot, fullManifestPath);

        var snapshot = project.CreateRestartSnapshot();
        ValidateRestartSnapshot(snapshot);
        EnsureDedicatedProjectRoot(projectRoot, fullManifestPath, snapshot.Id);
        ValidateManagedTargetShapes(projectRoot, fullManifestPath);

        var operationId = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(projectRoot, $".v2d-save-{operationId}.staging");
        var backupRoot = Path.Combine(projectRoot, $".v2d-save-{operationId}.backup");
        var retainRecoveryFiles = false;
        try
        {
            var stagingVault = Path.Combine(stagingRoot, VaultDirectoryName);
            var stagingDrawingTimelines = Path.Combine(
                stagingRoot,
                TimelineDirectoryName,
                DrawingTimelineDirectoryName);
            var stagingSceneTimelines = Path.Combine(
                stagingRoot,
                TimelineDirectoryName,
                SceneTimelineDirectoryName);
            Directory.CreateDirectory(stagingVault);
            Directory.CreateDirectory(stagingDrawingTimelines);
            Directory.CreateDirectory(stagingSceneTimelines);

            var drawingEntries = new List<DrawingManifestEntry>(snapshot.DrawingObjects.Length);
            foreach (var drawing in snapshot.DrawingObjects)
            {
                var svgRelativePath = DrawingSvgRelativePath(drawing.Id);
                var timelineRelativePath = DrawingTimelineRelativePath(drawing.Id);
                var svgPath = ResolveExpectedRelativePath(stagingRoot, svgRelativePath, svgRelativePath);
                var timelinePath = ResolveExpectedRelativePath(stagingRoot, timelineRelativePath, timelineRelativePath);

                DrawingObjectSvgCodec.Write(svgPath, drawing.Id, drawing.Scene);
                FlushFileToDisk(svgPath);
                WriteJson(timelinePath, DrawingTimelineDocument.From(drawing));
                ValidateMaximumFileSize(svgPath, MaxSvgAssetBytes, "drawing SVG");
                ValidateMaximumFileSize(timelinePath, MaxTimelineBytes, "drawing timeline");
                drawingEntries.Add(new DrawingManifestEntry
                {
                    Id = drawing.Id,
                    Name = drawing.Name,
                    Kind = drawing.Kind,
                    Detail = drawing.Detail,
                    AssetFolderId = drawing.AssetFolderId,
                    AssetTagIds = drawing.AssetTagIds,
                    AnchorX = drawing.AnchorX,
                    AnchorY = drawing.AnchorY,
                    CreatedAt = drawing.CreatedAt,
                    SvgPath = svgRelativePath,
                    SvgSha256 = ComputeSha256(svgPath),
                    TimelinePath = timelineRelativePath,
                    TimelineSha256 = ComputeSha256(timelinePath)
                });
            }

            var sceneEntries = new List<SceneManifestEntry>(snapshot.Scenes.Length);
            foreach (var scene in snapshot.Scenes)
            {
                var timelineRelativePath = SceneTimelineRelativePath(scene.Id);
                var timelinePath = ResolveExpectedRelativePath(stagingRoot, timelineRelativePath, timelineRelativePath);
                WriteJson(timelinePath, SceneTimelineDocument.From(scene));
                ValidateMaximumFileSize(timelinePath, MaxTimelineBytes, "scene timeline");
                sceneEntries.Add(new SceneManifestEntry
                {
                    Id = scene.Id,
                    Name = scene.Name,
                    Detail = scene.Detail,
                    Dimension = scene.Dimension,
                    Camera = scene.Camera,
                    CreatedAt = scene.CreatedAt,
                    TimelinePath = timelineRelativePath,
                    TimelineSha256 = ComputeSha256(timelinePath)
                });
            }

            var manifest = new ProjectManifest
            {
                FormatVersion = FormatVersion,
                Generator = new GeneratorDescriptor
                {
                    Software = GeneratorSoftware,
                    Version = typeof(ProjectVaultStore).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
                    SavedUtc = DateTimeOffset.UtcNow
                },
                Project = new ProjectDescriptor
                {
                    Id = snapshot.Id,
                    Name = snapshot.Name,
                    PlaybackFps = snapshot.PlaybackFps,
                    LoopPlayback = snapshot.LoopPlayback,
                    PlaybackStartFrame = snapshot.PlaybackStartFrame,
                    PlaybackEndFrame = snapshot.PlaybackEndFrame
                },
                AssetTags = snapshot.AssetTags.Select(AssetTagDescriptor.From).ToArray(),
                AssetFolders = snapshot.AssetFolders.Select(AssetFolderDescriptor.From).ToArray(),
                DrawingObjects = drawingEntries.ToArray(),
                Scenes = sceneEntries.ToArray()
            };
            ValidateManifest(manifest, projectRoot);

            var stagingManifest = Path.Combine(stagingRoot, Path.GetFileName(fullManifestPath));
            WriteJson(stagingManifest, manifest);
            ValidateMaximumFileSize(stagingManifest, MaxManifestBytes, "project manifest");
            _ = LoadValidatedProject(stagingManifest);
            CommitManagedFiles(stagingRoot, backupRoot, projectRoot, fullManifestPath, snapshot.Id);
            return fullManifestPath;
        }
        catch (ProjectSaveRecoveryException)
        {
            retainRecoveryFiles = true;
            throw;
        }
        finally
        {
            if (!retainRecoveryFiles)
            {
                DeleteDirectoryBestEffort(stagingRoot);
                DeleteDirectoryBestEffort(backupRoot);
            }
        }
    }

    internal static VectorProject Load(string manifestPath)
    {
        var fullManifestPath = NormalizeManifestPath(manifestPath);
        var projectRoot = Path.GetDirectoryName(fullManifestPath)!;
        if (Directory.Exists(projectRoot))
        {
            RejectReparsePoint(projectRoot);
            RecoverInterruptedSave(projectRoot, fullManifestPath);
        }
        return LoadValidatedProject(fullManifestPath);
    }

    private static VectorProject LoadValidatedProject(string fullManifestPath)
    {
        var projectRoot = Path.GetDirectoryName(fullManifestPath)!;
        if (!File.Exists(fullManifestPath))
        {
            throw new FileNotFoundException("The project manifest does not exist.", fullManifestPath);
        }

        RejectReparsePoint(projectRoot);
        RejectReparsePoint(fullManifestPath);
        var manifest = ReadJson<ProjectManifest>(fullManifestPath, MaxManifestBytes);
        ValidateManifest(manifest, projectRoot);
        var projectBytes = new FileInfo(fullManifestPath).Length;

        var drawings = new DrawingObjectRestartSnapshot[manifest.DrawingObjects.Length];
        for (var index = 0; index < manifest.DrawingObjects.Length; index++)
        {
            var entry = manifest.DrawingObjects[index];
            var expectedSvgPath = DrawingSvgRelativePath(entry.Id);
            var expectedTimelinePath = DrawingTimelineRelativePath(entry.Id);
            var svgPath = ResolveExpectedRelativePath(projectRoot, entry.SvgPath, expectedSvgPath);
            var timelinePath = ResolveExpectedRelativePath(projectRoot, entry.TimelinePath, expectedTimelinePath);
            projectBytes = AddProjectBytes(
                projectBytes,
                ValidateFile(svgPath, entry.SvgSha256, MaxSvgAssetBytes, "drawing SVG"));
            projectBytes = AddProjectBytes(
                projectBytes,
                ValidateFile(timelinePath, entry.TimelineSha256, MaxTimelineBytes, "drawing timeline"));

            var sceneSnapshot = DrawingObjectSvgCodec.Read(svgPath, entry.Id);
            var timeline = ReadJson<DrawingTimelineDocument>(timelinePath, MaxTimelineBytes);
            ValidateDrawingTimeline(timeline, sceneSnapshot, entry.Id, manifest.DrawingObjects);
            drawings[index] = new DrawingObjectRestartSnapshot
            {
                Id = entry.Id,
                Name = entry.Name,
                Kind = entry.Kind,
                Detail = entry.Detail,
                AssetFolderId = entry.AssetFolderId,
                AssetTagIds = entry.AssetTagIds,
                AnchorX = entry.AnchorX,
                AnchorY = entry.AnchorY,
                CreatedAt = entry.CreatedAt,
                Scene = CopyWithTimeline(sceneSnapshot, timeline.Timeline),
                Instances = timeline.Instances
            };
        }

        var scenes = new SceneRestartSnapshot[manifest.Scenes.Length];
        for (var index = 0; index < manifest.Scenes.Length; index++)
        {
            var entry = manifest.Scenes[index];
            var expectedTimelinePath = SceneTimelineRelativePath(entry.Id);
            var timelinePath = ResolveExpectedRelativePath(projectRoot, entry.TimelinePath, expectedTimelinePath);
            projectBytes = AddProjectBytes(
                projectBytes,
                ValidateFile(timelinePath, entry.TimelineSha256, MaxTimelineBytes, "scene timeline"));
            var timeline = ReadJson<SceneTimelineDocument>(timelinePath, MaxTimelineBytes);
            ValidateSceneTimeline(timeline, entry.Id, manifest.DrawingObjects);
            scenes[index] = new SceneRestartSnapshot
            {
                Id = entry.Id,
                Name = entry.Name,
                Detail = entry.Detail,
                Dimension = entry.Dimension,
                Camera = entry.Camera,
                CreatedAt = entry.CreatedAt,
                Layers = timeline.Layers,
                Instances = timeline.Instances,
                Timeline = timeline.Timeline
            };
        }

        var snapshot = new ProjectRestartSnapshot
        {
            Id = manifest.Project.Id,
            Name = manifest.Project.Name,
            PlaybackFps = manifest.Project.PlaybackFps,
            LoopPlayback = manifest.Project.LoopPlayback,
            PlaybackStartFrame = manifest.Project.PlaybackStartFrame,
            PlaybackEndFrame = manifest.Project.PlaybackEndFrame,
            AssetTags = manifest.AssetTags.Select(item => item.ToSnapshot()).ToArray(),
            AssetFolders = manifest.AssetFolders.Select(item => item.ToSnapshot()).ToArray(),
            DrawingObjects = drawings,
            Scenes = scenes
        };
        ValidateRestartSnapshot(snapshot);
        return VectorProject.RestoreRestartSnapshot(snapshot);
    }

    internal static (string StagingRoot, string BackupRoot, string JournalPath) GetSaveRecoveryPathsForRegression(
        string manifestPath,
        string operationId)
    {
        var fullManifestPath = NormalizeManifestPath(manifestPath);
        if (!IsCanonicalOperationId(operationId))
        {
            throw new ArgumentException("A canonical N-format operation ID is required.", nameof(operationId));
        }
        var projectRoot = Path.GetDirectoryName(fullManifestPath)!;
        return (
            Path.Combine(projectRoot, $".v2d-save-{operationId}.staging"),
            Path.Combine(projectRoot, $".v2d-save-{operationId}.backup"),
            Path.Combine(projectRoot, SaveJournalFileName));
    }

    internal static void WriteSaveJournalForRegression(
        string manifestPath,
        string operationId,
        string projectId,
        bool hadManagedProject,
        bool installed)
    {
        var fullManifestPath = NormalizeManifestPath(manifestPath);
        var projectRoot = Path.GetDirectoryName(fullManifestPath)!;
        RejectReparsePoint(projectRoot);
        var journal = new SaveJournal
        {
            Version = SaveJournalVersion,
            OperationId = operationId,
            ManifestFileName = Path.GetFileName(fullManifestPath),
            ProjectId = projectId,
            HadManagedProject = hadManagedProject,
            State = installed ? SaveJournalState.Installed : SaveJournalState.Prepared
        };
        ValidateSaveJournal(journal, fullManifestPath);
        WriteSaveJournal(projectRoot, journal);
    }

    private static string NormalizeManifestPath(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("A project manifest path is required.", nameof(manifestPath));
        }

        var fullPath = Path.GetFullPath(manifestPath);
        if (!string.Equals(Path.GetExtension(fullPath), ProjectExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Project manifests must use the {ProjectExtension} extension.", nameof(manifestPath));
        }
        return fullPath;
    }

    private static void EnsureDedicatedProjectRoot(string projectRoot, string manifestPath, string projectId)
    {
        var existingManifests = Directory.EnumerateFiles(projectRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(
                Path.GetExtension(path),
                ProjectExtension,
                StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .ToArray();
        foreach (var existingManifest in existingManifests)
        {
            if (!string.Equals(existingManifest, manifestPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The selected directory already contains a different Vector 2D project manifest.");
            }
        }

        if (!File.Exists(manifestPath))
        {
            if (Directory.Exists(Path.Combine(projectRoot, VaultDirectoryName))
                || Directory.Exists(Path.Combine(projectRoot, TimelineDirectoryName)))
            {
                throw new InvalidOperationException(
                    "The selected directory contains unmanaged .Vault or .TimeLine data. Choose a new project directory.");
            }
            return;
        }

        RejectReparsePoint(manifestPath);
        var existing = ReadJson<ProjectManifest>(manifestPath, MaxManifestBytes);
        ValidateManifest(existing, projectRoot);
        if (!string.Equals(existing.Project.Id, projectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected project manifest belongs to a different project. Choose a new project directory.");
        }
    }

    private static void ValidateManagedTargetShapes(string projectRoot, string manifestPath)
    {
        foreach (var directory in new[]
                 {
                     Path.Combine(projectRoot, VaultDirectoryName),
                     Path.Combine(projectRoot, TimelineDirectoryName)
                 })
        {
            if (File.Exists(directory))
            {
                throw new IOException($"A file blocks the managed project directory '{Path.GetFileName(directory)}'.");
            }
            RejectReparsePoint(directory);
        }
        if (Directory.Exists(manifestPath))
        {
            throw new IOException("A directory blocks the project manifest path.");
        }
        RejectReparsePoint(manifestPath);
    }

    private static void CommitManagedFiles(
        string stagingRoot,
        string backupRoot,
        string projectRoot,
        string manifestPath,
        string projectId)
    {
        var stagingVault = Path.Combine(stagingRoot, VaultDirectoryName);
        var stagingTimeline = Path.Combine(stagingRoot, TimelineDirectoryName);
        var stagingManifest = Path.Combine(stagingRoot, Path.GetFileName(manifestPath));
        var destinationVault = Path.Combine(projectRoot, VaultDirectoryName);
        var destinationTimeline = Path.Combine(projectRoot, TimelineDirectoryName);
        var backupVault = Path.Combine(backupRoot, VaultDirectoryName);
        var backupTimeline = Path.Combine(backupRoot, TimelineDirectoryName);
        var backupManifest = Path.Combine(backupRoot, Path.GetFileName(manifestPath));

        var operationId = Path.GetFileName(stagingRoot)
            .Replace(".v2d-save-", "", StringComparison.Ordinal)
            .Replace(".staging", "", StringComparison.Ordinal);
        if (!IsCanonicalOperationId(operationId))
        {
            throw new InvalidOperationException("The project save operation identifier is invalid.");
        }

        var hadManagedProject = File.Exists(manifestPath);
        if (hadManagedProject != Directory.Exists(destinationVault)
            || hadManagedProject != Directory.Exists(destinationTimeline))
        {
            throw new InvalidOperationException("The managed project files are incomplete before save commit.");
        }

        Directory.CreateDirectory(backupRoot);
        var journal = new SaveJournal
        {
            Version = SaveJournalVersion,
            OperationId = operationId,
            ManifestFileName = Path.GetFileName(manifestPath),
            ProjectId = projectId,
            HadManagedProject = hadManagedProject,
            State = SaveJournalState.Prepared
        };
        WriteSaveJournal(projectRoot, journal);
        try
        {
            if (Directory.Exists(destinationVault)) Directory.Move(destinationVault, backupVault);
            if (Directory.Exists(destinationTimeline)) Directory.Move(destinationTimeline, backupTimeline);
            if (File.Exists(manifestPath)) File.Move(manifestPath, backupManifest);

            Directory.Move(stagingVault, destinationVault);
            Directory.Move(stagingTimeline, destinationTimeline);
            File.Move(stagingManifest, manifestPath);
            WriteSaveJournal(projectRoot, journal with { State = SaveJournalState.Installed });
            DeleteDirectoryBestEffort(stagingRoot);
            DeleteDirectoryBestEffort(backupRoot);
            if (!Directory.Exists(stagingRoot) && !Directory.Exists(backupRoot))
            {
                try
                {
                    DeleteSaveJournal(projectRoot);
                }
                catch
                {
                    // Installed data is complete. The next Save/Load entry retries journal cleanup.
                }
            }
        }
        catch (Exception commitException)
        {
            try
            {
                RecoverInterruptedSave(projectRoot, manifestPath);
            }
            catch (Exception rollbackException)
            {
                throw new ProjectSaveRecoveryException(
                    $"The project save failed and the previous managed files could not be fully restored. "
                    + $"Recovery files were retained at '{stagingRoot}' and '{backupRoot}'.",
                    new AggregateException(commitException, rollbackException));
            }
            throw;
        }
    }

    private static void RecoverInterruptedSave(string projectRoot, string manifestPath)
    {
        var journalPath = Path.Combine(projectRoot, SaveJournalFileName);
        if (!File.Exists(journalPath)) return;

        RejectReparsePoint(journalPath);
        var journal = ReadJson<SaveJournal>(journalPath, MaxSaveJournalBytes);
        ValidateSaveJournal(journal, manifestPath);
        AppLog.Warn($"Recovering interrupted project save in {journal.State} state.");

        var stagingRoot = Path.Combine(projectRoot, $".v2d-save-{journal.OperationId}.staging");
        var backupRoot = Path.Combine(projectRoot, $".v2d-save-{journal.OperationId}.backup");
        var destinationVault = Path.Combine(projectRoot, VaultDirectoryName);
        var destinationTimeline = Path.Combine(projectRoot, TimelineDirectoryName);
        var backupVault = Path.Combine(backupRoot, VaultDirectoryName);
        var backupTimeline = Path.Combine(backupRoot, TimelineDirectoryName);
        var backupManifest = Path.Combine(backupRoot, journal.ManifestFileName);
        var stagingManifest = Path.Combine(stagingRoot, journal.ManifestFileName);

        RejectReparsePoint(stagingRoot);
        RejectReparsePoint(backupRoot);
        ValidateManagedTargetShapes(projectRoot, manifestPath);
        if (journal.State == SaveJournalState.Prepared)
        {
            ValidateJournalProjectOwnership(
                journal,
                File.Exists(backupManifest)
                    ? backupManifest
                    : File.Exists(manifestPath)
                        ? manifestPath
                        : stagingManifest);
        }

        if (journal.State == SaveJournalState.Prepared)
        {
            if (journal.HadManagedProject)
            {
                RestoreDirectoryFromBackup(backupVault, destinationVault);
                RestoreDirectoryFromBackup(backupTimeline, destinationTimeline);
                RestoreFileFromBackup(backupManifest, manifestPath);
                if (!Directory.Exists(destinationVault)
                    || !Directory.Exists(destinationTimeline)
                    || !File.Exists(manifestPath))
                {
                    throw new IOException("The interrupted project save could not restore the previous managed files.");
                }
                _ = LoadValidatedProject(manifestPath);
            }
            else
            {
                DeleteFileStrict(manifestPath);
                DeleteDirectoryStrict(destinationTimeline);
                DeleteDirectoryStrict(destinationVault);
            }
            AppLog.Info("Rolled back the prepared project save to its previous managed file set.");
        }
        else
        {
            try
            {
                if (!Directory.Exists(destinationVault)
                    || !Directory.Exists(destinationTimeline)
                    || !File.Exists(manifestPath))
                {
                    throw new IOException("The installed project save journal does not have a complete managed file set.");
                }
                ValidateJournalProjectOwnership(journal, manifestPath);
                _ = LoadValidatedProject(manifestPath);
                AppLog.Info("Preserved the complete installed project save while cleaning recovery files.");
            }
            catch (Exception installedException) when (journal.HadManagedProject)
            {
                try
                {
                    ValidateJournalProjectOwnership(journal, backupManifest);
                    _ = LoadValidatedProject(backupManifest);
                    RestoreDirectoryFromBackup(backupVault, destinationVault);
                    RestoreDirectoryFromBackup(backupTimeline, destinationTimeline);
                    RestoreFileFromBackup(backupManifest, manifestPath);
                    _ = LoadValidatedProject(manifestPath);
                    AppLog.Warn("Rolled back an incomplete installed project save to its validated backup.");
                }
                catch (Exception backupException)
                {
                    throw new ProjectSaveRecoveryException(
                        "The installed project save and its previous backup both failed validation.",
                        new AggregateException(installedException, backupException));
                }
            }
        }

        DeleteDirectoryStrict(stagingRoot);
        DeleteDirectoryStrict(backupRoot);
        DeleteSaveJournal(projectRoot);
        AppLog.Info("Completed interrupted project save recovery cleanup.");
    }

    private static void ValidateSaveJournal(SaveJournal journal, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var manifestFileName = Path.GetFileName(manifestPath);
        if (journal.Version != SaveJournalVersion
            || !IsCanonicalOperationId(journal.OperationId)
            || !IsSafeStableId(journal.ProjectId)
            || string.IsNullOrWhiteSpace(journal.ManifestFileName)
            || !string.Equals(journal.ManifestFileName, manifestFileName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(journal.ManifestFileName), journal.ManifestFileName, StringComparison.Ordinal)
            || journal.State is not SaveJournalState.Prepared and not SaveJournalState.Installed)
        {
            throw new InvalidDataException("The interrupted project save journal is invalid.");
        }
    }

    private static void ValidateJournalProjectOwnership(SaveJournal journal, string candidateManifestPath)
    {
        if (!File.Exists(candidateManifestPath))
        {
            throw new IOException("The interrupted project save has no manifest available for ownership validation.");
        }
        RejectReparsePoint(candidateManifestPath);
        var manifest = ReadJson<ProjectManifest>(candidateManifestPath, MaxManifestBytes);
        if (manifest.Project is null
            || !string.Equals(manifest.Project.Id, journal.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The interrupted project save journal belongs to a different project.");
        }
    }

    private static bool IsCanonicalOperationId(string value)
    {
        return Guid.TryParseExact(value, "N", out var operationId)
            && string.Equals(operationId.ToString("N"), value, StringComparison.Ordinal);
    }

    private static void WriteSaveJournal(string projectRoot, SaveJournal journal)
    {
        var journalPath = Path.Combine(projectRoot, SaveJournalFileName);
        var temporaryPath = journalPath + ".tmp";
        RejectReparsePoint(journalPath);
        RejectReparsePoint(temporaryPath);
        var bytes = Utf8WithoutBom.GetBytes(JsonSerializer.Serialize(journal, JsonOptions));
        if (bytes.LongLength > MaxSaveJournalBytes)
        {
            throw new InvalidDataException("The project save journal exceeds its supported size.");
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
        File.Move(temporaryPath, journalPath, overwrite: true);
    }

    private static void DeleteSaveJournal(string projectRoot)
    {
        DeleteFileStrict(Path.Combine(projectRoot, SaveJournalFileName + ".tmp"));
        DeleteFileStrict(Path.Combine(projectRoot, SaveJournalFileName));
    }

    private static void RestoreDirectoryFromBackup(string backupPath, string destinationPath)
    {
        RejectReparsePoint(backupPath);
        RejectReparsePoint(destinationPath);
        if (!Directory.Exists(backupPath)) return;
        DeleteDirectoryStrict(destinationPath);
        Directory.Move(backupPath, destinationPath);
    }

    private static void RestoreFileFromBackup(string backupPath, string destinationPath)
    {
        RejectReparsePoint(backupPath);
        RejectReparsePoint(destinationPath);
        if (!File.Exists(backupPath)) return;
        DeleteFileStrict(destinationPath);
        File.Move(backupPath, destinationPath);
    }

    private static void DeleteDirectoryStrict(string path)
    {
        if (!Directory.Exists(path)) return;
        RejectReparsePoint(path);
        Directory.Delete(path, recursive: true);
    }

    private static void DeleteFileStrict(string path)
    {
        if (!File.Exists(path)) return;
        RejectReparsePoint(path);
        File.Delete(path);
    }

    private static void ValidateManifest(ProjectManifest manifest, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException(
                manifest.FormatVersion > FormatVersion
                    ? $"Project format {manifest.FormatVersion} is newer than the supported format {FormatVersion}."
                    : $"Project format {manifest.FormatVersion} is not supported.");
        }
        if (manifest.Generator is null
            || string.IsNullOrWhiteSpace(manifest.Generator.Software)
            || string.IsNullOrWhiteSpace(manifest.Generator.Version)
            || manifest.Generator.SavedUtc == default
            || manifest.Generator.SavedUtc.Offset != TimeSpan.Zero
            || manifest.Project is null
            || string.IsNullOrWhiteSpace(manifest.Project.Id)
            || string.IsNullOrWhiteSpace(manifest.Project.Name))
        {
            throw new InvalidDataException("The project manifest metadata is incomplete.");
        }
        if (manifest.DrawingObjects is null || manifest.DrawingObjects.Length == 0
            || manifest.Scenes is null || manifest.Scenes.Length == 0
            || manifest.AssetFolders is null
            || manifest.AssetTags is null)
        {
            throw new InvalidDataException("The project manifest has no valid project roots.");
        }
        if (manifest.AssetFolders.Length > MaxAssetFolders
            || manifest.AssetTags.Length > MaxAssetTags
            || manifest.DrawingObjects.Length > MaxDrawingObjects
            || manifest.Scenes.Length > MaxScenes)
        {
            throw new InvalidDataException("The project manifest exceeds the supported root count.");
        }
        if (manifest.Project.PlaybackFps is < 1m or > 120m
            || manifest.Project.PlaybackStartFrame < 0
            || manifest.Project.PlaybackEndFrame < manifest.Project.PlaybackStartFrame)
        {
            throw new InvalidDataException("The project playback settings are invalid.");
        }

        ValidateUniqueIds(manifest.AssetFolders.Select(item => item?.Id), "asset folder");
        ValidateUniqueIds(manifest.AssetTags.Select(item => item?.Id), "asset tag");
        ValidateUniqueIds(manifest.DrawingObjects.Select(item => item?.Id), "drawing object");
        ValidateUniqueIds(manifest.Scenes.Select(item => item?.Id), "scene");
        ValidateAssetFolders(manifest.AssetFolders);
        ValidateAssetTags(manifest.AssetTags);

        var folderIds = manifest.AssetFolders.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var assetTagIds = manifest.AssetTags.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drawing in manifest.DrawingObjects)
        {
            if (drawing is null || !IsSafeStableId(drawing.Id) || string.IsNullOrWhiteSpace(drawing.Name)
                || drawing.Kind is null || drawing.Detail is null || drawing.AssetFolderId is null
                || drawing.AssetTagIds is null
                || drawing.AssetTagIds.Distinct(StringComparer.Ordinal).Count() != drawing.AssetTagIds.Length
                || drawing.AssetTagIds.Any(tagId => !assetTagIds.Contains(tagId)))
            {
                throw new InvalidDataException("A drawing-object descriptor is invalid.");
            }
            if (drawing.AssetFolderId.Length > 0 && !folderIds.Contains(drawing.AssetFolderId))
            {
                throw new InvalidDataException($"Drawing object '{drawing.Id}' references a missing asset folder.");
            }
            if (!float.IsFinite(drawing.AnchorX) || !float.IsFinite(drawing.AnchorY))
            {
                throw new InvalidDataException($"Drawing object '{drawing.Id}' has an invalid anchor.");
            }
            ValidateManifestFile(projectRoot, drawing.SvgPath, DrawingSvgRelativePath(drawing.Id), drawing.SvgSha256, paths);
            ValidateManifestFile(
                projectRoot,
                drawing.TimelinePath,
                DrawingTimelineRelativePath(drawing.Id),
                drawing.TimelineSha256,
                paths);
        }
        foreach (var scene in manifest.Scenes)
        {
            if (scene is null || !IsSafeStableId(scene.Id) || string.IsNullOrWhiteSpace(scene.Name) || scene.Detail is null
                || scene.Camera is null || !Enum.IsDefined(scene.Dimension) || !IsValidCamera(scene.Camera))
            {
                throw new InvalidDataException("A scene descriptor is invalid.");
            }
            ValidateManifestFile(
                projectRoot,
                scene.TimelinePath,
                SceneTimelineRelativePath(scene.Id),
                scene.TimelineSha256,
                paths);
        }
    }

    private static void ValidateManifestFile(
        string projectRoot,
        string relativePath,
        string expectedPath,
        string checksum,
        ISet<string> paths)
    {
        var resolved = ResolveExpectedRelativePath(projectRoot, relativePath, expectedPath);
        if (!paths.Add(resolved)) throw new InvalidDataException("The project manifest contains duplicate file references.");
        if (!IsSha256(checksum)) throw new InvalidDataException($"The checksum for '{relativePath}' is invalid.");
    }

    private static void ValidateAssetFolders(IReadOnlyList<AssetFolderDescriptor> folders)
    {
        var byId = folders.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (!IsSafeStableId(folder.Id) || string.IsNullOrWhiteSpace(folder.Name) || folder.ParentFolderId is null)
            {
                throw new InvalidDataException("An asset-folder descriptor is invalid.");
            }
            if (folder.ParentFolderId.Length > 0 && !byId.ContainsKey(folder.ParentFolderId))
            {
                throw new InvalidDataException($"Asset folder '{folder.Id}' references a missing parent.");
            }
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = folder;
            while (current.ParentFolderId.Length > 0)
            {
                if (!visited.Add(current.Id)) throw new InvalidDataException("The asset-folder hierarchy contains a cycle.");
                current = byId[current.ParentFolderId];
            }
        }
    }

    private static void ValidateAssetTags(IReadOnlyList<AssetTagDescriptor> tags)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            if (tag is null
                || !IsSafeStableId(tag.Id)
                || string.IsNullOrWhiteSpace(tag.Name)
                || tag.Name.Length > VectorProject.MaxAssetTagNameLength
                || !names.Add(tag.Name.Trim())
                || Color.FromArgb(tag.ColorArgb).A != 255)
            {
                throw new InvalidDataException("An asset-tag descriptor is invalid.");
            }
        }
    }

    private static void ValidateRestartSnapshot(ProjectRestartSnapshot snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Id) || string.IsNullOrWhiteSpace(snapshot.Name)
            || snapshot.AssetFolders is null || snapshot.AssetTags is null
            || snapshot.DrawingObjects is null || snapshot.DrawingObjects.Length == 0
            || snapshot.Scenes is null || snapshot.Scenes.Length == 0)
        {
            throw new InvalidDataException("The project snapshot is incomplete.");
        }
        if (snapshot.AssetFolders.Length > MaxAssetFolders
            || snapshot.AssetTags.Length > MaxAssetTags
            || snapshot.DrawingObjects.Length > MaxDrawingObjects
            || snapshot.Scenes.Length > MaxScenes)
        {
            throw new InvalidDataException("The project snapshot exceeds the supported root count.");
        }
        if (snapshot.PlaybackFps is < 1m or > 120m
            || snapshot.PlaybackStartFrame < 0
            || snapshot.PlaybackEndFrame < snapshot.PlaybackStartFrame)
        {
            throw new InvalidDataException("The project snapshot playback settings are invalid.");
        }
        ValidateUniqueIds(snapshot.AssetFolders.Select(item => item?.Id), "asset folder");
        ValidateUniqueIds(snapshot.AssetTags.Select(item => item?.Id), "asset tag");
        ValidateUniqueIds(snapshot.DrawingObjects.Select(item => item?.Id), "drawing object");
        ValidateUniqueIds(snapshot.Scenes.Select(item => item?.Id), "scene");

        ValidateAssetTags(snapshot.AssetTags.Select(AssetTagDescriptor.From).ToArray());
        var assetTagIds = snapshot.AssetTags.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var drawingIds = snapshot.DrawingObjects.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var graph = snapshot.DrawingObjects.ToDictionary(
            item => item.Id,
            item => item.Instances.Select(instance => instance.DrawingObjectId).ToArray(),
            StringComparer.Ordinal);
        foreach (var drawing in snapshot.DrawingObjects)
        {
            if (!IsSafeStableId(drawing.Id)
                || drawing.Scene is null
                || drawing.Instances is null
                || drawing.AssetTagIds is null
                || drawing.AssetTagIds.Distinct(StringComparer.Ordinal).Count() != drawing.AssetTagIds.Length
                || drawing.AssetTagIds.Any(tagId => !assetTagIds.Contains(tagId)))
            {
                throw new InvalidDataException("A drawing-object snapshot is invalid.");
            }
            ValidateInstances(drawing.Instances, drawingIds, $"drawing object '{drawing.Id}'");
        }
        foreach (var scene in snapshot.Scenes)
        {
            if (!IsSafeStableId(scene.Id) || scene.Layers is null || scene.Timeline is null || scene.Instances is null)
            {
                throw new InvalidDataException("A scene snapshot is invalid.");
            }
            ValidateInstances(scene.Instances, drawingIds, $"scene '{scene.Id}'");
        }
        ValidateAcyclicDrawingGraph(graph);
    }

    private static void ValidateDrawingTimeline(
        DrawingTimelineDocument document,
        VectorSceneSnapshot scene,
        string expectedDrawingId,
        IReadOnlyList<DrawingManifestEntry> drawings)
    {
        if (document is null || document.FormatVersion != FormatVersion
            || !string.Equals(document.DrawingObjectId, expectedDrawingId, StringComparison.Ordinal)
            || document.Layers is null || document.Timeline is null || document.Instances is null
            || document.ObjectLayer is null || document.ObjectKeyframeFrame is null)
        {
            throw new InvalidDataException($"The timeline for drawing object '{expectedDrawingId}' is invalid.");
        }
        ValidateUniqueIds(document.Layers.Select(item => item?.Id), "drawing layer");
        if (document.Layers.Any(item => item is null))
        {
            throw new InvalidDataException($"Drawing object '{expectedDrawingId}' has an invalid layer descriptor.");
        }
        if (document.Layers.Any(item => !Enum.IsDefined(item.BlendMode)))
        {
            throw new InvalidDataException($"Drawing object '{expectedDrawingId}' has an invalid layer blend mode.");
        }
        if (scene.LayerCount != document.Layers.Length
            || scene.ObjectCount != document.ObjectLayer.Length
            || scene.ObjectCount != document.ObjectKeyframeFrame.Length
            || scene.ActiveLayer != document.ActiveLayer
            || (scene.OnionSkinEnabled ?? scene.LayerOnionSkin.Any(enabled => enabled))
                != (document.OnionSkinEnabled ?? document.Layers.Any(layer => layer.OnionSkin))
            || scene.OnionSkinPreviousFrames != document.OnionSkinPreviousFrames
            || scene.OnionSkinNextFrames != document.OnionSkinNextFrames)
        {
            throw new InvalidDataException($"Drawing object '{expectedDrawingId}' has mismatched SVG and timeline state.");
        }

        for (var index = 0; index < document.Layers.Length; index++)
        {
            if (!document.Layers[index].Matches(scene, index))
            {
                throw new InvalidDataException($"Drawing object '{expectedDrawingId}' has mismatched layer metadata.");
            }
        }
        if (!scene.ObjectLayer.SequenceEqual(document.ObjectLayer)
            || !scene.ObjectKeyframeFrame.SequenceEqual(document.ObjectKeyframeFrame))
        {
            throw new InvalidDataException($"Drawing object '{expectedDrawingId}' has mismatched cel ownership.");
        }

        var drawingIds = drawings.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        ValidateInstances(document.Instances, drawingIds, $"drawing object '{expectedDrawingId}'");
        var layerIds = document.Layers.Select(item => item.Id).ToArray();
        ValidateTimeline(document.Timeline, layerIds);
        ValidateInstanceLayerReferences(document.Instances, layerIds, expectedDrawingId);
        ValidateCelOwnership(document, expectedDrawingId);
    }

    private static void ValidateSceneTimeline(
        SceneTimelineDocument document,
        string expectedSceneId,
        IReadOnlyList<DrawingManifestEntry> drawings)
    {
        if (document is null || document.FormatVersion != FormatVersion
            || !string.Equals(document.SceneId, expectedSceneId, StringComparison.Ordinal)
            || document.Layers is null || document.Layers.Layers is null
            || document.Layers.InstanceLayerIds is null || document.Timeline is null || document.Instances is null)
        {
            throw new InvalidDataException($"The timeline for scene '{expectedSceneId}' is invalid.");
        }
        var layers = document.Layers.Layers;
        ValidateUniqueIds(layers.Select(item => item?.Id), "scene layer");
        if (layers.Any(item => item is null))
        {
            throw new InvalidDataException($"Scene '{expectedSceneId}' has an invalid layer descriptor.");
        }
        var layerIds = layers.Select(item => item.Id).ToArray();
        if (layers.Any(item => item.Name is null || !Enum.IsDefined(item.BlendMode)))
        {
            throw new InvalidDataException($"Scene '{expectedSceneId}' has invalid layer metadata.");
        }
        if (!layerIds.Contains(document.Layers.ActiveLayerId, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Scene '{expectedSceneId}' has an invalid active layer.");
        }
        var drawingIds = drawings.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        ValidateInstances(document.Instances, drawingIds, $"scene '{expectedSceneId}'");
        ValidateInstanceLayerReferences(document.Instances, layerIds, expectedSceneId);
        if (document.Layers.InstanceLayerIds.Count != document.Instances.Length
            || document.Instances.Any(instance =>
                !string.Equals(
                    document.Layers.InstanceLayerIds.GetValueOrDefault(instance.Id),
                    instance.SceneLayerId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Scene '{expectedSceneId}' has mismatched instance-layer metadata.");
        }
        ValidateTimeline(document.Timeline, layerIds);
    }

    private static void ValidateInstances(
        IReadOnlyList<InstanceRestartSnapshot> instances,
        ISet<string> drawingIds,
        string owner)
    {
        ValidateUniqueIds(instances.Select(item => item?.Id), $"instance in {owner}");
        foreach (var instance in instances)
        {
            if (instance is null || !drawingIds.Contains(instance.DrawingObjectId) || string.IsNullOrWhiteSpace(instance.SceneLayerId)
                || instance.Name is null
                || !IsValidState(instance.Visible, instance.X, instance.Y, instance.Z, instance.RotationX, instance.RotationY,
                    instance.RotationZ, instance.SkewX, instance.SkewY, instance.ScaleX, instance.ScaleY, instance.ScaleZ,
                    instance.Alpha, instance.TintArgb,
                    instance.PlaybackFps, instance.PlaybackMode, instance.HoldFrame))
            {
                throw new InvalidDataException($"An instance in {owner} is invalid.");
            }
            var frames = new HashSet<int>();
            foreach (var keyframe in instance.StateKeyframes ?? [])
            {
                var state = keyframe.State;
                if (keyframe.Frame < 0 || !frames.Add(keyframe.Frame)
                    || !IsValidState(state.Visible, state.X, state.Y, state.Z, state.RotationX, state.RotationY,
                        state.RotationZ, state.SkewX, state.SkewY, state.ScaleX, state.ScaleY, state.ScaleZ,
                        state.Alpha, state.TintArgb,
                        state.PlaybackFps, state.PlaybackMode, state.HoldFrame))
                {
                    throw new InvalidDataException($"An instance state keyframe in {owner} is invalid.");
                }
            }
            frames.Clear();
            foreach (var keyframe in instance.PositionKeyframes ?? [])
            {
                if (keyframe.Frame < 0 || !frames.Add(keyframe.Frame)
                    || !float.IsFinite(keyframe.X) || !float.IsFinite(keyframe.Y))
                {
                    throw new InvalidDataException($"A legacy instance position keyframe in {owner} is invalid.");
                }
            }
        }
    }

    private static void ValidateInstanceLayerReferences(
        IReadOnlyList<InstanceRestartSnapshot> instances,
        IReadOnlyCollection<string> layerIds,
        string ownerId)
    {
        var validLayers = layerIds.ToHashSet(StringComparer.Ordinal);
        if (instances.Any(instance => !validLayers.Contains(instance.SceneLayerId)))
        {
            throw new InvalidDataException($"Container '{ownerId}' has an instance assigned to a missing layer.");
        }
        var instanceIds = instances.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (instanceIds.Overlaps(validLayers))
        {
            throw new InvalidDataException($"Container '{ownerId}' has colliding layer and instance IDs.");
        }
    }

    private static void ValidateTimeline(AnimationTimelineSnapshot timeline, IEnumerable<string> targetIds)
    {
        if (timeline.Tracks is null) throw new InvalidDataException("Timeline tracks are missing.");
        var expectedTargets = targetIds.ToHashSet(StringComparer.Ordinal);
        ValidateUniqueIds(timeline.Tracks.Select(item => item?.Id), "timeline track");
        var actualTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in timeline.Tracks)
        {
            if (track is null || string.IsNullOrWhiteSpace(track.TargetId) || !actualTargets.Add(track.TargetId)
                || track.Duration < 1 || track.Keyframes is null || track.Keyframes.Length == 0
                || track.Keyframes[0].Frame != 0)
            {
                throw new InvalidDataException("A timeline track is invalid.");
            }
            var previousFrame = -1;
            foreach (var keyframe in track.Keyframes)
            {
                if (keyframe.Frame <= previousFrame || keyframe.Frame < 0 || keyframe.Frame >= track.Duration
                    || !Enum.IsDefined(keyframe.Kind))
                {
                    throw new InvalidDataException("A timeline keyframe is invalid.");
                }
                previousFrame = keyframe.Frame;
            }

            var populatedFrames = track.Keyframes
                .Where(keyframe => keyframe.HasContent)
                .Select(keyframe => keyframe.Frame)
                .ToHashSet();
            var previousTweenEnd = -1;
            foreach (var tween in track.Tweens ?? [])
            {
                if (!tween.IsValid
                    || tween.EndFrame >= track.Duration
                    || !populatedFrames.Contains(tween.StartFrame)
                    || !populatedFrames.Contains(tween.EndFrame)
                    || tween.StartFrame < previousTweenEnd)
                {
                    throw new InvalidDataException("A timeline tween is invalid.");
                }

                previousTweenEnd = tween.EndFrame;
            }
        }
        if (!actualTargets.SetEquals(expectedTargets))
        {
            throw new InvalidDataException("Timeline targets do not match their owning layers and instances.");
        }
    }

    private static void ValidateCelOwnership(DrawingTimelineDocument document, string drawingObjectId)
    {
        var tracks = document.Timeline.Tracks.ToDictionary(track => track.TargetId, StringComparer.Ordinal);
        for (var objectIndex = 0; objectIndex < document.ObjectKeyframeFrame.Length; objectIndex++)
        {
            var layerIndex = document.ObjectLayer[objectIndex];
            if (layerIndex >= document.Layers.Length)
            {
                throw new InvalidDataException($"Drawing object '{drawingObjectId}' has an invalid object layer.");
            }
            var frame = document.ObjectKeyframeFrame[objectIndex];
            var track = tracks[document.Layers[layerIndex].Id];
            if (!track.Keyframes.Any(keyframe =>
                    keyframe.Frame == frame && keyframe.Kind == TimelineKeyframeKind.Populated))
            {
                throw new InvalidDataException(
                    $"Drawing object '{drawingObjectId}' has an object whose Cel is not owned by a populated keyframe.");
            }
        }
    }

    private static void ValidateAcyclicDrawingGraph(IReadOnlyDictionary<string, string[]> graph)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in graph.Keys)
        {
            Visit(id);
        }
        return;

        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!active.Add(id)) throw new InvalidDataException("The drawing-object graph contains a cycle.");
            foreach (var childId in graph[id]) Visit(childId);
            active.Remove(id);
            visited.Add(id);
        }
    }

    private static bool IsValidState(
        bool visible,
        float x,
        float y,
        float z,
        float rotationX,
        float rotationY,
        float rotationZ,
        float skewX,
        float skewY,
        float scaleX,
        float scaleY,
        float scaleZ,
        float alpha,
        int tintArgb,
        decimal playbackFps,
        DrawingObjectPlaybackMode playbackMode,
        int holdFrame)
    {
        _ = visible;
        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
            && float.IsFinite(rotationX) && float.IsFinite(rotationY) && float.IsFinite(rotationZ)
            && float.IsFinite(skewX) && float.IsFinite(skewY)
            && float.IsFinite(scaleX) && float.IsFinite(scaleY) && float.IsFinite(scaleZ)
            && float.IsFinite(alpha) && alpha is >= 0f and <= 1f
            && (uint)tintArgb >> 24 == 0xff
            && playbackFps is >= 1m and <= 120m && Enum.IsDefined(playbackMode) && holdFrame >= 0;
    }

    private static bool IsValidCamera(SceneCameraDefinition camera)
    {
        return !string.IsNullOrWhiteSpace(camera.Name) && Enum.IsDefined(camera.Projection)
            && float.IsFinite(camera.X) && float.IsFinite(camera.Y) && float.IsFinite(camera.Z)
            && float.IsFinite(camera.Depth) && float.IsFinite(camera.OrthographicSize)
            && float.IsFinite(camera.FieldOfViewDegrees);
    }

    private static void ValidateUniqueIds(IEnumerable<string?> ids, string kind)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || !unique.Add(id))
            {
                throw new InvalidDataException($"The project contains a blank or duplicate {kind} ID.");
            }
        }
    }

    private static bool IsSafeStableId(string id)
    {
        return !string.IsNullOrWhiteSpace(id)
            && id is not "." and not ".."
            && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !id.Contains(Path.DirectorySeparatorChar)
            && !id.Contains(Path.AltDirectorySeparatorChar);
    }

    private static string DrawingSvgRelativePath(string id) => $"{VaultDirectoryName}/{id}.svg";

    private static string DrawingTimelineRelativePath(string id) =>
        $"{TimelineDirectoryName}/{DrawingTimelineDirectoryName}/{id}.json";

    private static string SceneTimelineRelativePath(string id) =>
        $"{TimelineDirectoryName}/{SceneTimelineDirectoryName}/{id}.json";

    private static string ResolveExpectedRelativePath(string root, string relativePath, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("A project file path must be relative to the manifest directory.");
        }
        var segments = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException("A project file path contains traversal or empty segments.");
        }
        var normalized = string.Join(Path.DirectorySeparatorChar, segments);
        var expected = expectedPath.Replace('/', Path.DirectorySeparatorChar);
        if (!string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Project file '{relativePath}' does not use its stable-ID path.");
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        var rootPrefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A project file path escapes the manifest directory.");
        }
        RejectManagedPathReparsePoints(fullRoot, fullPath);
        return fullPath;
    }

    private static void RejectManagedPathReparsePoints(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Project manifests and managed assets cannot use filesystem reparse points.");
        }
    }

    private static long ValidateFile(
        string path,
        string expectedChecksum,
        long maximumBytes,
        string kind)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("A project asset is missing.", path);
        ValidateMaximumFileSize(path, maximumBytes, kind);
        var actual = ComputeSha256(path);
        if (!string.Equals(actual, expectedChecksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The checksum for project asset '{Path.GetFileName(path)}' does not match.");
        }
        return new FileInfo(path).Length;
    }

    private static long AddProjectBytes(long currentBytes, long fileBytes)
    {
        if (fileBytes < 0 || currentBytes > MaxProjectBytes - fileBytes)
        {
            throw new InvalidDataException("The project exceeds the supported aggregate file size limit.");
        }
        return currentBytes + fileBytes;
    }

    private static void ValidateMaximumFileSize(string path, long maximumBytes, string kind)
    {
        var length = new FileInfo(path).Length;
        if (length > maximumBytes)
        {
            throw new InvalidDataException($"The {kind} exceeds the supported per-file size limit.");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsSha256(string value)
    {
        return value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }

    private static void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var bytes = Utf8WithoutBom.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void FlushFileToDisk(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static T ReadJson<T>(string path, long maximumBytes) where T : class
    {
        try
        {
            ValidateMaximumFileSize(path, maximumBytes, "project JSON file");
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
                ?? throw new InvalidDataException($"Project file '{Path.GetFileName(path)}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Project file '{Path.GetFileName(path)}' contains invalid JSON.", exception);
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A later save uses a unique directory and can proceed independently.
        }
    }

    private static VectorSceneSnapshot CopyWithTimeline(
        VectorSceneSnapshot source,
        AnimationTimelineSnapshot timeline)
    {
        return new VectorSceneSnapshot
        {
            LayerCount = source.LayerCount,
            ObjectCount = source.ObjectCount,
            VirtualAtomCount = source.VirtualAtomCount,
            NextObjectOrder = source.NextObjectOrder,
            ActiveLayer = source.ActiveLayer,
            MaxHalfExtent = source.MaxHalfExtent,
            LayerIds = source.LayerIds,
            LayerNames = source.LayerNames,
            LayerKinds = source.LayerKinds,
            LayerParentIds = source.LayerParentIds,
            LayerMaskIds = source.LayerMaskIds,
            LayerLocked = source.LayerLocked,
            LayerVisible = source.LayerVisible,
            LayerOpacity = source.LayerOpacity,
            LayerBlendModes = source.LayerBlendModes,
            LayerColorArgb = source.LayerColorArgb,
            LayerOutline = source.LayerOutline,
            OnionSkinEnabled = source.OnionSkinEnabled,
            LayerOnionSkin = source.LayerOnionSkin,
            OnionSkinPreviousFrames = source.OnionSkinPreviousFrames,
            OnionSkinNextFrames = source.OnionSkinNextFrames,
            LayerStart = source.LayerStart,
            LayerEnd = source.LayerEnd,
            ObjectLayer = source.ObjectLayer,
            ObjectKeyframeFrame = source.ObjectKeyframeFrame,
            ObjectOrder = source.ObjectOrder,
            ObjectSubOrder = source.ObjectSubOrder,
            X = source.X,
            Y = source.Y,
            Width = source.Width,
            Height = source.Height,
            Angle = source.Angle,
            Stroke = source.Stroke,
            CurveControlX = source.CurveControlX,
            CurveControlY = source.CurveControlY,
            CurveControl2X = source.CurveControl2X,
            CurveControl2Y = source.CurveControl2Y,
            LineEndpointStyles = source.LineEndpointStyles,
            LineEndEndpointStyles = source.LineEndEndpointStyles,
            ShapeKind = source.ShapeKind,
            ShapeVertexCounts = source.ShapeVertexCounts,
            AtomCount = source.AtomCount,
            Argb = source.Argb,
            StrokeArgb = source.StrokeArgb,
            FillAutoMergeProtected = source.FillAutoMergeProtected,
            LinearGradientEnabled = source.LinearGradientEnabled,
            GradientKinds = source.GradientKinds,
            GradientStartArgb = source.GradientStartArgb,
            GradientEndArgb = source.GradientEndArgb,
            GradientStartX = source.GradientStartX,
            GradientStartY = source.GradientStartY,
            GradientEndX = source.GradientEndX,
            GradientEndY = source.GradientEndY,
            GradientStops = source.GradientStops,
            GradientPathLocalPoints = source.GradientPathLocalPoints,
            ShapeGradientMappingLocalContours = source.ShapeGradientMappingLocalContours,
            Timeline = timeline,
            PathLocalContours = source.PathLocalContours,
            PathBezierLocalContours = source.PathBezierLocalContours,
            FreehandLocalPoints = source.FreehandLocalPoints,
            FreehandBezierLocalNodes = source.FreehandBezierLocalNodes,
            MixingStrokeLocalSamples = source.MixingStrokeLocalSamples,
            MixingStrokeLocalRegions = source.MixingStrokeLocalRegions,
            ImportedSvgSources = source.ImportedSvgSources,
            TextObjects = source.TextObjects
        };
    }

    private sealed class ProjectManifest
    {
        public int FormatVersion { get; init; }
        public GeneratorDescriptor Generator { get; init; } = new();
        public ProjectDescriptor Project { get; init; } = new();
        public AssetTagDescriptor[] AssetTags { get; init; } = [];
        public AssetFolderDescriptor[] AssetFolders { get; init; } = [];
        public DrawingManifestEntry[] DrawingObjects { get; init; } = [];
        public SceneManifestEntry[] Scenes { get; init; } = [];
    }

    private sealed class GeneratorDescriptor
    {
        public string Software { get; init; } = "";
        public string Version { get; init; } = "";
        public DateTimeOffset SavedUtc { get; init; }
    }

    private sealed class ProjectDescriptor
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public decimal PlaybackFps { get; init; } = 30m;
        public bool LoopPlayback { get; init; } = true;
        public int PlaybackStartFrame { get; init; }
        public int PlaybackEndFrame { get; init; } = 239;
    }

    private enum SaveJournalState
    {
        Prepared,
        Installed
    }

    private sealed record SaveJournal
    {
        public int Version { get; init; }
        public string OperationId { get; init; } = "";
        public string ManifestFileName { get; init; } = "";
        public string ProjectId { get; init; } = "";
        public bool HadManagedProject { get; init; }
        public SaveJournalState State { get; init; }
    }

    private sealed class ProjectSaveRecoveryException : IOException
    {
        public ProjectSaveRecoveryException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed class AssetFolderDescriptor
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "Folder";
        public string ParentFolderId { get; init; } = "";
        public DateTime CreatedAt { get; init; }

        public static AssetFolderDescriptor From(ProjectAssetFolderRestartSnapshot snapshot) => new()
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            ParentFolderId = snapshot.ParentFolderId,
            CreatedAt = snapshot.CreatedAt
        };

        public ProjectAssetFolderRestartSnapshot ToSnapshot() => new()
        {
            Id = Id,
            Name = Name,
            ParentFolderId = ParentFolderId,
            CreatedAt = CreatedAt
        };
    }

    private sealed class AssetTagDescriptor
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "Tag";
        public int ColorArgb { get; init; } = Color.FromArgb(66, 165, 245).ToArgb();

        public static AssetTagDescriptor From(ProjectAssetTagRestartSnapshot snapshot) => new()
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            ColorArgb = snapshot.ColorArgb
        };

        public ProjectAssetTagRestartSnapshot ToSnapshot() => new()
        {
            Id = Id,
            Name = Name,
            ColorArgb = ColorArgb
        };
    }

    private sealed class DrawingManifestEntry
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "Drawing Object";
        public string Kind { get; init; } = "Symbol";
        public string Detail { get; init; } = "";
        public string AssetFolderId { get; init; } = "";
        public string[] AssetTagIds { get; init; } = [];
        public float AnchorX { get; init; }
        public float AnchorY { get; init; }
        public DateTime CreatedAt { get; init; }
        public string SvgPath { get; init; } = "";
        public string SvgSha256 { get; init; } = "";
        public string TimelinePath { get; init; } = "";
        public string TimelineSha256 { get; init; } = "";
    }

    private sealed class SceneManifestEntry
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "Scene";
        public string Detail { get; init; } = "";
        public SceneDimension Dimension { get; init; }
        public SceneCameraDefinition Camera { get; init; } = new();
        public DateTime CreatedAt { get; init; }
        public string TimelinePath { get; init; } = "";
        public string TimelineSha256 { get; init; } = "";
    }

    private sealed class DrawingTimelineDocument
    {
        public int FormatVersion { get; init; }
        public string DrawingObjectId { get; init; } = "";
        public DrawingLayerDescriptor[] Layers { get; init; } = [];
        public int ActiveLayer { get; init; }
        public bool? OnionSkinEnabled { get; init; }
        public int? OnionSkinPreviousFrames { get; init; }
        public int? OnionSkinNextFrames { get; init; }
        public ushort[] ObjectLayer { get; init; } = [];
        public int[] ObjectKeyframeFrame { get; init; } = [];
        public AnimationTimelineSnapshot Timeline { get; init; } = new();
        public InstanceRestartSnapshot[] Instances { get; init; } = [];

        public static DrawingTimelineDocument From(DrawingObjectRestartSnapshot drawing) => new()
        {
            FormatVersion = ProjectVaultStore.FormatVersion,
            DrawingObjectId = drawing.Id,
            Layers = Enumerable.Range(0, drawing.Scene.LayerCount)
                .Select(index => DrawingLayerDescriptor.From(drawing.Scene, index))
                .ToArray(),
            ActiveLayer = drawing.Scene.ActiveLayer,
            OnionSkinEnabled = drawing.Scene.OnionSkinEnabled,
            OnionSkinPreviousFrames = drawing.Scene.OnionSkinPreviousFrames,
            OnionSkinNextFrames = drawing.Scene.OnionSkinNextFrames,
            ObjectLayer = drawing.Scene.ObjectLayer,
            ObjectKeyframeFrame = drawing.Scene.ObjectKeyframeFrame,
            Timeline = drawing.Scene.Timeline ?? new AnimationTimelineSnapshot(),
            Instances = drawing.Instances
        };
    }

    private sealed class DrawingLayerDescriptor
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public DrawingLayerKind Kind { get; init; }
        public string ParentLayerId { get; init; } = "";
        public string MaskLayerId { get; init; } = "";
        public bool Locked { get; init; }
        public bool Visible { get; init; }
        public float Opacity { get; init; }
        public LayerBlendMode BlendMode { get; init; } = LayerBlendMode.Normal;
        public int ColorArgb { get; init; }
        public bool Outline { get; init; }
        public bool OnionSkin { get; init; }
        public int StartFrame { get; init; }
        public int EndFrame { get; init; }

        public static DrawingLayerDescriptor From(VectorSceneSnapshot scene, int index) => new()
        {
            Id = scene.LayerIds[index],
            Name = scene.LayerNames[index],
            Kind = scene.LayerKinds[index],
            ParentLayerId = scene.LayerParentIds[index],
            MaskLayerId = scene.LayerMaskIds[index],
            Locked = scene.LayerLocked[index],
            Visible = scene.LayerVisible[index],
            Opacity = scene.LayerOpacity[index],
            BlendMode = index < scene.LayerBlendModes.Length
                ? scene.LayerBlendModes[index]
                : LayerBlendMode.Normal,
            ColorArgb = scene.LayerColorArgb[index],
            Outline = scene.LayerOutline[index],
            OnionSkin = scene.LayerOnionSkin[index],
            StartFrame = scene.LayerStart[index],
            EndFrame = scene.LayerEnd[index]
        };

        public bool Matches(VectorSceneSnapshot scene, int index)
        {
            return string.Equals(Id, scene.LayerIds[index], StringComparison.Ordinal)
                && string.Equals(Name, scene.LayerNames[index], StringComparison.Ordinal)
                && Kind == scene.LayerKinds[index]
                && string.Equals(ParentLayerId, scene.LayerParentIds[index], StringComparison.Ordinal)
                && string.Equals(MaskLayerId, scene.LayerMaskIds[index], StringComparison.Ordinal)
                && Locked == scene.LayerLocked[index]
                && Visible == scene.LayerVisible[index]
                && Opacity.Equals(scene.LayerOpacity[index])
                && BlendMode == (index < scene.LayerBlendModes.Length
                    ? scene.LayerBlendModes[index]
                    : LayerBlendMode.Normal)
                && ColorArgb == scene.LayerColorArgb[index]
                && Outline == (index < scene.LayerOutline.Length && scene.LayerOutline[index])
                && OnionSkin == scene.LayerOnionSkin[index]
                && StartFrame == scene.LayerStart[index]
                && EndFrame == scene.LayerEnd[index];
        }
    }

    private sealed class SceneTimelineDocument
    {
        public int FormatVersion { get; init; }
        public string SceneId { get; init; } = "";
        public SceneLayerSnapshot Layers { get; init; } = new();
        public AnimationTimelineSnapshot Timeline { get; init; } = new();
        public InstanceRestartSnapshot[] Instances { get; init; } = [];

        public static SceneTimelineDocument From(SceneRestartSnapshot scene) => new()
        {
            FormatVersion = ProjectVaultStore.FormatVersion,
            SceneId = scene.Id,
            Layers = scene.Layers,
            Timeline = scene.Timeline,
            Instances = scene.Instances
        };
    }
}
