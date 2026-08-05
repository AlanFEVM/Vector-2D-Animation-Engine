using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class TweenCurveChangedEventArgs(TweenCurveAnchor[] anchors) : EventArgs
{
    public TweenCurveAnchor[] Anchors { get; } = anchors.ToArray();
}

internal sealed class TweenCurveEditorPanel : Panel
{
    private readonly Label _title = new();
    private readonly Label _summary = new();
    private readonly TweenCurveEditor _editor = new();
    private readonly SvgIconButton _addAnchor = new(SvgIconKind.Add);
    private readonly SvgIconButton _removeAnchor = new(SvgIconKind.Remove);
    private readonly ToolTip _toolTip = new() { InitialDelay = 350, ReshowDelay = 80, AutoPopDelay = 5000 };
    private TimelineTweenSelection? _selection;

    public TweenCurveEditorPanel()
    {
        BackColor = Theme.Panel;
        MinimumSize = new Size(224, 0);
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Tween Curve";

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12, 8, 12, 8),
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 19));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        _title.Dock = DockStyle.Fill;
        _title.Font = Theme.UiFont(10, FontStyle.Bold);
        _title.ForeColor = Theme.Text;
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _title.AutoEllipsis = true;

        _summary.Dock = DockStyle.Fill;
        _summary.Font = Theme.UiFont(8.5f);
        _summary.ForeColor = Theme.Muted;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.AutoEllipsis = true;

        _editor.Dock = DockStyle.Fill;
        _editor.Margin = new Padding(0, 4, 0, 5);
        _editor.CurveChanged += (_, e) =>
        {
            RefreshSummary();
            CurveChanged?.Invoke(this, e);
        };
        _editor.SelectedAnchorChanged += (_, _) => RefreshAnchorActions();
        _editor.InteractionStarted += (_, _) => InteractionStarted?.Invoke(this, EventArgs.Empty);
        _editor.InteractionCompleted += (_, _) => InteractionCompleted?.Invoke(this, EventArgs.Empty);
        _editor.InteractionCanceled += (_, _) => InteractionCanceled?.Invoke(this, EventArgs.Empty);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 4, 0, 0)
        };
        ConfigureToolbarButton(_addAnchor, "Add Anchor");
        ConfigureToolbarButton(_removeAnchor, "Delete Anchor");
        _addAnchor.Click += (_, _) => _editor.AddAnchor();
        _removeAnchor.Click += (_, _) => _editor.DeleteSelectedAnchor();
        toolbar.Controls.Add(_addAnchor);
        toolbar.Controls.Add(_removeAnchor);

        layout.Controls.Add(_title, 0, 0);
        layout.Controls.Add(_summary, 0, 1);
        layout.Controls.Add(_editor, 0, 2);
        layout.Controls.Add(toolbar, 0, 3);
        Controls.Add(layout);
        UiLocalization.Watch(this);
        ClearTween();
    }

    public int PreferredPanelHeight => 252;

    public event EventHandler<TweenCurveChangedEventArgs>? CurveChanged;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler? InteractionCanceled;

    public void SetTween(TimelineTweenSelection selection, TimelineTween tween)
    {
        var selectionChanged = _selection != selection;
        _selection = selection;
        _title.Text = UiLocalization.T(tween.Kind == TimelineTweenKind.Classic
            ? "Classic Tween Curve"
            : "Shape Tween Curve");
        _editor.SetCurve(tween.Kind, tween.CurveAnchors);
        if (selectionChanged) _editor.ClearSelection();
        RefreshSummary();
        RefreshAnchorActions();
        Enabled = true;
    }

    public void ClearTween()
    {
        _selection = null;
        _title.Text = UiLocalization.T("Tween Curve");
        _summary.Text = string.Empty;
        _editor.SetCurve(TimelineTweenKind.Classic, [new(0, 0), new(1, 1)]);
        _editor.ClearSelection();
        _addAnchor.Enabled = false;
        _removeAnchor.Enabled = false;
        Enabled = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void ConfigureToolbarButton(SvgIconButton button, string name)
    {
        button.Size = new Size(Theme.ControlHeightCompact, Theme.ControlHeightCompact);
        button.Margin = new Padding(0, 0, 6, 0);
        button.AccessibleName = name;
        Theme.StyleToolbarButton(button);
        _toolTip.SetToolTip(button, UiLocalization.T(name));
    }

    private void RefreshSummary()
    {
        if (_selection is not { } selection)
        {
            _summary.Text = string.Empty;
            return;
        }

        var anchor = _editor.SelectedAnchor;
        var anchorText = anchor is { } point
            ? $" | T {point.Time * 100:0.#}% | V {point.Value * 100:0.#}%"
            : string.Empty;
        _summary.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            UiLocalization.T("Frames {0}-{1} | {2} anchors{3}"),
            selection.StartFrame + 1,
            selection.EndFrame + 1,
            _editor.AnchorCount,
            anchorText);
    }

    private void RefreshAnchorActions()
    {
        _addAnchor.Enabled = _selection is not null && _editor.AnchorCount < TimelineTween.MaxCurveAnchorCount;
        _removeAnchor.Enabled = _selection is not null && _editor.CanDeleteSelectedAnchor;
        RefreshSummary();
    }
}

internal sealed class TweenCurveEditor : Control
{
    private sealed class AnimatedAnchor(TweenCurveAnchor anchor)
    {
        public TweenCurveAnchor Target = anchor;
        public PointF Position = new(anchor.Time, anchor.Value);
        public PointF Velocity;
        public float Scale = 1;
        public float ScaleVelocity;
        public float Alpha = 1;
    }

    private sealed class DeletedAnchorGhost(PointF position)
    {
        public PointF Position = position;
        public float Scale = 1;
        public float ScaleVelocity;
        public float Alpha = 1;
    }

    private readonly System.Windows.Forms.Timer _motionTimer = new() { Interval = 16 };
    private readonly Stopwatch _motionClock = Stopwatch.StartNew();
    private readonly List<AnimatedAnchor> _visuals = [];
    private readonly List<DeletedAnchorGhost> _ghosts = [];
    private TweenCurveAnchor[] _anchors = [new(0, 0), new(1, 1)];
    private TweenCurveAnchor[]? _interactionStart;
    private TimelineTweenKind _kind = TimelineTweenKind.Classic;
    private int _selectedAnchor = -1;
    private int _draggedAnchor = -1;
    private int _hoveredAnchor = -1;
    private long _lastMotionMilliseconds;

    public TweenCurveEditor()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.UserPaint,
            true);
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        TabStop = true;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Tween curve editor";
        AccessibleDescription = "Edits the selected tween easing curve";
        _motionTimer.Tick += (_, _) => TickMotion();
        RebuildVisuals(animateAdded: false);
    }

    public int AnchorCount => _anchors.Length;
    public TweenCurveAnchor? SelectedAnchor => (uint)_selectedAnchor < _anchors.Length
        ? _anchors[_selectedAnchor]
        : null;
    public bool CanDeleteSelectedAnchor => _selectedAnchor > 0 && _selectedAnchor < _anchors.Length - 1;

    public event EventHandler<TweenCurveChangedEventArgs>? CurveChanged;
    public event EventHandler? SelectedAnchorChanged;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler? InteractionCanceled;

    public void ClearSelection() => SelectAnchor(-1);

    public void SetCurve(TimelineTweenKind kind, IEnumerable<TweenCurveAnchor> anchors)
    {
        var next = anchors.ToArray();
        if (!TimelineTween.TryNormalizeCurveAnchors(next, out var normalized))
        {
            normalized = [new(0, 0), new(1, 1)];
        }
        next = normalized;

        _kind = kind is TimelineTweenKind.Classic or TimelineTweenKind.Shape
            ? kind
            : TimelineTweenKind.Classic;
        if (_anchors.SequenceEqual(next)) return;
        _anchors = next;
        _selectedAnchor = -1;
        RebuildVisuals(animateAdded: false);
        SelectedAnchorChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void AddAnchor()
    {
        if (_anchors.Length >= TimelineTween.MaxCurveAnchorCount) return;
        var interval = 0;
        var widest = float.MinValue;
        for (var index = 0; index < _anchors.Length - 1; index++)
        {
            var width = _anchors[index + 1].Time - _anchors[index].Time;
            if (width <= widest) continue;
            widest = width;
            interval = index;
        }

        var time = (_anchors[interval].Time + _anchors[interval + 1].Time) * 0.5f;
        AddAnchorAt(new TweenCurveAnchor(time, EvaluateModelCurve(time)));
    }

    public void DeleteSelectedAnchor()
    {
        if (!CanDeleteSelectedAnchor) return;
        RunDiscreteInteraction(() =>
        {
            var removedIndex = _selectedAnchor;
            var visual = _visuals[removedIndex];
            _ghosts.Add(new DeletedAnchorGhost(visual.Position));
            _anchors = _anchors.Where((_, index) => index != removedIndex).ToArray();
            _visuals.RemoveAt(removedIndex);
            _selectedAnchor = Math.Clamp(removedIndex - 1, 0, _anchors.Length - 1);
            CurveChanged?.Invoke(this, new TweenCurveChangedEventArgs(_anchors));
            SelectedAnchorChanged?.Invoke(this, EventArgs.Empty);
            StartMotion();
            Invalidate();
        });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Theme.Field);
        var chart = ChartBounds();
        if (chart.Width <= 2 || chart.Height <= 2) return;

        using var gridPen = new Pen(Color.FromArgb(80, Theme.Border), Math.Max(1f, DeviceDpi / 144f));
        using var referencePen = new Pen(Color.FromArgb(100, Theme.Muted), Math.Max(1f, DeviceDpi / 144f))
        {
            DashStyle = DashStyle.Dash
        };
        for (var index = 1; index < 4; index++)
        {
            var x = chart.Left + chart.Width * index / 4f;
            var y = chart.Top + chart.Height * index / 4f;
            e.Graphics.DrawLine(gridPen, x, chart.Top, x, chart.Bottom);
            e.Graphics.DrawLine(gridPen, chart.Left, y, chart.Right, y);
        }
        e.Graphics.DrawLine(referencePen, chart.Left, chart.Bottom, chart.Right, chart.Top);

        var displayAnchors = DisplayAnchors();
        using var curvePath = BuildCurvePath(chart, displayAnchors);
        using var curveGlow = new Pen(Color.FromArgb(48, Theme.Accent), DpiScale(5f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var curvePen = new Pen(Theme.Accent, DpiScale(2f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        e.Graphics.DrawPath(curveGlow, curvePath);
        e.Graphics.DrawPath(curvePen, curvePath);

        foreach (var ghost in _ghosts) DrawAnchor(e.Graphics, chart, ghost.Position, -1, ghost.Scale, ghost.Alpha, ghost: true);
        for (var index = 0; index < _visuals.Count; index++)
        {
            DrawAnchor(e.Graphics, chart, _visuals[index].Position, index, _visuals[index].Scale, _visuals[index].Alpha, ghost: false);
        }

        using var border = new Pen(Focused ? Theme.Accent : Theme.Border, Math.Max(1f, DeviceDpi / 96f));
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), Theme.AccentLabel, Theme.Field);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        var hit = HitTestAnchor(e.Location);
        SelectAnchor(hit);
        if (hit <= 0 || hit >= _anchors.Length - 1) return;
        _draggedAnchor = hit;
        _interactionStart = _anchors.ToArray();
        Capture = true;
        InteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggedAnchor >= 0)
        {
            var next = AnchorFromPoint(e.Location, _draggedAnchor);
            if (_anchors[_draggedAnchor] == next) return;
            _anchors[_draggedAnchor] = next;
            _visuals[_draggedAnchor].Target = next;
            CurveChanged?.Invoke(this, new TweenCurveChangedEventArgs(_anchors));
            SelectedAnchorChanged?.Invoke(this, EventArgs.Empty);
            StartMotion();
            Invalidate();
            return;
        }

        var hover = HitTestAnchor(e.Location);
        if (_hoveredAnchor != hover)
        {
            _hoveredAnchor = hover;
            Invalidate();
        }
        Cursor = hover >= 0 ? Cursors.Hand : Cursors.Cross;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || _draggedAnchor < 0) return;
        _draggedAnchor = -1;
        _interactionStart = null;
        Capture = false;
        Cursor = Cursors.Cross;
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || _anchors.Length >= TimelineTween.MaxCurveAnchorCount) return;
        var chart = ChartBounds();
        if (!chart.Contains(e.Location) || HitTestAnchor(e.Location) >= 0) return;
        var time = Math.Clamp((e.X - chart.Left) / (float)Math.Max(1, chart.Width), 0, 1);
        var value = Math.Clamp(1f - (e.Y - chart.Top) / (float)Math.Max(1, chart.Height), 0, 1);
        AddAnchorAt(new TweenCurveAnchor(time, value));
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_draggedAnchor >= 0 && !Capture) CancelInteraction();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && _draggedAnchor >= 0)
        {
            CancelInteraction();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode is Keys.Delete or Keys.Back)
        {
            DeleteSelectedAnchor();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Insert)
        {
            AddAnchor();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (!CanDeleteSelectedAnchor || e.KeyCode is not (Keys.Left or Keys.Right or Keys.Up or Keys.Down)) return;

        var step = e.Shift ? 0.05f : 0.01f;
        var current = _anchors[_selectedAnchor];
        var point = e.KeyCode switch
        {
            Keys.Left => current with { Time = current.Time - step },
            Keys.Right => current with { Time = current.Time + step },
            Keys.Up => current with { Value = current.Value + step },
            _ => current with { Value = current.Value - step }
        };
        RunDiscreteInteraction(() => UpdateAnchor(_selectedAnchor, point));
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _motionTimer.Stop();
            _motionTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void AddAnchorAt(TweenCurveAnchor requested)
    {
        var insertion = Array.FindIndex(_anchors, anchor => anchor.Time > requested.Time);
        if (insertion <= 0) return;
        var previous = _anchors[insertion - 1];
        var next = _anchors[insertion];
        var epsilon = Math.Min(0.002f, (next.Time - previous.Time) * 0.25f);
        var anchor = new TweenCurveAnchor(
            Math.Clamp(requested.Time, previous.Time + epsilon, next.Time - epsilon),
            Math.Clamp(requested.Value, previous.Value, next.Value));
        RunDiscreteInteraction(() =>
        {
            var anchors = _anchors.ToList();
            anchors.Insert(insertion, anchor);
            _anchors = anchors.ToArray();
            var visual = new AnimatedAnchor(anchor);
            if (MotionEnabled())
            {
                visual.Scale = 0.35f;
                visual.Alpha = 0;
            }
            _visuals.Insert(insertion, visual);
            _selectedAnchor = insertion;
            CurveChanged?.Invoke(this, new TweenCurveChangedEventArgs(_anchors));
            SelectedAnchorChanged?.Invoke(this, EventArgs.Empty);
            StartMotion();
            Invalidate();
        });
    }

    private void UpdateAnchor(int index, TweenCurveAnchor requested)
    {
        if (index <= 0 || index >= _anchors.Length - 1) return;
        var previous = _anchors[index - 1];
        var next = _anchors[index + 1];
        var epsilon = Math.Min(0.002f, (next.Time - previous.Time) * 0.25f);
        var anchor = new TweenCurveAnchor(
            Math.Clamp(requested.Time, previous.Time + epsilon, next.Time - epsilon),
            Math.Clamp(requested.Value, previous.Value, next.Value));
        if (_anchors[index] == anchor) return;
        _anchors[index] = anchor;
        _visuals[index].Target = anchor;
        CurveChanged?.Invoke(this, new TweenCurveChangedEventArgs(_anchors));
        SelectedAnchorChanged?.Invoke(this, EventArgs.Empty);
        StartMotion();
        Invalidate();
    }

    private TweenCurveAnchor AnchorFromPoint(Point point, int index)
    {
        var chart = ChartBounds();
        var requested = new TweenCurveAnchor(
            Math.Clamp((point.X - chart.Left) / (float)Math.Max(1, chart.Width), 0, 1),
            Math.Clamp(1f - (point.Y - chart.Top) / (float)Math.Max(1, chart.Height), 0, 1));
        var previous = _anchors[index - 1];
        var next = _anchors[index + 1];
        var epsilon = Math.Min(0.002f, (next.Time - previous.Time) * 0.25f);
        return new TweenCurveAnchor(
            Math.Clamp(requested.Time, previous.Time + epsilon, next.Time - epsilon),
            Math.Clamp(requested.Value, previous.Value, next.Value));
    }

    private void RunDiscreteInteraction(Action mutation)
    {
        _interactionStart = _anchors.ToArray();
        InteractionStarted?.Invoke(this, EventArgs.Empty);
        mutation();
        _interactionStart = null;
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelInteraction()
    {
        var original = _interactionStart;
        _draggedAnchor = -1;
        _interactionStart = null;
        Capture = false;
        if (original is not null)
        {
            _anchors = original;
            RebuildVisuals(animateAdded: false);
        }
        InteractionCanceled?.Invoke(this, EventArgs.Empty);
        SelectedAnchorChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private void SelectAnchor(int index)
    {
        if (_selectedAnchor == index) return;
        _selectedAnchor = index;
        SelectedAnchorChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private int HitTestAnchor(Point point)
    {
        var chart = ChartBounds();
        var radius = DpiScale(8f);
        var best = -1;
        var bestDistance = radius * radius;
        for (var index = 0; index < _visuals.Count; index++)
        {
            var center = ToPoint(chart, _visuals[index].Position);
            var dx = center.X - point.X;
            var dy = center.Y - point.Y;
            var distance = dx * dx + dy * dy;
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = index;
        }
        return best;
    }

    private void DrawAnchor(
        Graphics graphics,
        Rectangle chart,
        PointF position,
        int index,
        float scale,
        float alpha,
        bool ghost)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        scale = Math.Clamp(scale, 0, 1.12f);
        if (alpha <= 0.01f || scale <= 0.01f) return;
        var center = ToPoint(chart, position);
        var radius = DpiScale(index == _selectedAnchor ? 5.2f : 4.2f) * scale;
        var bounds = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        var selected = index == _selectedAnchor;
        var endpoint = index == 0 || index == _anchors.Length - 1;
        var fillColor = ghost
            ? Theme.Danger
            : selected ? Theme.AccentLabel : endpoint ? Theme.Muted : Theme.Accent;
        using var fill = new SolidBrush(Color.FromArgb((int)(alpha * 255), fillColor));
        using var outline = new Pen(Color.FromArgb((int)(alpha * 245), selected ? Theme.Text : Theme.PanelStrong), DpiScale(selected ? 2f : 1.2f));
        graphics.FillEllipse(fill, bounds);
        graphics.DrawEllipse(outline, bounds);
        if (!ghost && index == _hoveredAnchor && !selected)
        {
            using var hover = new Pen(Color.FromArgb((int)(alpha * 180), Theme.AccentLabel), DpiScale(1f));
            graphics.DrawEllipse(hover, RectangleF.Inflate(bounds, DpiScale(2.5f), DpiScale(2.5f)));
        }
    }

    private GraphicsPath BuildCurvePath(Rectangle chart, TweenCurveAnchor[] anchors)
    {
        var path = new GraphicsPath();
        const int sampleCount = 96;
        var tween = new TimelineTween(0, sampleCount, _kind) { CurveAnchors = anchors };
        var previous = ToPoint(chart, new TweenCurveAnchor(0, 0));
        for (var sample = 1; sample <= sampleCount; sample++)
        {
            var time = sample / (float)sampleCount;
            var current = ToPoint(chart, new TweenCurveAnchor(time, tween.ProgressAtNormalized(time)));
            path.AddLine(previous, current);
            previous = current;
        }
        return path;
    }

    private TweenCurveAnchor[] DisplayAnchors()
    {
        var display = _visuals
            .Select(visual => new TweenCurveAnchor(
                Math.Clamp(visual.Position.X, 0, 1),
                Math.Clamp(visual.Position.Y, 0, 1)))
            .ToArray();
        if (display.Length < 2) return [new(0, 0), new(1, 1)];
        display[0] = new TweenCurveAnchor(0, 0);
        display[^1] = new TweenCurveAnchor(1, 1);
        for (var index = 1; index < display.Length - 1; index++)
        {
            var maximumTime = 1f - 0.0001f * (display.Length - 1 - index);
            display[index] = display[index] with
            {
                Time = Math.Clamp(display[index].Time, display[index - 1].Time + 0.0001f, maximumTime),
                Value = Math.Max(display[index - 1].Value, display[index].Value)
            };
        }
        for (var index = display.Length - 2; index > 0; index--)
        {
            display[index] = display[index] with
            {
                Time = Math.Min(display[index].Time, display[index + 1].Time - 0.0001f),
                Value = Math.Min(display[index].Value, display[index + 1].Value)
            };
        }
        return display;
    }

    private float EvaluateModelCurve(float time)
    {
        var tween = new TimelineTween(0, 1000, _kind) { CurveAnchors = _anchors };
        return tween.ProgressAtNormalized(time);
    }

    private void RebuildVisuals(bool animateAdded)
    {
        _visuals.Clear();
        foreach (var anchor in _anchors)
        {
            var visual = new AnimatedAnchor(anchor);
            if (animateAdded && MotionEnabled())
            {
                visual.Scale = 0.35f;
                visual.Alpha = 0;
            }
            _visuals.Add(visual);
        }
        _ghosts.Clear();
        _motionTimer.Stop();
    }

    private void StartMotion()
    {
        if (!MotionEnabled())
        {
            foreach (var visual in _visuals)
            {
                visual.Position = new PointF(visual.Target.Time, visual.Target.Value);
                visual.Velocity = PointF.Empty;
                visual.Scale = 1;
                visual.ScaleVelocity = 0;
                visual.Alpha = 1;
            }
            _ghosts.Clear();
            _motionTimer.Stop();
            Invalidate();
            return;
        }

        _lastMotionMilliseconds = _motionClock.ElapsedMilliseconds;
        if (!_motionTimer.Enabled) _motionTimer.Start();
    }

    private void TickMotion()
    {
        if (!MotionEnabled())
        {
            StartMotion();
            return;
        }

        var now = _motionClock.ElapsedMilliseconds;
        var dt = Math.Clamp((now - _lastMotionMilliseconds) / 1000f, 0.008f, 0.034f);
        _lastMotionMilliseconds = now;
        var settled = true;
        foreach (var visual in _visuals)
        {
            var target = new PointF(visual.Target.Time, visual.Target.Value);
            settled &= StepSpring(ref visual.Position, ref visual.Velocity, target, dt, 260f, 22f);
            settled &= StepSpring(ref visual.Scale, ref visual.ScaleVelocity, 1f, dt, 320f, 18f, 0, 1.12f);
            var alphaStep = Math.Min(1, visual.Alpha + dt * 8f);
            settled &= Math.Abs(alphaStep - visual.Alpha) < 0.0001f || alphaStep >= 0.999f;
            visual.Alpha = alphaStep;
        }

        for (var index = _ghosts.Count - 1; index >= 0; index--)
        {
            var ghost = _ghosts[index];
            StepSpring(ref ghost.Scale, ref ghost.ScaleVelocity, 0f, dt, 280f, 24f, 0, 1.12f);
            ghost.Alpha = Math.Max(0, ghost.Alpha - dt * 6.5f);
            if (ghost.Alpha <= 0.01f && ghost.Scale <= 0.03f) _ghosts.RemoveAt(index);
            else settled = false;
        }

        Invalidate();
        if (settled && _ghosts.Count == 0) _motionTimer.Stop();
    }

    private static bool StepSpring(
        ref PointF value,
        ref PointF velocity,
        PointF target,
        float dt,
        float stiffness,
        float damping)
    {
        velocity.X += ((target.X - value.X) * stiffness - velocity.X * damping) * dt;
        velocity.Y += ((target.Y - value.Y) * stiffness - velocity.Y * damping) * dt;
        velocity.X = Math.Clamp(velocity.X, -8, 8);
        velocity.Y = Math.Clamp(velocity.Y, -8, 8);
        value.X += velocity.X * dt;
        value.Y += velocity.Y * dt;
        if (Math.Abs(target.X - value.X) > 0.0002f
            || Math.Abs(target.Y - value.Y) > 0.0002f
            || Math.Abs(velocity.X) > 0.003f
            || Math.Abs(velocity.Y) > 0.003f)
        {
            return false;
        }
        value = target;
        velocity = PointF.Empty;
        return true;
    }

    private static bool StepSpring(
        ref float value,
        ref float velocity,
        float target,
        float dt,
        float stiffness,
        float damping,
        float minimum,
        float maximum)
    {
        velocity += ((target - value) * stiffness - velocity * damping) * dt;
        velocity = Math.Clamp(velocity, -8, 8);
        value = Math.Clamp(value + velocity * dt, minimum, maximum);
        if (Math.Abs(target - value) > 0.0005f || Math.Abs(velocity) > 0.004f) return false;
        value = target;
        velocity = 0;
        return true;
    }

    private Rectangle ChartBounds()
    {
        var insetX = Math.Max(10, (int)Math.Round(12 * DeviceDpi / 96f));
        var insetY = Math.Max(8, (int)Math.Round(10 * DeviceDpi / 96f));
        return Rectangle.FromLTRB(insetX, insetY, Math.Max(insetX + 1, Width - insetX), Math.Max(insetY + 1, Height - insetY));
    }

    private static PointF ToPoint(Rectangle chart, TweenCurveAnchor anchor) => new(
        chart.Left + anchor.Time * chart.Width,
        chart.Bottom - anchor.Value * chart.Height);

    private static PointF ToPoint(Rectangle chart, PointF position) => new(
        chart.Left + position.X * chart.Width,
        chart.Bottom - position.Y * chart.Height);

    private float DpiScale(float value) => Math.Max(1f, value * DeviceDpi / 96f);
    private static bool MotionEnabled() => UiMotion.AnimationsEnabled && !SystemInformation.HighContrast;
}
