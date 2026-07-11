namespace VectorAnimationEngine;

internal interface ICompositionDefinition : ITimelineContext
{
    string Id { get; }
    string Name { get; set; }
    string Detail { get; set; }
    bool CanDraw { get; }
    IReadOnlyList<DrawingObjectInstanceDefinition> Instances { get; }
}
