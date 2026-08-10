using System.Text;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal sealed record ReleaseSourceFingerprints(string ManifestSha256, string PropsSha256);

internal sealed class ReleaseDocumentStore
{
    private readonly string _manifestPath;
    private readonly string _propsPath;
    private string? _loadedManifestFingerprint;
    private string? _loadedPropsFingerprint;

    public ReleaseDocumentStore(string repositoryRoot)
    {
        RepositoryRoot = RepositoryLocator.Validate(repositoryRoot);
        _manifestPath = Path.Combine(RepositoryRoot, "release", "release-notes.json");
        _propsPath = Path.Combine(RepositoryRoot, "Directory.Build.props");
    }

    public string RepositoryRoot { get; }
    public string ManifestPath => _manifestPath;

    public ReleaseNotesManifestDocument LoadManifest()
    {
        var content = File.ReadAllBytes(_manifestPath);
        using var stream = new MemoryStream(content, writable: false);
        var document = ReleaseNotesManifestCodec.Read(stream);
        ReleaseNotesManifestCodec.EnsureValid(document);
        SortNewestFirst(document);
        _loadedManifestFingerprint = Fingerprint(content);
        return document;
    }

    public Version LoadSourceVersion()
    {
        var content = File.ReadAllBytes(_propsPath);
        using var stream = new MemoryStream(content, writable: false);
        var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        var value = document.Root?
            .Elements("PropertyGroup")
            .Elements("Version")
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!TryParseStrictVersion(value, out var version))
        {
            throw new InvalidDataException("Directory.Build.props does not contain a strict x.y.z Version value.");
        }

        _loadedPropsFingerprint = Fingerprint(content);
        return version;
    }

    public IReadOnlyList<string> Validate(ReleaseNotesManifestDocument document, Version? selectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ReleaseNotesManifestCodec.Validate(document, selectedVersion);
    }

    public IReadOnlyList<string> ValidateForPublish(ReleaseNotesManifestDocument document, Version selectedVersion)
    {
        var errors = Validate(document, selectedVersion).ToList();
        var release = document.Releases.FirstOrDefault(candidate =>
            TryParseStrictVersion(candidate.Version, out var version) && version == selectedVersion);
        if (release is not null && ContainsPlaceholderContent(release))
        {
            errors.Add($"Release {selectedVersion.ToString(3)} still contains placeholder release-note content.");
        }
        return errors.AsReadOnly();
    }

    public bool SourceFilesMatchLoaded()
    {
        try
        {
            EnsureSourceFilesUnchanged();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public ReleaseSourceFingerprints GetLoadedFingerprints()
    {
        EnsureSourceFilesUnchanged();
        return new ReleaseSourceFingerprints(_loadedManifestFingerprint!, _loadedPropsFingerprint!);
    }

    public void Save(ReleaseNotesManifestDocument document, Version selectedVersion)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selectedVersion);
        ReleaseNotesManifestCodec.EnsureValid(document, selectedVersion);
        SortNewestFirst(document);
        EnsureSourceFilesUnchanged();

        var manifestText = EnsureTrailingNewLine(ReleaseNotesManifestCodec.Serialize(document));
        var propsText = CreateUpdatedProps(selectedVersion);
        var manifestBytes = Encoding.UTF8.GetBytes(manifestText);
        var propsBytes = Encoding.UTF8.GetBytes(propsText);
        ReplaceFilesWithRollback(
            () => VerifySavedFiles(selectedVersion),
            (_manifestPath, manifestBytes),
            (_propsPath, propsBytes));
        _loadedManifestFingerprint = Fingerprint(manifestBytes);
        _loadedPropsFingerprint = Fingerprint(propsBytes);
    }

    public static bool TryParseStrictVersion(string? text, out Version version)
    {
        return ReleaseNotesManifestCodec.TryParseStrictVersion(text, out version);
    }

    public static ReleaseNotesManifestRelease CreateNextPatch(ReleaseNotesManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var latest = document.Releases
            .Select(release => TryParseStrictVersion(release.Version, out var parsed) ? parsed : null)
            .Where(version => version is not null)
            .Cast<Version>()
            .OrderByDescending(version => version)
            .FirstOrDefault() ?? new Version(0, 0, 0);
        var next = new Version(latest.Major, latest.Minor, checked(latest.Build + 1));
        var nextText = next.ToString(3);

        return new ReleaseNotesManifestRelease
        {
            Version = nextText,
            ReleaseDate = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"),
            ShowInApplication = true,
            Locales = new ReleaseNotesManifestLocales
            {
                English = NewLocale(
                    $"Version {nextText}",
                    $"Release notes for version {nextText}.",
                    "Highlights",
                    "Describe the release highlight."),
                SimplifiedChinese = NewLocale(
                    $"版本 {nextText}",
                    $"{nextText} 版本更新说明。",
                    "版本亮点",
                    "描述本版本的主要变化。")
            }
        };
    }

    public static ReleaseNoteEntry ToReleaseNoteEntry(ReleaseNotesManifestRelease release)
    {
        if (!TryParseStrictVersion(release.Version, out var version))
        {
            throw new InvalidDataException("A strict x.y.z version is required for preview.");
        }
        if (!DateOnly.TryParseExact(
                release.ReleaseDate,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var releaseDate))
        {
            throw new InvalidDataException("A yyyy-MM-dd release date is required for preview.");
        }

        return new ReleaseNoteEntry(
            version,
            releaseDate,
            release.ShowInApplication,
            ToLocalizedReleaseNote(release.Locales.English),
            ToLocalizedReleaseNote(release.Locales.SimplifiedChinese));
    }

    private static ReleaseNotesManifestLocale NewLocale(
        string title,
        string summary,
        string heading,
        string item) => new()
    {
        Title = title,
        Summary = summary,
        Sections =
        [
            new ReleaseNotesManifestSection
            {
                Heading = heading,
                Items = [item]
            }
        ]
    };

    private static LocalizedReleaseNote ToLocalizedReleaseNote(ReleaseNotesManifestLocale locale) => new(
        locale.Title,
        locale.Summary,
        locale.Sections
            .Select(section => new ReleaseNoteSection(section.Heading, section.Items.ToArray()))
            .ToArray());

    private static bool ContainsPlaceholderContent(ReleaseNotesManifestRelease release)
    {
        return ContainsPlaceholderContent(release.Locales.English)
            || ContainsPlaceholderContent(release.Locales.SimplifiedChinese);
    }

    private static bool ContainsPlaceholderContent(ReleaseNotesManifestLocale locale)
    {
        return locale.Sections.Any(section =>
            string.Equals(section.Heading, "New section", StringComparison.Ordinal)
            || string.Equals(section.Heading, "新分区", StringComparison.Ordinal)
            || section.Items.Any(item =>
                string.Equals(item, "Describe the release highlight.", StringComparison.Ordinal)
                || string.Equals(item, "描述本版本的主要变化。", StringComparison.Ordinal)
                || string.Equals(item, "Describe this change.", StringComparison.Ordinal)
                || string.Equals(item, "描述本项改动。", StringComparison.Ordinal)));
    }

    private void EnsureSourceFilesUnchanged()
    {
        if (_loadedManifestFingerprint is null || _loadedPropsFingerprint is null)
        {
            throw new InvalidOperationException("Load the release manifest and source version before saving.");
        }
        if (!string.Equals(_loadedManifestFingerprint, Fingerprint(File.ReadAllBytes(_manifestPath)), StringComparison.Ordinal)
            || !string.Equals(_loadedPropsFingerprint, Fingerprint(File.ReadAllBytes(_propsPath)), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Release source files changed outside Release Manager. Reload the tool before saving.");
        }
    }

    private static string Fingerprint(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private string CreateUpdatedProps(Version selectedVersion)
    {
        var document = XDocument.Load(_propsPath, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("Directory.Build.props has no Project root.");
        var propertyGroup = root.Elements("PropertyGroup").FirstOrDefault();
        if (propertyGroup is null)
        {
            propertyGroup = new XElement("PropertyGroup");
            root.Add(propertyGroup);
        }

        var version = selectedVersion.ToString(3);
        SetProperty(propertyGroup, "Version", version);
        SetProperty(propertyGroup, "AssemblyVersion", version + ".0");
        SetProperty(propertyGroup, "FileVersion", version + ".0");
        SetProperty(propertyGroup, "InformationalVersion", version);

        using var writer = new Utf8StringWriter();
        using (var xmlWriter = XmlWriter.Create(writer, new XmlWriterSettings
        {
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            OmitXmlDeclaration = document.Declaration is null
        }))
        {
            document.Save(xmlWriter);
        }
        return EnsureTrailingNewLine(writer.ToString());
    }

    private static void SetProperty(XElement propertyGroup, string name, string value)
    {
        var element = propertyGroup.Element(name);
        if (element is null)
        {
            propertyGroup.Add(new XElement(name, value));
        }
        else
        {
            element.Value = value;
        }
    }

    private static void SortNewestFirst(ReleaseNotesManifestDocument document)
    {
        document.Releases = document.Releases
            .OrderByDescending(release =>
                TryParseStrictVersion(release.Version, out var version) ? version : new Version(0, 0, 0))
            .ToList();
    }

    private static string EnsureTrailingNewLine(string text) =>
        text.EndsWith(Environment.NewLine, StringComparison.Ordinal)
            ? text
            : text.TrimEnd('\r', '\n') + Environment.NewLine;

    private void VerifySavedFiles(Version selectedVersion)
    {
        using (var stream = File.OpenRead(_manifestPath))
        {
            var manifest = ReleaseNotesManifestCodec.Read(stream);
            ReleaseNotesManifestCodec.EnsureValid(manifest, selectedVersion);
        }

        var document = XDocument.Load(_propsPath, LoadOptions.PreserveWhitespace);
        var properties = document.Root?
            .Elements("PropertyGroup")
            .Elements()
            .GroupBy(element => element.Name.LocalName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value.Trim(), StringComparer.Ordinal)
            ?? throw new InvalidDataException("Saved Directory.Build.props has no Project root.");
        var version = selectedVersion.ToString(3);
        var assemblyVersion = version + ".0";
        if (!properties.TryGetValue("Version", out var savedVersion) || savedVersion != version
            || !properties.TryGetValue("AssemblyVersion", out var savedAssembly) || savedAssembly != assemblyVersion
            || !properties.TryGetValue("FileVersion", out var savedFile) || savedFile != assemblyVersion
            || !properties.TryGetValue("InformationalVersion", out var savedInformational) || savedInformational != version)
        {
            throw new InvalidDataException("Saved Directory.Build.props version properties failed read-back verification.");
        }
    }

    private static void ReplaceFilesWithRollback(
        Action verify,
        params (string Path, byte[] Content)[] files)
    {
        ArgumentNullException.ThrowIfNull(verify);
        var originals = files.ToDictionary(
            file => file.Path,
            file => File.Exists(file.Path) ? File.ReadAllBytes(file.Path) : null,
            StringComparer.OrdinalIgnoreCase);
        var temporaryPaths = new List<string>(files.Length);
        var replacedPaths = new List<string>(files.Length);

        try
        {
            foreach (var file in files)
            {
                var temporaryPath = file.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                WriteDurable(temporaryPath, file.Content);
                temporaryPaths.Add(temporaryPath);
            }

            for (var index = 0; index < files.Length; index++)
            {
                File.Move(temporaryPaths[index], files[index].Path, overwrite: true);
                replacedPaths.Add(files[index].Path);
            }
            verify();
        }
        catch
        {
            foreach (var path in replacedPaths.AsEnumerable().Reverse())
            {
                var original = originals[path];
                if (original is null)
                {
                    File.Delete(path);
                    continue;
                }

                var rollbackPath = path + "." + Guid.NewGuid().ToString("N") + ".rollback";
                WriteDurable(rollbackPath, original);
                File.Move(rollbackPath, path, overwrite: true);
            }

            throw;
        }
        finally
        {
            foreach (var temporaryPath in temporaryPaths)
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    private static void WriteDurable(string path, byte[] content)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}
