using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal readonly record struct TimelineFrameCell(string TrackId, int Frame);

internal enum TimelineFrameTransformMode : byte
{
    None,
    Move,
    ScaleLeft,
    ScaleRight,
    ScaleTop,
    ScaleBottom,
    ScaleTopLeft,
    ScaleTopRight,
    ScaleBottomRight,
    ScaleBottomLeft
}

internal readonly record struct TimelineFrameSelectionBounds(
    int FirstFrame,
    int LastFrame,
    int FirstTrackPosition,
    int LastTrackPosition)
{
    public int FrameCount => Math.Max(0, LastFrame - FirstFrame + 1);
    public int TrackCount => Math.Max(0, LastTrackPosition - FirstTrackPosition + 1);
}

internal readonly record struct TimelineFrameTransformCell(
    TimelineFrameCell Source,
    TimelineFrameCell Destination);

internal sealed class TimelineFrameTransformRequestedEventArgs(
    TimelineFrameTransformMode mode,
    IReadOnlyList<TimelineFrameTransformCell> cells) : EventArgs
{
    public TimelineFrameTransformMode Mode { get; } = mode;
    public IReadOnlyList<TimelineFrameTransformCell> Cells { get; } = cells;
}

internal readonly record struct TimelineTweenSelection(string TrackId, int StartFrame, int EndFrame);

internal sealed record TimelineSelectionSnapshot(
    TimelineFrameCell[] FrameCells,
    TimelineFrameCell? FrameAnchor,
    string? ActiveTrackId,
    string[] SelectedLayerTrackIds,
    string? LayerAnchorTrackId);

internal enum TimelineCommand
{
    CopyFrames,
    PasteFrames,
    ReverseFrames,
    InsertFrames,
    DeleteFrames,
    InsertKeyframes,
    InsertBlankKeyframes,
    ClearKeyframes,
    ClassicTween,
    ShapeTween,
    RemoveTween
}

internal enum TimelineFeedbackMotion : byte
{
    Outline,
    Sweep,
    Expand,
    Contract,
    Pulse,
    Ring,
    Fade,
    Flow,
    Morph,
    Dissolve,
    Reverse
}

internal readonly record struct TimelineFeedbackStyle(
    TimelineFeedbackMotion Motion,
    Color Color,
    int DurationMilliseconds);

internal enum TimelineLayerFeedbackKind : byte
{
    Add,
    Remove
}

internal readonly record struct TimelineLayerFeedbackStyle(Color Color, int DurationMilliseconds);

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
    TimelineLayerDropPlacement placement,
    bool moveOutOfMask = false) : EventArgs
{
    public string TrackId { get; } = trackId;
    public string TargetTrackId { get; } = targetTrackId;
    public TimelineLayerDropPlacement Placement { get; } = placement;
    public bool MoveOutOfMask { get; } = moveOutOfMask;
}

internal sealed class TimelineOnionSkinRangeChangedEventArgs(int previousFrames, int nextFrames) : EventArgs
{
    public int PreviousFrames { get; } = previousFrames;
    public int NextFrames { get; } = nextFrames;
}

internal sealed class TimelineMotionTrackRangeChangedEventArgs(int firstFrame, int lastFrame) : EventArgs
{
    public int FirstFrame { get; } = firstFrame;
    public int LastFrame { get; } = lastFrame;
}

internal enum TimelineOnionSkinRangeHandle : byte
{
    None,
    Previous,
    Next
}

internal sealed partial class TimelineStrip : Control
{
    private enum HeaderCommand : byte
    {
        None,
        AddLayer,
        Solo,
        All,
        AllVisibility,
        AllLocks,
        AllOutlines
    }

    private readonly record struct TimelineLayerVisualSnapshot(
        string TrackId,
        int VisiblePosition,
        string Name,
        Color Color);

    private readonly record struct TimelineLayerFeedbackTarget(
        TimelineLayerFeedbackKind Kind,
        TimelineLayerVisualSnapshot Snapshot);

    private const int PreferredGutterWidth = 232;
    private const int MinimumGutterWidth = 148;
    private const int RulerHeight = 28;
    private const int RowHeight = 21;
    private const int LowRowHeight = 16;
    private const int HighRowHeight = 28;
    private const int FrameCellWidth = 14;
    internal const int MinimumFrameCellWidth = 8;
    internal const int MaximumFrameCellWidth = 32;
    private const int HorizontalScrollHeight = 16;
    private const int HorizontalScrollArrowWidth = 15;
    private const int VerticalScrollWidth = 7;
    private const int VisibilityColumnWidth = 30;
    private const int LockColumnWidth = 24;
    private const int OutlineColumnWidth = 24;
    private const int LockColumnRight = VisibilityColumnWidth + LockColumnWidth;
    private const int LayerControlsWidth = LockColumnRight + OutlineColumnWidth;
    private const int AddLayerButtonWidth = 26;
    private const int SoloButtonWidth = 48;
    private const int AllButtonWidth = 42;
    private const int HeightResizeHandleHeight = 6;
    private static readonly TimelineFrameTransformMode[] FrameTransformHandleModes =
    [
        TimelineFrameTransformMode.ScaleTopLeft,
        TimelineFrameTransformMode.ScaleTopRight,
        TimelineFrameTransformMode.ScaleBottomRight,
        TimelineFrameTransformMode.ScaleBottomLeft,
        TimelineFrameTransformMode.ScaleLeft,
        TimelineFrameTransformMode.ScaleRight
    ];

    private ITimelineContext _context = null!;
    private AnimationTimeline _timeline = null!;
    private VectorScene? _vectorScene;
    private DrawingObjectDefinition? _drawingObjectDefinition;
    private SceneDefinition? _sceneDefinition;
    private string? _activeTrackId;
    private bool _draggingPlayhead;
    private bool _draggingFrameSelection;
    private TimelineFrameCell? _frameSelectionDragEnd;
    private bool _frameSelectionNotificationPending;
    private bool _frameSelectionFramePending;
    private bool _draggingFrameTransform;
    private bool _draggingLayer;
    private bool _draggingHorizontalScroll;
    private bool _draggingVerticalScroll;
    private TimelineOnionSkinRangeHandle _draggingOnionSkinRangeHandle;
    private TimelineOnionSkinRangeHandle _hoveredOnionSkinRangeHandle;
    private HeaderCommand _hoveredHeaderCommand;
    private int _horizontalScrollDragOffset;
    private int _verticalScrollDragOffset;
    private int _currentFrame;
    private int _startFrame;
    private int _endFrame;
    private int _knownFrameCount;
    private int _frameCellWidth = FrameCellWidth;
    private int _rowHeight = RowHeight;
    private TimelineFrameHeightPreset _frameHeightPreset = TimelineFrameHeightPreset.Medium;
    private int _firstVisibleFrame;
    private int _firstVisibleTrack;
    private int _hoverFrame = -1;
    private int _hoverTrack = -1;
    private TimelineFrameTransformMode _hoveredFrameTransformHandle;
    private int _layerDropTrack = -1;
    private TimelineLayerDropPlacement _layerDropPlacement;
    private bool _draggingHeightResize;
    private int _heightResizeStartScreenY;
    private int _heightResizeStartHeight;
    private int? _pendingHeightResize;
    private bool _heightResizeLayoutDirty;
    private readonly System.Windows.Forms.Timer _heightResizeTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _frameSelectionAutoScrollTimer = new() { Interval = 80 };
    private Point _frameSelectionPointer;
    private Point _frameTransformPointer;
    private readonly System.Windows.Forms.Timer _frameWidthCommitTimer = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer _commandFeedbackTimer = new() { Interval = 16 };
    private TimelineCommand? _commandFeedback;
    private TimelineFrameCell[] _commandFeedbackCells = [];
    private long _commandFeedbackStartedMilliseconds;
    private readonly System.Windows.Forms.Timer _layerFeedbackTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _layerDragAnimationTimer = new() { Interval = 16 };
    private readonly SvgIconBitmapCache _headerIconCache = new();
    private readonly PointF[] _playheadHandlePoints = new PointF[5];
    private TimelineLayerFeedbackTarget[] _layerFeedbackTargets = [];
    private TimelineLayerVisualSnapshot[] _knownLayerVisuals = [];
    private string[] _knownTrackStructureIds = [];
    private readonly List<int> _visibleTrackIndicesCache = [];
    private int[] _visibleTrackPositionsCache = [];
    private int[] _trackLayerIndicesCache = [];
    private readonly Dictionary<string, int> _trackIndicesByIdCache = new(StringComparer.Ordinal);
    private bool _timelineStructureCacheDirty = true;
    private long _layerFeedbackStartedMilliseconds;
    private readonly HashSet<string> _layerDragTrackIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _layerDragOffsets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _layerDragTargetOffsets = new(StringComparer.Ordinal);
    private int _layerDragStartPointerY;
    private int _layerDragPointerY;
    private long _layerDragLastTickMilliseconds;
    private bool _layerDragPreviewValid;
    private bool _isPlaying;
    private bool _suppressActiveLayerChanged;
    private readonly HashSet<TimelineFrameCell> _selectedFrameCells = [];
    private readonly HashSet<string> _selectedLayerTrackIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedFolderLayerIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedMaskLayerIds = new(StringComparer.Ordinal);
    private TimelineFrameCell? _selectionAnchor;
    private TimelineFrameTransformMode _frameTransformMode;
    private int _frameTransformStartFrame;
    private int _frameTransformStartTrackPosition;
    private TimelineFrameSelectionBounds _frameTransformOriginalBounds;
    private TimelineFrameSelectionBounds _frameTransformPreviewBounds;
    private bool _frameTransformChanged;
    private TimelineFrameCell[] _frameTransformSourceCells = [];
    private string[] _frameTransformVisibleTrackIds = [];
    private TimelineFrameTransformCell[] _frameTransformPairs = [];
    private TimelineFrameCell[] _frameTransformPreviewCells = [];
    private TimelineTweenSelection? _selectedTween;
    private string? _layerSelectionAnchorTrackId;
    private string? _draggedLayerTrackId;
    private readonly AnimatedContextMenuStrip _layerContextMenu = new();
    private readonly AnimatedContextMenuStrip _frameContextMenu = new();
    private readonly ToolTip _layerControlToolTip = new() { InitialDelay = 350, ReshowDelay = 80, AutoPopDelay = 5000 };
    private string _layerControlToolTipText = "";
    private readonly ToolStripMenuItem _newDrawingLayerMenuItem;
    private readonly ToolStripMenuItem _copyFramesMenuItem;
    private readonly ToolStripMenuItem _pasteFramesMenuItem;
    private readonly ToolStripMenuItem _reverseFramesMenuItem;
    private readonly ToolStripMenuItem _insertFramesMenuItem;
    private readonly ToolStripMenuItem _deleteFramesMenuItem;
    private readonly ToolStripMenuItem _insertKeyframesMenuItem;
    private readonly ToolStripMenuItem _insertBlankKeyframesMenuItem;
    private readonly ToolStripMenuItem _clearKeyframesMenuItem;
    private readonly ToolStripMenuItem _classicTweenMenuItem;
    private readonly ToolStripMenuItem _shapeTweenMenuItem;
    private readonly ToolStripMenuItem _removeTweenMenuItem;
    private readonly ToolStripMenuItem _newFolderLayerMenuItem;
    private readonly ToolStripMenuItem _newMaskLayerMenuItem;
    private readonly ToolStripMenuItem _toggleFolderMenuItem;
    private readonly ToolStripMenuItem _moveLayerOutOfMaskMenuItem;
    private readonly ToolStripMenuItem _moveLayerUpMenuItem;
    private readonly ToolStripMenuItem _moveLayerDownMenuItem;
    private readonly ToolStripMenuItem _removeLayersMenuItem;
    private readonly ToolStripMenuItem _renameLayerMenuItem;
    private readonly ToolStripMenuItem _layerColorMenuItem;
    private readonly ToolStripMenuItem _lockLayerMenuItem;
    private readonly ToolStripMenuItem _outlineLayerMenuItem;
    private readonly ToolStripMenuItem _showSelectedLayersMenuItem;
    private readonly ToolStripMenuItem _hideSelectedLayersMenuItem;
    private readonly Label _frameWidthLabel = CreateHeaderLabel("Frame width", "Timeline frame width");
    private readonly ModernSlider _frameWidthSlider = CreateFrameWidthSlider();
    private readonly ModernNumericUpDown _frameWidthInput = CreateFrameWidthInput();
    private readonly Label _frameHeightLabel = CreateHeaderLabel("Frame height", "Timeline frame height");
    private readonly ComboBox _frameHeightInput = CreateFrameHeightInput();
    private readonly ModernToggleSwitch _autoKeyframeToggle = new()
    {
        AutoSize = false,
        Text = "Auto Key",
        AccessibleName = "Automatically insert a keyframe before canvas edits",
        Height = Theme.ControlHeightCompact
    };
    private readonly ModernToggleSwitch _onionSkinToggle = new()
    {
        AutoSize = false,
        Text = "Onion",
        AccessibleName = "Enable onion skin for the drawing timeline",
        Height = Theme.ControlHeightCompact
    };
    private readonly ModernToggleSwitch _motionTrackToggle = new()
    {
        AutoSize = false,
        Text = "Motion",
        AccessibleName = "Show the selected symbol's motion track on the Stage",
        Height = Theme.ControlHeightCompact
    };
    private readonly Label _onionPreviousLabel = CreateOnionSkinRangeLabel("Prev");
    private readonly ModernNumericUpDown _onionPreviousFrames = CreateOnionSkinRangeInput("Previous onion skin frames");
    private readonly Label _onionNextLabel = CreateOnionSkinRangeLabel("Next");
    private readonly ModernNumericUpDown _onionNextFrames = CreateOnionSkinRangeInput("Next onion skin frames");

    /// <summary>
    /// Upper bound for the shared range inputs while they hold absolute motion-track frames. The
    /// sampler clamps to the timeline duration, so this only has to be generous enough that the
    /// operator can type any frame the project could hold.
    /// </summary>
    private const int MaximumMotionTrackFrame = 100_000;
    private bool _updatingOnionSkinControls;
    private bool _updatingMotionTrackControls;

    /// <summary>Mode the shared range inputs were last rewording for; drives the republish on a switch.</summary>
    private bool _onionSkinRangeAbsoluteApplied;
    private bool _updatingFrameWidthControls;
    private bool _updatingFrameHeightControl;
    private bool _frameWidthCommitPending;
    private decimal _playbackFps = 30m;
    private HeaderLayoutKey? _headerLayoutKey;

    public event EventHandler? CurrentFrameChanged;
    public event EventHandler? ActiveLayerChanged;
    public event EventHandler? LayerVisibilityChanged;
    public event EventHandler? AddLayerRequested;
    public event EventHandler? AddFolderLayerRequested;
    public event EventHandler? AddMaskLayerRequested;
    public event EventHandler? MoveLayerOutOfMaskRequested;
    public event EventHandler? FrameWidthCommitted;
    public event EventHandler? FrameHeightCommitted;
    public event EventHandler? AutoKeyframeChanged;
    public event EventHandler? FrameSelectionChanged;
    public event EventHandler<TimelineFrameTransformRequestedEventArgs>? FrameTransformRequested;
    public event EventHandler? SelectedTweenChanged;
    public event EventHandler<TimelineCommandRequestedEventArgs>? CommandRequested;
    public event EventHandler<TimelineLayerMoveRequestedEventArgs>? LayerMoveRequested;
    public event EventHandler? RemoveLayersRequested;
    public event EventHandler? LayerRenameRequested;
    public event EventHandler? LayerColorRequested;
    public event EventHandler? LayerLockRequested;
    public event EventHandler? LayerOutlineRequested;
    public event EventHandler? AllLayerVisibilityRequested;
    public event EventHandler? AllLayerLocksRequested;
    public event EventHandler? AllLayerOutlinesRequested;
    public event EventHandler? OnionSkinToggleRequested;
    public event EventHandler<TimelineOnionSkinRangeChangedEventArgs>? OnionSkinRangeChanged;
    public event EventHandler? OnionSkinRangeInteractionStarted;
    public event EventHandler? OnionSkinRangeInteractionCompleted;
    public event EventHandler? OnionSkinRangeInteractionCanceled;
    public event EventHandler? MotionTrackToggleRequested;
    public event EventHandler<TimelineMotionTrackRangeChangedEventArgs>? MotionTrackRangeChanged;

    /// <summary>
    /// True while the workbench is presenting a motion track for the selected symbol. Set by the
    /// workbench after it rebuilds the track, so the toggle reflects real availability.
    /// </summary>
    public bool MotionTrackToggleAvailable { get; set; }

    /// <summary>Checked state of the motion-track toggle, owned by the workbench.</summary>
    public bool MotionTrackEnabled { get; set; }

    /// <summary>
    /// First frame of the motion track's own sampling range. Unlike the onion-skin range this is an
    /// absolute frame, so moving the playhead never moves the sampled window.
    /// </summary>
    public int MotionTrackRangeFirst { get; set; }

    /// <summary>Last sampled frame of the motion track, inclusive.</summary>
    public int MotionTrackRangeLast { get; set; }

    /// <summary>True while the range inputs belong on the header, i.e. while a track is presented.</summary>
    public bool MotionTrackRangeVisible { get; set; }

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
        MinimumSize = new Size(360, 148);
        TabStop = true;
        _copyFramesMenuItem = CreateContextMenuItem("Copy Frames", TimelineCommand.CopyFrames, Keys.Control | Keys.C);
        _pasteFramesMenuItem = CreateContextMenuItem("Paste Frames", TimelineCommand.PasteFrames, Keys.Control | Keys.V);
        _reverseFramesMenuItem = CreateContextMenuItem("Reverse Frames", TimelineCommand.ReverseFrames, Keys.None);
        _insertFramesMenuItem = CreateContextMenuItem("Insert Frames", TimelineCommand.InsertFrames, Keys.F5);
        _deleteFramesMenuItem = CreateContextMenuItem("Delete Frames", TimelineCommand.DeleteFrames, Keys.Shift | Keys.F5);
        _insertKeyframesMenuItem = CreateContextMenuItem("Insert Keyframes", TimelineCommand.InsertKeyframes, Keys.F6);
        _insertBlankKeyframesMenuItem = CreateContextMenuItem("Insert Blank Keyframes", TimelineCommand.InsertBlankKeyframes, Keys.F7);
        _clearKeyframesMenuItem = CreateContextMenuItem("Clear Keyframes", TimelineCommand.ClearKeyframes, Keys.Shift | Keys.F6);
        _classicTweenMenuItem = CreateContextMenuItem("Create Classic Tween", TimelineCommand.ClassicTween, Keys.None);
        _shapeTweenMenuItem = CreateContextMenuItem("Create Shape Tween", TimelineCommand.ShapeTween, Keys.None);
        _removeTweenMenuItem = CreateContextMenuItem("Remove Tween", TimelineCommand.RemoveTween, Keys.None);
        _newDrawingLayerMenuItem = new ToolStripMenuItem("New Drawing Layer", null, (_, _) => AddLayerRequested?.Invoke(this, EventArgs.Empty));
        _newFolderLayerMenuItem = new ToolStripMenuItem("New Folder Layer", null, (_, _) => AddFolderLayerRequested?.Invoke(this, EventArgs.Empty));
        _newMaskLayerMenuItem = new ToolStripMenuItem("New Mask Layer", null, (_, _) => AddMaskLayerRequested?.Invoke(this, EventArgs.Empty));
        _toggleFolderMenuItem = new ToolStripMenuItem("Collapse Folder", null, (_, _) => ToggleActiveTrackGroupCollapsed());
        _moveLayerOutOfMaskMenuItem = new ToolStripMenuItem("Move Out of Mask Layer", null, (_, _) => MoveLayerOutOfMaskRequested?.Invoke(this, EventArgs.Empty));
        _moveLayerUpMenuItem = new ToolStripMenuItem("Move Layer Up", null, (_, _) => RequestLayerMove(-1));
        _moveLayerDownMenuItem = new ToolStripMenuItem("Move Layer Down", null, (_, _) => RequestLayerMove(1));
        _removeLayersMenuItem = new ToolStripMenuItem("Delete Layer", null, (_, _) => RemoveLayersRequested?.Invoke(this, EventArgs.Empty));
        _renameLayerMenuItem = new ToolStripMenuItem("Rename Layer...", null, (_, _) => LayerRenameRequested?.Invoke(this, EventArgs.Empty))
        {
            ShortcutKeys = Keys.Shift | Keys.F2
        };
        _layerColorMenuItem = new ToolStripMenuItem("Layer Color...", null, (_, _) => LayerColorRequested?.Invoke(this, EventArgs.Empty));
        _lockLayerMenuItem = new ToolStripMenuItem("Lock Layer", null, (_, _) => LayerLockRequested?.Invoke(this, EventArgs.Empty));
        _outlineLayerMenuItem = new ToolStripMenuItem("Show Layer as Outline", null, (_, _) => LayerOutlineRequested?.Invoke(this, EventArgs.Empty));
        _showSelectedLayersMenuItem = new ToolStripMenuItem("Show Selected Layers", null, (_, _) => SetSelectedTrackVisibility(true));
        _hideSelectedLayersMenuItem = new ToolStripMenuItem("Hide Selected Layers", null, (_, _) => SetSelectedTrackVisibility(false));
        _layerContextMenu.Items.AddRange(new ToolStripItem[]
        {
            _newDrawingLayerMenuItem,
            _newFolderLayerMenuItem,
            _newMaskLayerMenuItem,
            _toggleFolderMenuItem,
            _moveLayerOutOfMaskMenuItem,
            new ToolStripSeparator(),
            _moveLayerUpMenuItem,
            _moveLayerDownMenuItem,
            new ToolStripSeparator(),
            _removeLayersMenuItem,
            new ToolStripSeparator(),
            _showSelectedLayersMenuItem,
            _hideSelectedLayersMenuItem,
            new ToolStripSeparator(),
            _renameLayerMenuItem,
            _lockLayerMenuItem,
            _outlineLayerMenuItem,
            _layerColorMenuItem
        });
        _frameContextMenu.Items.AddRange(new ToolStripItem[]
        {
            _copyFramesMenuItem,
            _pasteFramesMenuItem,
            _reverseFramesMenuItem,
            new ToolStripSeparator(),
            _insertFramesMenuItem,
            _deleteFramesMenuItem,
            _insertKeyframesMenuItem,
            _insertBlankKeyframesMenuItem,
            _clearKeyframesMenuItem,
            new ToolStripSeparator(),
            _classicTweenMenuItem,
            _shapeTweenMenuItem,
            new ToolStripSeparator(),
            _removeTweenMenuItem
        });
        _layerContextMenu.Opening += HandleLayerContextMenuOpening;
        _frameContextMenu.Opening += HandleFrameContextMenuOpening;
        _heightResizeTimer.Tick += (_, _) => TickHeightResize();
        _frameSelectionAutoScrollTimer.Tick += (_, _) => TickFrameSelectionAutoScroll();
        _frameWidthCommitTimer.Tick += (_, _) => CommitFrameWidthChange();
        _commandFeedbackTimer.Tick += (_, _) => TickCommandFeedback();
        _layerFeedbackTimer.Tick += (_, _) => TickLayerFeedback();
        _layerDragAnimationTimer.Tick += (_, _) => TickLayerDragAnimation();
        _autoKeyframeToggle.CheckedChanged += (_, _) => AutoKeyframeChanged?.Invoke(this, EventArgs.Empty);
        _onionSkinToggle.CheckedChanged += (_, _) =>
        {
            if (!_updatingOnionSkinControls) OnionSkinToggleRequested?.Invoke(this, EventArgs.Empty);
        };
        _motionTrackToggle.CheckedChanged += (_, _) =>
        {
            if (!_updatingMotionTrackControls) MotionTrackToggleRequested?.Invoke(this, EventArgs.Empty);
        };
        _onionPreviousFrames.ValueChanged += (_, _) => RaiseOnionSkinRangeChanged();
        _onionNextFrames.ValueChanged += (_, _) => RaiseOnionSkinRangeChanged();
        _onionPreviousFrames.InteractionStarted += (_, _) => RaiseOnionSkinRangeInteraction(OnionSkinRangeInteractionStarted);
        _onionNextFrames.InteractionStarted += (_, _) => RaiseOnionSkinRangeInteraction(OnionSkinRangeInteractionStarted);
        _onionPreviousFrames.InteractionCompleted += (_, _) => RaiseOnionSkinRangeInteraction(OnionSkinRangeInteractionCompleted);
        _onionNextFrames.InteractionCompleted += (_, _) => RaiseOnionSkinRangeInteraction(OnionSkinRangeInteractionCompleted);
        _onionPreviousFrames.InteractionCanceled += (_, _) => RaiseOnionSkinRangeInteraction(OnionSkinRangeInteractionCanceled);
        _onionNextFrames.InteractionCanceled += (_, _) => RaiseOnionSkinRangeInteraction(OnionSkinRangeInteractionCanceled);
        _frameWidthSlider.ValueChanged += (_, _) =>
        {
            if (_updatingFrameWidthControls) return;
            SetFrameCellWidth(_frameWidthSlider.Value);
            ScheduleFrameWidthCommit();
        };
        _frameWidthInput.ValueChanged += (_, _) =>
        {
            if (_updatingFrameWidthControls) return;
            SetFrameCellWidth((int)_frameWidthInput.Value);
            ScheduleFrameWidthCommit();
        };
        _frameWidthSlider.InteractionCompleted += (_, _) => CommitFrameWidthChange();
        _frameWidthInput.InteractionCompleted += (_, _) => CommitFrameWidthChange();
        _frameWidthLabel.TextChanged += (_, _) => RefreshHeaderLayoutAfterTextChange();
        _frameHeightLabel.TextChanged += (_, _) => RefreshHeaderLayoutAfterTextChange();
        _autoKeyframeToggle.TextChanged += (_, _) => RefreshHeaderLayoutAfterTextChange();
        _frameHeightInput.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingFrameHeightControl || _frameHeightInput.SelectedIndex < 0) return;
            FrameHeightPreset = (TimelineFrameHeightPreset)_frameHeightInput.SelectedIndex;
            FrameHeightCommitted?.Invoke(this, EventArgs.Empty);
        };
        Controls.AddRange([
            _frameWidthLabel,
            _frameWidthSlider,
            _frameWidthInput,
            _frameHeightLabel,
            _frameHeightInput,
            _autoKeyframeToggle,
            _onionSkinToggle,
            _motionTrackToggle,
            _onionPreviousLabel,
            _onionPreviousFrames,
            _onionNextLabel,
            _onionNextFrames
        ]);
        InitializeTabGroupUi();
        BindContext(context);
    }

    private void RefreshHeaderLayoutAfterTextChange()
    {
        _headerLayoutKey = null;
        if (_timeline is null) return;
        LayoutHeaderControls(CreateLayout(), IsOnionSkinControlsAvailable());
        Invalidate();
    }

    public ITimelineContext Context => _context;

    public bool AutoKeyframeEnabled
    {
        get => _autoKeyframeToggle.Checked;
        set
        {
            if (_autoKeyframeToggle.Checked != value) _autoKeyframeToggle.Checked = value;
        }
    }

    public decimal PlaybackFps
    {
        get => _playbackFps;
        set
        {
            var next = Math.Clamp(value, 1m, 120m);
            if (_playbackFps == next) return;
            _playbackFps = next;
            Invalidate();
        }
    }

    public int FrameWidth
    {
        get => _frameCellWidth;
        set => SetFrameCellWidth(value);
    }

    public TimelineFrameHeightPreset FrameHeightPreset
    {
        get => _frameHeightPreset;
        set => SetFrameHeightPreset(value);
    }

    public int FrameHeight => _rowHeight;

    private void SetFrameHeightPreset(TimelineFrameHeightPreset value)
    {
        var next = Enum.IsDefined(value) ? value : TimelineFrameHeightPreset.Medium;
        _updatingFrameHeightControl = true;
        try
        {
            var index = (int)next;
            if (_frameHeightInput.SelectedIndex != index) _frameHeightInput.SelectedIndex = index;
        }
        finally
        {
            _updatingFrameHeightControl = false;
        }

        if (_frameHeightPreset == next) return;
        var previousLayout = CreateLayout();
        var activeTrackPosition = VisibleTrackPosition(GetActiveTrackIndex());
        var activeTrackCenter = activeTrackPosition < 0
            ? previousLayout.RowTop + _rowHeight * 0.5f
            : previousLayout.RowTop
                + (activeTrackPosition - _firstVisibleTrack) * _rowHeight
                + _rowHeight * 0.5f;
        _frameHeightPreset = next;
        _rowHeight = RowHeightFor(next);
        var nextLayout = CreateLayout();
        if (activeTrackPosition >= 0)
        {
            var desiredRow = (int)MathF.Round(
                (activeTrackCenter - nextLayout.RowTop - _rowHeight * 0.5f) / _rowHeight);
            SetFirstVisibleTrack(activeTrackPosition - desiredRow, invalidate: false);
        }
        EnsureActiveTrackVisible();
        Invalidate();
    }

    internal static int RowHeightFor(TimelineFrameHeightPreset preset)
    {
        return preset switch
        {
            TimelineFrameHeightPreset.Low => LowRowHeight,
            TimelineFrameHeightPreset.High => HighRowHeight,
            _ => RowHeight
        };
    }

    private void SetFrameCellWidth(int value)
    {
        var next = Math.Clamp(value, MinimumFrameCellWidth, MaximumFrameCellWidth);
        _updatingFrameWidthControls = true;
        try
        {
            if (_frameWidthSlider.Value != next) _frameWidthSlider.Value = next;
            if ((int)_frameWidthInput.Value != next) _frameWidthInput.Value = next;
        }
        finally
        {
            _updatingFrameWidthControls = false;
        }

        if (_frameCellWidth == next) return;
        var previousLayout = CreateLayout();
        var playheadCenter = previousLayout.TrackLeft
            + (_currentFrame - _firstVisibleFrame) * _frameCellWidth
            + _frameCellWidth * 0.5f;
        _frameCellWidth = next;
        var nextLayout = CreateLayout();
        var playheadColumn = (int)MathF.Round(
            (playheadCenter - nextLayout.TrackLeft - _frameCellWidth * 0.5f) / _frameCellWidth);
        SetFirstVisibleFrame(_currentFrame - playheadColumn, invalidate: false);
        EnsureCurrentFrameVisibleCore();
        Invalidate();
    }

    private void ScheduleFrameWidthCommit()
    {
        _frameWidthCommitPending = true;
        _frameWidthCommitTimer.Stop();
        _frameWidthCommitTimer.Start();
    }

    private void CommitFrameWidthChange()
    {
        _frameWidthCommitTimer.Stop();
        if (!_frameWidthCommitPending) return;
        _frameWidthCommitPending = false;
        FrameWidthCommitted?.Invoke(this, EventArgs.Empty);
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

    public TimelineTweenSelection? SelectedTween => ResolveLiveTweenSelection(_selectedTween);

    internal TimelineSelectionSnapshot CaptureSelectionSnapshot()
    {
        return new TimelineSelectionSnapshot(
            _selectedFrameCells.ToArray(),
            _selectionAnchor,
            _activeTrackId,
            _selectedLayerTrackIds.ToArray(),
            _layerSelectionAnchorTrackId);
    }

    internal void RestoreSelectionSnapshot(TimelineSelectionSnapshot snapshot)
    {
        var activeTrack = TrackIndexForId(snapshot.ActiveTrackId ?? "");
        if (IsTrackEditableInCurrentPresentation(activeTrack)) SetActiveTrack(activeTrack);

        _selectedLayerTrackIds.Clear();
        foreach (var trackId in snapshot.SelectedLayerTrackIds)
        {
            var trackIndex = TrackIndexForId(trackId);
            if (IsTrackEditableInCurrentPresentation(trackIndex) && IsTrackSelectableRow(trackIndex))
            {
                _selectedLayerTrackIds.Add(trackId);
            }
        }
        _layerSelectionAnchorTrackId = snapshot.LayerAnchorTrackId is { } layerAnchor
            && _selectedLayerTrackIds.Contains(layerAnchor)
                ? layerAnchor
                : null;

        var cells = snapshot.FrameCells
            .Where(cell => IsTrackEditableInCurrentPresentation(TrackIndexForId(cell.TrackId))
                && cell.Frame >= StartFrame)
            .ToArray();
        var anchor = snapshot.FrameAnchor is { } frameAnchor
            && IsTrackEditableInCurrentPresentation(TrackIndexForId(frameAnchor.TrackId))
            && frameAnchor.Frame >= StartFrame
                ? frameAnchor
                : (TimelineFrameCell?)null;
        SetFrameSelection(cells, anchor);
        EnsureActiveTrackVisible();
        EnsureCurrentFrameVisible();
    }

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
                if (EnsureCurrentFrameVisibleCore()) Invalidate();
                return;
            }

            var previousFrame = _currentFrame;
            _currentFrame = next;
            if (EnsureCurrentFrameVisibleCore()) Invalidate();
            else InvalidateFrameTransition(previousFrame, next);
            if (_draggingFrameSelection) _frameSelectionFramePending = true;
            else CurrentFrameChanged?.Invoke(this, EventArgs.Empty);
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
        var hadSelectedTween = _selectedTween is not null;
        CancelCommandFeedback();
        CancelLayerFeedback();

        if (_timeline is not null)
        {
            _timeline.Changed -= HandleTimelineChanged;
            _timeline.TabGroupsChanged -= HandleTabGroupsChanged;
        }

        _context = context;
        _vectorScene = context as VectorScene;
        _drawingObjectDefinition = context as DrawingObjectDefinition;
        _sceneDefinition = context as SceneDefinition;
        _context.SynchronizeTimelineTracks();
        _timeline = _context.Timeline;
        _timeline.Changed += HandleTimelineChanged;
        _timeline.TabGroupsChanged += HandleTabGroupsChanged;

        _knownFrameCount = FrameCount;
        _startFrame = 0;
        _endFrame = Math.Max(0, _knownFrameCount - 1);
        _currentFrame = Math.Clamp(_currentFrame, _startFrame, _endFrame);
        _firstVisibleFrame = _startFrame;
        _firstVisibleTrack = 0;
        _collapsedFolderLayerIds.Clear();
        _collapsedMaskLayerIds.Clear();
        InvalidateTimelineStructureCache();
        _activeTrackId = TrackCount > 0 ? _timeline.Tracks[Math.Max(0, GetModelActiveTrackIndex())].Id : null;
        _selectedFrameCells.Clear();
        _selectedTween = null;
        _selectedLayerTrackIds.Clear();
        _selectionAnchor = null;
        _layerSelectionAnchorTrackId = _activeTrackId;
        if (!string.IsNullOrWhiteSpace(_activeTrackId)) _selectedLayerTrackIds.Add(_activeTrackId);
        _knownLayerVisuals = CaptureLayerVisualSnapshots();
        CaptureKnownTrackStructure();
        RefreshOnionSkinControls();
        EnsureActiveTrackVisible();
        EnsureCurrentFrameVisible();
        Invalidate();
        if (hadSelectedTween) SelectedTweenChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshTimeline()
    {
        _context.SynchronizeTimelineTracks();
        var boundTimeline = _context.Timeline;
        if (!ReferenceEquals(_timeline, boundTimeline))
        {
            _timeline.Changed -= HandleTimelineChanged;
            _timeline.TabGroupsChanged -= HandleTabGroupsChanged;
            _timeline = boundTimeline;
            _timeline.Changed += HandleTimelineChanged;
            _timeline.TabGroupsChanged += HandleTabGroupsChanged;
        }

        InvalidateTimelineStructureCache();
        PruneCollapsedLayerGroupIds();
        PruneLayerSelection();
        DetectLayerCollectionFeedback();
        CaptureKnownTrackStructure();
        UpdateFrameBoundsForTimelineChange();
        RefreshOnionSkinControls();
        EnsureActiveTrackVisible();
        EnsureCurrentFrameVisible();
        UpdateSelectedTweenFromFrameSelection(forceNotification: _selectedTween is not null);
        Invalidate();
    }

    public void SelectModelActiveTrack()
    {
        var trackIndex = GetModelActiveTrackIndex();
        if (trackIndex < 0) return;
        _selectedLayerTrackIds.Clear();
        if (IsTrackSelectableRow(trackIndex)) _selectedLayerTrackIds.Add(_timeline.Tracks[trackIndex].Id);
        _layerSelectionAnchorTrackId = _timeline.Tracks[trackIndex].Id;
        SetActiveTrack(trackIndex);
    }

    internal bool SelectSingleLayerTarget(string targetId, bool notifyActiveLayerChanged = true)
    {
        if (string.IsNullOrWhiteSpace(targetId)) return false;
        for (var trackIndex = 0; trackIndex < TrackCount; trackIndex++)
        {
            if (!IsTrackSelectableRow(trackIndex)
                || !IsTrackEditableInCurrentPresentation(trackIndex)
                || !string.Equals(_timeline.Tracks[trackIndex].TargetId, targetId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsTrackInActiveTabGroup(trackIndex))
            {
                _timeline.SetActiveTabGroup(_timeline.Tracks[trackIndex].TabGroupId);
            }

            var previousSuppress = _suppressActiveLayerChanged;
            _suppressActiveLayerChanged = previousSuppress || !notifyActiveLayerChanged;
            try
            {
                SelectLayerTrack(trackIndex, Keys.None);
            }
            finally
            {
                _suppressActiveLayerChanged = previousSuppress;
            }
            return true;
        }

        return false;
    }

    public void SelectSingleFrame(string trackId, int frame)
    {
        var trackIndex = TrackIndexForId(trackId);
        if (!IsTrackEditableInCurrentPresentation(trackIndex)) return;
        SelectLayerTrack(trackIndex, Keys.None);
        var next = Math.Max(frame, StartFrame);
        var cell = new TimelineFrameCell(trackId, next);
        SetFrameSelection([cell], cell);
        CurrentFrame = next;
    }

    internal void SelectFrameCells(
        IEnumerable<TimelineFrameCell> cells,
        TimelineFrameCell? anchor = null)
    {
        var validCells = cells
            .Where(cell => IsTrackEditableInCurrentPresentation(TrackIndexForId(cell.TrackId))
                && cell.Frame >= StartFrame)
            .Distinct()
            .ToArray();
        if (validCells.Length == 0) return;

        var activeTrack = TrackIndexForId(validCells[0].TrackId);
        if (activeTrack >= 0) SelectLayerTrack(activeTrack, Keys.None);
        SetFrameSelection(validCells, anchor ?? validCells[0]);
    }

    internal void ClearSelectionFromEmptyArea()
    {
        var frameSelectionChanged = _selectedFrameCells.Count > 0 || _selectionAnchor is not null;
        _selectedFrameCells.Clear();
        _selectionAnchor = null;

        var activeTrack = GetActiveTrackIndex();
        var layerSelectionChanged = _selectedLayerTrackIds.Count > 1;
        if (layerSelectionChanged)
        {
            _selectedLayerTrackIds.Clear();
            if (IsTrackSelectableRow(activeTrack))
            {
                var trackId = _timeline.Tracks[activeTrack].Id;
                _selectedLayerTrackIds.Add(trackId);
                _layerSelectionAnchorTrackId = trackId;
            }
            else
            {
                _layerSelectionAnchorTrackId = null;
            }
        }

        if (frameSelectionChanged) NotifyFrameSelectionChanged();
        else if (layerSelectionChanged) Invalidate();
    }

    public void RefreshOnionSkinControls()
    {
        var drawingScene = DrawingScene();
        var sceneDefinition = drawingScene is null ? _sceneDefinition : null;
        var available = !_shotFilterActive && (drawingScene is not null || sceneDefinition is not null);
        var enabled = drawingScene?.OnionSkinEnabled ?? sceneDefinition?.OnionSkinEnabled == true;
        var previousFrames = drawingScene?.OnionSkinPreviousFrames ?? sceneDefinition?.OnionSkinPreviousFrames ?? 0;
        var nextFrames = drawingScene?.OnionSkinNextFrames ?? sceneDefinition?.OnionSkinNextFrames ?? 0;

        _updatingOnionSkinControls = true;
        try
        {
            if (_onionSkinToggle.Enabled != available) _onionSkinToggle.Enabled = available;
            ApplyOnionSkinRangeControlMode();
            if (available)
            {
                if (_onionSkinToggle.Checked != enabled) _onionSkinToggle.Checked = enabled;
                // Same two inputs, two meanings: with a track presented they carry its absolute
                // window, otherwise the onion skin's relative counts.
                var firstValue = MotionTrackRangeVisible ? MotionTrackRangeFirst : previousFrames;
                var lastValue = MotionTrackRangeVisible ? MotionTrackRangeLast : nextFrames;
                if (_onionPreviousFrames.Value != firstValue) _onionPreviousFrames.Value = firstValue;
                if (_onionNextFrames.Value != lastValue) _onionNextFrames.Value = lastValue;
            }
        }
        finally
        {
            _updatingOnionSkinControls = false;
        }

        if (!enabled) ResetOnionSkinRangeHandleInteraction();
        LayoutHeaderControls(CreateLayout(), available);
    }

    /// <summary>
    /// Rewords the shared range inputs for the mode they are in. The controls themselves are never
    /// swapped: onion skin reads them as "frames before/after the playhead" and the motion track reads
    /// them as "first/last frame", so only the wording and the upper bound follow the mode.
    /// </summary>
    private void ApplyOnionSkinRangeControlMode()
    {
        var absolute = MotionTrackRangeVisible;
        _onionSkinRangeAbsoluteApplied = absolute;
        var maximum = absolute ? MaximumMotionTrackFrame : VectorScene.MaximumOnionSkinFrames;
        if (_onionPreviousFrames.Maximum != maximum) _onionPreviousFrames.Maximum = maximum;
        if (_onionNextFrames.Maximum != maximum) _onionNextFrames.Maximum = maximum;

        ApplyRangeLabel(
            _onionPreviousLabel,
            absolute,
            relativeText: "Prev",
            absoluteText: "From",
            relativeAccessibleName: "Previous onion skin frames",
            absoluteAccessibleName: "Motion track first frame");
        ApplyRangeLabel(
            _onionNextLabel,
            absolute,
            relativeText: "Next",
            absoluteText: "To",
            relativeAccessibleName: "Next onion skin frames",
            absoluteAccessibleName: "Motion track last frame");
    }

    private static void ApplyRangeLabel(
        Label label,
        bool absolute,
        string relativeText,
        string absoluteText,
        string relativeAccessibleName,
        string absoluteAccessibleName)
    {
        var text = absolute ? absoluteText : relativeText;
        var accessibleName = absolute ? absoluteAccessibleName : relativeAccessibleName;
        if (!string.Equals(label.Text, text, StringComparison.Ordinal)) label.Text = text;
        if (!string.Equals(label.AccessibleName, accessibleName, StringComparison.Ordinal))
        {
            label.AccessibleName = accessibleName;
        }
    }

    /// <summary>
    /// Mirrors the motion-track toggle against the editing state. Availability and the checked
    /// value are owned by the workbench, because the track only exists while a symbol instance is
    /// selected; the strip never infers that from the timeline context alone.
    /// </summary>
    public void RefreshMotionTrackControls()
    {
        _updatingMotionTrackControls = true;
        try
        {
            if (_motionTrackToggle.Checked != MotionTrackEnabled) _motionTrackToggle.Checked = MotionTrackEnabled;
            if (_motionTrackToggle.Enabled != MotionTrackToggleAvailable)
            {
                _motionTrackToggle.Enabled = MotionTrackToggleAvailable;
            }

        }
        finally
        {
            _updatingMotionTrackControls = false;
        }

        // The two range inputs are shared, so a mode switch or a new window has to republish them;
        // otherwise they would keep showing the onion-skin counts the operator just left behind.
        if (MotionTrackRangeVisible || _onionSkinRangeAbsoluteApplied) RefreshOnionSkinControls();

        LayoutHeaderControls(CreateLayout(), IsOnionSkinControlsAvailable());
        Invalidate();
    }

    private void ResetOnionSkinRangeHandleInteraction()
    {
        var wasDragging = _draggingOnionSkinRangeHandle != TimelineOnionSkinRangeHandle.None;
        var wasHovered = _hoveredOnionSkinRangeHandle != TimelineOnionSkinRangeHandle.None;
        if (!wasDragging && !wasHovered) return;

        _draggingOnionSkinRangeHandle = TimelineOnionSkinRangeHandle.None;
        _hoveredOnionSkinRangeHandle = TimelineOnionSkinRangeHandle.None;
        Cursor = Cursors.Default;
        if (wasDragging)
        {
            OnionSkinRangeInteractionCanceled?.Invoke(this, EventArgs.Empty);
            if (Capture) Capture = false;
        }
        Invalidate();
    }

    public void EnsureCurrentFrameVisible()
    {
        if (EnsureCurrentFrameVisibleCore()) Invalidate();
    }

    private bool EnsureCurrentFrameVisibleCore()
    {
        if (!IsHandleCreated && Width <= 0) return false;
        var previousFirstVisibleFrame = _firstVisibleFrame;
        var layout = CreateLayout();
        var capacity = VisibleFrameCapacity(layout);
        var nextFirstVisibleFrame = ResolveCurrentFrameViewportStart(
            _currentFrame,
            _firstVisibleFrame,
            capacity,
            StartFrame,
            MaximumFirstVisibleFrame(layout),
            _isPlaying);
        SetFirstVisibleFrame(nextFirstVisibleFrame, invalidate: false);

        return previousFirstVisibleFrame != _firstVisibleFrame;
    }

    internal static int ResolveCurrentFrameViewportStart(
        int currentFrame,
        int firstVisibleFrame,
        int visibleFrameCapacity,
        int startFrame,
        int maximumFirstVisibleFrame,
        bool playing)
    {
        var minimum = Math.Min(startFrame, maximumFirstVisibleFrame);
        var maximum = Math.Max(startFrame, maximumFirstVisibleFrame);
        var capacity = Math.Max(1, visibleFrameCapacity);
        var first = Math.Clamp(firstVisibleFrame, minimum, maximum);
        long next = first;
        if (currentFrame < first)
        {
            next = currentFrame;
        }
        else if ((long)currentFrame >= (long)first + capacity)
        {
            var leadingColumns = playing ? Math.Max(1, capacity / 4) : capacity - 1;
            next = (long)currentFrame - leadingColumns;
        }

        return (int)Math.Clamp(next, minimum, maximum);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_timeline is not null)
            {
                _timeline.Changed -= HandleTimelineChanged;
                _timeline.TabGroupsChanged -= HandleTabGroupsChanged;
            }
            _heightResizeTimer.Stop();
            _heightResizeTimer.Dispose();
            _frameSelectionAutoScrollTimer.Stop();
            _frameSelectionAutoScrollTimer.Dispose();
            _frameWidthCommitTimer.Stop();
            _frameWidthCommitTimer.Dispose();
            _commandFeedbackTimer.Stop();
            _commandFeedbackTimer.Dispose();
            _layerFeedbackTimer.Stop();
            _layerFeedbackTimer.Dispose();
            _layerDragAnimationTimer.Stop();
            _layerDragAnimationTimer.Dispose();
            _headerIconCache.Dispose();
            _layerContextMenu.Dispose();
            _frameContextMenu.Dispose();
            DisposeTabGroupUi();
            _layerControlToolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        using (var backgroundBrush = new SolidBrush(BackColor))
        {
            FillAlignedRectangle(graphics, backgroundBrush, e.ClipRectangle);
        }
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        var layout = CreateLayout();
        DrawShell(graphics, layout, e.ClipRectangle);
        DrawRuler(graphics, layout, e.ClipRectangle);
        DrawTrackRows(graphics, layout, e.ClipRectangle);
        DrawLayerFeedback(graphics, layout);
        DrawCommandFeedback(graphics, layout);
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
            RefreshTimelineForHeightResize();
            return;
        }
        RefreshOnionSkinControls();
        EnsureCurrentFrameVisible();
        EnsureActiveTrackVisible();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!TabGroupBarBounds().Contains(e.Location)) _focusedTabGroupId = null;
        if (e.Button == MouseButtons.Left && HeightResizeHandleBounds().Contains(e.Location))
        {
            BeginHeightResize();
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            if (TryShowTabGroupContextMenu(e.Location)) return;
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
        if ((ModifierKeys & (Keys.Control | Keys.Shift)) == Keys.None
            && TryBeginFrameTransform(e.Location, layout))
        {
            return;
        }

        if (TryGetTrackIndex(e.Location, layout, out var trackIndex))
        {
            if (e.X < layout.TrackLeft)
            {
                if (_shotFilterActive)
                {
                    if (IsTrackEditableInCurrentPresentation(trackIndex))
                    {
                        SelectLayerTrack(trackIndex, ModifierKeys);
                    }

                    return;
                }

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
                else if (e.X < LockColumnRight)
                {
                    PrepareLayerTrackAction(trackIndex);
                    if (IsTrackLayer(trackIndex)) LayerLockRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (e.X < LayerControlsWidth)
                {
                    PrepareLayerTrackAction(trackIndex);
                    if (IsTrackLayer(trackIndex)) LayerOutlineRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (IsTrackCollapsible(trackIndex)
                    && LayerGroupDisclosureBounds(trackIndex, layout.RowTop + (VisibleTrackPosition(trackIndex) - _firstVisibleTrack) * _rowHeight).Contains(e.Location))
                {
                    SetActiveTrack(trackIndex);
                    ToggleTrackGroupCollapsed(trackIndex);
                }
                else
                {
                    SelectLayerTrack(trackIndex, ModifierKeys);
                    if (IsTrackLayer(trackIndex) && (ModifierKeys & (Keys.Control | Keys.Shift)) == Keys.None)
                    {
                        BeginLayerDrag(trackIndex, e.Y, layout);
                    }
                }
                return;
            }

            SelectLayerTrack(trackIndex, Keys.None);
            BeginFrameSelection(trackIndex, FrameFromX(e.X, layout), ModifierKeys);
            _draggingFrameSelection = true;
            _frameSelectionDragEnd = new TimelineFrameCell(_timeline.Tracks[trackIndex].Id, FrameFromX(e.X, layout));
            _frameSelectionPointer = e.Location;
            Capture = true;
            return;
        }

        if (layout.RulerBounds.Contains(e.Location) && e.X >= layout.TrackLeft)
        {
            ClearSelectionFromEmptyArea();
            _draggingPlayhead = true;
            Capture = true;
            CurrentFrame = FrameFromX(e.X, layout);
            return;
        }

        ClearSelectionFromEmptyArea();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_shotFilterActive) return;
        var layout = CreateLayout();
        if (e.X >= LockColumnRight && e.X < LayerControlsWidth && TryGetTrackIndex(e.Location, layout, out var trackIndex))
        {
            if (!IsTrackLayer(trackIndex)) return;
            PrepareLayerTrackAction(trackIndex);
            LayerColorRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (e.X >= LayerControlsWidth && e.X < layout.TrackLeft && TryGetTrackIndex(e.Location, layout, out trackIndex))
        {
            if (!IsTrackLayer(trackIndex)) return;
            PrepareLayerTrackAction(trackIndex);
            LayerRenameRequested?.Invoke(this, EventArgs.Empty);
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

        var layout = CreateLayout();
        if (_draggingFrameTransform)
        {
            HandleFrameTransformPointerMove(e.Location, layout);
            return;
        }

        if (_draggingFrameSelection)
        {
            HandleFrameSelectionPointerMove(e.Location, layout);
            return;
        }

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
            Cursor = Cursors.SizeWE;
            UpdateOnionSkinRangeDrag(e.X, layout);
            return;
        }

        if (_draggingLayer)
        {
            var pointerChanged = _layerDragPointerY != e.Y;
            _layerDragPointerY = e.Y;
            var targetChanged = false;
            if (TryGetTrackIndex(e.Location, layout, out var trackIndex))
            {
                var placement = LayerDropPlacementFor(trackIndex, e.Y, layout);
                targetChanged = _layerDropTrack != trackIndex || _layerDropPlacement != placement;
                if (targetChanged)
                {
                    _layerDropTrack = trackIndex;
                    _layerDropPlacement = placement;
                    RetargetLayerDragPreview();
                }
            }
            Cursor = Cursors.SizeAll;
            if (pointerChanged || targetChanged) InvalidateLayerDragRows(layout);
            return;
        }

        if (_draggingPlayhead)
        {
            if (e.X < layout.TrackLeft)
            {
                SetFirstVisibleFrame(_firstVisibleFrame - 1);
            }
            else if (e.X >= layout.TrackRight)
            {
                SetFirstVisibleFrame(_firstVisibleFrame + 1);
            }

            CurrentFrame = FrameFromX(e.X, layout);
            return;
        }

        if (HeightResizeHandleBounds().Contains(e.Location))
        {
            Cursor = Cursors.SizeNS;
            return;
        }

        UpdateHover(e.Location, layout);
        UpdateLayerControlToolTip(e.Location, layout);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = Cursors.Default;
        SetLayerControlToolTip("");
        var previousFrame = _hoverFrame;
        var previousTrack = _hoverTrack;
        var previousOnionSkinHandle = _hoveredOnionSkinRangeHandle;
        var previousHeaderCommand = _hoveredHeaderCommand;
        var previousFrameTransformHandle = _hoveredFrameTransformHandle;
        var previousTabGroup = _hoveredTabGroupId;
        if (previousFrame < 0
            && previousTrack < 0
            && previousOnionSkinHandle == TimelineOnionSkinRangeHandle.None
            && previousHeaderCommand == HeaderCommand.None
            && previousFrameTransformHandle == TimelineFrameTransformMode.None
            && previousTabGroup is null)
        {
            return;
        }

        var layout = CreateLayout();
        _hoveredOnionSkinRangeHandle = TimelineOnionSkinRangeHandle.None;
        _hoveredHeaderCommand = HeaderCommand.None;
        _hoverFrame = -1;
        _hoverTrack = -1;
        _hoveredFrameTransformHandle = TimelineFrameTransformMode.None;
        _hoveredTabGroupId = null;
        SetTabGroupToolTip("");
        InvalidateHoverTransition(
            layout,
            previousFrame,
            previousTrack,
            previousOnionSkinHandle,
            previousHeaderCommand);
        if (previousFrameTransformHandle != TimelineFrameTransformMode.None)
        {
            InvalidateFrameSelectionTransformBounds(layout);
        }
        if (previousTabGroup is not null) Invalidate(TabGroupBarBounds());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (TryHandleTabGroupKey(e)) return;
        if (e.Control && e.KeyCode == Keys.A)
        {
            SelectAllVisibleLayerTracks();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Apps || e.Shift && e.KeyCode == Keys.F10)
        {
            if (_shotFilterActive)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            var layout = CreateLayout();
            var activeTrack = GetActiveTrackIndex();
            var visiblePosition = VisibleTrackPosition(activeTrack);
            if (activeTrack >= 0 && visiblePosition >= 0)
            {
                PrepareLayerTrackAction(activeTrack);
                var row = Math.Clamp(visiblePosition - _firstVisibleTrack, 0, Math.Max(0, VisibleTrackCapacity(layout) - 1));
                _layerContextMenu.Show(this, new Point(LayerControlsWidth + 8, layout.RowTop + row * _rowHeight + _rowHeight / 2));
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
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
        if (e.KeyCode == Keys.Escape && _draggingLayer)
        {
            EndMouseDrag(canceled: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Escape && _draggingFrameTransform)
        {
            EndMouseDrag(canceled: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.Shift && e.KeyCode == Keys.F2 && GetActiveTrackIndex() >= 0 && SelectedLayerCount <= 1)
        {
            if (_shotFilterActive)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

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

        var onionSkinHandle = HitTestOnionSkinRangeHandle(location, layout);
        var headerCommand = HeaderCommandAt(location, layout);
        var frameTransformHandle = HitTestFrameSelectionTransformHandle(location, layout);
        var previousTabGroup = _hoveredTabGroupId;
        UpdateTabGroupHover(location, layout);
        if (frame == _hoverFrame
            && track == _hoverTrack
            && onionSkinHandle == _hoveredOnionSkinRangeHandle
            && headerCommand == _hoveredHeaderCommand
            && frameTransformHandle == _hoveredFrameTransformHandle
            && string.Equals(previousTabGroup, _hoveredTabGroupId, StringComparison.Ordinal))
        {
            return;
        }
        var previousFrame = _hoverFrame;
        var previousTrack = _hoverTrack;
        var previousOnionSkinHandle = _hoveredOnionSkinRangeHandle;
        var previousHeaderCommand = _hoveredHeaderCommand;
        var previousFrameTransformHandle = _hoveredFrameTransformHandle;
        _hoverFrame = frame;
        _hoverTrack = track;
        _hoveredOnionSkinRangeHandle = onionSkinHandle;
        _hoveredHeaderCommand = headerCommand;
        _hoveredFrameTransformHandle = frameTransformHandle;
        Cursor = frameTransformHandle != TimelineFrameTransformMode.None
            ? CursorForFrameTransform(frameTransformHandle)
            : onionSkinHandle == TimelineOnionSkinRangeHandle.None
                ? headerCommand == HeaderCommand.None
                    ? _hoveredTabGroupId is null ? Cursors.Default : Cursors.Hand
                    : Cursors.Hand
                : Cursors.SizeWE;
        InvalidateHoverTransition(
            layout,
            previousFrame,
            previousTrack,
            previousOnionSkinHandle,
            previousHeaderCommand);
        if (previousFrameTransformHandle != frameTransformHandle)
        {
            InvalidateFrameSelectionTransformBounds(layout);
        }
    }

    private void InvalidateHoverTransition(
        TimelineLayout layout,
        int previousFrame,
        int previousTrack,
        TimelineOnionSkinRangeHandle previousOnionSkinHandle,
        HeaderCommand previousHeaderCommand)
    {
        InvalidateHoverCell(layout, previousFrame, previousTrack);
        InvalidateHoverCell(layout, _hoverFrame, _hoverTrack);
        InvalidateOnionSkinHandle(layout, previousOnionSkinHandle);
        InvalidateOnionSkinHandle(layout, _hoveredOnionSkinRangeHandle);
        InvalidateHeaderCommand(layout, previousHeaderCommand);
        InvalidateHeaderCommand(layout, _hoveredHeaderCommand);
    }

    private void InvalidateHoverCell(TimelineLayout layout, int frame, int trackIndex)
    {
        var column = frame - _firstVisibleFrame;
        if (frame < 0 || column < 0 || column >= VisibleFrameDrawCount(layout)) return;

        var left = layout.TrackLeft + column * _frameCellWidth;
        Invalidate(new Rectangle(left, HeaderHeight, _frameCellWidth + 1, RulerHeight));
        if (trackIndex < 0) return;

        var visibleRow = VisibleTrackPosition(trackIndex) - _firstVisibleTrack;
        if (visibleRow < 0 || visibleRow >= VisibleTrackCapacity(layout)) return;
        var top = layout.RowTop + visibleRow * _rowHeight;
        Invalidate(new Rectangle(
            left,
            top,
            _frameCellWidth + 1,
            Math.Min(_rowHeight, layout.RowBottom - top)));
    }

    private void InvalidateOnionSkinHandle(
        TimelineLayout layout,
        TimelineOnionSkinRangeHandle handle)
    {
        if (handle == TimelineOnionSkinRangeHandle.None) return;
        Invalidate(Rectangle.Inflate(OnionSkinRangeHandleBounds(handle, layout), 2, 2));
    }

    private void InvalidateHeaderCommand(TimelineLayout layout, HeaderCommand command)
    {
        var bounds = command switch
        {
            HeaderCommand.Solo => SoloButtonBounds(layout),
            HeaderCommand.All => AllButtonBounds(layout),
            HeaderCommand.AddLayer => AddLayerButtonBounds(layout),
            HeaderCommand.AllVisibility => new Rectangle(0, HeaderHeight, VisibilityColumnWidth, RulerHeight),
            HeaderCommand.AllLocks => new Rectangle(VisibilityColumnWidth, HeaderHeight, LockColumnWidth, RulerHeight),
            HeaderCommand.AllOutlines => new Rectangle(LockColumnRight, HeaderHeight, OutlineColumnWidth, RulerHeight),
            _ => Rectangle.Empty
        };
        if (!bounds.IsEmpty) Invalidate(Rectangle.Inflate(bounds, 1, 1));
    }

    private void UpdateLayerControlToolTip(Point location, TimelineLayout layout)
    {
        if (_hoveredTabGroupId is not null) return;
        if (_shotFilterActive)
        {
            SetLayerControlToolTip(
                location.X < layout.TrackLeft && location.Y >= layout.RowTop
                    ? UiLocalization.T("Camera track")
                    : "");
            return;
        }

        var headerCommand = HeaderCommandAt(location, layout);
        if (headerCommand != HeaderCommand.None)
        {
            var headerText = headerCommand switch
            {
                HeaderCommand.AddLayer => "Add layer",
                HeaderCommand.Solo => "Show only the selected layer",
                HeaderCommand.All => "Show all layers",
                HeaderCommand.AllVisibility => AreAllLayersVisible() ? "Hide all layers" : "Show all layers",
                HeaderCommand.AllLocks => AreAllLayersLocked() ? "Unlock all layers" : "Lock all layers",
                HeaderCommand.AllOutlines => AreAllLayersOutlined()
                    ? "Show all layers normally"
                    : "Show all layers as outlines",
                _ => "Layer controls"
            };
            SetLayerControlToolTip(UiLocalization.T(headerText));
            return;
        }

        if (location.X < 0 || location.X >= LayerControlsWidth || location.Y < HeaderHeight)
        {
            SetLayerControlToolTip("");
            return;
        }

        var text = location.X < VisibilityColumnWidth
            ? "Show or hide layer"
            : location.X < LockColumnRight
                ? "Lock or unlock layer"
                : location.Y < layout.RowTop
                    ? "Show all layers as outlines"
                    : "Show layer as outline; double-click to change color";
        SetLayerControlToolTip(UiLocalization.T(text));
    }

    private void SetLayerControlToolTip(string text)
    {
        if (string.Equals(_layerControlToolTipText, text, StringComparison.Ordinal)) return;
        _layerControlToolTipText = text;
        _layerControlToolTip.SetToolTip(this, text);
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

    internal void RefreshTimelineForHeightResize()
    {
        if (_timeline is null) return;
        RefreshTimeline();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (_draggingFrameSelection) UpdateFrameSelectionFromPointer(e.Location);
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
        var (trackDelta, frameDelta) = ResolveWheelScrollDeltas(e.Delta, ModifierKeys);
        if (trackDelta != 0) SetFirstVisibleTrack(_firstVisibleTrack + trackDelta);
        if (frameDelta != 0) SetFirstVisibleFrame(_firstVisibleFrame + frameDelta);
    }

    internal static (int TrackDelta, int FrameDelta) ResolveWheelScrollDeltas(int delta, Keys modifiers)
    {
        if (delta == 0) return (0, 0);
        var notches = Math.Max(1, Math.Abs(delta) / 120) * Math.Sign(delta);
        var movement = -notches * 3;
        return (modifiers & Keys.Shift) != 0 ? (0, movement) : (movement, 0);
    }

    private bool TryHandleHeaderClick(Point point, TimelineLayout layout)
    {
        if (TryHandleTabGroupClick(point)) return true;
        if (_shotFilterActive)
        {
            if (point.Y < HeaderHeight
                && (AddLayerButtonBounds(layout).Contains(point)
                    || SoloButtonBounds(layout).Contains(point)
                    || AllButtonBounds(layout).Contains(point)))
            {
                return true;
            }

            return point.Y >= HeaderHeight
                && point.Y < layout.RowTop
                && point.X < layout.TrackLeft;
        }

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

        if (point.Y >= HeaderHeight && point.Y < layout.RowTop)
        {
            if (point.X < VisibilityColumnWidth)
            {
                AllLayerVisibilityRequested?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (point.X < LockColumnRight)
            {
                AllLayerLocksRequested?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (point.X < LayerControlsWidth)
            {
                AllLayerOutlinesRequested?.Invoke(this, EventArgs.Empty);
                return true;
            }
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

    private void BeginLayerDrag(int trackIndex, int pointerY, TimelineLayout layout)
    {
        ClearLayerDragPreview();
        _draggingLayer = true;
        _draggedLayerTrackId = _timeline.Tracks[trackIndex].Id;
        _layerDropTrack = trackIndex;
        _layerDropPlacement = TimelineLayerDropPlacement.Before;
        _layerDragStartPointerY = pointerY;
        _layerDragPointerY = pointerY;
        _layerDragTrackIds.UnionWith(ResolveLayerMoveTrackIds(trackIndex));
        RetargetLayerDragPreview();
        Capture = true;
        Cursor = Cursors.SizeAll;
        InvalidateLayerDragRows(layout);
    }

    private void RetargetLayerDragPreview()
    {
        _layerDragTargetOffsets.Clear();
        var visibleTracks = VisibleTrackIndices();
        var visibleTrackIds = new string[visibleTracks.Count];
        for (var position = 0; position < visibleTracks.Count; position++)
        {
            var trackId = _timeline.Tracks[visibleTracks[position]].Id;
            visibleTrackIds[position] = trackId;
            _layerDragTargetOffsets[trackId] = 0f;
        }

        _layerDragPreviewValid = _draggingLayer
            && !string.IsNullOrWhiteSpace(_draggedLayerTrackId)
            && _layerDropTrack >= 0
            && _layerDropTrack < TrackCount
            && IsTrackLayer(_layerDropTrack)
            && !_layerDragTrackIds.Contains(_timeline.Tracks[_layerDropTrack].Id);
        if (!_layerDragPreviewValid
            || _layerDropPlacement is TimelineLayerDropPlacement.Inside or TimelineLayerDropPlacement.Mask)
        {
            ScheduleLayerDragOffsetAnimation();
            return;
        }

        var targetTrackId = _timeline.Tracks[_layerDropTrack].Id;
        var targetGroupTrackIds = ResolveLayerMoveTrackIds(_layerDropTrack, includeOwningMask: true);
        var previewOrder = ResolveLayerDragPreviewOrder(
            visibleTrackIds,
            _layerDragTrackIds,
            targetGroupTrackIds,
            targetTrackId,
            _layerDropPlacement);
        var previewPositions = new Dictionary<string, int>(previewOrder.Count, StringComparer.Ordinal);
        for (var position = 0; position < previewOrder.Count; position++)
        {
            previewPositions[previewOrder[position]] = position;
        }

        for (var position = 0; position < visibleTrackIds.Length; position++)
        {
            var trackId = visibleTrackIds[position];
            if (!previewPositions.TryGetValue(trackId, out var previewPosition)) continue;
            _layerDragTargetOffsets[trackId] = (previewPosition - position) * _rowHeight;
        }
        ScheduleLayerDragOffsetAnimation();
    }

    internal static IReadOnlyList<string> ResolveLayerDragPreviewOrder(
        IReadOnlyList<string> visibleTrackIds,
        IReadOnlySet<string> movingTrackIds,
        IReadOnlySet<string> targetGroupTrackIds,
        string targetTrackId,
        TimelineLayerDropPlacement placement)
    {
        var original = visibleTrackIds.ToArray();
        if (placement is not TimelineLayerDropPlacement.Before and not TimelineLayerDropPlacement.After
            || original.Length == 0
            || movingTrackIds.Count == 0
            || movingTrackIds.Contains(targetTrackId))
        {
            return original;
        }

        var moving = original.Where(movingTrackIds.Contains).ToArray();
        if (moving.Length == 0) return original;
        var remaining = original.Where(trackId => !movingTrackIds.Contains(trackId)).ToList();
        var targetPosition = remaining.FindIndex(trackId => string.Equals(trackId, targetTrackId, StringComparison.Ordinal));
        if (targetPosition < 0) return original;

        var insertAt = targetPosition;
        if (placement == TimelineLayerDropPlacement.Before)
        {
            for (var position = 0; position < targetPosition; position++)
            {
                if (!targetGroupTrackIds.Contains(remaining[position])) continue;
                insertAt = position;
                break;
            }
        }
        else
        {
            for (var position = targetPosition; position < remaining.Count; position++)
            {
                if (targetGroupTrackIds.Contains(remaining[position])) insertAt = position + 1;
            }
        }

        remaining.InsertRange(Math.Clamp(insertAt, 0, remaining.Count), moving);
        return remaining;
    }

    internal HashSet<string> ResolveLayerMoveTrackIds(int rootTrackIndex, bool includeOwningMask = false)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (rootTrackIndex < 0 || rootTrackIndex >= TrackCount) return result;
        result.Add(_timeline.Tracks[rootTrackIndex].Id);

        var drawingScene = DrawingScene();
        var rootLayer = GetTrackLayerIndex(rootTrackIndex);
        if (drawingScene is null || rootLayer < 0) return result;

        HashSet<int> movingLayers;
        if (drawingScene.GetLayerKind(rootLayer) != DrawingLayerKind.Folder)
        {
            movingLayers = [rootLayer];
            if (drawingScene.GetLayerKind(rootLayer) == DrawingLayerKind.Mask)
            {
                var rootLayerId = drawingScene.LayerIds[rootLayer];
                for (var layer = 0; layer < drawingScene.LayerCount; layer++)
                {
                    if (layer < drawingScene.LayerMaskIds.Length
                        && string.Equals(drawingScene.LayerMaskIds[layer], rootLayerId, StringComparison.Ordinal))
                    {
                        movingLayers.Add(layer);
                    }
                }
            }
            else if (includeOwningMask && drawingScene.TryGetMaskLayerIndex(rootLayer, out var maskLayer))
            {
                movingLayers.Add(maskLayer);
            }
        }
        else
        {
            var layerById = new Dictionary<string, int>(drawingScene.LayerCount, StringComparer.Ordinal);
            for (var layer = 0; layer < drawingScene.LayerCount; layer++)
            {
                layerById.TryAdd(drawingScene.LayerIds[layer], layer);
            }

            var children = new List<int>?[drawingScene.LayerCount];
            var linkedMasks = new List<int>?[drawingScene.LayerCount];
            for (var layer = 0; layer < drawingScene.LayerCount; layer++)
            {
                if (layer < drawingScene.LayerParentIds.Length
                    && layerById.TryGetValue(drawingScene.LayerParentIds[layer], out var parent))
                {
                    (children[parent] ??= []).Add(layer);
                }
                if (layer >= drawingScene.LayerMaskIds.Length
                    || !layerById.TryGetValue(drawingScene.LayerMaskIds[layer], out var mask))
                {
                    continue;
                }
                (linkedMasks[layer] ??= []).Add(mask);
                (linkedMasks[mask] ??= []).Add(layer);
            }

            movingLayers = [rootLayer];
            var pending = new Queue<int>();
            pending.Enqueue(rootLayer);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (children[current] is { } childLayers)
                {
                    foreach (var child in childLayers)
                    {
                        if (movingLayers.Add(child)) pending.Enqueue(child);
                    }
                }
                if (linkedMasks[current] is { } linkedLayers)
                {
                    foreach (var linkedLayer in linkedLayers)
                    {
                        if (movingLayers.Add(linkedLayer)) pending.Enqueue(linkedLayer);
                    }
                }
            }
        }

        for (var trackIndex = 0; trackIndex < TrackCount; trackIndex++)
        {
            if (movingLayers.Contains(GetTrackLayerIndex(trackIndex))) result.Add(_timeline.Tracks[trackIndex].Id);
        }
        return result;
    }

    private void ScheduleLayerDragOffsetAnimation()
    {
        if (!UiMotion.AnimationsEnabled)
        {
            _layerDragOffsets.Clear();
            foreach (var (trackId, targetOffset) in _layerDragTargetOffsets)
            {
                _layerDragOffsets[trackId] = targetOffset;
            }
            _layerDragAnimationTimer.Stop();
            return;
        }

        var requiresAnimation = false;
        foreach (var (trackId, targetOffset) in _layerDragTargetOffsets)
        {
            var currentOffset = _layerDragOffsets.GetValueOrDefault(trackId);
            _layerDragOffsets.TryAdd(trackId, currentOffset);
            requiresAnimation |= Math.Abs(currentOffset - targetOffset) >= 0.08f;
        }

        if (!requiresAnimation)
        {
            _layerDragAnimationTimer.Stop();
            return;
        }

        _layerDragLastTickMilliseconds = Environment.TickCount64;
        if (!_layerDragAnimationTimer.Enabled) _layerDragAnimationTimer.Start();
    }

    private void TickLayerDragAnimation()
    {
        if (!_draggingLayer || IsDisposed)
        {
            _layerDragAnimationTimer.Stop();
            return;
        }

        if (!UiMotion.AnimationsEnabled)
        {
            ScheduleLayerDragOffsetAnimation();
            InvalidateLayerDragRows(CreateLayout());
            return;
        }

        var now = Environment.TickCount64;
        var elapsedMilliseconds = _layerDragLastTickMilliseconds <= 0
            ? _layerDragAnimationTimer.Interval
            : Math.Clamp(now - _layerDragLastTickMilliseconds, 1L, 64L);
        _layerDragLastTickMilliseconds = now;
        var blend = 1f - MathF.Pow(0.48f, (float)elapsedMilliseconds / _layerDragAnimationTimer.Interval);
        var settled = true;
        foreach (var (trackId, targetOffset) in _layerDragTargetOffsets)
        {
            var currentOffset = _layerDragOffsets.GetValueOrDefault(trackId);
            var nextOffset = currentOffset + (targetOffset - currentOffset) * blend;
            if (Math.Abs(nextOffset - targetOffset) < 0.08f)
            {
                nextOffset = targetOffset;
            }
            else
            {
                settled = false;
            }
            _layerDragOffsets[trackId] = nextOffset;
        }

        InvalidateLayerDragRows(CreateLayout());
        if (settled) _layerDragAnimationTimer.Stop();
    }

    private bool IsLayerDragGhost(int trackIndex)
    {
        if (!_draggingLayer || trackIndex < 0 || trackIndex >= TrackCount) return false;
        var trackId = _timeline.Tracks[trackIndex].Id;
        return _layerDropPlacement == TimelineLayerDropPlacement.Mask
            ? string.Equals(trackId, _draggedLayerTrackId, StringComparison.Ordinal)
            : _layerDragTrackIds.Contains(trackId);
    }

    private int LayerDragOffset(int trackIndex)
    {
        if (!_draggingLayer || trackIndex < 0 || trackIndex >= TrackCount) return 0;
        return (int)Math.Round(_layerDragOffsets.GetValueOrDefault(_timeline.Tracks[trackIndex].Id));
    }

    private void InvalidateLayerDragRows(TimelineLayout layout)
    {
        if (layout.RowBottom <= layout.RowTop || layout.TrackRight <= 0) return;
        Invalidate(Rectangle.FromLTRB(0, layout.RowTop, layout.TrackRight, layout.RowBottom));
    }

    private void ClearLayerDragPreview()
    {
        _layerDragAnimationTimer.Stop();
        _layerDragTrackIds.Clear();
        _layerDragOffsets.Clear();
        _layerDragTargetOffsets.Clear();
        _layerDragLastTickMilliseconds = 0;
        _layerDragPreviewValid = false;
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
                RefreshTimelineForHeightResize();
            }
        }

        if (_draggingOnionSkinRangeHandle != TimelineOnionSkinRangeHandle.None)
        {
            if (canceled) OnionSkinRangeInteractionCanceled?.Invoke(this, EventArgs.Empty);
            else OnionSkinRangeInteractionCompleted?.Invoke(this, EventArgs.Empty);
        }

        var frameTransformOldVisualBounds = Rectangle.Empty;
        if (_draggingFrameTransform)
        {
            var transformLayout = CreateLayout();
            frameTransformOldVisualBounds = UnionFrameTransformBounds(
                transformLayout,
                _frameTransformSourceCells,
                _frameTransformPreviewCells);
        }

        if (_draggingFrameTransform && !canceled) CommitFrameTransform();

        var selectionNotificationPending = _frameSelectionNotificationPending;
        var selectionFramePending = _frameSelectionFramePending;
        _frameSelectionNotificationPending = false;
        _frameSelectionFramePending = false;
        _frameSelectionDragEnd = null;
        _draggingPlayhead = false;
        _draggingFrameSelection = false;
        _frameSelectionAutoScrollTimer.Stop();
        var frameTransformWasActive = _draggingFrameTransform;
        _draggingFrameTransform = false;
        var layerDragWasActive = _draggingLayer;
        _draggingLayer = false;
        _draggingHorizontalScroll = false;
        _draggingVerticalScroll = false;
        _draggingHeightResize = false;
        _draggingOnionSkinRangeHandle = TimelineOnionSkinRangeHandle.None;
        _frameTransformMode = TimelineFrameTransformMode.None;
        _frameTransformChanged = false;
        _frameTransformSourceCells = [];
        _frameTransformVisibleTrackIds = [];
        _frameTransformPairs = [];
        _frameTransformPreviewCells = [];
        _hoveredFrameTransformHandle = TimelineFrameTransformMode.None;
        _draggedLayerTrackId = null;
        _layerDropTrack = -1;
        _layerDropPlacement = TimelineLayerDropPlacement.Before;
        ClearLayerDragPreview();
        if (layerDragWasActive)
        {
            Cursor = Cursors.Default;
            InvalidateLayerDragRows(CreateLayout());
        }
        else if (frameTransformWasActive)
        {
            Cursor = Cursors.Default;
            InvalidateFrameSelectionTransformBounds(CreateLayout(), frameTransformOldVisualBounds);
        }
        if (Capture) Capture = false;
        // Set the document frame before resolving selected content at that frame.
        // Capture loss retains the last preview, just as an ordinary selection drag does.
        if (selectionFramePending) CurrentFrameChanged?.Invoke(this, EventArgs.Empty);
        if (selectionNotificationPending)
        {
            UpdateSelectedTweenFromFrameSelection();
            FrameSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool TryBeginFrameTransform(Point location, TimelineLayout layout)
    {
        var sourceCells = SelectedCells();
        if (sourceCells.Count == 0) return false;

        var handle = HitTestFrameSelectionTransformHandle(location, layout);
        if (handle == TimelineFrameTransformMode.None) return false;

        var visibleTrackIds = VisibleTrackIndices()
            .Select(trackIndex => _timeline.Tracks[trackIndex].Id)
            .ToArray();
        if (!TryResolveFrameSelectionBounds(sourceCells, visibleTrackIds, out var bounds)) return false;

        _draggingFrameTransform = true;
        _frameTransformMode = handle;
        _frameTransformChanged = false;
        _frameTransformSourceCells = sourceCells.ToArray();
        _frameTransformVisibleTrackIds = visibleTrackIds;
        _frameTransformOriginalBounds = bounds;
        _frameTransformPreviewBounds = bounds;
        _frameTransformPointer = location;
        _frameTransformStartFrame = FrameFromX(location.X, layout);
        _frameTransformStartTrackPosition = ResolveFrameTransformTrackPosition(location, layout, visibleTrackIds.Length);
        _frameTransformPairs = ResolveFrameTransformCells(
                _frameTransformSourceCells,
                _frameTransformVisibleTrackIds,
                bounds,
                bounds,
                handle)
            .ToArray();
        _frameTransformPreviewCells = _frameTransformPairs
            .Select(pair => pair.Destination)
            .Distinct()
            .ToArray();
        _hoveredFrameTransformHandle = TimelineFrameTransformMode.None;
        Capture = true;
        Cursor = CursorForFrameTransform(handle);
        InvalidateFrameSelectionTransformBounds(layout);
        return true;
    }

    private void HandleFrameTransformPointerMove(Point location, TimelineLayout layout)
    {
        _frameTransformPointer = location;
        var verticalDelta = ResolveFrameSelectionVerticalScrollDelta(location.Y, layout.GridBounds);
        var horizontalDelta = ResolveFrameTransformHorizontalScrollDelta(location.X, layout.GridBounds);
        if (verticalDelta == 0 && horizontalDelta == 0)
        {
            _frameSelectionAutoScrollTimer.Stop();
        }
        else if (!_frameSelectionAutoScrollTimer.Enabled)
        {
            _frameSelectionAutoScrollTimer.Start();
        }

        UpdateFrameTransformFromPointer(location, layout);
    }

    private bool AutoScrollFrameTransform(Point location)
    {
        var layout = CreateLayout();
        var verticalDelta = ResolveFrameSelectionVerticalScrollDelta(location.Y, layout.GridBounds);
        var horizontalDelta = ResolveFrameTransformHorizontalScrollDelta(location.X, layout.GridBounds);
        if (verticalDelta == 0 && horizontalDelta == 0) return false;

        var previousFirstVisibleTrack = _firstVisibleTrack;
        var previousFirstVisibleFrame = _firstVisibleFrame;
        if (verticalDelta != 0) SetFirstVisibleTrack(_firstVisibleTrack + verticalDelta, invalidate: false);
        if (horizontalDelta != 0) SetFirstVisibleFrame(_firstVisibleFrame + horizontalDelta, invalidate: false);
        if (_firstVisibleTrack == previousFirstVisibleTrack && _firstVisibleFrame == previousFirstVisibleFrame) return false;

        UpdateFrameTransformFromPointer(location, CreateLayout());
        return true;
    }

    internal static int ResolveFrameTransformHorizontalScrollDelta(int pointerX, Rectangle gridBounds)
    {
        if (gridBounds.Width <= 0) return 0;
        if (pointerX < gridBounds.Left) return -1;
        return pointerX >= gridBounds.Right ? 1 : 0;
    }

    private void UpdateFrameTransformFromPointer(Point location, TimelineLayout layout)
    {
        if (!_draggingFrameTransform || _frameTransformVisibleTrackIds.Length == 0) return;

        var pointerFrame = FrameFromX(location.X, layout);
        var pointerTrackPosition = ResolveFrameTransformTrackPosition(
            location,
            layout,
            _frameTransformVisibleTrackIds.Length);
        var targetBounds = ResolveFrameTransformTargetBounds(
            _frameTransformOriginalBounds,
            _frameTransformMode,
            pointerFrame,
            pointerTrackPosition,
            _frameTransformStartFrame,
            _frameTransformStartTrackPosition,
            StartFrame,
            Math.Max(_frameTransformOriginalBounds.LastFrame, MaximumSelectableFrame(layout)),
            0,
            _frameTransformVisibleTrackIds.Length - 1);
        var pairs = ResolveFrameTransformCells(
            _frameTransformSourceCells,
            _frameTransformVisibleTrackIds,
            _frameTransformOriginalBounds,
            targetBounds,
            _frameTransformMode);

        var previousBounds = _frameTransformPreviewBounds;
        _frameTransformPreviewBounds = targetBounds;
        _frameTransformPairs = pairs.ToArray();
        _frameTransformPreviewCells = _frameTransformPairs
            .Select(pair => pair.Destination)
            .Distinct()
            .ToArray();
        _frameTransformChanged = !new HashSet<TimelineFrameCell>(_frameTransformSourceCells)
            .SetEquals(_frameTransformPreviewCells);
        if (previousBounds != targetBounds || _frameTransformChanged) Invalidate();
    }

    private int ResolveFrameTransformTrackPosition(
        Point location,
        TimelineLayout layout,
        int visibleTrackCount)
    {
        if (visibleTrackCount <= 0 || layout.GridBounds.Height <= 0) return -1;
        var y = Math.Clamp(location.Y, layout.GridBounds.Top, layout.GridBounds.Bottom - 1);
        var offset = Math.Clamp((y - layout.RowTop) / _rowHeight, 0, Math.Max(0, VisibleTrackCapacity(layout) - 1));
        return Math.Clamp(_firstVisibleTrack + offset, 0, visibleTrackCount - 1);
    }

    private void CommitFrameTransform()
    {
        if (!_frameTransformChanged || _frameTransformPairs.Length == 0) return;
        FrameTransformRequested?.Invoke(
            this,
            new TimelineFrameTransformRequestedEventArgs(
                _frameTransformMode,
                _frameTransformPairs.ToArray()));
    }

    private TimelineFrameTransformMode HitTestFrameSelectionTransformHandle(
        Point location,
        TimelineLayout layout)
    {
        var selected = SelectedCells();
        if (selected.Count == 0
            || !TryResolveVisibleFrameSelectionBounds(selected, out var selection))
        {
            return TimelineFrameTransformMode.None;
        }

        var bounds = GetFrameSelectionTransformBounds(layout, selection);
        if (bounds.IsEmpty || !layout.GridBounds.IntersectsWith(bounds)) return TimelineFrameTransformMode.None;

        var horizontalResizable = selection.FrameCount > 1;
        var verticalResizable = selection.TrackCount > 1;
        foreach (var candidate in FrameTransformHandleModes)
        {
            var isHorizontal = candidate is TimelineFrameTransformMode.ScaleLeft
                or TimelineFrameTransformMode.ScaleRight
                or TimelineFrameTransformMode.ScaleTopLeft
                or TimelineFrameTransformMode.ScaleTopRight
                or TimelineFrameTransformMode.ScaleBottomRight
                or TimelineFrameTransformMode.ScaleBottomLeft;
            var isVertical = candidate is TimelineFrameTransformMode.ScaleTopLeft
                or TimelineFrameTransformMode.ScaleTopRight
                or TimelineFrameTransformMode.ScaleBottomRight
                or TimelineFrameTransformMode.ScaleBottomLeft;
            if (isHorizontal && !horizontalResizable || isVertical && !verticalResizable) continue;
            if (FrameTransformHandleBounds(bounds, candidate).Contains(location)) return candidate;
        }

        return Rectangle.Inflate(bounds, ScaleTimelineMetric(5), ScaleTimelineMetric(5)).Contains(location)
            ? TimelineFrameTransformMode.Move
            : TimelineFrameTransformMode.None;
    }

    private static bool TryResolveFrameSelectionBounds(
        IReadOnlyList<TimelineFrameCell> cells,
        IReadOnlyList<string> visibleTrackIds,
        out TimelineFrameSelectionBounds bounds)
    {
        bounds = default;
        if (cells.Count == 0 || visibleTrackIds.Count == 0) return false;

        var positions = new Dictionary<string, int>(visibleTrackIds.Count, StringComparer.Ordinal);
        for (var index = 0; index < visibleTrackIds.Count; index++) positions.TryAdd(visibleTrackIds[index], index);

        var hasCell = false;
        var firstFrame = int.MaxValue;
        var lastFrame = int.MinValue;
        var firstTrackPosition = int.MaxValue;
        var lastTrackPosition = int.MinValue;
        foreach (var cell in cells)
        {
            if (!positions.TryGetValue(cell.TrackId, out var trackPosition)) continue;
            hasCell = true;
            firstFrame = Math.Min(firstFrame, cell.Frame);
            lastFrame = Math.Max(lastFrame, cell.Frame);
            firstTrackPosition = Math.Min(firstTrackPosition, trackPosition);
            lastTrackPosition = Math.Max(lastTrackPosition, trackPosition);
        }

        if (!hasCell) return false;
        bounds = new TimelineFrameSelectionBounds(firstFrame, lastFrame, firstTrackPosition, lastTrackPosition);
        return true;
    }

    private bool TryResolveVisibleFrameSelectionBounds(
        IEnumerable<TimelineFrameCell> cells,
        out TimelineFrameSelectionBounds bounds)
    {
        bounds = default;
        EnsureTimelineStructureCache();

        var hasCell = false;
        var firstFrame = int.MaxValue;
        var lastFrame = int.MinValue;
        var firstTrackPosition = int.MaxValue;
        var lastTrackPosition = int.MinValue;
        foreach (var cell in cells)
        {
            if (!_trackIndicesByIdCache.TryGetValue(cell.TrackId, out var trackIndex)
                || (uint)trackIndex >= (uint)_visibleTrackPositionsCache.Length)
            {
                continue;
            }

            var trackPosition = _visibleTrackPositionsCache[trackIndex];
            if (trackPosition < 0 || cell.Frame < StartFrame) continue;
            hasCell = true;
            firstFrame = Math.Min(firstFrame, cell.Frame);
            lastFrame = Math.Max(lastFrame, cell.Frame);
            firstTrackPosition = Math.Min(firstTrackPosition, trackPosition);
            lastTrackPosition = Math.Max(lastTrackPosition, trackPosition);
        }

        if (!hasCell) return false;
        bounds = new TimelineFrameSelectionBounds(firstFrame, lastFrame, firstTrackPosition, lastTrackPosition);
        return true;
    }

    internal static TimelineFrameSelectionBounds ResolveFrameTransformTargetBounds(
        TimelineFrameSelectionBounds original,
        TimelineFrameTransformMode mode,
        int pointerFrame,
        int pointerTrackPosition,
        int moveStartFrame,
        int moveStartTrackPosition,
        int minimumFrame,
        int maximumFrame,
        int minimumTrackPosition,
        int maximumTrackPosition)
    {
        if (mode == TimelineFrameTransformMode.None) return original;

        var firstFrame = original.FirstFrame;
        var lastFrame = original.LastFrame;
        var firstTrackPosition = original.FirstTrackPosition;
        var lastTrackPosition = original.LastTrackPosition;
        var frameLowerBound = Math.Min(minimumFrame, maximumFrame);
        var frameUpperBound = Math.Max(minimumFrame, maximumFrame);
        var trackLowerBound = Math.Min(minimumTrackPosition, maximumTrackPosition);
        var trackUpperBound = Math.Max(minimumTrackPosition, maximumTrackPosition);
        minimumFrame = frameLowerBound;
        maximumFrame = frameUpperBound;
        minimumTrackPosition = trackLowerBound;
        maximumTrackPosition = trackUpperBound;

        if (mode == TimelineFrameTransformMode.Move)
        {
            var maximumFirstFrame = Math.Max(minimumFrame, maximumFrame - original.FrameCount + 1);
            var maximumFirstTrack = Math.Max(minimumTrackPosition, maximumTrackPosition - original.TrackCount + 1);
            firstFrame = ClampInt(
                (long)original.FirstFrame + pointerFrame - moveStartFrame,
                minimumFrame,
                maximumFirstFrame);
            lastFrame = firstFrame + original.FrameCount - 1;
            firstTrackPosition = ClampInt(
                (long)original.FirstTrackPosition + pointerTrackPosition - moveStartTrackPosition,
                minimumTrackPosition,
                maximumFirstTrack);
            lastTrackPosition = firstTrackPosition + original.TrackCount - 1;
            return new TimelineFrameSelectionBounds(firstFrame, lastFrame, firstTrackPosition, lastTrackPosition);
        }

        var hasLeft = mode is TimelineFrameTransformMode.ScaleLeft
            or TimelineFrameTransformMode.ScaleTopLeft
            or TimelineFrameTransformMode.ScaleBottomLeft;
        var hasRight = mode is TimelineFrameTransformMode.ScaleRight
            or TimelineFrameTransformMode.ScaleTopRight
            or TimelineFrameTransformMode.ScaleBottomRight;
        var hasTop = mode is TimelineFrameTransformMode.ScaleTop
            or TimelineFrameTransformMode.ScaleTopLeft
            or TimelineFrameTransformMode.ScaleTopRight;
        var hasBottom = mode is TimelineFrameTransformMode.ScaleBottom
            or TimelineFrameTransformMode.ScaleBottomRight
            or TimelineFrameTransformMode.ScaleBottomLeft;

        if (hasLeft) firstFrame = Math.Clamp(pointerFrame, minimumFrame, lastFrame);
        if (hasRight) lastFrame = Math.Clamp(pointerFrame, firstFrame, maximumFrame);
        if (hasTop) firstTrackPosition = Math.Clamp(pointerTrackPosition, minimumTrackPosition, lastTrackPosition);
        if (hasBottom) lastTrackPosition = Math.Clamp(pointerTrackPosition, firstTrackPosition, maximumTrackPosition);
        return new TimelineFrameSelectionBounds(firstFrame, lastFrame, firstTrackPosition, lastTrackPosition);
    }

    internal static IReadOnlyList<TimelineFrameTransformCell> ResolveFrameTransformCells(
        IReadOnlyList<TimelineFrameCell> sourceCells,
        IReadOnlyList<string> visibleTrackIds,
        TimelineFrameSelectionBounds original,
        TimelineFrameSelectionBounds target,
        TimelineFrameTransformMode mode)
    {
        if (sourceCells.Count == 0 || visibleTrackIds.Count == 0 || mode == TimelineFrameTransformMode.None) return [];

        var positions = new Dictionary<string, int>(visibleTrackIds.Count, StringComparer.Ordinal);
        for (var index = 0; index < visibleTrackIds.Count; index++) positions.TryAdd(visibleTrackIds[index], index);
        var sourceByCoordinate = new Dictionary<(int TrackPosition, int Frame), TimelineFrameCell>();
        foreach (var source in sourceCells)
        {
            if (positions.TryGetValue(source.TrackId, out var trackPosition))
            {
                sourceByCoordinate.TryAdd((trackPosition, source.Frame), source);
            }
        }

        var byDestination = new Dictionary<TimelineFrameCell, TimelineFrameTransformCell>();
        if (mode == TimelineFrameTransformMode.Move)
        {
            var frameOffset = (long)target.FirstFrame - original.FirstFrame;
            var trackOffset = target.FirstTrackPosition - original.FirstTrackPosition;
            foreach (var source in sourceCells)
            {
                if (!positions.TryGetValue(source.TrackId, out var sourceTrackPosition)) continue;
                var destinationTrackPosition = sourceTrackPosition + trackOffset;
                var destinationFrame = (long)source.Frame + frameOffset;
                if ((uint)destinationTrackPosition >= (uint)visibleTrackIds.Count
                    || destinationFrame < 0
                    || destinationFrame > int.MaxValue)
                {
                    continue;
                }

                var destination = new TimelineFrameCell(
                    visibleTrackIds[destinationTrackPosition],
                    (int)destinationFrame);
                byDestination.TryAdd(destination, new TimelineFrameTransformCell(source, destination));
            }
            return byDestination.Values.ToArray();
        }

        for (var trackPosition = target.FirstTrackPosition; trackPosition <= target.LastTrackPosition; trackPosition++)
        {
            var sourceTrackPosition = MapScaledCoordinate(
                trackPosition,
                target.FirstTrackPosition,
                target.LastTrackPosition,
                original.FirstTrackPosition,
                original.LastTrackPosition);
            for (var frame = target.FirstFrame; frame <= target.LastFrame; frame++)
            {
                var sourceFrame = MapScaledCoordinate(
                    frame,
                    target.FirstFrame,
                    target.LastFrame,
                    original.FirstFrame,
                    original.LastFrame);
                if (!sourceByCoordinate.TryGetValue((sourceTrackPosition, sourceFrame), out var source)) continue;
                var destination = new TimelineFrameCell(visibleTrackIds[trackPosition], frame);
                byDestination.TryAdd(destination, new TimelineFrameTransformCell(source, destination));
            }
        }

        foreach (var source in sourceCells)
        {
            if (!positions.TryGetValue(source.TrackId, out var sourceTrackPosition)) continue;
            var destinationTrackPosition = MapScaledCoordinate(
                sourceTrackPosition,
                original.FirstTrackPosition,
                original.LastTrackPosition,
                target.FirstTrackPosition,
                target.LastTrackPosition);
            var destinationFrame = MapScaledCoordinate(
                source.Frame,
                original.FirstFrame,
                original.LastFrame,
                target.FirstFrame,
                target.LastFrame);
            if ((uint)destinationTrackPosition >= (uint)visibleTrackIds.Count) continue;
            var destination = new TimelineFrameCell(visibleTrackIds[destinationTrackPosition], destinationFrame);
            byDestination.TryAdd(destination, new TimelineFrameTransformCell(source, destination));
        }

        return byDestination.Values.ToArray();
    }

    private static int MapScaledCoordinate(
        int coordinate,
        int targetFirst,
        int targetLast,
        int sourceFirst,
        int sourceLast)
    {
        var targetSpan = Math.Max(1L, (long)targetLast - targetFirst);
        var sourceSpan = Math.Max(1L, (long)sourceLast - sourceFirst);
        var targetOffset = Math.Clamp((long)coordinate - targetFirst, 0, targetSpan);
        return ClampInt(
            sourceFirst + (targetOffset * sourceSpan + targetSpan / 2) / targetSpan,
            Math.Min(sourceFirst, sourceLast),
            Math.Max(sourceFirst, sourceLast));
    }

    private static int ClampInt(long value, int minimum, int maximum)
    {
        return (int)Math.Clamp(value, (long)Math.Min(minimum, maximum), (long)Math.Max(minimum, maximum));
    }

    private static Cursor CursorForFrameTransform(TimelineFrameTransformMode mode)
    {
        return mode switch
        {
            TimelineFrameTransformMode.Move => Cursors.SizeAll,
            TimelineFrameTransformMode.ScaleLeft or TimelineFrameTransformMode.ScaleRight => Cursors.SizeWE,
            TimelineFrameTransformMode.ScaleTop or TimelineFrameTransformMode.ScaleBottom => Cursors.SizeNS,
            TimelineFrameTransformMode.ScaleTopLeft or TimelineFrameTransformMode.ScaleBottomRight => Cursors.SizeNWSE,
            TimelineFrameTransformMode.ScaleTopRight or TimelineFrameTransformMode.ScaleBottomLeft => Cursors.SizeNESW,
            _ => Cursors.Default
        };
    }

    private Rectangle FrameTransformHandleBounds(
        Rectangle bounds,
        TimelineFrameTransformMode mode)
    {
        var center = mode switch
        {
            TimelineFrameTransformMode.ScaleTopLeft => new Point(bounds.Left, bounds.Top),
            TimelineFrameTransformMode.ScaleTopRight => new Point(bounds.Right, bounds.Top),
            TimelineFrameTransformMode.ScaleBottomRight => new Point(bounds.Right, bounds.Bottom),
            TimelineFrameTransformMode.ScaleBottomLeft => new Point(bounds.Left, bounds.Bottom),
            TimelineFrameTransformMode.ScaleLeft => new Point(bounds.Left, bounds.Top + bounds.Height / 2),
            TimelineFrameTransformMode.ScaleRight => new Point(bounds.Right, bounds.Top + bounds.Height / 2),
            TimelineFrameTransformMode.ScaleTop => new Point(bounds.Left + bounds.Width / 2, bounds.Top),
            TimelineFrameTransformMode.ScaleBottom => new Point(bounds.Left + bounds.Width / 2, bounds.Bottom),
            _ => Point.Empty
        };
        var size = ScaleTimelineMetric(8);
        var half = size / 2;
        return new Rectangle(center.X - half, center.Y - half, size, size);
    }

    private Rectangle GetFrameSelectionTransformBounds(
        TimelineLayout layout,
        IReadOnlyList<TimelineFrameCell> cells)
    {
        if (!TryResolveVisibleFrameSelectionBounds(cells, out var selection))
        {
            return Rectangle.Empty;
        }

        return GetFrameSelectionTransformBounds(layout, selection);
    }

    private Rectangle GetFrameSelectionTransformBounds(
        TimelineLayout layout,
        TimelineFrameSelectionBounds selection)
    {
        var left = (long)layout.TrackLeft
            + ((long)selection.FirstFrame - _firstVisibleFrame) * _frameCellWidth;
        var right = (long)layout.TrackLeft
            + ((long)selection.LastFrame - _firstVisibleFrame + 1) * _frameCellWidth;
        var top = (long)layout.RowTop
            + ((long)selection.FirstTrackPosition - _firstVisibleTrack) * _rowHeight;
        var bottom = (long)layout.RowTop
            + ((long)selection.LastTrackPosition - _firstVisibleTrack + 1) * _rowHeight;
        return Rectangle.FromLTRB(
            ClampInt(left + 1, int.MinValue, int.MaxValue),
            ClampInt(top + 1, int.MinValue, int.MaxValue),
            ClampInt(right - 1, int.MinValue, int.MaxValue),
            ClampInt(bottom - 1, int.MinValue, int.MaxValue));
    }

    private void InvalidateFrameSelectionTransformBounds(
        TimelineLayout layout,
        Rectangle additionalBounds = default)
    {
        var cells = _draggingFrameTransform && _frameTransformPreviewCells.Length > 0
            ? _frameTransformPreviewCells
            : SelectedCells();
        var bounds = GetFrameSelectionTransformBounds(layout, cells);
        if (!additionalBounds.IsEmpty)
        {
            bounds = bounds.IsEmpty ? additionalBounds : Rectangle.Union(bounds, additionalBounds);
        }
        if (!bounds.IsEmpty) Invalidate(Rectangle.Inflate(bounds, ScaleTimelineMetric(10), ScaleTimelineMetric(10)));
    }

    private Rectangle UnionFrameTransformBounds(
        TimelineLayout layout,
        IReadOnlyList<TimelineFrameCell> sourceCells,
        IReadOnlyList<TimelineFrameCell> previewCells)
    {
        var sourceBounds = GetFrameSelectionTransformBounds(layout, sourceCells);
        var previewBounds = GetFrameSelectionTransformBounds(layout, previewCells);
        return sourceBounds.IsEmpty
            ? previewBounds
            : previewBounds.IsEmpty
                ? sourceBounds
                : Rectangle.Union(sourceBounds, previewBounds);
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
                if (_shotFilterActive) return;
                PrepareLayerTrackAction(trackIndex);
                _layerContextMenu.Show(this, location);
            }
            else
            {
                SelectLayerTrack(trackIndex, Keys.None);
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
        if (_shotFilterActive)
        {
            e.Cancel = true;
            return;
        }

        var hasTrack = GetActiveTrackIndex() >= 0;
        var activeTrack = GetActiveTrackIndex();
        var isLayerTrack = hasTrack && IsTrackLayer(activeTrack);
        if (_shotFilterActive && hasTrack && !IsSceneShotTrack(activeTrack))
        {
            hasTrack = false;
            activeTrack = -1;
            isLayerTrack = false;
        }
        var hasLayerContext = hasTrack && isLayerTrack;
        var selectedLayerCount = SelectedLayerCount;
        var hasSingleLayerSelection = selectedLayerCount == 1;
        var allSelectedLayersLocked = selectedLayerCount > 0 && SelectedLayerTrackIndices().All(IsTrackLocked);
        var allSelectedLayersOutlined = selectedLayerCount > 0 && SelectedLayerTrackIndices().All(IsTrackOutlined);
        var activeVisiblePosition = VisibleTrackPosition(activeTrack);
        var visibleTrackCount = VisibleTrackCount();
        _newDrawingLayerMenuItem.Enabled = hasLayerContext && hasSingleLayerSelection;
        _newFolderLayerMenuItem.Enabled = hasLayerContext && hasSingleLayerSelection && DrawingScene() is not null;
        var activeSceneLayer = hasLayerContext && _sceneDefinition is not null
            ? _sceneDefinition.FindLayer(_timeline.Tracks[activeTrack].TargetId)
            : null;
        var canAddSceneMask = activeSceneLayer is
        {
            Kind: SceneLayerKind.Content,
            MaskLayerId: ""
        };
        _newMaskLayerMenuItem.Enabled = hasLayerContext
            && hasSingleLayerSelection
            && (DrawingScene() is not null || canAddSceneMask);
        var canToggleLayerGroup = hasLayerContext && hasSingleLayerSelection && IsTrackCollapsible(activeTrack);
        _toggleFolderMenuItem.Enabled = canToggleLayerGroup;
        var layerGroupName = IsTrackMaskGroup(activeTrack) ? "Mask" : "Folder";
        _toggleFolderMenuItem.Text = IsTrackCollapsed(activeTrack) ? $"Expand {layerGroupName}" : $"Collapse {layerGroupName}";
        _moveLayerOutOfMaskMenuItem.Enabled = hasLayerContext
            && hasSingleLayerSelection
            && IsTrackMaskedContent(activeTrack);
        _moveLayerUpMenuItem.Enabled = hasLayerContext && hasSingleLayerSelection && activeVisiblePosition > 0;
        _moveLayerDownMenuItem.Enabled = hasLayerContext && hasSingleLayerSelection && activeVisiblePosition >= 0 && activeVisiblePosition < visibleTrackCount - 1;
        _removeLayersMenuItem.Enabled = hasLayerContext && selectedLayerCount > 0;
        _removeLayersMenuItem.Text = selectedLayerCount > 1 ? "Delete Selected Layers" : "Delete Layer";
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
        _outlineLayerMenuItem.Enabled = hasLayerContext && selectedLayerCount > 0;
        _outlineLayerMenuItem.Text = selectedLayerCount > 1
            ? allSelectedLayersOutlined ? "Show Selected Layers Normally" : "Show Selected Layers as Outlines"
            : allSelectedLayersOutlined ? "Show Layer Normally" : "Show Layer as Outline";
        _outlineLayerMenuItem.Checked = hasLayerContext && allSelectedLayersOutlined;
    }

    private void HandleFrameContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var hasTrack = IsTrackEditableInCurrentPresentation(GetActiveTrackIndex());
        var commandCells = CommandCells();
        var hasFrames = commandCells.Count > 0;
        var canCreateClassicTween = hasTrack
            && hasFrames
            && CanCreateTween(commandCells, TimelineTweenKind.Classic);
        var canCreateShapeTween = hasTrack
            && hasFrames
            && CanCreateTween(commandCells, TimelineTweenKind.Shape);
        _copyFramesMenuItem.Enabled = hasTrack && hasFrames;
        _pasteFramesMenuItem.Enabled = hasTrack;
        _reverseFramesMenuItem.Enabled = hasTrack && CanReverseFrameSelection(commandCells);
        _insertFramesMenuItem.Enabled = hasTrack && hasFrames;
        _deleteFramesMenuItem.Enabled = hasTrack && hasFrames;
        _insertKeyframesMenuItem.Enabled = hasTrack && hasFrames;
        _insertBlankKeyframesMenuItem.Enabled = hasTrack && hasFrames;
        _clearKeyframesMenuItem.Enabled = hasTrack && hasFrames;
        _classicTweenMenuItem.Enabled = canCreateClassicTween;
        _shapeTweenMenuItem.Enabled = canCreateShapeTween;
        _removeTweenMenuItem.Enabled = ResolveSingleTweenSelection(_timeline, commandCells) is not null;
    }

    private bool CanCreateTween(
        IReadOnlyList<TimelineFrameCell> cells,
        TimelineTweenKind kind)
    {
        if (cells.Count == 0) return false;

        var trackId = cells[0].TrackId;
        if (cells.Any(cell => !string.Equals(cell.TrackId, trackId, StringComparison.Ordinal))) return false;

        var trackIndex = TrackIndexForId(trackId);
        if (!IsTrackSelectableRow(trackIndex))
        {
            return false;
        }

        var track = _timeline.FindTrack(trackId);
        if (track is null) return false;
        if (!track.TryResolveTweenSpan(
                cells.Min(cell => cell.Frame),
                cells.Max(cell => cell.Frame),
                out var startFrame,
                out var endFrame))
        {
            return false;
        }

        if (_sceneDefinition is not null)
        {
            return _sceneDefinition.CanCreateTimelineTween(
                track.TargetId,
                startFrame,
                endFrame,
                kind,
                out _);
        }

        var drawingScene = DrawingScene();
        if (drawingScene is null
            || !VectorScene.SupportsTimelineTweenLayer(GetTrackLayerKind(trackIndex), kind))
        {
            return false;
        }
        var layer = Array.IndexOf(drawingScene.LayerIds, track.TargetId);
        if ((uint)layer >= drawingScene.LayerCount) return false;
        return _drawingObjectDefinition is not null
            ? _drawingObjectDefinition.CanCreateTimelineTween(
                layer,
                startFrame,
                endFrame,
                kind,
                out _)
            : drawingScene.CanCreateTimelineTween(
                layer,
                startFrame,
                endFrame,
                kind,
                out _);
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
        if (!_draggingLayer
            || !_layerDragPreviewValid
            || string.IsNullOrWhiteSpace(_draggedLayerTrackId)
            || _layerDropTrack < 0)
        {
            return;
        }
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
            _layerDropPlacement,
            ShouldMoveLayerOutOfMaskOnDrag(IsTrackMaskedContent(sourceIndex), _layerDropPlacement)));
    }

    internal static bool ShouldMoveLayerOutOfMaskOnDrag(
        bool isMaskedContent,
        TimelineLayerDropPlacement placement) =>
        isMaskedContent && placement != TimelineLayerDropPlacement.Mask;

    private TimelineLayerDropPlacement LayerDropPlacementFor(int trackIndex, int y, TimelineLayout layout)
    {
        var sourceTrack = string.IsNullOrWhiteSpace(_draggedLayerTrackId) ? -1 : TrackIndexForId(_draggedLayerTrackId);
        var sourceKind = GetTrackLayerKind(sourceTrack);
        var targetKind = GetTrackLayerKind(trackIndex);
        var rowTop = layout.RowTop + (VisibleTrackPosition(trackIndex) - _firstVisibleTrack) * _rowHeight;
        var localY = y - rowTop;
        var withinCenter = localY >= _rowHeight / 4 && localY < _rowHeight * 3 / 4;
        if (DrawingScene() is not null
            && withinCenter
            && ((sourceKind == DrawingLayerKind.Mask && targetKind == DrawingLayerKind.Drawing)
                || (sourceKind == DrawingLayerKind.Drawing && targetKind == DrawingLayerKind.Mask)))
        {
            return TimelineLayerDropPlacement.Mask;
        }

        if (GetTrackLayerKind(trackIndex) != DrawingLayerKind.Folder) return localY < _rowHeight / 2
            ? TimelineLayerDropPlacement.Before
            : TimelineLayerDropPlacement.After;

        if (localY >= _rowHeight / 4 && localY < _rowHeight * 3 / 4) return TimelineLayerDropPlacement.Inside;
        return localY < _rowHeight / 2 ? TimelineLayerDropPlacement.Before : TimelineLayerDropPlacement.After;
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
        var end = new TimelineFrameCell(_timeline.Tracks[trackIndex].Id, frame);
        if (_draggingFrameSelection && _frameSelectionDragEnd == end) return;
        SelectFrameRange(anchor, end);
        if (_draggingFrameSelection)
        {
            _frameSelectionDragEnd = end;
            // Paint both the live range and its playhead before deferred subscribers run.
            if (IsHandleCreated) Update();
        }
    }

    private void HandleFrameSelectionPointerMove(Point location, TimelineLayout layout)
    {
        _frameSelectionPointer = location;
        if (ResolveFrameSelectionVerticalScrollDelta(location.Y, layout.GridBounds) == 0)
        {
            _frameSelectionAutoScrollTimer.Stop();
        }
        else if (!_frameSelectionAutoScrollTimer.Enabled)
        {
            _frameSelectionAutoScrollTimer.Start();
        }

        UpdateFrameSelectionFromPointer(location, layout);
    }

    private void TickFrameSelectionAutoScroll()
    {
        var scrolled = _draggingFrameSelection
            ? AutoScrollFrameSelection(_frameSelectionPointer)
            : _draggingFrameTransform && AutoScrollFrameTransform(_frameTransformPointer);
        if (!scrolled)
        {
            _frameSelectionAutoScrollTimer.Stop();
        }
    }

    internal bool AutoScrollFrameSelection(Point location)
    {
        var layout = CreateLayout();
        var delta = ResolveFrameSelectionVerticalScrollDelta(location.Y, layout.GridBounds);
        if (delta == 0) return false;

        var previousFirstVisibleTrack = _firstVisibleTrack;
        SetFirstVisibleTrack(_firstVisibleTrack + delta, invalidate: false);
        if (_firstVisibleTrack == previousFirstVisibleTrack) return false;

        UpdateFrameSelectionFromPointer(location, layout);
        return true;
    }

    internal static int ResolveFrameSelectionVerticalScrollDelta(int pointerY, Rectangle gridBounds)
    {
        if (gridBounds.Height <= 0) return 0;
        if (pointerY < gridBounds.Top) return -1;
        return pointerY >= gridBounds.Bottom ? 1 : 0;
    }

    internal void UpdateFrameSelectionFromPointer(Point location)
    {
        UpdateFrameSelectionFromPointer(location, CreateLayout());
    }

    private void UpdateFrameSelectionFromPointer(Point location, TimelineLayout layout)
    {
        var visibleTracks = VisibleTrackIndices();
        if (visibleTracks.Count == 0) return;

        var firstVisiblePosition = Math.Clamp(_firstVisibleTrack, 0, visibleTracks.Count - 1);
        var visibleTrackCount = Math.Min(
            VisibleTrackCapacity(layout),
            visibleTracks.Count - firstVisiblePosition);
        var (trackOffset, frameColumn) = ResolveFrameSelectionDragOffset(
            location,
            layout.GridBounds,
            _rowHeight,
            _frameCellWidth,
            visibleTrackCount);
        if (trackOffset < 0 || frameColumn < 0) return;

        var trackIndex = visibleTracks[firstVisiblePosition + trackOffset];
        var frame = (int)Math.Min(
            MaximumSelectableFrame(layout),
            (long)_firstVisibleFrame + frameColumn);
        UpdateFrameSelection(trackIndex, Math.Max(StartFrame, frame));
    }

    internal static (int TrackOffset, int FrameColumn) ResolveFrameSelectionDragOffset(
        Point location,
        Rectangle gridBounds,
        int rowHeight,
        int frameCellWidth,
        int visibleTrackCount)
    {
        if (gridBounds.Width <= 0
            || gridBounds.Height <= 0
            || rowHeight <= 0
            || frameCellWidth <= 0
            || visibleTrackCount <= 0)
        {
            return (-1, -1);
        }

        var x = Math.Clamp(location.X, gridBounds.Left, gridBounds.Right - 1);
        var y = Math.Clamp(location.Y, gridBounds.Top, gridBounds.Bottom - 1);
        var trackOffset = Math.Min((y - gridBounds.Top) / rowHeight, visibleTrackCount - 1);
        var frameColumn = (x - gridBounds.Left) / frameCellWidth;
        return (trackOffset, frameColumn);
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
        var nextSelection = new HashSet<TimelineFrameCell>();
        foreach (var cell in cells)
        {
            if (TrackIndexForId(cell.TrackId) < 0 || cell.Frame < StartFrame) continue;
            nextSelection.Add(cell);
        }

        var selectionChanged = !_selectedFrameCells.SetEquals(nextSelection);
        var anchorChanged = _selectionAnchor != anchor;
        if (!selectionChanged && !anchorChanged) return;

        _selectedFrameCells.Clear();
        _selectedFrameCells.UnionWith(nextSelection);
        _selectionAnchor = anchor;
        NotifyFrameSelectionChanged(selectionChanged);
    }

    private IReadOnlyList<TimelineFrameCell> CommandCells()
    {
        var selected = SelectedCells();
        if (selected.Count > 0) return selected;
        var activeTrack = GetActiveTrackIndex();
        return !IsTrackEditableInCurrentPresentation(activeTrack)
            ? []
            : [new TimelineFrameCell(_timeline.Tracks[activeTrack].Id, CurrentFrame)];
    }

    private IReadOnlyList<TimelineFrameCell> SelectedCells()
    {
        return _selectedFrameCells
            .Where(cell =>
            {
                var trackIndex = TrackIndexForId(cell.TrackId);
                return IsTrackEditableInCurrentPresentation(trackIndex)
                    && VisibleTrackPosition(trackIndex) >= 0
                    && cell.Frame >= StartFrame;
            })
            .OrderBy(cell => TrackIndexForId(cell.TrackId))
            .ThenBy(cell => cell.Frame)
            .ToArray();
    }

    internal static bool CanReverseFrameSelection(IEnumerable<TimelineFrameCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        return cells
            .GroupBy(cell => cell.TrackId, StringComparer.Ordinal)
            .Any(group => group.Select(cell => cell.Frame).Distinct().Count() > 1);
    }

    internal static TimelineTweenSelection? ResolveSingleTweenSelection(
        AnimationTimeline timeline,
        IReadOnlyList<TimelineFrameCell> cells)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (cells.Count == 0) return null;

        TimelineTweenSelection? resolved = null;
        foreach (var cell in cells)
        {
            var tween = timeline.EvaluateTween(cell.TrackId, cell.Frame);
            if (tween is not { } span) return null;
            var candidate = new TimelineTweenSelection(cell.TrackId, span.StartFrame, span.EndFrame);
            if (resolved is { } current && current != candidate) return null;
            resolved = candidate;
        }

        return resolved;
    }

    private TimelineTweenSelection? ResolveLiveTweenSelection(TimelineTweenSelection? candidate)
    {
        if (candidate is not { } selection) return null;
        var track = _timeline?.FindTrack(selection.TrackId);
        return track?.Tweens.Any(tween =>
            tween.StartFrame == selection.StartFrame && tween.EndFrame == selection.EndFrame) == true
                ? selection
                : null;
    }

    private void UpdateSelectedTweenFromFrameSelection(bool forceNotification = false)
    {
        var next = ResolveSingleTweenSelection(_timeline, SelectedCells());
        if (_selectedTween == next && !forceNotification) return;
        _selectedTween = next;
        Invalidate();
        SelectedTweenChanged?.Invoke(this, EventArgs.Empty);
    }

    private int TrackIndexForId(string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return -1;
        EnsureTimelineStructureCache();
        return _trackIndicesByIdCache.GetValueOrDefault(trackId, -1);
    }

    private void NotifyFrameSelectionChanged(bool visualStateChanged = true)
    {
        if (!_draggingFrameSelection) UpdateSelectedTweenFromFrameSelection();
        if (visualStateChanged)
        {
            Invalidate();
        }
        if (_draggingFrameSelection)
        {
            _frameSelectionNotificationPending = true;
            return;
        }
        FrameSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<int> SelectedLayerTrackIndices()
    {
        return VisibleTrackIndices()
            .Where(trackIndex => (_shotFilterActive
                    ? IsTrackSelectableRow(trackIndex)
                    : IsTrackLayer(trackIndex))
                && IsTrackSelected(trackIndex))
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
        if (!IsTrackSelectableRow(trackIndex))
        {
            SetActiveTrack(trackIndex);
            return;
        }

        var trackId = _timeline.Tracks[trackIndex].Id;
        var previousSelection = new HashSet<string>(_selectedLayerTrackIds, StringComparer.Ordinal);
        var layout = CreateLayout();
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
                if (IsTrackSelectableRow(candidate)) _selectedLayerTrackIds.Add(_timeline.Tracks[candidate].Id);
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
        if (shift) Invalidate();
        else InvalidateLayerSelectionChanges(layout, previousSelection);
    }

    private void InvalidateLayerSelectionChanges(TimelineLayout layout, IReadOnlySet<string> previousSelection)
    {
        foreach (var trackId in previousSelection)
        {
            if (_selectedLayerTrackIds.Contains(trackId)) continue;
            InvalidateTrackRow(layout, TrackIndexForId(trackId));
        }

        foreach (var trackId in _selectedLayerTrackIds)
        {
            if (previousSelection.Contains(trackId)) continue;
            InvalidateTrackRow(layout, TrackIndexForId(trackId));
        }
    }

    private void PrepareLayerTrackAction(int trackIndex)
    {
        if (!IsTrackSelected(trackIndex)) SelectLayerTrack(trackIndex, Keys.None);
        else SetActiveTrack(trackIndex);
    }

    internal void SelectAllVisibleLayerTracks()
    {
        _selectedLayerTrackIds.Clear();
        foreach (var trackIndex in VisibleTrackIndices())
        {
            if (_shotFilterActive
                ? IsTrackSelectableRow(trackIndex)
                : IsTrackLayer(trackIndex))
            {
                _selectedLayerTrackIds.Add(_timeline.Tracks[trackIndex].Id);
            }
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
            return trackIndex < 0
                || !IsTrackSelectableRow(trackIndex)
                || VisibleTrackPosition(trackIndex) < 0;
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
        var previousVisibleTrackCount = VisibleTrackCount();
        ExpandAncestorsForTrack(trackIndex);
        var visibleTrackStructureChanged = previousVisibleTrackCount != VisibleTrackCount();
        var layout = CreateLayout();
        var previousTrackIndex = GetActiveTrackIndex();
        var previousTrackId = ActiveTrackId;
        var track = _timeline.Tracks[trackIndex];
        _activeTrackId = track.Id;
        LayoutAutoKeyframeControl(layout);

        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            var layerIndex = GetTrackLayerIndex(trackIndex);
            if (layerIndex >= 0) drawingScene.ActiveLayer = layerIndex;
        }
        else if (_sceneDefinition is not null && !IsSceneShotTrack(trackIndex))
        {
            _sceneDefinition.SetActiveLayer(track.TargetId);
        }

        var previousFirstVisibleTrack = _firstVisibleTrack;
        EnsureActiveTrackVisible();
        if (visibleTrackStructureChanged || previousFirstVisibleTrack != _firstVisibleTrack)
        {
            Invalidate();
        }
        else
        {
            InvalidateTrackRow(layout, previousTrackIndex);
            InvalidateTrackRow(layout, trackIndex);
        }
        if (!string.Equals(previousTrackId, track.Id, StringComparison.Ordinal))
        {
            if (!_suppressActiveLayerChanged) ActiveLayerChanged?.Invoke(this, EventArgs.Empty);
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
            && (_drawingObjectDefinition?.Instances.Any(instance => string.Equals(
                    instance.Id,
                    _timeline.Tracks[rememberedIndex].TargetId,
                    StringComparison.Ordinal)) == true
                || IsSceneLightTrack(rememberedIndex)
                || IsSceneShotTrack(rememberedIndex)))
        {
            return IsTrackActiveInCurrentTabGroup(rememberedIndex) ? rememberedIndex : -1;
        }

        var modelIndex = GetModelActiveTrackIndex();
        if (modelIndex >= 0) return IsTrackActiveInCurrentTabGroup(modelIndex) ? modelIndex : -1;

        return rememberedIndex >= 0 && IsTrackActiveInCurrentTabGroup(rememberedIndex) ? rememberedIndex : -1;
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
            var layerIndex = GetTrackLayerIndex(trackIndex);
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
            var light = _sceneDefinition.FindLight(targetId);
            if (light is not null) return light.Name;
            var shot = _sceneDefinition.FindShot(targetId);
            if (shot is not null) return shot.Name;
        }

        return $"Track {trackIndex + 1}";
    }

    private int GetTrackLayerIndex(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return -1;
        EnsureTimelineStructureCache();
        return trackIndex < _trackLayerIndicesCache.Length ? _trackLayerIndicesCache[trackIndex] : -1;
    }

    private DrawingLayerKind GetTrackLayerKind(int trackIndex)
    {
        var drawingScene = DrawingScene();
        var layer = GetTrackLayerIndex(trackIndex);
        if (drawingScene is not null && layer >= 0) return drawingScene.GetLayerKind(layer);
        if (_sceneDefinition is not null
            && trackIndex >= 0
            && trackIndex < TrackCount
            && _sceneDefinition.FindLayer(_timeline.Tracks[trackIndex].TargetId)?.Kind == SceneLayerKind.Mask)
        {
            return DrawingLayerKind.Mask;
        }
        return DrawingLayerKind.Drawing;
    }

    private bool IsTrackMaskedContent(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        var targetId = _timeline.Tracks[trackIndex].TargetId;
        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            var layer = GetTrackLayerIndex(trackIndex);
            return layer >= 0 && drawingScene.TryGetMaskLayerIndex(layer, out _);
        }

        if (_sceneDefinition?.FindLayer(targetId) is not { Kind: SceneLayerKind.Content } contentLayer
            || string.IsNullOrWhiteSpace(contentLayer.MaskLayerId))
        {
            return false;
        }

        return _sceneDefinition.FindLayer(contentLayer.MaskLayerId)?.Kind == SceneLayerKind.Mask;
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
        if (_shotFilterActive) return ScaleTimelineMetric(30);
        return LayerControlsWidth + 23 + GetTrackDisplayDepth(trackIndex) * 12;
    }

    private List<int> VisibleTrackIndices()
    {
        EnsureTimelineStructureCache();
        return _visibleTrackIndicesCache;
    }

    private void EnsureTimelineStructureCache()
    {
        if (!_timelineStructureCacheDirty) return;
        _timelineStructureCacheDirty = false;
        _visibleTrackIndicesCache.Clear();
        _trackIndicesByIdCache.Clear();
        _visibleTrackPositionsCache = new int[TrackCount];
        _trackLayerIndicesCache = new int[TrackCount];
        Array.Fill(_visibleTrackPositionsCache, -1);
        Array.Fill(_trackLayerIndicesCache, -1);

        for (var trackIndex = 0; trackIndex < TrackCount; trackIndex++)
        {
            _trackIndicesByIdCache.TryAdd(_timeline.Tracks[trackIndex].Id, trackIndex);
        }

        var drawingScene = DrawingScene();
        if (drawingScene is null)
        {
            for (var trackIndex = 0; trackIndex < TrackCount; trackIndex++)
            {
                if ((_shotFilterActive || IsTrackInActiveTabGroup(trackIndex))
                    && IsTrackVisibleInActiveShot(trackIndex))
                {
                    _visibleTrackIndicesCache.Add(trackIndex);
                }
            }

            UpdateVisibleTrackPositions();
            return;
        }

        var layerIndexById = new Dictionary<string, int>(drawingScene.LayerCount, StringComparer.Ordinal);
        var trackIndexByLayer = new int[drawingScene.LayerCount];
        Array.Fill(trackIndexByLayer, -1);
        for (var layer = 0; layer < drawingScene.LayerCount; layer++)
        {
            layerIndexById.TryAdd(drawingScene.LayerIds[layer], layer);
        }
        for (var trackIndex = 0; trackIndex < TrackCount; trackIndex++)
        {
            var targetId = _timeline.Tracks[trackIndex].TargetId;
            if (!layerIndexById.TryGetValue(targetId, out var layer)) continue;
            _trackLayerIndicesCache[trackIndex] = layer;
            if (trackIndexByLayer[layer] < 0) trackIndexByLayer[layer] = trackIndex;
        }

        var matchingLayers = new HashSet<int>();
        for (var layer = 0; layer < drawingScene.LayerCount; layer++)
        {
            var trackIndex = trackIndexByLayer[layer];
            if (trackIndex >= 0 && IsTrackInActiveTabGroup(trackIndex)) matchingLayers.Add(layer);
        }

        var contextLayers = new HashSet<int>(matchingLayers);
        foreach (var layer in matchingLayers)
        {
            var parent = drawingScene.GetLayerParentIndex(layer);
            for (var depth = 0; parent >= 0 && depth < drawingScene.LayerCount; depth++)
            {
                contextLayers.Add(parent);
                parent = drawingScene.GetLayerParentIndex(parent);
            }

            if (drawingScene.TryGetMaskLayerIndex(layer, out var maskLayer)) contextLayers.Add(maskLayer);
        }

        var displayedTracks = new bool[TrackCount];
        foreach (var layer in drawingScene.GetLayerDisplayOrder())
        {
            var trackIndex = trackIndexByLayer[layer];
            if (trackIndex < 0
                || !contextLayers.Contains(layer)
                || IsLayerHiddenByCollapsedGroup(drawingScene, layer))
            {
                continue;
            }

            _visibleTrackIndicesCache.Add(trackIndex);
            displayedTracks[trackIndex] = true;
        }

        for (var trackIndex = 0; trackIndex < TrackCount; trackIndex++)
        {
            if (displayedTracks[trackIndex]) continue;
            var layer = _trackLayerIndicesCache[trackIndex];
            if (layer >= 0 && IsLayerHiddenByCollapsedGroup(drawingScene, layer)) continue;
            if (layer < 0
                && IsTrackInActiveTabGroup(trackIndex)
                && IsTrackVisibleInActiveShot(trackIndex))
            {
                _visibleTrackIndicesCache.Add(trackIndex);
            }
        }
        UpdateVisibleTrackPositions();
    }

    private void UpdateVisibleTrackPositions()
    {
        for (var position = 0; position < _visibleTrackIndicesCache.Count; position++)
        {
            _visibleTrackPositionsCache[_visibleTrackIndicesCache[position]] = position;
        }
    }

    private void InvalidateTimelineStructureCache()
    {
        _timelineStructureCacheDirty = true;
    }

    private int VisibleTrackCount() => VisibleTrackIndices().Count;

    private int VisibleTrackPosition(int trackIndex)
    {
        if (trackIndex < 0) return -1;
        EnsureTimelineStructureCache();
        return trackIndex < _visibleTrackPositionsCache.Length ? _visibleTrackPositionsCache[trackIndex] : -1;
    }

    private bool IsLayerHiddenByCollapsedGroup(VectorScene drawingScene, int layer)
    {
        var parent = drawingScene.GetLayerParentIndex(layer);
        for (var depth = 0; parent >= 0 && depth < drawingScene.LayerCount; depth++)
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

        if (changed) InvalidateTimelineStructureCache();
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
            InvalidateTimelineStructureCache();
            var activeTrack = GetActiveTrackIndex();
            var activeLayer = GetTrackLayerIndex(activeTrack);
            if (activeLayer >= 0 && IsLayerHiddenByCollapsedGroup(drawingScene, activeLayer)) SetActiveTrack(trackIndex);
        }
        else
        {
            InvalidateTimelineStructureCache();
        }

        RemoveCollapsedFrameSelection();
        PruneLayerSelection();
        SetFirstVisibleTrack(_firstVisibleTrack, invalidate: false);
        EnsureActiveTrackVisible();
        _knownLayerVisuals = CaptureLayerVisualSnapshots();
        Invalidate();
    }

    private void PruneCollapsedLayerGroupIds()
    {
        var drawingScene = DrawingScene();
        if (drawingScene is null)
        {
            var changed = _collapsedFolderLayerIds.Count > 0 || _collapsedMaskLayerIds.Count > 0;
            _collapsedFolderLayerIds.Clear();
            _collapsedMaskLayerIds.Clear();
            if (changed) InvalidateTimelineStructureCache();
            return;
        }

        var removed = _collapsedFolderLayerIds.RemoveWhere(id =>
        {
            var layer = Array.IndexOf(drawingScene.LayerIds, id);
            return layer < 0 || drawingScene.GetLayerKind(layer) != DrawingLayerKind.Folder;
        });
        removed += _collapsedMaskLayerIds.RemoveWhere(id =>
        {
            var layer = Array.IndexOf(drawingScene.LayerIds, id);
            return layer < 0
                || drawingScene.GetLayerKind(layer) != DrawingLayerKind.Mask
                || drawingScene.GetMaskContentLayerIndex(layer) < 0;
        });
        if (removed > 0) InvalidateTimelineStructureCache();
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
        return new Rectangle(labelLeft - 13, rowTop + (_rowHeight - 14) / 2, 12, 14);
    }

    private static void DrawLayerGroupDisclosure(Graphics graphics, Rectangle bounds, bool collapsed)
    {
        var backgroundColor = Theme.PanelStrong;
        using var background = new SolidBrush(backgroundColor);
        using var outline = new Pen(Theme.ReadableUiColor(backgroundColor, Theme.Border));
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
        using var fill = new SolidBrush(Theme.ReadableUiColor(backgroundColor, Theme.Accent));
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

    private void DrawLayerHierarchyGuide(Graphics graphics, int depth, int y, int labelLeft, Color background)
    {
        if (depth <= 0) return;
        using var guide = new Pen(Theme.ReadableUiColor(background, Theme.Muted), 1f);
        var x = labelLeft - 6;
        graphics.DrawLine(guide, x, y, x, y + _rowHeight / 2);
        graphics.DrawLine(guide, x, y + _rowHeight / 2, x + 5, y + _rowHeight / 2);
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
            using var fill = new SolidBrush(ThemeNeutral(Color.FromArgb(202, 224, 224, 224), Theme.Text));
            graphics.FillEllipse(fill, centerX - 1, centerY + 1, 2, 2);
        }
    }

    private void DrawTrackKindGlyph(Graphics graphics, int trackIndex, int x, int centerY, Color background)
    {
        if (IsSceneShotTrack(trackIndex))
        {
            SvgIcons.Draw(
                graphics,
                SvgIconKind.Camera,
                new Rectangle(x - 1, centerY - 7, 14, 14),
                Theme.ReadableUiColor(background, Theme.Mix(Theme.Text, GetTrackColor(trackIndex), 0.6f)));
            return;
        }

        if (_sceneDefinition is not null
            && trackIndex >= 0
            && trackIndex < TrackCount
            && _sceneDefinition.FindLight(_timeline.Tracks[trackIndex].TargetId) is { } light)
        {
            var icon = light.Kind switch
            {
                SceneLightKind.Directional => SvgIconKind.DirectionalLight,
                SceneLightKind.Point => SvgIconKind.PointLight,
                SceneLightKind.Area => SvgIconKind.AreaLight,
                _ => SvgIconKind.AmbientLight
            };
            SvgIcons.Draw(
                graphics,
                icon,
                new Rectangle(x - 1, centerY - 7, 14, 14),
                Theme.ReadableUiColor(background, Theme.Mix(Theme.Text, GetTrackColor(trackIndex), 0.6f)));
            return;
        }

        DrawLayerKindGlyph(graphics, GetTrackLayerKind(trackIndex), x, centerY, background);
    }

    private static void DrawLayerKindGlyph(Graphics graphics, DrawingLayerKind kind, int x, int centerY, Color background)
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
                var maskColor = Theme.ReadableUiColor(background, Color.FromArgb(230, 130, 205, 208));
                using (var outline = new Pen(maskColor, 1.2f))
                using (var center = new SolidBrush(maskColor))
                {
                    graphics.DrawEllipse(outline, bounds);
                    graphics.FillEllipse(center, x + 4, centerY - 1, 3, 3);
                }
                break;
            default:
                using (var fill = new SolidBrush(Theme.ReadableUiColor(background, Theme.Text)))
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
            var layerIndex = GetTrackLayerIndex(trackIndex);
            if (layerIndex >= 0) return drawingScene.IsLayerEffectivelyVisible(layerIndex);
        }

        if (_drawingObjectDefinition is not null)
        {
            return _drawingObjectDefinition.Instances
                .FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal))?.Visible == true;
        }

        if (_sceneDefinition is not null)
        {
            if (_sceneDefinition.FindShot(targetId) is not null) return true;
            if (_sceneDefinition.FindLight(targetId) is { } light)
            {
                return _sceneDefinition.TryEvaluateLightSettings(light.Id, _currentFrame, out var settings)
                    && settings.Enabled;
            }
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
            var layerIndex = GetTrackLayerIndex(trackIndex);
            if (layerIndex >= 0) return drawingScene.GetLayerColor(layerIndex);
        }

        if (_sceneDefinition?.FindLayer(targetId) is { } sceneLayer) return Color.FromArgb(sceneLayer.ColorArgb);
        if (_sceneDefinition?.FindLight(targetId) is { } light)
            return Color.FromArgb(
                _sceneDefinition.TryEvaluateLightSettings(light.Id, _currentFrame, out var settings)
                    ? settings.ColorArgb
                    : light.Settings.ColorArgb);
        if (_sceneDefinition?.FindShot(targetId) is { } shot) return Color.FromArgb(shot.ColorArgb);
        return Theme.Muted;
    }

    internal static Color LayerItemBackgroundColor(Color layerColor, bool active, bool selected, bool alternate)
    {
        var neutral = active
            ? Theme.AccentSurface
            : selected
                ? Theme.Mix(Theme.Panel, Theme.AccentSurface, 0.58f)
                : alternate
                    ? Theme.Mix(Theme.Panel, Theme.PanelStrong, 0.34f)
                    : Theme.Panel;
        return Theme.Mix(neutral, Color.FromArgb(255, layerColor.R, layerColor.G, layerColor.B), active ? 0.24f : 0.31f);
    }

    private bool IsTrackOutlined(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        var targetId = _timeline.Tracks[trackIndex].TargetId;
        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            var layerIndex = GetTrackLayerIndex(trackIndex);
            return layerIndex >= 0 && drawingScene.IsLayerEffectivelyOutlined(layerIndex);
        }

        return _sceneDefinition?.FindLayer(targetId)?.Outline == true;
    }

    private bool IsTrackLayer(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        var targetId = _timeline.Tracks[trackIndex].TargetId;
        var drawingScene = DrawingScene();
        if (drawingScene is not null) return GetTrackLayerIndex(trackIndex) >= 0;
        return _sceneDefinition?.FindLayer(targetId) is not null;
    }

    private bool IsSceneLightTrack(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount || _sceneDefinition is null) return false;
        return _sceneDefinition.FindLight(_timeline.Tracks[trackIndex].TargetId) is not null;
    }

    /// <summary>Shot columns carry the shot's framed viewport and behave like light columns.</summary>
    private bool IsSceneShotTrack(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount || _sceneDefinition is null) return false;
        return _sceneDefinition.FindShot(_timeline.Tracks[trackIndex].TargetId) is not null;
    }

    private bool IsTrackSelectableRow(int trackIndex) =>
        IsTrackLayer(trackIndex) || IsSceneLightTrack(trackIndex) || IsSceneShotTrack(trackIndex);

    private bool IsTrackExplicitlyVisible(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        var targetId = _timeline.Tracks[trackIndex].TargetId;
        // Shot columns have no visibility toggle; they are always presented while shown.
        if (IsSceneShotTrack(trackIndex)) return true;

        var drawingScene = DrawingScene();
        var layerIndex = drawingScene is null ? -1 : GetTrackLayerIndex(trackIndex);
        if (drawingScene is not null && layerIndex >= 0)
        {
            return drawingScene.LayerVisible[layerIndex];
        }

        if (_drawingObjectDefinition is not null)
        {
            return _drawingObjectDefinition.Instances
                .FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal))?.Visible == true;
        }

        if (_sceneDefinition?.FindLight(targetId) is { } light)
            return _sceneDefinition.TryEvaluateLightSettings(light.Id, _currentFrame, out var settings)
                && settings.Enabled;
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
        if (IsSceneShotTrack(trackIndex) || IsSceneLightTrack(trackIndex)) return false;
        var targetId = _timeline.Tracks[trackIndex].TargetId;
        var drawingScene = DrawingScene();
        var layerIndex = drawingScene is null ? -1 : GetTrackLayerIndex(trackIndex);
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
        if (trackIndex < 0
            || trackIndex >= TrackCount
            || IsSceneLightTrack(trackIndex)
            || IsSceneShotTrack(trackIndex))
        {
            return;
        }

        var targetId = _timeline.Tracks[trackIndex].TargetId;

        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            drawingScene.SoloLayer(GetTrackLayerIndex(trackIndex));
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

    private void HandleTimelineChanged(object? sender, EventArgs e)
    {
        if (TimelineTrackStructureChanged())
        {
            InvalidateTimelineStructureCache();
            PruneCollapsedLayerGroupIds();
            DetectLayerCollectionFeedback();
            CaptureKnownTrackStructure();
        }
        UpdateFrameBoundsForTimelineChange();
        RefreshOnionSkinControls();
        EnsureActiveTrackVisible();
        EnsureCurrentFrameVisible();
        UpdateSelectedTweenFromFrameSelection(forceNotification: _selectedTween is not null);
        Invalidate();
    }

    private bool TimelineTrackStructureChanged()
    {
        if (_knownTrackStructureIds.Length != TrackCount) return true;
        for (var trackIndex = 0; trackIndex < TrackCount; trackIndex++)
        {
            if (!string.Equals(
                    _knownTrackStructureIds[trackIndex],
                    _timeline.Tracks[trackIndex].Id,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private void CaptureKnownTrackStructure()
    {
        _knownTrackStructureIds = _timeline.Tracks.Select(track => track.Id).ToArray();
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

        _selectedFrameCells.RemoveWhere(cell => TrackIndexForId(cell.TrackId) < 0 || cell.Frame < _startFrame);
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
        var maxFirst = MaximumFirstVisibleFrame(layout);
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
        var visibleRow = (point.Y - layout.RowTop) / _rowHeight;
        var visibleTracks = VisibleTrackIndices();
        var visiblePosition = _firstVisibleTrack + visibleRow;
        if (visiblePosition < 0 || visiblePosition >= visibleTracks.Count) return false;
        trackIndex = visibleTracks[visiblePosition];
        return true;
    }

    private int FrameFromX(int x, TimelineLayout layout)
    {
        var column = (Math.Clamp(x, layout.TrackLeft, Math.Max(layout.TrackLeft, layout.TrackRight - 1)) - layout.TrackLeft) / _frameCellWidth;
        var frame = Math.Min((long)_firstVisibleFrame + column, MaximumSelectableFrame(layout));
        return Math.Max(StartFrame, (int)frame);
    }

    private int VisibleFrameCapacity(TimelineLayout layout)
    {
        return Math.Max(1, (layout.TrackRight - layout.TrackLeft) / _frameCellWidth);
    }

    private int VisibleFrameDrawCount(TimelineLayout layout)
    {
        return Math.Max(1, (int)Math.Ceiling((layout.TrackRight - layout.TrackLeft) / (double)_frameCellWidth));
    }

    private (int FirstColumn, int LastColumnExclusive) VisibleFramePaintRange(
        TimelineLayout layout,
        Rectangle clipBounds,
        bool includeLeadingColumn = false)
    {
        var columnCount = VisibleFrameDrawCount(layout);
        if (clipBounds.Right <= layout.TrackLeft || clipBounds.Left >= layout.TrackRight)
        {
            return (0, 0);
        }

        var first = Math.Clamp((clipBounds.Left - layout.TrackLeft) / _frameCellWidth, 0, columnCount);
        if (includeLeadingColumn && first > 0) first--;
        var last = Math.Clamp(
            (int)Math.Ceiling((clipBounds.Right - layout.TrackLeft) / (double)_frameCellWidth),
            first,
            columnCount);
        return (first, last);
    }

    private void InvalidateFrameTransition(int previousFrame, int nextFrame)
    {
        var layout = CreateLayout();
        var headerBounds = HeaderFrameStatusBounds(layout);
        if (headerBounds.Width > 0 && headerBounds.Height > 0) Invalidate(headerBounds);
        InvalidateRulerFrameColumn(layout, previousFrame);
        InvalidateRulerFrameColumn(layout, nextFrame);
        InvalidateOnionSkinRangeTransition(layout, previousFrame, nextFrame);
        InvalidateFrameColumn(layout, previousFrame);
        InvalidateFrameColumn(layout, nextFrame);
    }

    private void InvalidateRulerFrameColumn(TimelineLayout layout, int frame)
    {
        var column = frame - _firstVisibleFrame;
        var columnCount = VisibleFrameDrawCount(layout);
        if (column < -1 || column > columnCount) return;

        var margin = Math.Max(2, ScaleTimelineMetric(5));
        var left = (int)Math.Clamp(
            (long)layout.TrackLeft + ((long)column - 1) * _frameCellWidth - margin,
            layout.TrackLeft,
            layout.TrackRight);
        var right = (int)Math.Clamp(
            (long)layout.TrackLeft + ((long)column + 2) * _frameCellWidth + margin,
            layout.TrackLeft,
            layout.TrackRight);
        if (right > left) Invalidate(Rectangle.FromLTRB(left, HeaderHeight, right, layout.RowTop));
    }

    private void InvalidateOnionSkinRangeTransition(
        TimelineLayout layout,
        int previousFrame,
        int nextFrame)
    {
        if (!TryGetOnionSkinRange(out var previousFrames, out var nextFrames, out var enabled) || !enabled)
        {
            return;
        }

        var firstFrame = Math.Min(
            Math.Max(StartFrame, previousFrame - previousFrames),
            Math.Max(StartFrame, nextFrame - previousFrames));
        var lastFrame = Math.Max(
            Math.Min(EndFrame, previousFrame + nextFrames),
            Math.Min(EndFrame, nextFrame + nextFrames));
        var margin = Math.Max(2, ScaleTimelineMetric(7));
        var left = (int)Math.Clamp(
            (long)layout.TrackLeft + ((long)firstFrame - _firstVisibleFrame) * _frameCellWidth - margin,
            layout.TrackLeft,
            layout.TrackRight);
        var right = (int)Math.Clamp(
            (long)layout.TrackLeft + ((long)lastFrame - _firstVisibleFrame + 1) * _frameCellWidth + margin,
            layout.TrackLeft,
            layout.TrackRight);
        if (right > left) Invalidate(Rectangle.FromLTRB(left, HeaderHeight, right, layout.RowTop));
    }

    private void InvalidateFrameColumn(TimelineLayout layout, int frame)
    {
        var column = frame - _firstVisibleFrame;
        if (column < 0 || column >= VisibleFrameDrawCount(layout)) return;
        var margin = Math.Max(1, ScaleTimelineMetric(3));
        var left = Math.Max(layout.TrackLeft, layout.TrackLeft + column * _frameCellWidth - margin);
        var right = Math.Min(layout.TrackRight, layout.TrackLeft + (column + 1) * _frameCellWidth + margin);
        Invalidate(Rectangle.FromLTRB(left, layout.RowTop, right, layout.RowBottom));
    }

    private void InvalidateTrackRow(TimelineLayout layout, int trackIndex)
    {
        var visiblePosition = VisibleTrackPosition(trackIndex);
        var visibleRow = visiblePosition - _firstVisibleTrack;
        if (visibleRow < 0 || visibleRow >= VisibleTrackCapacity(layout)) return;
        var top = layout.RowTop + visibleRow * _rowHeight;
        Invalidate(new Rectangle(0, top, layout.TrackRight, Math.Min(_rowHeight, layout.RowBottom - top)));
    }

    private int MaximumSelectableFrame(TimelineLayout layout)
    {
        return (int)Math.Min(int.MaxValue, (long)EndFrame + VisibleFrameDrawCount(layout));
    }

    private int MaximumFirstVisibleFrame(TimelineLayout layout)
    {
        return Math.Max(StartFrame, MaximumSelectableFrame(layout) - VisibleFrameDrawCount(layout) + 1);
    }

    private int VisibleTrackCapacity(TimelineLayout layout)
    {
        return Math.Max(1, (layout.RowBottom - layout.RowTop) / _rowHeight);
    }

    private TimelineLayout CreateLayout()
    {
        var trackLeft = Math.Min(PreferredGutterWidth, Math.Max(MinimumGutterWidth, Width - _frameCellWidth * 5));
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
        var visibleFrames = VisibleFrameDrawCount(layout);
        var maxValue = Math.Max(0, MaximumFirstVisibleFrame(layout) - StartFrame);
        var total = Math.Max(1L, (long)maxValue + visibleFrames);
        var thumbWidth = maxValue == 0
            ? track.Width
            : Math.Clamp((int)Math.Round(track.Width * Math.Min(1d, visibleFrames / (double)total)), 24, Math.Max(24, track.Width));
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

    private HeaderCommand HeaderCommandAt(Point point, TimelineLayout layout)
    {
        if (_shotFilterActive) return HeaderCommand.None;
        if (AddLayerButtonBounds(layout).Contains(point)) return HeaderCommand.AddLayer;
        if (SoloButtonBounds(layout).Contains(point)) return HeaderCommand.Solo;
        if (AllButtonBounds(layout).Contains(point)) return HeaderCommand.All;
        if (point.Y >= HeaderHeight && point.Y < layout.RowTop)
        {
            if (point.X < VisibilityColumnWidth) return HeaderCommand.AllVisibility;
            if (point.X < LockColumnRight) return HeaderCommand.AllLocks;
            if (point.X < LayerControlsWidth) return HeaderCommand.AllOutlines;
        }

        return HeaderCommand.None;
    }

    private bool IsOnionSkinControlsAvailable()
    {
        if (_shotFilterActive) return false;
        // Drawing timelines expose onion skin through their own scene; the scene
        // composition timeline carries the equivalent state on the scene definition.
        return DrawingScene() is not null || _sceneDefinition is not null;
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



    private static ModernSlider CreateFrameWidthSlider()
    {
        return new ModernSlider
        {
            Minimum = MinimumFrameCellWidth,
            Maximum = MaximumFrameCellWidth,
            SmallChange = 1,
            LargeChange = 4,
            TickFrequency = 4,
            Value = FrameCellWidth,
            Height = Theme.ControlHeightCompact,
            AccessibleName = "Timeline frame width"
        };
    }

    private static ModernNumericUpDown CreateFrameWidthInput()
    {
        var input = new ModernNumericUpDown
        {
            Minimum = MinimumFrameCellWidth,
            Maximum = MaximumFrameCellWidth,
            DecimalPlaces = 0,
            Increment = 1,
            Value = FrameCellWidth,
            Suffix = " px",
            Height = Theme.ControlHeightCompact,
            AccessibleName = "Timeline frame width in pixels"
        };
        Theme.StyleNumeric(input);
        return input;
    }

    private static ComboBox CreateFrameHeightInput()
    {
        var input = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            IntegralHeight = false,
            MaxDropDownItems = 3,
            Height = Theme.ControlHeightCompact,
            AccessibleName = "Timeline frame height preset"
        };
        input.Items.AddRange(["Low", "Medium", "High"]);
        input.SelectedIndex = (int)TimelineFrameHeightPreset.Medium;
        Theme.StyleComboBox(input);
        return input;
    }

    private void LayoutHeaderControls(TimelineLayout layout, bool onionSkinAvailable)
    {
        LayoutTabGroupControls(layout);
        var layoutKey = new HeaderLayoutKey(
            ClientSize,
            IsHandleCreated ? DeviceDpi : 96,
            onionSkinAvailable,
            IsAutoKeyframeAvailable(),
            MotionTrackToggleAvailable,
            MotionTrackRangeVisible,
            _frameWidthLabel.Text,
            _frameHeightLabel.Text,
            _autoKeyframeToggle.Text,
            _onionSkinToggle.Text,
            _motionTrackToggle.Text,
            _onionPreviousLabel.Text,
            _onionNextLabel.Text);
        if (_headerLayoutKey == layoutKey) return;
        _headerLayoutKey = layoutKey;

        var frameWidthBounds = FrameWidthControlsBounds(layout, Rectangle.Empty);
        var showFrameWidth = !frameWidthBounds.IsEmpty;
        SetControlVisible(_frameWidthLabel, showFrameWidth);
        SetControlVisible(_frameWidthSlider, showFrameWidth);
        SetControlVisible(_frameWidthInput, showFrameWidth);
        if (showFrameWidth)
        {
            var left = frameWidthBounds.Left;
            var labelWidth = FrameWidthLabelWidth();
            var sliderWidth = FrameWidthSliderWidth();
            var gap = ScaleTimelineMetric(4);
            SetControlBounds(_frameWidthLabel, new Rectangle(left, frameWidthBounds.Top, labelWidth, frameWidthBounds.Height));
            left += labelWidth;
            SetControlBounds(_frameWidthSlider, new Rectangle(left, frameWidthBounds.Top, sliderWidth, frameWidthBounds.Height));
            left += sliderWidth + gap;
            SetControlBounds(_frameWidthInput, new Rectangle(left, frameWidthBounds.Top, FrameWidthNumericWidth(), frameWidthBounds.Height));
        }

        var frameHeightAnchor = showFrameWidth ? frameWidthBounds : Rectangle.Empty;
        var frameHeightBounds = FrameHeightControlsBounds(layout, frameHeightAnchor);
        var showFrameHeight = !frameHeightBounds.IsEmpty;
        SetControlVisible(_frameHeightLabel, showFrameHeight);
        SetControlVisible(_frameHeightInput, showFrameHeight);
        if (showFrameHeight)
        {
            var labelWidth = FrameHeightLabelWidth();
            SetControlBounds(
                _frameHeightLabel,
                new Rectangle(frameHeightBounds.Left, frameHeightBounds.Top, labelWidth, frameHeightBounds.Height));
            SetControlBounds(
                _frameHeightInput,
                new Rectangle(
                    frameHeightBounds.Left + labelWidth,
                    frameHeightBounds.Top,
                    FrameHeightInputWidth(),
                    frameHeightBounds.Height));
        }

        var onionSkinAnchor = showFrameHeight ? frameHeightBounds : frameHeightAnchor;
        LayoutOnionSkinControls(layout, onionSkinAvailable, onionSkinAnchor);
        LayoutMotionTrackControl(layout);
        LayoutAutoKeyframeControl(layout);
    }

    private Rectangle OnionSkinControlsBounds(TimelineLayout layout, Rectangle rightControlsBounds)
    {
        var controlsWidth = OnionSkinControlsWidth();
        var right = rightControlsBounds.IsEmpty ? layout.TrackRight : rightControlsBounds.Left - ScaleTimelineMetric(8);
        if (!IsOnionSkinControlsAvailable() || right - layout.TrackLeft < controlsWidth + ScaleTimelineMetric(112))
        {
            return Rectangle.Empty;
        }

        // The header's left cluster reads Auto Key, then the motion-track toggle, then the onion-skin
        // group. The group's creep limit therefore has to include the motion toggle's footprint;
        // pinned at TrackLeft + 200 the motion toggle had no slot of its own and was pushed under the
        // onion group (visible in Simplified Chinese, where the longer labels leave only a 70px gap).
        var budget = ScaleTimelineMetric(200) + MotionTrackClusterFootprint();
        var left = Math.Min(
            right - controlsWidth - ScaleTimelineMetric(8),
            layout.TrackLeft + budget);

        // Never let the group drift back over the motion-track toggle. If the frame controls leave
        // room for neither, the onion group yields: it has other controls beside it, while the
        // motion toggle is the feature's only entry point.
        var motionFloor = MotionTrackClusterFloor(layout);
        if (motionFloor > left) left = motionFloor;
        if (left + controlsWidth > right) return Rectangle.Empty;

        return new Rectangle(left, ScaleTimelineMetric(2), controlsWidth, Math.Max(Theme.ControlHeightCompact, ToolbarHeaderHeight - ScaleTimelineMetric(4)));
    }

    /// <summary>
    /// Horizontal space the motion-track cluster contributes to the header's left cluster: the toggle
    /// plus, while a track is presented, its absolute frame range inputs.
    /// </summary>
    private int MotionTrackClusterFootprint() => MotionTrackToggleWidth() + ScaleTimelineMetric(8);

    /// <summary>
    /// Leftmost x the motion-track toggle may occupy: immediately right of Auto Key, which is pinned
    /// to the track's left edge.
    /// </summary>
    private int MotionTrackSlotLeft(TimelineLayout layout)
    {
        var autoKeyframeBounds = AutoKeyframeControlsBounds(layout);
        return autoKeyframeBounds.IsEmpty
            ? layout.TrackLeft + ScaleTimelineMetric(8)
            : autoKeyframeBounds.Right + ScaleTimelineMetric(8);
    }

    /// <summary>
    /// Leftmost x the onion-skin group may occupy while still leaving the motion-track toggle its
    /// own slot between Auto Key and the group.
    /// </summary>
    private int MotionTrackClusterFloor(TimelineLayout layout)
    {
        return MotionTrackSlotLeft(layout) + MotionTrackClusterFootprint();
    }

    private Rectangle AutoKeyframeControlsBounds(TimelineLayout layout)
    {
        if (!IsAutoKeyframeAvailable()) return Rectangle.Empty;
        var controlsWidth = AutoKeyframeToggleWidth();
        var idealLeft = layout.TrackLeft + ScaleTimelineMetric(8);
        var maximumLeft = Math.Max(
            layout.TrackLeft,
            layout.TrackRight - controlsWidth - ScaleTimelineMetric(2));
        return new Rectangle(
            Math.Min(idealLeft, maximumLeft),
            ScaleTimelineMetric(2),
            controlsWidth,
            Math.Max(Theme.ControlHeightCompact, ToolbarHeaderHeight - ScaleTimelineMetric(4)));
    }

    private Rectangle FrameWidthControlsBounds(TimelineLayout layout, Rectangle rightControlsBounds)
    {
        var controlsWidth = FrameWidthControlsWidth();
        var right = rightControlsBounds.IsEmpty
            ? layout.TrackRight - ScaleTimelineMetric(8)
            : rightControlsBounds.Left - ScaleTimelineMetric(8);
        if (right - layout.TrackLeft < controlsWidth + ScaleTimelineMetric(112)) return Rectangle.Empty;
        return new Rectangle(
            right - controlsWidth,
            ScaleTimelineMetric(2),
            controlsWidth,
            Math.Max(Theme.ControlHeightCompact, ToolbarHeaderHeight - ScaleTimelineMetric(4)));
    }

    private Rectangle FrameHeightControlsBounds(TimelineLayout layout, Rectangle rightControlsBounds)
    {
        var controlsWidth = FrameHeightControlsWidth();
        var right = rightControlsBounds.IsEmpty
            ? layout.TrackRight - ScaleTimelineMetric(8)
            : rightControlsBounds.Left - ScaleTimelineMetric(8);
        if (right - layout.TrackLeft < controlsWidth + ScaleTimelineMetric(112)) return Rectangle.Empty;
        return new Rectangle(
            right - controlsWidth,
            ScaleTimelineMetric(2),
            controlsWidth,
            Math.Max(Theme.ControlHeightCompact, ToolbarHeaderHeight - ScaleTimelineMetric(4)));
    }

    private void LayoutOnionSkinControls(TimelineLayout layout, bool available, Rectangle rightControlsBounds)
    {
        var bounds = available ? OnionSkinControlsBounds(layout, rightControlsBounds) : Rectangle.Empty;
        var visible = !bounds.IsEmpty;
        SetControlVisible(_onionSkinToggle, visible);
        SetControlVisible(_onionPreviousLabel, visible);
        SetControlVisible(_onionPreviousFrames, visible);
        SetControlVisible(_onionNextLabel, visible);
        SetControlVisible(_onionNextFrames, visible);
        if (!visible) return;

        var left = bounds.Left;
        var toggleWidth = OnionSkinToggleWidth();
        var previousLabelWidth = OnionSkinLabelWidth(_onionPreviousLabel);
        var nextLabelWidth = OnionSkinLabelWidth(_onionNextLabel);
        var numericWidth = OnionSkinNumericWidth();
        var gap = ScaleTimelineMetric(6);
        SetControlBounds(_onionSkinToggle, new Rectangle(left, bounds.Top, toggleWidth, bounds.Height));
        left += toggleWidth + gap;
        SetControlBounds(_onionPreviousLabel, new Rectangle(left, bounds.Top, previousLabelWidth, bounds.Height));
        left += previousLabelWidth;
        SetControlBounds(_onionPreviousFrames, new Rectangle(left, bounds.Top, numericWidth, bounds.Height));
        left += numericWidth + gap;
        SetControlBounds(_onionNextLabel, new Rectangle(left, bounds.Top, nextLabelWidth, bounds.Height));
        left += nextLabelWidth;
        SetControlBounds(_onionNextFrames, new Rectangle(left, bounds.Top, numericWidth, bounds.Height));
    }

    /// <summary>
    /// Places the motion-track toggle in its own slot between Auto Key and the onion-skin group, so
    /// the three related controls read as one cluster.
    /// </summary>
    private void LayoutMotionTrackControl(TimelineLayout layout)
    {
        // The toggle stays *visible* wherever the timeline can host it, so the feature is
        // discoverable; it is merely *disabled* until a symbol instance is selected, which is what
        // MotionTrackToggleAvailable reports. Hiding it outright would leave no entry point.
        var bounds = MotionTrackControlsBounds(layout);
        SetControlVisible(_motionTrackToggle, !bounds.IsEmpty);
        if (!bounds.IsEmpty) SetControlBounds(_motionTrackToggle, bounds);
    }

    private Rectangle MotionTrackControlsBounds(TimelineLayout layout)
    {
        if (!IsOnionSkinControlsAvailable()) return Rectangle.Empty;
        var controlsWidth = MotionTrackToggleWidth();
        // Sit immediately left of the onion-skin cluster, which is anchored to the right controls.
        var frameHeightBounds = FrameHeightControlsBounds(layout, Rectangle.Empty);
        var frameWidthBounds = FrameWidthControlsBounds(layout, frameHeightBounds);
        var anchor = !frameWidthBounds.IsEmpty ? frameWidthBounds : frameHeightBounds;
        var right = anchor.IsEmpty
            ? layout.TrackRight - ScaleTimelineMetric(8)
            : anchor.Left - ScaleTimelineMetric(8);
        var onionBounds = OnionSkinControlsBounds(layout, anchor);
        if (!onionBounds.IsEmpty) right = onionBounds.Left - ScaleTimelineMetric(8);

        // The toggle is the entry point for the whole feature, so it must never be dropped just
        // because the header is crowded: slide it left, but never under Auto Key, which is pinned to
        // the track's left edge and is laid out after this control.
        var left = right - controlsWidth;
        var minimumLeft = MotionTrackSlotLeft(layout);
        if (left < minimumLeft) left = minimumLeft;
        if (layout.TrackRight - left < controlsWidth) return Rectangle.Empty;
        return new Rectangle(
            left,
            ScaleTimelineMetric(2),
            controlsWidth,
            Math.Max(Theme.ControlHeightCompact, ToolbarHeaderHeight - ScaleTimelineMetric(4)));
    }

    private int MotionTrackToggleWidth()
    {
        return Math.Max(
            ScaleTimelineMetric(72),
            _motionTrackToggle.GetPreferredSize(Size.Empty).Width + ScaleTimelineMetric(2));
    }

    private void LayoutAutoKeyframeControl(TimelineLayout layout)
    {
        var available = IsAutoKeyframeAvailable();
        if (_autoKeyframeToggle.Enabled != available) _autoKeyframeToggle.Enabled = available;
        var bounds = available ? AutoKeyframeControlsBounds(layout) : Rectangle.Empty;
        SetControlVisible(_autoKeyframeToggle, !bounds.IsEmpty);
        if (!bounds.IsEmpty) SetControlBounds(_autoKeyframeToggle, bounds);
    }

    private bool IsAutoKeyframeAvailable() =>
        DrawingScene() is not null
        || IsSceneLightTrack(GetActiveTrackIndex())
        || IsSceneShotTrack(GetActiveTrackIndex())
        // Scene composition tracks materialize keyframes before instance, light,
        // mask and tween edits, so the toggle is meaningful there as well.
        || _sceneDefinition is not null;

    private static void SetControlVisible(Control control, bool visible)
    {
        if (control.Visible != visible) control.Visible = visible;
    }

    private static void SetControlBounds(Control control, Rectangle bounds)
    {
        if (control.Bounds != bounds) control.Bounds = bounds;
    }

    private void RaiseOnionSkinRangeChanged()
    {
        if (_updatingOnionSkinControls) return;
        if (MotionTrackRangeVisible)
        {
            // Same inputs, other meaning: while a track is presented these hold its absolute window.
            MotionTrackRangeChanged?.Invoke(
                this,
                new TimelineMotionTrackRangeChangedEventArgs(
                    (int)_onionPreviousFrames.Value,
                    (int)_onionNextFrames.Value));
            return;
        }

        OnionSkinRangeChanged?.Invoke(
            this,
            new TimelineOnionSkinRangeChangedEventArgs((int)_onionPreviousFrames.Value, (int)_onionNextFrames.Value));
    }

    /// <summary>
    /// Interaction notices for the shared inputs. In absolute mode the inputs only steer view state,
    /// so there is no onion-skin edit for the workbench to open an undo session for.
    /// </summary>
    private void RaiseOnionSkinRangeInteraction(EventHandler? handler)
    {
        if (MotionTrackRangeVisible) return;
        handler?.Invoke(this, EventArgs.Empty);
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

    private int AutoKeyframeToggleWidth()
    {
        return Math.Max(
            ScaleTimelineMetric(96),
            _autoKeyframeToggle.GetPreferredSize(Size.Empty).Width + ScaleTimelineMetric(2));
    }

    private int FrameWidthControlsWidth()
    {
        return FrameWidthLabelWidth()
            + FrameWidthSliderWidth()
            + FrameWidthNumericWidth()
            + ScaleTimelineMetric(4);
    }

    private int FrameHeightControlsWidth()
    {
        return FrameHeightLabelWidth() + FrameHeightInputWidth();
    }

    private int FrameHeightLabelWidth()
    {
        return TextRenderer.MeasureText(
                _frameHeightLabel.Text,
                _frameHeightLabel.Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width
            + ScaleTimelineMetric(8);
    }

    private int FrameHeightInputWidth() => ScaleTimelineMetric(112);

    private int FrameWidthLabelWidth()
    {
        return TextRenderer.MeasureText(
                _frameWidthLabel.Text,
                _frameWidthLabel.Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width
            + ScaleTimelineMetric(8);
    }

    private int FrameWidthSliderWidth() => ScaleTimelineMetric(84);

    private int FrameWidthNumericWidth() => ScaleTimelineMetric(52);

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

    private static void DrawHeaderButton(Graphics graphics, Rectangle bounds, string text, bool hovered)
    {
        var highContrast = SystemInformation.HighContrast;
        var fill = highContrast
            ? hovered ? SystemColors.Highlight : SystemColors.Control
            : hovered
                ? Theme.PanelHover
                : Theme.Mix(Theme.Panel, Theme.PanelStrong, 0.34f);
        using var fillBrush = new SolidBrush(fill);
        using var font = Theme.UiFont(8.5f);
        graphics.FillRectangle(fillBrush, bounds);
        if (hovered || highContrast)
        {
            using var borderPen = new Pen(
                highContrast ? SystemColors.WindowText : Theme.ReadableUiColor(fill, Theme.BorderHover));
            graphics.DrawRectangle(borderPen, bounds);
        }
        TextRenderer.DrawText(
            graphics,
            text,
            font,
            bounds,
            highContrast
                ? hovered ? SystemColors.HighlightText : SystemColors.ControlText
                : Theme.ReadableText(fill, hovered ? Theme.Text : Theme.Muted),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawHeaderIconButton(
        Graphics graphics,
        Rectangle bounds,
        SvgIconKind icon,
        bool hovered)
    {
        var highContrast = SystemInformation.HighContrast;
        var fill = highContrast
            ? hovered ? SystemColors.Highlight : SystemColors.Control
            : hovered
                ? Theme.PanelHover
                : Theme.Mix(Theme.Panel, Theme.PanelStrong, 0.34f);
        using var fillBrush = new SolidBrush(fill);
        graphics.FillRectangle(fillBrush, bounds);
        if (hovered || highContrast)
        {
            using var borderPen = new Pen(
                highContrast ? SystemColors.WindowText : Theme.ReadableUiColor(fill, Theme.BorderHover));
            graphics.DrawRectangle(borderPen, bounds);
        }
        _headerIconCache.Draw(
            graphics,
            icon,
            bounds,
            highContrast
                ? hovered ? SystemColors.HighlightText : SystemColors.ControlText
                : Theme.ReadableText(fill, hovered ? Theme.Text : Theme.Muted));
    }

    private void DrawMasterControlCell(Graphics graphics, Rectangle bounds, HeaderCommand command)
    {
        if (_hoveredHeaderCommand != command || bounds.Width <= 0 || bounds.Height <= 0) return;
        var highContrast = SystemInformation.HighContrast;
        using var fillBrush = new SolidBrush(highContrast ? SystemColors.Highlight : Theme.PanelHover);
        using var borderPen = new Pen(
            highContrast ? SystemColors.WindowText : Theme.ReadableUiColor(Theme.PanelHover, Theme.BorderHover));
        graphics.FillRectangle(fillBrush, bounds);
        graphics.DrawRectangle(borderPen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
    }

    private bool AreAllLayersVisible()
    {
        var drawingScene = DrawingScene();
        if (drawingScene is not null && drawingScene.LayerCount > 0)
        {
            var layersVisible = Enumerable.Range(0, drawingScene.LayerCount)
                .All(layer => layer < drawingScene.LayerVisible.Length && drawingScene.LayerVisible[layer]);
            var instancesVisible = _drawingObjectDefinition?.Instances.All(instance => instance.Visible) != false;
            return layersVisible && instancesVisible;
        }

        if (_sceneDefinition is null || _sceneDefinition.Layers.Count == 0) return false;
        return _sceneDefinition.Layers.All(layer => layer.Visible);
    }

    private bool AreAllLayersLocked()
    {
        var drawingScene = DrawingScene();
        return drawingScene is not null
            && drawingScene.LayerCount > 0
            && Enumerable.Range(0, drawingScene.LayerCount)
                .All(layer => layer < drawingScene.LayerLocked.Length && drawingScene.LayerLocked[layer]);
    }

    private bool AreAllLayersOutlined()
    {
        var drawingScene = DrawingScene();
        if (drawingScene is not null && drawingScene.LayerCount > 0)
        {
            return Enumerable.Range(0, drawingScene.LayerCount)
                .All(layer => layer < drawingScene.LayerOutline.Length && drawingScene.LayerOutline[layer]);
        }

        if (_sceneDefinition is null || _sceneDefinition.Layers.Count == 0) return false;
        return _sceneDefinition.Layers.All(layer => layer.Outline);
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

    private static void DrawOutlineSwatch(Graphics graphics, Rectangle bounds, Color color, bool outlined)
    {
        var size = Math.Clamp(Math.Min(bounds.Width, bounds.Height) - 10, 7, 12);
        var swatch = new Rectangle(
            bounds.Left + (bounds.Width - size) / 2,
            bounds.Top + (bounds.Height - size) / 2,
            size,
            size);
        using var fill = new SolidBrush(color);
        using var edge = new Pen(color, outlined ? 1.6f : 1f);
        if (!outlined) graphics.FillRectangle(fill, swatch);
        graphics.DrawRectangle(edge, swatch.X, swatch.Y, swatch.Width, swatch.Height);
        if (!outlined)
        {
            using var inner = new Pen(Color.FromArgb(90, Color.Black));
            graphics.DrawRectangle(inner, swatch.X + 1, swatch.Y + 1, Math.Max(1, swatch.Width - 2), Math.Max(1, swatch.Height - 2));
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

    private readonly record struct HeaderLayoutKey(
        Size ClientSize,
        int Dpi,
        bool OnionSkinAvailable,
        bool AutoKeyframeAvailable,
        bool MotionTrackAvailable,
        bool MotionTrackRangeVisible,
        string FrameWidthLabel,
        string FrameHeightLabel,
        string AutoKeyframeLabel,
        string OnionSkinLabel,
        string MotionTrackLabel,
        string PreviousLabel,
        string NextLabel);
}
