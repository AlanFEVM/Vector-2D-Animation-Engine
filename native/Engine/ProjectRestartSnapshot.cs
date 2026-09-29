using System.Numerics;

namespace VectorAnimationEngine;

// This snapshot is an internal handoff format for a development-process restart,
// not a durable project-file contract.
internal sealed class ProjectRestartSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Untitled Project";
    public decimal PlaybackFps { get; init; } = 30m;
    public bool LoopPlayback { get; init; } = true;
    public int PlaybackStartFrame { get; init; }
    public int PlaybackEndFrame { get; init; } = 239;
    public ProjectAssetTagRestartSnapshot[] AssetTags { get; init; } = [];
    public ProjectAssetFolderRestartSnapshot[] AssetFolders { get; init; } = [];
    public ExternalSvgAssetRestartSnapshot[] ExternalSvgAssets { get; init; } = [];
    public DrawingObjectRestartSnapshot[] DrawingObjects { get; init; } = [];
    public SceneRestartSnapshot[] Scenes { get; init; } = [];
}

internal sealed class ProjectAssetTagRestartSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Tag";
    public int ColorArgb { get; init; } = Color.FromArgb(66, 165, 245).ToArgb();
}

internal sealed class ProjectAssetFolderRestartSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Folder";
    public string ParentFolderId { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}

internal sealed class ExternalSvgAssetRestartSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "SVG";
    public string SourcePath { get; init; } = "";
    public string ProjectRelativePath { get; init; } = "";
    public string LastKnownSha256 { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}

internal sealed class DrawingObjectRestartSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Symbol";
    public string Kind { get; init; } = "Symbol";
    public string Detail { get; init; } = "Reusable symbol";
    public string AssetFolderId { get; init; } = "";
    public string[] AssetTagIds { get; init; } = [];
    public float AnchorX { get; init; }
    public float AnchorY { get; init; }
    public DrawingObjectSnapPointRestartSnapshot[] SnapPoints { get; init; } = [];
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public VectorSceneSnapshot Scene { get; init; } = new();
    public InstanceRestartSnapshot[] Instances { get; init; } = [];
}

internal sealed class DrawingObjectSnapPointRestartSnapshot
{
    public string Id { get; init; } = "";
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
}

internal sealed class SceneRestartSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Scene";
    public string Detail { get; init; } = "Scene composition context";
    public SceneDimension Dimension { get; init; } = SceneDimension.TwoD;
    public SceneCameraDefinition Camera { get; init; } = new();
    public SceneLightRestartSnapshot[]? Lights { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public SceneLayerSnapshot Layers { get; init; } = new();
    public InstanceRestartSnapshot[] Instances { get; init; } = [];
    public AnimationTimelineSnapshot Timeline { get; init; } = new();
    public SceneShotSnapshot[] Shots { get; init; } = [];
    public bool OnionSkinEnabled { get; init; }
    public int OnionSkinPreviousFrames { get; init; } = VectorScene.DefaultOnionSkinPreviousFrames;
    public int OnionSkinNextFrames { get; init; } = VectorScene.DefaultOnionSkinNextFrames;
}

internal sealed class SceneLightRestartSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Light";
    public SceneLightKind Kind { get; init; }
    public bool Enabled { get; init; } = true;
    public int ColorArgb { get; init; } = unchecked((int)0xffffffff);
    public float Intensity { get; init; } = 1f;
    public float Range { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 RotationDegrees { get; init; }
    public Vector2 AreaSize { get; init; }
    public bool CastsShadows { get; init; }
    public float ShadowStrength { get; init; } = 1f;
    public float ShadowSoftness { get; init; }
    public SceneLightStateKeyframe[]? StateKeyframes { get; init; }
}

internal sealed class InstanceRestartSnapshot
{
    public string Id { get; init; } = "";
    public string DrawingObjectId { get; init; } = "";
    public string SceneLayerId { get; init; } = "";
    public string Name { get; init; } = "Instance";
    public bool Visible { get; init; } = true;
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float RotationX { get; init; }
    public float RotationY { get; init; }
    public float RotationZ { get; init; }
    public float SkewX { get; init; }
    public float SkewY { get; init; }
    public float ScaleX { get; init; } = 1;
    public float ScaleY { get; init; } = 1;
    public float ScaleZ { get; init; } = 1;
    public Vector3 RotationPivot { get; init; }
    public Vector3 ScalePivot { get; init; }
    public DistortWarp? Distortion { get; init; }
    public float Alpha { get; init; } = 1;
    public int TintArgb { get; init; } = unchecked((int)0xffffffff);
    public SymbolFilters Filters { get; init; }
    public SpatialOpticalMaterial? OpticalMaterialOverride { get; init; }
    public decimal PlaybackFps { get; init; } = 30m;
    public DrawingObjectPlaybackMode PlaybackMode { get; init; } = DrawingObjectPlaybackMode.PlayOnce;
    public int HoldFrame { get; init; }
    public InstancePositionKeyframe[] PositionKeyframes { get; init; } = [];
    public InstanceStateKeyframe[] StateKeyframes { get; init; } = [];
}
