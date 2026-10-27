using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class CameraDepthDiagram : Control
{
    private CameraParallaxLayer[] _layers = [];
    private float _cameraZ;
    private bool _dragging;
    private float _dragMinimum;
    private float _dragMaximum;
    private float _dragValue;
    private int _lastY;
    internal string SelectedLayerId { get; private set; } = "";
    internal event EventHandler? EditStarted;
    internal event EventHandler? EditCompleted;
    internal event EventHandler? EditCanceled;
    internal event EventHandler? SelectionChanged;
    internal event EventHandler<CameraParallaxLayerEventArgs>? DepthChanged;

    public CameraDepthDiagram()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.PanelStrong;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        TabStop = true;
        AccessibleName = "Layer depth diagram";
        AccessibleDescription = "Drag depth lines · Shift: precise · Esc: cancel";
        Cursor = Cursors.SizeNS;
    }

    internal void Bind(float cameraZ, CameraParallaxLayer[] layers)
    {
        _cameraZ = cameraZ;
        _layers = layers;
        if (SelectedLayerId.Length > 0 && !layers.Any(layer => layer.Id == SelectedLayerId))
        {
            CancelGesture();
            SelectLayer("");
        }
        Invalidate();
    }

    internal void SelectLayer(string layerId)
    {
        if (SelectedLayerId == layerId) return;
        CancelGesture();
        SelectedLayerId = layerId;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private (float Minimum, float Maximum) DepthRange()
    {
        if (_dragging) return (_dragMinimum, _dragMaximum);
        var minimum = Math.Min(_cameraZ, _layers.Select(layer => layer.Depth).DefaultIfEmpty(0).Min());
        var maximum = Math.Max(_cameraZ + 1000, _layers.Select(layer => layer.Depth).DefaultIfEmpty(1000).Max());
        var margin = Math.Max(100, (maximum - minimum) * 0.1f);
        return (minimum - margin, maximum + margin);
    }

    private float PlotTop => 20f * DeviceDpi / 96f;
    private float PlotHeight => Math.Max(1, Height - 48f * DeviceDpi / 96f);
    private float DepthY(float depth)
    {
        var range = DepthRange();
        return PlotTop + (depth - range.Minimum) / Math.Max(1, range.Maximum - range.Minimum) * PlotHeight;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var dpi = DeviceDpi / 96f;
        var center = Width * 0.5f;
        var cameraY = DepthY(_cameraZ);
        var bottom = PlotTop + PlotHeight;
        using var guides = new Pen(Theme.ControlOutline, dpi);
        for (var index = 0; index < 4; index++)
        {
            var lineY = PlotTop + index * PlotHeight / 3;
            graphics.DrawLine(guides, 10 * dpi, lineY, Width - 10 * dpi, lineY);
        }
        using var frustum = new Pen(Enabled ? Theme.Accent : Theme.Muted, dpi) { DashStyle = DashStyle.Dash };
        graphics.DrawLine(frustum, center, cameraY, 18 * dpi, bottom);
        graphics.DrawLine(frustum, center, cameraY, Width - 18 * dpi, bottom);
        using var cameraBrush = new SolidBrush(Theme.Accent);
        graphics.FillEllipse(cameraBrush, center - 4 * dpi, cameraY - 4 * dpi, 8 * dpi, 8 * dpi);
        TextRenderer.DrawText(graphics, UiLocalization.T("Camera"), Font,
            new Rectangle((int)(center + 8 * dpi), (int)(cameraY - 10 * dpi), Math.Max(1, (int)(center - 14 * dpi)), (int)(20 * dpi)),
            Enabled ? ForeColor : Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        foreach (var layer in _layers.OrderBy(layer => layer.Id == SelectedLayerId))
        {
            var lineY = DepthY(layer.Depth);
            var fraction = Math.Clamp((lineY - cameraY) / Math.Max(1, bottom - cameraY), 0.08f, 1);
            var halfWidth = Math.Max(18 * dpi, (center - 18 * dpi) * fraction);
            var selected = layer.Id == SelectedLayerId;
            using var line = new Pen(layer.Enabled && Enabled ? Color.FromArgb(layer.ColorArgb) : Theme.Muted,
                (selected ? 3 : 2) * dpi) { DashStyle = layer.Enabled ? DashStyle.Solid : DashStyle.Dot };
            graphics.DrawLine(line, center - halfWidth, lineY, center + halfWidth, lineY);
            if (selected)
            {
                using var dot = new SolidBrush(line.Color);
                graphics.FillEllipse(dot, center - 4 * dpi, lineY - 4 * dpi, 8 * dpi, 8 * dpi);
            }
            if (selected || !_layers.Any(other => other.Id != layer.Id
                && Math.Abs(DepthY(other.Depth) - lineY) < 18 * dpi))
                TextRenderer.DrawText(graphics, layer.Name, Font,
                    new Rectangle((int)(8 * dpi), (int)(lineY - 21 * dpi), Math.Max(1, (int)(center - 16 * dpi)), (int)(19 * dpi)),
                    line.Color, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        }
        var selectedLayer = _layers.FirstOrDefault(layer => layer.Id == SelectedLayerId);
        var caption = selectedLayer is null ? $"{UiLocalization.T("Camera Z")}: {_cameraZ:0.#}"
            : $"{selectedLayer.Name} · Z {selectedLayer.Depth:0.#}";
        TextRenderer.DrawText(graphics, caption, Font,
            new Rectangle((int)(8 * dpi), Height - (int)(25 * dpi), Math.Max(1, Width - (int)(16 * dpi)), (int)(22 * dpi)),
            Enabled ? ForeColor : Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        if (Focused) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        var hits = _layers.Select(layer => (layer.Id, Distance: Math.Abs(DepthY(layer.Depth) - eventArgs.Y)))
            .Append((Id: "", Distance: Math.Abs(DepthY(_cameraZ) - eventArgs.Y)))
            .OrderBy(hit => hit.Item2).ThenBy(hit => hit.Id != SelectedLayerId).ToArray();
        if (hits.Length == 0 || hits[0].Item2 > 12 * DeviceDpi / 96f) return;
        SelectLayer(hits[0].Id);
        (_dragMinimum, _dragMaximum) = DepthRange();
        _dragValue = SelectedLayerId.Length == 0 ? _cameraZ : _layers.First(layer => layer.Id == SelectedLayerId).Depth;
        _lastY = eventArgs.Y;
        _dragging = true;
        Capture = true;
        EditStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        if (!_dragging) return;
        var precision = (ModifierKeys & Keys.Shift) != 0 ? 0.1f : 1f;
        _dragValue += (eventArgs.Y - _lastY) * (_dragMaximum - _dragMinimum) / PlotHeight * precision;
        _lastY = eventArgs.Y;
        DepthChanged?.Invoke(this, new CameraParallaxLayerEventArgs(SelectedLayerId, _dragValue, true));
        _dragValue = SelectedLayerId.Length == 0 ? _cameraZ
            : _layers.FirstOrDefault(layer => layer.Id == SelectedLayerId)?.Depth ?? _dragValue;
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (!_dragging || eventArgs.Button != MouseButtons.Left) return;
        _dragging = false;
        Capture = false;
        EditCompleted?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    internal void CancelGesture()
    {
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        EditCanceled?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs eventArgs)
    {
        base.OnMouseCaptureChanged(eventArgs);
        if (!Capture) CancelGesture();
    }

    protected override void OnLostFocus(EventArgs eventArgs)
    {
        CancelGesture();
        base.OnLostFocus(eventArgs);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape && _dragging) { CancelGesture(); return true; }
        if ((keyData & Keys.KeyCode) is Keys.Up or Keys.Down)
        {
            if (!_dragging) OnKeyDown(new KeyEventArgs(keyData));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if (!Enabled || eventArgs.KeyCode is not (Keys.Up or Keys.Down) || _dragging) return;
        var value = SelectedLayerId.Length == 0 ? _cameraZ : _layers.FirstOrDefault(layer => layer.Id == SelectedLayerId)?.Depth ?? 0;
        EditStarted?.Invoke(this, EventArgs.Empty);
        DepthChanged?.Invoke(this, new CameraParallaxLayerEventArgs(SelectedLayerId,
            value + (eventArgs.KeyCode == Keys.Up ? -1 : 1) * (eventArgs.Shift ? 10 : 100), true));
        EditCompleted?.Invoke(this, EventArgs.Empty);
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CancelGesture();
        base.Dispose(disposing);
    }
}
