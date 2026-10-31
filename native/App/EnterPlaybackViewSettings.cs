namespace VectorAnimationEngine;

internal enum EnterPlaybackFitMode
{
    KeepCurrent,
    Fit,
    Fill,
    Custom
}

internal sealed record EnterPlaybackViewSettings
{
    public EnterPlaybackFitMode Mode { get; init; } = EnterPlaybackFitMode.KeepCurrent;
    public int ScalePercent { get; init; } = 100;
    public int Margin { get; init; } = 24;
    public bool RestoreView { get; init; } = true;

    internal EnterPlaybackViewSettings Normalized() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : EnterPlaybackFitMode.KeepCurrent,
        ScalePercent = Math.Clamp(ScalePercent, 10, 400),
        Margin = Math.Clamp(Margin, 0, 160)
    };

    internal RectangleF ResolveBounds(Size viewport, SizeF source, float dpiScale)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || source.Width <= 0 || source.Height <= 0
            || !float.IsFinite(source.Width) || !float.IsFinite(source.Height)) return RectangleF.Empty;
        var settings = Normalized();
        var margin = Math.Min(settings.Margin * dpiScale,
            Math.Max(0, (Math.Min(viewport.Width, viewport.Height) - 1) * 0.5f));
        var widthScale = Math.Max(1, viewport.Width - margin * 2) / source.Width;
        var heightScale = Math.Max(1, viewport.Height - margin * 2) / source.Height;
        var scale = settings.Mode switch
        {
            EnterPlaybackFitMode.Fit => Math.Min(widthScale, heightScale),
            EnterPlaybackFitMode.Fill => Math.Max(widthScale, heightScale),
            _ => 1f
        } * settings.ScalePercent / 100f;
        var width = source.Width * scale;
        var height = source.Height * scale;
        return new RectangleF((viewport.Width - width) * 0.5f, (viewport.Height - height) * 0.5f, width, height);
    }
}
