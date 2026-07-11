namespace VectorAnimationEngine;

internal struct FixedStepBatcher
{
    private double _accumulator;

    public FixedStepBatcher(double stepsPerSecond)
    {
        if (!double.IsFinite(stepsPerSecond) || stepsPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(stepsPerSecond));
        StepSeconds = 1.0 / stepsPerSecond;
    }

    public double StepSeconds { get; }

    public int Consume(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return 0;
        _accumulator += elapsedSeconds;
        var steps = (int)(_accumulator / StepSeconds);
        if (steps > 0) _accumulator -= steps * StepSeconds;
        return steps;
    }

    public void Reset() => _accumulator = 0;
}
