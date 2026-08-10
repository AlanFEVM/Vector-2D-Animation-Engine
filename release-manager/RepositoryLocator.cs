namespace VectorAnimationEngine;

internal static class RepositoryLocator
{
    private const string RepositoryEnvironmentVariable = "VECTOR2D_REPO_ROOT";

    public static string Find()
    {
        var configured = Environment.GetEnvironmentVariable(RepositoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return Validate(configured);

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (IsRepositoryRoot(directory.FullName)) return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Vector 2D Animation Engine repository. " +
            $"Run from the repository or set {RepositoryEnvironmentVariable}.");
    }

    public static string Validate(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsRepositoryRoot(fullPath))
        {
            throw new DirectoryNotFoundException($"The selected directory is not the repository root: {fullPath}");
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsRepositoryRoot(string path) =>
        File.Exists(Path.Combine(path, "Directory.Build.props"))
        && File.Exists(Path.Combine(path, "release", "release-notes.json"))
        && File.Exists(Path.Combine(path, "scripts", "publish-single-exe.ps1"))
        && File.Exists(Path.Combine(path, "native", "VectorAnimationEngine.Native.csproj"));
}
