using System.Diagnostics;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private const double WorkspacePreRenderSlowFrameMilliseconds = 40;
    private const double WorkspacePreRenderCooldownMilliseconds = 500;
    private readonly VectorScene _workspacePreRenderScene = new();
    private SceneDefinition? _workspacePreRenderDefinition;
    private VectorScene? _workspacePreRenderSourceScene;
    private long _workspacePreRenderRevision = long.MinValue;
    private int _workspacePreRenderCursor = int.MinValue;
    private int _workspacePreRenderAnchorFrame = int.MinValue;
    private long _workspacePreRenderNotBefore;

    private void TickWorkspacePreRender()
    {
        if (_playing
            || IsDisposed
            || !IsHandleCreated
            || WindowState == FormWindowState.Minimized
            || !_stage.Visible
            || !_stage.IsHandleCreated
            || !_stage.RendersReferenceProjection
            || _stage.ReferenceCameraTransitionActive
            || _stage.Reference3DOpticalInteractionPreviewActive
            || Stopwatch.GetTimestamp() < _workspacePreRenderNotBefore)
        {
            return;
        }

        if (_stage.SceneCompositionMaskClips.Count > 0
            || _stage.UnderlayScene is not null
            || _stage.OnionSkinScene is not null
            || _stage.DragPreviewScene is not null)
        {
            return;
        }

        var revision = _stage.Reference3DWorkspaceFrameCacheRevision;
        var definition = IsSceneBuildingContext() ? ActiveScene() : null;
        var sourceScene = _stage.Scene;
        if (!ReferenceEquals(_workspacePreRenderDefinition, definition)
            || !ReferenceEquals(_workspacePreRenderSourceScene, sourceScene)
            || _workspacePreRenderRevision != revision)
        {
            _workspacePreRenderDefinition = definition;
            _workspacePreRenderSourceScene = sourceScene;
            _workspacePreRenderRevision = revision;
            _workspacePreRenderCursor = _frame;
            _workspacePreRenderAnchorFrame = _frame;
        }
        else if (_workspacePreRenderAnchorFrame != _frame)
        {
            _workspacePreRenderCursor = _frame;
            _workspacePreRenderAnchorFrame = _frame;
        }

        if (!TryGetWorkspacePreRenderFrame(out var frame)) return;

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            if (definition is null)
            {
                _stage.TryPreRenderReference3DFrame(frame);
                return;
            }

            var composition = SceneCompositionBuilder.Build(
                _workspacePreRenderScene,
                definition,
                _drawingObjects,
                frame,
                _playbackSettings.Fps);
            _stage.TryPreRenderReference3DFrame(
                frame,
                _workspacePreRenderScene,
                composition);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Workspace pre-render skipped: {ex.Message}");
        }
        finally
        {
            if (Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
                >= WorkspacePreRenderSlowFrameMilliseconds)
            {
                _workspacePreRenderNotBefore = Stopwatch.GetTimestamp()
                    + (long)(WorkspacePreRenderCooldownMilliseconds * Stopwatch.Frequency / 1000d);
            }
        }
    }

    private bool TryGetWorkspacePreRenderFrame(out int frame)
    {
        frame = -1;
        var start = _playbackSettings.StartFrame;
        var end = _playbackSettings.EndFrame;
        if (end <= start) return false;

        var current = Math.Clamp(_frame, start, end);
        var span = end - start + 1;
        var cursor = _workspacePreRenderCursor;
        for (var offset = 1; offset <= span; offset++)
        {
            var candidate = _playbackSettings.LoopPlayback
                ? start + PositiveModulo((long)cursor - start + offset, span)
                : cursor + offset;
            if (!_playbackSettings.LoopPlayback && candidate > end) break;
            if (candidate == current) continue;

            _workspacePreRenderCursor = candidate;
            frame = candidate;
            return true;
        }

        _workspacePreRenderCursor = current;
        return false;
    }

    private static int PositiveModulo(long value, int modulus)
    {
        if (modulus <= 0) return 0;
        var result = value % modulus;
        return (int)(result < 0 ? result + modulus : result);
    }
}
