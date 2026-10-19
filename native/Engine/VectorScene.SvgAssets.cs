using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    internal static bool IsValidImportedSvgAssetId(string? assetId) =>
        !string.IsNullOrWhiteSpace(assetId)
        && assetId.Length <= VectorProject.MaxExternalSvgAssetPathLength
        && assetId.IndexOf('\0') < 0;

    /// <summary>Associates a whole SVG placement with a shared library asset, retaining its embedded source.</summary>
    public bool TrySetImportedSvgAsset(int objectIndex, string assetId)
    {
        if (!TryGetImportedSvgSource(objectIndex, out _) || !IsValidImportedSvgAssetId(assetId)) return false;
        if (_importedSvgAssetIds.TryGetValue(objectIndex, out var previous)
            && string.Equals(previous, assetId, StringComparison.Ordinal)) return true;
        _importedSvgAssetIds[objectIndex] = assetId;
        GeometryRevision++;
        return true;
    }

    public bool TryGetImportedSvgAssetId(int objectIndex, out string assetId)
    {
        if ((uint)objectIndex < ObjectCount
            && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.ImportedSvg
            && _importedSvgAssetIds.TryGetValue(objectIndex, out assetId!)) return true;
        assetId = string.Empty;
        return false;
    }

    public int[] FindImportedSvgObjectsUsingAsset(string assetId) =>
        string.IsNullOrWhiteSpace(assetId) ? [] : _importedSvgAssetIds
            .Where(item => (uint)item.Key < ObjectCount
                && ShapeKind[item.Key] == VectorAnimationEngine.ShapeKind.ImportedSvg
                && string.Equals(item.Value, assetId, StringComparison.Ordinal))
            .Select(item => item.Key).Order().ToArray();

    /// <summary>Updates shared content without changing placement, transforms, distortions or keyframes.</summary>
    public int UpdateImportedSvgAssetSource(string assetId, string source)
    {
        var updates = PrepareImportedSvgAssetSourceUpdate(assetId, source);
        ApplyImportedSvgAssetSourceUpdate(updates);
        return updates.Count;
    }

    internal Dictionary<int, string> PrepareImportedSvgAssetSourceUpdate(string assetId, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        source = NormalizeImportedSvgAssetSource(source);
        ValidateSvgAssetSourceLength(source);
        var updates = new Dictionary<int, string>();
        foreach (var index in FindImportedSvgObjectsUsingAsset(assetId))
        {
            if (!_importedSvgSources.TryGetValue(index, out var previous))
                throw new InvalidDataException("A shared SVG placement is missing its source.");
            var replacement = ReplaceImportedSvgAssetSource(previous, source, 0);
            if (!string.Equals(previous, replacement, StringComparison.Ordinal)) updates.Add(index, replacement);
        }
        return updates;
    }

    internal void ApplyImportedSvgAssetSourceUpdate(Dictionary<int, string> updates)
    {
        foreach (var (index, source) in updates) _importedSvgSources[index] = source;
        if (updates.Count > 0) GeometryRevision++;
    }

    private const int MaximumSvgAssetSourceCharacters = 8 * 1024 * 1024;
    private const int MaximumSvgAssetWrapperDepth = 16;
    private const string SvgAssetWrapperAttribute = "data-v2d-asset-wrapper";
    private const string SvgDataUriPrefix = "data:image/svg+xml;base64,";

    /// <summary>Removes only explicitly marked engine composition wrappers, leaving user SVG images intact.</summary>
    internal static string ResolveImportedSvgAssetSource(string source)
    {
        for (var depth = 0; ; depth++)
        {
            var wrapper = ReadImportedSvgAssetWrapper(source, depth);
            if (wrapper is null) return NormalizeImportedSvgAssetSource(source);
            source = DecodeImportedSvgAssetWrapper(wrapper.Value.Image);
        }
    }

    private static string ReplaceImportedSvgAssetSource(string previous, string source, int depth)
    {
        var wrapper = ReadImportedSvgAssetWrapper(previous, depth);
        if (wrapper is null) return source;
        var nested = DecodeImportedSvgAssetWrapper(wrapper.Value.Image);
        var replacement = ReplaceImportedSvgAssetSource(nested, source, depth + 1);
        if (string.Equals(nested, replacement, StringComparison.Ordinal)) return previous;
        wrapper.Value.Image.SetAttributeValue("href", SvgDataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(replacement)));
        var result = wrapper.Value.Root.ToString(SaveOptions.DisableFormatting);
        ValidateSvgAssetSourceLength(result);
        return result;
    }

    private static (XElement Root, XElement Image)? ReadImportedSvgAssetWrapper(string source, int depth)
    {
        ValidateSvgAssetSourceLength(source);
        if (!source.Contains(SvgAssetWrapperAttribute, StringComparison.Ordinal)) return null;
        using var input = new StringReader(source);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumSvgAssetSourceCharacters
        });
        var root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
        var marker = root.Attribute(SvgAssetWrapperAttribute);
        if (marker is null) return null;
        XNamespace svg = "http://www.w3.org/2000/svg";
        var elements = root.Elements().ToArray();
        if (depth >= MaximumSvgAssetWrapperDepth || marker.Value != "1"
            || root.Name != svg + "svg" || elements.Length != 1 || elements[0].Name != svg + "image"
            || elements[0].HasElements)
            throw new InvalidDataException("The internal SVG asset wrapper is invalid or too deeply nested.");
        return (root, elements[0]);
    }

    private static string DecodeImportedSvgAssetWrapper(XElement image)
    {
        var href = (string?)image.Attribute("href") ?? "";
        if (!href.StartsWith(SvgDataUriPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("The internal SVG asset wrapper has no embedded SVG source.");
        try
        {
            var result = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(href[SvgDataUriPrefix.Length..]));
            ValidateSvgAssetSourceLength(result);
            return result;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException("The internal SVG asset wrapper contains invalid embedded source.", exception);
        }
    }

    private static void ValidateSvgAssetSourceLength(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > MaximumSvgAssetSourceCharacters)
            throw new InvalidDataException("The SVG asset source is empty or exceeds the supported size limit.");
    }

    internal static string NormalizeImportedSvgAssetSource(string source)
    {
        if (source.Length > 0 && source[0] == '\uFEFF') source = source[1..];
        return source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
