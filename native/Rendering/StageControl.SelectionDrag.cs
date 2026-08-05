using System.Diagnostics;
using System.Drawing;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal const double DragFirstMoveBudgetMilliseconds = 30;

    private bool _selectionDragPreviewActive;
    private PointF _selectionDragPreviewOffset;
    private long _selectionDragPreviewRevision;
    private long _presentedSelectionDragPreviewRevision;
    private long _dragFirstMoveStartedAt;
    private long _dragFirstMovePresentRequestedAt;
    private bool _dragFirstMoveAwaitingPresent;
    private bool _dragFirstMoveTransactionCompleted;
    private bool _dragFirstMoveRequiresSelectionPreview;
    private VectorScene? _selectionFillDragFrontScene;
    private readonly HashSet<int> _selectionFillDragFrontObjects = [];

    internal bool SelectionDragPreviewActive => _selectionDragPreviewActive && _selectedElements.Length > 0;
    internal PointF SelectionDragPreviewOffset => SelectionDragPreviewActive
        ? _selectionDragPreviewOffset
        : PointF.Empty;
    internal bool SelectionFillDragFrontActive =>
        _selectionFillDragFrontObjects.Count > 0
        && ReferenceEquals(_selectionFillDragFrontScene, Scene);
    internal double LastDragFirstMoveTotalMilliseconds { get; private set; }
    internal double LastDragFirstMoveHandlerMilliseconds { get; private set; }
    internal double LastDragSnapshotMilliseconds { get; private set; }
    internal double LastDragMaterializeMilliseconds { get; private set; }
    internal double LastDragFirstPresentMilliseconds { get; private set; }

    internal void BeginDragFirstMoveTelemetry(long startedAt)
    {
        BeginDragFirstMoveTelemetryCore(startedAt, requiresSelectionPreview: true);
    }

    internal void BeginDragFirstMoveTelemetry(
        long startedAt,
        double snapshotMilliseconds,
        double materializeMilliseconds)
    {
        BeginDragFirstMoveTelemetryCore(startedAt, requiresSelectionPreview: false);
        RecordDragFirstMovePreparationTimings(snapshotMilliseconds, materializeMilliseconds);
    }

    private void BeginDragFirstMoveTelemetryCore(long startedAt, bool requiresSelectionPreview)
    {
        _dragFirstMoveStartedAt = startedAt;
        _dragFirstMovePresentRequestedAt = Stopwatch.GetTimestamp();
        _dragFirstMoveAwaitingPresent = true;
        _dragFirstMoveTransactionCompleted = false;
        _dragFirstMoveRequiresSelectionPreview = requiresSelectionPreview;
        LastDragFirstMoveTotalMilliseconds = 0;
        LastDragFirstMoveHandlerMilliseconds = ElapsedMilliseconds(
            startedAt,
            _dragFirstMovePresentRequestedAt);
        LastDragSnapshotMilliseconds = 0;
        LastDragMaterializeMilliseconds = 0;
        LastDragFirstPresentMilliseconds = 0;
    }

    internal void RecordDragFirstMovePreparationTimings(
        double snapshotMilliseconds,
        double materializeMilliseconds)
    {
        LastDragSnapshotMilliseconds = Math.Max(0, snapshotMilliseconds);
        LastDragMaterializeMilliseconds = Math.Max(0, materializeMilliseconds);
        if (_dragFirstMoveStartedAt == 0) return;
        LastDragFirstMoveHandlerMilliseconds = Math.Max(
            LastDragFirstMoveHandlerMilliseconds,
            ElapsedMilliseconds(_dragFirstMoveStartedAt, Stopwatch.GetTimestamp()));
        _dragFirstMoveTransactionCompleted = true;
        CompleteDragFirstMoveTelemetryIfReady();
    }

    private void CompleteDragFirstMoveTelemetryIfReady()
    {
        if (_dragFirstMoveStartedAt == 0
            || _dragFirstMoveAwaitingPresent
            || !_dragFirstMoveTransactionCompleted)
        {
            return;
        }

        LogSlowDragFirstMove();
        _dragFirstMoveStartedAt = 0;
        _dragFirstMovePresentRequestedAt = 0;
        _dragFirstMoveTransactionCompleted = false;
        _dragFirstMoveRequiresSelectionPreview = false;
    }

    private void ResetDragFirstMoveTelemetryState()
    {
        _dragFirstMoveStartedAt = 0;
        _dragFirstMovePresentRequestedAt = 0;
        _dragFirstMoveAwaitingPresent = false;
        _dragFirstMoveTransactionCompleted = false;
        _dragFirstMoveRequiresSelectionPreview = false;
    }

    internal bool PresentSelectionDragPreview(PointF worldOffset)
    {
        if (!SetSelectionDragPreviewOffsetCore(worldOffset, invalidate: true)) return false;
        var revision = _selectionDragPreviewRevision;
        if (_presentedSelectionDragPreviewRevision == revision) return true;
        if (_paintInProgress
            || !Visible
            || !IsHandleCreated
            || !CanSynchronouslyPresentInteractiveFrame())
        {
            return false;
        }

        Update();
        return SelectionDragPreviewActive
            && _presentedSelectionDragPreviewRevision == revision;
    }

    internal void SetSelectionDragPreviewOffset(PointF worldOffset)
    {
        SetSelectionDragPreviewOffsetCore(worldOffset, invalidate: true);
    }

    internal void ClearSelectionDragPreview(bool invalidate = true)
    {
        ClearSelectionDragPreviewCore(invalidate);
    }

    internal void SetSelectionFillDragFrontObjects(
        IEnumerable<int> objectIndices,
        bool invalidate = true)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        var scene = Scene;
        var next = objectIndices
            .Where(index => (uint)index < scene.ObjectCount
                && scene.HasFill(index)
                && scene.IsObjectActive(index, Frame))
            .ToHashSet();
        if (ReferenceEquals(_selectionFillDragFrontScene, scene)
            && _selectionFillDragFrontObjects.SetEquals(next))
        {
            return;
        }

        _selectionFillDragFrontObjects.Clear();
        _selectionFillDragFrontObjects.UnionWith(next);
        _selectionFillDragFrontScene = next.Count > 0 ? scene : null;
        if (invalidate) Invalidate();
    }

    internal void ClearSelectionFillDragFront(bool invalidate = true)
    {
        if (!ClearSelectionFillDragFrontCore() || !invalidate) return;
        Invalidate();
    }

    internal IReadOnlySet<int>? SelectionFillDragFrontObjectsFor(VectorScene scene)
    {
        return ReferenceEquals(_selectionFillDragFrontScene, scene)
            && _selectionFillDragFrontObjects.Count > 0
                ? _selectionFillDragFrontObjects
                : null;
    }

    private bool SetSelectionDragPreviewOffsetCore(PointF worldOffset, bool invalidate)
    {
        if (_selectedElements.Length == 0
            || !float.IsFinite(worldOffset.X)
            || !float.IsFinite(worldOffset.Y))
        {
            ClearSelectionDragPreviewCore(invalidate);
            return false;
        }

        if (_selectionDragPreviewActive && _selectionDragPreviewOffset == worldOffset) return true;
        _selectionDragPreviewActive = true;
        _selectionDragPreviewOffset = worldOffset;
        AdvanceSelectionDragPreviewRevision();
        if (invalidate) InvalidateOverlay();
        return true;
    }

    private bool ClearSelectionDragPreviewCore(bool invalidate)
    {
        if (!_selectionDragPreviewActive && _selectionDragPreviewOffset == PointF.Empty) return false;
        _selectionDragPreviewActive = false;
        _selectionDragPreviewOffset = PointF.Empty;
        AdvanceSelectionDragPreviewRevision();
        if (invalidate) InvalidateOverlay();
        return true;
    }

    private bool ClearSelectionFillDragFrontCore()
    {
        if (_selectionFillDragFrontScene is null && _selectionFillDragFrontObjects.Count == 0) return false;
        _selectionFillDragFrontScene = null;
        _selectionFillDragFrontObjects.Clear();
        return true;
    }

    private void AdvanceSelectionDragPreviewRevision()
    {
        unchecked
        {
            _selectionDragPreviewRevision++;
            if (_selectionDragPreviewRevision == 0) _selectionDragPreviewRevision = 1;
        }
    }

    private void LogSlowDragFirstMove()
    {
        if (LastDragFirstMoveTotalMilliseconds <= DragFirstMoveBudgetMilliseconds
            && LastDragFirstMoveHandlerMilliseconds <= DragFirstMoveBudgetMilliseconds)
        {
            return;
        }
        AppLog.Warn(
            $"Slow Stage first drag move: feedback={LastDragFirstMoveTotalMilliseconds:0.0} ms, "
            + $"transaction={LastDragFirstMoveHandlerMilliseconds:0.0} ms, snapshot={LastDragSnapshotMilliseconds:0.0} ms, "
            + $"materialize={LastDragMaterializeMilliseconds:0.0} ms, firstPresent={LastDragFirstPresentMilliseconds:0.0} ms, "
            + $"budget={DragFirstMoveBudgetMilliseconds:0.0} ms, backend={(LastFrameUsedDirect2D ? "Direct2D" : "GDI")}.");
    }
}
