namespace VectorAnimationEngine;

internal sealed class VectorSceneSnapshot
{
    public int LayerCount { get; init; }
    public int ObjectCount { get; init; }
    public long VirtualAtomCount { get; init; }
    public long NextObjectOrder { get; init; }
    public int ActiveLayer { get; init; }
    public float MaxHalfExtent { get; init; }
    public string[] LayerIds { get; init; } = [];
    public string[] LayerNames { get; init; } = [];
    public bool[] LayerVisible { get; init; } = [];
    public float[] LayerOpacity { get; init; } = [];
    public int[] LayerStart { get; init; } = [];
    public int[] LayerEnd { get; init; } = [];
    public ushort[] ObjectLayer { get; init; } = [];
    public int[] ObjectKeyframeFrame { get; init; } = [];
    public long[] ObjectOrder { get; init; } = [];
    public double[] ObjectSubOrder { get; init; } = [];
    public float[] X { get; init; } = [];
    public float[] Y { get; init; } = [];
    public float[] Width { get; init; } = [];
    public float[] Height { get; init; } = [];
    public float[] Angle { get; init; } = [];
    public float[] Stroke { get; init; } = [];
    public float[] CurveControlX { get; init; } = [];
    public float[] CurveControlY { get; init; } = [];
    public ShapeKind[] ShapeKind { get; init; } = [];
    public uint[] AtomCount { get; init; } = [];
    public int[] Argb { get; init; } = [];
    public int[] StrokeArgb { get; init; } = [];
    public AnimationTimelineSnapshot? Timeline { get; init; }
    public Dictionary<int, PointF[][]> PathLocalContours { get; init; } = new();
    public Dictionary<int, PointF[]> FreehandLocalPoints { get; init; } = new();
}
