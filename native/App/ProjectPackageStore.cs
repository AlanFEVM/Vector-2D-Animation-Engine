using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace VectorAnimationEngine;

/// <summary>
/// 单文件工程容器（.v2d）：把传统文件夹工程（.v2dProject 清单 + .Vault + .TimeLine）
/// 整体打包进一个 ZIP 容器，用户看到的工程只有一个文件，不再散开。
///
/// 编辑期间容器会解包到 %LOCALAPPDATA%\Vector2DAnimationEngine\ProjectWorkspaces 下的
/// 持久托管工作区（按容器文件完整路径取哈希），保存时先由 ProjectVaultStore 以既有
/// staging/backup/journal 原子语义提交到工作区，再重新打包原子覆盖容器文件。
/// 容器内的相对布局与文件夹工程完全一致，因此校验和、恢复与旧格式兼容性保持不变。
/// </summary>
internal static class ProjectPackageStore
{
    internal const string PackageExtension = ".v2d";
    internal const string PackageManifestFileName = "Project.v2dProject";

    private const string VaultDirectoryName = ".Vault";
    private const string TimelineDirectoryName = ".TimeLine";
    private const long MaxPackageBytes = 8L * 1024 * 1024 * 1024;
    private const int WorkspaceTokenLength = 16;

    private static readonly TimeSpan WorkspaceRetention = TimeSpan.FromDays(14);
    private static readonly UTF8Encoding EntryNameEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private static string WorkspaceRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vector2DAnimationEngine",
        "ProjectWorkspaces");

    /// <summary>
    /// 将工程保存进单文件容器。返回托管工作区内的清单完整路径（运行期编辑继续使用该路径）。
    /// </summary>
    internal static string SavePackage(VectorProject project, string packagePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        var fullPackagePath = NormalizePackagePath(packagePath);

        var workspace = GetWorkspaceDirectory(fullPackagePath);
        var pendingWorkspace = workspace + ".new";
        var retiredWorkspace = workspace + ".old";
        DeleteDirectoryBestEffort(pendingWorkspace);
        DeleteDirectoryBestEffort(retiredWorkspace);
        Directory.CreateDirectory(pendingWorkspace);
        RejectReparsePoint(pendingWorkspace);

        var originalImageBindings = project.ImageAssets
            .Select(asset => (Asset: asset, asset.SourcePath, asset.ProjectRelativePath))
            .ToArray();
        var originalSvgBindings = project.ExternalSvgAssets
            .Select(asset => (Asset: asset, asset.SourcePath, asset.ProjectRelativePath, asset.LastKnownSha256))
            .ToArray();
        var workspaceInstalled = false;
        try
        {
            // 先把工程原子提交到全新的待交换工作区；现有工作区保持不动，资产 SourcePath
            // 仍可从中读取。保存失败时当前工作区与容器都不受影响。
            var pendingManifest = Path.Combine(pendingWorkspace, PackageManifestFileName);
            ProjectVaultStore.Save(project, pendingManifest);

            var finalImageBindings = project.ImageAssets
                .Select(asset => (Asset: asset, SourcePath: Path.Combine(
                    workspace, asset.ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar))))
                .ToArray();
            var finalSvgBindings = project.ExternalSvgAssets
                .Select(asset => (Asset: asset, SourcePath: Path.Combine(
                    workspace, asset.ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar))))
                .ToArray();
            var manifestPath = Path.Combine(workspace, PackageManifestFileName);

            // 打包待交换工作区，再原子替换容器文件。
            WritePackageAtomically(fullPackagePath, pendingWorkspace);

            // 交换工作区：旧目录改名让位，新目录就位，随后尽力删除旧目录。
            if (Directory.Exists(workspace))
            {
                Directory.Move(workspace, retiredWorkspace);
            }

            try
            {
                Directory.Move(pendingWorkspace, workspace);
            }
            catch
            {
                if (Directory.Exists(retiredWorkspace) && !Directory.Exists(workspace))
                {
                    try { Directory.Move(retiredWorkspace, workspace); }
                    catch { /* 保留两个目录供下次恢复，不掩盖原始异常 */ }
                }
                throw;
            }

            workspaceInstalled = true;
            foreach (var binding in finalImageBindings) binding.Asset.SourcePath = binding.SourcePath;
            foreach (var binding in finalSvgBindings) binding.Asset.SourcePath = binding.SourcePath;
            DeleteDirectoryBestEffort(retiredWorkspace);
            TouchWorkspace(workspace);
            return manifestPath;
        }
        catch
        {
            if (!workspaceInstalled)
            {
                foreach (var binding in originalImageBindings)
                {
                    binding.Asset.SourcePath = binding.SourcePath;
                    binding.Asset.ProjectRelativePath = binding.ProjectRelativePath;
                }
                foreach (var binding in originalSvgBindings)
                {
                    binding.Asset.SourcePath = binding.SourcePath;
                    binding.Asset.ProjectRelativePath = binding.ProjectRelativePath;
                    binding.Asset.LastKnownSha256 = binding.LastKnownSha256;
                }
            }
            DeleteDirectoryBestEffort(pendingWorkspace);
            throw;
        }
    }

    /// <summary>
    /// 打开单文件容器：解包到托管工作区后交给 ProjectVaultStore.Load 校验加载。
    /// </summary>
    internal static (VectorProject Project, string ManifestPath) OpenPackage(string packagePath)
    {
        var fullPackagePath = NormalizePackagePath(packagePath);
        if (!File.Exists(fullPackagePath))
        {
            throw new FileNotFoundException("The project package does not exist.", fullPackagePath);
        }

        RejectReparsePoint(fullPackagePath);
        ValidateMaximumFileSize(fullPackagePath, MaxPackageBytes, "project package");

        var workspace = GetWorkspaceDirectory(fullPackagePath);
        ExtractPackage(fullPackagePath, workspace);

        var manifestCandidates = Directory.EnumerateFiles(workspace, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(
                Path.GetExtension(path),
                ProjectVaultStore.ProjectExtension,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (manifestCandidates.Length != 1)
        {
            throw new InvalidDataException(
                $"A project package must contain exactly one {ProjectVaultStore.ProjectExtension} manifest.");
        }

        var manifestPath = Path.GetFullPath(manifestCandidates[0]);
        var project = ProjectVaultStore.Load(manifestPath);
        TouchWorkspace(workspace);
        return (project, manifestPath);
    }

    internal static bool IsPackagePath(string? path)
    {
        return !string.IsNullOrEmpty(path)
            && string.Equals(
                Path.GetExtension(path),
                PackageExtension,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 清理过期托管工作区与异常遗留的交换目录。仅在启动时尽力执行，任何失败都不应影响应用启动。
    /// </summary>
    internal static void CleanupStaleWorkspaces(DateTimeOffset? nowUtc = null)
    {
        var cutoffUtc = (nowUtc ?? DateTimeOffset.UtcNow).UtcDateTime - WorkspaceRetention;
        try
        {
            if (!Directory.Exists(WorkspaceRoot)) return;
            foreach (var directory in Directory.EnumerateDirectories(WorkspaceRoot))
            {
                var name = Path.GetFileName(directory);
                var isWorkspace = name.Length == WorkspaceTokenLength
                    && name.All(IsHexadecimal);
                var isLeftover = (name.EndsWith(".new", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                    && IsWorkspaceTokenPrefix(name);
                if (!isWorkspace && !isLeftover) continue;
                try
                {
                    var lastWrite = Directory.GetLastWriteTimeUtc(directory);
                    if (lastWrite < cutoffUtc) DeleteDirectoryBestEffort(directory);
                }
                catch
                {
                    // 单个工作区清理失败不影响其他工作区。
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Warn("Project workspace cleanup did not complete: " + exception.Message);
        }
    }

    private static void WritePackageAtomically(string fullPackagePath, string workspace)
    {
        var packageDirectory = Path.GetDirectoryName(fullPackagePath)!;
        Directory.CreateDirectory(packageDirectory);
        RejectReparsePoint(packageDirectory);

        var temporaryPackagePath = fullPackagePath + ".v2d-tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPackagePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var manifestPath = Path.Combine(workspace, PackageManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    throw new FileNotFoundException(
                        "The committed project manifest is missing from the package workspace.",
                        manifestPath);
                }

                AddPackageEntry(archive, manifestPath, PackageManifestFileName);
                AddManagedDirectory(archive, workspace, VaultDirectoryName);
                AddManagedDirectory(archive, workspace, TimelineDirectoryName);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPackagePath, fullPackagePath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporaryPackagePath);
            throw;
        }
    }

    private static void AddManagedDirectory(ZipArchive archive, string workspace, string directoryName)
    {
        var managedRoot = Path.Combine(workspace, directoryName);
        if (!Directory.Exists(managedRoot)) return;
        foreach (var file in Directory.EnumerateFiles(managedRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(workspace, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            AddPackageEntry(archive, file, relativePath);
        }
    }

    private static void AddPackageEntry(ZipArchive archive, string sourcePath, string entryName)
    {
        ValidatePackageRelativePath(entryName);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var fileStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        fileStream.CopyTo(entryStream);
    }

    private static void ExtractPackage(string fullPackagePath, string workspace)
    {
        // 重新解包前清掉旧内容（同一容器再次打开或上次解包不完整）。该目录由本类按
        // 容器路径哈希独占管理，不会触碰用户选择的其他位置。
        DeleteDirectoryBestEffort(workspace);
        Directory.CreateDirectory(workspace);
        RejectReparsePoint(workspace);

        var workspaceFull = Path.GetFullPath(workspace)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(fullPackagePath);
        var sawManifest = false;
        var extractedTotalBytes = 0L;
        foreach (var entry in archive.Entries)
        {
            var entryName = NormalizeEntryName(entry.FullName);
            if (entryName.Length == 0) continue;
            ValidatePackageRelativePath(entryName);
            var isManifest = !entryName.Contains('/')
                && string.Equals(
                    Path.GetExtension(entryName),
                    ProjectVaultStore.ProjectExtension,
                    StringComparison.OrdinalIgnoreCase);
            var isManagedFile = entryName.StartsWith(VaultDirectoryName + "/", StringComparison.Ordinal)
                || entryName.StartsWith(TimelineDirectoryName + "/", StringComparison.Ordinal);
            if (!isManifest && !isManagedFile)
            {
                throw new InvalidDataException(
                    $"The project package contains an unsupported entry '{entryName}'.");
            }

            if (entry.Length < 0 || entry.Length > MaxPackageBytes)
            {
                throw new InvalidDataException(
                    $"The project package entry '{entryName}' has an invalid size.");
            }

            extractedTotalBytes += entry.Length;
            if (extractedTotalBytes > MaxPackageBytes)
            {
                throw new InvalidDataException(
                    "The project package exceeds the maximum supported uncompressed size.");
            }

            if (isManifest)
            {
                if (sawManifest)
                {
                    throw new InvalidDataException(
                        "The project package contains more than one project manifest.");
                }
                sawManifest = true;
            }

            var targetPath = Path.GetFullPath(Path.Combine(workspace, ToRelativeFileSystemPath(entryName)));
            if (!targetPath.StartsWith(workspaceFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The project package entry '{entryName}' escapes the workspace directory.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }

        if (!sawManifest)
        {
            throw new InvalidDataException(
                $"The project package is missing a {ProjectVaultStore.ProjectExtension} manifest.");
        }
    }

    private static void ValidatePackageRelativePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath.StartsWith('/'))
        {
            throw new InvalidDataException("Project package entries must use relative paths.");
        }

        var segments = relativePath.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.IndexOfAny(Path.GetInvalidPathChars()) >= 0
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.Any(char.IsControl))
            {
                throw new InvalidDataException(
                    $"The project package contains an invalid path segment in '{relativePath}'.");
            }
        }
    }

    private static string NormalizeEntryName(string fullName)
    {
        var normalized = fullName.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        // 目录条目（以 / 结尾）不参与解包；真实目录由文件条目创建。
        if (normalized.EndsWith('/')) return string.Empty;
        return normalized;
    }

    private static string ToRelativeFileSystemPath(string relativePath)
    {
        return relativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string GetWorkspaceDirectory(string fullPackagePath)
    {
        var tokenBytes = SHA256.HashData(
            EntryNameEncoding.GetBytes(Path.GetFullPath(fullPackagePath).TrimEnd(Path.DirectorySeparatorChar)));
        var token = Convert.ToHexString(tokenBytes)[..WorkspaceTokenLength].ToLowerInvariant();
        return Path.Combine(WorkspaceRoot, token);
    }

    private static string NormalizePackagePath(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("A project package path is required.", nameof(packagePath));
        }

        var fullPath = Path.GetFullPath(packagePath);
        if (!string.Equals(Path.GetExtension(fullPath), PackageExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Project packages must use the {PackageExtension} extension.",
                nameof(packagePath));
        }

        return fullPath;
    }

    private static void TouchWorkspace(string workspace)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(workspace, DateTime.UtcNow);
        }
        catch
        {
            // 时间戳只用于过期清理，失败不影响打开结果。
        }
    }

    private static bool IsHexadecimal(char value)
    {
        return (value >= '0' && value <= '9')
            || (value >= 'a' && value <= 'f')
            || (value >= 'A' && value <= 'F');
    }

    private static bool IsWorkspaceTokenPrefix(string name)
    {
        var dot = name.IndexOf('.');
        if (dot <= 0) return false;
        var prefix = name[..dot];
        return prefix.Length == WorkspaceTokenLength && prefix.All(IsHexadecimal);
    }

    private static void RejectReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Project packages and managed workspaces cannot use filesystem reparse points.");
        }
    }

    private static void ValidateMaximumFileSize(string path, long maximumBytes, string kind)
    {
        var length = new FileInfo(path).Length;
        if (length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The {kind} exceeds the maximum supported size of {maximumBytes} bytes.");
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Could not remove project workspace directory '{path}': {exception.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时文件由下次保存覆盖，删除失败无需升级。
        }
    }
}
