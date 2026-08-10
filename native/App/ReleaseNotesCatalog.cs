using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace VectorAnimationEngine;

internal sealed class ReleaseNoteSection
{
    public ReleaseNoteSection(string heading, params string[] items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Length == 0 || items.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A release-note section must contain at least one non-empty item.", nameof(items));
        }

        Heading = heading;
        Items = Array.AsReadOnly(items.ToArray());
    }

    public string Heading { get; }
    public IReadOnlyList<string> Items { get; }
}

internal sealed class LocalizedReleaseNote
{
    public LocalizedReleaseNote(string title, string summary, params ReleaseNoteSection[] sections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Length == 0 || sections.Any(section => section is null))
        {
            throw new ArgumentException("Localized release notes must contain at least one section.", nameof(sections));
        }

        Title = title;
        Summary = summary;
        Sections = Array.AsReadOnly(sections.ToArray());
    }

    public string Title { get; }
    public string Summary { get; }
    public IReadOnlyList<ReleaseNoteSection> Sections { get; }
}

internal sealed class ReleaseNoteEntry
{
    public ReleaseNoteEntry(
        Version version,
        DateOnly releaseDate,
        LocalizedReleaseNote english,
        LocalizedReleaseNote simplifiedChinese)
        : this(version, releaseDate, showInApplication: true, english, simplifiedChinese)
    {
    }

    public ReleaseNoteEntry(
        Version version,
        DateOnly releaseDate,
        bool showInApplication,
        LocalizedReleaseNote english,
        LocalizedReleaseNote simplifiedChinese)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(english);
        ArgumentNullException.ThrowIfNull(simplifiedChinese);

        Version = version;
        ReleaseDate = releaseDate;
        ShowInApplication = showInApplication;
        English = english;
        SimplifiedChinese = simplifiedChinese;
    }

    public Version Version { get; }
    public DateOnly ReleaseDate { get; }
    public bool ShowInApplication { get; }
    public LocalizedReleaseNote English { get; }
    public LocalizedReleaseNote SimplifiedChinese { get; }

    public LocalizedReleaseNote ContentFor(UiLanguage language) =>
        language == UiLanguage.SimplifiedChinese ? SimplifiedChinese : English;
}

internal sealed class ReleaseNotesManifestDocument
{
    [JsonPropertyName("schemaVersion")]
    [JsonRequired]
    public int SchemaVersion { get; set; } = ReleaseNotesManifestCodec.SupportedSchemaVersion;

    [JsonPropertyName("releases")]
    [JsonRequired]
    public List<ReleaseNotesManifestRelease> Releases { get; set; } = [];
}

internal sealed class ReleaseNotesManifestRelease
{
    [JsonPropertyName("version")]
    [JsonRequired]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("releaseDate")]
    [JsonRequired]
    public string ReleaseDate { get; set; } = string.Empty;

    [JsonPropertyName("showInApplication")]
    [JsonRequired]
    public bool ShowInApplication { get; set; }

    [JsonPropertyName("locales")]
    [JsonRequired]
    public ReleaseNotesManifestLocales Locales { get; set; } = new();
}

internal sealed class ReleaseNotesManifestLocales
{
    [JsonPropertyName("en")]
    [JsonRequired]
    public ReleaseNotesManifestLocale English { get; set; } = new();

    [JsonPropertyName("zh-CN")]
    [JsonRequired]
    public ReleaseNotesManifestLocale SimplifiedChinese { get; set; } = new();
}

internal sealed class ReleaseNotesManifestLocale
{
    [JsonPropertyName("title")]
    [JsonRequired]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    [JsonRequired]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("sections")]
    [JsonRequired]
    public List<ReleaseNotesManifestSection> Sections { get; set; } = [];
}

internal sealed class ReleaseNotesManifestSection
{
    [JsonPropertyName("heading")]
    [JsonRequired]
    public string Heading { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    [JsonRequired]
    public List<string> Items { get; set; } = [];
}

internal static class ReleaseNotesManifestCodec
{
    public const int SupportedSchemaVersion = 1;
    public const string EmbeddedResourceName = "VectorAnimationEngine.ReleaseNotes.release-notes.json";

    private static readonly Regex StrictVersionPattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$",
        RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions WriteOptions = new(ReadOptions)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true
    };

    public static ReleaseNotesManifestDocument Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The manifest stream must be readable.", nameof(source));

        var document = JsonSerializer.Deserialize<ReleaseNotesManifestDocument>(source, ReadOptions)
            ?? throw new InvalidDataException("The release-note manifest cannot be JSON null.");
        EnsureValid(document);
        return document;
    }

    public static ReleaseNotesManifestDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<ReleaseNotesManifestDocument>(json, ReadOptions)
            ?? throw new InvalidDataException("The release-note manifest cannot be JSON null.");
        EnsureValid(document);
        return document;
    }

    public static void Write(Stream destination, ReleaseNotesManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The manifest stream must be writable.", nameof(destination));
        }

        var canonical = CreateCanonicalDocument(document);
        JsonSerializer.Serialize(destination, canonical, WriteOptions);
    }

    public static string Serialize(ReleaseNotesManifestDocument document)
    {
        var canonical = CreateCanonicalDocument(document);
        return JsonSerializer.Serialize(canonical, WriteOptions);
    }

    public static IReadOnlyList<string> Validate(
        ReleaseNotesManifestDocument document,
        Version? requiredVersion = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();

        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            errors.Add(
                $"schemaVersion must be {SupportedSchemaVersion}; found {document.SchemaVersion.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (document.Releases is null || document.Releases.Count == 0)
        {
            errors.Add("releases must contain at least one release.");
            return Array.AsReadOnly(errors.ToArray());
        }

        var seenVersions = new HashSet<Version>();
        for (var index = 0; index < document.Releases.Count; index++)
        {
            var release = document.Releases[index];
            var path = $"releases[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (release is null)
            {
                errors.Add($"{path} must be an object.");
                continue;
            }

            if (!TryParseStrictVersion(release.Version, out var version))
            {
                errors.Add($"{path}.version must use strict numeric x.y.z form.");
            }
            else
            {
                if (!seenVersions.Add(version)) errors.Add($"{path}.version duplicates {version}.");
            }

            if (!DateOnly.TryParseExact(
                    release.ReleaseDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                errors.Add($"{path}.releaseDate must use yyyy-MM-dd form and contain a valid date.");
            }

            ValidateLocales(release.Locales, path, errors);
        }

        if (requiredVersion is not null && !seenVersions.Contains(NormalizeVersion(requiredVersion)))
        {
            errors.Add($"releases must contain the current application version {FormatVersion(requiredVersion)}.");
        }

        return Array.AsReadOnly(errors.ToArray());
    }

    public static void EnsureValid(
        ReleaseNotesManifestDocument document,
        Version? requiredVersion = null)
    {
        var errors = Validate(document, requiredVersion);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "Release-note manifest validation failed:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }
    }

    internal static bool TryParseStrictVersion(string? value, out Version version)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && StrictVersionPattern.IsMatch(value)
            && Version.TryParse(value, out var parsed)
            && parsed.Build >= 0
            && parsed.Revision < 0)
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static ReleaseNotesManifestDocument CreateCanonicalDocument(ReleaseNotesManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureValid(document);
        return new ReleaseNotesManifestDocument
        {
            SchemaVersion = document.SchemaVersion,
            Releases = document.Releases
                .OrderByDescending(release => Version.Parse(release.Version))
                .ToList()
        };
    }

    private static void ValidateLocales(
        ReleaseNotesManifestLocales? locales,
        string releasePath,
        ICollection<string> errors)
    {
        if (locales is null)
        {
            errors.Add($"{releasePath}.locales must contain en and zh-CN.");
            return;
        }

        ValidateLocale(locales.English, $"{releasePath}.locales.en", errors);
        ValidateLocale(locales.SimplifiedChinese, $"{releasePath}.locales.zh-CN", errors);

        var englishSections = locales.English?.Sections;
        var chineseSections = locales.SimplifiedChinese?.Sections;
        if (englishSections is null || chineseSections is null) return;
        if (englishSections.Count != chineseSections.Count)
        {
            errors.Add($"{releasePath}.locales must have the same section count in en and zh-CN.");
            return;
        }

        for (var index = 0; index < englishSections.Count; index++)
        {
            var englishItemCount = englishSections[index]?.Items?.Count;
            var chineseItemCount = chineseSections[index]?.Items?.Count;
            if (englishItemCount != chineseItemCount)
            {
                errors.Add(
                    $"{releasePath}.locales section {index.ToString(CultureInfo.InvariantCulture)} must have the same item count in en and zh-CN.");
            }
        }
    }

    private static void ValidateLocale(
        ReleaseNotesManifestLocale? locale,
        string path,
        ICollection<string> errors)
    {
        if (locale is null)
        {
            errors.Add($"{path} must be an object.");
            return;
        }

        if (string.IsNullOrWhiteSpace(locale.Title)) errors.Add($"{path}.title must not be empty.");
        if (string.IsNullOrWhiteSpace(locale.Summary)) errors.Add($"{path}.summary must not be empty.");
        if (locale.Sections is null || locale.Sections.Count == 0)
        {
            errors.Add($"{path}.sections must contain at least one section.");
            return;
        }

        for (var index = 0; index < locale.Sections.Count; index++)
        {
            var section = locale.Sections[index];
            var sectionPath = $"{path}.sections[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (section is null)
            {
                errors.Add($"{sectionPath} must be an object.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(section.Heading))
            {
                errors.Add($"{sectionPath}.heading must not be empty.");
            }

            if (section.Items is null || section.Items.Count == 0)
            {
                errors.Add($"{sectionPath}.items must contain at least one item.");
            }
            else if (section.Items.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"{sectionPath}.items must not contain empty items.");
            }
        }
    }

    private static Version NormalizeVersion(Version version) =>
        version.Revision >= 0
            ? new Version(version.Major, version.Minor, version.Build)
            : version;

    private static string FormatVersion(Version version) =>
        $"{version.Major.ToString(CultureInfo.InvariantCulture)}." +
        $"{version.Minor.ToString(CultureInfo.InvariantCulture)}." +
        version.Build.ToString(CultureInfo.InvariantCulture);
}

internal static class ReleaseNotesCatalog
{
    private sealed record CatalogState(
        IReadOnlyList<ReleaseNoteEntry> AllEntries,
        IReadOnlyList<ReleaseNoteEntry> VisibleEntries,
        ReleaseNoteEntry Current);

    private static readonly Version ApplicationVersion = ReadApplicationVersion();
    private static readonly CatalogState State = LoadCatalog(ApplicationVersion);

    public static IReadOnlyList<ReleaseNoteEntry> Entries => State.VisibleEntries;
    public static IReadOnlyList<ReleaseNoteEntry> AllEntries => State.AllEntries;
    public static ReleaseNoteEntry Current => State.Current;
    public static Version CurrentVersion => ApplicationVersion;
    public static ReleaseNoteEntry Latest => State.AllEntries[0];
    public static Version LatestVersion => Latest.Version;

    private static CatalogState LoadCatalog(Version currentVersion)
    {
        var assembly = typeof(ReleaseNotesCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ReleaseNotesManifestCodec.EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Required embedded release-note manifest '{ReleaseNotesManifestCodec.EmbeddedResourceName}' was not found in assembly '{assembly.GetName().Name}'.");

        try
        {
            var document = ReleaseNotesManifestCodec.Read(stream);
            ReleaseNotesManifestCodec.EnsureValid(document, currentVersion);
            var allEntries = document.Releases
                .Select(ToRuntimeEntry)
                .OrderByDescending(entry => entry.Version)
                .ToArray();
            var visibleEntries = allEntries
                .Where(entry => entry.ShowInApplication && entry.Version <= currentVersion)
                .ToArray();
            var current = allEntries.Single(entry => entry.Version == currentVersion);
            return new CatalogState(
                Array.AsReadOnly(allEntries),
                Array.AsReadOnly(visibleEntries),
                current);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Embedded release-note manifest '{ReleaseNotesManifestCodec.EmbeddedResourceName}' is invalid: {exception.Message}",
                exception);
        }
    }

    private static ReleaseNoteEntry ToRuntimeEntry(ReleaseNotesManifestRelease release)
    {
        _ = ReleaseNotesManifestCodec.TryParseStrictVersion(release.Version, out var version);
        _ = DateOnly.TryParseExact(
            release.ReleaseDate,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var releaseDate);
        return new ReleaseNoteEntry(
            version,
            releaseDate,
            release.ShowInApplication,
            ToRuntimeLocale(release.Locales.English),
            ToRuntimeLocale(release.Locales.SimplifiedChinese));
    }

    private static LocalizedReleaseNote ToRuntimeLocale(ReleaseNotesManifestLocale locale) =>
        new(
            locale.Title,
            locale.Summary,
            locale.Sections
                .Select(section => new ReleaseNoteSection(section.Heading, section.Items.ToArray()))
                .ToArray());

    private static Version ReadApplicationVersion()
    {
        var informationalVersion = typeof(ReleaseNotesCatalog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Split('+', 2)[0];
        if (!ReleaseNotesManifestCodec.TryParseStrictVersion(informationalVersion, out var version))
        {
            throw new InvalidOperationException(
                $"Assembly informational version '{informationalVersion ?? "missing"}' must use strict numeric x.y.z form.");
        }

        return version;
    }
}
