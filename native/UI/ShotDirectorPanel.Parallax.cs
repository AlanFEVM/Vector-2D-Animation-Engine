namespace VectorAnimationEngine;

internal sealed partial class ShotDirectorPanel
{
    internal CameraParallaxPanel ParallaxEditor { get; } = new();

    internal void SetParallaxLayers(CameraParallaxLayer[] layers)
    {
        _state = _state with { ParallaxLayers = layers };
        if (ActiveShot() is { } shot)
            ParallaxEditor.Bind(_state.ActiveShotId, _state.CurrentFrame, shot.Framing, layers);
    }
}
