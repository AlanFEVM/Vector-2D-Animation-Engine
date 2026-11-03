using System.Globalization;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private Control? _horizontalRuler;
    private Control? _verticalRuler;
    private Button? _rulerMenuButton;
    private string _rulerUnit = "px";
    private bool _rulersVisible;
    private AnimatedContextMenuStrip? _rulerMenu;
    internal event EventHandler? RulerGuidesChanged;
    private RulerGuide? _rulerGuidePreview;
    private RulerGuide? _movingRulerGuide;

    private bool TryBeginExistingGuideDrag(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || (ModifierKeys & Keys.Control) == 0) return false;
        var lines = RulerGuideSegments().ToArray();
        var best = -1;
        var distance = 6f * DeviceDpi / 96f;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var dx = line.End.X - line.Start.X;
            var dy = line.End.Y - line.Start.Y;
            var length = dx * dx + dy * dy;
            if (length <= 0) continue;
            var t = Math.Clamp(((e.X - line.Start.X) * dx + (e.Y - line.Start.Y) * dy) / length, 0, 1);
            var x = e.X - line.Start.X - t * dx;
            var y = e.Y - line.Start.Y - t * dy;
            var candidate = MathF.Sqrt(x * x + y * y);
            if (candidate > distance) continue;
            distance = candidate;
            best = index;
        }
        if (best < 0) return false;
        var guide = lines[best].Guide;
        var ruler = (RulerStrip?)(guide.Horizontal ? _horizontalRuler : _verticalRuler);
        ruler?.BeginExistingDrag(guide, e.Location);
        return ruler is not null;
    }
    internal List<RulerGuide> CurrentRulerGuides => _referenceSceneDefinition?.RulerGuides ?? Scene.RulerGuides;

    internal IEnumerable<(PointF Start, PointF End, RulerGuide Guide)> RulerGuideSegments()
    {
        foreach (var guide in CurrentRulerGuides.Concat(_rulerGuidePreview is { } preview ? new[] { preview } : Array.Empty<RulerGuide>()))
        {
            if (ReferenceEquals(guide, _movingRulerGuide)) continue;
            var a = guide.Horizontal ? new PointF(-1000000, guide.Position) : new PointF(guide.Position, -1000000);
            var b = guide.Horizontal ? new PointF(1000000, guide.Position) : new PointF(guide.Position, 1000000);
            if (RendersReferenceProjection)
            {
                if (!TryProjectScenePosition(new System.Numerics.Vector3(a.X, a.Y, 0), out a, out _)
                    || !TryProjectScenePosition(new System.Numerics.Vector3(b.X, b.Y, 0), out b, out _)) continue;
            }
            else { a = WorldToScreen(a); b = WorldToScreen(b); }
            yield return (a, b, guide);
        }
    }

    private void DrawRulerGuidesGdi(Graphics graphics)
    {
        using var pen = new Pen(Color.FromArgb(190, Theme.Accent)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        foreach (var line in RulerGuideSegments()) graphics.DrawLine(pen, line.Start, line.End);
    }

    public void ShowRulerMenu(Control anchor)
    {
        _rulerMenu?.Show(anchor, new Point(0, anchor.Height));
    }

    private void InitializeRulers()
    {
        var menu = new AnimatedContextMenuStrip();
        _rulerMenu = menu;
        var hidden = new ToolStripMenuItem("隐藏标尺 / Hide rulers");
        menu.Items.Add(hidden);
        hidden.Click += (_, _) => SetRulerUnit(null);
        foreach (var unit in new[] { "px", "mm", "cm", "in" })
        {
            var item = new ToolStripMenuItem(unit == "px" ? "像素 / Pixels (px)" : unit + " (96 px/in)") { Tag = unit };
            item.Click += (_, _) => SetRulerUnit(unit);
            menu.Items.Add(item);
        }
        menu.Opening += (_, _) =>
        {
            hidden.Checked = !_rulersVisible;
            foreach (var item in menu.Items.OfType<ToolStripMenuItem>().Where(item => item.Tag is string))
                item.Checked = _rulersVisible && Equals(item.Tag, _rulerUnit);
        };
        _horizontalRuler = new RulerStrip(this, true);
        _verticalRuler = new RulerStrip(this, false);
        _rulerMenuButton = new Button
        {
            Text = "↔", FlatStyle = FlatStyle.Flat, TabStop = true,
            AccessibleName = "标尺 / Rulers", BackColor = Color.FromArgb(38, 46, 50), ForeColor = Color.White
        };
        _rulerMenuButton.Click += (_, _) => menu.Show(_rulerMenuButton, new Point(0, _rulerMenuButton.Height));
        _rulerMenuButton.Visible = false;
        _rulerMenuButton.Disposed += (_, _) => menu.Dispose();
        Controls.AddRange([_horizontalRuler, _verticalRuler, _rulerMenuButton]);
        SizeChanged += (_, _) => LayoutRulers();
        Invalidated += (_, _) => { _horizontalRuler.Invalidate(); _verticalRuler.Invalidate(); };
        // Scene navigation can present a scheduled GPU frame without invalidating
        // the parent HWND. Refresh child rulers after the camera frame is presented.
        ViewChanged += (_, _) => RefreshVisibleRulers();
        FrameRendered += (_, _) => RefreshVisibleRulers();
        LayoutRulers();
    }

    private void RefreshVisibleRulers()
    {
        if (!_rulersVisible || IsDisposed || Disposing) return;
        _horizontalRuler?.Invalidate();
        _verticalRuler?.Invalidate();
        _horizontalRuler?.Update();
        _verticalRuler?.Update();
    }

    private void SetRulerUnit(string? unit)
    {
        _rulersVisible = unit is not null;
        if (unit is not null) _rulerUnit = unit;
        LayoutRulers();
        Invalidate();
    }

    private void LayoutRulers()
    {
        if (_horizontalRuler is null || _verticalRuler is null || _rulerMenuButton is null) return;
        var thickness = Math.Max(22, (int)Math.Round(24 * DeviceDpi / 96f));
        _rulerMenuButton.SetBounds(0, 0, thickness, thickness);
        _horizontalRuler.SetBounds(0, Math.Max(0, ClientSize.Height - thickness), Math.Max(0, ClientSize.Width - thickness), thickness);
        _verticalRuler.SetBounds(Math.Max(0, ClientSize.Width - thickness), 0, thickness, Math.Max(0, ClientSize.Height - thickness));
        _horizontalRuler.Visible = _verticalRuler.Visible = _rulersVisible;
        _horizontalRuler.Invalidate();
        _verticalRuler.Invalidate();
    }

    private sealed class RulerStrip : Control
    {
        private readonly StageControl _stage;
        private readonly bool _horizontal;
        private bool _draggingGuide;
        private List<RulerGuide>? _dragOwner;
        private RulerGuide? _originalGuide;

        internal void BeginExistingDrag(RulerGuide guide, Point stagePoint)
        {
            Focus();
            _originalGuide = guide;
            _stage._movingRulerGuide = guide;
            _dragOwner = _stage.CurrentRulerGuides;
            _draggingGuide = true;
            Capture = true;
            UpdateGuidePreview(PointToClient(_stage.PointToScreen(stagePoint)));
        }
        public RulerStrip(StageControl stage, bool horizontal)
        {
            _stage = stage;
            _horizontal = horizontal;
            DoubleBuffered = true;
            TabStop = false;
            AccessibleName = horizontal ? "水平标尺 / Horizontal ruler" : "垂直标尺 / Vertical ruler";
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            _draggingGuide = true;
            _dragOwner = _stage.CurrentRulerGuides;
            Capture = true;
            UpdateGuidePreview(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_draggingGuide) UpdateGuidePreview(e.Location);
        }

        private void CancelGuideDrag()
        {
            _draggingGuide = false;
            _dragOwner = null;
            _originalGuide = null;
            _stage._movingRulerGuide = null;
            _stage._rulerGuidePreview = null;
            Capture = false;
            _stage.Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && _draggingGuide) CancelGuideDrag();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_draggingGuide && keyData == Keys.Escape) { CancelGuideDrag(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_draggingGuide || e.Button != MouseButtons.Left) return;
            UpdateGuidePreview(e.Location);
            var guide = _stage._rulerGuidePreview;
            var owner = _dragOwner;
            var original = _originalGuide;
            var stagePoint = _stage.PointToClient(PointToScreen(e.Location));
            var delete = original is not null && (_stage._horizontalRuler!.Visible && _stage._horizontalRuler.Bounds.Contains(stagePoint)
                || _stage._verticalRuler!.Visible && _stage._verticalRuler.Bounds.Contains(stagePoint));
            CancelGuideDrag();
            if (!ReferenceEquals(owner, _stage.CurrentRulerGuides) || owner is null) return;
            if (original is not null)
            {
                var index = owner.FindIndex(item => ReferenceEquals(item, original));
                if (index < 0 || (!delete && guide is null)) return;
                if (delete) owner.RemoveAt(index);
                else owner[index] = guide!;
            }
            else
            {
                if (guide is null || owner.Count >= 4096) return;
                owner.Add(guide);
            }
            _stage.RulerGuidesChanged?.Invoke(_stage, EventArgs.Empty);
            _stage.Invalidate();
            base.OnMouseUp(e);
        }

        private void UpdateGuidePreview(Point location)
        {
            _stage._rulerGuidePreview = null;
            _stage.Invalidate();
            var screen = _stage.PointToClient(PointToScreen(location));
            if (!_stage.ClientRectangle.Contains(screen)
                || _stage._horizontalRuler!.Bounds.Contains(screen) || _stage._verticalRuler!.Bounds.Contains(screen)) return;
            var world = _stage.ScreenToWorld(screen);
            if (_stage.RendersReferenceProjection)
            {
                if (!_stage.TryGetReferenceRay(screen, out var ray) || Math.Abs(ray.Direction.Z) < 0.00001f) return;
                var distance = -ray.Origin.Z / ray.Direction.Z;
                if (distance < 0) return;
                var point = ray.Origin + ray.Direction * distance;
                world = new PointF(point.X, point.Y);
            }
            var position = VectorUnits.Quantize(_horizontal ? world.Y : world.X);
            if (!float.IsFinite(position) || Math.Abs(position) > 100000000) return;
            _stage._rulerGuidePreview = new RulerGuide(_horizontal, position);
            _stage.Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(38, 46, 50));
            var pixelsPerUnit = _stage._rulerUnit switch { "mm" => 96d / 25.4, "cm" => 96d / 2.54, "in" => 96d, _ => 1d };
            var worldPerUnit = pixelsPerUnit * VectorUnits.UnitsPerPixel;
            var rotated = _stage.PlanarCanvasRotation != 0;
            var origin = _stage.WorldToScreen(PointF.Empty);
            var axis = _stage.WorldToScreen(_horizontal ? new PointF(1000, 0) : new PointF(0, 1000));
            if (_stage.RendersReferenceProjection)
            {
                if (!_stage.TryProjectScenePosition(System.Numerics.Vector3.Zero, out origin, out _)
                    || !_stage.TryProjectScenePosition(
                        _horizontal ? new System.Numerics.Vector3(1000, 0, 0) : new System.Numerics.Vector3(0, 1000, 0),
                        out axis, out _)) return;
            }
            double originPosition = _horizontal ? origin.X : origin.Y;
            double pixelsPerWorld = ((_horizontal ? axis.X : axis.Y) - originPosition) / 1000d;
            if (rotated)
            {
                PointF WorldAt(Point screen)
                {
                    if (!_stage.RendersReferenceProjection) return _stage.ScreenToWorld(screen);
                    if (!_stage.TryGetReferenceRay(screen, out var ray) || Math.Abs(ray.Direction.Z) < 0.00001f) return PointF.Empty;
                    var point = ray.Origin - ray.Direction * (ray.Origin.Z / ray.Direction.Z);
                    return new PointF(point.X, point.Y);
                }
                var firstWorld = WorldAt(_horizontal ? new Point(0, Top) : new Point(Left, 0));
                var second = WorldAt(_horizontal ? new Point(100, Top) : new Point(Left, 100));
                var useX = Math.Abs(second.X - firstWorld.X) >= Math.Abs(second.Y - firstWorld.Y);
                var change = useX ? second.X - firstWorld.X : second.Y - firstWorld.Y;
                if (Math.Abs(change) < 0.000001f) return;
                pixelsPerWorld = 100d / change;
                originPosition = -(useX ? firstWorld.X : firstWorld.Y) * pixelsPerWorld;
            }
            var screenPerUnit = Math.Abs(pixelsPerWorld * worldPerUnit);
            if (!double.IsFinite(screenPerUnit) || screenPerUnit <= 0) return;
            var rough = 90 * DeviceDpi / 96d / screenPerUnit;
            var power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
            var major = new[] { 1d, 2d, 5d, 10d }.First(value => value * power >= rough) * power;
            var step = major / 5;
            var minimum = ((_horizontal ? Left : Top) - originPosition) / pixelsPerWorld / worldPerUnit;
            var maximum = ((_horizontal ? Right : Bottom) - originPosition) / pixelsPerWorld / worldPerUnit;
            using var pen = new Pen(Color.FromArgb(151, 168, 174));
            var first = Math.Floor(Math.Min(minimum, maximum) / step);
            var last = Math.Ceiling(Math.Max(minimum, maximum) / step);
            for (var index = first; index <= last && index - first < 2048; index++)
            {
                var value = index * step;
                var position = (float)(originPosition + value * worldPerUnit * pixelsPerWorld - (_horizontal ? Left : Top));
                if (_stage.RendersReferenceProjection && !rotated)
                {
                    var coordinate = (float)(value * worldPerUnit);
                    if (!_stage.TryProjectScenePosition(
                        _horizontal ? new System.Numerics.Vector3(coordinate, 0, 0) : new System.Numerics.Vector3(0, coordinate, 0),
                        out var projected, out _)) continue;
                    position = _horizontal ? projected.X - Left : projected.Y - Top;
                }
                var primary = Math.Abs(index % 5) < 0.001;
                if (_horizontal) e.Graphics.DrawLine(pen, position, 0, position, primary ? 10 : 5);
                else e.Graphics.DrawLine(pen, 0, position, primary ? 10 : 5, position);
                if (!primary) continue;
                var label = (Math.Abs(value) < step * 0.001 ? 0 : value).ToString("0.####", CultureInfo.InvariantCulture);
                if (_horizontal)
                    TextRenderer.DrawText(e.Graphics, label, Font, new Point((int)position + 2, 1), Color.Gainsboro, TextFormatFlags.NoPadding);
                else
                {
                    var state = e.Graphics.Save();
                    e.Graphics.TranslateTransform(1, position - 2);
                    e.Graphics.RotateTransform(-90);
                    using var brush = new SolidBrush(Color.Gainsboro);
                    e.Graphics.DrawString(label, Font, brush, 0, 0);
                    e.Graphics.Restore(state);
                }
            }
        }
    }
}
