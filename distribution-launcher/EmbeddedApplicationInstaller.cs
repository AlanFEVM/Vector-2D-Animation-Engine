using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace VectorAnimationEngine.DistributionLauncher;

internal static class EmbeddedApplicationInstaller
{
    private const string ResourceName = "VectorAnimationEngine.EmbeddedPayload.zip";
    private const string ApplicationDirectoryName = ".V2DEngine";
    private const string PayloadMarkerName = ".payload.sha256";
    private const int MaximumEntries = 4_096;
    private const long MaximumExtractedBytes = 64L * 1024 * 1024;

    public static bool HasValidEmbeddedPayload()
    {
        try
        {
            using var stream = OpenPayload();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            return HasRequiredEntry(archive, "VectorAnimationEngine.dll")
                && HasRequiredEntry(archive, "VectorAnimationEngine.deps.json")
                && HasRequiredEntry(archive, "VectorAnimationEngine.runtimeconfig.json")
                && HasRequiredEntry(archive, "release.json")
                && archive.Entries.Count <= MaximumEntries
                && archive.Entries.Sum(entry => entry.Length) <= MaximumExtractedBytes;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureInstalled(string root)
    {
        var applicationDirectory = Path.Combine(root, ApplicationDirectoryName);
        var payloadHash = EmbeddedPayloadHash();

        using var mutex = new Mutex(false, InstallMutexName(root));
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromMinutes(5));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
            if (!ownsMutex) throw new TimeoutException("等待软件主体更新超时。");

            var operationId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var stagingDirectory = Path.Combine(root, $".v2i-{operationId}");
            var backupDirectory = Path.Combine(root, $".v2b-{operationId}");
            try
            {
                using (var stream = OpenPayload())
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
                {
                    SafeArchive.Extract(
                        archive,
                        stagingDirectory,
                        MaximumEntries,
                        MaximumExtractedBytes,
                        CancellationToken.None);
                }

                if (!HasRequiredApplicationFiles(stagingDirectory))
                {
                    throw new InvalidDataException("内置软件主体不完整。");
                }
                File.WriteAllText(Path.Combine(stagingDirectory, PayloadMarkerName), payloadHash);
                ReplaceDirectory(applicationDirectory, stagingDirectory, backupDirectory);
                TryDeleteDirectory(backupDirectory);
            }
            finally
            {
                TryDeleteDirectory(stagingDirectory);
                if (!Directory.Exists(applicationDirectory) && Directory.Exists(backupDirectory))
                {
                    Directory.Move(backupDirectory, applicationDirectory);
                }
            }
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }

    public static bool RunSelfTest()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"v2d-payload-self-test-{Guid.NewGuid():N}");
        try
        {
            _ = SafeArchive.ResolveTargetPath(root, "VectorAnimationEngine.dll");
            try
            {
                _ = SafeArchive.ResolveTargetPath(root, "../outside.dll");
                return false;
            }
            catch (InvalidDataException)
            {
            }

            EnsureInstalled(root);
            var applicationDirectory = Path.Combine(root, ApplicationDirectoryName);
            if (!HasRequiredApplicationFiles(applicationDirectory)) return false;

            var sentinel = Path.Combine(applicationDirectory, ".stale-install-sentinel");
            File.WriteAllText(sentinel, "stale");
            EnsureInstalled(root);
            return HasRequiredApplicationFiles(applicationDirectory)
                && !File.Exists(sentinel);
        }
        catch
        {
            return false;
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static Stream OpenPayload()
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("EXE 中没有内置软件主体。");
    }

    private static string EmbeddedPayloadHash()
    {
        using var sha256 = SHA256.Create();
        using var stream = OpenPayload();
        return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
    }

    private static bool HasRequiredApplicationFiles(string directory)
    {
        return File.Exists(Path.Combine(directory, "VectorAnimationEngine.dll"))
            && File.Exists(Path.Combine(directory, "VectorAnimationEngine.deps.json"))
            && File.Exists(Path.Combine(directory, "VectorAnimationEngine.runtimeconfig.json"))
            && File.Exists(Path.Combine(directory, "release.json"));
    }

    private static bool HasRequiredEntry(ZipArchive archive, string name)
    {
        return archive.Entries.Any(entry => entry.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void ReplaceDirectory(string destination, string staging, string backup)
    {
        var backedUp = false;
        var installed = false;
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                backedUp = true;
            }
            Directory.Move(staging, destination);
            installed = true;
            if (!HasRequiredApplicationFiles(destination))
            {
                throw new InvalidDataException("软件主体更新后校验失败。");
            }
        }
        catch
        {
            if (installed && Directory.Exists(destination)) TryDeleteDirectory(destination);
            if (!Directory.Exists(destination) && backedUp && Directory.Exists(backup))
            {
                Directory.Move(backup, destination);
            }
            throw;
        }
    }

    private static string InstallMutexName(string root)
    {
        using var sha256 = SHA256.Create();
        var normalized = Path.GetFullPath(root).TrimEnd('\\', '/').ToUpperInvariant();
        var hash = BitConverter.ToString(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized)))
            .Replace("-", string.Empty)
            .Substring(0, 24);
        return $"Local\\Vector2D.ApplicationInstall.{hash}";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup failures do not invalidate an installed payload.
        }
    }
}
