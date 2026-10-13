using System.Security.Cryptography;

namespace VectorAnimationEngine;

internal static class ProjectTemporaryResources
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Roots = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, FileStream> Leases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string Parent = Path.Combine(Path.GetTempPath(), "Vector2DAnimationEngine", "Projects");

    internal static void RejectReparsePath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Resource paths cannot contain filesystem reparse points.");
    }

    internal static string Ensure(string projectId)
    {
        lock (Gate)
        {
            if (Roots.TryGetValue(projectId, out var existing)) return existing;
            RejectReparsePath(Parent);
            Directory.CreateDirectory(Parent);
            ReclaimExpired();
            var token = Guid.NewGuid().ToString("N");
            var root = Path.Combine(Parent, token);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".owner"), token);
            Leases.Add(root, new FileStream(Path.Combine(root, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read));
            Roots.Add(projectId, root);
            return root;
        }
    }

    internal static bool TryCopy(string projectId, string source, string checksum, long maximumBytes, out string destination)
    {
        destination = "";
        try
        {
            destination = Copy(projectId, source, checksum, maximumBytes);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {
            AppLog.Warn($"Resource import failed: {exception.Message}");
            return false;
        }
    }

    internal static string SnapshotSource(string projectId, string source, string checksum, long maximumBytes)
        => TryCopy(projectId, source, checksum, maximumBytes, out var copy) ? copy : source;

    internal static string Copy(string projectId, string source, string checksum, long maximumBytes)
    {
        lock (Gate)
        {
            RejectReparsePath(source);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > maximumBytes) throw new InvalidDataException("Resource exceeds the supported size limit.");
            var actual = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (!string.Equals(actual, checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Resource changed since it was imported.");
            var root = Ensure(projectId);
            RejectReparsePath(root);
            var destination = Path.Combine(root, actual + Path.GetExtension(source).ToLowerInvariant());
            RejectReparsePath(destination);
            if (File.Exists(destination))
            {
                using var cached = File.OpenRead(destination);
                if (cached.Length != input.Length || !string.Equals(Convert.ToHexString(SHA256.HashData(cached)), actual, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Temporary resource checksum does not match.");
                return destination;
            }
            input.Position = 0;
            var created = false;
            try
            {
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    input.CopyTo(output);
                    output.Flush(true);
                }
                using var copied = File.OpenRead(destination);
                if (copied.Length != input.Length || !string.Equals(Convert.ToHexString(SHA256.HashData(copied)), actual, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Copied resource checksum does not match.");
                return destination;
            }
            catch
            {
                if (created) File.Delete(destination);
                throw;
            }
        }
    }

    private static void ValidateRoot(string root)
    {
        if (!string.Equals(Path.GetDirectoryName(root), Parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
            throw new InvalidDataException("Temporary resource root is invalid.");
        RejectReparsePath(root);
        var marker = Path.Combine(root, ".owner");
        RejectReparsePath(marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length != 32 || File.ReadAllText(marker) != Path.GetFileName(root))
            throw new InvalidDataException("Temporary resource ownership is invalid.");
    }

    internal static void Adopt(string source)
    {
        var root = Path.GetDirectoryName(source);
        if (root is null || !string.Equals(Path.GetDirectoryName(root), Parent, StringComparison.OrdinalIgnoreCase)) return;
        AdoptRoots([root]);
    }

    internal static void AdoptRoots(IEnumerable<string> roots)
    {
        lock (Gate)
            foreach (var root in roots)
            {
                ValidateRoot(root);
                if (Leases.ContainsKey(root)) continue;
                var lease = Path.Combine(root, ".lease");
                RejectReparsePath(lease);
                Leases.Add(root, new FileStream(lease, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
    }

    internal static string[] PreserveForRestart(DateTimeOffset expiry)
    {
        lock (Gate)
        {
            foreach (var root in Leases.Keys)
            {
                ValidateRoot(root);
                var marker = Path.Combine(root, ".preserve");
                RejectReparsePath(marker);
                File.WriteAllText(marker, expiry.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return Leases.Keys.ToArray();
        }
    }

    internal static void CompleteRestart()
    {
        lock (Gate)
            foreach (var root in Leases.Keys)
            {
                var marker = Path.Combine(root, ".preserve");
                RejectReparsePath(marker);
                File.Delete(marker);
            }
    }

    private static bool IsPreserved(string root)
    {
        var marker = Path.Combine(root, ".preserve");
        RejectReparsePath(marker);
        return File.Exists(marker) && new FileInfo(marker).Length <= 32
            && long.TryParse(File.ReadAllText(marker), out var expiry)
            && expiry > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    internal static void Cleanup()
    {
        lock (Gate)
        {
            foreach (var lease in Leases.Values) lease.Dispose();
            Leases.Clear();
            Roots.Clear();
            ReclaimExpired();
        }
    }

    private static void ReclaimExpired()
    {
        if (!Directory.Exists(Parent)) return;
        RejectReparsePath(Parent);
        foreach (var root in Directory.EnumerateDirectories(Parent))
        {
            try
            {
                ValidateRoot(root);
                if (IsPreserved(root)) continue;
                var leasePath = Path.Combine(root, ".lease");
                RejectReparsePath(leasePath);
                using (var lease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    DeleteContents(root, leasePath);
                File.Delete(leasePath);
                Directory.Delete(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AppLog.Warn($"Temporary project cleanup deferred: {exception.Message}");
            }
        }
    }

    private static void DeleteContents(string directory, string leasePath)
    {
        RejectReparsePath(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectReparsePath(entry);
            if (string.Equals(entry, leasePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(entry))
            {
                DeleteContents(entry, leasePath);
                Directory.Delete(entry);
            }
            else File.Delete(entry);
        }
    }
}
