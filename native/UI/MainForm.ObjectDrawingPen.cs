namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private DrawingToolCommit? _objectDrawingPenCommit;
    private readonly List<int> _objectDrawingPenObjects = [];

    private bool BeginObjectDrawingPenSegment()
    {
        if (_objectDrawingPenCommit is not null) return true;
        if (!ObjectDrawingActive) return false;
        _objectDrawingPenCommit = BeginDrawingToolCommit();
        _objectDrawingPenObjects.Clear();
        return true;
    }

    private void CompleteObjectDrawingPenPath()
    {
        if (_objectDrawingPenCommit is not { } commit) return;
        var objects = _objectDrawingPenObjects.ToArray();
        _objectDrawingPenCommit = null;
        _objectDrawingPenObjects.Clear();
        CompleteObjectDrawingCommit(commit, objects);
    }
}
