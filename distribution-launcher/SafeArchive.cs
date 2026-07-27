using System.IO.Compression;

namespace VectorAnimationEngine.DistributionLauncher;

internal static class SafeArchive
{
    public static void Extract(
        ZipArchive archive,
        string destination,
        int maximumEntries,
        long maximumExtractedBytes,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > maximumEntries)
        {
            throw new InvalidDataException("压缩包包含过多文件。");
        }

        Directory.CreateDirectory(destination);
        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            extractedBytes = checked(extractedBytes + entry.Length);
            if (extractedBytes > maximumExtractedBytes)
            {
                throw new InvalidDataException("压缩包解压大小超过允许范围。");
            }

            var targetPath = ResolveTargetPath(destination, entry.FullName);
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)
                || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    public static string ResolveTargetPath(string destination, string entryName)
    {
        var segments = entryName
            .Replace('\\', '/')
            .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || Path.IsPathRooted(entryName)
            || segments.Any(segment => segment is "." or ".." || segment.IndexOf(':') >= 0))
        {
            throw new InvalidDataException("压缩包包含不安全的文件路径。");
        }

        var root = Path.GetFullPath(destination)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(destination, Path.Combine(segments)));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("压缩包尝试写入软件目录之外。");
        }
        return target;
    }
}
