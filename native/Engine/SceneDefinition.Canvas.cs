namespace VectorAnimationEngine;

internal sealed partial class SceneDefinition
{
    public SceneCanvasSettings? Canvas { get; private set; }
    public bool CanvasWelcomeDismissed { get; private set; }

    public bool TrySetCanvas(SceneCanvasSettings? settings)
    {
        if (settings is { IsValid: false } || Canvas == settings)
        {
            return false;
        }

        Canvas = settings;
        return true;
    }

    public void DismissCanvasWelcome()
    {
        CanvasWelcomeDismissed = true;
    }

    internal void RestoreCanvas(SceneCanvasSettings? settings, bool welcomeDismissed)
    {
        if (settings is { IsValid: false })
        {
            throw new InvalidDataException("The scene canvas settings are invalid.");
        }

        Canvas = settings;
        CanvasWelcomeDismissed = welcomeDismissed;
    }
}
