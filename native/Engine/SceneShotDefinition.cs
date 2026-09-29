using System.Numerics;

namespace VectorAnimationEngine;

/// <summary>
/// The editing intent of a director camera. Both kinds keep the full 3D scene and real perspective
/// depth; the kind decides which channels the operator edits.
/// </summary>
/// <remarks>
/// <see cref="ThreeD"/> is deliberately zero so documents written before this field existed
/// deserialize as a full 3D camera. Any other ordering would silently pin X/Y rotation on every
/// legacy camera the moment it was loaded.
/// </remarks>
internal enum SceneShotCameraKind
{
    /// <summary>Full 3D camera with free XYZ position and three-axis rotation.</summary>
    ThreeD = 0,

    /// <summary>
    /// Flat stage camera. It pans on X/Y, dollies along Z (keeping depth and perspective size change),
    /// and rotates around Z only. X/Y rotation is pinned so the horizon cannot tilt away from the
    /// stage plane.
    /// </summary>
    TwoD = 1
}

/// <summary>Stable serialized presets; zero preserves framing in legacy documents.</summary>
internal enum SceneShotAspectRatio
{
    FollowScene = 0,
    Landscape16To9 = 1,
    Portrait9To16 = 2,
    Standard4To3 = 3,
    Photo3To2 = 4,
    Square1To1 = 5,
    Portrait4To5 = 6,
    Ultrawide21To9 = 7
}

/// <summary>
/// Animated state of one director camera. A camera is an independent 3D object that observes the
/// single scene; it never owns, filters, or reassigns scene animation layers.
/// </summary>
internal readonly record struct SceneShotSettings
{
    internal const float MinimumZoom = 0.01f;
    internal const float MaximumZoom = 100f;
    internal const float MinimumFocalLength = 1f;
    internal const float MaximumFocalLength = 5_000f;
    internal const float MinimumFrameWidth = 1920f;
    internal const float MinimumFrameHeight = 1080f;
    internal const float MinimumOrthographicSize = MinimumFrameHeight;
    internal const float MaximumOrthographicSize = 5_000_000f;
    internal const float MaximumPosition = 5_000_000f;
    internal const float MaximumRotation = 360_000f;

    /// <summary>
    /// Reference stand-off that a default camera sits at. Perspective framing is scaled relative to
    /// it, so the default camera frames the scene exactly as before while a Z dolly changes coverage.
    /// </summary>
    internal const float DefaultStandOffDistance = 1_000f;

    // Zoom and RotationDegrees are retained as a small compatibility surface for the old 2D
    // framing preview. Camera editing uses the full 3D fields below.
    public SceneShotCameraKind Kind { get; init; } = SceneShotCameraKind.ThreeD;
    public SceneShotAspectRatio AspectRatio { get; init; } = SceneShotAspectRatio.FollowScene;
    public CameraProjection Projection { get; init; } = CameraProjection.Perspective;
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; } = -1_000f;
    public float RotationX { get; init; }
    public float RotationY { get; init; }
    public float RotationDegrees { get; init; }
    public float Zoom { get; init; } = 1f;
    public float FocalLength { get; init; } = 50f;
    public float OrthographicSize { get; init; } = 28_000f;

    public static SceneShotSettings Default => new()
    {
        Kind = SceneShotCameraKind.ThreeD,
        Projection = CameraProjection.Perspective,
        X = 0f,
        Y = 0f,
        Z = -1_000f,
        RotationX = 0f,
        RotationY = 0f,
        RotationDegrees = 0f,
        Zoom = 1f,
        FocalLength = 50f,
        OrthographicSize = 28_000f
    };

    /// <summary>Legacy four-value constructor used by the old 2D preview and old documents.</summary>
    public SceneShotSettings(float x, float y, float zoom, float rotationDegrees) : this()
    {
        X = x;
        Y = y;
        Zoom = zoom;
        RotationDegrees = rotationDegrees;
        FocalLength = 50f;
        OrthographicSize = 28_000f;
    }

    public SceneShotSettings(
        CameraProjection projection,
        Vector3 position,
        Vector3 rotationDegrees,
        float focalLength,
        float orthographicSize) : this()
    {
        Projection = projection;
        X = position.X;
        Y = position.Y;
        Z = position.Z;
        RotationX = rotationDegrees.X;
        RotationY = rotationDegrees.Y;
        RotationDegrees = rotationDegrees.Z;
        FocalLength = focalLength;
        OrthographicSize = orthographicSize;
        Zoom = projection == CameraProjection.Orthographic
            ? 28_000f / Math.Max(MinimumOrthographicSize, orthographicSize)
            : focalLength / 50f;
    }

    public Vector3 Position => new(X, Y, Z);

    public Vector3 Rotation => new(RotationX, RotationY, RotationDegrees);

    public float RotationZ => RotationDegrees;

    /// <summary>
    /// A 2D camera keeps perspective depth along Z but may only rotate around Z. X/Y rotation is
    /// pinned to zero so the framed plane stays parallel to the stage.
    /// </summary>
    public bool PinsPlanarRotation => Kind == SceneShotCameraKind.TwoD;

    /// <summary>True when the camera exposes editable X/Y rotation channels.</summary>
    public bool AllowsPlanarRotationEdits => Kind == SceneShotCameraKind.ThreeD;

    /// <summary>
    /// Applies the kind invariant: a 2D camera drops any X/Y rotation while keeping its Z position
    /// so perspective depth and dolly zoom remain intact.
    /// </summary>
    internal SceneShotSettings EnforceKind()
    {
        if (!PinsPlanarRotation) return this;
        if (RotationX == 0f && RotationY == 0f) return this;
        return this with { RotationX = 0f, RotationY = 0f };
    }

    public bool IsValid => Enum.IsDefined(Kind)
        && Enum.IsDefined(Projection)
        && Enum.IsDefined(AspectRatio)
        && FiniteInRange(X, MaximumPosition)
        && FiniteInRange(Y, MaximumPosition)
        && FiniteInRange(Z, MaximumPosition)
        && FiniteInRange(RotationX, MaximumRotation)
        && FiniteInRange(RotationY, MaximumRotation)
        && FiniteInRange(RotationDegrees, MaximumRotation)
        && float.IsFinite(Zoom)
        && Zoom is >= MinimumZoom and <= MaximumZoom
        && float.IsFinite(FocalLength)
        && FocalLength is >= MinimumFocalLength and <= MaximumFocalLength
        && float.IsFinite(OrthographicSize)
        // Accept legacy positive sizes at the decoding boundary, then normalize them to the new limit.
        && OrthographicSize is >= 0.01f and <= MaximumOrthographicSize;

    internal SceneShotSettings Normalized()
    {
        var kind = Enum.IsDefined(Kind) ? Kind : SceneShotCameraKind.ThreeD;
        var projection = Enum.IsDefined(Projection) ? Projection : CameraProjection.Perspective;
        var focalLength = Math.Clamp(
            float.IsFinite(FocalLength) ? FocalLength : 50f,
            MinimumFocalLength,
            MaximumFocalLength);
        var orthographicSize = Math.Clamp(
            float.IsFinite(OrthographicSize) ? OrthographicSize : 28_000f,
            MinimumOrthographicSize,
            MaximumOrthographicSize);
        return new SceneShotSettings
        {
            Kind = kind,
            AspectRatio = Enum.IsDefined(AspectRatio) ? AspectRatio : SceneShotAspectRatio.FollowScene,
            Projection = projection,
            X = ClampPosition(X),
            Y = ClampPosition(Y),
            Z = ClampPosition(Z),
            RotationX = kind == SceneShotCameraKind.TwoD ? 0f : NormalizeAngle(RotationX),
            RotationY = kind == SceneShotCameraKind.TwoD ? 0f : NormalizeAngle(RotationY),
            RotationDegrees = NormalizeAngle(RotationDegrees),
            Zoom = Math.Clamp(float.IsFinite(Zoom) ? Zoom : 1f, MinimumZoom, MaximumZoom),
            FocalLength = focalLength,
            OrthographicSize = orthographicSize
        };
    }

    internal static SceneShotSettings Interpolate(
        SceneShotSettings source,
        SceneShotSettings target,
        float progress)
    {
        progress = float.IsFinite(progress) ? Math.Clamp(progress, 0f, 1f) : 0f;
        source = source.Normalized();
        target = target.Normalized();
        if (progress <= 0f) return source;
        if (progress >= 1f) return target;

        return new SceneShotSettings
        {
            // Kind and Projection are discrete camera modes. They change at the destination keyframe
            // while every numeric camera property follows the same Classic tween. Interpolating from
            // a 2D to a 3D camera keeps Z depth and Z rotation continuous and starts planar rotation
            // from the pinned zero baseline.
            Kind = source.Kind,
            AspectRatio = source.AspectRatio,
            Projection = source.Projection,
            X = Lerp(source.X, target.X, progress),
            Y = Lerp(source.Y, target.Y, progress),
            Z = Lerp(source.Z, target.Z, progress),
            RotationX = LerpAngle(source.RotationX, target.RotationX, progress),
            RotationY = LerpAngle(source.RotationY, target.RotationY, progress),
            RotationDegrees = LerpAngle(source.RotationDegrees, target.RotationDegrees, progress),
            Zoom = Lerp(source.Zoom, target.Zoom, progress),
            FocalLength = Lerp(source.FocalLength, target.FocalLength, progress),
            OrthographicSize = Lerp(source.OrthographicSize, target.OrthographicSize, progress)
        };
    }

    /// <summary>
    /// Resolves the framed viewport as world-space corners using the camera's real 3D orientation.
    /// A perspective camera keeps its focal-length field of view and its distance along the view
    /// direction, so a dolly changes the framed extent exactly as it does on the Stage. A 2D camera
    /// has its X/Y rotation pinned, which makes this plane parallel to the stage.
    /// </summary>
    internal (Vector3 Center, Vector3 Right, Vector3 Up, float HalfWidth, float HalfHeight) ResolveWorldFrame(
        float canvasWidth,
        float canvasHeight)
    {
        var normalized = Normalized();
        var width = Math.Max(1f, canvasWidth);
        var height = Math.Max(1f, canvasHeight);
        var aspect = ResolveAspectRatio(width, height);
        var rotation = SpatialTransformMath.CreateRotation(
            normalized.RotationX,
            normalized.RotationY,
            normalized.RotationDegrees);
        var right = NormalizeOr(Vector3.TransformNormal(Vector3.UnitX, rotation), Vector3.UnitX);
        // Scene Y grows downward on the Stage, so local +Y maps to screen-up.
        var up = NormalizeOr(-Vector3.TransformNormal(Vector3.UnitY, rotation), -Vector3.UnitY);
        var forward = NormalizeOr(Vector3.TransformNormal(Vector3.UnitZ, rotation), Vector3.UnitZ);
        var distance = ResolveWorldFrameDistance(normalized, height);
        // Perspective framing scales with stand-off, exactly like a real lens: the same focal length
        // covers more of the scene when the camera pulls back. This is what makes a Z dolly visible
        // in the preview and in the gizmo. Orthographic framing is distance independent.
        var halfHeight = normalized.Projection == CameraProjection.Orthographic
            ? Math.Max(1f, normalized.OrthographicSize * 0.5f)
            : Math.Max(1f, height * 25f / Math.Max(MinimumFocalLength, normalized.FocalLength))
                * (distance / DefaultStandOffDistance);
        halfHeight = Math.Max(halfHeight, MinimumFrameHalfHeight(aspect));
        var halfWidth = Math.Max(MinimumFrameWidth * 0.5f, halfHeight * aspect);
        // The anchor follows the Stage convention: the frame sits on the stage plane the camera looks
        // at, so the camera does not drag its framing window along as it dollies.
        var toStagePlane = Vector3.Dot(-normalized.Position, forward);
        var anchorDistance = float.IsFinite(toStagePlane) && toStagePlane > 0f ? toStagePlane : distance;
        var center = normalized.Position + forward * anchorDistance;
        return (center, right, up, halfWidth, halfHeight);
    }

    /// <summary>
    /// Stand-off from the camera to the scene plane it frames. The director camera stores its
    /// stand-off in Z (the default is <c>-1_000</c>), so the magnitude of Z is the framing distance.
    /// A Z dolly therefore changes how much of the scene the frame covers.
    /// </summary>
    private static float ResolveWorldFrameDistance(SceneShotSettings normalized, float canvasHeight)
    {
        var fallback = Math.Max(DefaultStandOffDistance, canvasHeight * 0.35f);
        var distance = MathF.Abs(normalized.Z);
        if (!float.IsFinite(distance) || distance < 1f) distance = fallback;
        return Math.Clamp(distance, 1f, 2_000_000f);
    }

    /// <summary>World-space corners of the framed viewport in TL/TR/BR/BL order.</summary>
    internal Vector3[] ResolveWorldFrameCorners(float canvasWidth, float canvasHeight)
    {
        var (center, right, up, halfWidth, halfHeight) = ResolveWorldFrame(canvasWidth, canvasHeight);
        return
        [
            center - right * halfWidth + up * halfHeight,
            center + right * halfWidth + up * halfHeight,
            center + right * halfWidth - up * halfHeight,
            center - right * halfWidth - up * halfHeight
        ];
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
    {
        var length = value.Length();
        return length > 1e-6f && float.IsFinite(length) ? value / length : fallback;
    }

    /// <summary>
    /// Converts a camera to the legacy 2D preview framing. The actual camera state remains 3D;
    /// this adapter only exists because the Stage preview is a 2D composition surface.
    /// </summary>
    internal SceneShotSettings ToPreviewFraming()
    {
        var normalized = Normalized();
        var zoom = normalized.Projection == CameraProjection.Orthographic
            ? 28_000f / normalized.OrthographicSize
            : normalized.FocalLength / 50f;
        // Older camera records only carried the four-value 2D framing state. Preserve a non-default
        // compatibility zoom when the new lens fields still have their defaults.
        if (Math.Abs(normalized.Zoom - 1f) > 0.0001f
            && Math.Abs(normalized.FocalLength - 50f) <= 0.0001f
            && Math.Abs(normalized.OrthographicSize - 28_000f) <= 0.0001f)
        {
            zoom = normalized.Zoom;
        }

        return new SceneShotSettings(normalized.X, normalized.Y, zoom, normalized.RotationDegrees)
        { AspectRatio = normalized.AspectRatio };
    }

    /// <summary>Lower-left and upper-right corners for the legacy 2D preview surface.</summary>
    internal (Vector2 LowerLeft, Vector2 UpperRight, float HalfWidth, float HalfHeight) ResolveFrame(
        float canvasWidth,
        float canvasHeight)
    {
        var zoom = Zoom is >= MinimumZoom and <= MaximumZoom ? Zoom : 1f;
        var width = Math.Max(1f, canvasWidth);
        var height = Math.Max(1f, canvasHeight);
        var aspect = ResolveAspectRatio(width, height);
        var halfHeight = Math.Max(height / (2f * zoom), MinimumFrameHalfHeight(aspect));
        var halfWidth = Math.Max(MinimumFrameWidth * 0.5f, halfHeight * aspect);
        return (
            new Vector2(X - halfWidth, Y - halfHeight),
            new Vector2(X + halfWidth, Y + halfHeight),
            halfWidth,
            halfHeight);
    }

    internal float ResolveAspectRatio(float canvasWidth, float canvasHeight) => AspectRatio switch
    {
        SceneShotAspectRatio.Landscape16To9 => 16f / 9f,
        SceneShotAspectRatio.Portrait9To16 => 9f / 16f,
        SceneShotAspectRatio.Standard4To3 => 4f / 3f,
        SceneShotAspectRatio.Photo3To2 => 3f / 2f,
        SceneShotAspectRatio.Square1To1 => 1f,
        SceneShotAspectRatio.Portrait4To5 => 4f / 5f,
        SceneShotAspectRatio.Ultrawide21To9 => 21f / 9f,
        _ => Math.Max(1f, canvasWidth) / Math.Max(1f, canvasHeight)
    };

    internal static float MinimumFrameHalfHeight(float aspect) =>
        Math.Max(MinimumFrameHeight * 0.5f, MinimumFrameWidth * 0.5f / aspect);

    private static bool FiniteInRange(float value, float limit) =>
        float.IsFinite(value) && Math.Abs(value) <= limit;

    private static float ClampPosition(float value) =>
        Math.Clamp(float.IsFinite(value) ? value : 0f, -MaximumPosition, MaximumPosition);

    private static float Lerp(float source, float target, float progress) =>
        (float)((double)source + ((double)target - source) * progress);

    private static float LerpAngle(float source, float target, float progress)
    {
        var delta = ((double)target - source) % 360d;
        if (delta > 180d) delta -= 360d;
        else if (delta < -180d) delta += 360d;
        return NormalizeAngle((float)((double)source + delta * progress));
    }

    private static float NormalizeAngle(float degrees)
    {
        if (!float.IsFinite(degrees)) return 0f;
        var normalized = degrees % 360f;
        if (normalized > 180f) normalized -= 360f;
        else if (normalized < -180f) normalized += 360f;
        return normalized;
    }
}

/// <summary>Captured camera state at one timeline frame. Frame zero is held by <see cref="SceneShotDefinition.Settings"/>.</summary>
internal readonly record struct SceneShotStateKeyframe(int Frame, SceneShotSettings Settings);

/// <summary>
/// An independent 3D camera in the single scene. Its stable ID is also the target ID of its
/// timeline track. The legacy duration/layer fields are retained only so older project files can be
/// read and normalized into the new camera model.
/// </summary>
internal sealed class SceneShotDefinition
{
    internal const int DefaultDurationFrames = AnimationTimeline.DefaultDuration;
    internal const int MinimumDurationFrames = 1;
    internal const int MaximumDurationFrames = 100_000;
    internal const int MaximumNameLength = 80;
    internal const int MaximumDetailLength = 512;

    internal static readonly int DefaultColorArgb = Color.FromArgb(255, 244, 152, 62).ToArgb();

    private readonly List<SceneShotStateKeyframe> _stateKeyframes = [];
    private readonly IReadOnlyList<SceneShotStateKeyframe> _stateKeyframeView;

    public SceneShotDefinition()
    {
        _stateKeyframeView = _stateKeyframes.AsReadOnly();
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Camera";
    public string Detail { get; set; } = "Scene camera";

    // Kept for old scene-shot documents. A camera now follows the complete scene timeline.
    public int DurationFrames { get; set; } = DefaultDurationFrames;
    public int ColorArgb { get; set; } = DefaultColorArgb;

    /// <summary>Legacy layer claims are intentionally ignored by the new camera model.</summary>
    [Obsolete("Cameras do not own scene layers.")]
    public List<string> LayerIds { get; } = [];

    public SceneShotSettings Settings { get; private set; } = SceneShotSettings.Default;

    public IReadOnlyList<SceneShotStateKeyframe> StateKeyframes => _stateKeyframeView;

    public SceneShotSettings EvaluateSettings(int frame)
    {
        frame = Math.Max(0, frame);
        var index = LowerBoundStateKeyframe(frame);
        if (index >= _stateKeyframes.Count || _stateKeyframes[index].Frame != frame) index--;
        return index >= 0 ? _stateKeyframes[index].Settings : Settings;
    }

    internal bool SetSettingsAtFrame(int frame, SceneShotSettings settings)
    {
        frame = Math.Max(0, frame);
        settings = settings.Normalized();
        if (frame == 0)
        {
            var removedKeyframes = _stateKeyframes.RemoveAll(keyframe => keyframe.Frame == 0);
            if (Settings == settings && removedKeyframes == 0) return false;
            Settings = settings;
            return true;
        }

        var index = LowerBoundStateKeyframe(frame);
        if (index < _stateKeyframes.Count && _stateKeyframes[index].Frame == frame)
        {
            if (_stateKeyframes[index].Settings == settings) return false;
            _stateKeyframes[index] = new SceneShotStateKeyframe(frame, settings);
            return true;
        }

        _stateKeyframes.Insert(index, new SceneShotStateKeyframe(frame, settings));
        return true;
    }

    internal bool RemoveStateKeyframe(int frame)
    {
        if (frame <= 0) return false;
        var index = LowerBoundStateKeyframe(frame);
        if (index >= _stateKeyframes.Count || _stateKeyframes[index].Frame != frame) return false;
        _stateKeyframes.RemoveAt(index);
        return true;
    }

    internal void InsertStateFrames(int frame, int count)
    {
        if (count <= 0) return;
        frame = Math.Max(0, frame);
        for (var index = _stateKeyframes.Count - 1; index >= 0; index--)
        {
            var keyframe = _stateKeyframes[index];
            if (keyframe.Frame < frame) continue;
            _stateKeyframes[index] = keyframe with { Frame = keyframe.Frame + count };
        }
    }

    internal void RemoveStateFrames(int frame, int count)
    {
        if (count <= 0) return;
        frame = Math.Max(0, frame);
        _stateKeyframes.RemoveAll(keyframe => keyframe.Frame >= frame && keyframe.Frame < frame + count);
        for (var index = 0; index < _stateKeyframes.Count; index++)
        {
            var keyframe = _stateKeyframes[index];
            if (keyframe.Frame <= frame) continue;
            _stateKeyframes[index] = keyframe with { Frame = Math.Max(frame, keyframe.Frame - count) };
        }
    }

    internal void RestoreStateKeyframes(IEnumerable<SceneShotStateKeyframe>? keyframes)
    {
        _stateKeyframes.Clear();
        if (keyframes is null) return;
        var byFrame = new SortedDictionary<int, SceneShotSettings>();
        foreach (var keyframe in keyframes)
        {
            if (!keyframe.Settings.IsValid) continue;
            byFrame[Math.Max(0, keyframe.Frame)] = keyframe.Settings.Normalized();
        }

        if (byFrame.TryGetValue(0, out var frameZero))
        {
            Settings = frameZero;
            byFrame.Remove(0);
        }

        foreach (var (frame, settings) in byFrame)
        {
            _stateKeyframes.Add(new SceneShotStateKeyframe(frame, settings));
        }
    }

    internal void RestoreFrom(SceneShotDefinition source)
    {
        Name = source.Name;
        Detail = source.Detail;
        DurationFrames = source.DurationFrames;
        ColorArgb = source.ColorArgb;
        Settings = source.Settings;
        LayerIds.Clear();
        RestoreStateKeyframes(source.StateKeyframes);
    }

    internal SceneShotDefinition Clone()
    {
        var clone = new SceneShotDefinition
        {
            Id = Id,
            Name = Name,
            Detail = Detail,
            DurationFrames = DurationFrames,
            ColorArgb = ColorArgb,
            Settings = Settings
        };
        clone.RestoreStateKeyframes(StateKeyframes);
        return clone;
    }

    private int LowerBoundStateKeyframe(int frame)
    {
        var low = 0;
        var high = _stateKeyframes.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_stateKeyframes[mid].Frame < frame) low = mid + 1;
            else high = mid;
        }

        return low;
    }
}

/// <summary>Full-scene camera range retained for callers that still draw a ruler band.</summary>
internal readonly record struct SceneShotRange(string ShotId, string Name, int StartFrame, int EndFrame)
{
    public int FrameCount => EndFrame >= StartFrame ? EndFrame - StartFrame + 1 : 0;

    public bool Contains(int frame) => frame >= StartFrame && frame <= EndFrame;
}

/// <summary>Captured camera list, used for undo, restart snapshots, and durable scene timelines.</summary>
internal sealed class SceneShotSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Camera";
    public string Detail { get; init; } = "Scene camera";
    public int DurationFrames { get; init; } = SceneShotDefinition.DefaultDurationFrames;
    public int ColorArgb { get; init; } = SceneShotDefinition.DefaultColorArgb;

    // Read-compatible with the former shot-layer model. New snapshots always write an empty array.
    public string[] LayerIds { get; init; } = [];
    public SceneShotSettings Settings { get; init; } = SceneShotSettings.Default;
    public SceneShotStateKeyframe[] StateKeyframes { get; init; } = [];
}
