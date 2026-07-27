using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace VectorAnimationEngine.DistributionLauncher;

internal static class RuntimeInstaller
{
    private const string ReleaseMetadataUrl =
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json";
    private const long MaximumDownloadBytes = 512L * 1024 * 1024;
    private const long MaximumExtractedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumArchiveEntries = 20_000;

    public static bool IsRuntimeValid(string runtimeDirectory)
    {
        if (!File.Exists(Path.Combine(runtimeDirectory, "dotnet.exe"))) return false;
        var desktopRoot = Path.Combine(runtimeDirectory, "shared", "Microsoft.WindowsDesktop.App");
        try
        {
            if (!Directory.Exists(desktopRoot)) return false;
            foreach (var directory in Directory.EnumerateDirectories(desktopRoot)
                         .OrderByDescending(path => ParseVersion(Path.GetFileName(path))))
            {
                var versionName = Path.GetFileName(directory);
                if (!Version.TryParse(versionName, out var version) || version.Major != 8) continue;
                if (File.Exists(Path.Combine(directory, "System.Windows.Forms.dll"))
                    && File.Exists(Path.Combine(runtimeDirectory, "shared", "Microsoft.NETCore.App", versionName, "coreclr.dll"))
                    && File.Exists(Path.Combine(runtimeDirectory, "host", "fxr", versionName, "hostfxr.dll")))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    public static void EnsureInstalled(
        string root,
        IProgress<RuntimeInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var runtimeDirectory = Path.Combine(root, ".Runtime");
        if (IsRuntimeValid(runtimeDirectory)) return;

        using var mutex = new Mutex(false, RuntimeMutexName(root));
        var ownsMutex = false;
        try
        {
            progress?.Report(new RuntimeInstallProgress("正在等待运行环境安装锁..."));
            try
            {
                ownsMutex = WaitForMutex(mutex, TimeSpan.FromMinutes(20), cancellationToken);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
            if (!ownsMutex) throw new TimeoutException("等待其他运行环境安装进程超时。");
            if (IsRuntimeValid(runtimeDirectory)) return;

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            progress?.Report(new RuntimeInstallProgress("正在获取 Microsoft .NET 8 发布信息..."));
            var bundle = ResolveRuntimeBundle(cancellationToken);
            var operationId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var coreArchivePath = Path.Combine(root, $".rtc-{operationId}.zip");
            var desktopArchivePath = Path.Combine(root, $".rtd-{operationId}.zip");
            var stagingDirectory = Path.Combine(root, $".rti-{operationId}");
            var backupDirectory = Path.Combine(root, $".rtb-{operationId}");
            try
            {
                DownloadPackage(bundle.Core, coreArchivePath, progress, cancellationToken);
                progress?.Report(new RuntimeInstallProgress("正在校验 .NET Core 运行环境..."));
                VerifyPackageHash(coreArchivePath, bundle.Core.Hash);
                DownloadPackage(bundle.Desktop, desktopArchivePath, progress, cancellationToken);
                progress?.Report(new RuntimeInstallProgress("正在校验 Windows Desktop 运行环境..."));
                VerifyPackageHash(desktopArchivePath, bundle.Desktop.Hash);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new RuntimeInstallProgress("正在安装运行环境..."));
                ExtractPackage(coreArchivePath, stagingDirectory, cancellationToken);
                ExtractPackage(desktopArchivePath, stagingDirectory, cancellationToken);
                if (!IsRuntimeValid(stagingDirectory))
                {
                    throw new InvalidDataException("下载的运行环境结构不完整。" + DescribeRuntimeLayout(stagingDirectory));
                }

                cancellationToken.ThrowIfCancellationRequested();
                ReplaceRuntimeDirectory(runtimeDirectory, stagingDirectory, backupDirectory);
                TryDeleteDirectory(backupDirectory);
                progress?.Report(new RuntimeInstallProgress($".NET {bundle.Version} 运行环境已安装。", 100));
            }
            finally
            {
                TryDeleteFile(coreArchivePath);
                TryDeleteFile(desktopArchivePath);
                TryDeleteDirectory(stagingDirectory);
                if (!Directory.Exists(runtimeDirectory) && Directory.Exists(backupDirectory))
                {
                    Directory.Move(backupDirectory, runtimeDirectory);
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
        try
        {
            const string fixture = "{\"releases\":[{\"runtime\":{\"version\":\"8.0.12\",\"files\":[{\"name\":\"dotnet-runtime-win-x64.zip\",\"rid\":\"win-x64\",\"url\":\"https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.12/runtime.zip\",\"hash\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}]},\"windowsdesktop\":{\"version\":\"8.0.12\",\"files\":[{\"name\":\"windowsdesktop-runtime-win-x64.zip\",\"rid\":\"win-x64\",\"url\":\"https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.12/runtime.zip\",\"hash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]}}]}";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(fixture));
            var bundle = ParseRuntimeBundle(stream);
            return bundle.Version == new Version(8, 0, 12)
                && bundle.Core.Hash.Length == 128
                && bundle.Desktop.Hash.Length == 128
                && IsOfficialDownloadUri(new Uri(bundle.Core.Url))
                && IsOfficialDownloadUri(new Uri(bundle.Desktop.Url));
        }
        catch
        {
            return false;
        }
    }

    private static RuntimeBundle ResolveRuntimeBundle(CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient();
        using var response = client.GetAsync(ReleaseMetadataUrl, cancellationToken).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        ValidateFinalResponseUri(response);
        using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        return ParseRuntimeBundle(stream);
    }

    private static RuntimeBundle ParseRuntimeBundle(Stream stream)
    {
        var serializer = new DataContractJsonSerializer(typeof(ReleaseMetadata));
        var metadata = serializer.ReadObject(stream) as ReleaseMetadata;
        RuntimeBundle? selected = null;
        foreach (var release in metadata?.Releases ?? Array.Empty<ReleaseEntry>())
        {
            if (!Version.TryParse(release.Runtime?.Version, out var coreVersion)
                || !Version.TryParse(release.WindowsDesktop?.Version, out var desktopVersion)
                || coreVersion.Major != 8
                || coreVersion != desktopVersion)
            {
                continue;
            }

            var core = FindRuntimePackage(release.Runtime?.Files, "dotnet-runtime-win-x64.zip", ".NET Core");
            var desktop = FindRuntimePackage(
                release.WindowsDesktop?.Files,
                "windowsdesktop-runtime-win-x64.zip",
                "Windows Desktop");
            if (core is null || desktop is null) continue;
            if (selected is null || coreVersion > selected.Version)
            {
                selected = new RuntimeBundle(coreVersion, core, desktop);
            }
        }

        return selected ?? throw new InvalidDataException(
            "Microsoft 发布信息中没有同版本的 .NET 8 Core 与 Windows Desktop x64 运行环境。");
    }

    private static RuntimePackage? FindRuntimePackage(
        IReadOnlyList<ReleaseFile>? files,
        string expectedName,
        string displayName)
    {
        foreach (var file in files ?? Array.Empty<ReleaseFile>())
        {
            if (!string.Equals(file.Rid, "win-x64", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(file.Name, expectedName, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(file.Url)
                || string.IsNullOrWhiteSpace(file.Hash))
            {
                continue;
            }

            if (!Uri.TryCreate(file.Url!, UriKind.Absolute, out var uri) || !IsOfficialDownloadUri(uri)) continue;
            var normalizedHash = file.Hash!.Trim();
            if (normalizedHash.Length != 128 || normalizedHash.Any(character => !Uri.IsHexDigit(character))) continue;
            return new RuntimePackage(displayName, uri.AbsoluteUri, normalizedHash);
        }
        return null;
    }

    private static void DownloadPackage(
        RuntimePackage package,
        string destination,
        IProgress<RuntimeInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient();
        using var response = client.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .GetAwaiter()
            .GetResult();
        response.EnsureSuccessStatusCode();
        ValidateFinalResponseUri(response);
        var total = response.Content.Headers.ContentLength;
        if (total is > MaximumDownloadBytes) throw new InvalidDataException("运行环境下载文件超过允许大小。");

        using var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            FileOptions.SequentialScan);
        var buffer = new byte[1024 * 128];
        long received = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).GetAwaiter().GetResult();
            if (read <= 0) break;
            target.Write(buffer, 0, read);
            received += read;
            if (received > MaximumDownloadBytes) throw new InvalidDataException("运行环境下载文件超过允许大小。");
            var percent = total is > 0 ? (int)Math.Min(100, received * 100 / total.Value) : (int?)null;
            progress?.Report(new RuntimeInstallProgress(
                total is > 0
                    ? $"正在下载 {package.DisplayName} 运行环境... {percent}%"
                    : $"正在下载 {package.DisplayName} 运行环境...",
                percent));
        }
        target.Flush(true);
    }

    private static void VerifyPackageHash(string archivePath, string expectedHash)
    {
        using var sha512 = SHA512.Create();
        using var stream = File.OpenRead(archivePath);
        var actual = BitConverter.ToString(sha512.ComputeHash(stream)).Replace("-", string.Empty);
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("运行环境下载文件的 SHA-512 校验失败。");
        }
    }

    private static void ExtractPackage(string archivePath, string destination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        SafeArchive.Extract(
            archive,
            destination,
            MaximumArchiveEntries,
            MaximumExtractedBytes,
            cancellationToken);
    }

    private static void ReplaceRuntimeDirectory(string runtime, string staging, string backup)
    {
        var backedUp = false;
        var installed = false;
        try
        {
            if (Directory.Exists(runtime))
            {
                Directory.Move(runtime, backup);
                backedUp = true;
            }
            Directory.Move(staging, runtime);
            installed = true;
            if (!IsRuntimeValid(runtime)) throw new InvalidDataException("运行环境安装后校验失败。");
        }
        catch
        {
            if (installed && Directory.Exists(runtime)) TryDeleteDirectory(runtime);
            if (!Directory.Exists(runtime) && backedUp && Directory.Exists(backup)) Directory.Move(backup, runtime);
            throw;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Vector2DAnimationEngine-Bootstrap/1.0");
        return client;
    }

    private static void ValidateFinalResponseUri(HttpResponseMessage response)
    {
        var uri = response.RequestMessage?.RequestUri;
        if (uri is null || !IsOfficialDownloadUri(uri))
        {
            throw new InvalidDataException("运行环境下载被重定向到非 Microsoft 地址。");
        }
    }

    private static bool IsOfficialDownloadUri(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        var host = uri.DnsSafeHost;
        return host.Equals("builds.dotnet.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("dotnetcli.azureedge.net", StringComparison.OrdinalIgnoreCase)
            || host.Equals("download.visualstudio.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".download.visualstudio.microsoft.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool WaitForMutex(Mutex mutex, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mutex.WaitOne(TimeSpan.FromMilliseconds(250))) return true;
        }
        return false;
    }

    private static string RuntimeMutexName(string root)
    {
        using var sha256 = SHA256.Create();
        var normalized = Path.GetFullPath(root).TrimEnd('\\', '/').ToUpperInvariant();
        var hash = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized)))
            .Replace("-", string.Empty)
            .Substring(0, 24);
        return $"Local\\Vector2D.RuntimeInstall.{hash}";
    }

    private static Version ParseVersion(string value)
    {
        return Version.TryParse(value, out var version) ? version : new Version(0, 0);
    }

    private static string DescribeRuntimeLayout(string runtimeDirectory)
    {
        return $" dotnet={File.Exists(Path.Combine(runtimeDirectory, "dotnet.exe"))};"
            + $" hostfxr={RuntimeVersions(Path.Combine(runtimeDirectory, "host", "fxr"))};"
            + $" core={RuntimeVersions(Path.Combine(runtimeDirectory, "shared", "Microsoft.NETCore.App"))};"
            + $" desktop={RuntimeVersions(Path.Combine(runtimeDirectory, "shared", "Microsoft.WindowsDesktop.App"))}.";
    }

    private static string RuntimeVersions(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? string.Join(",", Directory.EnumerateDirectories(root).Select(Path.GetFileName))
                : "missing";
        }
        catch
        {
            return "unreadable";
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    [DataContract]
    private sealed class ReleaseMetadata
    {
        [DataMember(Name = "releases")]
        public ReleaseEntry[]? Releases { get; set; }
    }

    [DataContract]
    private sealed class ReleaseEntry
    {
        [DataMember(Name = "runtime")]
        public RuntimeComponentRelease? Runtime { get; set; }

        [DataMember(Name = "windowsdesktop")]
        public RuntimeComponentRelease? WindowsDesktop { get; set; }
    }

    [DataContract]
    private sealed class RuntimeComponentRelease
    {
        [DataMember(Name = "version")]
        public string? Version { get; set; }

        [DataMember(Name = "files")]
        public ReleaseFile[]? Files { get; set; }
    }

    [DataContract]
    private sealed class ReleaseFile
    {
        [DataMember(Name = "name")]
        public string? Name { get; set; }

        [DataMember(Name = "rid")]
        public string? Rid { get; set; }

        [DataMember(Name = "url")]
        public string? Url { get; set; }

        [DataMember(Name = "hash")]
        public string? Hash { get; set; }
    }

    private sealed class RuntimePackage
    {
        public RuntimePackage(string displayName, string url, string hash)
        {
            DisplayName = displayName;
            Url = url;
            Hash = hash;
        }

        public string DisplayName { get; }
        public string Url { get; }
        public string Hash { get; }
    }

    private sealed class RuntimeBundle
    {
        public RuntimeBundle(Version version, RuntimePackage core, RuntimePackage desktop)
        {
            Version = version;
            Core = core;
            Desktop = desktop;
        }

        public Version Version { get; }
        public RuntimePackage Core { get; }
        public RuntimePackage Desktop { get; }
    }
}
