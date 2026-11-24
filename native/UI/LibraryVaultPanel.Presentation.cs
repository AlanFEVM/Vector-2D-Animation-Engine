using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed partial class LibraryVaultPanel
{
    private const int ProjectAssetRowHeight = 26;
    private readonly Label _assetFooter = new();
    private readonly Dictionary<VaultRow, (int Size, int Frame, int Background, Bitmap? Image, long Used)> _assetThumbnails = new(ReferenceEqualityComparer.Instance);
    private long _assetThumbnailUse;
    private int _assetThumbnailRevision;

    private void ClearAssetThumbnails(bool clearImages = true)
    {
        if (clearImages) ClearImageThumbnails();
        foreach (var entry in _assetThumbnails.Values) entry.Image?.Dispose();
        _assetThumbnails.Clear();
        if (!_projectObjects.IsDisposed) _projectObjects.Invalidate();
    }

    private void RefreshAssetThumbnailRevision()
    {
        var hash = new HashCode();
        if (_project is not null)
            foreach (var symbol in _project.DrawingObjects)
            {
                hash.Add(symbol.Scene.GeometryRevision);
                hash.Add(symbol.Scene.SummaryRevision);
                hash.Add(symbol.Scene.ActiveContentRevision);
            }
        var revision = hash.ToHashCode();
        if (revision == _assetThumbnailRevision) return;
        _assetThumbnailRevision = revision;
        ClearAssetThumbnails(clearImages: false);
    }

    private Bitmap? AssetThumbnail(VaultRow row, int size)
    {
        if (row.Folder is not null || row.ImageAssetMissing || row.ExternalSvgMissing) return null;
        if (row.ImageAsset is not null) return ImageAssetThumbnail(row, size);
        var frame = row.DrawingObject is { } symbol
            ? Math.Clamp(_frameProvider?.Invoke() ?? 0, 0, Math.Max(0, symbol.FrameCount - 1)) : 0;
        if (_assetThumbnails.TryGetValue(row, out var entry))
        {
            if (entry.Size == size && entry.Frame == frame && entry.Background == Theme.Stage.ToArgb())
            {
                _assetThumbnails[row] = entry with { Used = ++_assetThumbnailUse };
                return entry.Image;
            }
            entry.Image?.Dispose();
            _assetThumbnails.Remove(row);
        }
        if (_assetThumbnails.Count >= 256)
        {
            var oldest = _assetThumbnails.MinBy(item => item.Value.Used);
            oldest.Value.Image?.Dispose();
            _assetThumbnails.Remove(oldest.Key);
        }
        Bitmap? image = null;
        try { image = CreateAssetThumbnail(row, size, frame); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Error($"Vault thumbnail unavailable: {row.Item.Name}", ex);
        }
        _assetThumbnails[row] = (size, frame, Theme.Stage.ToArgb(), image, ++_assetThumbnailUse);
        return image;
    }

    private BitmapImageRaster? ResolveThumbnailImage(ImageAssetDefinition asset) =>
        _imageAssetPreviewProvider is { } provider ? provider(asset)
            : BitmapImageRasterizer.Decode(asset.SourcePath, asset.ImportSettings);

    private Bitmap? CreateAssetThumbnail(VaultRow row, int size, int frame)
    {
        if (row.ImageAsset is { } asset)
        {
            var raster = ResolveThumbnailImage(asset);
            if (raster is null) return null;
            using var source = raster.AcquireBitmap();
            var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.InterpolationMode = asset.ImportSettings.FilterMode == ImageFilterMode.Point
                    ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                var scale = Math.Min(size / (float)raster.PixelWidth, size / (float)raster.PixelHeight);
                var width = raster.PixelWidth * scale;
                var height = raster.PixelHeight * scale;
                graphics.DrawImage(source.Bitmap, new RectangleF((size - width) / 2, (size - height) / 2, width, height));
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        var scene = new VectorScene();
        scene.CreateEmpty();
        SceneCompositionResult? composition = null;
        if (row.DrawingObject is { } symbol && _project is not null)
            composition = SceneCompositionBuilder.BuildDrawingObjectPreview(scene, symbol, _project.DrawingObjects,
                PointF.Empty, frame, opacity: 1, parentFps: _project.PlaybackFps);
        else if (row.ExternalSvgAsset is { } svg)
        {
            var loaded = ImportedSvgRasterizer.Load(svg.SourcePath);
            scene.AddImportedSvgObject(0, PointF.Empty,
                new SizeF(VectorUnits.FromPixels(loaded.IntrinsicSize.Width), VectorUnits.FromPixels(loaded.IntrinsicSize.Height)),
                loaded.Source, svg.Name);
        }
        else return null;
        var active = Enumerable.Range(0, scene.ObjectCount).Where(i => scene.IsObjectActive(i, frame)).ToArray();
        if (active.Length == 0) return null;
        var bounds = active.Select(scene.GetObjectWorldBounds).Aggregate(RectangleF.Union);
        var empty = new VectorScene();
        empty.CreateEmpty();
        using var stage = new StageControl(empty) { Size = new Size(size, size), Frame = frame, BackColor = Theme.Stage };
        if (composition is not null) stage.BindUnderlayComposition(scene, composition);
        else stage.BindScene(scene);
        stage.BitmapImageResolver = id => _project?.ImageAssets.FirstOrDefault(item => item.Id == id) is { } image
            ? ResolveThumbnailImage(image) : null;
        stage.BitmapImageSamplingProvider = id => _project?.ImageAssets.FirstOrDefault(item => item.Id == id)
            ?.ImportSettings.FilterMode == ImageFilterMode.Point ? BitmapSampling.Point : BitmapSampling.Linear;
        var settings = SceneShotSettings.Default with { Kind = SceneShotCameraKind.TwoD,
            Projection = CameraProjection.Orthographic, AspectRatio = SceneShotAspectRatio.Square1To1,
            X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2,
            OrthographicSize = Math.Clamp(Math.Max(bounds.Width, bounds.Height) * 1.15f,
                SceneShotSettings.MinimumOrthographicSize, SceneShotSettings.MaximumOrthographicSize) };
        return stage.TryCaptureShotPreview(settings, new Size(size, size), out var result) ? result : null;
    }

    private Control BuildAssetHeading()
    {
        return new Label
        {
            Text = "Project Assets", Dock = DockStyle.Fill,
            Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 8),
            BackColor = Theme.Panel,
            Font = Theme.UiFont(Theme.InspectorLabelFontSize),
            ForeColor = Theme.PanelSecondaryText,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            UseMnemonic = false
        };
    }

    private Control BuildAssetFooter()
    {
        _assetFooter.Dock = DockStyle.Fill;
        _assetFooter.Margin = Padding.Empty;
        _assetFooter.Font = Theme.UiFont(8f);
        _assetFooter.ForeColor = Theme.SecondaryText;
        _assetFooter.TextAlign = ContentAlignment.MiddleLeft;
        _assetFooter.AutoEllipsis = true;
        _assetFooter.Paint += (_, e) =>
        {
            using var line = new Pen(Theme.ControlOutline);
            e.Graphics.DrawLine(line, 0, 0, _assetFooter.Width, 0);
        };
        return _assetFooter;
    }

    private void UpdateAssetFooter()
    {
        var count = EnumerateProjectNodes().Count(node => node.Tag is VaultRow { Folder: null });
        _assetFooter.Text = string.Format(UiLocalization.T("Assets: {0} · Drag to stage"), count);
    }

    // Keep the native tree's IDs, navigation, expansion and drag/drop contracts.
    // Only replace its presentation; Theme still owns pointer/focus invalidation.
    private void DrawAssetCard(object? sender, DrawTreeNodeEventArgs e)
    {
        if (e.Node is not { } node) return;
        var g = e.Graphics;
        var scale = _projectObjects.DeviceDpi / 96f;
        int S(int value) => Math.Max(1, (int)Math.Round(value * scale));
        var rowBounds = new Rectangle(0, e.Bounds.Top, _projectObjects.ClientSize.Width, e.Bounds.Height);
        using (var background = new SolidBrush(Theme.Panel)) g.FillRectangle(background, rowBounds);
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
            | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        if (node.Tag is not VaultRow row)
        {
            SvgIcons.Draw(g, SvgIconKind.Vault, new Rectangle(S(8), e.Bounds.Top + (e.Bounds.Height - S(16)) / 2, S(16), S(16)), Theme.SecondaryText);
            var hint = UiLocalization.T(AssetFilterActive ? "Try another search or tag" : "Drop files here to import");
            TextRenderer.DrawText(g, $"{UiLocalization.T(node.Text)} · {hint}", _projectObjects.Font,
                new Rectangle(S(32), e.Bounds.Top, Math.Max(0, rowBounds.Width - S(40)), e.Bounds.Height),
                Theme.SecondaryText, flags);
            return;
        }

        var selected = _multiSelectedNodes.Contains(node) || _projectObjects.SelectedNode == node;
        var hovered = rowBounds.Contains(_projectObjects.PointToClient(Control.MousePosition));
        var surface = !Enabled ? Theme.DisabledSurface : selected ? Theme.AccentSurface
            : hovered ? Theme.PanelHover : Theme.Mix(Theme.Panel, Theme.PanelStrong, 0.46f);
        var foreground = !Enabled ? Theme.DisabledText : Theme.ReadableText(surface, Theme.Text);
        var muted = Theme.ReadableText(surface, Theme.SecondaryText);
        var card = new Rectangle(S(1), e.Bounds.Top + S(1), Math.Max(1, rowBounds.Width - S(3)), e.Bounds.Height - S(2));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(surface))
        using (var border = new Pen(selected ? Theme.Accent : Theme.Mix(Theme.Panel, Theme.ControlOutline, 0.35f)))
            Theme.PaintUiSurface(g, fill, border, card);

        var left = S(8) + node.Level * _projectObjects.Indent;
        if (node.Nodes.Count > 0)
        {
            // Same hit rectangle as Theme.TreeExpandGlyphBounds.
            var expand = new Rectangle(4 + node.Level * Math.Max(16, _projectObjects.Indent),
                e.Bounds.Top + (e.Bounds.Height - 16) / 2, 16, 16);
            SvgIcons.Draw(g, node.IsExpanded ? SvgIconKind.ChevronDown : SvgIconKind.ChevronRight, expand, muted);
            left = Math.Max(left, expand.Right + S(6));
        }
        var tile = new Rectangle(left, card.Top + (card.Height - S(20)) / 2, S(20), S(20));
        var missing = row.ImageAssetMissing || row.ExternalSvgMissing;
        var tint = missing ? Theme.Warning : row.Folder is not null ? Theme.SecondaryText : Theme.Accent;
        using (var tileFill = new SolidBrush(Theme.Mix(surface, tint, 0.12f)))
            Theme.PaintUiSurface(g, tileFill, null, tile);
        if (AssetThumbnail(row, tile.Width) is { } thumbnail)
            g.DrawImageUnscaled(thumbnail, tile.Location);
        else SvgIcons.Draw(g, row.LeadingIcon, Rectangle.Inflate(tile, -S(2), -S(2)), tint);
        var textLeft = tile.Right + S(6);
        var markerCount = Math.Min(row.TrailingColors.Count, 4);
        var textRight = card.Right - S(12) - markerCount * S(9);
        using var nameFont = Theme.UiFont(9f);
        using var detailFont = Theme.UiFont(8f);
        var textWidth = Math.Max(0, textRight - textLeft);
        var detailWidth = Math.Min(S(120), textWidth / 2);
        TextRenderer.DrawText(g, row.Item.Name, nameFont,
            new Rectangle(textLeft, card.Top, Math.Max(0, textWidth - detailWidth - S(8)), card.Height), foreground, flags);
        var detail = row.Folder is not null
            ? string.Format(UiLocalization.T("Folder · Items: {0}"), node.Nodes.Count)
            : missing ? UiLocalization.T(row.ImageAssetMissing ? "Missing image" : "Missing link")
            : row.ImageAsset is { } image ? $"{UiLocalization.T("Image")} · {image.PixelWidth} × {image.PixelHeight}"
            : row.ExternalSvgAsset is not null ? UiLocalization.T("SVG Assets")
            : row.DrawingObject is { } symbol
                ? string.Format(UiLocalization.T("Symbol · Frames: {0}"), symbol.FrameCount) : row.Item.Kind;
        TextRenderer.DrawText(g, detail, detailFont,
            new Rectangle(textRight - detailWidth, card.Top, detailWidth, card.Height),
            missing ? Theme.Warning : muted, flags | TextFormatFlags.Right);
        for (var index = 0; index < markerCount; index++)
        {
            using var marker = new SolidBrush(row.TrailingColors[index]);
            g.FillEllipse(marker, card.Right - S(17) - index * S(9), card.Top + (card.Height - S(6)) / 2, S(6), S(6));
        }
        if (_projectObjects.Focused && selected && (e.State & TreeNodeStates.Focused) != 0)
            Theme.DrawKeyboardFocus(g, Rectangle.Inflate(card, -S(3), -S(3)), Theme.Accent, surface);
    }
}
