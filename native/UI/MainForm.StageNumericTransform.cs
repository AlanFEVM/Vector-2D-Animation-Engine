using System.Globalization;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private char _stageNumericMode;
    private char _stageNumericAxis;
    private string _stageNumericInput = "";
    private Label? _stageNumericPrompt;
    private VectorScene? _stageNumericScene;
    private int _stageNumericFrame;

    private bool BeginStageNumericTransform(char mode)
    {
        if (IsScene3DView() || _playing || HasActiveCanvasPointerInteraction() || StageSelectionBounds() is null) return false;
        _stageNumericMode = mode;
        _stageNumericAxis = '\0';
        _stageNumericInput = "";
        _stageNumericScene = _scene;
        _stageNumericFrame = _frame;
        RefreshStageNumericPrompt();
        _stage.Focus();
        return true;
    }

    private void RefreshStageNumericPrompt(bool invalid = false)
    {
        if (_stageNumericPrompt is null)
        {
            _stageNumericPrompt = new Label { AutoSize = true, Padding = new Padding(8), UseMnemonic = false };
            _stage.Controls.Add(_stageNumericPrompt);
        }
        var name = _stageNumericMode == 'G' ? "Move" : _stageNumericMode == 'R' ? "Rotate" : "Scale";
        _stageNumericPrompt.BackColor = Theme.Panel;
        _stageNumericPrompt.ForeColor = Theme.Text;
        _stageNumericPrompt.Text = $"{UiLocalization.T(name)}  {_stageNumericAxis}  {_stageNumericInput}  "
            + UiLocalization.T("X/Y: axis; Enter: confirm; Esc: cancel")
            + (invalid ? "  " + UiLocalization.T("Invalid value") : "");
        _stageNumericPrompt.Location = new Point(54, 8);
        _stageNumericPrompt.Visible = true;
        _stageNumericPrompt.BringToFront();
    }

    private void CancelStageNumericTransform()
    {
        _stageNumericMode = '\0';
        _stageNumericScene = null;
        if (_stageNumericPrompt is not null) _stageNumericPrompt.Visible = false;
    }

    private bool HandleStageNumericTransformKey(Keys keyData)
    {
        if (_stageNumericMode == '\0') return false;
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Escape) { CancelStageNumericTransform(); return true; }
        if (key == Keys.Enter) { CommitStageNumericTransform(); return true; }
        if ((keyData & (Keys.Control | Keys.Alt)) != 0) return true;
        if (key is Keys.X or Keys.Y)
        { _stageNumericAxis = key == Keys.X ? 'X' : 'Y'; RefreshStageNumericPrompt(); return true; }
        if (key == Keys.Back)
        { if (_stageNumericInput.Length > 0) _stageNumericInput = _stageNumericInput[..^1]; }
        else
        {
            var text = key is >= Keys.D0 and <= Keys.D9 ? ((int)key - (int)Keys.D0).ToString(CultureInfo.InvariantCulture)
                : key is >= Keys.NumPad0 and <= Keys.NumPad9 ? ((int)key - (int)Keys.NumPad0).ToString(CultureInfo.InvariantCulture)
                : key is Keys.OemPeriod or Keys.Decimal ? "."
                : key is Keys.OemMinus or Keys.Subtract ? "-"
                : key == Keys.Oemcomma ? "," : "";
            if (_stageNumericInput.Length < 64) _stageNumericInput += text;
        }
        RefreshStageNumericPrompt();
        return true;
    }

    private bool CommitStageNumericTransform()
    {
        if (_stageNumericMode == '\0') return false;
        if (!ReferenceEquals(_stageNumericScene, _scene) || _stageNumericFrame != _frame)
        { CancelStageNumericTransform(); return false; }
        var parts = _stageNumericInput.Split(',');
        var values = new List<float>();
        foreach (var part in parts)
        {
            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !float.IsFinite(value) || Math.Abs(value) > 1_000_000)
            { RefreshStageNumericPrompt(invalid: true); return false; }
            values.Add(value);
        }
        if (values.Count > (_stageNumericMode == 'R' ? 1 : 2)
            || _stageNumericMode == 'S' && values.Any(value => Math.Abs(value) > 1000 || Math.Abs(value) < 0.0001f))
        { RefreshStageNumericPrompt(invalid: true); return false; }
        if (StageSelectionBounds() is not { } bounds) { CancelStageNumericTransform(); return false; }
        var mode = _stageNumericMode;
        var axis = _stageNumericAxis;
        var x = values[0];
        var y = values.Count > 1 ? values[1] : mode == 'S' ? x : 0;
        if (axis == 'Y') { y = x; x = mode == 'S' ? 1 : 0; }
        if (axis == 'X') y = mode == 'S' ? 1 : 0;
        var pivot = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var radians = values[0] % 360 * MathF.PI / 180;
        PointF Transform(PointF point)
        {
            if (mode == 'G') return new(point.X + VectorUnits.FromPixels(x), point.Y + VectorUnits.FromPixels(y));
            var dx = point.X - pivot.X;
            var dy = point.Y - pivot.Y;
            if (mode == 'S') return new(pivot.X + dx * x, pivot.Y + dy * y);
            return new(pivot.X + dx * MathF.Cos(radians) - dy * MathF.Sin(radians),
                pivot.Y + dx * MathF.Sin(radians) + dy * MathF.Cos(radians));
        }
        CancelStageNumericTransform();
        return EditStageSelection(targets =>
        {
            var motionTargets = _scene.ExpandMotionTransformObjects(targets, _frame).Where(i => !_scene.ObjectLocked[i]).ToArray();
            var session = _scene.BeginTransformSession(motionTargets);
            _scene.ApplyTransformSession(session, Transform, convertPrimitivesToPaths: mode == 'S', rebuildGeometryIndex: false);
        }, (instance, frame) =>
        {
            var state = instance.EvaluateState(frame);
            var position = Transform(state.Position);
            instance.SetStateAtFrame(frame, state with { X = VectorUnits.Quantize(position.X), Y = VectorUnits.Quantize(position.Y),
                RotationZ = mode == 'R' ? state.RotationZ + values[0] : state.RotationZ,
                ScaleX = mode == 'S' ? state.ScaleX * x : state.ScaleX,
                ScaleY = mode == 'S' ? state.ScaleY * y : state.ScaleY });
        });
    }
}
