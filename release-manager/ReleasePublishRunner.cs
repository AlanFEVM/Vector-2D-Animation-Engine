using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace VectorAnimationEngine;

internal sealed record ReleasePublishResult(
    int SchemaVersion,
    string Version,
    string Path,
    long SizeBytes,
    long MaximumBytes,
    string Sha256);

internal sealed class ReleasePublishRunner
{
    private readonly string _repositoryRoot;

    public ReleasePublishRunner(string repositoryRoot)
    {
        _repositoryRoot = RepositoryLocator.Validate(repositoryRoot);
    }

    public async Task<ReleasePublishResult> PublishAsync(
        Version version,
        string outputDirectory,
        ReleaseSourceFingerprints sourceFingerprints,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(sourceFingerprints);
        ArgumentNullException.ThrowIfNull(log);

        var scriptPath = Path.Combine(_repositoryRoot, "scripts", "publish-single-exe.ps1");
        var resultPath = Path.Combine(
            Path.GetTempPath(),
            "v2d-release-manager-" + Guid.NewGuid().ToString("N") + ".json");
        var cancellationPath = Path.Combine(
            Path.GetTempPath(),
            "v2d-release-manager-cancel-" + Guid.NewGuid().ToString("N") + ".signal");
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShell(),
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-Version");
        startInfo.ArgumentList.Add(version.ToString(3));
        startInfo.ArgumentList.Add("-OutputDirectory");
        startInfo.ArgumentList.Add(Path.GetFullPath(outputDirectory));
        startInfo.ArgumentList.Add("-ResultPath");
        startInfo.ArgumentList.Add(resultPath);
        startInfo.ArgumentList.Add("-CancellationPath");
        startInfo.ArgumentList.Add(cancellationPath);
        startInfo.ArgumentList.Add("-ExpectedManifestSha256");
        startInfo.ArgumentList.Add(sourceFingerprints.ManifestSha256);
        startInfo.ArgumentList.Add("-ExpectedPropsSha256");
        startInfo.ArgumentList.Add(sourceFingerprints.PropsSha256);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var cancellationAcknowledged = false;
        process.OutputDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data)) return;
            if (string.Equals(args.Data, "V2D_RELEASE_CANCELLED=1", StringComparison.Ordinal))
            {
                cancellationAcknowledged = true;
            }
            log(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) log("ERROR: " + args.Data);
        };

        try
        {
            log($"> {startInfo.FileName} -File {scriptPath} -Version {version.ToString(3)}");
            if (!process.Start()) throw new InvalidOperationException("The release publish process did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    File.WriteAllText(cancellationPath, string.Empty);
                    log("Cancellation requested. The current packaging step will finish before publishing stops.");
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            });
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (!cancellationAcknowledged
                && cancellationToken.IsCancellationRequested
                && File.Exists(cancellationPath))
            {
                try
                {
                    cancellationAcknowledged = string.Equals(
                        File.ReadAllText(cancellationPath).Trim(),
                        "acknowledged",
                        StringComparison.Ordinal);
                }
                catch (IOException)
                {
                }
            }
            if (cancellationAcknowledged) throw new OperationCanceledException(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Release publishing failed with exit code {process.ExitCode}.");
            }
            if (!File.Exists(resultPath))
            {
                throw new InvalidDataException("The publish script did not write its result contract.");
            }

            var result = JsonSerializer.Deserialize<ReleasePublishResult>(
                await File.ReadAllTextAsync(resultPath, CancellationToken.None).ConfigureAwait(false),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The publish result contract is empty.");
            await VerifyResultAsync(result, version, outputDirectory, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (File.Exists(resultPath)) File.Delete(resultPath);
            if (File.Exists(cancellationPath)) File.Delete(cancellationPath);
        }
    }

    private static async Task VerifyResultAsync(
        ReleasePublishResult result,
        Version requestedVersion,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        if (result.SchemaVersion != 1) throw new InvalidDataException("Unsupported publish result schema.");
        if (!string.Equals(result.Version, requestedVersion.ToString(3), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The published artifact version does not match the requested version.");
        }

        var artifactPath = Path.GetFullPath(result.Path);
        var outputRoot = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!artifactPath.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The publish result points outside the selected output directory.");
        }
        if (!File.Exists(artifactPath)) throw new FileNotFoundException("The published artifact is missing.", artifactPath);
        var expectedName = $"VectorAnimationEngine-{requestedVersion.ToString(3)}-win-x64.exe";
        if (!string.Equals(Path.GetFileName(artifactPath), expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The publish result artifact name does not match the formal package contract.");
        }

        var fileInfo = new FileInfo(artifactPath);
        if (fileInfo.Length != result.SizeBytes
            || result.SizeBytes <= 0
            || result.MaximumBytes <= 0
            || result.SizeBytes >= result.MaximumBytes)
        {
            throw new InvalidDataException("The published artifact size does not match the verified result.");
        }

        await using var stream = File.OpenRead(artifactPath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (result.Sha256.Length != 64
            || !result.Sha256.All(Uri.IsHexDigit)
            || !string.Equals(hash, result.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The published artifact SHA-256 does not match the verified result.");
        }
    }

    private static string ResolvePowerShell()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windowsPowerShell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windowsPowerShell) ? windowsPowerShell : "pwsh.exe";
    }
}
