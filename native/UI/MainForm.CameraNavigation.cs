using System.Diagnostics;
using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private const float ReferenceCameraKeyboardPixelsPerSecond = 360f;
    private const float ReferenceCameraKeyboardAcceleration = 4f;
    private const double ReferenceCameraKeyboardSmoothingMilliseconds = 60d;
    private const float ReferenceCameraKeyboardStopVelocity = 0.5f;
    private const double ReferenceCameraKeyboardMaximumFrameMilliseconds = 64d;

    [Flags]
    private enum ReferenceCameraNavigationKeys : byte
    {
        None = 0,
        Left = 1 << 0,
        Right = 1 << 1,
        Forward = 1 << 2,
        Backward = 1 << 3
    }

    private readonly System.Windows.Forms.Timer _referenceCameraKeyboardTimer = new() { Interval = 16 };
    private ReferenceCameraNavigationKeys _referenceCameraNavigationKeys;
    private Vector2 _referenceCameraKeyboardVelocity;
    private long _referenceCameraKeyboardLastTick;
    private bool _referenceCameraRightLookPending;
    private bool _referenceCameraRightLookActive;
    private Point _referenceCameraRightLookStartScreen;
    private Point _referenceCameraRightLookLastScreen;

    internal bool ReferenceCameraKeyboardNavigationActive =>
        _referenceCameraNavigationKeys != ReferenceCameraNavigationKeys.None
        || _referenceCameraKeyboardVelocity.LengthSquared() > 0;

    private bool ReferenceCameraRightLookSessionActive =>
        _referenceCameraRightLookPending || _referenceCameraRightLookActive;

    internal static PointF? ResolveReferenceCameraKeyboardDirection(Keys keyData)
    {
        var modifiers = keyData & Keys.Modifiers;
        if (modifiers is not (Keys.None or Keys.Shift)) return null;

        return (keyData & Keys.KeyCode) switch
        {
            Keys.Left => new PointF(-1, 0),
            Keys.Right => new PointF(1, 0),
            Keys.Up => new PointF(0, 1),
            Keys.Down => new PointF(0, -1),
            _ => null
        };
    }

    internal static bool IsReferenceCameraKeyboardShortcut(Keys keyData)
        => ResolveReferenceCameraKeyboardDirection(keyData) is not null;

    private void HookReferenceCameraKeyboardNavigation()
    {
        _referenceCameraKeyboardTimer.Tick += (_, _) => TickReferenceCameraKeyboardNavigation();
        _stage.PreviewKeyDown += StageReferenceCameraPreviewKeyDown;
        _stage.KeyDown += StageReferenceCameraKeyDown;
        _stage.KeyUp += StageReferenceCameraKeyUp;
        _stage.LostFocus += (_, _) =>
        {
            CancelReferenceCameraRightLook();
            CancelReferenceCameraKeyboardNavigation();
        };
    }

    private bool HandleReferenceCameraRightLookMouseDown(MouseEventArgs e)
    {
        if (ReferenceCameraRightLookSessionActive) return true;
        if (e.Button != MouseButtons.Right || !IsScene3DView()) return false;
        if (IsAltPressed()) return false;

        // Existing edits retain their right-button cancellation path.
        if (_spatialTransformKeyboardActive
            || _spatialTransformPointerSession is not null
            || _projectedSceneMoveStartRayOrigin is not null
            || _sceneLightGizmoPointerSession is not null)
        {
            return false;
        }

        // WinForms establishes native capture before raising MouseDown. Ignore that
        // capture for this admission check; the other interaction state still owns
        // right-button cancellation and blocks camera look as usual.
        if (HasActiveCanvasPointerInteraction(includeCapture: false) || _brushColorPaletteActive) return true;
        if (IsTextEditActive() && !CommitTextEdit()) return true;

        CancelReferenceCameraKeyboardNavigation();
        _stage.Focus();
        _pendingHoverScreen = null;
        _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
        _stage.SetSceneLightGizmoHover(SceneLightGizmoHandleHit.None);

        _referenceCameraRightLookPending = true;
        _referenceCameraRightLookActive = false;
        _referenceCameraRightLookStartScreen = e.Location;
        _referenceCameraRightLookLastScreen = e.Location;
        _stage.Capture = true;
        _lastMouse = e.Location;
        _startScreen = e.Location;
        _startWorld = null;
        return true;
    }

    private bool HandleReferenceCameraRightLookMouseMove(MouseEventArgs e)
    {
        if (!ReferenceCameraRightLookSessionActive) return false;
        if (!IsScene3DView() || (e.Button & MouseButtons.Right) == 0)
        {
            EndReferenceCameraRightLook();
            return true;
        }

        var screen = e.Location;
        var dx = screen.X - _referenceCameraRightLookLastScreen.X;
        var dy = screen.Y - _referenceCameraRightLookLastScreen.Y;
        _lastMouse = screen;

        if (!_referenceCameraRightLookActive
            && !ReferenceCameraRightLookDragExceeded(
                _referenceCameraRightLookStartScreen,
                screen))
        {
            return true;
        }

        if (!_referenceCameraRightLookActive)
        {
            _referenceCameraRightLookPending = false;
            _referenceCameraRightLookActive = true;
            _stage.BeginReference3DOpticalInteractionPreview();
            _stage.Cursor = Cursors.SizeAll;
        }

        _referenceCameraRightLookLastScreen = screen;
        if (dx != 0 || dy != 0) _stage.LookAroundReferenceCamera(dx, dy);
        return true;
    }

    private bool HandleReferenceCameraRightLookMouseUp(MouseEventArgs e)
    {
        if (!ReferenceCameraRightLookSessionActive) return false;
        if (e.Button != MouseButtons.Right) return true;

        HandleReferenceCameraRightLookMouseMove(e);
        var showContextMenu = _referenceCameraRightLookPending
            && _stage.ClientRectangle.Contains(e.Location);
        EndReferenceCameraRightLook();
        if (showContextMenu) ShowStageContextMenu(e.Location);
        return true;
    }

    private static bool ReferenceCameraRightLookDragExceeded(Point start, Point current)
    {
        var dragSize = SystemInformation.DragSize;
        var thresholdX = Math.Max(1, dragSize.Width / 2);
        var thresholdY = Math.Max(1, dragSize.Height / 2);
        return Math.Abs(current.X - start.X) > thresholdX
            || Math.Abs(current.Y - start.Y) > thresholdY;
    }

    private void EndReferenceCameraRightLook()
    {
        if (!ReferenceCameraRightLookSessionActive) return;
        var wasActive = _referenceCameraRightLookActive;
        _referenceCameraRightLookPending = false;
        _referenceCameraRightLookActive = false;
        _referenceCameraRightLookStartScreen = Point.Empty;
        _referenceCameraRightLookLastScreen = Point.Empty;
        _lastMouse = null;
        _startScreen = null;
        _startWorld = null;
        if (wasActive) _stage.EndReference3DOpticalInteractionPreview();
        if (_stage.Capture) _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private void CancelReferenceCameraRightLook() => EndReferenceCameraRightLook();

    private void StageReferenceCameraPreviewKeyDown(object? sender, PreviewKeyDownEventArgs e)
    {
        if (CanAcceptReferenceCameraKeyboardNavigation()
            && IsReferenceCameraKeyboardShortcut(e.KeyData))
        {
            e.IsInputKey = true;
        }
    }

    private void StageReferenceCameraKeyDown(object? sender, KeyEventArgs e)
    {
        if (!BeginReferenceCameraKeyboardNavigation(e.KeyData)) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void StageReferenceCameraKeyUp(object? sender, KeyEventArgs e)
    {
        if (!EndReferenceCameraKeyboardNavigation(e.KeyCode)) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    internal bool BeginReferenceCameraKeyboardNavigation(Keys keyData)
    {
        if (!CanAcceptReferenceCameraKeyboardNavigation()
            || ResolveReferenceCameraNavigationKey(keyData) is not { } navigationKey)
        {
            return false;
        }

        _referenceCameraNavigationKeys |= navigationKey;
        _stage.BeginReference3DOpticalInteractionPreview();
        if (!_referenceCameraKeyboardTimer.Enabled)
        {
            _referenceCameraKeyboardLastTick = Stopwatch.GetTimestamp();
            _referenceCameraKeyboardTimer.Start();
        }
        return true;
    }

    internal bool EndReferenceCameraKeyboardNavigation(Keys keyCode)
    {
        if (ResolveReferenceCameraNavigationKey(keyCode, ignoreModifiers: true) is not { } navigationKey
            || (_referenceCameraNavigationKeys & navigationKey) == 0)
        {
            return false;
        }

        _referenceCameraNavigationKeys &= ~navigationKey;
        if (_referenceCameraNavigationKeys == ReferenceCameraNavigationKeys.None
            && (!UiMotion.AnimationsEnabled
                || _referenceCameraKeyboardVelocity.Length() <= ReferenceCameraKeyboardStopVelocity))
        {
            CancelReferenceCameraKeyboardNavigation();
        }
        return true;
    }

    internal void CancelReferenceCameraKeyboardNavigation()
    {
        _referenceCameraKeyboardTimer.Stop();
        _referenceCameraNavigationKeys = ReferenceCameraNavigationKeys.None;
        _referenceCameraKeyboardVelocity = Vector2.Zero;
        _referenceCameraKeyboardLastTick = 0;
        _stage.EndReference3DOpticalInteractionPreview();
    }

    private void TickReferenceCameraKeyboardNavigation()
    {
        if (!CanContinueReferenceCameraKeyboardNavigation())
        {
            CancelReferenceCameraKeyboardNavigation();
            return;
        }

        var modifiers = ModifierKeys;
        if ((modifiers & (Keys.Control | Keys.Alt)) != Keys.None)
        {
            CancelReferenceCameraKeyboardNavigation();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsedMilliseconds = _referenceCameraKeyboardLastTick == 0
            ? _referenceCameraKeyboardTimer.Interval
            : Math.Clamp(
                Stopwatch.GetElapsedTime(_referenceCameraKeyboardLastTick, now).TotalMilliseconds,
                1d,
                ReferenceCameraKeyboardMaximumFrameMilliseconds);
        _referenceCameraKeyboardLastTick = now;
        AdvanceReferenceCameraKeyboardNavigation(
            elapsedMilliseconds,
            accelerated: (modifiers & Keys.Shift) != Keys.None);
    }

    internal bool AdvanceReferenceCameraKeyboardNavigation(double elapsedMilliseconds, bool accelerated)
    {
        elapsedMilliseconds = Math.Clamp(
            elapsedMilliseconds,
            0,
            ReferenceCameraKeyboardMaximumFrameMilliseconds);
        if (elapsedMilliseconds <= 0) return ReferenceCameraKeyboardNavigationActive;

        var direction = ResolveReferenceCameraKeyboardDirection(_referenceCameraNavigationKeys);
        var speed = ReferenceCameraKeyboardPixelsPerSecond
            * (accelerated ? ReferenceCameraKeyboardAcceleration : 1f);
        var targetVelocity = direction * speed;
        var elapsedSeconds = (float)(elapsedMilliseconds / 1000d);
        Vector2 displacement;
        if (!UiMotion.AnimationsEnabled)
        {
            _referenceCameraKeyboardVelocity = targetVelocity;
            displacement = targetVelocity * elapsedSeconds;
        }
        else
        {
            var smoothingSeconds = (float)(ReferenceCameraKeyboardSmoothingMilliseconds / 1000d);
            var decay = MathF.Exp(-elapsedSeconds / smoothingSeconds);
            var velocityDelta = _referenceCameraKeyboardVelocity - targetVelocity;
            displacement = targetVelocity * elapsedSeconds
                + velocityDelta * (smoothingSeconds * (1 - decay));
            _referenceCameraKeyboardVelocity = targetVelocity + velocityDelta * decay;
        }

        if (displacement.LengthSquared() > 0)
        {
            _stage.TranslateReferenceCameraLocalByPixels(displacement.X, displacement.Y);
        }

        if (_referenceCameraNavigationKeys == ReferenceCameraNavigationKeys.None
            && _referenceCameraKeyboardVelocity.Length() <= ReferenceCameraKeyboardStopVelocity)
        {
            CancelReferenceCameraKeyboardNavigation();
            return false;
        }
        return true;
    }

    private bool CanAcceptReferenceCameraKeyboardNavigation()
    {
        return IsScene3DView()
            && _stage.ContainsFocus
            && !HasActiveCanvasPointerInteraction();
    }

    private bool CanContinueReferenceCameraKeyboardNavigation()
    {
        return ReferenceCameraKeyboardNavigationActive
            && CanAcceptReferenceCameraKeyboardNavigation();
    }

    private static ReferenceCameraNavigationKeys? ResolveReferenceCameraNavigationKey(
        Keys keyData,
        bool ignoreModifiers = false)
    {
        if (!ignoreModifiers)
        {
            var modifiers = keyData & Keys.Modifiers;
            if (modifiers is not (Keys.None or Keys.Shift)) return null;
        }

        return (keyData & Keys.KeyCode) switch
        {
            Keys.Left => ReferenceCameraNavigationKeys.Left,
            Keys.Right => ReferenceCameraNavigationKeys.Right,
            Keys.Up => ReferenceCameraNavigationKeys.Forward,
            Keys.Down => ReferenceCameraNavigationKeys.Backward,
            _ => null
        };
    }

    private static Vector2 ResolveReferenceCameraKeyboardDirection(ReferenceCameraNavigationKeys keys)
    {
        var direction = new Vector2(
            (keys.HasFlag(ReferenceCameraNavigationKeys.Right) ? 1 : 0)
                - (keys.HasFlag(ReferenceCameraNavigationKeys.Left) ? 1 : 0),
            (keys.HasFlag(ReferenceCameraNavigationKeys.Forward) ? 1 : 0)
                - (keys.HasFlag(ReferenceCameraNavigationKeys.Backward) ? 1 : 0));
        return direction.LengthSquared() > 1 ? Vector2.Normalize(direction) : direction;
    }
}
