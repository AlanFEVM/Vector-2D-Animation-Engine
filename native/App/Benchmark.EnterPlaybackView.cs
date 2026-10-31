namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunEnterPlaybackViewSettingsRegression()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ApplicationSettings>("{}")!;
        AssertTimeline(legacy.EnterPlaybackView == new EnterPlaybackViewSettings(),
            "Legacy settings do not preserve the current Enter playback view by default.");
        var normalized = ApplicationSettingsStore.Normalize(legacy with
        {
            EnterPlaybackView = new() { Mode = (EnterPlaybackFitMode)999, ScalePercent = 999, Margin = -1 }
        }).EnterPlaybackView;
        AssertTimeline(normalized.Mode == EnterPlaybackFitMode.KeepCurrent
            && normalized.ScalePercent == 400 && normalized.Margin == 0,
            "Enter playback settings were not normalized.");
        var settings = new EnterPlaybackViewSettings { Mode = EnterPlaybackFitMode.Fit, Margin = 20 };
        var viewport = new Size(800, 600);
        var source = new SizeF(400, 200);
        AssertTimeline(settings.ResolveBounds(viewport, source, 1) == new RectangleF(20, 110, 760, 380),
            "Fit playback did not preserve aspect ratio and margins.");
        AssertTimeline((settings with { Mode = EnterPlaybackFitMode.Fill }).ResolveBounds(viewport, source, 1)
            == new RectangleF(-160, 20, 1120, 560), "Fill playback did not crop proportionally.");
        AssertTimeline((settings with { Mode = EnterPlaybackFitMode.Custom, ScalePercent = 200 })
            .ResolveBounds(viewport, source, 1) == new RectangleF(0, 100, 800, 400),
            "Custom playback scale no longer uses source pixels.");
        AssertTimeline(settings.ResolveBounds(viewport, source, 1.5f) == new RectangleF(30, 115, 740, 370),
            "Playback margins did not follow DPI.");
        var tiny = (settings with { Margin = 160 }).ResolveBounds(new Size(16, 10), source, 1);
        AssertTimeline(tiny.Width > 0 && tiny.Height > 0 && float.IsFinite(tiny.Width),
            "A small viewport produced invalid playback bounds.");
        var scene = new VectorScene();
        scene.CreateEmpty(frameCount: 1);
        using var stage = new StageControl(scene) { ClientSize = viewport };
        stage.Pan(24, -18);
        var original = stage.CaptureViewState();
        stage.ApplyEnterPlaybackView(original, new());
        AssertTimeline(stage.CaptureViewState() == original, "Keep-current playback reset the editing view.");
        stage.ApplyEnterPlaybackView(original, settings);
        AssertTimeline(stage.CameraX == 0 && stage.CameraY == 0, "Playback fitting did not center the canvas.");
        stage.RestoreViewState(original);
        AssertTimeline(stage.CaptureViewState() == original, "Playback view restoration lost camera state.");
        Console.WriteLine("enter_playback_view_settings=ok,legacy_default=keep,fit=ok,fill=ok,custom=ok,dpi=ok,restore=ok");
    }
}
