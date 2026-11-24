namespace VectorAnimationEngine;

internal sealed partial class LibraryVaultPanel
{
    private const long MaximumImageThumbnailBytes = 16L * 1024 * 1024;
    private const int MaximumImageThumbnailEntries = VectorProject.MaxImageAssetCount;
    private readonly Dictionary<ImageThumbnailKey, ImageThumbnailEntry> _imageThumbnails = new();
    private readonly Dictionary<ImageThumbnailKey, ImageThumbnailRequest> _pendingImageThumbnails = new();
    private CancellationTokenSource _imageThumbnailCancellation = new();
    private Task? _imageThumbnailWorker;
    private bool _imageThumbnailWorkerPosted;
    private long _imageThumbnailUse;
    private long _imageThumbnailBytes;
    private Func<ImageAssetDefinition, string?>? _imageAssetThumbnailPathProvider;

    private readonly record struct ImageThumbnailKey(string ProjectId, string AssetId, string SourcePath,
        string ProjectRelativePath, string Sha256, BitmapImageImportSettings Settings, int Size);
    private sealed record ImageThumbnailEntry(Bitmap? Image, long Used);
    private sealed record ImageThumbnailRequest(ImageThumbnailKey Key, ImageAssetDefinition Asset, VaultRow Row);

    public void SetImageAssetThumbnailPathProvider(Func<ImageAssetDefinition, string?>? provider)
    {
        _imageAssetThumbnailPathProvider = provider;
        ClearImageThumbnails();
    }

    private Bitmap? ImageAssetThumbnail(VaultRow row, int size)
    {
        var asset = row.ImageAsset!;
        var key = new ImageThumbnailKey(_project?.Id ?? "", asset.Id, asset.SourcePath,
            asset.ProjectRelativePath, asset.Sha256, asset.ImportSettings, size);
        if (_imageThumbnails.TryGetValue(key, out var entry))
        {
            _imageThumbnails[key] = entry with { Used = ++_imageThumbnailUse };
            return entry.Image;
        }
        if (!IsHandleCreated || IsDisposed || !Visible) return null;
        _pendingImageThumbnails[key] = new(key, asset, row);
        // Never start I/O in DrawNode, including the synchronous prefix of an
        // async method. Only a single posted worker may drain the current viewport.
        ScheduleImageThumbnailWorker();
        return null;
    }

    private void ScheduleImageThumbnailWorker()
    {
        if (!IsDisposed && IsHandleCreated && Visible && _pendingImageThumbnails.Count > 0
            && !_imageThumbnailWorkerPosted && _imageThumbnailWorker is not { IsCompleted: false })
        {
            _imageThumbnailWorkerPosted = true;
            BeginInvoke(() =>
            {
                _imageThumbnailWorkerPosted = false;
                if (!IsDisposed && !_imageThumbnailCancellation.IsCancellationRequested)
                    _imageThumbnailWorker = RunImageThumbnailWorkerAsync();
            });
        }
    }

    private async Task RunImageThumbnailWorkerAsync()
    {
        try { await LoadVisibleImageThumbnailsAsync(); }
        finally
        {
            _imageThumbnailWorker = null;
            ScheduleImageThumbnailWorker();
        }
    }

    private async Task LoadVisibleImageThumbnailsAsync()
    {
        var token = _imageThumbnailCancellation.Token;
        while (!token.IsCancellationRequested && !IsDisposed && Visible && _pendingImageThumbnails.Count > 0)
        {
            // A fast scrollbar drag must not queue every image it crossed. Keep
            // requests for visible nodes only, then decode one image at a time.
            var visible = new HashSet<VaultRow>(ReferenceEqualityComparer.Instance);
            for (var node = _projectObjects.TopNode; node is not null; node = node.NextVisibleNode)
            {
                if (node.Bounds.Top >= _projectObjects.ClientSize.Height) break;
                if (node.Tag is VaultRow row) visible.Add(row);
            }
            foreach (var key in _pendingImageThumbnails.Where(item => !visible.Contains(item.Value.Row)).Select(item => item.Key).ToArray())
                _pendingImageThumbnails.Remove(key);
            if (_pendingImageThumbnails.Count == 0) return;
            var request = _pendingImageThumbnails.First().Value;
            _pendingImageThumbnails.Remove(request.Key);
            // Repaints can request an in-flight image again before it completes.
            if (_imageThumbnails.ContainsKey(request.Key)) continue;
            Bitmap? image = null;
            try
            {
                // Path resolution belongs to the UI/project owner. Background work
                // receives immutable path/settings/size values, never live models.
                var path = _imageAssetThumbnailPathProvider is { } provider
                    ? provider(request.Asset) : request.Key.SourcePath;
                if (path is not null)
                    image = await Task.Run(() => BitmapImageRasterizer.CreateLibraryThumbnail(
                        path, request.Key.Settings, request.Key.Size, token), token);
                token.ThrowIfCancellationRequested();
                CacheImageThumbnail(request.Key, image);
                image = null; // Cache owns the completed bitmap from this point.
                for (var node = _projectObjects.TopNode; node is not null; node = node.NextVisibleNode)
                {
                    if (node.Bounds.Top >= _projectObjects.ClientSize.Height) break;
                    if (node.Tag is VaultRow row && ReferenceEquals(row.ImageAsset, request.Asset))
                        _projectObjects.Invalidate(new Rectangle(0, node.Bounds.Top,
                            _projectObjects.ClientSize.Width, node.Bounds.Height));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                if (!token.IsCancellationRequested && !IsDisposed)
                {
                    CacheImageThumbnail(request.Key, null);
                    AppLog.Error($"Vault thumbnail unavailable: {request.Row.Item.Name}", exception);
                }
            }
            finally { image?.Dispose(); }
        }
    }

    private void CacheImageThumbnail(ImageThumbnailKey key, Bitmap? image)
    {
        var bytes = image is null ? 0L : (long)image.Width * image.Height * 4;
        if (_imageThumbnails.Remove(key, out var replaced))
        {
            if (replaced.Image is { } old) _imageThumbnailBytes -= (long)old.Width * old.Height * 4;
            replaced.Image?.Dispose();
        }
        while (_imageThumbnails.Count > 0 && (_imageThumbnails.Count >= MaximumImageThumbnailEntries
            || _imageThumbnailBytes + bytes > MaximumImageThumbnailBytes))
        {
            var oldest = _imageThumbnails.MinBy(item => item.Value.Used);
            _imageThumbnails.Remove(oldest.Key);
            if (oldest.Value.Image is { } old) _imageThumbnailBytes -= (long)old.Width * old.Height * 4;
            oldest.Value.Image?.Dispose();
        }
        _imageThumbnails[key] = new(image, ++_imageThumbnailUse);
        _imageThumbnailBytes += bytes;
    }

    private void ClearImageThumbnails()
    {
        _imageThumbnailCancellation.Cancel();
        _imageThumbnailCancellation.Dispose();
        _imageThumbnailCancellation = new();
        _pendingImageThumbnails.Clear();
        foreach (var entry in _imageThumbnails.Values) entry.Image?.Dispose();
        _imageThumbnails.Clear();
        _imageThumbnailBytes = 0;
        if (!IsDisposed && !_projectObjects.IsDisposed) _projectObjects.Invalidate();
    }
}
