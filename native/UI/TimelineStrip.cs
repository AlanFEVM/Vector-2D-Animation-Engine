using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal readonly record struct TimelineFrameCell(string TrackId, int Frame);

internal enum TimelineCommand
{
    CopyFrames,
    PasteFrames,
    InsertFrames,
    DeleteFrames,
    InsertKeyframes,
    InsertBlankKeyframes,
    ClearKeyframes
}

internal sealed class TimelineCommandRequestedEventArgs(
    TimelineCommand command,
    IReadOnlyList<TimelineFrameCell> cells) : EventArgs
{
    public TimelineCommand Command { get; } = command;
    public IReadOnlyList<TimelineFrameCell> Cells { get; } = cells;
}

internal enum TimelineLayerDropPlacement : byte
{
    Before,
    After,
    Inside,
    Mask
}

internal sealed class TimelineLayerMoveRequestedEventArgs(
    string trackId,
    string targetTrackId,
    TimelineLayerDropPlacement placement) : EventArgs
{
    public string TrackId { get; } = trackId;
    public string TargetTrackId { get; } = targetTrackId;
    public TimelineLayerDropPlacement Placement { get; } = placement;
}

internal sealed class TimelineOnionSkinRangeChangedEventArgs(int previousFrames, int nextFrames) : EventArgs
{
    public int PreviousFrames { get; } = previousFrames;
    public int NextFrames { get; } = nextFrames;
}

internal enum TimelineOnionSkinRangeHandle : byte
{
    None,
    Previous,
    Next
}

internal sealed class TimelineStrip : Control
{
    private const int PreferredGutterWidth = 232;
    private const int MinimumGutterWidth = 148;
    private int HeaderHeight => ScaleTimelineMetric(34);
    private const int RulerHeight = 28;
    private const int RowHeight = 23;
    private const int FrameCellWidth = 16;
    private const int HorizontalScrollHeight = 16;
    private const int HorizontalScrollArrowWidth = 15;
    private const int VerticalScrollWidth = 7;
    private const int VisibilityColumnWidth = 30;
    private const int LockColumnWidth = 24;
    private const int LayerControlsWidth = VisibilityColumnWidth + LockColumnWidth;
    private const int AddLayerButtonWidth = 26;
    private const int SoloButtonWidth = 48;
    private const int AllButtonWidth = 42;
    private const int HeightResizeHandleHeight = 6;

    private ITimelineContext _context = null!;
    private AnimationTimeline _timeline = null!;
    private VectorScene? _vectorScene;
    private DrawingObjectDefinition? _drawingObjectDefinition;
    private SceneDefinition? _sceneDefinition;
    private string? _activeTrackId;
    private bool _draggingPlayhead;
    private bool _draggingFrameSelection;
    private bool _draggingLayer;
    private bool _draggingHorizontalScroll;
    private bool _draggingVerticalScroll;
    private TimelineOnionSkinRangeHandle _draggingOnionSkinRangeHandle;
    private int _horizontalScrollDragOffset;
    private int _verticalScrollDragOffset;
    private int _currentFrame;
    private int _startFrame;
    private int _endFrame;
    private int _knownFrameCount;
    private int _firstVisibleFrame;
    private int _firstVisibleTrack;
    private int _hoverFrame = -1;
    private int _hoverTrack = -1;
    private int _layerDropTrack = -1;
    private TimelineLayerDropPlacement _layerDropPlacement;
    private bool _draggingHeightResize;
    private int _heightResizeStartScreenY;
    private int _heightResizeStartHeight;
    private int? _pendingHeightResize;
    private bool _heightResizeLayoutDirty;
    private readonly System.Windows.Forms.Timer _heightResizeTimer = new() { Interval = 16 };
    private bool _isPlaying;
    private readonly HashSet<TimelineFrameCell> _selectedFrameCells = [];
    private readonly HashSet<string> _selectedLayerTrackIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedFolderLayerIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedMaskLayerIds = new(StringComparer.Ordinal);
    private TimelineFrameCell? _selectionAnchor;
    private string? _layerSelectionAnchorTrackId;
    private string? _draggedLayerTrackId;
    private readonly AnimatedContextMenuStrip _layerContextMenu = new();
    private readonly AnimatedContextMenuStrip _frameContextMenu = new();
    private readonly ToolStripMenuItem _newDrawingLayerMenuItem;
    private readonly ToolStripMenuItem _copyFramesMenuItem;
    private readonly ToolStripMenuItem _pasteFramesMenuItem;
    private readonly ToolStripMenuItem _insertFramesMenuItem;
    private readonly ToolStripMenuItem _deleteFramesMenuItem;
    private readonly ToolStripMenuItem _insertKeyframesMenuItem;
    private readonly ToolStripMenuItem _insertBlankKeyframesMenuItem;
    private readonly ToolStripMenuItem _clearKeyframesMenuItem;
    private readonly ToolStripMenuItem _newFolderLayerMenuItem;
    private readonly ToolStripMenuItem _newMaskLayerMenuItem;
    private readonly ToolStripMenuItem _toggleFolderMenuItem;
    private readonly ToolStripMenuItem _moveLayerUpMenuItem;
    private readonly ToolStripMenuItem _moveLayerDownMenuItem;
    private readonly ToolStripMenuItem _renameLayerMenuItem;
    private readonly ToolStripMenuItem _layerColorMenuItem;
    private readonly ToolStripMenuItem _lockLayerMenuItem;
    private readonly ToolStripMenuItem _showSelectedLayersMenuItem;
    private readonly ToolStripMenuItem _hideSelectedLayersMenuItem;
    private readonly Label _playbackFpsLabel = CreateHeaderLabel("FPS", "Animation frame rate");
    private readonly ModernNumericUpDown _playbackFps = CreatePlaybackFpsInput();
    private readonly ModernToggleSwitch _onionSkinToggle = new()
    {
        AutoSize = false,
        Text = "Onion",
        AccessibleName = "Enable onion skin for the active layer",
        Height = Theme.ControlHeightCompact
    };
    private readonly Label _onionPreviousLabel = CreateOnionSkinRangeLabel("Prev");
    private readonly ModernNumericUpDown _onionPreviousFrames = CreateOnionSkinRangeInput("Previous onion skin frames");
    private readonly Label _onionNextLabel = CreateOnionSkinRangeLabel("Next");
    private readonly ModernNumericUpDown _onionNextFrames = CreateOnionSkinRangeInput("Next onion skin frames");
    private bool _updatingOnionSkinControls;
    private bool _updatingPlaybackFps;

    public event EventHandler? CurrentFrameChanged;
    public event EventHandler? ActiveLayerChanged;
    public event EventHandler? LayerVisibilityChanged;
    public event EventHandler? AddLayerRequested;
    public event EventHandler? AddFolderLayerRequested;
    public event EventHandler? AddMaskLayerRequested;
    public event EventHandler? PlaybackFpsChanged;
    public event EventHandler? FrameSelectionChanged;
    public event EventHandler<TimelineCommandRequestedEventArgs>? CommandRequested;
    public event EventHandler<TimelineLayerMoveRequestedEventArgs>? LayerMoveRequested;
    public event EventHandler? LayerRenameRequested;
    public event EventHandler? LayerColorRequested;
    public event EventHandler? LayerLockRequested;
    public event EventHandler? LayerOnionSkinRequested;
    public event EventHandler<TimelineOnionSkinRangeChangedEventArgs>? OnionSkinRangeChanged;
    public event EventHandler? OnionSkinRangeInteractionStarted;
    public event EventHandler? OnionSkinRangeInteractionCompleted;
    public event EventHandler? OnionSkinRangeInteractionCanceled;

    public TimelineStrip(VectorScene scene)
        : this((ITimelineContext)scene)
    {
    }

    public TimelineStrip(ITimelineContext context)
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        BackColor = Theme.Top;
        ForeColor = Theme.Text;
        Font = Theme.UiFont(9);
        Cursor = Cursors.Default;
        MinimumSize = new Size(360, 118);
        TabStop = true;
        _copyFramesMenuItem = CreateContextMenuItem("Copy Frames", TimelineCommand.CopyFrames, Keys.Control | Keys.C);
        _pasteFramesMenuItem = CreateContextMenuItem("Paste Frames", TimelineCommand.PasteFrames, Keys.Control | Keys.V);
        _insertFramesMenuItem = CreateContextMenuItem("Insert Frames", TimelineCommand.InsertFrames, Keys.F5);
        _deleteFramesMenuItem = CreateContextMenuItem("Delete Frames", TimelineCommand.DeleteFrames, Keys.Shift | Keys.F5);
        _insertKeyframesMenuItem = CreateContextMenuItem("Insert Keyframes", TimelineCommand.InsertKeyframes, Keys.F6);
        _insertBlankKeyframesMenuItem = CreateContextMenuItem("Insert Blank Keyframes", TimelineCommand.InsertBlankKeyframes, Keys.F7);
        _clearKeyframesMenuItem = CreateContextMenuItem("Clear Keyframes", TimelineCommand.ClearKeyframes, Keys.Shift | Keys.F6);
        _newDrawingLayerMenuItem = new ToolStripMenuItem("New Drawing Layer", null, (_, _) => AddLayerRequested?.Invoke(this, EventArgs.Empty));
        _newFolderLayerMenuItem = new ToolStripMenuItem("New Folder Layer", null, (_, _) => AddFolderLayerRequested?.Invoke(this, EventArgs.Empty));
        _newMaskLayerMenuItem = new ToolStripMenuItem("New Mask Layer", null, (_, _) => AddMaskLayerRequested?.Invoke(this, EventArgs.Empty));
        _toggleFolderMenuItem = new ToolStripMenuItem("Collapse Folder", null, (_, _) => ToggleActiveTrackGroupCollapsed());
        _moveLayerUpMenuItem = new ToolStripMenuItem("Move Layer Up", null, (_, _) => RequestLayerMove(-1));
        _moveLayerDownMenuItem = new ToolStripMenuItem("Move Layer Down", null, (_, _) => RequestLayerMove(1));
        _renameLayerMenuItem = new ToolStripMenuItem("Rename Layer...", null, (_, _) => LayerRenameRequested?.Invoke(this, EventArgs.Empty))
        {
            ShortcutKeys = Keys.F2
        };
        _layerColorMenuItem = new ToolStripMenuItem("Layer Color...", null, (_, _) => LayerColorRequested?.Invoke(this, EventArgs.Empty));
        _lockLayerMenuItem = new ToolStripMenuItem("Lock Layer", null, (_, _) => LayerLockRequested?.Invoke(this, EventArgs.Empty));
        _showSelectedLayersMenuItem = new ToolStripMenuItem("Show Selected Layers", null, (_, _) => SetSelectedTrackVisibility(true));
        _hideSelectedLayersMenuItem = new ToolStripMenuItem("Hide Selected Layers", null, (_, _) => SetSelectedTrackVisibility(false));
        _layerContextMenu.Items.AddRange(new ToolStripItem[]
        {
            _newDrawingLayerMenuItem,
            _newFolderLayerMenuItem,
            _newMaskLayerMenuItem,
            _toggleFolderMenuItem,
            new ToolStripSeparator(),
            _moveLayerUpMenuItem,
            _moveLayerDownMenuItem,
            new ToolStripSeparator(),
            _showSelectedLayersMenuItem,
            _hideSelectedLayersMenuItem,
            new ToolStripSeparator(),
            _renameLayerMenuItem,
            _lockLayerMenuItem,
            _layerColorMenuItem
        });
        _frameContextMenu.Items.AddRange(new ToolStripItem[]
        {
            _copyFramesMenuItem,
            _pasteFramesMenuItem,
            new ToolStripSeparator(),
            _insertFramesMenuItem,
            _deleteFramesMenuItem,
            _insertKeyframesMenuItem,
            _insertBlankKeyframesMenuItem,
            _clearKeyframesMenuItem
        });
        _layerContextMenu.Opening += HandleLayerContextMenuOpening;
        _frameContextMenu.Opening += HandleFrameContextMenuOpening;
        _heightResizeTimer.Tick += (_, _) => TickHeightResize();
        _onionSkinToggle.CheckedChanged += (_, _) =>
        {
            if (!_updatingOnionSkinControls) LayerOnionSkinRequested?.Invoke(this, EventArgs.Empty);
        };
        _onionPreviousFrames.ValueChanged += (_, _) => RaiseOnionSkinRangeChanged();
        _onionNextFrames.ValueChanged += (_, _) => RaiseOnionSkinRangeChanged();
        _onionPreviousFrames.InteractionStarted += (_, _) => OnionSkinRangeInteractionStarted?.Invoke(this, EventArgs.Empty);
        _onionNextFrames.InteractionStarted += (_, _) => OnionSkinRangeInteractionStarted?.Invoke(this, EventArgs.Empty);
        _onionPreviousFrames.InteractionCompleted += (_, _) => OnionSkinRangeInteractionCompleted?.Invoke(this, EventArgs.Empty);
        _onionNextFrames.InteractionCompleted += (_, _) => OnionSkinRangeInteractionCompleted?.Invoke(this, EventArgs.Empty);
        _onionPreviousFrames.InteractionCanceled += (_, _) => OnionSkinRangeInteractionCanceled?.Invoke(this, EventArgs.Empty);
        _onionNextFrames.InteractionCanceled += (_, _) => OnionSkinRangeInteractionCanceled?.Invoke(this, EventArgs.Empty);
        _playbackFps.ValueChanged += (_, _) =>
        {
            if (!_updatingPlaybackFps) PlaybackFpsChanged?.Invoke(this, EventArgs.Empty);
        };
        Controls.AddRange([
            _playbackFpsLabel,
            _playbackFps,
            _onionSkinToggle,
            _onionPreviousLabel,
            _onionPreviousFrames,
            _onionNextLabel,
            _onionNextFrames
        ]);
        BindContext(context);
    }

    public ITimelineContext Context => _context;

    public int PlaybackFps
    {
        get => (int)_playbackFps.Value;
        set
        {
            var next = Math.Clamp(value, (int)_playbackFps.Minimum, (int)_playbackFps.Maximum);
            if ((int)_playbackFps.Value == next) return;
            _updatingPlaybackFps = true;
            try
            {
                _playbackFps.Value = next;
            }
            finally
            {
                _updatingPlaybackFps = false;
            }
        }
    }

    public string? ActiveTrackId
    {
        get
        {
            var index = GetActiveTrackIndex();
            return index >= 0 && index < TrackCount ? _timeline.Tracks[index].Id : null;
        }
    }

    public int ActiveTrackIndex
    {
        get => GetActiveTrackIndex();
        set => SetActiveTrack(value);
    }

    public IReadOnlyList<TimelineFrameCell> SelectedFrameCells => SelectedCells();

    public bool HasFrameSelection => _selectedFrameCells.Count > 0;

    public IReadOnlyList<string> SelectedLayerTargetIds => SelectedLayerTrackIndices()
        .Select(trackIndex => _timeline.Tracks[trackIndex].TargetId)
        .ToArray();

    public int SelectedLayerCount => SelectedLayerTrackIndices().Count;

    public int CurrentFrame
    {
        get => _currentFrame;
        set
        {
            var next = Math.Clamp(value, StartFrame, EndFrame);
            if (_currentFrame == next)
            {
                EnsureCurrentFrameVisible();
                return;
            }

            _currentFrame = next;
            EnsureCurrentFrameVisible();
            Invalidate();
            CurrentFrameChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            Invalidate();
        }
    }

    public int StartFrame
    {
        get => _startFrame;
        set
        {
            var next = Math.Clamp(value, 0, Math.Max(0, FrameCount - 1));
            if (next > _endFrame) _endFrame = next;
            if (_startFrame == next) return;
            _startFrame = next;
            CurrentFrame = _currentFrame;
            EnsureCurrentFrameVisible();
            Invalidate();
        }
    }

    public int EndFrame
    {
        get => _endFrame;
        set
        {
            var next = Math.Clamp(value, 0, Math.Max(0, FrameCount - 1));
            if (next < _startFrame) _startFrame = next;
            if (_endFrame == next) return;
            _endFrame = next;
            CurrentFrame = _currentFrame;
            EnsureCurrentFrameVisible();
            Invalidate();
        }
    }

    private int FrameCount => Math.Max(1, Math.Max(_context.FrameCount, _timeline.Duration));
    private int TrackCount => _timeline.Tracks.Count;

    public void BindScene(VectorScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        BindContext(scene);
    }

    public void BindSceneDefinition(SceneDefinition scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        BindContext(scene);
    }

    public void BindContext(ITimelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_timeline is not null) _timeline.Changed -= HandleTimelineChanged;

        _context = context;
        _vectorScene = context as VectorScene;
        _drawingObjectDefinition = context as DrawingObjectDefinition;
        _sceneDefinition = context as SceneDefinition;
        _context.SynchronizeTimelineTracks();
        _timeline = _context.Timeline;
        _timeline.Changed += HandleTimelineChanged;

        _knownFrameCount = FrameCount;
        _startFrame = 0;
        _endFrame = Math.Max(0, _knownFrameCount - 1);
        _currentFrame = Math.Clamp(_currentFrame, _startFrame, _endFrame);
        _firstVisibleFrame = _startFrame;
        _firstVisibleTrack = 0;
        _collapsedFolderLayerIds.Clear();
        _collapsedMaskLayerIds.Clear();
        _activeTrackId = TrackCount > 0 ? _timeline.Tracks[Math.Max(0, GetModelActiveTrackIndex())].Id : null;
        _selectedFrameCells.Clear();
        _selectedLayerTrackIds.Clear();
        _selectionAnchor = null;
        _layerSelectionAnchorTrackId = _activeTrackId;
        if (!string.IsNullOrWhiteSpace(_activeTrackId)) _selectedLayerTrackIds.Add(_activeTrackId);
        RefreshOnionSkinControls();
        EnsureActiveTrackVisible();
        EnsureCurrentFrameVisible();
        Invalidate();
    }

    public void RefreshTimeline()
    {
        _context.SynchronizeTimelineTracks();
        var boundTimeline = _context.Timeline;
        if (!ReferenceEquals(_timeline, boundTimeline))
        {
            _timeline.Changed -= HandleTimelineChanged;
            _timeline = boundTimeline;
            _timeline.Changed += HandleTimelineChanged;
        }

        PruneCollapsedLayerGroupIds();
        PruneLayerSelection();
        UpdateFrameBoundsForTimelineChange();
        RefreshOnionSkinControls();
        EnsureActiveTrackVisible();
        EnsureCurrentFrameVisible();
        Invalidate();
    }

    public void SelectModelActiveTrack()
    {
        var trackIndex = GetModelActiveTrackIndex();
        if (trackIndex < 0) return;
        _selectedLayerTrackIds.Clear();
        if (IsTrackLayer(trackIndex)) _selectedLayerTrackIds.Add(_timeline.Tracks[trackIndex].Id);
        _layerSelectionAnchorTrackId = _timeline.Tracks[trackIndex].Id;
        SetActiveTrack(trackIndex);
    }

    public void SelectSingleFrame(string trackId, int frame)
    {
        if (TrackIndexForId(trackId) < 0) return;
        var next = Math.Clamp(frame, StartFrame, EndFrame);
        var cell = new TimelineFrameCell(trackId, next);
        SetFrameSelection([cell], cell);
        CurrentFrame = next;
    }

    public void RefreshOnionSkinControls()
    {
        var drawingScene = DrawingScene();
        var activeTrack = GetActiveTrackIndex();
        var layer = drawingScene is null || activeTrack < 0
            ? -1
            : FindLayerIndex(drawingScene, _timeline.Tracks[activeTrack].TargetId);
        var available = drawingScene is not null && layer >= 0;

        _updatingOnionSkinControls = true;
        try
        {
            _onionSkinToggle.Enabled = available;
            if (available)
            {
                _onionSkinToggle.Checked = drawingScene!.LayerOnionSkin[layer];
                _onionPreviousFrames.Value = drawingScene.OnionSkinPreviousFrames;
                _onionNextFrames.Value = drawingScene.OnionSkinNextFrames;
            }
        }
        finally
        {
            _updatingOnionSkinControls = false;
        }

        LayoutHeaderControls(CreateLayout(), available);
    }

    public void EnsureCurrentFrameVisible()
    {
        if (!IsHandleCreated && Width <= 0) return;
        var layout = CreateLayout();
        var capacity = VisibleFrameCapacity(layout);
        if (_currentFrame < _firstVisibleFrame)
        {
            SetFirstVisibleFrame(_currentFrame, invalidate: false);
        }
        else if (_currentFrame >= _firstVisibleFrame + capacity)
        {
            SetFirstVisibleFrame(_currentFrame - capacity + 1, invalidate: false);
        }
        else
        {
            SetFirstVisibleFrame(_firstVisibleFrame, invalidate: false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_timeline is not null) _timeline.Changed -= HandleTimelineChanged;
            _heightResizeTimer.Stop();
            _heightResizeTimer.Dispose();
            _layerContextMenu.Dispose();
            _frameContextMenu.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(BackColor);

        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        var layout = CreateLayout();
        LayoutHeaderControls(layout, IsOnionSkinControlsAvailable());
        DrawShell(graphics, layout);
        DrawRuler(graphics, layout);
        DrawTrackRows(graphics, layout);
        DrawPlayhead(graphics, layout);
        DrawHorizontalScroll(graphics, layout);
        DrawVerticalScroll(graphics, layout);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_timeline is null) return;
        if (_draggingHeightResize)
        {
            _heightResizeLayoutDirty = true;
            Invalidate();
            return;
        }
        RefreshOnionSkinControls();
        EnsureCurrentFrameVisible();
        EnsureActiveTrackVisible();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && HeightResizeHandleBounds().Contains(e.Location))
        {
            BeginHeightResize();
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            ShowContextMenu(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        Focus();
        var layout = CreateLayout();
        if (TryBeginOnionSkinRangeDrag(e.Location, layout)) return;
        if (TryHandleHeaderClick(e.Location, layout)) return;
        if (TryHandleHorizontalScrollMouseDown(e.Location, layout)) return;
        if (TryHandleVerticalScrollMouseDown(e.Location, layout)) return;

        if (TryGetTrackIndex(e.Location, layout, out var trackIndex))
        {
            if (e.X < layout.TrackLeft)
            {
                if (e.X < VisibilityColumnWidth)
                {
                    if (IsTrackLayer(trackIndex))
                    {
                        PrepareLayerTrackAction(trackIndex);
                        SetSelectedTrackVisibility(!IsTrackExplicitlyVisible(trackIndex));
                    }
                    else if (SetTrackVisibility(trackIndex, !IsTrackExplicitlyVisible(trackIndex)))
                    {
                        Invalidate();
                        LayerVisibilityChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (e.X < LayerControlsWidth)
                {
                    PrepareLayerTrackAction(trackIndex);
                    if (IsTrackLayer(trackIndex)) LayerLockRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (IsTrackCollapsible(trackIndex)
                    && LayerGroupDisclosureBounds(trackIndex, layout.RowTop + (VisibleTrackPosition(trackIndex) - _firstVisibleTrack) * RowHeight).Contains(e.Location))
                {
                    SetActiveTrack(trackIndex);
                    ToggleTrackGroupCollapsed(trackIndex);
                }
                else
                {
                    SelectLayerTrack(trackIndex, ModifierKeys);
                    if (IsTrackLayer(trackIndex) && (ModifierKeys & (Keys.Control | Keys.Shift)) == Keys.None)
                    {
                        _draggingLayer = true;
                        _draggedLayerTrackId = _timeline.Tracks[trackIndex].Id;
                        _layerDropTrack = trackIndex;
                        _layerDropPlacement = TimelineLayerDropPlacement.Before;
                        Capture = true;
                    }
                }
                return;
            }

            SetActiveTrack(trackIndex);
            BeginFrameSelection(trackIndex, FrameFromX(e.X, layout), ModifierKeys);
            _draggingFrameSelection = true;
            Capture = true;
            return;
        }

        if (layout.RulerBounds.Contains(e.Location) && e.X >= layout.TrackLeft)
        {
            _draggingPlayhead = true;
            Capture = true;
            CurrentFrame = FrameFromX(e.X, layout);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        var layout = CreateLayout();
        if (e.X >= LayerControlsWidth && e.X < layout.TrackLeft && TryGetTrackIndex(e.Location, layout, out var trackIndex))
        {
            if (IsTrackLayer(trackIndex))
            {
                PrepareLayerTrackAction(trackIndex);
                SetSelectedTrackVisibility(!IsTrackExplicitlyVisible(trackIndex));
            }
            else if (SetTrackVisibility(trackIndex, !IsTrackExplicitlyVisible(trackIndex)))
            {
                Invalidate();
                LayerVisibilityChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingHeightResize)
        {
            UpdateHeightResize();
            return;
        }
        if (HeightResizeHandleBounds().Contains(e.Location))
        {
            Cursor = Cursors.SizeNS;
            return;
        }

        var layout = CreateLayout();
        UpdateHover(e.Location, layout);

        if (_draggingHorizontalScroll)
        {
            DragHorizontalScroll(e.X, layout);
            return;
        }

        if (_draggingVerticalScroll)
        {
            DragVerticalScroll(e.Y, layout);
            return;
        }

        if (_draggingOnionSkinRangeHandle != TimelineOnionSkinRangeHandle.None)
        {
            UpdateOnionSkinRangeDrag(e.X, layout);
            return;
        }

        if (_draggingLayer)
        {
            if (TryGetTrackIndex(e.Location, layout, out var trackIndex))
            {
                _layerDropTrack = trackIndex;
                _layerDropPlacement = LayerDropPlacementFor(trackIndex, e.Y, layout);
                Invalidate();
            }
            return;
        }

        if (_draggingFrameSelection)
        {
            if (TryGetTrackIndex(e.Location, layout, out var trackIndex) && e.X >= layout.TrackLeft)
            {
                UpdateFrameSelection(trackIndex, FrameFromX(e.X, layout));
            }
            return;
        }

        if (!_draggingPlayhead) return;
        if (e.X < layout.TrackLeft)
        {
            SetFirstVisibleFrame(_firstVisibleFrame - 1);
        }
        else if (e.X >= layout.TrackRight)
        {
            SetFirstVisibleFrame(_firstVisibleFrame + 1);
        }

        CurrentFrame = FrameFromX(e.X, layout);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = Cursors.Default;
        if (_hoverFrame < 0 && _hoverTrack < 0) return;
        _hoverFrame = -1;
        _hoverTrack = -1;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Control && e.KeyCode == Keys.A)
        {
            SelectAllVisibleLayerTracks();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Escape && _draggingHeightResize)
        {
            _pendingHeightResize = null;
            _heightResizeTimer.Stop();
            Height = _heightResizeStartHeight;
            _heightResizeLayoutDirty = true;
            EndMouseDrag(canceled: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.F2 && GetActiveTrackIndex() >= 0 && SelectedLayerCount <= 1)
        {
            LayerRenameRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode is Keys.Left or Keys.Right && ToggleTrackGroupCollapsedFromKeyboard(e.KeyCode == Keys.Left))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode != Keys.Escape || _draggingOnionSkinRangeHandle == TimelineOnionSkinRangeHandle.None) return;
        EndMouseDrag(canceled: true);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void UpdateHover(Point location, TimelineLayout layout)
    {
        var frame = -1;
        var track = -1;
        if (location.X >= layout.TrackLeft
            && location.X < layout.TrackRight
            && (layout.RulerBounds.Contains(location) || TryGetTrackIndex(location, layout, out track)))
        {
            frame = FrameFromX(location.X, layout);
        }

        if (frame == _hoverFrame && track == _hoverTrack) return;
        _hoverFrame = frame;
        _hoverTrack = track;
        Cursor = HitTestOnionSkinRangeHandle(location, layout) == TimelineOnionSkinRangeHandle.None
            ? Cursors.Default
            : Cursors.SizeWE;
        Invalidate();
    }

    private Rectangle HeightResizeHandleBounds()
    {
        return new Rectangle(0, 0, Math.Max(0, Width), Math.Min(HeightResizeHandleHeight, Math.Max(0, Height)));
    }

    private void BeginHeightResize()
    {
        _draggingHeightResize = true;
        _heightResizeStartHeight = Height;
        _heightResizeStartScreenY = Cursor.Position.Y;
        _pendingHeightResize = null;
        _heightResizeLayoutDirty = false;
        _heightResizeTimer.Stop();
        Capture = true;
        Cursor = Cursors.SizeNS;
        Invalidate();
    }

    private void UpdateHeightResize()
    {
        var delta = Cursor.Position.Y - _heightResizeStartScreenY;
        var maximumHeight = Parent is { ClientSize.Height: > 0 } parent
            ? Math.Max(MinimumSize.Height, parent.ClientSize.Height - 148)
            : 720;
        var nextHeight = Math.Clamp(_heightResizeStartHeight - delta, MinimumSize.Height, maximumHeight);
        if (_pendingHeightResize is null && Height == nextHeight) return;
        _pendingHeightResize = nextHeight;
        if (!_heightResizeTimer.Enabled) _heightResizeTimer.Start();
    }

    private void TickHeightResize()
    {
        if (!_draggingHeightResize)
        {
            _heightResizeTimer.Stop();
            return;
        }

        ApplyPendingHeightResize();
    }

    private void ApplyPendingHeightResize()
    {
        if (_pendingHeightResize is not { } nextHeight) return;
        _pendingHeightResize = null;
        if (Height == nextHeight) return;
        Height = nextHeight;
        _heightResizeLayoutDirty = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        CommitLayerDrag();
        EndMouseDrag(canceled: false);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) EndMouseDrag(canceled: true);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var layout = CreateLayout();
        var notches = Math.Max(1, Math.Abs(e.Delta) / 120) * Math.Sign(e.Delta);
        if (e.X < layout.TrackLeft && VisibleTrackCount() > VisibleTrackCapacity(layout) && (ModifierKeys & Keys.Shift) == 0)
        {
            SetFirstVisibleTrack(_firstVisibleTrack - notches * 3);
        }
        else
        {
            SetFirstVisibleFrame(_firstVisibleFrame - notches * 3);
        }
    }

    private void DrawShell(Graphics graphics, TimelineLayout layout)
    {
        using var headerBrush = new SolidBrush(Theme.Panel);
        using var gutterBrush = new SolidBrush(Color.FromArgb(28, 31, 34));
        using var borderPen = new Pen(Color.FromArgb(82, 91, 96));
        using var softPen = new Pen(Color.FromArgb(48, 54, 58));
        using var titleFont = Theme.UiFont(10, FontStyle.Bold);

        graphics.FillRectangle(headerBrush, layout.HeaderBounds);
        graphics.FillRectangle(gutterBrush, layout.GutterBounds);
        graphics.DrawLine(borderPen, 0, HeaderHeight - 1, Width, HeaderHeight - 1);
        graphics.DrawLine(borderPen, layout.TrackLeft - 1, 0, layout.TrackLeft - 1, layout.ScrollTop);
        graphics.DrawLine(softPen, 0, layout.RowTop - 1, Width, layout.RowTop - 1);
        graphics.DrawLine(borderPen, 0, Height - 1, Width, Height - 1);
        DrawHeightResizeHandle(graphics);

        var allBounds = AllButtonBounds(layout);
        var soloBounds = SoloButtonBounds(layout);
        var addLayerBounds = AddLayerButtonBounds(layout);
        var titleRight = Math.Max(42, addLayerBounds.Left - 6);
        TextRenderer.DrawText(
            graphics,
            "Timeline",
            titleFont,
            Rectangle.FromLTRB(10, 1, titleRight, HeaderHeight - 1),
            Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        DrawHeaderButton(graphics, soloBounds, "Solo");
        DrawHeaderButton(graphics, allBounds, "All");
        DrawHeaderButton(graphics, addLayerBounds, "+");

        var fpsControlsBounds = PlaybackFpsControlsBounds(layout);
        var onionControlsBounds = OnionSkinControlsBounds(layout, fpsControlsBounds);
        var firstControlsLeft = new[]
        {
            fpsControlsBounds.IsEmpty ? layout.TrackRight : fpsControlsBounds.Left,
            onionControlsBounds.IsEmpty ? layout.TrackRight : onionControlsBounds.Left
        }.Min();
        var summaryRight = firstControlsLeft - 8;
        var name = _sceneDefinition?.Name ?? _drawingObjectDefinition?.Name ?? "Drawing Timeline";
        var summary = $"{name}    Frame {CurrentFrame} / {Math.Max(0, FrameCount - 1)}";
        TextRenderer.DrawText(
            graphics,
            summary,
            Font,
            Rectangle.FromLTRB(layout.TrackLeft + 8, 1, Math.Max(layout.TrackLeft + 8, summaryRight), HeaderHeight - 1),
            Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private void DrawHeightResizeHandle(Graphics graphics)
    {
        var y = Math.Max(2, HeightResizeHandleHeight / 2);
        var width = Math.Min(66, Math.Max(28, Width / 8));
        var left = Math.Max(0, (Width - width) / 2);
        var color = _draggingHeightResize ? Theme.Accent : Color.FromArgb(194, 174, 212, 215);
        using var line = new Pen(color, 1.2f);
        graphics.DrawLine(line, left, y, left + width, y);
        for (var x = left + 4; x < left + width - 2; x += 6)
        {
            graphics.DrawLine(line, x, y - 2, x, y + 2);
        }
    }

    private void DrawRuler(Graphics graphics, TimelineLayout layout)
    {
        using var rulerBrush = new SolidBrush(Color.FromArgb(23, 26, 29));
        using var majorBrush = new SolidBrush(Color.FromArgb(32, 37, 40));
        using var selectedBrush = new SolidBrush(Color.FromArgb(150, 177, 68, 65));
        using var hoverBrush = new SolidBrush(Color.FromArgb(34, Theme.Accent));
        using var gridPen = new Pen(Color.FromArgb(64, 72, 77));
        using var minorPen = new Pen(Color.FromArgb(48, 55, 59));
        using var rulerFont = Theme.UiFont(7.5f);

        graphics.FillRectangle(rulerBrush, layout.RulerBounds);
        TextRenderer.DrawText(
            graphics,
            "Layer",
            Font,
            new Rectangle(VisibilityColumnWidth + 5, HeaderHeight, Math.Max(20, layout.TrackLeft - VisibilityColumnWidth - 17), RulerHeight),
            Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var state = graphics.Save();
        graphics.SetClip(Rectangle.FromLTRB(layout.TrackLeft, HeaderHeight, layout.TrackRight, layout.RowTop));
        var columns = VisibleFrameDrawCount(layout);
        for (var column = 0; column < columns; column++)
        {
            var frame = _firstVisibleFrame + column;
            var x = layout.TrackLeft + column * FrameCellWidth;
            var bounds = new Rectangle(x, HeaderHeight, FrameCellWidth, RulerHeight);
            var inTimelineRange = frame <= EndFrame;
            var major = frame == StartFrame || frame % 5 == 0;
            if (inTimelineRange && major) graphics.FillRectangle(majorBrush, bounds);
            if (inTimelineRange && frame == _hoverFrame && frame != CurrentFrame) graphics.FillRectangle(hoverBrush, bounds);
            if (inTimelineRange && frame == CurrentFrame) graphics.FillRectangle(selectedBrush, bounds);

            graphics.DrawLine(inTimelineRange && major ? gridPen : minorPen, x, major ? HeaderHeight + 5 : HeaderHeight + 17, x, layout.RowTop - 1);
            if (!major) continue;
            TextRenderer.DrawText(
                graphics,
                frame.ToString(),
                rulerFont,
                new Rectangle(x + 2, HeaderHeight + 1, FrameCellWidth * 2 - 2, RulerHeight - 4),
                inTimelineRange && frame == CurrentFrame ? Color.White : inTimelineRange ? Theme.Muted : Color.FromArgb(118, Theme.Muted),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }

        graphics.DrawLine(gridPen, layout.TrackRight - 1, HeaderHeight, layout.TrackRight - 1, layout.RowTop);
        DrawOnionSkinRangeHandles(graphics, layout);
        graphics.Restore(state);
    }

    private void DrawOnionSkinRangeHandles(Graphics graphics, TimelineLayout layout)
    {
        if (!TryGetOnionSkinRange(out var previousFrames, out var nextFrames, out var enabled)) return;

        var previousFrame = Math.Max(StartFrame, CurrentFrame - previousFrames);
        var nextFrame = Math.Min(EndFrame, CurrentFrame + nextFrames);
        var previousX = FrameCenterX(previousFrame, layout);
        var currentX = FrameCenterX(CurrentFrame, layout);
        var nextX = FrameCenterX(nextFrame, layout);
        if (float.IsNaN(currentX)) return;

        var opacity = enabled ? 232 : 116;
        var previousColor = Color.FromArgb(opacity, 224, 134, 126);
        var nextColor = Color.FromArgb(opacity, 111, 195, 218);
        var handleY = HeaderHeight + RulerHeight - ScaleTimelineMetric(7);
        using var previousPen = new Pen(previousColor, ScaleTimelineMetric(1));
        using var nextPen = new Pen(nextColor, ScaleTimelineMetric(1));
        if (!float.IsNaN(previousX) && previousFrame != CurrentFrame)
        {
            graphics.DrawLine(previousPen, previousX, handleY, currentX, handleY);
        }
        if (!float.IsNaN(nextX) && nextFrame != CurrentFrame)
        {
            graphics.DrawLine(nextPen, currentX, handleY, nextX, handleY);
        }

        DrawOnionSkinRangeHandle(graphics, OnionSkinRangeHandleBounds(TimelineOnionSkinRangeHandle.Previous, layout), previousColor, pointsRight: true);
        DrawOnionSkinRangeHandle(graphics, OnionSkinRangeHandleBounds(TimelineOnionSkinRangeHandle.Next, layout), nextColor, pointsRight: false);
    }

    private static void DrawOnionSkinRangeHandle(Graphics graphics, Rectangle bounds, Color color, bool pointsRight)
    {
        if (bounds.IsEmpty) return;
        var centerX = bounds.Left + bounds.Width / 2f;
        var centerY = bounds.Top + bounds.Height / 2f;
        var direction = pointsRight ? 1f : -1f;
        var halfWidth = Math.Max(3f, bounds.Width * 0.35f);
        var halfHeight = Math.Max(4f, bounds.Height * 0.35f);
        var points = new[]
        {
            new PointF(centerX + direction * halfWidth, centerY),
            new PointF(centerX - direction * halfWidth, centerY - halfHeight),
            new PointF(centerX - direction * halfWidth, centerY + halfHeight)
        };
        using var fill = new SolidBrush(color);
        using var outline = new Pen(Color.FromArgb(Math.Min(255, color.A + 20), color), 1f);
        graphics.FillPolygon(fill, points);
        graphics.DrawPolygon(outline, points);
    }

    private bool TryBeginOnionSkinRangeDrag(Point location, TimelineLayout layout)
    {
        var handle = HitTestOnionSkinRangeHandle(location, layout);
        if (handle == TimelineOnionSkinRangeHandle.None) return false;

        _draggingOnionSkinRangeHandle = handle;
        OnionSkinRangeInteractionStarted?.Invoke(this, EventArgs.Empty);
        Capture = true;
        Invalidate();
        return true;
    }

    private void UpdateOnionSkinRangeDrag(int x, TimelineLayout layout)
    {
        if (_draggingOnionSkinRangeHandle == TimelineOnionSkinRangeHandle.None
            || !TryGetOnionSkinRange(out var previousFrames, out var nextFrames, out _))
        {
            return;
        }

        var targetFrame = FrameFromX(x, layout);
        if (_draggingOnionSkinRangeHandle == TimelineOnionSkinRangeHandle.Previous)
        {
            previousFrames = Math.Clamp(
                CurrentFrame - targetFrame,
                0,
                Math.Min(VectorScene.MaximumOnionSkinFrames, CurrentFrame - StartFrame));
        }
        else
        {
            nextFrames = Math.Clamp(
                targetFrame - CurrentFrame,
                0,
                Math.Min(VectorScene.MaximumOnionSkinFrames, EndFrame - CurrentFrame));
        }

        OnionSkinRangeChanged?.Invoke(this, new TimelineOnionSkinRangeChangedEventArgs(previousFrames, nextFrames));
        Invalidate();
    }

    private TimelineOnionSkinRangeHandle HitTestOnionSkinRangeHandle(Point location, TimelineLayout layout)
    {
        if (!layout.RulerBounds.Contains(location) || !TryGetOnionSkinRange(out _, out _, out _)) return TimelineOnionSkinRangeHandle.None;
        var previous = OnionSkinRangeHandleBounds(TimelineOnionSkinRangeHandle.Previous, layout);
        var next = OnionSkinRangeHandleBounds(TimelineOnionSkinRangeHandle.Next, layout);
        if (previous.Contains(location) && next.Contains(location))
        {
            return location.X <= FrameCenterX(CurrentFrame, layout)
                ? TimelineOnionSkinRangeHandle.Previous
                : TimelineOnionSkinRangeHandle.Next;
        }

        if (previous.Contains(location)) return TimelineOnionSkinRangeHandle.Previous;
        return next.Contains(location) ? TimelineOnionSkinRangeHandle.Next : TimelineOnionSkinRangeHandle.None;
    }

    private Rectangle OnionSkinRangeHandleBounds(TimelineOnionSkinRangeHandle handle, TimelineLayout layout)
    {
        if (!TryGetOnionSkinRange(out var previousFrames, out var nextFrames, out _)) return Rectangle.Empty;
        var frame = handle switch
        {
            TimelineOnionSkinRangeHandle.Previous => Math.Max(StartFrame, CurrentFrame - previousFrames),
            TimelineOnionSkinRangeHandle.Next => Math.Min(EndFrame, CurrentFrame + nextFrames),
            _ => -1
        };
        var centerX = FrameCenterX(frame, layout);
        if (float.IsNaN(centerX)) return Rectangle.Empty;

        var width = ScaleTimelineMetric(14);
        return new Rectangle(
            (int)Math.Round(centerX - width * 0.5f),
            HeaderHeight + ScaleTimelineMetric(2),
            width,
            Math.Max(1, RulerHeight - ScaleTimelineMetric(4)));
    }

    private bool TryGetOnionSkinRange(out int previousFrames, out int nextFrames, out bool enabled)
    {
        previousFrames = 0;
        nextFrames = 0;
        enabled = false;
        var drawingScene = DrawingScene();
        var activeTrack = GetActiveTrackIndex();
        if (drawingScene is null || activeTrack < 0) return false;
        var layer = FindLayerIndex(drawingScene, _timeline.Tracks[activeTrack].TargetId);
        if (layer < 0) return false;

        previousFrames = drawingScene.OnionSkinPreviousFrames;
        nextFrames = drawingScene.OnionSkinNextFrames;
        enabled = drawingScene.LayerOnionSkin[layer];
        return true;
    }

    private float FrameCenterX(int frame, TimelineLayout layout)
    {
        if (frame < _firstVisibleFrame || frame >= _firstVisibleFrame + VisibleFrameDrawCount(layout)) return float.NaN;
        var center = layout.TrackLeft + (frame - _firstVisibleFrame) * FrameCellWidth + FrameCellWidth * 0.5f;
        return center < layout.TrackLeft || center >= layout.TrackRight ? float.NaN : center;
    }

    private void DrawTrackRows(Graphics graphics, TimelineLayout layout)
    {
        using var rowBrush = new SolidBrush(Color.FromArgb(30, 34, 37));
        using var alternateRowBrush = new SolidBrush(Color.FromArgb(34, 38, 41));
        using var activeRowBrush = new SolidBrush(Color.FromArgb(38, 65, 62));
        using var selectedLayerRowBrush = new SolidBrush(Color.FromArgb(46, Theme.Accent));
        using var activeEdgeBrush = new SolidBrush(Theme.Accent);
        using var populatedExposureBrush = new SolidBrush(Color.FromArgb(86, 79, 179, 162));
        using var blankExposureBrush = new SolidBrush(Color.FromArgb(68, 120, 130, 133));
        using var populatedLinePen = new Pen(Color.FromArgb(196, 112, 218, 201));
        using var blankLinePen = new Pen(Color.FromArgb(170, 156, 166, 168));
        using var gridPen = new Pen(Color.FromArgb(47, 54, 58));
        using var majorGridPen = new Pen(Color.FromArgb(61, 69, 74));
        using var selectedCellPen = new Pen(Color.FromArgb(238, 206, 164, 81), 2);
        using var hiddenBrush = new SolidBrush(Color.FromArgb(126, 12, 14, 16));
        using var currentColumnBrush = new SolidBrush(Color.FromArgb(30, 240, 94, 91));
        using var hoverCellBrush = new SolidBrush(Color.FromArgb(27, Theme.Accent));
        using var selectionBrush = new SolidBrush(Color.FromArgb(56, Theme.Accent));
        using var layerDropFill = new SolidBrush(Color.FromArgb(52, Theme.Accent));
        using var layerDropPen = new Pen(Theme.Accent, 2f);
        using var eyePen = new Pen(Color.FromArgb(214, 224, 224, 224), 1.35f);
        using var hiddenEyePen = new Pen(Color.FromArgb(130, 144, 148, 148), 1.2f);
        using var lockPen = new Pen(Color.FromArgb(212, 224, 224, 224), 1.25f);
        using var unlockedLockPen = new Pen(Color.FromArgb(126, 144, 148, 148), 1.2f);

        var visibleTracks = VisibleTrackIndices();
        var visibleRows = VisibleTrackCapacity(layout);
        var activeTrack = GetActiveTrackIndex();
        var rowCount = Math.Min(visibleRows, Math.Max(0, visibleTracks.Count - _firstVisibleTrack));

        if (rowCount == 0)
        {
            TextRenderer.DrawText(
                graphics,
                "No timeline tracks",
                Font,
                layout.GridBounds,
                Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        var graphicsState = graphics.Save();
        graphics.SetClip(Rectangle.FromLTRB(0, layout.RowTop, layout.TrackRight, layout.RowBottom));
        for (var visibleRow = 0; visibleRow < rowCount; visibleRow++)
        {
            var trackIndex = visibleTracks[_firstVisibleTrack + visibleRow];
            var track = _timeline.Tracks[trackIndex];
            var y = layout.RowTop + visibleRow * RowHeight;
            var rowBounds = new Rectangle(0, y, layout.TrackRight, RowHeight);
            var active = trackIndex == activeTrack;
            var selectedLayer = IsTrackSelected(trackIndex);
            graphics.FillRectangle(active ? activeRowBrush : visibleRow % 2 == 0 ? rowBrush : alternateRowBrush, rowBounds);
            if (selectedLayer && !active)
            {
                graphics.FillRectangle(selectedLayerRowBrush, 3, y + 1, Math.Max(0, layout.TrackLeft - 3), RowHeight - 2);
            }
            if (active) graphics.FillRectangle(activeEdgeBrush, 0, y, 3, RowHeight);

            var visible = IsTrackVisible(trackIndex);
            var locked = IsTrackLocked(trackIndex);
            DrawVisibilityIcon(graphics, visible, 14, y + RowHeight / 2f, visible ? eyePen : hiddenEyePen);
            DrawLockIcon(graphics, locked, VisibilityColumnWidth + LockColumnWidth / 2, y + RowHeight / 2, lockPen, unlockedLockPen);
            using (var layerColorBrush = new SolidBrush(GetTrackColor(trackIndex)))
            {
                graphics.FillRectangle(layerColorBrush, LayerControlsWidth + 3, y + 5, 4, RowHeight - 10);
            }
            var layerDepth = GetTrackDisplayDepth(trackIndex);
            var labelLeft = GetTrackLabelLeft(trackIndex);
            DrawLayerHierarchyGuide(graphics, layerDepth, y, labelLeft);
            if (IsTrackCollapsible(trackIndex)) DrawLayerGroupDisclosure(graphics, LayerGroupDisclosureBounds(trackIndex, y), IsTrackCollapsed(trackIndex));
            DrawLayerKindGlyph(graphics, GetTrackLayerKind(trackIndex), labelLeft, y + RowHeight / 2);
            TextRenderer.DrawText(
                graphics,
                GetTrackName(trackIndex),
                Font,
                new Rectangle(labelLeft + 15, y, Math.Max(16, layout.TrackLeft - labelLeft - VerticalScrollWidth - 20), RowHeight),
                !visible || locked ? Theme.Muted : active ? Theme.Text : Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            if (_draggingLayer && trackIndex == _layerDropTrack)
            {
                if (_layerDropPlacement is TimelineLayerDropPlacement.Inside or TimelineLayerDropPlacement.Mask)
                {
                    var inset = Math.Min(layout.TrackLeft - 10, LayerControlsWidth + 17 + GetTrackDisplayDepth(trackIndex) * 12);
                    graphics.FillRectangle(layerDropFill, inset, y + 2, Math.Max(8, layout.TrackRight - inset - 3), RowHeight - 4);
                    graphics.DrawRectangle(layerDropPen, inset, y + 2, Math.Max(7, layout.TrackRight - inset - 4), RowHeight - 5);
                }
                else
                {
                    var dropY = _layerDropPlacement == TimelineLayerDropPlacement.Before ? y : y + RowHeight;
                    graphics.DrawLine(layerDropPen, 3, dropY, layout.TrackRight - 1, dropY);
                }
            }

            DrawTrackCells(
                graphics,
                layout,
                track,
                trackIndex,
                y,
                active,
                visible,
                populatedExposureBrush,
                blankExposureBrush,
                populatedLinePen,
                blankLinePen,
                gridPen,
                majorGridPen,
                selectedCellPen,
                hiddenBrush,
                currentColumnBrush,
                hoverCellBrush,
                selectionBrush);
        }

        graphics.Restore(graphicsState);
    }

    private void DrawTrackCells(
        Graphics graphics,
        TimelineLayout layout,
        AnimationTimelineTrack track,
        int trackIndex,
        int y,
        bool active,
        bool visible,
        Brush populatedExposureBrush,
        Brush blankExposureBrush,
        Pen populatedLinePen,
        Pen blankLinePen,
        Pen gridPen,
        Pen majorGridPen,
        Pen selectedCellPen,
        Brush hiddenBrush,
        Brush currentColumnBrush,
        Brush hoverCellBrush,
        Brush selectionBrush)
    {
        var state = graphics.Save();
        graphics.SetClip(new Rectangle(layout.TrackLeft, y, layout.TrackRight - layout.TrackLeft, RowHeight));
        var columns = VisibleFrameDrawCount(layout);
        for (var column = 0; column < columns; column++)
        {
            var frame = _firstVisibleFrame + column;
            var x = layout.TrackLeft + column * FrameCellWidth;
            var cellBounds = new Rectangle(x, y, FrameCellWidth, RowHeight);
            var inTimelineRange = frame <= EndFrame;
            var selected = false;
            if (inTimelineRange)
            {
                if (trackIndex == _hoverTrack && frame == _hoverFrame && frame != CurrentFrame)
                {
                    graphics.FillRectangle(hoverCellBrush, cellBounds);
                }
                if (frame == CurrentFrame) graphics.FillRectangle(currentColumnBrush, cellBounds);

                selected = _selectedFrameCells.Contains(new TimelineFrameCell(track.Id, frame));
                if (selected) graphics.FillRectangle(selectionBrush, cellBounds);

                var exposure = track.EvaluateExposure(frame);
                if (exposure.SourceKind is { } sourceKind)
                {
                    var exposureBounds = new Rectangle(x + 1, y + 5, FrameCellWidth, RowHeight - 10);
                    var exposureBrush = sourceKind == TimelineKeyframeKind.Populated
                        ? populatedExposureBrush
                        : blankExposureBrush;
                    var exposurePen = sourceKind == TimelineKeyframeKind.Populated
                        ? populatedLinePen
                        : blankLinePen;
                    graphics.FillRectangle(exposureBrush, exposureBounds);
                    graphics.DrawLine(exposurePen, x + 1, y + RowHeight / 2, x + FrameCellWidth, y + RowHeight / 2);
                    if (frame == exposure.EndFrame)
                    {
                        graphics.DrawLine(exposurePen, x + FrameCellWidth - 2, y + 6, x + FrameCellWidth - 2, y + RowHeight - 6);
                    }

                    if (exposure.IsKeyframe)
                    {
                        DrawKeyframeMarker(graphics, sourceKind, x + FrameCellWidth / 2f, y + RowHeight / 2f);
                    }
                }
            }

            graphics.DrawLine(inTimelineRange && frame % 5 == 0 ? majorGridPen : gridPen, x, y, x, y + RowHeight);
            graphics.DrawLine(gridPen, x, y + RowHeight - 1, x + FrameCellWidth, y + RowHeight - 1);
            if (inTimelineRange && !visible) graphics.FillRectangle(hiddenBrush, cellBounds);

            if (selected || active && frame == CurrentFrame)
            {
                graphics.DrawRectangle(selectedCellPen, x + 1, y + 1, FrameCellWidth - 3, RowHeight - 3);
            }
        }

        graphics.Restore(state);
    }

    private static void DrawKeyframeMarker(Graphics graphics, TimelineKeyframeKind kind, float centerX, float centerY)
    {
        const float radius = 3.8f;
        var bounds = new RectangleF(centerX - radius, centerY - radius, radius * 2, radius * 2);
        using var outlinePen = new Pen(Color.FromArgb(235, 231, 237, 235), 1.2f);
        if (kind == TimelineKeyframeKind.Populated)
        {
            using var fillBrush = new SolidBrush(Color.FromArgb(238, 231, 237, 235));
            graphics.FillEllipse(fillBrush, bounds);
            graphics.DrawEllipse(outlinePen, bounds);
        }
        else
        {
            using var fillBrush = new SolidBrush(Color.FromArgb(30, 34, 37));
            graphics.FillEllipse(fillBrush, bounds);
            graphics.DrawEllipse(outlinePen, bounds);
        }
    }

    private void DrawPlayhead(Graphics graphics, TimelineLayout layout)
    {
        if (CurrentFrame < _firstVisibleFrame) return;
        var column = CurrentFrame - _firstVisibleFrame;
        if (column >= VisibleFrameDrawCount(layout)) return;

        var centerX = layout.TrackLeft + column * FrameCellWidth + FrameCellWidth / 2f;
        if (centerX < layout.TrackLeft || centerX >= layout.TrackRight) return;

        var pulse = _isPlaying ? 0.55f : 0f;
        var playhead = Color.FromArgb(242, 94, 91);
        using var glowPen = new Pen(Color.FromArgb((int)Math.Round(42 + pulse * 68), playhead), 4.5f + pulse * 2.5f);
        using var linePen = new Pen(playhead, 1.5f + pulse * 0.35f);
        using var fillBrush = new SolidBrush(playhead);
        if (_isPlaying) graphics.DrawLine(glowPen, centerX, HeaderHeight, centerX, layout.RowBottom);
        graphics.DrawLine(linePen, centerX, HeaderHeight, centerX, layout.RowBottom);
        var handle = new[]
        {
            new PointF(centerX - 5, HeaderHeight),
            new PointF(centerX + 5, HeaderHeight),
            new PointF(centerX + 5, HeaderHeight + 8),
            new PointF(centerX, HeaderHeight + 13),
            new PointF(centerX - 5, HeaderHeight + 8)
        };
        graphics.FillPolygon(fillBrush, handle);
    }

    private void DrawHorizontalScroll(Graphics graphics, TimelineLayout layout)
    {
        var scroll = GetHorizontalScrollGeometry(layout);
        using var backgroundBrush = new SolidBrush(Color.FromArgb(20, 23, 25));
        using var buttonBrush = new SolidBrush(Color.FromArgb(35, 40, 43));
        using var thumbBrush = new SolidBrush(Color.FromArgb(75, 85, 90));
        using var borderPen = new Pen(Color.FromArgb(55, 63, 68));
        using var arrowBrush = new SolidBrush(Color.FromArgb(190, 202, 202));

        graphics.FillRectangle(backgroundBrush, scroll.Bounds);
        graphics.FillRectangle(buttonBrush, scroll.DecreaseButton);
        graphics.FillRectangle(buttonBrush, scroll.IncreaseButton);
        graphics.DrawRectangle(borderPen, scroll.Bounds.X, scroll.Bounds.Y, Math.Max(0, scroll.Bounds.Width - 1), Math.Max(0, scroll.Bounds.Height - 1));
        if (scroll.Thumb.Width > 0) graphics.FillRectangle(thumbBrush, scroll.Thumb);

        DrawHorizontalArrow(graphics, scroll.DecreaseButton, pointsLeft: true, arrowBrush);
        DrawHorizontalArrow(graphics, scroll.IncreaseButton, pointsLeft: false, arrowBrush);
    }

    private void DrawVerticalScroll(Graphics graphics, TimelineLayout layout)
    {
        var scroll = GetVerticalScrollGeometry(layout);
        if (scroll.MaxValue <= 0 || scroll.Bounds.Height <= 0) return;
        using var backgroundBrush = new SolidBrush(Color.FromArgb(19, 22, 24));
        using var thumbBrush = new SolidBrush(Color.FromArgb(75, 85, 90));
        graphics.FillRectangle(backgroundBrush, scroll.Bounds);
        graphics.FillRectangle(thumbBrush, scroll.Thumb);
    }

    private static void DrawHorizontalArrow(Graphics graphics, Rectangle bounds, bool pointsLeft, Brush brush)
    {
        var centerX = bounds.Left + bounds.Width / 2f;
        var centerY = bounds.Top + bounds.Height / 2f;
        var direction = pointsLeft ? 1 : -1;
        var points = new[]
        {
            new PointF(centerX + direction * 3, centerY - 4),
            new PointF(centerX + direction * 3, centerY + 4),
            new PointF(centerX - direction * 2, centerY)
        };
        graphics.FillPolygon(brush, points);
    }

    private bool TryHandleHeaderClick(Point point, TimelineLayout layout)
    {
        if (AddLayerButtonBounds(layout).Contains(point))
        {
            AddLayerRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (SoloButtonBounds(layout).Contains(point))
        {
            SoloActiveTrack();
            return true;
        }

        if (AllButtonBounds(layout).Contains(point))
        {
            ShowAllTracks();
            return true;
        }

        return false;
    }

    private bool TryHandleHorizontalScrollMouseDown(Point point, TimelineLayout layout)
    {
        var scroll = GetHorizontalScrollGeometry(layout);
        if (!scroll.Bounds.Contains(point)) return false;

        if (scroll.DecreaseButton.Contains(point))
        {
            SetFirstVisibleFrame(_firstVisibleFrame - 1);
        }
        else if (scroll.IncreaseButton.Contains(point))
        {
            SetFirstVisibleFrame(_firstVisibleFrame + 1);
        }
        else if (scroll.Thumb.Contains(point) && scroll.MaxValue > 0)
        {
            _draggingHorizontalScroll = true;
            _horizontalScrollDragOffset = point.X - scroll.Thumb.Left;
            Capture = true;
        }
        else if (point.X < scroll.Thumb.Left)
        {
            SetFirstVisibleFrame(_firstVisibleFrame - VisibleFrameCapacity(layout));
        }
        else if (point.X > scroll.Thumb.Right)
        {
            SetFirstVisibleFrame(_firstVisibleFrame + VisibleFrameCapacity(layout));
        }

        return true;
    }

    private bool TryHandleVerticalScrollMouseDown(Point point, TimelineLayout layout)
    {
        var scroll = GetVerticalScrollGeometry(layout);
        if (scroll.MaxValue <= 0 || !scroll.Bounds.Contains(point)) return false;

        if (scroll.Thumb.Contains(point))
        {
            _draggingVerticalScroll = true;
            _verticalScrollDragOffset = point.Y - scroll.Thumb.Top;
            Capture = true;
        }
        else if (point.Y < scroll.Thumb.Top)
        {
            SetFirstVisibleTrack(_firstVisibleTrack - VisibleTrackCapacity(layout));
        }
        else
        {
            SetFirstVisibleTrack(_firstVisibleTrack + VisibleTrackCapacity(layout));
        }

        return true;
    }

    private void DragHorizontalScroll(int mouseX, TimelineLayout layout)
    {
        var scroll = GetHorizontalScrollGeometry(layout);
        var travel = scroll.Track.Width - scroll.Thumb.Width;
        if (scroll.MaxValue <= 0 || travel <= 0) return;
        var thumbX = Math.Clamp(mouseX - _horizontalScrollDragOffset, scroll.Track.Left, scroll.Track.Right - scroll.Thumb.Width);
        var ratio = (thumbX - scroll.Track.Left) / (double)travel;
        SetFirstVisibleFrame(StartFrame + (int)Math.Round(ratio * scroll.MaxValue));
    }

    private void DragVerticalScroll(int mouseY, TimelineLayout layout)
    {
        var scroll = GetVerticalScrollGeometry(layout);
        var travel = scroll.Bounds.Height - scroll.Thumb.Height;
        if (scroll.MaxValue <= 0 || travel <= 0) return;
        var thumbY = Math.Clamp(mouseY - _verticalScrollDragOffset, scroll.Bounds.Top, scroll.Bounds.Bottom - scroll.Thumb.Height);
        var ratio = (thumbY - scroll.Bounds.Top) / (double)travel;
        SetFirstVisibleTrack((int)Math.Round(ratio * scroll.MaxValue));
    }

    private void EndMouseDrag(bool canceled = false)
    {
        if (_draggingHeightResize)
        {
            ApplyPendingHeightResize();
            _heightResizeTimer.Stop();
            if (_heightResizeLayoutDirty)
            {
                _heightResizeLayoutDirty = false;
                RefreshOnionSkinControls();
                EnsureCurrentFrameVisible();
                EnsureActiveTrackVisible();
            }
        }

        if (_draggingOnionSkinRangeHandle != TimelineOnionSkinRangeHandle.None)
        {
            if (canceled) OnionSkinRangeInteractionCanceled?.Invoke(this, EventArgs.Empty);
            else OnionSkinRangeInteractionCompleted?.Invoke(this, EventArgs.Empty);
        }

        _draggingPlayhead = false;
        _draggingFrameSelection = false;
        _draggingLayer = false;
        _draggingHorizontalScroll = false;
        _draggingVerticalScroll = false;
        _draggingHeightResize = false;
        _draggingOnionSkinRangeHandle = TimelineOnionSkinRangeHandle.None;
        _draggedLayerTrackId = null;
        _layerDropTrack = -1;
        _layerDropPlacement = TimelineLayerDropPlacement.Before;
        if (Capture) Capture = false;
    }

    private ToolStripMenuItem CreateContextMenuItem(string text, TimelineCommand command, Keys shortcut)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => RequestCommand(command))
        {
            ShortcutKeys = shortcut
        };
        return item;
    }

    private void ShowContextMenu(Point location)
    {
        var layout = CreateLayout();
        if (TryGetTrackIndex(location, layout, out var trackIndex))
        {
            if (location.X < layout.TrackLeft)
            {
                PrepareLayerTrackAction(trackIndex);
                _layerContextMenu.Show(this, location);
            }
            else
            {
                SetActiveTrack(trackIndex);
                var frame = FrameFromX(location.X, layout);
                var cell = new TimelineFrameCell(_timeline.Tracks[trackIndex].Id, frame);
                if (!_selectedFrameCells.Contains(cell)) SetFrameSelection([cell], cell);
                CurrentFrame = frame;
                _frameContextMenu.Show(this, location);
            }

            return;
        }

        if (layout.RulerBounds.Contains(location) && location.X >= layout.TrackLeft)
        {
            CurrentFrame = FrameFromX(location.X, layout);
            _frameContextMenu.Show(this, location);
        }
    }

    private void HandleLayerContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var hasTrack = GetActiveTrackIndex() >= 0;
        var activeTrack = GetActiveTrackIndex();
        var isLayerTrack = hasTrack && IsTrackLayer(activeTrack);
        var hasLayerContext = hasTrack && isLayerTrack;
        var selectedLayerCount = SelectedLayerCount;
        var hasSingleLayerSelection = selectedLayerCount == 1;
        var allSelectedLayersLocked = selectedLayerCount > 0 && SelectedLayerTrackIndices().All(IsTrackLocked);
        var activeVisiblePosition = VisibleTrackPosition(activeTrack);
        var visibleTrackCount = VisibleTrackCount();
        _newDrawingLayerMenuItem.Enabled = hasLayerContext && hasSingleLayerSelection;
        _newFolderLayerMenuItem.Enabled = hasLayerContext && hasSingleLayerSelection && DrawingScene() is not null;
        _newMaskLayerMenuItem.Enabled = hasLayerContext && hasSingleLayerSelection && DrawingScene() is not null;
        var canToggleLayerGroup = hasLayerContext && hasSingleLayerSelection && IsTrackCollapsible(activeTrack);
        _toggleFolderMenuItem.Enabled = canToggleLayerGroup;
        var layerGroupName = IsTrackMaskGroup(activeTrack) ? "Mask" : "Folder";
        _toggleFolderMenuItem.Text = IsTrackCollapsed(activeTrack) ? $"Expand {layerGroupName}" : $"Collapse {layerGroupName}";
        _moveLayerUpMenuItem.Enabled = hasLayerContext && hasSingleLayerSelection && activeVisiblePosition > 0;
        _moveLayerDownMenuItem.Enabled = hasLayerContext && hasSingleLayerSelection && activeVisiblePosition >= 0 && activeVisiblePosition < visibleTrackCount - 1;
        _showSelectedLayersMenuItem.Enabled = hasLayerContext && selectedLayerCount > 0;
        _hideSelectedLayersMenuItem.Enabled = hasLayerContext && selectedLayerCount > 0;
        _renameLayerMenuItem.Enabled = hasLayerContext && hasSingleLayerSelection;
        _layerColorMenuItem.Enabled = hasLayerContext && selectedLayerCount > 0;
        _layerColorMenuItem.Text = selectedLayerCount > 1 ? "Layer Color for Selected..." : "Layer Color...";
        _lockLayerMenuItem.Enabled = hasLayerContext && selectedLayerCount > 0 && DrawingScene() is not null;
        _lockLayerMenuItem.Text = selectedLayerCount > 1
            ? allSelectedLayersLocked ? "Unlock Selected Layers" : "Lock Selected Layers"
            : allSelectedLayersLocked ? "Unlock Layer" : "Lock Layer";
        _lockLayerMenuItem.Checked = hasLayerContext && allSelectedLayersLocked;
    }

    private void HandleFrameContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var hasTrack = GetActiveTrackIndex() >= 0;
        var hasFrames = CommandCells().Count > 0;
        _copyFramesMenuItem.Enabled = hasTrack && hasFrames;
        _pasteFramesMenuItem.Enabled = hasTrack;
        _insertFramesMenuItem.Enabled = hasTrack && hasFrames;
        _deleteFramesMenuItem.Enabled = hasTrack && hasFrames;
        _insertKeyframesMenuItem.Enabled = hasTrack && hasFrames;
        _insertBlankKeyframesMenuItem.Enabled = hasTrack && hasFrames;
        _clearKeyframesMenuItem.Enabled = hasTrack && hasFrames;
    }

    private void RequestCommand(TimelineCommand command)
    {
        var cells = CommandCells();
        if (cells.Count == 0) return;
        CommandRequested?.Invoke(this, new TimelineCommandRequestedEventArgs(command, cells));
    }

    private void RequestLayerMove(int direction)
    {
        var sourceIndex = GetActiveTrackIndex();
        if (sourceIndex < 0) return;
        var visibleTracks = VisibleTrackIndices();
        var sourcePosition = visibleTracks.IndexOf(sourceIndex);
        if (sourcePosition < 0) return;
        var destinationPosition = Math.Clamp(sourcePosition + direction, 0, visibleTracks.Count - 1);
        if (destinationPosition == sourcePosition) return;
        var destinationIndex = visibleTracks[destinationPosition];
        LayerMoveRequested?.Invoke(this, new TimelineLayerMoveRequestedEventArgs(
            _timeline.Tracks[sourceIndex].Id,
            _timeline.Tracks[destinationIndex].Id,
            direction < 0 ? TimelineLayerDropPlacement.Before : TimelineLayerDropPlacement.After));
    }

    private void CommitLayerDrag()
    {
        if (!_draggingLayer || string.IsNullOrWhiteSpace(_draggedLayerTrackId) || _layerDropTrack < 0) return;
        var sourceIndex = -1;
        for (var index = 0; index < TrackCount; index++)
        {
            if (!string.Equals(_timeline.Tracks[index].Id, _draggedLayerTrackId, StringComparison.Ordinal)) continue;
            sourceIndex = index;
            break;
        }

        if (sourceIndex < 0 || _layerDropTrack >= TrackCount) return;
        var targetTrackId = _timeline.Tracks[_layerDropTrack].Id;
        if (string.Equals(_draggedLayerTrackId, targetTrackId, StringComparison.Ordinal)) return;
        LayerMoveRequested?.Invoke(this, new TimelineLayerMoveRequestedEventArgs(
            _draggedLayerTrackId,
            targetTrackId,
            _layerDropPlacement));
    }

    private TimelineLayerDropPlacement LayerDropPlacementFor(int trackIndex, int y, TimelineLayout layout)
    {
        var sourceTrack = string.IsNullOrWhiteSpace(_draggedLayerTrackId) ? -1 : TrackIndexForId(_draggedLayerTrackId);
        var sourceKind = GetTrackLayerKind(sourceTrack);
        var targetKind = GetTrackLayerKind(trackIndex);
        var rowTop = layout.RowTop + (VisibleTrackPosition(trackIndex) - _firstVisibleTrack) * RowHeight;
        var localY = y - rowTop;
        var withinCenter = localY >= RowHeight / 4 && localY < RowHeight * 3 / 4;
        if (withinCenter
            && ((sourceKind == DrawingLayerKind.Mask && targetKind == DrawingLayerKind.Drawing)
                || (sourceKind == DrawingLayerKind.Drawing && targetKind == DrawingLayerKind.Mask)))
        {
            return TimelineLayerDropPlacement.Mask;
        }

        if (GetTrackLayerKind(trackIndex) != DrawingLayerKind.Folder) return localY < RowHeight / 2
            ? TimelineLayerDropPlacement.Before
            : TimelineLayerDropPlacement.After;

        if (localY >= RowHeight / 4 && localY < RowHeight * 3 / 4) return TimelineLayerDropPlacement.Inside;
        return localY < RowHeight / 2 ? TimelineLayerDropPlacement.Before : TimelineLayerDropPlacement.After;
    }

    private void BeginFrameSelection(int trackIndex, int frame, Keys modifiers)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return;
        var cell = new TimelineFrameCell(_timeline.Tracks[trackIndex].Id, frame);
        CurrentFrame = frame;
        if ((modifiers & Keys.Shift) == Keys.Shift && _selectionAnchor is { } anchor)
        {
            SelectFrameRange(anchor, cell);
            return;
        }

        if ((modifiers & Keys.Control) == Keys.Control)
        {
            if (!_selectedFrameCells.Add(cell)) _selectedFrameCells.Remove(cell);
            _selectionAnchor = cell;
            NotifyFrameSelectionChanged();
            return;
        }

        SetFrameSelection([cell], cell);
    }

    private void UpdateFrameSelection(int trackIndex, int frame)
    {
        if (_selectionAnchor is not { } anchor || trackIndex < 0 || trackIndex >= TrackCount) return;
        SelectFrameRange(anchor, new TimelineFrameCell(_timeline.Tracks[trackIndex].Id, frame));
    }

    private void SelectFrameRange(TimelineFrameCell anchor, TimelineFrameCell end)
    {
        var anchorTrack = TrackIndexForId(anchor.TrackId);
        var endTrack = TrackIndexForId(end.TrackId);
        if (anchorTrack < 0 || endTrack < 0) return;

        var visibleTracks = VisibleTrackIndices();
        var anchorPosition = visibleTracks.IndexOf(anchorTrack);
        var endPosition = visibleTracks.IndexOf(endTrack);
        if (anchorPosition < 0 || endPosition < 0) return;
        var firstTrack = Math.Min(anchorPosition, endPosition);
        var lastTrack = Math.Max(anchorPosition, endPosition);
        var firstFrame = Math.Min(anchor.Frame, end.Frame);
        var lastFrame = Math.Max(anchor.Frame, end.Frame);
        var cells = new List<TimelineFrameCell>((lastTrack - firstTrack + 1) * (lastFrame - firstFrame + 1));
        for (var trackPosition = firstTrack; trackPosition <= lastTrack; trackPosition++)
        {
            var trackIndex = visibleTracks[trackPosition];
            for (var frame = firstFrame; frame <= lastFrame; frame++)
            {
                cells.Add(new TimelineFrameCell(_timeline.Tracks[trackIndex].Id, frame));
            }
        }

        SetFrameSelection(cells, anchor);
        CurrentFrame = end.Frame;
    }

    private void SetFrameSelection(IEnumerable<TimelineFrameCell> cells, TimelineFrameCell? anchor)
    {
        _selectedFrameCells.Clear();
        foreach (var cell in cells)
        {
            if (TrackIndexForId(cell.TrackId) < 0 || cell.Frame < StartFrame || cell.Frame > EndFrame) continue;
            _selectedFrameCells.Add(cell);
        }

        _selectionAnchor = anchor;
        NotifyFrameSelectionChanged();
    }

    private IReadOnlyList<TimelineFrameCell> CommandCells()
    {
        var selected = SelectedCells();
        if (selected.Count > 0) return selected;
        var activeTrack = GetActiveTrackIndex();
        return activeTrack < 0
            ? []
            : [new TimelineFrameCell(_timeline.Tracks[activeTrack].Id, CurrentFrame)];
    }

    private IReadOnlyList<TimelineFrameCell> SelectedCells()
    {
        return _selectedFrameCells
            .Where(cell => VisibleTrackPosition(TrackIndexForId(cell.TrackId)) >= 0 && cell.Frame >= StartFrame && cell.Frame <= EndFrame)
            .OrderBy(cell => TrackIndexForId(cell.TrackId))
            .ThenBy(cell => cell.Frame)
            .ToArray();
    }

    private int TrackIndexForId(string trackId)
    {
        for (var index = 0; index < TrackCount; index++)
        {
            if (string.Equals(_timeline.Tracks[index].Id, trackId, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    private void NotifyFrameSelectionChanged()
    {
        Invalidate();
        FrameSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<int> SelectedLayerTrackIndices()
    {
        return VisibleTrackIndices()
            .Where(trackIndex => IsTrackLayer(trackIndex) && IsTrackSelected(trackIndex))
            .ToArray();
    }

    private bool IsTrackSelected(int trackIndex)
    {
        return trackIndex >= 0
            && trackIndex < TrackCount
            && _selectedLayerTrackIds.Contains(_timeline.Tracks[trackIndex].Id);
    }

    private void SelectLayerTrack(int trackIndex, Keys modifiers)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return;
        if (!IsTrackLayer(trackIndex))
        {
            SetActiveTrack(trackIndex);
            return;
        }

        var trackId = _timeline.Tracks[trackIndex].Id;
        var visibleTracks = VisibleTrackIndices();
        var control = (modifiers & Keys.Control) == Keys.Control;
        var shift = (modifiers & Keys.Shift) == Keys.Shift;
        if (shift
            && !string.IsNullOrWhiteSpace(_layerSelectionAnchorTrackId)
            && TrackIndexForId(_layerSelectionAnchorTrackId) is var anchorTrack
            && anchorTrack >= 0
            && visibleTracks.IndexOf(anchorTrack) is var anchorPosition
            && anchorPosition >= 0
            && visibleTracks.IndexOf(trackIndex) is var trackPosition
            && trackPosition >= 0)
        {
            _selectedLayerTrackIds.Clear();
            var first = Math.Min(anchorPosition, trackPosition);
            var last = Math.Max(anchorPosition, trackPosition);
            for (var position = first; position <= last; position++)
            {
                var candidate = visibleTracks[position];
                if (IsTrackLayer(candidate)) _selectedLayerTrackIds.Add(_timeline.Tracks[candidate].Id);
            }
        }
        else if (control)
        {
            if (!_selectedLayerTrackIds.Remove(trackId))
            {
                _selectedLayerTrackIds.Add(trackId);
                _layerSelectionAnchorTrackId = trackId;
            }
            else if (string.Equals(_layerSelectionAnchorTrackId, trackId, StringComparison.Ordinal))
            {
                _layerSelectionAnchorTrackId = null;
            }
        }
        else
        {
            _selectedLayerTrackIds.Clear();
            _selectedLayerTrackIds.Add(trackId);
            _layerSelectionAnchorTrackId = trackId;
        }

        SetActiveTrack(trackIndex);
        Invalidate();
    }

    private void PrepareLayerTrackAction(int trackIndex)
    {
        if (!IsTrackSelected(trackIndex)) SelectLayerTrack(trackIndex, Keys.None);
        else SetActiveTrack(trackIndex);
    }

    private void SelectAllVisibleLayerTracks()
    {
        _selectedLayerTrackIds.Clear();
        foreach (var trackIndex in VisibleTrackIndices())
        {
            if (IsTrackLayer(trackIndex)) _selectedLayerTrackIds.Add(_timeline.Tracks[trackIndex].Id);
        }

        var active = GetActiveTrackIndex();
        _layerSelectionAnchorTrackId = IsTrackSelected(active)
            ? _timeline.Tracks[active].Id
            : _selectedLayerTrackIds.FirstOrDefault();
        Invalidate();
    }

    private void PruneLayerSelection()
    {
        _selectedLayerTrackIds.RemoveWhere(trackId =>
        {
            var trackIndex = TrackIndexForId(trackId);
            return trackIndex < 0 || !IsTrackLayer(trackIndex) || VisibleTrackPosition(trackIndex) < 0;
        });
        if (!string.IsNullOrWhiteSpace(_layerSelectionAnchorTrackId)
            && !_selectedLayerTrackIds.Contains(_layerSelectionAnchorTrackId))
        {
            _layerSelectionAnchorTrackId = null;
        }
    }

    private void SetActiveTrack(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return;
        ExpandAncestorsForTrack(trackIndex);
        var previousTrackId = ActiveTrackId;
        var track = _timeline.Tracks[trackIndex];
        _activeTrackId = track.Id;

        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            var layerIndex = FindLayerIndex(drawingScene, track.TargetId);
            if (layerIndex >= 0) drawingScene.ActiveLayer = layerIndex;
        }
        else if (_sceneDefinition is not null)
        {
            _sceneDefinition.SetActiveLayer(track.TargetId);
        }

        EnsureActiveTrackVisible();
        RefreshOnionSkinControls();
        Invalidate();
        if (!string.Equals(previousTrackId, track.Id, StringComparison.Ordinal))
        {
            ActiveLayerChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int GetActiveTrackIndex()
    {
        if (TrackCount == 0) return -1;

        var rememberedIndex = -1;
        if (!string.IsNullOrWhiteSpace(_activeTrackId))
        {
            for (var i = 0; i < TrackCount; i++)
            {
                if (!string.Equals(_timeline.Tracks[i].Id, _activeTrackId, StringComparison.Ordinal)) continue;
                rememberedIndex = i;
                break;
            }
        }

        if (rememberedIndex >= 0
            && _drawingObjectDefinition?.Instances.Any(instance => string.Equals(
                instance.Id,
                _timeline.Tracks[rememberedIndex].TargetId,
                StringComparison.Ordinal)) == true)
        {
            return rememberedIndex;
        }

        var modelIndex = GetModelActiveTrackIndex();
        if (modelIndex >= 0) return modelIndex;

        return rememberedIndex >= 0 ? rememberedIndex : 0;
    }

    private int GetModelActiveTrackIndex()
    {
        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            if (drawingScene.ActiveLayer < 0 || drawingScene.ActiveLayer >= drawingScene.LayerIds.Length) return -1;

            var targetId = drawingScene.LayerIds[drawingScene.ActiveLayer];
            for (var i = 0; i < TrackCount; i++)
            {
                if (string.Equals(_timeline.Tracks[i].TargetId, targetId, StringComparison.Ordinal)) return i;
            }
        }
        else if (_sceneDefinition is not null)
        {
            var activeLayerId = _sceneDefinition.ActiveLayerId;
            for (var i = 0; i < TrackCount; i++)
            {
                if (string.Equals(_timeline.Tracks[i].TargetId, activeLayerId, StringComparison.Ordinal)) return i;
            }
        }

        return -1;
    }

    private string GetTrackName(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return "Track";
        var targetId = _timeline.Tracks[trackIndex].TargetId;

        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            var layerIndex = FindLayerIndex(drawingScene, targetId);
            if (layerIndex >= 0) return drawingScene.LayerNames[layerIndex];
        }

        if (_drawingObjectDefinition is not null)
        {
            var instance = _drawingObjectDefinition.Instances.FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal));
            if (instance is not null) return instance.Name;
        }

        if (_sceneDefinition is not null)
        {
            var layer = _sceneDefinition.FindLayer(targetId);
            if (layer is not null) return layer.Name;
        }

        return $"Track {trackIndex + 1}";
    }

    private int GetTrackLayerIndex(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return -1;
        var drawingScene = DrawingScene();
        return drawingScene is null ? -1 : FindLayerIndex(drawingScene, _timeline.Tracks[trackIndex].TargetId);
    }

    private DrawingLayerKind GetTrackLayerKind(int trackIndex)
    {
        var drawingScene = DrawingScene();
        var layer = GetTrackLayerIndex(trackIndex);
        return drawingScene is not null && layer >= 0
            ? drawingScene.GetLayerKind(layer)
            : DrawingLayerKind.Drawing;
    }

    private int GetTrackDisplayDepth(int trackIndex)
    {
        var drawingScene = DrawingScene();
        var layer = GetTrackLayerIndex(trackIndex);
        if (drawingScene is null || layer < 0) return 0;

        var depth = drawingScene.GetLayerDepth(layer);
        return drawingScene.TryGetMaskLayerIndex(layer, out var maskLayer)
            ? Math.Max(depth, drawingScene.GetLayerDepth(maskLayer) + 1)
            : depth;
    }

    private int GetTrackLabelLeft(int trackIndex)
    {
        return LayerControlsWidth + 23 + GetTrackDisplayDepth(trackIndex) * 12;
    }

    private List<int> VisibleTrackIndices()
    {
        var result = new List<int>(TrackCount);
        var drawingScene = DrawingScene();
        if (drawingScene is null)
        {
            for (var trackIndex = 0; trackIndex < TrackCount; trackIndex++) result.Add(trackIndex);
            return result;
        }

        var trackIndexByLayerId = new Dictionary<string, int>(TrackCount, StringComparer.Ordinal);
        for (var trackIndex = 0; trackIndex < TrackCount; trackIndex++)
        {
            var targetId = _timeline.Tracks[trackIndex].TargetId;
            if (!trackIndexByLayerId.ContainsKey(targetId)) trackIndexByLayerId.Add(targetId, trackIndex);
        }

        foreach (var layer in drawingScene.GetLayerDisplayOrder())
        {
            if (!trackIndexByLayerId.TryGetValue(drawingScene.LayerIds[layer], out var trackIndex)
                || IsLayerHiddenByCollapsedGroup(drawingScene, layer))
            {
                continue;
            }

            result.Add(trackIndex);
        }

        var displayedTracks = result.ToHashSet();
        for (var trackIndex = 0; trackIndex < TrackCount; trackIndex++)
        {
            if (displayedTracks.Contains(trackIndex)) continue;
            var layer = GetTrackLayerIndex(trackIndex);
            if (layer >= 0 && IsLayerHiddenByCollapsedGroup(drawingScene, layer)) continue;
            result.Add(trackIndex);
        }
        return result;
    }

    private int VisibleTrackCount() => VisibleTrackIndices().Count;

    private int VisibleTrackPosition(int trackIndex)
    {
        return trackIndex < 0 ? -1 : VisibleTrackIndices().IndexOf(trackIndex);
    }

    private bool IsLayerHiddenByCollapsedGroup(VectorScene drawingScene, int layer)
    {
        var parent = drawingScene.GetLayerParentIndex(layer);
        var visited = new HashSet<int>();
        while (parent >= 0 && visited.Add(parent))
        {
            if (_collapsedFolderLayerIds.Contains(drawingScene.LayerIds[parent])) return true;
            parent = drawingScene.GetLayerParentIndex(parent);
        }

        return drawingScene.TryGetMaskLayerIndex(layer, out var maskLayer)
            && _collapsedMaskLayerIds.Contains(drawingScene.LayerIds[maskLayer]);
    }

    private bool IsTrackFolder(int trackIndex) => GetTrackLayerKind(trackIndex) == DrawingLayerKind.Folder;

    private bool IsTrackMaskGroup(int trackIndex)
    {
        var drawingScene = DrawingScene();
        var layer = GetTrackLayerIndex(trackIndex);
        return drawingScene is not null
            && layer >= 0
            && drawingScene.GetLayerKind(layer) == DrawingLayerKind.Mask
            && drawingScene.GetMaskContentLayerIndex(layer) >= 0;
    }

    private bool IsTrackCollapsible(int trackIndex) => IsTrackFolder(trackIndex) || IsTrackMaskGroup(trackIndex);

    private bool IsTrackCollapsed(int trackIndex)
    {
        var drawingScene = DrawingScene();
        var layer = GetTrackLayerIndex(trackIndex);
        if (drawingScene is null || layer < 0) return false;

        return drawingScene.GetLayerKind(layer) switch
        {
            DrawingLayerKind.Folder => _collapsedFolderLayerIds.Contains(drawingScene.LayerIds[layer]),
            DrawingLayerKind.Mask => _collapsedMaskLayerIds.Contains(drawingScene.LayerIds[layer]),
            _ => false
        };
    }

    private bool ExpandAncestorsForTrack(int trackIndex)
    {
        var drawingScene = DrawingScene();
        var layer = GetTrackLayerIndex(trackIndex);
        if (drawingScene is null || layer < 0) return false;
        var changed = false;
        var parent = drawingScene.GetLayerParentIndex(layer);
        var visited = new HashSet<int>();
        while (parent >= 0 && visited.Add(parent))
        {
            changed |= _collapsedFolderLayerIds.Remove(drawingScene.LayerIds[parent]);
            parent = drawingScene.GetLayerParentIndex(parent);
        }

        if (drawingScene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            changed |= _collapsedMaskLayerIds.Remove(drawingScene.LayerIds[maskLayer]);
        }

        return changed;
    }

    private void ToggleActiveTrackGroupCollapsed()
    {
        var trackIndex = GetActiveTrackIndex();
        if (trackIndex >= 0) ToggleTrackGroupCollapsed(trackIndex);
    }

    private bool ToggleTrackGroupCollapsedFromKeyboard(bool collapse)
    {
        var trackIndex = GetActiveTrackIndex();
        if (!IsTrackCollapsible(trackIndex)) return false;
        if (collapse == IsTrackCollapsed(trackIndex)) return false;
        ToggleTrackGroupCollapsed(trackIndex);
        return true;
    }

    private void ToggleTrackGroupCollapsed(int trackIndex)
    {
        var drawingScene = DrawingScene();
        var layer = GetTrackLayerIndex(trackIndex);
        if (drawingScene is null || layer < 0 || !IsTrackCollapsible(trackIndex)) return;

        var collapsedLayerIds = drawingScene.GetLayerKind(layer) == DrawingLayerKind.Folder
            ? _collapsedFolderLayerIds
            : _collapsedMaskLayerIds;
        var layerId = drawingScene.LayerIds[layer];
        if (!collapsedLayerIds.Remove(layerId))
        {
            collapsedLayerIds.Add(layerId);
            var activeTrack = GetActiveTrackIndex();
            var activeLayer = GetTrackLayerIndex(activeTrack);
            if (activeLayer >= 0 && IsLayerHiddenByCollapsedGroup(drawingScene, activeLayer)) SetActiveTrack(trackIndex);
        }

        RemoveCollapsedFrameSelection();
        PruneLayerSelection();
        SetFirstVisibleTrack(_firstVisibleTrack, invalidate: false);
        EnsureActiveTrackVisible();
        Invalidate();
    }

    private void PruneCollapsedLayerGroupIds()
    {
        var drawingScene = DrawingScene();
        if (drawingScene is null)
        {
            _collapsedFolderLayerIds.Clear();
            _collapsedMaskLayerIds.Clear();
            return;
        }

        _collapsedFolderLayerIds.RemoveWhere(id =>
        {
            var layer = Array.IndexOf(drawingScene.LayerIds, id);
            return layer < 0 || drawingScene.GetLayerKind(layer) != DrawingLayerKind.Folder;
        });
        _collapsedMaskLayerIds.RemoveWhere(id =>
        {
            var layer = Array.IndexOf(drawingScene.LayerIds, id);
            return layer < 0
                || drawingScene.GetLayerKind(layer) != DrawingLayerKind.Mask
                || drawingScene.GetMaskContentLayerIndex(layer) < 0;
        });
        RemoveCollapsedFrameSelection();
        PruneLayerSelection();
    }

    private void RemoveCollapsedFrameSelection()
    {
        var changed = _selectedFrameCells.RemoveWhere(cell => VisibleTrackPosition(TrackIndexForId(cell.TrackId)) < 0) > 0;
        if (_selectionAnchor is { } anchor && !_selectedFrameCells.Contains(anchor)) _selectionAnchor = null;
        if (changed) FrameSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private Rectangle LayerGroupDisclosureBounds(int trackIndex, int rowTop)
    {
        var labelLeft = GetTrackLabelLeft(trackIndex);
        return new Rectangle(labelLeft - 13, rowTop + (RowHeight - 14) / 2, 12, 14);
    }

    private static void DrawLayerGroupDisclosure(Graphics graphics, Rectangle bounds, bool collapsed)
    {
        using var background = new SolidBrush(Theme.PanelStrong);
        using var outline = new Pen(Theme.Border);
        graphics.FillRectangle(background, bounds);
        graphics.DrawRectangle(outline, bounds);
        var points = collapsed
            ? new[]
            {
                new Point(bounds.Left + 4, bounds.Top + 3),
                new Point(bounds.Left + 4, bounds.Bottom - 3),
                new Point(bounds.Right - 3, bounds.Top + bounds.Height / 2)
            }
            : new[]
            {
                new Point(bounds.Left + 3, bounds.Top + 4),
                new Point(bounds.Right - 3, bounds.Top + 4),
                new Point(bounds.Left + bounds.Width / 2, bounds.Bottom - 3)
            };
        using var fill = new SolidBrush(Theme.Accent);
        graphics.FillPolygon(fill, points);
    }

    private bool IsTrackLocked(int trackIndex)
    {
        var drawingScene = DrawingScene();
        var layer = GetTrackLayerIndex(trackIndex);
        return drawingScene is not null
            && layer >= 0
            && drawingScene.IsLayerEffectivelyLocked(layer);
    }

    private static void DrawLayerHierarchyGuide(Graphics graphics, int depth, int y, int labelLeft)
    {
        if (depth <= 0) return;
        using var guide = new Pen(Color.FromArgb(92, 104, 121, 126), 1f);
        var x = labelLeft - 6;
        graphics.DrawLine(guide, x, y, x, y + RowHeight / 2);
        graphics.DrawLine(guide, x, y + RowHeight / 2, x + 5, y + RowHeight / 2);
    }

    private static void DrawLockIcon(Graphics graphics, bool locked, int centerX, int centerY, Pen lockedPen, Pen unlockedPen)
    {
        var pen = locked ? lockedPen : unlockedPen;
        var body = new Rectangle(centerX - 5, centerY - 1, 10, 7);
        var shackle = new Rectangle(centerX - 4, centerY - 6, 8, 8);
        graphics.DrawArc(pen, shackle, locked ? 180 : 210, locked ? 180 : 140);
        graphics.DrawRectangle(pen, body);
        if (locked)
        {
            using var fill = new SolidBrush(Color.FromArgb(202, 224, 224, 224));
            graphics.FillEllipse(fill, centerX - 1, centerY + 1, 2, 2);
        }
    }

    private static void DrawLayerKindGlyph(Graphics graphics, DrawingLayerKind kind, int x, int centerY)
    {
        var bounds = new Rectangle(x, centerY - 5, 11, 9);
        switch (kind)
        {
            case DrawingLayerKind.Folder:
                using (var fill = new SolidBrush(Color.FromArgb(192, 224, 172, 72)))
                using (var outline = new Pen(Color.FromArgb(228, 248, 202, 112)))
                {
                    graphics.FillRectangle(fill, bounds.X, bounds.Y + 2, bounds.Width, bounds.Height - 2);
                    graphics.FillRectangle(fill, bounds.X + 1, bounds.Y, 5, 3);
                    graphics.DrawRectangle(outline, bounds.X, bounds.Y + 2, bounds.Width - 1, bounds.Height - 3);
                }
                break;
            case DrawingLayerKind.Mask:
                using (var outline = new Pen(Color.FromArgb(230, 130, 205, 208), 1.2f))
                using (var center = new SolidBrush(Color.FromArgb(160, 130, 205, 208)))
                {
                    graphics.DrawEllipse(outline, bounds);
                    graphics.FillEllipse(center, x + 4, centerY - 1, 3, 3);
                }
                break;
            default:
                using (var fill = new SolidBrush(Color.FromArgb(192, Theme.Text)))
                {
                    graphics.FillRectangle(fill, x + 1, centerY - 4, 9, 8);
                }
                break;
        }
    }

    private bool IsTrackVisible(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        var targetId = _timeline.Tracks[trackIndex].TargetId;

        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            var layerIndex = FindLayerIndex(drawingScene, targetId);
            if (layerIndex >= 0) return drawingScene.IsLayerEffectivelyVisible(layerIndex);
        }

        if (_drawingObjectDefinition is not null)
        {
            return _drawingObjectDefinition.Instances
                .FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal))?.Visible == true;
        }

        if (_sceneDefinition is not null)
        {
            return _sceneDefinition.IsLayerVisible(targetId);
        }

        return true;
    }

    private Color GetTrackColor(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return Theme.Muted;
        var targetId = _timeline.Tracks[trackIndex].TargetId;
        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            var layerIndex = FindLayerIndex(drawingScene, targetId);
            if (layerIndex >= 0) return drawingScene.GetLayerColor(layerIndex);
        }

        if (_sceneDefinition?.FindLayer(targetId) is { } sceneLayer) return Color.FromArgb(sceneLayer.ColorArgb);
        return Theme.Muted;
    }

    private bool IsTrackOnionSkin(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        var drawingScene = DrawingScene();
        if (drawingScene is null) return false;
        var layerIndex = FindLayerIndex(drawingScene, _timeline.Tracks[trackIndex].TargetId);
        return layerIndex >= 0 && layerIndex < drawingScene.LayerOnionSkin.Length && drawingScene.LayerOnionSkin[layerIndex];
    }

    private bool IsTrackLayer(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        var targetId = _timeline.Tracks[trackIndex].TargetId;
        var drawingScene = DrawingScene();
        if (drawingScene is not null) return FindLayerIndex(drawingScene, targetId) >= 0;
        return _sceneDefinition?.FindLayer(targetId) is not null;
    }

    private bool IsTrackExplicitlyVisible(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        var targetId = _timeline.Tracks[trackIndex].TargetId;

        var drawingScene = DrawingScene();
        var layerIndex = drawingScene is null ? -1 : FindLayerIndex(drawingScene, targetId);
        if (drawingScene is not null && layerIndex >= 0)
        {
            return drawingScene.LayerVisible[layerIndex];
        }

        if (_drawingObjectDefinition is not null)
        {
            return _drawingObjectDefinition.Instances
                .FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal))?.Visible == true;
        }

        return _sceneDefinition?.FindLayer(targetId)?.Visible == true;
    }

    private void SetSelectedTrackVisibility(bool visible)
    {
        var tracks = SelectedLayerTrackIndices();
        var changed = false;
        foreach (var trackIndex in tracks)
        {
            changed |= SetTrackVisibility(trackIndex, visible);
        }

        if (!changed) return;
        Invalidate();
        LayerVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool SetTrackVisibility(int trackIndex, bool visible)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        var targetId = _timeline.Tracks[trackIndex].TargetId;
        var drawingScene = DrawingScene();
        var layerIndex = drawingScene is null ? -1 : FindLayerIndex(drawingScene, targetId);
        if (drawingScene is not null && layerIndex >= 0) return drawingScene.SetLayerVisible(layerIndex, visible);

        if (_drawingObjectDefinition is not null)
        {
            var instance = _drawingObjectDefinition.Instances.FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal));
            if (instance is null || instance.Visible == visible) return false;
            instance.Visible = visible;
            return true;
        }

        return _sceneDefinition?.SetLayerVisible(targetId, visible) == true;
    }

    private void SoloActiveTrack()
    {
        var trackIndex = GetActiveTrackIndex();
        if (trackIndex < 0 || trackIndex >= TrackCount) return;
        var targetId = _timeline.Tracks[trackIndex].TargetId;

        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            drawingScene.SoloLayer(FindLayerIndex(drawingScene, targetId));
            if (_drawingObjectDefinition is not null)
            {
                foreach (var instance in _drawingObjectDefinition.Instances)
                {
                    instance.Visible = string.Equals(instance.Id, targetId, StringComparison.Ordinal);
                }
            }
        }
        else if (_sceneDefinition is not null)
        {
            _sceneDefinition.SoloLayer(targetId);
        }

        Invalidate();
        LayerVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowAllTracks()
    {
        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            drawingScene.ShowAllLayers();
            if (_drawingObjectDefinition is not null)
            {
                foreach (var instance in _drawingObjectDefinition.Instances) instance.Visible = true;
            }
        }
        else if (_sceneDefinition is not null)
        {
            _sceneDefinition.ShowAllLayers();
        }

        Invalidate();
        LayerVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private VectorScene? DrawingScene() => _vectorScene ?? _drawingObjectDefinition?.Scene;

    private static int FindLayerIndex(VectorScene scene, string targetId)
    {
        for (var i = 0; i < scene.LayerIds.Length; i++)
        {
            if (string.Equals(scene.LayerIds[i], targetId, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    private void HandleTimelineChanged(object? sender, EventArgs e)
    {
        UpdateFrameBoundsForTimelineChange();
        RefreshOnionSkinControls();
        EnsureActiveTrackVisible();
        EnsureCurrentFrameVisible();
        Invalidate();
    }

    private void UpdateFrameBoundsForTimelineChange()
    {
        var oldLastFrame = Math.Max(0, _knownFrameCount - 1);
        var followedTimelineEnd = _knownFrameCount <= 0 || _endFrame >= oldLastFrame;
        _knownFrameCount = FrameCount;
        var newLastFrame = Math.Max(0, _knownFrameCount - 1);
        _startFrame = Math.Clamp(_startFrame, 0, newLastFrame);
        _endFrame = followedTimelineEnd
            ? newLastFrame
            : Math.Clamp(_endFrame, _startFrame, newLastFrame);
        _currentFrame = Math.Clamp(_currentFrame, _startFrame, _endFrame);

        if (!string.IsNullOrWhiteSpace(_activeTrackId) && _timeline.FindTrack(_activeTrackId) is null)
        {
            _activeTrackId = TrackCount > 0 ? _timeline.Tracks[0].Id : null;
        }

        _selectedFrameCells.RemoveWhere(cell => TrackIndexForId(cell.TrackId) < 0 || cell.Frame < _startFrame || cell.Frame > _endFrame);
        if (_selectionAnchor is { } anchor && !_selectedFrameCells.Contains(anchor)) _selectionAnchor = null;
    }

    private void EnsureActiveTrackVisible()
    {
        var layout = CreateLayout();
        var capacity = VisibleTrackCapacity(layout);
        var active = GetActiveTrackIndex();
        ExpandAncestorsForTrack(active);
        var activePosition = VisibleTrackPosition(active);
        if (activePosition < 0)
        {
            SetFirstVisibleTrack(0, invalidate: false);
        }
        else if (activePosition < _firstVisibleTrack)
        {
            SetFirstVisibleTrack(activePosition, invalidate: false);
        }
        else if (activePosition >= _firstVisibleTrack + capacity)
        {
            SetFirstVisibleTrack(activePosition - capacity + 1, invalidate: false);
        }
        else
        {
            SetFirstVisibleTrack(_firstVisibleTrack, invalidate: false);
        }
    }

    private void SetFirstVisibleFrame(int frame, bool invalidate = true)
    {
        var layout = CreateLayout();
        var maxFirst = Math.Max(StartFrame, EndFrame - VisibleFrameCapacity(layout) + 1);
        var next = Math.Clamp(frame, StartFrame, maxFirst);
        if (_firstVisibleFrame == next) return;
        _firstVisibleFrame = next;
        if (invalidate) Invalidate();
    }

    private void SetFirstVisibleTrack(int track, bool invalidate = true)
    {
        var layout = CreateLayout();
        var maxFirst = Math.Max(0, VisibleTrackCount() - VisibleTrackCapacity(layout));
        var next = Math.Clamp(track, 0, maxFirst);
        if (_firstVisibleTrack == next) return;
        _firstVisibleTrack = next;
        if (invalidate) Invalidate();
    }

    private bool TryGetTrackIndex(Point point, TimelineLayout layout, out int trackIndex)
    {
        trackIndex = -1;
        if (point.Y < layout.RowTop || point.Y >= layout.RowBottom) return false;
        var visibleRow = (point.Y - layout.RowTop) / RowHeight;
        var visibleTracks = VisibleTrackIndices();
        var visiblePosition = _firstVisibleTrack + visibleRow;
        if (visiblePosition < 0 || visiblePosition >= visibleTracks.Count) return false;
        trackIndex = visibleTracks[visiblePosition];
        return true;
    }

    private int FrameFromX(int x, TimelineLayout layout)
    {
        var column = (Math.Clamp(x, layout.TrackLeft, Math.Max(layout.TrackLeft, layout.TrackRight - 1)) - layout.TrackLeft) / FrameCellWidth;
        return Math.Clamp(_firstVisibleFrame + column, StartFrame, EndFrame);
    }

    private int VisibleFrameCapacity(TimelineLayout layout)
    {
        return Math.Max(1, (layout.TrackRight - layout.TrackLeft) / FrameCellWidth);
    }

    private int VisibleFrameDrawCount(TimelineLayout layout)
    {
        return Math.Max(1, (int)Math.Ceiling((layout.TrackRight - layout.TrackLeft) / (double)FrameCellWidth));
    }

    private int VisibleTrackCapacity(TimelineLayout layout)
    {
        return Math.Max(1, (layout.RowBottom - layout.RowTop) / RowHeight);
    }

    private TimelineLayout CreateLayout()
    {
        var trackLeft = Math.Min(PreferredGutterWidth, Math.Max(MinimumGutterWidth, Width - FrameCellWidth * 5));
        var trackRight = Math.Max(trackLeft + 1, Width - 1);
        var rowTop = HeaderHeight + RulerHeight;
        var scrollTop = Math.Max(rowTop, Height - HorizontalScrollHeight);
        var rowBottom = Math.Max(rowTop, scrollTop);
        return new TimelineLayout(
            trackLeft,
            trackRight,
            rowTop,
            rowBottom,
            scrollTop,
            new Rectangle(0, 0, Width, HeaderHeight),
            new Rectangle(0, HeaderHeight, trackLeft, Math.Max(0, scrollTop - HeaderHeight)),
            new Rectangle(trackLeft, HeaderHeight, Math.Max(0, trackRight - trackLeft), RulerHeight),
            new Rectangle(trackLeft, rowTop, Math.Max(0, trackRight - trackLeft), Math.Max(0, rowBottom - rowTop)));
    }

    private ScrollGeometry GetHorizontalScrollGeometry(TimelineLayout layout)
    {
        var bounds = new Rectangle(
            layout.TrackLeft,
            layout.ScrollTop,
            Math.Max(1, layout.TrackRight - layout.TrackLeft),
            Math.Max(1, Height - layout.ScrollTop));
        var decrease = new Rectangle(bounds.Left, bounds.Top, Math.Min(HorizontalScrollArrowWidth, bounds.Width), bounds.Height);
        var increase = new Rectangle(Math.Max(bounds.Left, bounds.Right - HorizontalScrollArrowWidth), bounds.Top, Math.Min(HorizontalScrollArrowWidth, bounds.Width), bounds.Height);
        var trackLeft = Math.Min(bounds.Right, decrease.Right + 2);
        var trackRight = Math.Max(trackLeft, increase.Left - 2);
        var track = Rectangle.FromLTRB(trackLeft, bounds.Top + 3, trackRight, Math.Max(bounds.Top + 4, bounds.Bottom - 3));
        var capacity = VisibleFrameCapacity(layout);
        var total = Math.Max(1, EndFrame - StartFrame + 1);
        var maxValue = Math.Max(0, total - capacity);
        var thumbWidth = maxValue == 0
            ? track.Width
            : Math.Clamp((int)Math.Round(track.Width * Math.Min(1d, capacity / (double)total)), 24, Math.Max(24, track.Width));
        thumbWidth = Math.Min(track.Width, thumbWidth);
        var travel = Math.Max(0, track.Width - thumbWidth);
        var position = maxValue == 0 ? 0d : (_firstVisibleFrame - StartFrame) / (double)maxValue;
        var thumbLeft = track.Left + (int)Math.Round(Math.Clamp(position, 0d, 1d) * travel);
        var thumb = new Rectangle(thumbLeft, track.Top, thumbWidth, track.Height);
        return new ScrollGeometry(bounds, decrease, increase, track, thumb, maxValue);
    }

    private VerticalScrollGeometry GetVerticalScrollGeometry(TimelineLayout layout)
    {
        var bounds = new Rectangle(
            Math.Max(0, layout.TrackLeft - VerticalScrollWidth),
            layout.RowTop,
            VerticalScrollWidth,
            Math.Max(0, layout.RowBottom - layout.RowTop));
        var capacity = VisibleTrackCapacity(layout);
        var visibleTrackCount = VisibleTrackCount();
        var maxValue = Math.Max(0, visibleTrackCount - capacity);
        if (bounds.Height <= 0 || maxValue == 0) return new VerticalScrollGeometry(bounds, Rectangle.Empty, maxValue);

        var thumbHeight = Math.Clamp(
            (int)Math.Round(bounds.Height * Math.Min(1d, capacity / (double)Math.Max(1, visibleTrackCount))),
            18,
            Math.Max(18, bounds.Height));
        thumbHeight = Math.Min(bounds.Height, thumbHeight);
        var travel = Math.Max(0, bounds.Height - thumbHeight);
        var ratio = _firstVisibleTrack / (double)maxValue;
        var thumbTop = bounds.Top + (int)Math.Round(Math.Clamp(ratio, 0d, 1d) * travel);
        return new VerticalScrollGeometry(bounds, new Rectangle(bounds.Left, thumbTop, bounds.Width, thumbHeight), maxValue);
    }

    private static Rectangle SoloButtonBounds(TimelineLayout layout)
    {
        var all = AllButtonBounds(layout);
        return new Rectangle(Math.Max(4, all.Left - SoloButtonWidth - 5), 5, SoloButtonWidth, 22);
    }

    private static Rectangle AddLayerButtonBounds(TimelineLayout layout)
    {
        var solo = SoloButtonBounds(layout);
        return new Rectangle(Math.Max(4, solo.Left - AddLayerButtonWidth - 5), 5, AddLayerButtonWidth, 22);
    }

    private static Rectangle AllButtonBounds(TimelineLayout layout)
    {
        return new Rectangle(Math.Max(4, layout.TrackLeft - AllButtonWidth - 8), 5, AllButtonWidth, 22);
    }

    private bool IsOnionSkinControlsAvailable()
    {
        var drawingScene = DrawingScene();
        var activeTrack = GetActiveTrackIndex();
        return drawingScene is not null
            && activeTrack >= 0
            && FindLayerIndex(drawingScene, _timeline.Tracks[activeTrack].TargetId) >= 0;
    }

    private static Label CreateHeaderLabel(string text, string accessibleName)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            Height = Theme.ControlHeightCompact,
            BackColor = Theme.Panel,
            ForeColor = Theme.Muted,
            Font = Theme.UiFont(8.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = accessibleName
        };
    }

    private static Label CreateOnionSkinRangeLabel(string text)
    {
        return CreateHeaderLabel(text, $"{text} onion skin range");
    }

    private static ModernNumericUpDown CreateOnionSkinRangeInput(string accessibleName)
    {
        var input = new ModernNumericUpDown
        {
            Minimum = 0,
            Maximum = VectorScene.MaximumOnionSkinFrames,
            DecimalPlaces = 0,
            Increment = 1,
            Height = Theme.ControlHeightCompact,
            AccessibleName = accessibleName
        };
        Theme.StyleNumeric(input);
        return input;
    }

    private static ModernNumericUpDown CreatePlaybackFpsInput()
    {
        var input = new ModernNumericUpDown
        {
            Minimum = 1,
            Maximum = 120,
            DecimalPlaces = 0,
            Increment = 1,
            Value = 30,
            Height = Theme.ControlHeightCompact,
            AccessibleName = "Animation frame rate in frames per second"
        };
        Theme.StyleNumeric(input);
        return input;
    }

    private void LayoutHeaderControls(TimelineLayout layout, bool onionSkinAvailable)
    {
        var fpsBounds = PlaybackFpsControlsBounds(layout);
        var showFps = !fpsBounds.IsEmpty;
        _playbackFpsLabel.Visible = showFps;
        _playbackFps.Visible = showFps;
        if (showFps)
        {
            var labelWidth = PlaybackFpsLabelWidth();
            _playbackFpsLabel.SetBounds(fpsBounds.Left, fpsBounds.Top, labelWidth, fpsBounds.Height);
            _playbackFps.SetBounds(fpsBounds.Left + labelWidth, fpsBounds.Top, PlaybackFpsNumericWidth(), fpsBounds.Height);
        }

        LayoutOnionSkinControls(layout, onionSkinAvailable, fpsBounds);
    }

    private Rectangle PlaybackFpsControlsBounds(TimelineLayout layout)
    {
        var controlsWidth = PlaybackFpsControlsWidth();
        if (layout.TrackRight - layout.TrackLeft < controlsWidth + ScaleTimelineMetric(112)) return Rectangle.Empty;
        return new Rectangle(
            layout.TrackRight - controlsWidth - ScaleTimelineMetric(8),
            ScaleTimelineMetric(2),
            controlsWidth,
            Math.Max(Theme.ControlHeightCompact, HeaderHeight - ScaleTimelineMetric(4)));
    }

    private Rectangle OnionSkinControlsBounds(TimelineLayout layout, Rectangle fpsBounds)
    {
        var controlsWidth = OnionSkinControlsWidth();
        var right = fpsBounds.IsEmpty ? layout.TrackRight : fpsBounds.Left - ScaleTimelineMetric(8);
        if (!IsOnionSkinControlsAvailable() || right - layout.TrackLeft < controlsWidth + ScaleTimelineMetric(112))
        {
            return Rectangle.Empty;
        }

        var left = Math.Min(
            right - controlsWidth - ScaleTimelineMetric(8),
            layout.TrackLeft + ScaleTimelineMetric(200));
        return new Rectangle(left, ScaleTimelineMetric(2), controlsWidth, Math.Max(Theme.ControlHeightCompact, HeaderHeight - ScaleTimelineMetric(4)));
    }

    private void LayoutOnionSkinControls(TimelineLayout layout, bool available, Rectangle fpsBounds)
    {
        var bounds = available ? OnionSkinControlsBounds(layout, fpsBounds) : Rectangle.Empty;
        var visible = !bounds.IsEmpty;
        _onionSkinToggle.Visible = visible;
        _onionPreviousLabel.Visible = visible;
        _onionPreviousFrames.Visible = visible;
        _onionNextLabel.Visible = visible;
        _onionNextFrames.Visible = visible;
        if (!visible) return;

        var left = bounds.Left;
        var toggleWidth = OnionSkinToggleWidth();
        var previousLabelWidth = OnionSkinLabelWidth(_onionPreviousLabel);
        var nextLabelWidth = OnionSkinLabelWidth(_onionNextLabel);
        var numericWidth = OnionSkinNumericWidth();
        var gap = ScaleTimelineMetric(6);
        _onionSkinToggle.SetBounds(left, bounds.Top, toggleWidth, bounds.Height);
        left += toggleWidth + gap;
        _onionPreviousLabel.SetBounds(left, bounds.Top, previousLabelWidth, bounds.Height);
        left += previousLabelWidth;
        _onionPreviousFrames.SetBounds(left, bounds.Top, numericWidth, bounds.Height);
        left += numericWidth + gap;
        _onionNextLabel.SetBounds(left, bounds.Top, nextLabelWidth, bounds.Height);
        left += nextLabelWidth;
        _onionNextFrames.SetBounds(left, bounds.Top, numericWidth, bounds.Height);
    }

    private void RaiseOnionSkinRangeChanged()
    {
        if (_updatingOnionSkinControls) return;
        OnionSkinRangeChanged?.Invoke(
            this,
            new TimelineOnionSkinRangeChangedEventArgs((int)_onionPreviousFrames.Value, (int)_onionNextFrames.Value));
    }

    private int OnionSkinControlsWidth()
    {
        var gap = ScaleTimelineMetric(6);
        return OnionSkinToggleWidth()
            + OnionSkinLabelWidth(_onionPreviousLabel)
            + OnionSkinNumericWidth()
            + OnionSkinLabelWidth(_onionNextLabel)
            + OnionSkinNumericWidth()
            + gap * 2;
    }

    private int PlaybackFpsControlsWidth()
    {
        return PlaybackFpsLabelWidth() + PlaybackFpsNumericWidth() + ScaleTimelineMetric(4);
    }

    private int PlaybackFpsLabelWidth()
    {
        return TextRenderer.MeasureText(
                _playbackFpsLabel.Text,
                _playbackFpsLabel.Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width
            + ScaleTimelineMetric(8);
    }

    private int PlaybackFpsNumericWidth() => ScaleTimelineMetric(48);

    private int OnionSkinToggleWidth()
    {
        return Math.Max(
            ScaleTimelineMetric(78),
            _onionSkinToggle.GetPreferredSize(Size.Empty).Width + ScaleTimelineMetric(2));
    }

    private int OnionSkinLabelWidth(Label label)
    {
        return TextRenderer.MeasureText(
                label.Text,
                label.Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width
            + ScaleTimelineMetric(8);
    }

    private int OnionSkinNumericWidth() => ScaleTimelineMetric(48);

    private int ScaleTimelineMetric(int logicalPixels)
    {
        var dpi = IsHandleCreated ? DeviceDpi : 96;
        return Math.Max(1, (int)Math.Round(logicalPixels * dpi / 96f));
    }

    private static void DrawHeaderButton(Graphics graphics, Rectangle bounds, string text)
    {
        using var fillBrush = new SolidBrush(Theme.PanelStrong);
        using var borderPen = new Pen(Theme.Border);
        using var font = Theme.UiFont(8.5f);
        graphics.FillRectangle(fillBrush, bounds);
        graphics.DrawRectangle(borderPen, bounds);
        TextRenderer.DrawText(
            graphics,
            text,
            font,
            bounds,
            Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static void DrawVisibilityIcon(Graphics graphics, bool visible, float x, float y, Pen pen)
    {
        var bounds = new RectangleF(x - 8, y - 5, 16, 10);
        graphics.DrawEllipse(pen, bounds);
        if (visible)
        {
            using var fillBrush = new SolidBrush(pen.Color);
            graphics.FillEllipse(fillBrush, x - 2.4f, y - 2.4f, 4.8f, 4.8f);
        }
        else
        {
            graphics.DrawLine(pen, x - 8, y + 6, x + 8, y - 6);
        }
    }

    private readonly record struct TimelineLayout(
        int TrackLeft,
        int TrackRight,
        int RowTop,
        int RowBottom,
        int ScrollTop,
        Rectangle HeaderBounds,
        Rectangle GutterBounds,
        Rectangle RulerBounds,
        Rectangle GridBounds);

    private readonly record struct ScrollGeometry(
        Rectangle Bounds,
        Rectangle DecreaseButton,
        Rectangle IncreaseButton,
        Rectangle Track,
        Rectangle Thumb,
        int MaxValue);

    private readonly record struct VerticalScrollGeometry(Rectangle Bounds, Rectangle Thumb, int MaxValue);
}
