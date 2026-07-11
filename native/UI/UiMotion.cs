using System.Runtime.CompilerServices;

namespace VectorAnimationEngine;

internal static class UiMotion
{
    private static readonly ConditionalWeakTable<Button, ButtonMotionState> ButtonStates = new();
    private static readonly HashSet<ButtonMotionState> RunningStates = [];
    private static readonly System.Windows.Forms.Timer Timer = new() { Interval = 16 };

    static UiMotion()
    {
        Timer.Tick += (_, _) => Tick();
    }

    public static void ConfigureButton(Button button, Color normal, Color hover, Color pressed, bool active)
    {
        ArgumentNullException.ThrowIfNull(button);
        var state = ButtonStates.GetValue(button, static control => new ButtonMotionState(control));
        state.Configure(normal, hover, pressed, active);
    }

    public static bool IsActive(Button button)
    {
        return ButtonStates.TryGetValue(button, out var state) && state.Active;
    }

    public static float HoverProgress(Button button)
    {
        return ButtonStates.TryGetValue(button, out var state) ? state.HoverProgress : 0f;
    }

    private static void Schedule(ButtonMotionState state)
    {
        RunningStates.Add(state);
        if (!Timer.Enabled) Timer.Start();
    }

    private static void Tick()
    {
        foreach (var state in RunningStates.ToArray())
        {
            if (!state.Step()) RunningStates.Remove(state);
        }

        if (RunningStates.Count == 0) Timer.Stop();
    }

    private sealed class ButtonMotionState
    {
        private readonly Button _button;
        private Color _normal;
        private Color _hover;
        private Color _pressed;
        private Color _current;
        private Color _target;
        private bool _initialized;
        private bool _hovered;
        private bool _pressedDown;
        private float _hoverProgress;
        private float _hoverTarget;

        public ButtonMotionState(Button button)
        {
            _button = button;
            _button.MouseEnter += (_, _) =>
            {
                _hovered = true;
                _hoverTarget = 1f;
                Retarget();
            };
            _button.MouseLeave += (_, _) =>
            {
                _hovered = false;
                _pressedDown = false;
                _hoverTarget = 0f;
                Retarget();
            };
            _button.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                _pressedDown = true;
                Retarget();
            };
            _button.MouseUp += (_, _) =>
            {
                _pressedDown = false;
                Retarget();
            };
            _button.EnabledChanged += (_, _) => Retarget();
            _button.Disposed += (_, _) => RunningStates.Remove(this);
        }

        public bool Active { get; private set; }
        public float HoverProgress => _hoverProgress;

        public void Configure(Color normal, Color hover, Color pressed, bool active)
        {
            _normal = normal;
            _hover = hover;
            _pressed = pressed;
            Active = active;
            if (!_initialized)
            {
                _initialized = true;
                _current = ResolveTarget();
                _target = _current;
                _button.BackColor = _current;
                _button.Invalidate();
                return;
            }

            Retarget();
        }

        public bool Step()
        {
            if (_button.IsDisposed) return false;
            _current = Mix(_current, _target, 0.30f);
            _hoverProgress += (_hoverTarget - _hoverProgress) * 0.30f;
            var settledColor = ColorDistance(_current, _target) < 1.2f;
            var settledHover = Math.Abs(_hoverProgress - _hoverTarget) < 0.015f;
            if (settledColor) _current = _target;
            if (settledHover) _hoverProgress = _hoverTarget;

            _button.BackColor = _current;
            _button.Invalidate();
            return !settledColor || !settledHover;
        }

        private void Retarget()
        {
            if (!_initialized || _button.IsDisposed) return;
            _target = ResolveTarget();
            Schedule(this);
        }

        private Color ResolveTarget()
        {
            if (!_button.Enabled) return Theme.DisabledSurface;
            if (_pressedDown) return _pressed;
            return _hovered ? _hover : _normal;
        }

        private static Color Mix(Color from, Color to, float amount)
        {
            amount = Math.Clamp(amount, 0f, 1f);
            return Color.FromArgb(
                (int)Math.Round(from.A + (to.A - from.A) * amount),
                (int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount),
                (int)Math.Round(from.B + (to.B - from.B) * amount));
        }

        private static float ColorDistance(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        }
    }
}
