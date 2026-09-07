using System.Numerics;

namespace VectorAnimationEngine;

internal enum SceneLightGizmoHandleKind : byte
{
    None,
    Position,
    PositionX,
    PositionY,
    PositionZ,
    Direction,
    Intensity,
    Range,
    AreaWidth,
    AreaHeight,
    ShadowStrength,
    ShadowSoftness
}

internal readonly record struct SceneLightGizmoHandleHit(SceneLightGizmoHandleKind Kind)
{
    public static SceneLightGizmoHandleHit None { get; } = new(SceneLightGizmoHandleKind.None);

    public bool IsValid => Kind != SceneLightGizmoHandleKind.None;
}

internal readonly record struct SceneLightGizmoEntry(
    string Id,
    SceneLightKind Kind,
    SceneLightSettings Settings,
    bool Selected)
{
    public string LightId => Id;

    public bool IsValid => !string.IsNullOrWhiteSpace(Id)
        && Enum.IsDefined(Kind)
        && Settings.IsValid(Kind);
}

internal readonly record struct SceneLightMarkerScreenGeometry(
    string LightId,
    SceneLightKind Kind,
    PointF Origin,
    float Depth,
    int ColorArgb,
    bool Enabled,
    bool Selected);

internal readonly record struct SceneLightGizmoScreenGeometry(
    PointF Origin,
    PointF PositionXHandle,
    PointF PositionYHandle,
    PointF PositionZHandle,
    PointF DirectionHandle,
    PointF IntensityTrackStart,
    PointF IntensityTrackEnd,
    PointF IntensityHandle,
    PointF RangeHandle,
    float RangeRadius,
    PointF AreaWidthHandle,
    PointF AreaHeightHandle,
    PointF ShadowStrengthTrackStart,
    PointF ShadowStrengthTrackEnd,
    PointF ShadowStrengthHandle,
    PointF ShadowSoftnessTrackStart,
    PointF ShadowSoftnessTrackEnd,
    PointF ShadowSoftnessHandle,
    float WorldUnitsPerPixel);

internal sealed partial class StageControl
{
    private const string LegacySceneLightGizmoId = "__selected_scene_light__";
    private const float SceneLightGizmoTrackPixels = 62f;
    private const float SceneLightGizmoHitRadiusPixels = 9f;
    private const float SceneLightMarkerHitRadiusPixels = 12f;
    private const float SceneLightAmbientMarkerMarginPixels = 22f;
    private const float SceneLightAmbientMarkerSpacingPixels = 28f;
    private SceneLightGizmoEntry[] _sceneLightGizmoEntries = [];
    private int _sceneLightGizmoSelectedIndex = -1;
    private bool _sceneLightGizmoVisible;
    private SceneLightKind _sceneLightGizmoKind;
    private SceneLightSettings _sceneLightGizmoSettings;
    private SceneLightGizmoHandleHit _sceneLightGizmoHoveredHandle = SceneLightGizmoHandleHit.None;
    private SceneLightGizmoHandleHit _sceneLightGizmoActiveHandle = SceneLightGizmoHandleHit.None;

    internal static Color SceneLightGizmoHighlightColor { get; } = Color.FromArgb(246, 255, 196, 56);
    internal static Color SceneLightGizmoPositionXColor { get; } = Color.FromArgb(245, 255, 92, 92);
    internal static Color SceneLightGizmoPositionYColor { get; } = Color.FromArgb(245, 92, 218, 122);
    internal static Color SceneLightGizmoPositionZColor { get; } = Color.FromArgb(245, 92, 172, 255);
    internal static Color SceneLightGizmoIntensityColor { get; } = Color.FromArgb(245, 255, 184, 76);
    internal static Color SceneLightGizmoRangeColor { get; } = Color.FromArgb(235, 62, 195, 214);
    internal static Color SceneLightGizmoAreaWidthColor { get; } = Color.FromArgb(240, 230, 95, 170);
    internal static Color SceneLightGizmoAreaHeightColor { get; } = Color.FromArgb(240, 90, 207, 126);
    internal static Color SceneLightGizmoShadowStrengthColor { get; } = Color.FromArgb(235, 128, 112, 190);
    internal static Color SceneLightGizmoShadowSoftnessColor { get; } = Color.FromArgb(235, 165, 176, 190);

    internal bool SceneLightGizmoVisible => _sceneLightGizmoVisible;
    internal int SceneLightMarkerCount => _sceneLightGizmoEntries.Length;
    internal SceneLightKind SceneLightGizmoKind => _sceneLightGizmoKind;
    internal SceneLightSettings SceneLightGizmoSettings => _sceneLightGizmoSettings;
    internal SceneLightGizmoHandleHit SceneLightGizmoHighlightedHandle =>
        _sceneLightGizmoActiveHandle.IsValid
            ? _sceneLightGizmoActiveHandle
            : _sceneLightGizmoHoveredHandle;

    internal void SetSceneLightGizmo(SceneLightKind kind, SceneLightSettings settings)
    {
        if (!Enum.IsDefined(kind) || !settings.IsValid(kind))
        {
            ClearSceneLightGizmo();
            return;
        }

        if ((uint)_sceneLightGizmoSelectedIndex < _sceneLightGizmoEntries.Length)
        {
            var nextEntries = (SceneLightGizmoEntry[])_sceneLightGizmoEntries.Clone();
            var selected = nextEntries[_sceneLightGizmoSelectedIndex];
            nextEntries[_sceneLightGizmoSelectedIndex] = selected with
            {
                Kind = kind,
                Settings = settings,
                Selected = true
            };
            SetSceneLightGizmos(nextEntries);
            return;
        }

        SetSceneLightGizmos(
        [
            new SceneLightGizmoEntry(
                LegacySceneLightGizmoId,
                kind,
                settings,
                Selected: true)
        ]);
    }

    internal void SetSceneLightGizmos(IReadOnlyList<SceneLightGizmoEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var normalized = new List<SceneLightGizmoEntry>(entries.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var selectedIndex = -1;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!entry.IsValid || !ids.Add(entry.LightId)) continue;
            if (entry.Selected)
            {
                if (selectedIndex >= 0) entry = entry with { Selected = false };
                else selectedIndex = normalized.Count;
            }
            normalized.Add(entry);
        }

        var nextEntries = normalized.ToArray();
        var oldSelectedId = (uint)_sceneLightGizmoSelectedIndex < _sceneLightGizmoEntries.Length
            ? _sceneLightGizmoEntries[_sceneLightGizmoSelectedIndex].LightId
            : string.Empty;
        var nextSelectedId = (uint)selectedIndex < nextEntries.Length
            ? nextEntries[selectedIndex].LightId
            : string.Empty;
        var selectionChanged = !string.Equals(oldSelectedId, nextSelectedId, StringComparison.Ordinal);
        var changed = !_sceneLightGizmoEntries.AsSpan().SequenceEqual(nextEntries);

        _sceneLightGizmoEntries = nextEntries;
        _sceneLightGizmoSelectedIndex = selectedIndex;
        _sceneLightGizmoVisible = selectedIndex >= 0;
        if (selectedIndex >= 0)
        {
            var selected = nextEntries[selectedIndex];
            if (selectionChanged || _sceneLightGizmoKind != selected.Kind)
            {
                _sceneLightGizmoHoveredHandle = SceneLightGizmoHandleHit.None;
                _sceneLightGizmoActiveHandle = SceneLightGizmoHandleHit.None;
            }
            _sceneLightGizmoKind = selected.Kind;
            _sceneLightGizmoSettings = selected.Settings;
        }
        else
        {
            _sceneLightGizmoSettings = default;
            _sceneLightGizmoHoveredHandle = SceneLightGizmoHandleHit.None;
            _sceneLightGizmoActiveHandle = SceneLightGizmoHandleHit.None;
        }

        if (changed) InvalidateOverlay();
    }

    internal void ClearSceneLightGizmo()
    {
        var changed = _sceneLightGizmoVisible
            || _sceneLightGizmoEntries.Length > 0
            || _sceneLightGizmoHoveredHandle.IsValid
            || _sceneLightGizmoActiveHandle.IsValid;
        _sceneLightGizmoEntries = [];
        _sceneLightGizmoSelectedIndex = -1;
        _sceneLightGizmoVisible = false;
        _sceneLightGizmoSettings = default;
        _sceneLightGizmoHoveredHandle = SceneLightGizmoHandleHit.None;
        _sceneLightGizmoActiveHandle = SceneLightGizmoHandleHit.None;
        if (changed) InvalidateOverlay();
    }

    internal void SetSceneLightGizmoHover(SceneLightGizmoHandleHit handle)
    {
        var next = NormalizeSceneLightGizmoHandle(handle);
        if (_sceneLightGizmoHoveredHandle == next) return;
        _sceneLightGizmoHoveredHandle = next;
        InvalidateOverlay();
    }

    internal void SetSceneLightGizmoActive(SceneLightGizmoHandleHit handle)
    {
        var next = NormalizeSceneLightGizmoHandle(handle);
        if (_sceneLightGizmoActiveHandle == next) return;
        _sceneLightGizmoActiveHandle = next;
        InvalidateOverlay();
    }

    internal bool IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind kind) =>
        SceneLightGizmoHighlightedHandle.Kind == kind;

    internal bool SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind handle)
    {
        if (!_sceneLightGizmoVisible) return false;
        return handle switch
        {
            SceneLightGizmoHandleKind.Position
                or SceneLightGizmoHandleKind.PositionX
                or SceneLightGizmoHandleKind.PositionY
                or SceneLightGizmoHandleKind.PositionZ =>
                _sceneLightGizmoKind is SceneLightKind.Directional or SceneLightKind.Point or SceneLightKind.Area,
            SceneLightGizmoHandleKind.Direction =>
                _sceneLightGizmoKind is SceneLightKind.Directional or SceneLightKind.Area,
            SceneLightGizmoHandleKind.Intensity => true,
            SceneLightGizmoHandleKind.Range =>
                _sceneLightGizmoKind is SceneLightKind.Point or SceneLightKind.Area,
            SceneLightGizmoHandleKind.AreaWidth or SceneLightGizmoHandleKind.AreaHeight =>
                _sceneLightGizmoKind == SceneLightKind.Area,
            SceneLightGizmoHandleKind.ShadowStrength or SceneLightGizmoHandleKind.ShadowSoftness =>
                _sceneLightGizmoKind != SceneLightKind.Ambient
                && _sceneLightGizmoSettings.CastsShadows,
            _ => false
        };
    }

    private SceneLightGizmoHandleHit NormalizeSceneLightGizmoHandle(SceneLightGizmoHandleHit handle) =>
        handle.IsValid && SupportsSceneLightGizmoHandle(handle.Kind)
            ? handle
            : SceneLightGizmoHandleHit.None;

    internal SceneLightGizmoHandleHit HitTestSceneLightGizmo(Point screen)
    {
        if (!_sceneLightGizmoVisible
            || ReferenceDimension != SceneDimension.ThreeD
            || !TryGetSceneLightGizmoScreenGeometry(out var geometry))
        {
            return SceneLightGizmoHandleHit.None;
        }

        var radius = SceneLightGizmoHitRadiusPixels * SpatialGizmoDpiScale;
        if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.Intensity)
            && HitTrack(screen, geometry.IntensityHandle, geometry.IntensityTrackStart, geometry.IntensityTrackEnd, radius))
        {
            return new SceneLightGizmoHandleHit(SceneLightGizmoHandleKind.Intensity);
        }
        if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.ShadowStrength)
            && HitTrack(
                screen,
                geometry.ShadowStrengthHandle,
                geometry.ShadowStrengthTrackStart,
                geometry.ShadowStrengthTrackEnd,
                radius))
        {
            return new SceneLightGizmoHandleHit(SceneLightGizmoHandleKind.ShadowStrength);
        }
        if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.ShadowSoftness)
            && HitTrack(
                screen,
                geometry.ShadowSoftnessHandle,
                geometry.ShadowSoftnessTrackStart,
                geometry.ShadowSoftnessTrackEnd,
                radius))
        {
            return new SceneLightGizmoHandleHit(SceneLightGizmoHandleKind.ShadowSoftness);
        }
        if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.Position)
            && ReferencePointDistance(screen, geometry.Origin) <= radius)
        {
            return new SceneLightGizmoHandleHit(SceneLightGizmoHandleKind.Position);
        }
        if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.AreaWidth)
            && HitEndpointOrSegment(screen, geometry.AreaWidthHandle, geometry.Origin, radius))
        {
            return new SceneLightGizmoHandleHit(SceneLightGizmoHandleKind.AreaWidth);
        }
        if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.AreaHeight)
            && HitEndpointOrSegment(screen, geometry.AreaHeightHandle, geometry.Origin, radius))
        {
            return new SceneLightGizmoHandleHit(SceneLightGizmoHandleKind.AreaHeight);
        }
        if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.Direction)
            && (ReferencePointDistance(screen, geometry.DirectionHandle) <= radius
                || _sceneLightGizmoKind == SceneLightKind.Area
                    && ReferencePointDistance(screen, geometry.Origin) > radius * 1.35f
                    && DistanceToSegment(screen, geometry.Origin, geometry.DirectionHandle)
                        <= radius * 0.65f))
        {
            return new SceneLightGizmoHandleHit(SceneLightGizmoHandleKind.Direction);
        }
        if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.Range)
            && (ReferencePointDistance(screen, geometry.RangeHandle) <= radius
                || Math.Abs(ReferencePointDistance(screen, geometry.Origin) - geometry.RangeRadius) <= radius * 0.75f))
        {
            return new SceneLightGizmoHandleHit(SceneLightGizmoHandleKind.Range);
        }
        return HitTestSceneLightPositionAxes(screen, geometry, radius);
    }

    internal string? HitTestSceneLightMarker(Point screen)
    {
        if (ReferenceDimension != SceneDimension.ThreeD || _sceneLightGizmoEntries.Length == 0)
            return null;

        var radius = SceneLightMarkerHitRadiusPixels * SpatialGizmoDpiScale;
        if ((uint)_sceneLightGizmoSelectedIndex < _sceneLightGizmoEntries.Length
            && TryGetSceneLightMarkerScreenGeometry(_sceneLightGizmoSelectedIndex, out var selected)
            && ReferencePointDistance(screen, selected.Origin) <= radius)
        {
            return selected.LightId;
        }

        for (var index = _sceneLightGizmoEntries.Length - 1; index >= 0; index--)
        {
            if (index == _sceneLightGizmoSelectedIndex
                || !TryGetSceneLightMarkerScreenGeometry(index, out var geometry)
                || ReferencePointDistance(screen, geometry.Origin) > radius)
            {
                continue;
            }
            return geometry.LightId;
        }
        return null;
    }

    internal bool TryGetSceneLightMarkerScreenGeometry(
        int index,
        out SceneLightMarkerScreenGeometry geometry)
    {
        geometry = default;
        if (ReferenceDimension != SceneDimension.ThreeD
            || (uint)index >= _sceneLightGizmoEntries.Length)
        {
            return false;
        }

        var entry = _sceneLightGizmoEntries[index];
        PointF origin;
        float depth;
        if (entry.Kind == SceneLightKind.Ambient)
        {
            origin = SceneLightAmbientMarkerOrigin(index);
            depth = _referenceDistance;
        }
        else if (!TryProjectScenePosition(entry.Settings.Position, out origin, out depth))
        {
            return false;
        }

        geometry = new SceneLightMarkerScreenGeometry(
            entry.LightId,
            entry.Kind,
            origin,
            depth,
            entry.Settings.ColorArgb,
            entry.Settings.Enabled,
            entry.Selected);
        return true;
    }

    internal bool TryGetSceneLightGizmoScreenGeometry(out SceneLightGizmoScreenGeometry geometry)
    {
        geometry = default;
        if (!_sceneLightGizmoVisible || ReferenceDimension != SceneDimension.ThreeD) return false;

        if ((uint)_sceneLightGizmoSelectedIndex >= _sceneLightGizmoEntries.Length
            || !TryGetSceneLightMarkerScreenGeometry(_sceneLightGizmoSelectedIndex, out var marker))
        {
            return false;
        }

        var dpiScale = SpatialGizmoDpiScale;
        var settings = _sceneLightGizmoSettings;
        var hasWorldOrigin = _sceneLightGizmoKind != SceneLightKind.Ambient;
        var sceneOrigin = hasWorldOrigin ? settings.Position : Vector3.Zero;
        var origin = marker.Origin;
        var depth = marker.Depth;

        var projectionScale = 0.035f * _referenceZoomScale
            * ReferencePerspectiveScale(Math.Max(ReferenceNearPlane, depth));
        var worldUnitsPerPixel = 1f / Math.Max(0.0001f, projectionScale);
        var worldLength = SceneLightGizmoTrackPixels * dpiScale * worldUnitsPerPixel;
        var positionAxisLength = SceneLightGizmoTrackPixels * 0.82f * dpiScale;
        var positionXHandle = hasWorldOrigin
            ? ProjectedHandle(
                sceneOrigin,
                origin,
                Vector3.UnitX,
                worldLength,
                positionAxisLength,
                Vector2.UnitX)
            : origin;
        var positionYHandle = hasWorldOrigin
            ? ProjectedHandle(
                sceneOrigin,
                origin,
                Vector3.UnitY,
                worldLength,
                positionAxisLength,
                new Vector2(0, -1))
            : origin;
        var positionZHandle = hasWorldOrigin
            ? ProjectedHandle(
                sceneOrigin,
                origin,
                Vector3.UnitZ,
                worldLength,
                positionAxisLength,
                Vector2.Normalize(new Vector2(-0.7f, 0.7f)))
            : origin;
        var directionHandle = ProjectedHandle(
            sceneOrigin,
            origin,
            Reference3DDirectionalLightVector(settings.RotationDegrees),
            worldLength,
            SceneLightGizmoTrackPixels * dpiScale,
            new Vector2(0, -1));

        var trackLength = SceneLightGizmoTrackPixels * dpiScale;
        var bankX = origin.X + 26f * dpiScale;
        if (bankX + trackLength + 14f * dpiScale > ClientSize.Width)
            bankX = origin.X - 26f * dpiScale - trackLength;
        var bankY = origin.Y - 54f * dpiScale;
        if (bankY < 14f * dpiScale) bankY = origin.Y + 54f * dpiScale;
        var intensityStart = new PointF(bankX, bankY);
        var intensityEnd = new PointF(bankX + trackLength, bankY);
        var intensityAmount = MathF.Log(1f + settings.Intensity)
            / MathF.Log(1f + SceneLightSettings.MaximumIntensity);
        var intensityHandle = SceneLightLerp(intensityStart, intensityEnd, intensityAmount);

        var rangeRadius = SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.Range)
            ? Math.Clamp(settings.Range / worldUnitsPerPixel, 30f * dpiScale, 132f * dpiScale)
            : 0f;
        var rangeHandle = new PointF(origin.X + rangeRadius, origin.Y);

        var rotation = Quaternion.CreateFromYawPitchRoll(
            DegreesToRadians(settings.RotationDegrees.Y),
            DegreesToRadians(settings.RotationDegrees.X),
            DegreesToRadians(settings.RotationDegrees.Z));
        var areaWidthHandle = ProjectedSizedHandle(
            sceneOrigin,
            origin,
            Vector3.Transform(Vector3.UnitX, rotation),
            settings.AreaSize.X * 0.5f,
            worldLength,
            dpiScale,
            Vector2.UnitX);
        var areaHeightHandle = ProjectedSizedHandle(
            sceneOrigin,
            origin,
            Vector3.Transform(Vector3.UnitY, rotation),
            settings.AreaSize.Y * 0.5f,
            worldLength,
            dpiScale,
            new Vector2(0, -1));

        var shadowStrengthStart = new PointF(bankX, bankY + 15f * dpiScale);
        var shadowStrengthEnd = new PointF(bankX + trackLength, shadowStrengthStart.Y);
        var shadowStrengthHandle = SceneLightLerp(
            shadowStrengthStart,
            shadowStrengthEnd,
            settings.ShadowStrength);
        var shadowSoftnessStart = new PointF(bankX, bankY + 30f * dpiScale);
        var shadowSoftnessEnd = new PointF(bankX + trackLength, shadowSoftnessStart.Y);
        var shadowSoftnessHandle = SceneLightLerp(
            shadowSoftnessStart,
            shadowSoftnessEnd,
            settings.ShadowSoftness);

        geometry = new SceneLightGizmoScreenGeometry(
            origin,
            positionXHandle,
            positionYHandle,
            positionZHandle,
            directionHandle,
            intensityStart,
            intensityEnd,
            intensityHandle,
            rangeHandle,
            rangeRadius,
            areaWidthHandle,
            areaHeightHandle,
            shadowStrengthStart,
            shadowStrengthEnd,
            shadowStrengthHandle,
            shadowSoftnessStart,
            shadowSoftnessEnd,
            shadowSoftnessHandle,
            worldUnitsPerPixel);
        return true;
    }

    private PointF SceneLightAmbientMarkerOrigin(int entryIndex)
    {
        var ambientIndex = 0;
        for (var index = 0; index < entryIndex; index++)
        {
            if (_sceneLightGizmoEntries[index].Kind == SceneLightKind.Ambient) ambientIndex++;
        }

        var dpiScale = SpatialGizmoDpiScale;
        var margin = SceneLightAmbientMarkerMarginPixels * dpiScale;
        var spacing = SceneLightAmbientMarkerSpacingPixels * dpiScale;
        var columns = Math.Max(1, (int)MathF.Floor(Math.Max(spacing, ClientSize.Width - margin * 2) / spacing));
        return new PointF(
            margin + ambientIndex % columns * spacing,
            margin + ambientIndex / columns * spacing);
    }

    private PointF ProjectedSizedHandle(
        Vector3 sceneOrigin,
        PointF screenOrigin,
        Vector3 axis,
        float size,
        float fallbackWorldLength,
        float dpiScale,
        Vector2 fallbackDirection)
    {
        var endpoint = ProjectedHandle(
            sceneOrigin,
            screenOrigin,
            axis,
            Math.Max(0.01f, size),
            0,
            fallbackDirection);
        var offset = new Vector2(endpoint.X - screenOrigin.X, endpoint.Y - screenOrigin.Y);
        var length = offset.Length();
        if (length <= 0.001f)
        {
            endpoint = ProjectedHandle(
                sceneOrigin,
                screenOrigin,
                axis,
                fallbackWorldLength,
                0,
                fallbackDirection);
            offset = new Vector2(endpoint.X - screenOrigin.X, endpoint.Y - screenOrigin.Y);
            length = offset.Length();
        }
        var direction = length > 0.001f ? offset / length : fallbackDirection;
        var displayLength = Math.Clamp(length, 28f * dpiScale, 104f * dpiScale);
        return new PointF(
            screenOrigin.X + direction.X * displayLength,
            screenOrigin.Y + direction.Y * displayLength);
    }

    private PointF ProjectedHandle(
        Vector3 sceneOrigin,
        PointF screenOrigin,
        Vector3 direction,
        float worldLength,
        float fixedScreenLength,
        Vector2 fallbackDirection)
    {
        var projectionOrigin = screenOrigin;
        _ = TryProjectScenePosition(sceneOrigin, out projectionOrigin, out _);
        var endpoint = projectionOrigin;
        if (Finite(direction)
            && direction.LengthSquared() > 0.000001f
            && TryProjectScenePosition(
                sceneOrigin + Vector3.Normalize(direction) * worldLength,
                out var projected,
                out _))
        {
            endpoint = projected;
        }
        var offset = new Vector2(
            endpoint.X - projectionOrigin.X,
            endpoint.Y - projectionOrigin.Y);
        var length = offset.Length();
        var screenDirection = length > 0.001f ? offset / length : fallbackDirection;
        var resolvedLength = fixedScreenLength > 0 ? fixedScreenLength : length;
        return new PointF(
            screenOrigin.X + screenDirection.X * resolvedLength,
            screenOrigin.Y + screenDirection.Y * resolvedLength);
    }

    private static bool HitTrack(Point screen, PointF handle, PointF start, PointF end, float radius) =>
        ReferencePointDistance(screen, handle) <= radius
        || DistanceToSegment(screen, start, end) <= radius * 0.55f;

    private static bool HitEndpointOrSegment(Point screen, PointF endpoint, PointF origin, float radius) =>
        ReferencePointDistance(screen, endpoint) <= radius
        || DistanceToSegment(screen, origin, endpoint) <= radius * 0.55f;

    private SceneLightGizmoHandleHit HitTestSceneLightPositionAxes(
        Point screen,
        SceneLightGizmoScreenGeometry geometry,
        float radius)
    {
        if (!SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.PositionX))
            return SceneLightGizmoHandleHit.None;

        var bestKind = SceneLightGizmoHandleKind.None;
        var bestScore = float.MaxValue;
        ConsiderSceneLightPositionAxis(
            screen,
            geometry.Origin,
            geometry.PositionXHandle,
            SceneLightGizmoHandleKind.PositionX,
            radius,
            ref bestKind,
            ref bestScore);
        ConsiderSceneLightPositionAxis(
            screen,
            geometry.Origin,
            geometry.PositionYHandle,
            SceneLightGizmoHandleKind.PositionY,
            radius,
            ref bestKind,
            ref bestScore);
        ConsiderSceneLightPositionAxis(
            screen,
            geometry.Origin,
            geometry.PositionZHandle,
            SceneLightGizmoHandleKind.PositionZ,
            radius,
            ref bestKind,
            ref bestScore);
        return bestKind == SceneLightGizmoHandleKind.None
            ? SceneLightGizmoHandleHit.None
            : new SceneLightGizmoHandleHit(bestKind);
    }

    private static void ConsiderSceneLightPositionAxis(
        Point screen,
        PointF origin,
        PointF endpoint,
        SceneLightGizmoHandleKind kind,
        float radius,
        ref SceneLightGizmoHandleKind bestKind,
        ref float bestScore)
    {
        var endpointScore = ReferencePointDistance(screen, endpoint) / radius;
        var segmentScore = DistanceToSegment(screen, origin, endpoint) / (radius * 0.55f);
        var score = Math.Min(endpointScore, segmentScore);
        if (score > 1f || score >= bestScore) return;
        bestKind = kind;
        bestScore = score;
    }

    private static PointF SceneLightLerp(PointF start, PointF end, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new PointF(
            start.X + (end.X - start.X) * amount,
            start.Y + (end.Y - start.Y) * amount);
    }
}
