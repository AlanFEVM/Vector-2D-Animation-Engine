using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal static class DrawingObjectSvgCodec
{
    private const int MinimumReadableFormatVersion = 1;
    private const int CurrentFormatVersion = 5;
    private const string MetadataId = "v2d-metadata";
    private const string SvgVersion = "1.1";
    private const string StageViewBox = "-24000 -14000 48000 28000";
    private const string LegacyMetadataEncoding = "base64-json";
    private const string CompressedMetadataEncoding = "base64-brotli-json";
    private const int MaxLayersPerAsset = 100_000;
    private const int MaxObjectsPerAsset = 250_000;
    private const long MaxPointsPerAsset = 2_000_000;
    private const int MaxImportedSvgSourceCharacters = 8 * 1024 * 1024;
    private const long MaxImportedSvgSourceCharactersPerAsset = 8L * 1024 * 1024;
    private const int MaxImportedSvgNameCharacters = 80;
    private const int MaxTimelineTabGroupNameLength = 80;
    private const int MaxDistortionWarpsPerObject = 256;
    private const long MaxSvgFileBytes = 128L * 1024 * 1024;
    private const long MaxSvgCharacters = 128L * 1024 * 1024;
    private const int MaxMetadataDecodedBytes = 96 * 1024 * 1024;
    private const int MaxMetadataEncodedBytes = 96 * 1024 * 1024;
    private const int MaxMetadataBase64Characters = 128 * 1024 * 1024;
    private static readonly XNamespace SvgNamespace = "http://www.w3.org/2000/svg";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        MaxDepth = 256
    };

    internal static void Write(string path, string drawingObjectId, VectorSceneSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(drawingObjectId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot, CurrentFormatVersion);

        var envelope = new MetadataEnvelope
        {
            Version = CurrentFormatVersion,
            DrawingObjectId = drawingObjectId,
            Snapshot = snapshot
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (json.Length > MaxMetadataDecodedBytes)
        {
            throw new InvalidDataException("The symbol SVG metadata exceeds the supported decoded size limit.");
        }

        var compressed = ProjectPayloadCompression.Compress(json);
        if (compressed.Length > MaxMetadataEncodedBytes)
        {
            throw new InvalidDataException("The symbol SVG metadata exceeds the supported encoded size limit.");
        }

        var metadata = Convert.ToBase64String(compressed);
        if (metadata.Length > MaxMetadataBase64Characters)
        {
            throw new InvalidDataException("The symbol SVG metadata exceeds the supported Base64 size limit.");
        }
        var root = new XElement(
            SvgNamespace + "svg",
            new XAttribute("version", SvgVersion),
            new XAttribute("viewBox", StageViewBox),
            new XAttribute("data-v2d-format-version", CurrentFormatVersion),
            new XAttribute("data-v2d-drawing-object-id", drawingObjectId),
            new XElement(SvgNamespace + "title", drawingObjectId),
            new XElement(
                SvgNamespace + "metadata",
                new XAttribute("id", MetadataId),
                new XAttribute("data-v2d-format-version", CurrentFormatVersion),
                new XAttribute("data-encoding", CompressedMetadataEncoding),
                metadata));

        AddPreview(root, snapshot);
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            NewLineHandling = NewLineHandling.None
        };
        using var writer = XmlWriter.Create(path, settings);
        document.Save(writer);
    }

    internal static VectorSceneSnapshot Read(string path, string expectedDrawingObjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDrawingObjectId);

        try
        {
            if (new FileInfo(path).Length > MaxSvgFileBytes)
            {
                throw new InvalidDataException("The symbol SVG exceeds the supported per-file size limit.");
            }
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxSvgCharacters
            };
            using var reader = XmlReader.Create(path, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;
            if (root is null || root.Name != SvgNamespace + "svg")
            {
                throw new InvalidDataException("The symbol asset is not an SVG document.");
            }
            if (!string.Equals((string?)root.Attribute("version"), SvgVersion, StringComparison.Ordinal)
                || !string.Equals((string?)root.Attribute("viewBox"), StageViewBox, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The symbol SVG root has an unsupported version or view box.");
            }
            if (!TryParseVersion((string?)root.Attribute("data-v2d-format-version"), out var rootVersion)
                || !IsReadableFormatVersion(rootVersion))
            {
                throw new InvalidDataException("The symbol SVG format version is unsupported.");
            }
            if (!string.Equals(
                    (string?)root.Attribute("data-v2d-drawing-object-id"),
                    expectedDrawingObjectId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The symbol SVG asset identifier does not match the project manifest.");
            }

            var metadataElements = root
                .Elements(SvgNamespace + "metadata")
                .Where(element => string.Equals((string?)element.Attribute("id"), MetadataId, StringComparison.Ordinal))
                .ToArray();
            if (metadataElements.Length != 1)
            {
                throw new InvalidDataException("The symbol SVG must contain exactly one V2D metadata element.");
            }

            var metadataElement = metadataElements[0];
            if (!TryParseVersion((string?)metadataElement.Attribute("data-v2d-format-version"), out var metadataVersion)
                || metadataVersion != rootVersion
                || !string.Equals(
                    (string?)metadataElement.Attribute("data-encoding"),
                    MetadataEncodingForVersion(rootVersion),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The symbol SVG metadata format is unsupported.");
            }

            var encoded = metadataElement.Value.Trim();
            if (encoded.Length == 0) throw new InvalidDataException("The symbol SVG metadata is empty.");
            if (encoded.Length > MaxMetadataBase64Characters)
            {
                throw new InvalidDataException("The symbol SVG metadata exceeds the supported Base64 size limit.");
            }

            var payload = Convert.FromBase64String(encoded);
            if (payload.Length > MaxMetadataEncodedBytes)
            {
                throw new InvalidDataException("The symbol SVG metadata exceeds the supported encoded size limit.");
            }

            var json = rootVersion == CurrentFormatVersion
                ? ProjectPayloadCompression.Decompress(payload, MaxMetadataDecodedBytes)
                : payload;
            if (json.Length > MaxMetadataDecodedBytes)
            {
                throw new InvalidDataException("The symbol SVG metadata exceeds the supported decoded size limit.");
            }

            var envelope = JsonSerializer.Deserialize<MetadataEnvelope>(json, JsonOptions);
            if (envelope is null
                || envelope.Version != rootVersion
                || !string.Equals(envelope.DrawingObjectId, expectedDrawingObjectId, StringComparison.Ordinal)
                || envelope.Snapshot is null)
            {
                throw new InvalidDataException("The symbol SVG metadata does not match the requested asset.");
            }

            ValidateSnapshot(envelope.Snapshot, rootVersion);
            return envelope.Snapshot;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is XmlException or JsonException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The symbol SVG metadata is malformed.", ex);
        }
    }

    private static void AddPreview(XElement root, VectorSceneSnapshot snapshot)
    {
        var objectIndices = PreviewObjectIndices(snapshot);
        var definitions = new XElement(SvgNamespace + "defs");
        foreach (var index in objectIndices)
        {
            if (CreateGradient(snapshot, index) is { } gradient) definitions.Add(gradient);
        }
        if (definitions.HasElements) root.Add(definitions);

        var preview = new XElement(
            SvgNamespace + "g",
            new XAttribute("id", "v2d-preview"),
            new XAttribute("data-v2d-preview-frame", PreviewFrame(snapshot)));
        foreach (var index in objectIndices)
        {
            if (CreatePreviewElement(snapshot, index) is { } element) preview.Add(element);
        }
        root.Add(preview);
    }

    private static int[] PreviewObjectIndices(VectorSceneSnapshot snapshot)
    {
        var frame = PreviewFrame(snapshot);
        return Enumerable.Range(0, snapshot.ObjectCount)
            .Where(index => snapshot.ObjectKeyframeFrame[index] == frame)
            .Where(index => snapshot.LayerVisible[snapshot.ObjectLayer[index]])
            .Where(index => snapshot.LayerKinds[snapshot.ObjectLayer[index]] != DrawingLayerKind.Folder)
            .OrderByDescending(index => snapshot.ObjectLayer[index])
            .ThenBy(index => snapshot.ObjectOrder[index])
            .ThenBy(index => snapshot.ObjectSubOrder[index])
            .ThenBy(index => index)
            .ToArray();
    }

    private static int PreviewFrame(VectorSceneSnapshot snapshot)
    {
        var visibleFrames = Enumerable.Range(0, snapshot.ObjectCount)
            .Where(index => snapshot.LayerVisible[snapshot.ObjectLayer[index]])
            .Where(index => snapshot.LayerKinds[snapshot.ObjectLayer[index]] != DrawingLayerKind.Folder)
            .Select(index => snapshot.ObjectKeyframeFrame[index])
            .ToArray();
        return visibleFrames.Length > 0
            ? visibleFrames.Min()
            : snapshot.ObjectCount == 0
                ? 0
                : snapshot.ObjectKeyframeFrame.Min();
    }

    /// <summary>
    /// Builds the paint-ready SVG element for one snapshot object. Exposed so
    /// <see cref="DrawingObjectSvgExport"/> can reuse the exact same geometry and
    /// paint emission as the stored preview instead of duplicating it.
    /// </summary>
    internal static XElement? CreateExportElement(VectorSceneSnapshot snapshot, int index) =>
        CreatePreviewElement(snapshot, index);

    /// <summary>
    /// Builds the gradient definition for one snapshot object, or null when the
    /// object does not use a supported gradient.
    /// </summary>
    internal static XElement? CreateExportGradient(VectorSceneSnapshot snapshot, int index) =>
        CreateGradient(snapshot, index);

    private static XElement? CreatePreviewElement(VectorSceneSnapshot snapshot, int index)
    {
        var shape = snapshot.ShapeKind[index];
        XElement? element = shape switch
        {
            ShapeKind.Rectangle => new XElement(
                SvgNamespace + "rect",
                new XAttribute("x", Number(snapshot.X[index] - snapshot.Width[index] * 0.5f)),
                new XAttribute("y", Number(snapshot.Y[index] - snapshot.Height[index] * 0.5f)),
                new XAttribute("width", Number(snapshot.Width[index])),
                new XAttribute("height", Number(snapshot.Height[index])),
                RotationAttribute(snapshot, index)),
            ShapeKind.Ellipse => new XElement(
                SvgNamespace + "ellipse",
                new XAttribute("cx", Number(snapshot.X[index])),
                new XAttribute("cy", Number(snapshot.Y[index])),
                new XAttribute("rx", Number(snapshot.Width[index] * 0.5f)),
                new XAttribute("ry", Number(snapshot.Height[index] * 0.5f)),
                RotationAttribute(snapshot, index)),
            ShapeKind.Triangle => CreatePolygon(snapshot, index, 3, star: false),
            ShapeKind.Polygon => CreatePolygon(snapshot, index, Math.Clamp(snapshot.ShapeVertexCounts[index], 3, 64), star: false),
            ShapeKind.Star => CreatePolygon(snapshot, index, Math.Clamp(snapshot.ShapeVertexCounts[index], 3, 32), star: true),
            ShapeKind.Line => CreateLinePath(snapshot, index),
            ShapeKind.Path => CreateCompoundPath(snapshot, index),
            ShapeKind.Freeform or ShapeKind.BrushStroke => CreateFreehandPath(snapshot, index),
            ShapeKind.ImportedSvg => CreateImportedSvgImage(snapshot, index),
            ShapeKind.Bitmap => CreateBitmapPlaceholder(snapshot, index),
            ShapeKind.Text => CreateTextPath(snapshot, index),
            ShapeKind.MixingStroke => CreateMixingStrokePreview(snapshot, index),
            _ => null
        };
        if (element is null) return null;

        element.SetAttributeValue("id", $"v2d-object-{index.ToString(CultureInfo.InvariantCulture)}");
        element.SetAttributeValue("data-v2d-object-index", index.ToString(CultureInfo.InvariantCulture));
        if (shape is not ShapeKind.ImportedSvg and not ShapeKind.MixingStroke and not ShapeKind.Bitmap)
        {
            ApplyPaint(element, snapshot, index);
        }
        var opacity = snapshot.LayerOpacity[snapshot.ObjectLayer[index]];
        if (opacity < 1f) element.SetAttributeValue("opacity", Number(opacity));
        return element;
    }

    private static XElement? CreateImportedSvgImage(VectorSceneSnapshot snapshot, int index)
    {
        if (!snapshot.ImportedSvgSources.TryGetValue(index, out var source)
            || string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
        return new XElement(
            SvgNamespace + "image",
            new XAttribute("x", Number(snapshot.X[index] - snapshot.Width[index] * 0.5f)),
            new XAttribute("y", Number(snapshot.Y[index] - snapshot.Height[index] * 0.5f)),
            new XAttribute("width", Number(snapshot.Width[index])),
            new XAttribute("height", Number(snapshot.Height[index])),
            new XAttribute("preserveAspectRatio", "none"),
            new XAttribute("href", $"data:image/svg+xml;base64,{encoded}"),
            RotationAttribute(snapshot, index));
    }

    /// <summary>
    /// Symbol SVG previews are geometry-only documents that deliberately do not embed the
    /// project's image library. A placed bitmap therefore degrades to an explicitly marked
    /// placeholder frame instead of silently disappearing from the preview.
    /// </summary>
    private static XElement? CreateBitmapPlaceholder(VectorSceneSnapshot snapshot, int index)
    {
        if (!snapshot.BitmapObjects.TryGetValue(index, out var data) || data is null || !data.IsValid)
        {
            return null;
        }

        return new XElement(
            SvgNamespace + "rect",
            new XAttribute("x", Number(snapshot.X[index] - snapshot.Width[index] * 0.5f)),
            new XAttribute("y", Number(snapshot.Y[index] - snapshot.Height[index] * 0.5f)),
            new XAttribute("width", Number(snapshot.Width[index])),
            new XAttribute("height", Number(snapshot.Height[index])),
            new XAttribute("fill", "none"),
            new XAttribute("stroke", "#7a7f87"),
            new XAttribute("stroke-width", "2"),
            new XAttribute("data-v2d-bitmap-placeholder", data.ImageAssetId),
            RotationAttribute(snapshot, index));
    }

    private static XAttribute RotationAttribute(VectorSceneSnapshot snapshot, int index)
    {
        var degrees = snapshot.Angle[index] * 180f / MathF.PI;
        return new XAttribute(
            "transform",
            $"rotate({Number(degrees)} {Number(snapshot.X[index])} {Number(snapshot.Y[index])})");
    }

    private static XElement CreatePolygon(VectorSceneSnapshot snapshot, int index, int vertexCount, bool star)
    {
        var count = star ? vertexCount * 2 : vertexCount;
        var points = new PointF[count];
        for (var pointIndex = 0; pointIndex < count; pointIndex++)
        {
            var radius = star && pointIndex % 2 != 0 ? 0.46f : 1f;
            var angle = -MathF.PI / 2 + pointIndex * MathF.Tau / count;
            points[pointIndex] = LocalToWorld(
                snapshot,
                index,
                MathF.Cos(angle) * snapshot.Width[index] * 0.5f * radius,
                MathF.Sin(angle) * snapshot.Height[index] * 0.5f * radius);
        }

        return new XElement(SvgNamespace + "polygon", new XAttribute("points", Points(points)));
    }

    private static XElement CreateLinePath(VectorSceneSnapshot snapshot, int index)
    {
        var start = LocalToWorld(snapshot, index, -snapshot.Width[index] * 0.5f, 0);
        var end = LocalToWorld(snapshot, index, snapshot.Width[index] * 0.5f, 0);
        var data = $"M {Point(start)} C {Number(snapshot.CurveControlX[index])} {Number(snapshot.CurveControlY[index])} "
            + $"{Number(snapshot.CurveControl2X[index])} {Number(snapshot.CurveControl2Y[index])} {Point(end)}";
        return new XElement(SvgNamespace + "path", new XAttribute("d", data));
    }

    private static XElement? CreateCompoundPath(VectorSceneSnapshot snapshot, int index)
    {
        if (snapshot.PathBezierLocalContours.TryGetValue(index, out var bezierContours)
            && bezierContours.Length > 0)
        {
            return CreateCompoundBezierPath(snapshot, index, bezierContours);
        }
        if (!snapshot.PathLocalContours.TryGetValue(index, out var contours) || contours.Length == 0) return null;
        var worldContours = contours
            .Select(contour => contour
                .Select(point => LocalToWorld(snapshot, index, point.X, point.Y))
                .ToArray())
            .ToArray();
        return CreateCompoundPath(worldContours);
    }

    private static XElement? CreateCompoundBezierPath(
        VectorSceneSnapshot snapshot,
        int index,
        IReadOnlyList<PathBezierNode[]> contours)
    {
        var data = new StringBuilder();
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            var first = contour[0];
            data.Append("M ").Append(Point(LocalToWorld(snapshot, index, first.Anchor.X, first.Anchor.Y)));
            for (var segmentIndex = 0; segmentIndex < contour.Length; segmentIndex++)
            {
                var current = contour[segmentIndex];
                var next = contour[(segmentIndex + 1) % contour.Length];
                data.Append(" C ")
                    .Append(Point(LocalToWorld(snapshot, index, current.OutgoingControl.X, current.OutgoingControl.Y)))
                    .Append(' ')
                    .Append(Point(LocalToWorld(snapshot, index, next.IncomingControl.X, next.IncomingControl.Y)))
                    .Append(' ')
                    .Append(Point(LocalToWorld(snapshot, index, next.Anchor.X, next.Anchor.Y)));
            }
            data.Append(" Z ");
        }

        return data.Length == 0
            ? null
            : new XElement(
                SvgNamespace + "path",
                new XAttribute("d", data.ToString().TrimEnd()),
                new XAttribute("fill-rule", "evenodd"));
    }

    private static XElement? CreateTextPath(VectorSceneSnapshot snapshot, int index)
    {
        if (!snapshot.TextObjects.TryGetValue(index, out var data)
            || !TextGeometry.TryCreateWorldContours(
                data,
                new PointF(snapshot.X[index], snapshot.Y[index]),
                new SizeF(snapshot.Width[index], snapshot.Height[index]),
                snapshot.Angle[index],
                out var contours))
        {
            return null;
        }

        return CreateCompoundPath(contours);
    }

    private static XElement? CreateCompoundPath(IReadOnlyList<PointF[]> contours)
    {
        var data = new StringBuilder();
        foreach (var contour in contours)
        {
            if (contour.Length == 0) continue;
            data.Append("M ").Append(Point(contour[0]));
            for (var pointIndex = 1; pointIndex < contour.Length; pointIndex++)
            {
                data.Append(" L ").Append(Point(contour[pointIndex]));
            }
            data.Append(" Z ");
        }
        return data.Length == 0
            ? null
            : new XElement(
                SvgNamespace + "path",
                new XAttribute("d", data.ToString().TrimEnd()),
                new XAttribute("fill-rule", "evenodd"));
    }

    private static XElement? CreateFreehandPath(VectorSceneSnapshot snapshot, int index)
    {
        if (snapshot.ShapeKind[index] == ShapeKind.Freeform
            && snapshot.FreehandBezierLocalNodes.TryGetValue(index, out var nodes)
            && nodes.Length >= 2)
        {
            var curveData = new StringBuilder();
            var firstNode = nodes[0];
            curveData.Append("M ").Append(Point(LocalToWorld(
                snapshot,
                index,
                firstNode.Anchor.X,
                firstNode.Anchor.Y)));
            for (var segmentIndex = 0; segmentIndex < nodes.Length - 1; segmentIndex++)
            {
                var current = nodes[segmentIndex];
                var next = nodes[segmentIndex + 1];
                curveData.Append(" C ")
                    .Append(Point(LocalToWorld(snapshot, index, current.OutgoingControl.X, current.OutgoingControl.Y)))
                    .Append(' ')
                    .Append(Point(LocalToWorld(snapshot, index, next.IncomingControl.X, next.IncomingControl.Y)))
                    .Append(' ')
                    .Append(Point(LocalToWorld(snapshot, index, next.Anchor.X, next.Anchor.Y)));
            }
            return new XElement(SvgNamespace + "path", new XAttribute("d", curveData.ToString()));
        }

        if (!snapshot.FreehandLocalPoints.TryGetValue(index, out var points) || points.Length == 0) return null;
        var data = new StringBuilder();
        var first = LocalToWorld(snapshot, index, points[0].X, points[0].Y);
        data.Append("M ").Append(Point(first));
        for (var pointIndex = 1; pointIndex < points.Length; pointIndex++)
        {
            var point = LocalToWorld(snapshot, index, points[pointIndex].X, points[pointIndex].Y);
            data.Append(" L ").Append(Point(point));
        }
        if (snapshot.ShapeKind[index] == ShapeKind.BrushStroke) data.Append(" Z");
        return new XElement(SvgNamespace + "path", new XAttribute("d", data.ToString()));
    }

    private static XElement? CreateMixingStrokePreview(VectorSceneSnapshot snapshot, int index)
    {
        if (snapshot.MixingStrokeLocalRegions.TryGetValue(index, out var region))
        {
            var regionGroup = new XElement(SvgNamespace + "g");
            for (var triangle = 0; triangle < region.TriangleIndices.Length; triangle += 3)
            {
                var first = region.Vertices[region.TriangleIndices[triangle]];
                var second = region.Vertices[region.TriangleIndices[triangle + 1]];
                var third = region.Vertices[region.TriangleIndices[triangle + 2]];
                var color = MixingBrushRegionData.InterpolatePremultipliedLinear(
                    first.Argb,
                    second.Argb,
                    third.Argb,
                    new PointF(1f / 3f, 1f / 3f));
                if (color.A == 0) continue;

                var polygon = new XElement(
                    SvgNamespace + "polygon",
                    new XAttribute("points", Points(new[]
                    {
                        LocalToWorld(snapshot, index, first.Point.X, first.Point.Y),
                        LocalToWorld(snapshot, index, second.Point.X, second.Point.Y),
                        LocalToWorld(snapshot, index, third.Point.X, third.Point.Y)
                    })),
                    new XAttribute("fill", $"#{color.R:X2}{color.G:X2}{color.B:X2}"),
                    new XAttribute("stroke", "none"));
                if (color.A < byte.MaxValue)
                {
                    polygon.SetAttributeValue("fill-opacity", Number(color.A / 255f));
                }
                regionGroup.Add(polygon);
            }
            return regionGroup.HasElements ? regionGroup : null;
        }

        if (!snapshot.MixingStrokeLocalSamples.TryGetValue(index, out var samples)
            || samples.Length == 0)
        {
            return null;
        }

        var group = new XElement(SvgNamespace + "g");
        foreach (var sample in samples)
        {
            if (!Finite(sample.Point.X, sample.Point.Y, sample.Diameter) || sample.Diameter <= 0) continue;
            var point = LocalToWorld(snapshot, index, sample.Point.X, sample.Point.Y);
            var color = Color.FromArgb(sample.Argb);
            if (color.A == 0) continue;
            var circle = new XElement(
                SvgNamespace + "circle",
                new XAttribute("cx", Number(point.X)),
                new XAttribute("cy", Number(point.Y)),
                new XAttribute("r", Number(sample.Diameter * 0.5f)),
                new XAttribute("fill", $"#{color.R:X2}{color.G:X2}{color.B:X2}"),
                new XAttribute("stroke", "none"));
            if (color.A < byte.MaxValue)
            {
                circle.SetAttributeValue("fill-opacity", Number(color.A / 255f));
            }
            group.Add(circle);
        }
        return group.HasElements ? group : null;
    }

    private static void ApplyPaint(XElement element, VectorSceneSnapshot snapshot, int index)
    {
        var strokeOnly = snapshot.ShapeKind[index] is ShapeKind.Line or ShapeKind.Freeform;
        if (strokeOnly)
        {
            element.SetAttributeValue("fill", "none");
            ApplyPaintValue(element, "stroke", snapshot, index, snapshot.StrokeArgb[index], useGradient: snapshot.ShapeKind[index] == ShapeKind.Line);
        }
        else
        {
            ApplyPaintValue(element, "fill", snapshot, index, snapshot.Argb[index], useGradient: true);
            if (snapshot.ShapeKind[index] != ShapeKind.BrushStroke && snapshot.Stroke[index] > 0)
            {
                ApplyPaintValue(element, "stroke", snapshot, index, snapshot.StrokeArgb[index], useGradient: false);
            }
            else
            {
                element.SetAttributeValue("stroke", "none");
            }
        }

        if (snapshot.Stroke[index] > 0)
        {
            element.SetAttributeValue("stroke-width", Number(snapshot.Stroke[index]));
            element.SetAttributeValue("stroke-linejoin", "round");
            var roundStart = snapshot.LineEndpointStyles[index] == LineEndpointStyle.Round;
            var roundEnd = snapshot.LineEndEndpointStyles[index] == LineEndpointStyle.Round;
            element.SetAttributeValue("stroke-linecap", roundStart && roundEnd ? "round" : "butt");
        }
    }

    private static void ApplyPaintValue(
        XElement element,
        string attribute,
        VectorSceneSnapshot snapshot,
        int index,
        int argb,
        bool useGradient)
    {
        if (useGradient && HasPreviewGradient(snapshot, index))
        {
            element.SetAttributeValue(attribute, $"url(#v2d-gradient-{index.ToString(CultureInfo.InvariantCulture)})");
            return;
        }

        var color = Color.FromArgb(argb);
        element.SetAttributeValue(attribute, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
        if (color.A < byte.MaxValue)
        {
            element.SetAttributeValue($"{attribute}-opacity", Number(color.A / 255f));
        }
    }

    private static XElement? CreateGradient(VectorSceneSnapshot snapshot, int index)
    {
        if (!HasPreviewGradient(snapshot, index)) return null;
        var kind = snapshot.GradientKinds[index];
        XElement gradient;
        if (kind == GradientKind.Linear)
        {
            gradient = new XElement(
                SvgNamespace + "linearGradient",
                new XAttribute("x1", Number(snapshot.GradientStartX[index])),
                new XAttribute("y1", Number(snapshot.GradientStartY[index])),
                new XAttribute("x2", Number(snapshot.GradientEndX[index])),
                new XAttribute("y2", Number(snapshot.GradientEndY[index])));
        }
        else
        {
            var dx = snapshot.GradientEndX[index] - snapshot.GradientStartX[index];
            var dy = snapshot.GradientEndY[index] - snapshot.GradientStartY[index];
            var radius = Math.Max(0.001f, MathF.Sqrt(dx * dx + dy * dy));
            gradient = new XElement(
                SvgNamespace + "radialGradient",
                new XAttribute("cx", Number(snapshot.GradientStartX[index])),
                new XAttribute("cy", Number(snapshot.GradientStartY[index])),
                new XAttribute("r", Number(radius)));
        }

        gradient.SetAttributeValue("id", $"v2d-gradient-{index.ToString(CultureInfo.InvariantCulture)}");
        gradient.SetAttributeValue("gradientUnits", "userSpaceOnUse");
        var stops = snapshot.GradientStops.TryGetValue(index, out var stored) && stored.Length > 0
            ? stored
            : new[]
            {
                new GradientStop(0, snapshot.GradientStartArgb[index]),
                new GradientStop(1, snapshot.GradientEndArgb[index])
            };
        foreach (var stop in stops)
        {
            var color = Color.FromArgb(stop.Argb);
            var element = new XElement(
                SvgNamespace + "stop",
                new XAttribute("offset", Number(Math.Clamp(stop.Position, 0f, 1f))),
                new XAttribute("stop-color", $"#{color.R:X2}{color.G:X2}{color.B:X2}"));
            if (color.A < byte.MaxValue) element.SetAttributeValue("stop-opacity", Number(color.A / 255f));
            gradient.Add(element);
        }
        return gradient;
    }

    private static bool HasPreviewGradient(VectorSceneSnapshot snapshot, int index)
    {
        return snapshot.ShapeKind[index] != ShapeKind.Text
            && snapshot.LinearGradientEnabled[index]
            && snapshot.GradientKinds[index] is GradientKind.Linear or GradientKind.Radial or GradientKind.ShapeRadial;
    }

    private static PointF LocalToWorld(VectorSceneSnapshot snapshot, int index, float localX, float localY)
    {
        var cosine = MathF.Cos(snapshot.Angle[index]);
        var sine = MathF.Sin(snapshot.Angle[index]);
        return new PointF(
            snapshot.X[index] + localX * cosine - localY * sine,
            snapshot.Y[index] + localX * sine + localY * cosine);
    }

    private static string Points(IEnumerable<PointF> points) => string.Join(" ", points.Select(Point));

    private static string Point(PointF point) => $"{Number(point.X)},{Number(point.Y)}";

    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool TryParseVersion(string? value, out int version)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    private static bool IsReadableFormatVersion(int version) =>
        version >= MinimumReadableFormatVersion && version <= CurrentFormatVersion;

    private static string MetadataEncodingForVersion(int version) =>
        version == CurrentFormatVersion ? CompressedMetadataEncoding : LegacyMetadataEncoding;

    private static void ValidateSnapshot(VectorSceneSnapshot snapshot, int formatVersion)
    {
        if (snapshot.LayerCount <= 0 || snapshot.LayerCount > MaxLayersPerAsset
            || snapshot.ObjectCount < 0 || snapshot.ObjectCount > MaxObjectsPerAsset
            || snapshot.VirtualAtomCount < 0)
        {
            throw new InvalidDataException("The symbol scene counts are invalid.");
        }
        if ((uint)snapshot.ActiveLayer >= snapshot.LayerCount || !FiniteNonNegative(snapshot.MaxHalfExtent))
        {
            throw new InvalidDataException("The symbol scene state is invalid.");
        }

        ValidateArray(snapshot.LayerIds, snapshot.LayerCount, nameof(snapshot.LayerIds));
        ValidateArray(snapshot.LayerNames, snapshot.LayerCount, nameof(snapshot.LayerNames));
        ValidateArray(snapshot.LayerKinds, snapshot.LayerCount, nameof(snapshot.LayerKinds));
        ValidateArray(snapshot.LayerParentIds, snapshot.LayerCount, nameof(snapshot.LayerParentIds));
        ValidateArray(snapshot.LayerMaskIds, snapshot.LayerCount, nameof(snapshot.LayerMaskIds));
        ValidateArray(snapshot.LayerLocked, snapshot.LayerCount, nameof(snapshot.LayerLocked));
        ValidateArray(snapshot.LayerVisible, snapshot.LayerCount, nameof(snapshot.LayerVisible));
        ValidateArray(snapshot.LayerOpacity, snapshot.LayerCount, nameof(snapshot.LayerOpacity));
        ValidateOptionalArray(snapshot.LayerBlendModes, snapshot.LayerCount, nameof(snapshot.LayerBlendModes));
        ValidateArray(snapshot.LayerColorArgb, snapshot.LayerCount, nameof(snapshot.LayerColorArgb));
        ValidateOptionalArray(snapshot.LayerOutline, snapshot.LayerCount, nameof(snapshot.LayerOutline));
        ValidateArray(snapshot.LayerOnionSkin, snapshot.LayerCount, nameof(snapshot.LayerOnionSkin));
        ValidateArray(snapshot.LayerStart, snapshot.LayerCount, nameof(snapshot.LayerStart));
        ValidateArray(snapshot.LayerEnd, snapshot.LayerCount, nameof(snapshot.LayerEnd));
        if (snapshot.LayerIds.Any(string.IsNullOrWhiteSpace)
            || snapshot.LayerIds.Distinct(StringComparer.Ordinal).Count() != snapshot.LayerCount
            || snapshot.LayerNames.Any(value => value is null)
            || snapshot.LayerParentIds.Any(value => value is null)
            || snapshot.LayerMaskIds.Any(value => value is null)
            || snapshot.LayerKinds.Any(kind => !Enum.IsDefined(kind))
            || snapshot.LayerBlendModes.Any(mode => !Enum.IsDefined(mode))
            || snapshot.OnionSkinEnabled is { } onionSkinEnabled
                && snapshot.LayerOnionSkin.Any(enabled => enabled != onionSkinEnabled)
            || snapshot.LayerOpacity.Any(value => !float.IsFinite(value) || value < 0f || value > 1f)
            || snapshot.LayerStart.Any(value => value < 0)
            || snapshot.LayerEnd.Select((value, index) => value < -1 || value >= 0 && value < snapshot.LayerStart[index]).Any(invalid => invalid))
        {
            throw new InvalidDataException("The symbol layer metadata is invalid.");
        }

        ValidateObjectArrays(snapshot);
        for (var index = 0; index < snapshot.ObjectCount; index++)
        {
            if (snapshot.ObjectLayer[index] >= snapshot.LayerCount
                || snapshot.ObjectKeyframeFrame[index] < 0
                || !double.IsFinite(snapshot.ObjectSubOrder[index])
                || !Finite(snapshot.X[index], snapshot.Y[index], snapshot.Width[index], snapshot.Height[index], snapshot.Angle[index], snapshot.Stroke[index])
                || snapshot.Width[index] < 0
                || snapshot.Height[index] < 0
                || snapshot.Stroke[index] < 0
                || !Finite(snapshot.CurveControlX[index], snapshot.CurveControlY[index], snapshot.CurveControl2X[index], snapshot.CurveControl2Y[index])
                || !Enum.IsDefined(snapshot.LineEndpointStyles[index])
                || !Enum.IsDefined(snapshot.LineEndEndpointStyles[index])
                || !Enum.IsDefined(snapshot.ShapeKind[index])
                || !Enum.IsDefined(snapshot.GradientKinds[index])
                || !Finite(
                    snapshot.GradientStartX[index],
                    snapshot.GradientStartY[index],
                    snapshot.GradientEndX[index],
                    snapshot.GradientEndY[index]))
            {
                throw new InvalidDataException($"Drawing-object metadata for object {index} is invalid.");
            }
        }

        ValidateGradientStops(snapshot);
        ValidatePointDictionary(snapshot.GradientPathLocalPoints, snapshot.ObjectCount, nameof(snapshot.GradientPathLocalPoints));
        ValidateContourDictionary(snapshot.ShapeGradientMappingLocalContours, snapshot.ObjectCount, nameof(snapshot.ShapeGradientMappingLocalContours));
        ValidateContourDictionary(snapshot.PathLocalContours, snapshot.ObjectCount, nameof(snapshot.PathLocalContours));
        ValidatePathBezierContours(snapshot);
        ValidatePointDictionary(snapshot.FreehandLocalPoints, snapshot.ObjectCount, nameof(snapshot.FreehandLocalPoints));
        ValidateFreehandBezierNodes(snapshot);
        ValidateMixingStrokePayloads(snapshot, formatVersion);
        ValidateImportedSvgSources(snapshot);
        ValidateBitmapObjects(snapshot);
        ValidateTextObjects(snapshot);
        ValidateObjectDistortions(snapshot, formatVersion);
        var pointCount = CountPoints(snapshot.GradientPathLocalPoints)
            + CountPoints(snapshot.FreehandLocalPoints)
            + snapshot.MixingStrokeLocalSamples.Values.Sum(samples => (long)samples.Length)
            + snapshot.MixingStrokeLocalRegions.Values.Sum(region =>
                (long)(region.Vertices?.Length ?? 0) + (region.TriangleIndices?.Length ?? 0))
            + CountPoints(snapshot.ShapeGradientMappingLocalContours)
            + CountPoints(snapshot.PathLocalContours)
            + CountPoints(snapshot.PathBezierLocalContours) * 3
            + CountPoints(snapshot.FreehandBezierLocalNodes) * 3
            + CountDistortionPoints(snapshot.ObjectDistortions);
        if (pointCount > MaxPointsPerAsset)
        {
            throw new InvalidDataException("The symbol asset exceeds the supported point count.");
        }
        ValidateTimeline(snapshot.Timeline, formatVersion);
    }

    private static void ValidateMixingStrokePayloads(VectorSceneSnapshot snapshot, int formatVersion)
    {
        if (snapshot.MixingStrokeLocalSamples is null
            || snapshot.MixingStrokeLocalRegions is null)
        {
            throw new InvalidDataException("Mixing-stroke metadata is missing.");
        }
        if (formatVersion < 2 && snapshot.MixingStrokeLocalRegions.Count > 0)
        {
            throw new InvalidDataException("Mixing-stroke region metadata requires SVG format version 2.");
        }

        foreach (var (index, samples) in snapshot.MixingStrokeLocalSamples)
        {
            if ((uint)index >= snapshot.ObjectCount
                || snapshot.ShapeKind[index] != ShapeKind.MixingStroke
                || samples is null
                || samples.Length == 0
                || samples.Any(sample => !Finite(sample.Point.X, sample.Point.Y, sample.Diameter)
                    || sample.Diameter <= 0)
                || HasForbiddenMixingStrokePayload(snapshot, index)
                || snapshot.MixingStrokeLocalRegions.ContainsKey(index))
            {
                throw new InvalidDataException("Mixing-stroke metadata is invalid.");
            }
        }

        foreach (var (index, region) in snapshot.MixingStrokeLocalRegions)
        {
            if ((uint)index >= snapshot.ObjectCount
                || snapshot.ShapeKind[index] != ShapeKind.MixingStroke
                || region is null
                || !MixingBrushRegionData.TryNormalize(
                    region.Vertices,
                    region.TriangleIndices,
                    out _)
                || HasForbiddenMixingStrokePayload(snapshot, index)
                || snapshot.MixingStrokeLocalSamples.ContainsKey(index))
            {
                throw new InvalidDataException("Mixing-stroke region metadata is invalid.");
            }
        }

        for (var index = 0; index < snapshot.ObjectCount; index++)
        {
            if (snapshot.ShapeKind[index] == ShapeKind.MixingStroke
                && snapshot.MixingStrokeLocalSamples.ContainsKey(index)
                    == snapshot.MixingStrokeLocalRegions.ContainsKey(index))
            {
                throw new InvalidDataException($"Mixing-stroke object {index} must have exactly one paint payload.");
            }
        }
    }

    private static bool HasForbiddenMixingStrokePayload(VectorSceneSnapshot snapshot, int index) =>
        snapshot.Stroke[index] != 0
        || snapshot.LinearGradientEnabled[index]
        || snapshot.GradientKinds[index] != GradientKind.Solid
        || snapshot.GradientStops.ContainsKey(index)
        || snapshot.GradientPathLocalPoints.ContainsKey(index)
        || snapshot.ShapeGradientMappingLocalContours.ContainsKey(index)
        || snapshot.PathLocalContours.ContainsKey(index)
        || snapshot.PathBezierLocalContours.ContainsKey(index)
        || snapshot.FreehandLocalPoints.ContainsKey(index)
        || snapshot.FreehandBezierLocalNodes.ContainsKey(index);

    private static void ValidateImportedSvgSources(VectorSceneSnapshot snapshot)
    {
        if (snapshot.ImportedSvgSources is null)
        {
            throw new InvalidDataException("Imported SVG source metadata is missing.");
        }

        long totalCharacters = 0;
        foreach (var (index, source) in snapshot.ImportedSvgSources)
        {
            if ((uint)index >= snapshot.ObjectCount
                || snapshot.ShapeKind[index] != ShapeKind.ImportedSvg
                || string.IsNullOrWhiteSpace(source)
                || source.Length > MaxImportedSvgSourceCharacters)
            {
                throw new InvalidDataException("Imported SVG source metadata is invalid.");
            }

            totalCharacters += source.Length;
            if (totalCharacters > MaxImportedSvgSourceCharactersPerAsset)
            {
                throw new InvalidDataException("Imported SVG source metadata exceeds the supported asset limit.");
            }
        }

        for (var index = 0; index < snapshot.ObjectCount; index++)
        {
            if (snapshot.ShapeKind[index] == ShapeKind.ImportedSvg
                && !snapshot.ImportedSvgSources.ContainsKey(index))
            {
                throw new InvalidDataException($"Imported SVG object {index} has no source payload.");
            }
        }

        if (snapshot.ImportedSvgNames is null)
        {
            throw new InvalidDataException("Imported SVG name metadata is missing.");
        }
        foreach (var (index, name) in snapshot.ImportedSvgNames)
        {
            if ((uint)index >= snapshot.ObjectCount
                || snapshot.ShapeKind[index] != ShapeKind.ImportedSvg
                || string.IsNullOrWhiteSpace(name)
                || name.Length > MaxImportedSvgNameCharacters)
            {
                throw new InvalidDataException("Imported SVG name metadata is invalid.");
            }
        }
    }

    private static void ValidateBitmapObjects(VectorSceneSnapshot snapshot)
    {
        if (snapshot.BitmapObjects is null)
        {
            throw new InvalidDataException("Bitmap object metadata is missing.");
        }

        foreach (var (index, data) in snapshot.BitmapObjects)
        {
            if ((uint)index >= snapshot.ObjectCount
                || index >= snapshot.ShapeKind.Length
                || snapshot.ShapeKind[index] != ShapeKind.Bitmap
                || data is null
                || !data.IsValid)
            {
                throw new InvalidDataException("Bitmap object metadata is invalid.");
            }
        }

        for (var index = 0; index < Math.Min(snapshot.ObjectCount, snapshot.ShapeKind.Length); index++)
        {
            if (snapshot.ShapeKind[index] == ShapeKind.Bitmap
                && !snapshot.BitmapObjects.ContainsKey(index))
            {
                throw new InvalidDataException($"Bitmap object {index} has no image asset payload.");
            }
        }
    }

    private static void ValidateTextObjects(VectorSceneSnapshot snapshot)
    {
        if (snapshot.TextObjects is null)
        {
            throw new InvalidDataException("Editable text metadata is missing.");
        }

        foreach (var (index, data) in snapshot.TextObjects)
        {
            if ((uint)index >= snapshot.ObjectCount
                || snapshot.ShapeKind[index] != ShapeKind.Text
                || !TextGeometry.IsValidStoredData(data)
                || snapshot.Width[index] <= 0
                || snapshot.Height[index] <= 0
                || snapshot.Stroke[index] != 0
                || snapshot.LinearGradientEnabled[index]
                || snapshot.GradientKinds[index] != GradientKind.Solid
                || snapshot.GradientStops.ContainsKey(index)
                || snapshot.GradientPathLocalPoints.ContainsKey(index)
                || snapshot.ShapeGradientMappingLocalContours.ContainsKey(index)
                || snapshot.PathLocalContours.ContainsKey(index)
                || snapshot.PathBezierLocalContours.ContainsKey(index)
                || snapshot.FreehandLocalPoints.ContainsKey(index)
                || snapshot.FreehandBezierLocalNodes.ContainsKey(index))
            {
                throw new InvalidDataException("Editable text metadata is invalid.");
            }
        }

        for (var index = 0; index < snapshot.ObjectCount; index++)
        {
            if (snapshot.ShapeKind[index] == ShapeKind.Text
                && !snapshot.TextObjects.ContainsKey(index))
            {
                throw new InvalidDataException($"Text object {index} has no editable payload.");
            }
        }
    }

    private static void ValidateObjectDistortions(VectorSceneSnapshot snapshot, int formatVersion)
    {
        if (snapshot.ObjectDistortions is null)
        {
            throw new InvalidDataException("Object-distortion metadata is missing.");
        }
        if (formatVersion < 4 && snapshot.ObjectDistortions.Count > 0)
        {
            throw new InvalidDataException("Object-distortion metadata requires SVG format version 4.");
        }

        foreach (var (objectIndex, warps) in snapshot.ObjectDistortions)
        {
            if ((uint)objectIndex >= snapshot.ObjectCount
                || warps is null
                || warps.Length is 0 or > MaxDistortionWarpsPerObject
                || warps.Any(warp => !warp.IsValid))
            {
                throw new InvalidDataException("Object-distortion metadata is invalid.");
            }

            foreach (var warp in warps)
            {
                ValidateDistortionSide(warp.Envelope.Top);
                ValidateDistortionSide(warp.Envelope.Right);
                ValidateDistortionSide(warp.Envelope.Bottom);
                ValidateDistortionSide(warp.Envelope.Left);
            }
        }
    }

    private static void ValidateDistortionSide(IReadOnlyList<DistortBezierAnchor> anchors)
    {
        if (anchors.Count < 2)
        {
            throw new InvalidDataException("A distortion boundary has too few anchors.");
        }

        var ids = new HashSet<Guid>();
        var previousSourceT = -1f;
        foreach (var anchor in anchors)
        {
            if (anchor.Id == Guid.Empty
                || !ids.Add(anchor.Id)
                || !float.IsFinite(anchor.SourceT)
                || anchor.SourceT < 0f
                || anchor.SourceT > 1f
                || anchor.SourceT <= previousSourceT
                || !Finite(
                    anchor.Anchor.X,
                    anchor.Anchor.Y,
                    anchor.IncomingControl.X,
                    anchor.IncomingControl.Y,
                    anchor.OutgoingControl.X,
                    anchor.OutgoingControl.Y))
            {
                throw new InvalidDataException("A distortion boundary anchor is invalid.");
            }
            previousSourceT = anchor.SourceT;
        }
    }

    private static void ValidateObjectArrays(VectorSceneSnapshot snapshot)
    {
        var count = snapshot.ObjectCount;
        ValidateArray(snapshot.ObjectLayer, count, nameof(snapshot.ObjectLayer));
        ValidateArray(snapshot.ObjectKeyframeFrame, count, nameof(snapshot.ObjectKeyframeFrame));
        ValidateArray(snapshot.ObjectOrder, count, nameof(snapshot.ObjectOrder));
        ValidateArray(snapshot.ObjectSubOrder, count, nameof(snapshot.ObjectSubOrder));
        ValidateArray(snapshot.X, count, nameof(snapshot.X));
        ValidateArray(snapshot.Y, count, nameof(snapshot.Y));
        ValidateArray(snapshot.Width, count, nameof(snapshot.Width));
        ValidateArray(snapshot.Height, count, nameof(snapshot.Height));
        ValidateArray(snapshot.Angle, count, nameof(snapshot.Angle));
        ValidateArray(snapshot.Stroke, count, nameof(snapshot.Stroke));
        ValidateArray(snapshot.CurveControlX, count, nameof(snapshot.CurveControlX));
        ValidateArray(snapshot.CurveControlY, count, nameof(snapshot.CurveControlY));
        ValidateArray(snapshot.CurveControl2X, count, nameof(snapshot.CurveControl2X));
        ValidateArray(snapshot.CurveControl2Y, count, nameof(snapshot.CurveControl2Y));
        ValidateArray(snapshot.LineEndpointStyles, count, nameof(snapshot.LineEndpointStyles));
        ValidateArray(snapshot.LineEndEndpointStyles, count, nameof(snapshot.LineEndEndpointStyles));
        ValidateArray(snapshot.ShapeKind, count, nameof(snapshot.ShapeKind));
        ValidateArray(snapshot.ShapeVertexCounts, count, nameof(snapshot.ShapeVertexCounts));
        ValidateArray(snapshot.AtomCount, count, nameof(snapshot.AtomCount));
        ValidateArray(snapshot.Argb, count, nameof(snapshot.Argb));
        ValidateArray(snapshot.StrokeArgb, count, nameof(snapshot.StrokeArgb));
        ValidateOptionalArray(snapshot.FillAutoMergeProtected, count, nameof(snapshot.FillAutoMergeProtected));
        ValidateOptionalArray(snapshot.QuadraticLineEditing, count, nameof(snapshot.QuadraticLineEditing));
        ValidateArray(snapshot.LinearGradientEnabled, count, nameof(snapshot.LinearGradientEnabled));
        ValidateArray(snapshot.GradientKinds, count, nameof(snapshot.GradientKinds));
        ValidateArray(snapshot.GradientStartArgb, count, nameof(snapshot.GradientStartArgb));
        ValidateArray(snapshot.GradientEndArgb, count, nameof(snapshot.GradientEndArgb));
        ValidateArray(snapshot.GradientStartX, count, nameof(snapshot.GradientStartX));
        ValidateArray(snapshot.GradientStartY, count, nameof(snapshot.GradientStartY));
        ValidateArray(snapshot.GradientEndX, count, nameof(snapshot.GradientEndX));
        ValidateArray(snapshot.GradientEndY, count, nameof(snapshot.GradientEndY));
    }

    private static void ValidateGradientStops(VectorSceneSnapshot snapshot)
    {
        if (snapshot.GradientStops is null) throw new InvalidDataException("Gradient-stop metadata is missing.");
        foreach (var (index, stops) in snapshot.GradientStops)
        {
            if ((uint)index >= snapshot.ObjectCount
                || stops is null
                || stops.Any(stop => !float.IsFinite(stop.Position)))
            {
                throw new InvalidDataException("Gradient-stop metadata is invalid.");
            }
        }
    }

    private static void ValidatePointDictionary(
        Dictionary<int, PointF[]> values,
        int objectCount,
        string name)
    {
        if (values is null) throw new InvalidDataException($"{name} metadata is missing.");
        foreach (var (index, points) in values)
        {
            if ((uint)index >= objectCount || points is null || points.Any(point => !Finite(point.X, point.Y)))
            {
                throw new InvalidDataException($"{name} metadata is invalid.");
            }
        }
    }

    private static void ValidateContourDictionary(
        Dictionary<int, PointF[][]> values,
        int objectCount,
        string name)
    {
        if (values is null) throw new InvalidDataException($"{name} metadata is missing.");
        foreach (var (index, contours) in values)
        {
            if ((uint)index >= objectCount
                || contours is null
                || contours.Any(contour => contour is null || contour.Any(point => !Finite(point.X, point.Y))))
            {
                throw new InvalidDataException($"{name} metadata is invalid.");
            }
        }
    }

    private static void ValidatePathBezierContours(VectorSceneSnapshot snapshot)
    {
        if (snapshot.PathBezierLocalContours is null)
        {
            throw new InvalidDataException("Path Bezier metadata is missing.");
        }

        foreach (var (index, contours) in snapshot.PathBezierLocalContours)
        {
            if ((uint)index >= snapshot.ObjectCount
                || snapshot.ShapeKind[index] != ShapeKind.Path
                || !snapshot.PathLocalContours.ContainsKey(index)
                || contours is null
                || contours.Length == 0
                || contours.Any(contour => contour is null
                    || contour.Length < 3
                    || contour.Any(node => !Finite(
                        node.Anchor.X,
                        node.Anchor.Y,
                        node.IncomingControl.X,
                        node.IncomingControl.Y,
                        node.OutgoingControl.X,
                        node.OutgoingControl.Y))))
            {
                throw new InvalidDataException("Path Bezier metadata is invalid.");
            }
        }
    }

    private static void ValidateFreehandBezierNodes(VectorSceneSnapshot snapshot)
    {
        if (snapshot.FreehandBezierLocalNodes is null)
        {
            throw new InvalidDataException("Freehand Bezier metadata is missing.");
        }

        foreach (var (index, nodes) in snapshot.FreehandBezierLocalNodes)
        {
            if ((uint)index >= snapshot.ObjectCount
                || snapshot.ShapeKind[index] != ShapeKind.Freeform
                || !snapshot.FreehandLocalPoints.ContainsKey(index)
                || nodes is null
                || nodes.Length < 2
                || nodes.Any(node => !Finite(
                    node.Anchor.X,
                    node.Anchor.Y,
                    node.IncomingControl.X,
                    node.IncomingControl.Y,
                    node.OutgoingControl.X,
                    node.OutgoingControl.Y)))
            {
                throw new InvalidDataException("Freehand Bezier metadata is invalid.");
            }
        }
    }

    private static void ValidateTimeline(AnimationTimelineSnapshot? timeline, int formatVersion)
    {
        if (timeline is null) return;
        if (timeline.Tracks is null) throw new InvalidDataException("Timeline metadata is missing tracks.");
        var tabGroupIds = ValidateTimelineTabGroups(timeline);
        var trackIds = new HashSet<string>(StringComparer.Ordinal);
        var targetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in timeline.Tracks)
        {
            var tabGroupId = string.IsNullOrWhiteSpace(track?.TabGroupId)
                ? AnimationTimeline.DefaultTabGroupId
                : track.TabGroupId;
            if (track is null
                || string.IsNullOrWhiteSpace(track.Id)
                || string.IsNullOrWhiteSpace(track.TargetId)
                || !trackIds.Add(track.Id)
                || !targetIds.Add(track.TargetId)
                || track.Duration <= 0
                || track.Keyframes is null
                || !tabGroupIds.Contains(tabGroupId))
            {
                throw new InvalidDataException("Timeline track metadata is invalid.");
            }

            var previousFrame = -1;
            foreach (var keyframe in track.Keyframes)
            {
                if (keyframe.Frame <= previousFrame
                    || keyframe.Frame < 0
                    || keyframe.Frame >= track.Duration
                    || !Enum.IsDefined(keyframe.Kind))
                {
                    throw new InvalidDataException("Timeline keyframe metadata is invalid.");
                }
                previousFrame = keyframe.Frame;
            }

            var populatedFrames = track.Keyframes
                .Where(keyframe => keyframe.HasContent)
                .Select(keyframe => keyframe.Frame)
                .ToHashSet();
            var previousTweenEnd = -1;
            foreach (var tween in track.Tweens ?? [])
            {
                if (!tween.IsValid
                    || formatVersion < 3 && !tween.IsLinearCurve
                    || tween.EndFrame >= track.Duration
                    || !populatedFrames.Contains(tween.StartFrame)
                    || !populatedFrames.Contains(tween.EndFrame)
                    || tween.StartFrame < previousTweenEnd)
                {
                    throw new InvalidDataException("Timeline tween metadata is invalid.");
                }

                previousTweenEnd = tween.EndFrame;
            }
        }
    }

    private static HashSet<string> ValidateTimelineTabGroups(AnimationTimelineSnapshot timeline)
    {
        var groupIds = new HashSet<string>(StringComparer.Ordinal)
        {
            AnimationTimeline.DefaultTabGroupId,
            AnimationTimeline.TerrainTabGroupId
        };
        var declaredGroupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in timeline.TabGroups ?? [])
        {
            if (group is null
                || string.IsNullOrWhiteSpace(group.Id)
                || string.Equals(group.Id, AnimationTimeline.AllTabGroupId, StringComparison.Ordinal)
                || !IsSafeTimelineTabGroupId(group.Id)
                || string.IsNullOrWhiteSpace(group.Name)
                || group.Name.Length > MaxTimelineTabGroupNameLength
                || !declaredGroupIds.Add(group.Id))
            {
                throw new InvalidDataException("Timeline tab-group metadata is invalid.");
            }

            if (string.Equals(group.Id, AnimationTimeline.DefaultTabGroupId, StringComparison.Ordinal)
                || string.Equals(group.Id, AnimationTimeline.TerrainTabGroupId, StringComparison.Ordinal))
            {
                continue;
            }

            groupIds.Add(group.Id);
        }

        var activeGroupId = string.IsNullOrWhiteSpace(timeline.ActiveTabGroupId)
            ? AnimationTimeline.DefaultTabGroupId
            : timeline.ActiveTabGroupId;
        if (!string.Equals(activeGroupId, AnimationTimeline.AllTabGroupId, StringComparison.Ordinal)
            && !groupIds.Contains(activeGroupId))
        {
            throw new InvalidDataException("Timeline active tab-group metadata is invalid.");
        }

        return groupIds;
    }

    private static bool IsSafeTimelineTabGroupId(string id)
    {
        return id is not "." and not ".."
            && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !id.Contains(Path.DirectorySeparatorChar)
            && !id.Contains(Path.AltDirectorySeparatorChar);
    }

    private static long CountPoints(IReadOnlyDictionary<int, PointF[]> values) =>
        values.Values.Sum(points => (long)points.Length);

    private static long CountPoints(IReadOnlyDictionary<int, PointF[][]> values) =>
        values.Values.Sum(contours => contours.Sum(points => (long)points.Length));

    private static long CountPoints(IReadOnlyDictionary<int, PathBezierNode[][]> values) =>
        values.Values.Sum(contours => contours.Sum(nodes => (long)nodes.Length));

    private static long CountPoints(IReadOnlyDictionary<int, PathBezierNode[]> values) =>
        values.Values.Sum(nodes => (long)nodes.Length);

    private static long CountDistortionPoints(IReadOnlyDictionary<int, DistortWarp[]> values)
    {
        return values.Values.Sum(warps => warps.Sum(warp =>
            3L * (warp.Envelope.Top.Length
                + warp.Envelope.Right.Length
                + warp.Envelope.Bottom.Length
                + warp.Envelope.Left.Length)
            + 3L));
    }

    private static void ValidateArray<T>(T[]? values, int expectedLength, string name)
    {
        if (values is null || values.Length != expectedLength)
        {
            throw new InvalidDataException($"{name} metadata has an invalid length.");
        }
    }

    private static void ValidateOptionalArray<T>(T[]? values, int expectedLength, string name)
    {
        if (values is null || values.Length != 0 && values.Length != expectedLength)
        {
            throw new InvalidDataException($"{name} metadata has an invalid length.");
        }
    }

    private static bool Finite(params float[] values) => values.All(float.IsFinite);

    private static bool FiniteNonNegative(float value) => float.IsFinite(value) && value >= 0;

    private sealed class MetadataEnvelope
    {
        public int Version { get; init; }
        public string DrawingObjectId { get; init; } = "";
        public VectorSceneSnapshot? Snapshot { get; init; }
    }
}
