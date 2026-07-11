namespace VectorAnimationEngine;

internal sealed class SceneDefinition : ITimelineContext, ICompositionDefinition
{
    private readonly AnimationTimeline _timeline = new();
    private readonly List<DrawingObjectInstanceDefinition> _instances = [];
    private readonly IReadOnlyList<DrawingObjectInstanceDefinition> _instanceView;

    public SceneDefinition()
    {
        _instanceView = _instances.AsReadOnly();
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Scene";
    public string Detail { get; set; } = "Scene composition context";
    public bool CanDraw => false;
    public SceneDimension Dimension { get; set; } = SceneDimension.TwoD;
    public SceneCameraDefinition Camera { get; set; } = new();
    public IReadOnlyList<DrawingObjectInstanceDefinition> Instances => _instanceView;
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public AnimationTimeline Timeline
    {
        get
        {
            SynchronizeTimelineTracks();
            return _timeline;
        }
    }

    public int FrameCount => _timeline.Tracks.Count == 0 ? AnimationTimeline.DefaultDuration : _timeline.Duration;
    public IReadOnlyList<string> TimelineTargetIds => Instances.Select(instance => instance.Id).ToArray();

    public void SynchronizeTimelineTracks()
    {
        _timeline.SynchronizeTracks(Instances.Select(instance => instance.Id), FrameCount);
    }

    internal void AddInstance(VectorProject project, DrawingObjectInstanceDefinition instance)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(instance);
        if (!project.OwnsScene(this)
            || !project.ContainsDrawingObject(instance.DrawingObjectId)
            || string.IsNullOrWhiteSpace(instance.Id)
            || _instances.Any(item => string.Equals(item.Id, instance.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The scene instance reference or identifier is invalid for this project.");
        }

        _instances.Add(instance);
        SynchronizeTimelineTracks();
    }

    internal bool RemoveInstance(DrawingObjectInstanceDefinition instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var removed = _instances.Remove(instance);
        if (removed) SynchronizeTimelineTracks();
        return removed;
    }
}
