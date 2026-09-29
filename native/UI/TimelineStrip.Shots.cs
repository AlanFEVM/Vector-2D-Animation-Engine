using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

// Director camera presentation for the timeline: cameras are independent tracks over the one scene.
// The director view presents only camera tracks; the scene workflow presents scene tracks without
// the director-camera rows.
internal sealed partial class TimelineStrip
{
    private string? _activeShotId;
    private SceneShotRange[] _shotRanges = [];
    private HashSet<string> _shotTrackIds = new(StringComparer.Ordinal);
    private bool _shotFilterActive;
    private bool _shotTracksHidden;

    internal string? ActiveShotId => _activeShotId;

    internal bool HasShotFilter => _shotFilterActive;

    internal bool ShotTracksHidden => _shotTracksHidden;

    private bool IsCameraOnlyTimeline => _shotFilterActive;

    private bool IsTrackEditableInCurrentPresentation(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        if (_shotFilterActive) return IsSceneShotTrack(trackIndex);
        return !_shotTracksHidden || !IsSceneShotTrack(trackIndex);
    }

    /// <summary>Number of track rows currently presented. Used by the timeline regression suite.</summary>
    internal int VisibleTrackRowCount => VisibleTrackCount();

    internal SceneShotRange[] ShotRanges => _shotRanges;

    /// <summary>
    /// Selects the camera label shown above the timeline and, in the director workspace, limits
    /// the visible rows to the independent camera tracks. Pass <paramref name="hideShotTracks"/>
    /// for the scene workflow to hide camera rows while keeping normal scene-layer interaction
    /// enabled. The underlying scene timeline remains one continuous timeline; this is only a
    /// presentation filter.
    /// </summary>
    internal void ApplyShotFilter(
        string? shotId,
        IReadOnlyCollection<string>? targetIds,
        IReadOnlyList<SceneShotRange>? ranges,
        bool hideShotTracks = false)
    {
        var nextShotId = string.IsNullOrWhiteSpace(shotId) ? null : shotId;
        var nextFilterActive = targetIds is not null || ranges is not null;
        // The director filter and the scene-workflow suppression are mutually exclusive
        // presentations. A camera-only filter always wins when both arguments are supplied.
        var nextShotTracksHidden = hideShotTracks && !nextFilterActive;
        var nextShotTrackIds = targetIds is not null
            ? targetIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal)
            : ranges is not null
                ? ranges
                    .Select(range => range.ShotId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        var selectionChanged = !string.Equals(_activeShotId, nextShotId, StringComparison.Ordinal);
        var rangesChanged = !ShotRangesEqual(_shotRanges, ranges);
        var filterChanged = _shotFilterActive != nextFilterActive
            || !_shotTrackIds.SetEquals(nextShotTrackIds)
            || _shotTracksHidden != nextShotTracksHidden;
        _activeShotId = nextShotId;
        _shotFilterActive = nextFilterActive;
        _shotTracksHidden = nextShotTracksHidden;
        _shotTrackIds = nextShotTrackIds;
        _shotRanges = ranges is null ? [] : ranges.ToArray();
        if (!selectionChanged && !rangesChanged && !filterChanged)
        {
            return;
        }

        // Keep the row cache and any selected tween coherent when camera rows are entering or
        // leaving the presentation. Selection identity remains the stable track ID, never a row.
        InvalidateTimelineStructureCache();
        RemoveCollapsedFrameSelection();
        PruneLayerSelection();
        UpdateSelectedTweenFromFrameSelection(forceNotification: _selectedTween is not null);
        SetFirstVisibleTrack(_firstVisibleTrack, invalidate: false);
        EnsureActiveTrackVisible();
        if (_shotFilterActive)
        {
            var visibleCameraTracks = VisibleTrackIndices();
            var activeTrack = TrackIndexForId(_activeTrackId ?? "");
            if (activeTrack < 0 || VisibleTrackPosition(activeTrack) < 0)
            {
                activeTrack = visibleCameraTracks.Count > 0 ? visibleCameraTracks[0] : -1;
                _activeTrackId = activeTrack >= 0 ? _timeline.Tracks[activeTrack].Id : null;
            }

            _selectedLayerTrackIds.RemoveWhere(trackId =>
            {
                var index = TrackIndexForId(trackId);
                return index < 0 || !IsTrackEditableInCurrentPresentation(index);
            });
            if (activeTrack >= 0 && IsTrackEditableInCurrentPresentation(activeTrack))
            {
                _selectedLayerTrackIds.Add(_timeline.Tracks[activeTrack].Id);
                _layerSelectionAnchorTrackId = _timeline.Tracks[activeTrack].Id;
            }

            EnsureActiveTrackVisible();
        }
        else if (_shotTracksHidden)
        {
            var activeTrack = TrackIndexForId(_activeTrackId ?? "");
            if (activeTrack >= 0 && IsSceneShotTrack(activeTrack))
            {
                var visibleTracks = VisibleTrackIndices();
                var fallback = visibleTracks.FirstOrDefault(index =>
                    IsTrackSelectableRow(index) && !IsSceneShotTrack(index));
                _activeTrackId = fallback >= 0 ? _timeline.Tracks[fallback].Id : null;
                _selectedLayerTrackIds.RemoveWhere(trackId =>
                {
                    var index = TrackIndexForId(trackId);
                    return index < 0 || !IsTrackEditableInCurrentPresentation(index);
                });
                if (fallback >= 0)
                {
                    _selectedLayerTrackIds.Add(_timeline.Tracks[fallback].Id);
                    _layerSelectionAnchorTrackId = _timeline.Tracks[fallback].Id;
                }
            }

            EnsureActiveTrackVisible();
        }
        RefreshOnionSkinControls();
        _knownLayerVisuals = CaptureLayerVisualSnapshots();
        Invalidate();
    }

    /// <summary>Scrolls the frame window so the requested frame is inside the visible range.</summary>
    internal void ScrollFrameIntoView(int frame)
    {
        var layout = CreateLayout();
        var visibleFrames = VisibleFrameDrawCount(layout);
        var clamped = Math.Max(StartFrame, frame);
        if (clamped >= _firstVisibleFrame && clamped < _firstVisibleFrame + visibleFrames)
        {
            Invalidate();
            return;
        }

        SetFirstVisibleFrame(Math.Max(StartFrame, clamped - 1));
        Invalidate();
    }

    private bool IsTrackVisibleInActiveShot(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        if (!_shotFilterActive)
        {
            return !_shotTracksHidden || !IsSceneShotTrack(trackIndex);
        }

        if (_sceneDefinition is null) return false;

        var targetId = _timeline.Tracks[trackIndex].TargetId;
        return _shotTrackIds.Contains(targetId) && IsSceneShotTrack(trackIndex);
    }

    private SceneShotRange? FindActiveShotRange()
    {
        if (_activeShotId is null) return null;
        foreach (var range in _shotRanges)
        {
            if (string.Equals(range.ShotId, _activeShotId, StringComparison.Ordinal)) return range;
        }

        return null;
    }

    private string ResolveTimelineHeaderLabel(string contextName)
    {
        if (FindActiveShotRange() is not { } activeShot) return contextName;
        return string.IsNullOrWhiteSpace(contextName)
            ? activeShot.Name
            : contextName + "  \u00b7  " + activeShot.Name;
    }

    private static bool ShotRangesEqual(SceneShotRange[] left, IReadOnlyList<SceneShotRange>? right)
    {
        if (right is null) return left.Length == 0;
        if (left.Length != right.Count) return false;
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index]) return false;
        }

        return true;
    }

    /// <summary>Column window (relative to the visible frame window) covered by one shot span.</summary>
    internal static (int FirstColumn, int LastColumnExclusive) ResolveShotVisibleColumns(
        int shotStartFrame,
        int shotEndFrame,
        int firstVisibleFrame,
        int visibleFrameCount)
    {
        if (shotEndFrame < shotStartFrame || visibleFrameCount <= 0) return (0, 0);
        var first = Math.Max(shotStartFrame, firstVisibleFrame) - firstVisibleFrame;
        var lastExclusive = Math.Min(shotEndFrame + 1, firstVisibleFrame + visibleFrameCount) - firstVisibleFrame;
        if (lastExclusive <= first) return (0, 0);
        return (first, lastExclusive);
    }

    private void DrawShotRangeBands(Graphics graphics, TimelineLayout layout, Rectangle clipBounds)
    {
        if (_shotRanges.Length == 0) return;
        var paintBounds = Rectangle.Intersect(layout.RulerBounds, clipBounds);
        if (paintBounds.Width <= 0 || paintBounds.Height <= 0) return;

        using var activeFill = new SolidBrush(Color.FromArgb(46, Theme.Accent));
        using var idleFill = new SolidBrush(Color.FromArgb(18, Theme.Muted));
        var visibleFrames = VisibleFrameDrawCount(layout);
        foreach (var range in _shotRanges)
        {
            var (firstColumn, lastColumnExclusive) = ResolveShotVisibleColumns(
                range.StartFrame,
                range.EndFrame,
                _firstVisibleFrame,
                visibleFrames);
            if (lastColumnExclusive <= firstColumn) continue;
            var isActive = string.Equals(range.ShotId, _activeShotId, StringComparison.Ordinal);
            var bounds = ShotBandBounds(layout, firstColumn, lastColumnExclusive);
            if (bounds.Width <= 0) continue;
            graphics.FillRectangle(isActive ? activeFill : idleFill, bounds);
        }
    }

    private void DrawShotRangeEdges(Graphics graphics, TimelineLayout layout, Rectangle clipBounds)
    {
        if (_shotRanges.Length == 0) return;
        var paintBounds = Rectangle.Intersect(layout.RulerBounds, clipBounds);
        if (paintBounds.Width <= 0 || paintBounds.Height <= 0) return;

        using var activeEdge = new Pen(Theme.Accent, 1.4f);
        using var idleEdge = new Pen(Color.FromArgb(150, Theme.ReadableUiColor(Theme.Top, Theme.BorderHover)), 1f);
        using var labelFont = Theme.UiFont(7.5f);
        var visibleFrames = VisibleFrameDrawCount(layout);
        var state = graphics.Save();
        graphics.SetClip(paintBounds, CombineMode.Intersect);
        foreach (var range in _shotRanges)
        {
            var (firstColumn, lastColumnExclusive) = ResolveShotVisibleColumns(
                range.StartFrame,
                range.EndFrame,
                _firstVisibleFrame,
                visibleFrames);
            if (lastColumnExclusive <= firstColumn) continue;
            var isActive = string.Equals(range.ShotId, _activeShotId, StringComparison.Ordinal);
            var bounds = ShotBandBounds(layout, firstColumn, lastColumnExclusive);
            if (!isActive)
            {
                graphics.DrawLine(idleEdge, bounds.Left, bounds.Top + 3, bounds.Left, bounds.Bottom - 3);
                continue;
            }

            graphics.DrawLine(activeEdge, bounds.Left, bounds.Top, bounds.Left, bounds.Bottom);
            graphics.DrawLine(activeEdge, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom);
            var labelWidth = bounds.Width - 6;
            if (labelWidth < 30) continue;
            TextRenderer.DrawText(
                graphics,
                range.Name,
                labelFont,
                new Rectangle(bounds.Left + 3, bounds.Top, labelWidth, bounds.Height - 1),
                Theme.ReadableText(Theme.Top, Theme.AccentLabel),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        graphics.Restore(state);
    }

    private Rectangle ShotBandBounds(TimelineLayout layout, int firstColumn, int lastColumnExclusive)
    {
        var left = layout.TrackLeft + firstColumn * _frameCellWidth;
        var right = layout.TrackLeft + lastColumnExclusive * _frameCellWidth;
        return Rectangle.FromLTRB(
            Math.Max(layout.TrackLeft, left),
            layout.RulerBounds.Top + 1,
            Math.Min(layout.TrackRight, right),
            Math.Max(layout.RulerBounds.Top + 2, layout.RulerBounds.Bottom - 1));
    }
}
