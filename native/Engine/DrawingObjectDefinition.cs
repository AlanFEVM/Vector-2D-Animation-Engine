namespace VectorAnimationEngine;

internal sealed record DrawingObjectDragData(string ProjectId, string DrawingObjectId);

internal sealed class DrawingObjectDefinition : ICompositionDefinition
{
    private readonly List<DrawingObjectInstanceDefinition> _instances = [];
    private readonly IReadOnlyList<DrawingObjectInstanceDefinition> _instanceView;

    public DrawingObjectDefinition()
    {
        _instanceView = _instances.AsReadOnly();
        Scene.ConfigureAdditionalTimelineTargets(() => _instanceView.Select(instance => instance.Id).ToArray());
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Drawing Object";
    public string Kind { get; set; } = "Symbol";
    public string Detail { get; set; } = "Reusable drawing object";
    public bool CanDraw => true;
    public VectorScene Scene { get; } = new();
    public IReadOnlyList<DrawingObjectInstanceDefinition> Instances => _instanceView;
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public AnimationTimeline Timeline => Scene.Timeline;
    public IReadOnlyList<string> TimelineTargetIds => Scene.TimelineTargetIds;

    public AnimationTimeline InstanceTimeline
    {
        get
        {
            SynchronizeInstanceTimelineTracks();
            return Timeline;
        }
    }

    public int FrameCount => Scene.FrameCount;

    public void SynchronizeTimelineTracks() => Scene.SynchronizeTimelineTracks();

    public void SynchronizeInstanceTimelineTracks() => SynchronizeTimelineTracks();

    internal void AddInstance(VectorProject project, DrawingObjectInstanceDefinition instance)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(instance);
        if (!project.OwnsDrawingObject(this)
            || !project.CanContainDrawingObject(Id, instance.DrawingObjectId)
            || string.IsNullOrWhiteSpace(instance.Id)
            || Scene.LayerIds.Contains(instance.Id, StringComparer.Ordinal)
            || _instances.Any(item => string.Equals(item.Id, instance.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The drawing-object instance would create an invalid or recursive containment relationship.");
        }

        _instances.Add(instance);
        SynchronizeInstanceTimelineTracks();
    }

    internal int RemoveInstancesReferencing(string drawingObjectId)
    {
        if (string.IsNullOrWhiteSpace(drawingObjectId)) return 0;
        var removed = _instances.RemoveAll(instance =>
            string.Equals(instance.DrawingObjectId, drawingObjectId, StringComparison.Ordinal));
        if (removed > 0) SynchronizeInstanceTimelineTracks();
        return removed;
    }

    public VaultItem ToVaultItem()
    {
        var payload = string.Join(Environment.NewLine, new[]
        {
            $"DrawingObjectId: {Id}",
            $"Name: {Name}",
            $"Kind: {Kind}",
            $"CreatedAt: {CreatedAt:O}",
            Detail
        });

        return new VaultItem
        {
            Kind = "Drawing Object",
            Name = Name,
            Detail = Detail,
            Payload = payload,
            ReferenceKind = "DrawingObject",
            ReferenceId = Id
        };
    }
}
