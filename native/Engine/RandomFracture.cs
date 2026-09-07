using Clipper2Lib;

namespace VectorAnimationEngine;

internal enum RandomFractureMode : byte
{
    Linear,
    Pointillize
}

internal enum RandomFractureForceAlgorithm : byte
{
    Radial,
    Directional,
    Random
}

internal sealed record RandomFractureOptions
{
    public int FragmentCountLimit { get; init; } = 24;
    public int EdgeCount { get; init; } = 6;
    public float FragmentRandomness { get; init; } = 0.72f;
    public int RandomSeed { get; init; } = 1337;
    public bool PreserveStroke { get; init; } = true;
    public RandomFractureMode Mode { get; init; } = RandomFractureMode.Pointillize;
    public RandomFractureForceAlgorithm ForceAlgorithm { get; init; } = RandomFractureForceAlgorithm.Radial;
    public float FractureStrength { get; init; } = 28f;
    public float DirectionAngleDegrees { get; init; } = -90f;
    public float RotationStrengthDegrees { get; init; } = 3f;
    public bool GenerateAnimation { get; init; }
    public int AnimationFrames { get; init; } = 36;
    // Kept for compatibility with older callers. New fracture operations use
    // EnableFragmentCollisions and leave overlap disabled by default.
    public bool AllowOverlap { get; init; }
    public bool EnableFragmentCollisions { get; init; } = true;
    public float GroundPositionPercent { get; init; } = 100f;
    public float GravityPixelsPerFrameSquared { get; init; } = 0.8f;
    public float AirResistancePercent { get; init; } = 2f;
    public float BouncePercent { get; init; } = 35f;

    public RandomFractureOptions Normalize()
    {
        return this with
        {
            FragmentCountLimit = Math.Clamp(FragmentCountLimit, 2, 256),
            EdgeCount = Math.Clamp(EdgeCount, 3, 32),
            FragmentRandomness = ClampFinite(FragmentRandomness, 0f, 1f, 0.72f),
            RandomSeed = RandomSeed,
            FractureStrength = ClampFinite(FractureStrength, 0f, 500f, 28f),
            DirectionAngleDegrees = ClampFinite(DirectionAngleDegrees, -180f, 180f, -90f),
            RotationStrengthDegrees = ClampFinite(RotationStrengthDegrees, -90f, 90f, 3f),
            AnimationFrames = Math.Clamp(AnimationFrames, 1, 240),
            GroundPositionPercent = ClampFinite(GroundPositionPercent, -200f, 400f, 100f),
            GravityPixelsPerFrameSquared = ClampFinite(GravityPixelsPerFrameSquared, 0f, 50f, 0.8f),
            AirResistancePercent = ClampFinite(AirResistancePercent, 0f, 100f, 2f),
            BouncePercent = ClampFinite(BouncePercent, 0f, 100f, 35f)
        };
    }

    private static float ClampFinite(float value, float minimum, float maximum, float fallback)
    {
        return float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }
}

internal sealed record RandomFractureFragment(
    PointF[][] Contours,
    PointF Center,
    RectangleF Bounds,
    double Area,
    int SeedIndex);

internal readonly record struct RandomFractureMotion(PointF Offset, float Rotation);

internal static class RandomFractureGenerator
{
    private const double CoordinateScale = 1000d;
    private const double MinimumArea = 0.5d * CoordinateScale * CoordinateScale;
    private const float PointContainmentTolerance = 0.01f;
    private const float CollisionEpsilon = 0.01f;
    private const float CollisionSeparation = 0.25f;
    private const float SupportPointTolerance = 0.001f;
    private const double CollisionMinimumArea = 1d;
    private const double SegmentIntersectionEpsilon = 0.0001d;
    private const float MaximumSubstepPixels = 2f;
    private const int MaximumSimulationSubsteps = 128;
    private const int MaximumTerrainSimulationSubsteps = 4;
    private const int LargeSimulationFragmentCount = 32;
    private const int MaximumLargeSimulationSubsteps = 3;
    private const int MaximumLargeSimulationSolverPasses = 2;
    private const int CollisionSolverPasses = 8;
    private const int MaximumFragmentCollisionChecksPerFrame = 120_000;
    private const int MaximumTerrainCollisionChecksPerFrame = 64_000;
    private const float RestingVelocityPixels = 0.05f;
    private const float RestingAngularVelocity = 0.0001f;
    // Small tangential velocities are static contact noise rather than useful
    // motion. Removing them lets a fragment enter the resting state instead of
    // sliding and being corrected by a different terrain edge every frame.
    private const float StaticFrictionVelocityPixels = 0.35f;
    private const float TerrainContactDirectionBlend = 0.2f;
    private const float TerrainContactDirectionSwitchDot = 0.75f;
    private const float MaximumContactOrientationStepRadians = 0.12f;
    private const int FragmentRestingContactFrames = 2;
    private const int CollisionTriangulationPrecision = 8;
    private const float TerrainProbeDistance = 0.5f;
    private const int TerrainSettlePasses = 2;
    private const int MaximumTerrainContactSearchExpansions = 8;
    // Keep the exact fill intersection as the authority, but bound the number
    // of edge normals used for MTD refinement. The nearest edge candidates are
    // sufficient for a local contact and prevent complex terrain from making
    // every preview frame quadratic in its vertex count.
    private const int MaximumTerrainContactCandidates = 32;
    private const float CollisionDirectionDeduplicationDot = 0.999999f;
    private const int SeparationSearchIterations = 20;

    public static bool TryGenerate(
        IReadOnlyList<PointF[]> sourceContours,
        RandomFractureOptions options,
        out RandomFractureFragment[] fragments,
        out RectangleF sourceBounds,
        out string error,
        CancellationToken cancellationToken = default)
    {
        fragments = [];
        sourceBounds = RectangleF.Empty;
        error = string.Empty;
        if (sourceContours is null || sourceContours.Count == 0)
        {
            error = "The selected object has no closed fill boundary.";
            return false;
        }

        options = options.Normalize();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePaths = ToClipperPaths(sourceContours);
            if (sourcePaths.Count == 0)
            {
                error = "The selected object has no usable fill boundary.";
                return false;
            }

            var region = Clipper.Union(sourcePaths, FillRule.EvenOdd);
            if (region.Count == 0)
            {
                error = "The selected fill boundary could not be normalized.";
                return false;
            }

            sourceBounds = BoundsFromPaths(region);
            if (!HasUsableArea(sourceBounds) || !IsFinite(sourceBounds))
            {
                error = "The selected object is too small to fracture.";
                return false;
            }

            var random = new Random(options.RandomSeed);
            var count = options.FragmentCountLimit;
            var generated = options.Mode == RandomFractureMode.Linear
                ? GenerateLinear(region, sourceBounds, count, options, random, cancellationToken)
                : GeneratePointillized(region, sourceBounds, count, options, random, cancellationToken);
            if (generated.Count == 0)
            {
                error = "The selected object did not produce any valid fragments.";
                return false;
            }

            fragments = generated
                .OrderBy(fragment => fragment.SeedIndex)
                .ToArray();
            return true;
        }
        catch (Exception exception) when (exception is
            ClipperLibException or OverflowException or ArgumentException or InvalidOperationException)
        {
            fragments = [];
            sourceBounds = RectangleF.Empty;
            error = $"The fracture geometry could not be generated: {exception.Message}";
            return false;
        }
    }

    public static RandomFractureMotion[][] Simulate(
        IReadOnlyList<RandomFractureFragment> fragments,
        RectangleF sourceBounds,
        RandomFractureOptions options,
        IReadOnlyList<PointF[]>? collisionTerrainContours = null,
        CancellationToken cancellationToken = default)
    {
        return Simulate(
            fragments,
            sourceBounds,
            options,
            collisionTerrainContours,
            collisionTerrainContoursByFrame: null,
            cancellationToken: cancellationToken);
    }

    public static RandomFractureMotion[][] Simulate(
        IReadOnlyList<RandomFractureFragment> fragments,
        RectangleF sourceBounds,
        RandomFractureOptions options,
        IReadOnlyList<PointF[]>? collisionTerrainContours,
        IReadOnlyList<PointF[][]?>? collisionTerrainContoursByFrame,
        CancellationToken cancellationToken = default)
    {
        options = options.Normalize();
        var frameCount = options.GenerateAnimation ? options.AnimationFrames + 1 : 1;
        if (fragments.Count == 0) return new[] { Array.Empty<RandomFractureMotion>() };

        if (!options.GenerateAnimation)
        {
            return new[]
            {
                fragments
                    .Select(_ => new RandomFractureMotion(PointF.Empty, 0f))
                    .ToArray()
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var random = new Random(options.RandomSeed ^ 0x5f3759df);
        var states = new FragmentState[fragments.Count];
        var result = new RandomFractureMotion[frameCount][];
        var origin = new PointF(
            sourceBounds.Left + sourceBounds.Width * 0.5f,
            sourceBounds.Top + sourceBounds.Height * 0.5f);
        var ground = sourceBounds.Top + sourceBounds.Height * options.GroundPositionPercent / 100f;
        var gravity = new PointF(0, VectorUnits.FromPixels(options.GravityPixelsPerFrameSquared));
        var resistance = Math.Clamp(1f - options.AirResistancePercent / 100f, 0f, 1f);
        var bounce = Math.Clamp(options.BouncePercent / 100f, 0f, 1f);
        var terrainCollisionsEnabled = options.EnableFragmentCollisions;
        // AllowOverlap only relaxes fragment-to-fragment contacts. Authored
        // terrain remains authoritative for every fracture animation.
        var fragmentCollisionsEnabled = terrainCollisionsEnabled && !options.AllowOverlap;
        var terrainFramesSupplied = collisionTerrainContoursByFrame is not null;
        var allowImplicitGround = !terrainFramesSupplied
            && collisionTerrainContours is not { Count: > 0 };
        var directionRadians = options.DirectionAngleDegrees * MathF.PI / 180f;
        var fragmentGeometry = fragments
            .Select(fragment => BuildCollisionGeometry(fragment.Contours, fragment.Center))
            .ToArray();
        CollisionGeometry? terrainGeometry;
        CollisionGeometry?[]? terrainGeometries = null;
        if (terrainCollisionsEnabled && terrainFramesSupplied)
        {
            terrainGeometries = new CollisionGeometry?[frameCount];
            IReadOnlyList<PointF[]>? previousContours = null;
            CollisionGeometry? previousGeometry = null;
            for (var terrainFrame = 0; terrainFrame < frameCount; terrainFrame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var contours = TerrainContoursAtFrame(
                    collisionTerrainContours,
                    collisionTerrainContoursByFrame,
                    terrainFrame);
                if (ReferenceEquals(contours, previousContours))
                {
                    terrainGeometries[terrainFrame] = previousGeometry;
                    continue;
                }

                previousContours = contours;
                previousGeometry = contours is { Count: > 0 }
                    ? BuildCollisionGeometry(
                        contours,
                        PointF.Empty,
                        triangulate: false,
                        buildEdgeIndex: true,
                        cancellationToken: cancellationToken)
                    : null;
                terrainGeometries[terrainFrame] = previousGeometry;
            }

            terrainGeometry = terrainGeometries[0];
        }
        else
        {
            terrainGeometry = terrainCollisionsEnabled && collisionTerrainContours is { Count: > 0 }
                ? BuildCollisionGeometry(
                    collisionTerrainContours,
                    PointF.Empty,
                    triangulate: false,
                    buildEdgeIndex: true,
                    cancellationToken: cancellationToken)
                : null;
        }
        var previousPositions = new PointF[states.Length];
        var previousAngles = new float[states.Length];
        var terrainResting = new bool[states.Length];
        var terrainContactValid = new bool[states.Length];
        var terrainContactNormals = new PointF[states.Length];
        var terrainContactTangents = new PointF[states.Length];
        var terrainPendingTangents = new PointF[states.Length];
        var terrainPendingTangentFrames = new int[states.Length];
        var fragmentCollisionAffected = new bool[states.Length];
        var fragmentResting = new bool[states.Length];
        var fragmentQuietContactFrames = new int[states.Length];

        for (var index = 0; index < fragments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fragment = fragments[index];
            var delta = new PointF(fragment.Center.X - origin.X, fragment.Center.Y - origin.Y);
            var deltaLength = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            var radial = deltaLength > 0.0001f
                ? new PointF(delta.X / deltaLength, delta.Y / deltaLength)
                : new PointF(0, -1);
            var randomAngle = (float)(random.NextDouble() * Math.PI * 2d);
            var randomDirection = new PointF(MathF.Cos(randomAngle), MathF.Sin(randomAngle));
            var forceDirection = options.ForceAlgorithm switch
            {
                RandomFractureForceAlgorithm.Directional => new PointF(
                    MathF.Cos(directionRadians),
                    MathF.Sin(directionRadians)),
                RandomFractureForceAlgorithm.Random => randomDirection,
                _ => radial
            };
            var variation = 0.72f + (float)random.NextDouble() * 0.56f;
            var speedPixels = options.FractureStrength * variation;
            var angularSpeed = options.RotationStrengthDegrees
                * (0.65f + (float)random.NextDouble() * 0.7f)
                * (random.Next(2) == 0 ? -1f : 1f)
                * MathF.PI / 180f;
            states[index] = new FragmentState(
                fragment.Center,
                new PointF(
                    VectorUnits.FromPixels(forceDirection.X * speedPixels),
                    VectorUnits.FromPixels(forceDirection.Y * speedPixels)),
                0,
                angularSpeed,
                fragmentGeometry[index]);
        }

        for (var frame = 0; frame < frameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[frame] = states
                .Select(state => new RandomFractureMotion(
                    new PointF(state.Position.X - state.Start.X, state.Position.Y - state.Start.Y),
                    state.Angle))
                .ToArray();

            if (frame == frameCount - 1) break;
            var nextTerrainGeometry = terrainGeometries is not null
                ? terrainGeometries[Math.Min(frame + 1, terrainGeometries.Length - 1)]
                : terrainGeometry;
            var terrainChanged = terrainFramesSupplied
                && !CollisionGeometryEquivalent(terrainGeometry, nextTerrainGeometry);
            var sweptTerrainGeometry = terrainChanged
                ? BuildSweptTerrainGeometry(
                    terrainGeometry,
                    nextTerrainGeometry,
                    cancellationToken)
                : null;
            var maximumSpeedPixels = states
                .Select(state => MathF.Sqrt(
                    state.Velocity.X * state.Velocity.X
                    + state.Velocity.Y * state.Velocity.Y) * VectorUnits.PixelsPerUnit)
                .DefaultIfEmpty()
                .Max();
            var maximumAngularTravelPixels = states
                .Select(state => MathF.Abs(state.AngularVelocity)
                    * GeometryRadius(state.Geometry.Contours)
                    * VectorUnits.PixelsPerUnit)
                .DefaultIfEmpty()
                .Max();
            var substeps = Math.Clamp(
                (int)MathF.Ceiling(
                    (maximumSpeedPixels + maximumAngularTravelPixels)
                    / MaximumSubstepPixels),
                1,
                MaximumSimulationSubsteps);
            var fragmentPairCount = (long)states.Length * (states.Length - 1) / 2;
            if (fragmentCollisionsEnabled && fragmentPairCount > 0)
            {
                var maximumSubsteps = Math.Max(
                    1L,
                    MaximumFragmentCollisionChecksPerFrame
                        / Math.Max(1L, fragmentPairCount * CollisionSolverPasses));
                substeps = Math.Min(
                    substeps,
                    (int)Math.Clamp(maximumSubsteps, 1L, (long)MaximumSimulationSubsteps));
            }

            if (terrainCollisionsEnabled && states.Length > 0)
            {
                var maximumSubsteps = Math.Max(
                    1L,
                    MaximumTerrainCollisionChecksPerFrame
                        / Math.Max(1L, (long)states.Length * CollisionSolverPasses));
                substeps = Math.Min(
                    substeps,
                    (int)Math.Clamp(maximumSubsteps, 1L, (long)MaximumSimulationSubsteps));
                substeps = Math.Min(substeps, MaximumTerrainSimulationSubsteps);
                if (states.Length >= LargeSimulationFragmentCount)
                {
                    substeps = Math.Min(substeps, MaximumLargeSimulationSubsteps);
                }
            }

            var solverPasses = states.Length >= LargeSimulationFragmentCount
                ? MaximumLargeSimulationSolverPasses
                : CollisionSolverPasses;
            if (fragmentCollisionsEnabled && fragmentPairCount > 0)
            {
                solverPasses = Math.Min(
                    solverPasses,
                    Math.Max(
                        1,
                        (int)Math.Min(
                            CollisionSolverPasses,
                            MaximumFragmentCollisionChecksPerFrame
                                / Math.Max(1L, fragmentPairCount * substeps))));
            }

            if (terrainCollisionsEnabled && states.Length > 0)
            {
                solverPasses = Math.Min(
                    solverPasses,
                    Math.Max(
                        1,
                        (int)Math.Min(
                            CollisionSolverPasses,
                            MaximumTerrainCollisionChecksPerFrame
                                / Math.Max(1L, (long)states.Length * substeps))));
            }

            var timeStep = 1f / substeps;
            var substepResistance = MathF.Pow(resistance, timeStep);
            var gravityStep = gravity.Y * timeStep;
            for (var substep = 0; substep < substeps; substep++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var index = 0; index < states.Length; index++)
                {
                    var state = states[index];
                    previousPositions[index] = state.Position;
                    previousAngles[index] = state.Angle;
                    if (terrainChanged)
                    {
                        terrainResting[index] = false;
                        fragmentResting[index] = false;
                        fragmentQuietContactFrames[index] = 0;
                    }
                    if (!terrainChanged
                        && (terrainResting[index] || fragmentResting[index])
                        && IsRestingState(state))
                    {
                        state.Velocity = PointF.Empty;
                        state.AngularVelocity = 0f;
                        states[index] = state;
                        continue;
                    }

                    terrainResting[index] = false;
                    if (fragmentResting[index])
                    {
                        fragmentResting[index] = false;
                        fragmentQuietContactFrames[index] = 0;
                    }
                    state.Velocity = new PointF(
                        state.Velocity.X * substepResistance,
                        (state.Velocity.Y + gravityStep) * substepResistance);
                    state.Position = new PointF(
                        state.Position.X + state.Velocity.X * timeStep,
                        state.Position.Y + state.Velocity.Y * timeStep);
                    state.Angle += state.AngularVelocity * timeStep;
                    states[index] = state;
                }

                Array.Clear(fragmentCollisionAffected, 0, fragmentCollisionAffected.Length);
                for (var pass = 0; pass < solverPasses; pass++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!fragmentCollisionsEnabled) break;
                    var passChanged = ResolveFragmentCollisions(
                        states,
                        bounce,
                        fragmentCollisionAffected,
                        cancellationToken);
                    if (!passChanged) break;
                }

                for (var index = 0; index < terrainResting.Length; index++)
                {
                    if (!fragmentCollisionAffected[index])
                    {
                        if (!fragmentResting[index]) fragmentQuietContactFrames[index] = 0;
                        continue;
                    }

                    terrainResting[index] = false;
                    terrainContactValid[index] = false;
                    terrainPendingTangents[index] = PointF.Empty;
                    terrainPendingTangentFrames[index] = 0;
                    if (IsRestingState(states[index]))
                    {
                        fragmentQuietContactFrames[index] = Math.Min(
                            FragmentRestingContactFrames,
                            fragmentQuietContactFrames[index] + 1);
                        fragmentResting[index] = fragmentQuietContactFrames[index]
                            >= FragmentRestingContactFrames;
                    }
                    else
                    {
                        fragmentQuietContactFrames[index] = 0;
                        fragmentResting[index] = false;
                    }
                }

                if (terrainCollisionsEnabled)
                {
                    ResolveTerrainCollisions(
                        states,
                        terrainGeometry,
                        nextTerrainGeometry,
                        sweptTerrainGeometry,
                        ground,
                        bounce,
                        previousPositions,
                        previousAngles,
                        terrainResting,
                        terrainContactValid,
                        terrainContactNormals,
                        terrainContactTangents,
                        terrainPendingTangents,
                        terrainPendingTangentFrames,
                        allowImplicitGround,
                        allowRestingSkip: !terrainChanged,
                        performSweptCollision: true,
                        cancellationToken: cancellationToken);
                }
            }

            terrainGeometry = nextTerrainGeometry;
        }

        return result;
    }

    private static List<RandomFractureFragment> GenerateLinear(
        Paths64 sourceRegion,
        RectangleF bounds,
        int count,
        RandomFractureOptions options,
        Random random,
        CancellationToken cancellationToken)
    {
        var angle = ((float)random.NextDouble() - 0.5f)
            * MathF.PI
            * 0.45f
            * options.FragmentRandomness;
        var normal = new PointF(MathF.Cos(angle), MathF.Sin(angle));
        var tangent = new PointF(-normal.Y, normal.X);
        var corners = new[]
        {
            new PointF(bounds.Left, bounds.Top),
            new PointF(bounds.Right, bounds.Top),
            new PointF(bounds.Right, bounds.Bottom),
            new PointF(bounds.Left, bounds.Bottom)
        };
        var minimum = corners.Min(point => Dot(point, normal));
        var maximum = corners.Max(point => Dot(point, normal));
        var tangentMinimum = corners.Min(point => Dot(point, tangent));
        var tangentMaximum = corners.Max(point => Dot(point, tangent));
        var margin = Math.Max(bounds.Width, bounds.Height) + 10f;
        tangentMinimum -= margin;
        tangentMaximum += margin;
        var step = (maximum - minimum) / count;
        var cuts = new float[count + 1];
        cuts[0] = minimum - margin;
        cuts[^1] = maximum + margin;
        for (var index = 1; index < count; index++)
        {
            var jitter = ((float)random.NextDouble() - 0.5f)
                * step
                * 0.34f
                * options.FragmentRandomness;
            cuts[index] = minimum + step * index + jitter;
        }

        var edgeSegments = Math.Max(1, options.EdgeCount);
        var cutOffsets = new float[count + 1][];
        for (var cutIndex = 0; cutIndex <= count; cutIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offsets = new float[edgeSegments + 1];
            if (cutIndex > 0 && cutIndex < count)
            {
                for (var segment = 1; segment < offsets.Length - 1; segment++)
                {
                    offsets[segment] = ((float)random.NextDouble() - 0.5f)
                        * step
                        * 0.44f
                        * options.FragmentRandomness;
                }
            }

            cutOffsets[cutIndex] = offsets;
        }

        var result = new List<RandomFractureFragment>(count);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var polygon = new PointF[2 * (edgeSegments + 1)];
            for (var segment = 0; segment <= edgeSegments; segment++)
            {
                var tangentValue = tangentMinimum
                    + (tangentMaximum - tangentMinimum) * segment / edgeSegments;
                polygon[segment] = Reconstruct(
                    normal,
                    tangent,
                    cuts[index] + cutOffsets[index][segment],
                    tangentValue);
                polygon[polygon.Length - 1 - segment] = Reconstruct(
                    normal,
                    tangent,
                    cuts[index + 1] + cutOffsets[index + 1][segment],
                    tangentValue);
            }

            AddClippedFragment(result, sourceRegion, polygon, index);
        }

        return result;
    }

    private static List<RandomFractureFragment> GeneratePointillized(
        Paths64 sourceRegion,
        RectangleF bounds,
        int count,
        RandomFractureOptions options,
        Random random,
        CancellationToken cancellationToken)
    {
        var seeds = CreateSeeds(sourceRegion, bounds, count, options, random, cancellationToken);
        var result = new List<RandomFractureFragment>(seeds.Count);
        var margin = Math.Max(bounds.Width, bounds.Height) * 2f + 10f;
        var initial = new List<PointF>
        {
            new(bounds.Left - margin, bounds.Top - margin),
            new(bounds.Right + margin, bounds.Top - margin),
            new(bounds.Right + margin, bounds.Bottom + margin),
            new(bounds.Left - margin, bounds.Bottom + margin)
        };

        for (var seedIndex = 0; seedIndex < seeds.Count; seedIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var polygon = initial;
            var seed = seeds[seedIndex];
            for (var otherIndex = 0; otherIndex < seeds.Count && polygon.Count >= 3; otherIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (otherIndex == seedIndex) continue;
                polygon = ClipToVoronoiHalfPlane(polygon, seed, seeds[otherIndex]);
            }

            if (polygon.Count >= 3) AddClippedFragment(result, sourceRegion, polygon.ToArray(), seedIndex);
        }

        return result;
    }

    private static List<PointF> CreateSeeds(
        Paths64 sourceRegion,
        RectangleF bounds,
        int count,
        RandomFractureOptions options,
        Random random,
        CancellationToken cancellationToken)
    {
        var seeds = new List<PointF>(count);
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
        var rows = Math.Max(1, (int)Math.Ceiling(count / (double)columns));
        var jitterAmount = options.FragmentRandomness
            * (0.28f + 0.012f * Math.Clamp(options.EdgeCount, 3, 32));
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var column = index % columns;
            var row = index / columns;
            var baseX = bounds.Left + bounds.Width * (column + 0.5f) / columns;
            var baseY = bounds.Top + bounds.Height * (row + 0.5f) / rows;
            var randomPoint = new PointF(
                bounds.Left + (float)random.NextDouble() * bounds.Width,
                bounds.Top + (float)random.NextDouble() * bounds.Height);
            var point = new PointF(
                baseX + (randomPoint.X - baseX) * jitterAmount,
                baseY + (randomPoint.Y - baseY) * jitterAmount);
            if (!ContainsPoint(sourceRegion, point))
            {
                point = FindInteriorSeed(sourceRegion, bounds, point, random);
            }

            if (!ContainsPoint(sourceRegion, point)) continue;
            if (seeds.Any(existing => DistanceSquared(existing, point) < 0.0001f)) continue;
            seeds.Add(point);
        }

        if (seeds.Count < 2)
        {
            var center = new PointF(
                bounds.Left + bounds.Width * 0.5f,
                bounds.Top + bounds.Height * 0.5f);
            if (ContainsPoint(sourceRegion, center)) seeds.Add(center);
        }

        return seeds;
    }

    private static PointF FindInteriorSeed(Paths64 sourceRegion, RectangleF bounds, PointF origin, Random random)
    {
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var amount = (attempt + 1) / 24f;
            var point = new PointF(
                origin.X + (bounds.Left + (float)random.NextDouble() * bounds.Width - origin.X) * amount,
                origin.Y + (bounds.Top + (float)random.NextDouble() * bounds.Height - origin.Y) * amount);
            if (ContainsPoint(sourceRegion, point)) return point;
        }

        return origin;
    }

    private static List<PointF> ClipToVoronoiHalfPlane(
        IReadOnlyList<PointF> polygon,
        PointF owner,
        PointF other)
    {
        if (polygon.Count == 0) return [];
        var midpoint = new PointF((owner.X + other.X) * 0.5f, (owner.Y + other.Y) * 0.5f);
        var direction = new PointF(other.X - owner.X, other.Y - owner.Y);
        var output = new List<PointF>(polygon.Count + 1);
        var previous = polygon[^1];
        var previousValue = Dot(new PointF(previous.X - midpoint.X, previous.Y - midpoint.Y), direction);
        var previousInside = previousValue <= PointContainmentTolerance;
        for (var index = 0; index < polygon.Count; index++)
        {
            var current = polygon[index];
            var currentValue = Dot(new PointF(current.X - midpoint.X, current.Y - midpoint.Y), direction);
            var currentInside = currentValue <= PointContainmentTolerance;
            if (currentInside != previousInside)
            {
                var denominator = previousValue - currentValue;
                var t = Math.Abs(denominator) <= 0.000001f ? 0.5f : previousValue / denominator;
                t = Math.Clamp(t, 0f, 1f);
                output.Add(new PointF(
                    previous.X + (current.X - previous.X) * t,
                    previous.Y + (current.Y - previous.Y) * t));
            }

            if (currentInside) output.Add(current);
            previous = current;
            previousValue = currentValue;
            previousInside = currentInside;
        }

        return output;
    }

    private static void AddClippedFragment(
        ICollection<RandomFractureFragment> destination,
        Paths64 sourceRegion,
        IReadOnlyList<PointF> cutter,
        int seedIndex)
    {
        var cutterPath = ToClipperPath(cutter);
        if (cutterPath.Count < 3) return;
        var clipped = Clipper.Intersect(new Paths64 { cutterPath }, sourceRegion, FillRule.EvenOdd);
        if (clipped.Count == 0) return;

        var contours = FromClipperPaths(clipped);
        if (contours.Length == 0) return;
        var area = Math.Abs(clipped.Sum(Clipper.Area));
        if (area < MinimumArea) return;
        var bounds = BoundsFromContours(contours);
        if (!HasUsableArea(bounds) || !IsFinite(bounds)) return;
        var center = new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Top + bounds.Height * 0.5f);
        destination.Add(new RandomFractureFragment(contours, center, bounds, area / (CoordinateScale * CoordinateScale), seedIndex));
    }

    private static Paths64 ToClipperPaths(IReadOnlyList<PointF[]> contours)
    {
        var result = new Paths64(contours.Count);
        foreach (var contour in contours)
        {
            var path = ToClipperPath(contour);
            if (path.Count >= 3 && Math.Abs(Clipper.Area(path)) >= MinimumArea) result.Add(path);
        }

        return result;
    }

    private static Path64 ToClipperPath(IReadOnlyList<PointF> points)
    {
        var path = new Path64(points.Count);
        foreach (var point in points)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) continue;
            path.Add(new Point64(
                checked((long)Math.Round(point.X * CoordinateScale, MidpointRounding.AwayFromZero)),
                checked((long)Math.Round(point.Y * CoordinateScale, MidpointRounding.AwayFromZero))));
        }

        return Clipper.StripDuplicates(path, true);
    }

    private static PointF[][] FromClipperPaths(Paths64 paths)
    {
        var contours = new List<PointF[]>(paths.Count);
        foreach (var path in paths)
        {
            if (path.Count < 3 || Math.Abs(Clipper.Area(path)) < MinimumArea) continue;
            var contour = new PointF[path.Count];
            for (var index = 0; index < path.Count; index++)
            {
                // Keep the clipping result at its native sub-unit precision.
                // Scene storage applies the normal vector-unit quantization
                // once at the final append boundary.
                contour[index] = new PointF(
                    (float)(path[index].X / CoordinateScale),
                    (float)(path[index].Y / CoordinateScale));
            }

            if (contour.Length >= 3) contours.Add(contour);
        }

        return contours.ToArray();
    }

    private static RectangleF BoundsFromPaths(Paths64 paths)
    {
        var hasPoint = false;
        var left = 0f;
        var right = 0f;
        var top = 0f;
        var bottom = 0f;
        foreach (var path in paths)
        {
            foreach (var point in path)
            {
                var current = new PointF((float)(point.X / CoordinateScale), (float)(point.Y / CoordinateScale));
                if (!hasPoint)
                {
                    left = right = current.X;
                    top = bottom = current.Y;
                    hasPoint = true;
                    continue;
                }

                left = Math.Min(left, current.X);
                right = Math.Max(right, current.X);
                top = Math.Min(top, current.Y);
                bottom = Math.Max(bottom, current.Y);
            }
        }

        return hasPoint ? RectangleF.FromLTRB(left, top, right, bottom) : RectangleF.Empty;
    }

    private static RectangleF BoundsFromContours(IReadOnlyList<PointF[]> contours)
    {
        var hasPoint = false;
        var left = 0f;
        var right = 0f;
        var top = 0f;
        var bottom = 0f;
        foreach (var contour in contours)
        {
            foreach (var point in contour)
            {
                if (!hasPoint)
                {
                    left = right = point.X;
                    top = bottom = point.Y;
                    hasPoint = true;
                    continue;
                }

                left = Math.Min(left, point.X);
                right = Math.Max(right, point.X);
                top = Math.Min(top, point.Y);
                bottom = Math.Max(bottom, point.Y);
            }
        }

        return hasPoint ? RectangleF.FromLTRB(left, top, right, bottom) : RectangleF.Empty;
    }

    private static bool ContainsPoint(Paths64 region, PointF point)
    {
        var hits = 0;
        foreach (var path in region)
        {
            var contour = path
                .Select(item => new PointF((float)(item.X / CoordinateScale), (float)(item.Y / CoordinateScale)))
                .ToArray();
            if (PointOnContour(point, contour)) return true;
            if (PointInPolygon(point, contour)) hits++;
        }

        return (hits & 1) != 0;
    }

    private static bool PointInPolygon(
        PointF point,
        IReadOnlyList<PointF> polygon,
        CancellationToken cancellationToken = default)
    {
        var inside = false;
        for (var index = 0; index < polygon.Count; index++)
        {
            if ((index & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var previous = polygon[(index + polygon.Count - 1) % polygon.Count];
            var current = polygon[index];
            if ((current.Y > point.Y) == (previous.Y > point.Y)) continue;
            var denominator = previous.Y - current.Y;
            if (MathF.Abs(denominator) <= 0.000001f) continue;
            var intersectionX = (previous.X - current.X) * (point.Y - current.Y)
                / denominator
                + current.X;
            if (point.X < intersectionX) inside = !inside;
        }

        return inside;
    }

    private static bool PointInContours(
        PointF point,
        IReadOnlyList<PointF[]> contours,
        CancellationToken cancellationToken = default)
    {
        var inside = false;
        for (var contourIndex = 0; contourIndex < contours.Count; contourIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contour = contours[contourIndex];
            if (PointOnContour(point, contour)) return true;
            if (PointInPolygon(point, contour, cancellationToken)) inside = !inside;
        }

        return inside;
    }

    private static bool PointInCollisionGeometry(
        PointF point,
        CollisionGeometry geometry,
        CancellationToken cancellationToken = default)
    {
        if (!PointInBounds(point, geometry.Bounds)) return false;
        if (geometry.EdgeIndex is not { } edgeIndex)
        {
            return PointInContours(point, geometry.Contours, cancellationToken);
        }

        // Count crossings of a horizontal ray. The indexed query only visits
        // terrain edges in the point's Y cells, so a point-in-region probe no
        // longer scans every vertex of a large contour during MTD bisection.
        var candidates = new List<int>();
        var rayBounds = RectangleF.FromLTRB(
            point.X - CollisionEpsilon,
            point.Y - CollisionEpsilon,
            geometry.Bounds.Right + CollisionEpsilon,
            point.Y + CollisionEpsilon);
        edgeIndex.CollectCandidates(rayBounds, candidates);
        var crossings = 0;
        foreach (var candidateIndex in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edge = geometry.Edges[candidateIndex];
            if (PointOnSegment(point, edge.Start, edge.End)) return true;
            if ((edge.Start.Y > point.Y) == (edge.End.Y > point.Y)) continue;
            var denominator = edge.End.Y - edge.Start.Y;
            if (MathF.Abs(denominator) <= 0.000001f) continue;
            var intersectionX = (edge.End.X - edge.Start.X)
                    * (point.Y - edge.Start.Y)
                / denominator
                + edge.Start.X;
            if (point.X < intersectionX) crossings++;
        }

        return (crossings & 1) != 0;
    }

    private static bool PointOnContour(PointF point, IReadOnlyList<PointF> contour)
    {
        for (var index = 0; index < contour.Count; index++)
        {
            var start = contour[index];
            var end = contour[(index + 1) % contour.Count];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0.000001f) continue;
            var t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
            if (t < 0 || t > 1) continue;
            var closest = new PointF(start.X + dx * t, start.Y + dy * t);
            if (DistanceSquared(point, closest) <= PointContainmentTolerance * PointContainmentTolerance) return true;
        }

        return false;
    }

    private static bool ResolveFragmentCollisions(
        FragmentState[] states,
        float restitution,
        IList<bool> affectedStates,
        CancellationToken cancellationToken = default)
    {
        if (states.Length < 2) return false;

        var worldGeometry = states
            .Select(state => TransformCollisionGeometry(state.Geometry, state.Position, state.Angle))
            .ToArray();
        var broadphaseBounds = worldGeometry
            .Select(geometry => geometry.Bounds)
            .ToArray();
        var order = Enumerable.Range(0, states.Length).ToArray();
        Array.Sort(order, (first, second) =>
        {
            var comparison = broadphaseBounds[first].Left.CompareTo(broadphaseBounds[second].Left);
            return comparison != 0 ? comparison : first.CompareTo(second);
        });

        var changed = false;
        for (var orderIndex = 0; orderIndex < order.Length; orderIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var firstIndex = order[orderIndex];
            var firstSweepBounds = broadphaseBounds[firstIndex];
            for (var nextOrderIndex = orderIndex + 1; nextOrderIndex < order.Length; nextOrderIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var secondIndex = order[nextOrderIndex];
                var secondSweepBounds = broadphaseBounds[secondIndex];
                if (secondSweepBounds.Left > firstSweepBounds.Right + CollisionEpsilon) break;
                if (!BoundsOverlap(worldGeometry[firstIndex].Bounds, worldGeometry[secondIndex].Bounds)) continue;
                if (!TryGetCompoundCollision(
                        worldGeometry[firstIndex],
                        worldGeometry[secondIndex],
                        CollisionNormal(firstIndex, secondIndex),
                        cancellationToken,
                        out var hit))
                {
                    continue;
                }

                var first = states[firstIndex];
                var second = states[secondIndex];
                var correction = (hit.Penetration + CollisionSeparation) * 0.5f;
                first.Position = new PointF(
                    first.Position.X - hit.Normal.X * correction,
                    first.Position.Y - hit.Normal.Y * correction);
                second.Position = new PointF(
                    second.Position.X + hit.Normal.X * correction,
                    second.Position.Y + hit.Normal.Y * correction);

                var relativeVelocity = new PointF(
                    second.Velocity.X - first.Velocity.X,
                    second.Velocity.Y - first.Velocity.Y);
                var closingVelocity = Dot(relativeVelocity, hit.Normal);
                if (closingVelocity < 0f)
                {
                    var impulse = -(1f + restitution) * closingVelocity * 0.5f;
                    first.Velocity = new PointF(
                        first.Velocity.X - hit.Normal.X * impulse,
                        first.Velocity.Y - hit.Normal.Y * impulse);
                    second.Velocity = new PointF(
                        second.Velocity.X + hit.Normal.X * impulse,
                        second.Velocity.Y + hit.Normal.Y * impulse);
                }

                first.Velocity = new PointF(first.Velocity.X * 0.96f, first.Velocity.Y * 0.96f);
                second.Velocity = new PointF(second.Velocity.X * 0.96f, second.Velocity.Y * 0.96f);
                states[firstIndex] = first;
                states[secondIndex] = second;
                affectedStates[firstIndex] = true;
                affectedStates[secondIndex] = true;
                changed = true;
            }
        }

        return changed;
    }

    private static IReadOnlyList<PointF[]>? TerrainContoursAtFrame(
        IReadOnlyList<PointF[]>? staticContours,
        IReadOnlyList<PointF[][]?>? contoursByFrame,
        int frame)
    {
        if (contoursByFrame is null) return staticContours;
        if (contoursByFrame.Count == 0) return Array.Empty<PointF[]>();

        var index = Math.Clamp(frame, 0, contoursByFrame.Count - 1);
        return contoursByFrame[index];
    }

    private static bool CollisionGeometryEquivalent(
        CollisionGeometry? first,
        CollisionGeometry? second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first is null || second is null) return false;
        if (first.Contours.Length != second.Contours.Length) return false;

        for (var contourIndex = 0; contourIndex < first.Contours.Length; contourIndex++)
        {
            var firstContour = first.Contours[contourIndex];
            var secondContour = second.Contours[contourIndex];
            if (firstContour.Length != secondContour.Length) return false;
            for (var pointIndex = 0; pointIndex < firstContour.Length; pointIndex++)
            {
                if (DistanceSquared(firstContour[pointIndex], secondContour[pointIndex]) > 0.000001f)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static CollisionGeometry? BuildSweptTerrainGeometry(
        CollisionGeometry? previous,
        CollisionGeometry? current,
        CancellationToken cancellationToken = default)
    {
        if (previous is null || current is null) return null;
        if (CollisionGeometryEquivalent(previous, current)) return null;
        if (previous.Contours.Length != current.Contours.Length) return null;

        var sweptContours = new List<PointF[]>();
        for (var contourIndex = 0; contourIndex < previous.Contours.Length; contourIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previousContour = previous.Contours[contourIndex];
            var currentContour = current.Contours[contourIndex];
            if (previousContour.Length != currentContour.Length
                || previousContour.Length < 2)
            {
                return null;
            }

            var winding = SignedArea(previousContour);
            for (var pointIndex = 0; pointIndex < previousContour.Length; pointIndex++)
            {
                if ((pointIndex & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                var nextPointIndex = (pointIndex + 1) % previousContour.Length;
                var movement = new PointF(
                    (currentContour[pointIndex].X + currentContour[nextPointIndex].X
                        - previousContour[pointIndex].X - previousContour[nextPointIndex].X) * 0.5f,
                    (currentContour[pointIndex].Y + currentContour[nextPointIndex].Y
                        - previousContour[pointIndex].Y - previousContour[nextPointIndex].Y) * 0.5f);
                if (!TryGetTerrainEdgeNormal(
                        previous.Contours, previousContour,
                        previousContour[pointIndex], previousContour[nextPointIndex],
                        winding, movement, out var normal)
                    || Dot(normal, movement) <= CollisionEpsilon) continue;

                var quad = new[]
                {
                    previousContour[pointIndex],
                    previousContour[nextPointIndex],
                    currentContour[nextPointIndex],
                    currentContour[pointIndex]
                };
                if (Math.Abs(SignedArea(quad)) > CollisionMinimumArea)
                {
                    if (SignedArea(quad) < 0) Array.Reverse(quad);
                    sweptContours.Add(quad);
                }
            }
        }

        if (sweptContours.Count == 0) return null;

        PointF[][] normalizedContours;
        try
        {
            // Adjacent edge strips overlap while a terrain translates. Merge
            // them with non-zero semantics before the regular even-odd cleanup
            // so the middle of a swept thin surface remains solid.
            var sweptPaths = ToCollisionPaths(sweptContours);
            normalizedContours = FromCollisionPaths(
                Clipper.Union(sweptPaths, FillRule.NonZero));
        }
        catch (Exception exception) when (exception is
            ClipperLibException or OverflowException or ArgumentException or InvalidOperationException)
        {
            normalizedContours = sweptContours.ToArray();
        }

        return normalizedContours.Length == 0
            ? null
            : BuildCollisionGeometry(
                normalizedContours,
                PointF.Empty,
                triangulate: false,
                buildEdgeIndex: true,
                cancellationToken: cancellationToken);
    }

    private static bool TryGetTerrainMovementDirection(
        CollisionGeometry? previous,
        CollisionGeometry? current,
        PointF position,
        out PointF direction)
    {
        direction = PointF.Empty;
        if (previous is null || current is null
            || previous.Contours.Length != current.Contours.Length) return false;

        var nearestDistance = float.MaxValue;
        for (var contourIndex = 0; contourIndex < previous.Contours.Length; contourIndex++)
        {
            var before = previous.Contours[contourIndex];
            var after = current.Contours[contourIndex];
            if (before.Length != after.Length) continue;
            var winding = SignedArea(before);
            for (var index = 0; index < before.Length; index++)
            {
                var next = (index + 1) % before.Length;
                var movement = new PointF(
                    (after[index].X + after[next].X - before[index].X - before[next].X) * 0.5f,
                    (after[index].Y + after[next].Y - before[index].Y - before[next].Y) * 0.5f);
                if (!TryGetTerrainEdgeNormal(previous.Contours, before, before[index], before[next],
                        winding, movement, out var normal)
                    || Dot(normal, movement) <= CollisionEpsilon) continue;

                var distance = DistanceSquared(position, ClosestPointOnSegment(position, before[index], before[next]));
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                direction = NormalizeDirection(movement, normal);
            }
        }

        return nearestDistance < float.MaxValue;
    }

    private static bool ResolveTerrainCollisions(
        FragmentState[] states,
        CollisionGeometry? terrainGeometry,
        CollisionGeometry? nextTerrainGeometry,
        CollisionGeometry? sweptTerrainGeometry,
        float ground,
        float restitution,
        IReadOnlyList<PointF> previousPositions,
        IReadOnlyList<float> previousAngles,
        IList<bool> terrainResting,
        IList<bool> terrainContactValid,
        IList<PointF> terrainContactNormals,
        IList<PointF> terrainContactTangents,
        PointF[] terrainPendingTangents,
        int[] terrainPendingTangentFrames,
        bool allowImplicitGround,
        bool allowRestingSkip,
        bool performSweptCollision,
        CancellationToken cancellationToken = default)
    {
        var previousTerrainGeometry = terrainGeometry;
        terrainGeometry = nextTerrainGeometry;
        var changed = false;
        for (var stateIndex = 0; stateIndex < states.Length; stateIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = states[stateIndex];
            if (allowRestingSkip
                && terrainResting[stateIndex]
                && IsRestingState(state))
            {
                states[stateIndex] = state;
                continue;
            }

            terrainResting[stateIndex] = false;
            var contactWasValid = terrainContactValid[stateIndex];
            var terrainNormalHint = contactWasValid
                ? NormalizeDirection(terrainContactNormals[stateIndex], new PointF(0f, -1f))
                : new PointF(0f, -1f);
            var worldGeometry = TransformCollisionGeometry(state.Geometry, state.Position, state.Angle);
            CollisionHit terrainHit = default;
            CollisionGeometry? collisionTerrainGeometry = terrainGeometry;
            var hasTerrainCollision = terrainGeometry is not null
                && TryGetTerrainCollision(
                    terrainGeometry,
                    worldGeometry,
                    terrainNormalHint,
                    cancellationToken,
                    out terrainHit);
            if (sweptTerrainGeometry is not null
                && HasFilledRegionOverlap(sweptTerrainGeometry, worldGeometry, cancellationToken)
                && TryGetTerrainMovementDirection(previousTerrainGeometry, terrainGeometry,
                    state.Position, out var movementDirection)
                && TryFindSeparationDistance(sweptTerrainGeometry, worldGeometry,
                    movementDirection, cancellationToken, null, false, out var sweptDistance))
            {
                terrainHit = new CollisionHit(movementDirection, sweptDistance, state.Position,
                    new PointF(-movementDirection.Y, movementDirection.X));
                collisionTerrainGeometry = sweptTerrainGeometry;
                hasTerrainCollision = true;
            }

            var movingTerrainGeometry = terrainGeometry;
            if (!hasTerrainCollision
                && movingTerrainGeometry is not null
                && performSweptCollision
                && (DistanceSquared(previousPositions[stateIndex], state.Position) > 0.000001f
                    || MathF.Abs(previousAngles[stateIndex] - state.Angle) > 0.000001f)
                && TryGetSweptTerrainCollision(
                    movingTerrainGeometry,
                    state.Geometry,
                    previousPositions[stateIndex],
                    previousAngles[stateIndex],
                    state.Position,
                    state.Angle,
                    terrainNormalHint,
                    cancellationToken,
                    out var sweptTime,
                    out terrainHit))
            {
                state.Position = new PointF(
                    previousPositions[stateIndex].X
                        + (state.Position.X - previousPositions[stateIndex].X) * sweptTime,
                    previousPositions[stateIndex].Y
                        + (state.Position.Y - previousPositions[stateIndex].Y) * sweptTime);
                state.Angle = previousAngles[stateIndex]
                    + (state.Angle - previousAngles[stateIndex]) * sweptTime;
                worldGeometry = TransformCollisionGeometry(state.Geometry, state.Position, state.Angle);
                hasTerrainCollision = TryGetTerrainCollision(
                    movingTerrainGeometry,
                    worldGeometry,
                    contactWasValid
                        ? terrainNormalHint
                        : terrainHit.Normal,
                    cancellationToken,
                    out terrainHit);
                if (hasTerrainCollision) collisionTerrainGeometry = movingTerrainGeometry;
            }

            var resolvedContact = false;
            if (hasTerrainCollision && collisionTerrainGeometry is not null)
            {
                var surfaceTangent = CollisionSurfaceTangent(
                    terrainHit,
                    collisionTerrainGeometry.Contours);
                var pendingTangent = terrainPendingTangents[stateIndex];
                var pendingTangentFrames = terrainPendingTangentFrames[stateIndex];
                var stableTangent = StabilizeTerrainTangent(
                    surfaceTangent,
                    contactWasValid ? terrainContactTangents[stateIndex] : PointF.Empty,
                    contactWasValid,
                    ref pendingTangent,
                    ref pendingTangentFrames);
                terrainPendingTangents[stateIndex] = pendingTangent;
                terrainPendingTangentFrames[stateIndex] = pendingTangentFrames;
                terrainHit = terrainHit with { SurfaceTangent = stableTangent };
                ApplyStaticCollision(
                    ref state,
                    terrainHit,
                    state.Geometry.Contours,
                    collisionTerrainGeometry.Contours,
                    restitution,
                    maximumOrientationDelta: contactWasValid
                        ? MaximumContactOrientationStepRadians
                        : null);
                changed = true;
                resolvedContact = true;
                worldGeometry = TransformCollisionGeometry(state.Geometry, state.Position, state.Angle);

                // Rotation is resolved from the real terrain edge above. That
                // rotation can expose a second overlap, so settle the pose
                // before the next solver pass instead of leaving it embedded.
                var settleGeometry = terrainGeometry ?? collisionTerrainGeometry;
                for (var settlePass = 0; settlePass < TerrainSettlePasses; settlePass++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryGetTerrainCollision(
                            settleGeometry,
                            worldGeometry,
                            terrainHit.Normal,
                            cancellationToken,
                            out var residualHit))
                    {
                        break;
                    }

                    ApplyStaticCollision(
                        ref state,
                        residualHit,
                        state.Geometry.Contours,
                        settleGeometry.Contours,
                        restitution,
                        alignOrientation: false);
                    changed = true;
                    worldGeometry = TransformCollisionGeometry(state.Geometry, state.Position, state.Angle);
                }

                terrainContactValid[stateIndex] = true;
                terrainContactNormals[stateIndex] = StabilizeTerrainNormal(
                    terrainHit.Normal,
                    contactWasValid ? terrainContactNormals[stateIndex] : PointF.Empty,
                    contactWasValid);
                terrainContactTangents[stateIndex] = stableTangent;
                terrainResting[stateIndex] = IsRestingState(state);
            }

            // Authored terrain is the collision surface for this simulation.
            // The implicit source-height ground is only a fallback when no
            // terrain was supplied; otherwise it can stop fragments before
            // they reach a user-drawn surface.
            if (allowImplicitGround
                && terrainGeometry is null
                && nextTerrainGeometry is null
                && TryGetGroundContact(worldGeometry, ground, out var groundHit))
            {
                var surfaceTangent = CollisionSurfaceTangent(
                    groundHit,
                    Array.Empty<PointF[]>());
                var pendingTangent = terrainPendingTangents[stateIndex];
                var pendingTangentFrames = terrainPendingTangentFrames[stateIndex];
                var stableTangent = StabilizeTerrainTangent(
                    surfaceTangent,
                    contactWasValid ? terrainContactTangents[stateIndex] : PointF.Empty,
                    contactWasValid,
                    ref pendingTangent,
                    ref pendingTangentFrames);
                terrainPendingTangents[stateIndex] = pendingTangent;
                terrainPendingTangentFrames[stateIndex] = pendingTangentFrames;
                groundHit = groundHit with { SurfaceTangent = stableTangent };
                ApplyStaticCollision(
                    ref state,
                    groundHit,
                    state.Geometry.Contours,
                    Array.Empty<PointF[]>(),
                    restitution,
                    maximumOrientationDelta: contactWasValid
                        ? MaximumContactOrientationStepRadians
                        : null);
                changed = true;
                resolvedContact = true;
                terrainContactValid[stateIndex] = true;
                terrainContactNormals[stateIndex] = StabilizeTerrainNormal(
                    groundHit.Normal,
                    contactWasValid ? terrainContactNormals[stateIndex] : PointF.Empty,
                    contactWasValid);
                terrainContactTangents[stateIndex] = stableTangent;
                terrainResting[stateIndex] = IsRestingState(state);
            }

            if (!resolvedContact)
            {
                terrainContactValid[stateIndex] = false;
                terrainContactNormals[stateIndex] = PointF.Empty;
                terrainContactTangents[stateIndex] = PointF.Empty;
                terrainPendingTangents[stateIndex] = PointF.Empty;
                terrainPendingTangentFrames[stateIndex] = 0;
            }

            states[stateIndex] = state;
        }

        return changed;
    }

    private static bool TryGetSweptTerrainCollision(
        CollisionGeometry terrain,
        CollisionGeometry moving,
        PointF startPosition,
        float startAngle,
        PointF endPosition,
        float endAngle,
        PointF normalHint,
        CancellationToken cancellationToken,
        out float contactTime,
        out CollisionHit hit)
    {
        contactTime = 0f;
        hit = default;
        if (terrain.Edges.Length == 0 || moving.Contours.Length == 0) return false;

        var sweptBounds = SweptCollisionBounds(
            moving,
            startPosition,
            startAngle,
            endPosition,
            endAngle);
        var candidateTerrainEdges = new List<CollisionEdge>();
        if (terrain.EdgeIndex is { } edgeIndex)
        {
            var candidateIndices = new List<int>();
            edgeIndex.CollectCandidates(sweptBounds, candidateIndices);
            foreach (var candidateIndex in candidateIndices)
            {
                var edge = terrain.Edges[candidateIndex];
                if (EdgeBoundsOverlap(edge, sweptBounds)) candidateTerrainEdges.Add(edge);
            }
        }
        else
        {
            candidateTerrainEdges.AddRange(terrain.Edges.Where(edge => EdgeBoundsOverlap(edge, sweptBounds)));
        }

        if (candidateTerrainEdges.Count == 0) return false;

        var startSine = MathF.Sin(startAngle);
        var startCosine = MathF.Cos(startAngle);
        var endSine = MathF.Sin(endAngle);
        var endCosine = MathF.Cos(endAngle);
        var candidateTimes = new List<float>();

        // A first contact between two polygon regions is a vertex/edge event.
        // Check both moving vertices against terrain edges and terrain vertices
        // in the moving object's local frame. This catches a thin terrain even
        // when the discrete substep starts before it and ends after it.
        foreach (var contour in moving.Contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var point in contour)
            {
                var startPoint = TransformLocalPoint(
                    point,
                    startPosition,
                    startSine,
                    startCosine);
                var endPoint = TransformLocalPoint(
                    point,
                    endPosition,
                    endSine,
                    endCosine);
                AddSweepIntersectionTimes(
                    candidateTimes,
                    startPoint,
                    endPoint,
                    candidateTerrainEdges);
            }
        }

        foreach (var terrainEdge in candidateTerrainEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddRelativeSweepIntersectionTimes(
                candidateTimes,
                terrainEdge.Start,
                startPosition,
                startSine,
                startCosine,
                endPosition,
                endSine,
                endCosine,
                moving.Contours);
            AddRelativeSweepIntersectionTimes(
                candidateTimes,
                terrainEdge.End,
                startPosition,
                startSine,
                startCosine,
                endPosition,
                endSine,
                endCosine,
                moving.Contours);
        }

        if (candidateTimes.Count == 0) return false;
        candidateTimes.Sort();
        var uniqueTimes = new List<float>(candidateTimes.Count);
        foreach (var candidate in candidateTimes)
        {
            var clamped = Math.Clamp(candidate, 0f, 1f);
            if (uniqueTimes.Count == 0 || clamped - uniqueTimes[^1] > 0.000001f)
            {
                uniqueTimes.Add(clamped);
            }
        }

        for (var index = 0; index < uniqueTimes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = uniqueTimes[index];
            var exit = index + 1 < uniqueTimes.Count
                ? uniqueTimes[index + 1]
                : 1f;
            if (exit <= entry + 0.000001f) continue;

            // The exact overlap has positive area between boundary events.
            // Probe several fractions so a very thin region does not disappear
            // between two candidate events, then resolve at the earliest hit.
            var fractions = new[] { 0.05f, 0.25f, 0.5f, 0.75f, 0.95f };
            foreach (var fraction in fractions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var probeTime = entry + (exit - entry) * fraction;
                var probePosition = new PointF(
                    startPosition.X + (endPosition.X - startPosition.X) * probeTime,
                    startPosition.Y + (endPosition.Y - startPosition.Y) * probeTime);
                var probeAngle = startAngle + (endAngle - startAngle) * probeTime;
                var probeGeometry = TransformCollisionGeometry(moving, probePosition, probeAngle);
                if (!TryGetTerrainCollision(
                        terrain,
                        probeGeometry,
                        normalHint,
                        cancellationToken,
                        out var probeHit)) continue;

                contactTime = probeTime;
                hit = probeHit;
                return true;
            }
        }

        return false;
    }

    private static void AddSweepIntersectionTimes(
        ICollection<float> destination,
        PointF start,
        PointF end,
        IReadOnlyList<CollisionEdge> edges)
    {
        foreach (var edge in edges)
        {
            if (!TryGetSegmentIntersection(start, end, edge.Start, edge.End, out var intersection)) continue;
            destination.Add(SegmentParameter(start, end, intersection));
        }
    }

    private static void AddRelativeSweepIntersectionTimes(
        ICollection<float> destination,
        PointF worldPoint,
        PointF startPosition,
        float startSine,
        float startCosine,
        PointF endPosition,
        float endSine,
        float endCosine,
        IReadOnlyList<PointF[]> movingContours)
    {
        var startLocal = InverseTransformPoint(worldPoint, startPosition, startSine, startCosine);
        var endLocal = InverseTransformPoint(worldPoint, endPosition, endSine, endCosine);
        foreach (var contour in movingContours)
        {
            if (contour is not { Length: >= 2 }) continue;
            for (var index = 0; index < contour.Length; index++)
            {
                var edgeStart = contour[index];
                var edgeEnd = contour[(index + 1) % contour.Length];
                if (!TryGetSegmentIntersection(startLocal, endLocal, edgeStart, edgeEnd, out var intersection)) continue;
                destination.Add(SegmentParameter(startLocal, endLocal, intersection));
            }
        }
    }

    private static float SegmentParameter(PointF start, PointF end, PointF point)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0.000001f) return 0f;
        return Math.Clamp(
            ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
            0f,
            1f);
    }

    private static PointF InverseTransformPoint(
        PointF point,
        PointF position,
        float sine,
        float cosine)
    {
        var dx = point.X - position.X;
        var dy = point.Y - position.Y;
        return new PointF(
            dx * cosine + dy * sine,
            -dx * sine + dy * cosine);
    }

    private static void ApplyStaticCollision(
        ref FragmentState state,
        CollisionHit hit,
        IReadOnlyList<PointF[]> fragmentContours,
        IReadOnlyList<PointF[]> surfaceContours,
        float restitution,
        bool alignOrientation = true,
        float? maximumOrientationDelta = null)
    {
        state.Position = new PointF(
            state.Position.X + hit.Normal.X * (hit.Penetration + CollisionSeparation),
            state.Position.Y + hit.Normal.Y * (hit.Penetration + CollisionSeparation));

        var normalVelocity = Dot(state.Velocity, hit.Normal);
        var tangent = new PointF(-hit.Normal.Y, hit.Normal.X);
        var tangentVelocity = Dot(state.Velocity, tangent);
        var staticFrictionVelocity = VectorUnits.FromPixels(StaticFrictionVelocityPixels);
        if (MathF.Abs(tangentVelocity) <= staticFrictionVelocity)
        {
            tangentVelocity = 0f;
        }
        else
        {
            tangentVelocity *= 0.98f;
        }
        if (normalVelocity < 0f) normalVelocity = -normalVelocity * restitution;
        if (MathF.Abs(normalVelocity) < VectorUnits.FromPixels(0.05f)) normalVelocity = 0f;
        state.Velocity = new PointF(
            tangent.X * tangentVelocity + hit.Normal.X * normalVelocity,
            tangent.Y * tangentVelocity + hit.Normal.Y * normalVelocity);

        // A static contact owns the final orientation. This prevents angular
        // velocity from fighting the terrain slope on the following frame.
        state.AngularVelocity = 0f;
        if (!alignOrientation) return;

        var targetTangent = CollisionSurfaceTangent(hit, surfaceContours);

        AlignFragmentToSurface(
            ref state,
            fragmentContours,
            hit.ContactPoint,
            targetTangent,
            hit.Normal,
            maximumOrientationDelta);
    }

    private static PointF CollisionSurfaceTangent(
        CollisionHit hit,
        IReadOnlyList<PointF[]> surfaceContours)
    {
        var targetTangent = hit.SurfaceTangent;
        if (DistanceSquared(targetTangent, PointF.Empty) <= 0.000001f)
        {
            targetTangent = new PointF(-hit.Normal.Y, hit.Normal.X);
            if (TryGetSurfaceTangent(
                    surfaceContours,
                    hit.ContactPoint,
                    targetTangent,
                    out var surfaceTangent))
            {
                targetTangent = surfaceTangent;
            }
        }

        return NormalizeDirection(targetTangent, new PointF(1f, 0f));
    }

    private static PointF StabilizeTerrainTangent(
        PointF current,
        PointF previous,
        bool hasPrevious,
        ref PointF pending,
        ref int pendingFrames)
    {
        current = NormalizeDirection(current, new PointF(1f, 0f));
        if (!hasPrevious)
        {
            pending = PointF.Empty;
            pendingFrames = 0;
            return current;
        }

        previous = NormalizeDirection(previous, current);
        if (Dot(current, previous) < 0f)
        {
            current = new PointF(-current.X, -current.Y);
        }

        var alignment = Dot(current, previous);
        if (alignment < TerrainContactDirectionSwitchDot)
        {
            var pendingAlignment = DistanceSquared(pending, PointF.Empty) > 0.000001f
                ? MathF.Abs(Dot(
                    current,
                    NormalizeDirection(pending, current)))
                : -1f;
            if (pendingAlignment >= TerrainContactDirectionSwitchDot)
            {
                pendingFrames++;
            }
            else
            {
                pending = current;
                pendingFrames = 1;
            }

            if (pendingFrames < 2) return previous;

            pending = PointF.Empty;
            pendingFrames = 0;
            return current;
        }

        pending = PointF.Empty;
        pendingFrames = 0;
        var blend = TerrainContactDirectionBlend;
        return NormalizeDirection(
            new PointF(
                previous.X * (1f - blend) + current.X * blend,
                previous.Y * (1f - blend) + current.Y * blend),
            current);
    }

    private static PointF StabilizeTerrainNormal(
        PointF current,
        PointF previous,
        bool hasPrevious)
    {
        current = NormalizeDirection(current, new PointF(0f, -1f));
        if (!hasPrevious) return current;

        previous = NormalizeDirection(previous, current);
        if (Dot(current, previous) < TerrainContactDirectionSwitchDot)
        {
            return current;
        }

        var blend = TerrainContactDirectionBlend;
        return NormalizeDirection(
            new PointF(
                previous.X * (1f - blend) + current.X * blend,
                previous.Y * (1f - blend) + current.Y * blend),
            current);
    }

    private static bool TryGetCompoundCollision(
        CollisionGeometry first,
        CollisionGeometry second,
        PointF normalHint,
        CancellationToken cancellationToken,
        out CollisionHit hit)
    {
        hit = default;
        if (first.Parts.Length == 0
            || second.Parts.Length == 0
            || !BoundsOverlap(first.Bounds, second.Bounds))
        {
            return false;
        }

        var complex = first.Parts.Length > 1
            || second.Parts.Length > 1
            || first.Contours.Length > 1
            || second.Contours.Length > 1;
        if (complex && !HasFilledRegionOverlap(first, second, cancellationToken)) return false;

        var found = false;
        foreach (var firstPart in first.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var secondPart in second.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!BoundsOverlap(firstPart.Bounds, secondPart.Bounds)
                    || !TryGetConvexCollision(firstPart, secondPart, normalHint, out var candidate))
                {
                    continue;
                }

                if (!found || candidate.Penetration < hit.Penetration)
                {
                    hit = candidate;
                    found = true;
                }
            }
        }

        if (!found) return false;

        // SAT over a convex decomposition is exact for convex pieces, but a
        // decomposition can report a hit through a hole or across two
        // disconnected islands. The exact filled-region test above prevents
        // those false contacts before the solver moves either fragment.
        if (complex
            && TryGetMinimumTranslationCollision(
                first,
                second,
                normalHint,
                cancellationToken,
                allowEdgePlaneFastPath: false,
                overlapAlreadyConfirmed: true,
                out var preciseHit))
        {
            hit = preciseHit;
        }

        return true;
    }

    private static bool TryGetTerrainCollision(
        CollisionGeometry terrain,
        CollisionGeometry fragment,
        PointF normalHint,
        CancellationToken cancellationToken,
        out CollisionHit hit)
    {
        hit = default;
        if (!BoundsOverlap(terrain.Bounds, fragment.Bounds)) return false;

        // The exact filled-region test is the authoritative collision test.
        // It handles concave boundaries, holes, islands, and a fragment that
        // crosses a thin terrain strip without relying on a vertex being
        // inside one of the triangulation pieces.
        if (!TryHasFilledRegionOverlap(
                terrain,
                fragment,
                cancellationToken,
                out var overlap)) return false;
        if (!overlap) return false;

        if (TryGetMinimumTranslationCollision(
                terrain,
                fragment,
                normalHint,
                cancellationToken,
                allowEdgePlaneFastPath: true,
                overlapAlreadyConfirmed: true,
                out hit)) return true;

        // A malformed legacy contour may still have a usable convex fallback.
        // Keep the fallback conservative, but never use it to turn a failed
        // exact overlap test into a terrain hit.
        return TryGetCompoundCollision(
            terrain,
            fragment,
            normalHint,
            cancellationToken,
            out hit);
    }

    private static bool HasFilledRegionOverlap(
        CollisionGeometry first,
        CollisionGeometry second,
        CancellationToken cancellationToken = default)
    {
        return ContoursOverlap(first, second, PointF.Empty, cancellationToken);
    }

    private static bool TryHasFilledRegionOverlap(
        CollisionGeometry first,
        CollisionGeometry second,
        CancellationToken cancellationToken,
        out bool overlap)
    {
        overlap = ContoursOverlap(first, second, PointF.Empty, cancellationToken);
        return true;
    }

    private static bool TryGetMinimumTranslationCollision(
        CollisionGeometry obstacle,
        CollisionGeometry moving,
        PointF normalHint,
        CancellationToken cancellationToken,
        bool allowEdgePlaneFastPath,
        bool overlapAlreadyConfirmed,
        out CollisionHit hit)
    {
        hit = default;
        if (!overlapAlreadyConfirmed
            && (!TryHasFilledRegionOverlap(obstacle, moving, cancellationToken, out var overlap) || !overlap))
        {
            return false;
        }

        var directions = CollisionDirections(obstacle, moving, normalHint, cancellationToken);
        var found = false;
        var bestDistance = float.MaxValue;
        var bestAlignment = float.MinValue;
        var bestDirection = PointF.Empty;
        CollisionEdge? bestSurface = null;
        var referenceNormal = NormalizeDirection(normalHint, new PointF(0f, -1f));
        var hasTerrainSurfaceDirections = allowEdgePlaneFastPath
            && obstacle.EdgeIndex is not null
            && directions.Any(candidate => candidate.Surface is not null);
        var directionPassCount = hasTerrainSurfaceDirections ? 2 : 1;
        for (var directionPass = 0; directionPass < directionPassCount; directionPass++)
        {
            var surfacePass = hasTerrainSurfaceDirections && directionPass == 0;
            if (!surfacePass && hasTerrainSurfaceDirections && found) break;
            for (var directionIndex = 0; directionIndex < directions.Count; directionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = directions[directionIndex];
                if (hasTerrainSurfaceDirections
                    && (candidate.Surface is not null) != surfacePass)
                {
                    continue;
                }

                if (!TryFindSeparationDistance(
                        obstacle,
                        moving,
                        candidate.Direction,
                        cancellationToken,
                        candidate.Surface,
                        allowEdgePlaneFastPath,
                        out var distance)) continue;
                var alignment = Dot(candidate.Direction, referenceNormal);
                if (found
                    && (distance > bestDistance + 0.005f
                        || MathF.Abs(distance - bestDistance) <= 0.005f
                        && alignment <= bestAlignment))
                {
                    continue;
                }

                found = true;
                bestDistance = distance;
                bestAlignment = alignment;
                bestDirection = candidate.Direction;
                bestSurface = candidate.Surface;
            }
        }

        if (!found) return false;
        var contactPoint = CollisionContactPoint(obstacle, moving, bestSurface);
        var tangent = bestSurface is { } surface
            ? NormalizeDirection(
                new PointF(surface.End.X - surface.Start.X, surface.End.Y - surface.Start.Y),
                new PointF(1f, 0f))
            : PointF.Empty;
        hit = new CollisionHit(
            bestDirection,
            Math.Max(0f, bestDistance),
            contactPoint,
            tangent);
        return true;
    }

    private static List<CollisionDirection> CollisionDirections(
        CollisionGeometry obstacle,
        CollisionGeometry moving,
        PointF normalHint,
        CancellationToken cancellationToken)
    {
        var result = new List<CollisionDirection>(MaximumTerrainContactCandidates + 16);
        AddCollisionDirection(result, normalHint, null, includeOpposite: true);

        // The closest finite edges cover a boundary crossing and a fragment
        // near an island boundary. Query only the local terrain cells; the
        // normal hint above still handles a fragment fully inside a large
        // island without scanning every terrain segment.
        var terrainEdgeCandidates = new List<(CollisionEdge Edge, float Distance)>();
        if (obstacle.EdgeIndex is { } edgeIndex)
        {
            var padding = Math.Max(
                2f,
                Math.Max(moving.Bounds.Width, moving.Bounds.Height) * 0.5f);
            var candidateIndices = new List<int>();
            for (var expansion = 0; expansion < MaximumTerrainContactSearchExpansions; expansion++)
            {
                var searchBounds = RectangleF.FromLTRB(
                    moving.Bounds.Left - padding,
                    moving.Bounds.Top - padding,
                    moving.Bounds.Right + padding,
                    moving.Bounds.Bottom + padding);
                edgeIndex.CollectCandidates(searchBounds, candidateIndices);
                foreach (var candidateIndex in candidateIndices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var edge = obstacle.Edges[candidateIndex];
                    if (!EdgeBoundsOverlap(edge, searchBounds)) continue;
                    terrainEdgeCandidates.Add((edge, EdgeDistanceToContours(edge, moving.Contours)));
                }

                if (terrainEdgeCandidates.Count > 0) break;
                padding *= 2f;
            }
        }
        else
        {
            terrainEdgeCandidates.AddRange(obstacle.Edges.Select(edge =>
                (edge, EdgeDistanceToContours(edge, moving.Contours))));
        }

        terrainEdgeCandidates.Sort((first, second) => first.Distance.CompareTo(second.Distance));
        for (var candidateIndex = 0;
             candidateIndex < terrainEdgeCandidates.Count
             && candidateIndex < MaximumTerrainContactCandidates;
             candidateIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = terrainEdgeCandidates[candidateIndex];
            AddCollisionDirection(result, candidate.Edge.Normal, candidate.Edge, includeOpposite: false);
        }

        if (moving.Edges.Length == 0) return result;
        var obstacleCenter = new PointF(
            (obstacle.Bounds.Left + obstacle.Bounds.Right) * 0.5f,
            (obstacle.Bounds.Top + obstacle.Bounds.Bottom) * 0.5f);
        foreach (var candidate in moving.Edges
                     .Select(edge => (Edge: edge, Distance: Distance(
                         obstacleCenter,
                         ClosestPointOnSegment(obstacleCenter, edge.Start, edge.End))))
                     .OrderBy(item => item.Distance)
                     .Take(MaximumTerrainContactCandidates / 2))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCollisionDirection(result, candidate.Edge.Normal, null, includeOpposite: true);
        }

        return result;
    }

    private static void AddCollisionDirection(
        List<CollisionDirection> directions,
        PointF direction,
        CollisionEdge? surface,
        bool includeOpposite)
    {
        direction = NormalizeDirection(direction, new PointF(0f, -1f));
        Add(direction, surface);
        if (includeOpposite) Add(new PointF(-direction.X, -direction.Y), null);

        void Add(PointF candidate, CollisionEdge? candidateSurface)
        {
            for (var index = 0; index < directions.Count; index++)
            {
                if (Dot(directions[index].Direction, candidate) < CollisionDirectionDeduplicationDot) continue;
                if (directions[index].Surface is null && candidateSurface is not null)
                {
                    directions[index] = directions[index] with { Surface = candidateSurface };
                }

                return;
            }

            directions.Add(new CollisionDirection(candidate, candidateSurface));
        }
    }

    private static bool TryFindSeparationDistance(
        CollisionGeometry obstacle,
        CollisionGeometry moving,
        PointF direction,
        CancellationToken cancellationToken,
        CollisionEdge? surface,
        bool allowEdgePlaneFastPath,
        out float distance)
    {
        distance = 0f;
        direction = NormalizeDirection(direction, new PointF(0f, -1f));

        if (allowEdgePlaneFastPath
            && surface is { } edge
            && Dot(direction, edge.Normal) >= 0.999f
            && TryProjectContours(moving.Contours, direction, out var fastMovingMinimum, out _))
        {
            // The terrain edge is oriented out of the filled region. Moving
            // the fragment until its support passes that finite edge plane is
            // a conservative local separation estimate. The exact overlap test
            // remains the authority, and the residual terrain passes below
            // correct any simultaneous contact with another contour.
            var edgePlane = Dot(edge.Start, direction);
            var edgeDistance = edgePlane - fastMovingMinimum + CollisionSeparation;
            if (float.IsFinite(edgeDistance) && edgeDistance > CollisionEpsilon)
            {
                distance = edgeDistance;
                return true;
            }
        }

        if (!TryProjectContours(obstacle.Contours, direction, out _, out var obstacleMaximum)
            || !TryProjectContours(moving.Contours, direction, out var movingMinimum, out _))
        {
            return false;
        }

        var upper = Math.Max(
            CollisionSeparation * 4f,
            obstacleMaximum - movingMinimum + CollisionSeparation + 1f);
        var overlappingAtUpperBound = false;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryHasFilledRegionOverlap(
                    obstacle,
                    moving,
                    new PointF(direction.X * upper, direction.Y * upper),
                    cancellationToken,
                    out overlappingAtUpperBound))
            {
                return false;
            }

            if (overlappingAtUpperBound) upper *= 2f;
            else break;
        }

        if (overlappingAtUpperBound || !float.IsFinite(upper)) return false;
        var lower = 0f;
        for (var iteration = 0; iteration < SeparationSearchIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var middle = (lower + upper) * 0.5f;
            if (!TryHasFilledRegionOverlap(
                    obstacle,
                    moving,
                    new PointF(direction.X * middle, direction.Y * middle),
                    cancellationToken,
                    out var stillOverlapping))
            {
                return false;
            }

            if (stillOverlapping) lower = middle;
            else upper = middle;
        }

        distance = upper;
        return float.IsFinite(distance);
    }

    private static bool TryHasFilledRegionOverlap(
        CollisionGeometry first,
        CollisionGeometry second,
        PointF translation,
        CancellationToken cancellationToken,
        out bool overlap)
    {
        overlap = ContoursOverlap(first, second, translation, cancellationToken);
        return true;
    }

    private static bool ContoursOverlap(
        CollisionGeometry first,
        CollisionGeometry second,
        PointF secondTranslation,
        CancellationToken cancellationToken = default)
    {
        var firstContours = first.Contours;
        var secondContours = second.Contours;
        if (firstContours.Length == 0 || secondContours.Length == 0) return false;
        if (!float.IsFinite(secondTranslation.X) || !float.IsFinite(secondTranslation.Y)) return false;

        var firstBounds = first.Bounds;
        var secondBounds = second.Bounds;
        var translatedSecondBounds = RectangleF.FromLTRB(
            secondBounds.Left + secondTranslation.X,
            secondBounds.Top + secondTranslation.Y,
            secondBounds.Right + secondTranslation.X,
            secondBounds.Bottom + secondTranslation.Y);
        if (!BoundsOverlap(firstBounds, translatedSecondBounds)) return false;

        // A positive-area polygon intersection must either cross a boundary
        // or contain a vertex of one region in the other. Use the static edge
        // index whenever one is available; MTD refinement calls this method
        // repeatedly, so scanning every terrain edge here would dominate the
        // simulation cost for hand-drawn terrain.
        var candidates = new List<int>();
        if (first.EdgeIndex is { } firstEdgeIndex)
        {
            foreach (var secondEdge in second.Edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var secondStart = new PointF(
                    secondEdge.Start.X + secondTranslation.X,
                    secondEdge.Start.Y + secondTranslation.Y);
                var secondEnd = new PointF(
                    secondEdge.End.X + secondTranslation.X,
                    secondEdge.End.Y + secondTranslation.Y);
                firstEdgeIndex.CollectCandidates(SegmentBounds(secondStart, secondEnd), candidates);
                foreach (var firstEdgeIndexValue in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var firstEdge = first.Edges[firstEdgeIndexValue];
                    if (!SegmentsBoundsOverlap(
                            firstEdge.Start,
                            firstEdge.End,
                            secondStart,
                            secondEnd)
                        || !TryGetSegmentIntersection(
                            firstEdge.Start,
                            firstEdge.End,
                            secondStart,
                            secondEnd,
                            out _)) continue;
                    return true;
                }
            }
        }
        else if (second.EdgeIndex is { } secondEdgeIndex)
        {
            foreach (var firstEdge in first.Edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var queryBounds = SegmentBounds(firstEdge.Start, firstEdge.End);
                queryBounds = RectangleF.FromLTRB(
                    queryBounds.Left - secondTranslation.X,
                    queryBounds.Top - secondTranslation.Y,
                    queryBounds.Right - secondTranslation.X,
                    queryBounds.Bottom - secondTranslation.Y);
                secondEdgeIndex.CollectCandidates(queryBounds, candidates);
                foreach (var secondEdgeIndexValue in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var secondEdge = second.Edges[secondEdgeIndexValue];
                    var secondStart = new PointF(
                        secondEdge.Start.X + secondTranslation.X,
                        secondEdge.Start.Y + secondTranslation.Y);
                    var secondEnd = new PointF(
                        secondEdge.End.X + secondTranslation.X,
                        secondEdge.End.Y + secondTranslation.Y);
                    if (!SegmentsBoundsOverlap(
                            firstEdge.Start,
                            firstEdge.End,
                            secondStart,
                            secondEnd)
                        || !TryGetSegmentIntersection(
                            firstEdge.Start,
                            firstEdge.End,
                            secondStart,
                            secondEnd,
                            out _)) continue;
                    return true;
                }
            }
        }
        else
        {
            foreach (var firstContour in firstContours)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (firstContour is not { Length: >= 2 }) continue;
                for (var firstIndex = 0; firstIndex < firstContour.Length; firstIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var firstStart = firstContour[firstIndex];
                    var firstEnd = firstContour[(firstIndex + 1) % firstContour.Length];
                    for (var secondContourIndex = 0; secondContourIndex < secondContours.Length; secondContourIndex++)
                    {
                        var secondContour = secondContours[secondContourIndex];
                        if (secondContour is not { Length: >= 2 }) continue;
                        for (var secondIndex = 0; secondIndex < secondContour.Length; secondIndex++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var secondStart = new PointF(
                                secondContour[secondIndex].X + secondTranslation.X,
                                secondContour[secondIndex].Y + secondTranslation.Y);
                            var secondEnd = new PointF(
                                secondContour[(secondIndex + 1) % secondContour.Length].X + secondTranslation.X,
                                secondContour[(secondIndex + 1) % secondContour.Length].Y + secondTranslation.Y);
                            if (!SegmentsBoundsOverlap(firstStart, firstEnd, secondStart, secondEnd)) continue;
                            if (TryGetSegmentIntersection(
                                    firstStart,
                                    firstEnd,
                                    secondStart,
                                    secondEnd,
                                    out _))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }

        if (first.EdgeIndex is { } vertexIndex)
        {
            vertexIndex.CollectCandidates(translatedSecondBounds, candidates);
            foreach (var candidateIndex in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var edge = first.Edges[candidateIndex];
                if (!EdgeBoundsOverlap(edge, translatedSecondBounds)) continue;
                if (PointInCollisionGeometry(
                        new PointF(
                            edge.Start.X - secondTranslation.X,
                            edge.Start.Y - secondTranslation.Y),
                        second,
                        cancellationToken)
                    || PointInCollisionGeometry(
                        new PointF(
                            edge.End.X - secondTranslation.X,
                            edge.End.Y - secondTranslation.Y),
                        second,
                        cancellationToken))
                {
                    return true;
                }
            }
        }
        else
        {
            foreach (var contour in firstContours)
            {
                foreach (var point in contour)
                {
                    if (!PointInBounds(point, translatedSecondBounds)) continue;
                    if (PointInCollisionGeometry(
                            new PointF(
                                point.X - secondTranslation.X,
                                point.Y - secondTranslation.Y),
                            second,
                            cancellationToken))
                    {
                        return true;
                    }
                }
            }
        }

        foreach (var contour in secondContours)
        {
            foreach (var point in contour)
            {
                var translated = new PointF(
                    point.X + secondTranslation.X,
                    point.Y + secondTranslation.Y);
                if (!PointInBounds(translated, firstBounds)) continue;
                if (PointInCollisionGeometry(translated, first, cancellationToken)) return true;
            }
        }

        return false;
    }

    private static bool TryProjectContours(
        IReadOnlyList<PointF[]> contours,
        PointF axis,
        out float minimum,
        out float maximum)
    {
        minimum = float.MaxValue;
        maximum = float.MinValue;
        foreach (var contour in contours)
        {
            foreach (var point in contour)
            {
                var projection = Dot(point, axis);
                if (!float.IsFinite(projection)) return false;
                minimum = Math.Min(minimum, projection);
                maximum = Math.Max(maximum, projection);
            }
        }

        return minimum != float.MaxValue && maximum != float.MinValue;
    }

    private static PointF CollisionContactPoint(
        CollisionGeometry obstacle,
        CollisionGeometry moving,
        CollisionEdge? preferredSurface)
    {
        var bestPoint = PointF.Empty;
        var bestDistance = float.MaxValue;
        var surfaces = preferredSurface is { } preferred
            ? new[] { preferred }
            : obstacle.Edges;
        foreach (var surface in surfaces)
        {
            foreach (var contour in moving.Contours)
            {
                if (contour is not { Length: >= 2 }) continue;
                for (var index = 0; index < contour.Length; index++)
                {
                    if (TryGetSegmentIntersection(
                            surface.Start,
                            surface.End,
                            contour[index],
                            contour[(index + 1) % contour.Length],
                            out var intersection))
                    {
                        return intersection;
                    }
                }

                for (var index = 0; index < contour.Length; index++)
                {
                    var point = contour[index];
                    var closest = ClosestPointOnSegment(point, surface.Start, surface.End);
                    var distance = Distance(point, closest);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    bestPoint = closest;
                }
            }
        }

        if (bestDistance < float.MaxValue) return bestPoint;
        return new PointF(
            (obstacle.Bounds.Left + obstacle.Bounds.Right) * 0.5f,
            (obstacle.Bounds.Top + obstacle.Bounds.Bottom) * 0.5f);
    }

    private static float EdgeDistanceToContours(
        CollisionEdge edge,
        IReadOnlyList<PointF[]> contours)
    {
        var best = float.MaxValue;
        foreach (var contour in contours)
        {
            if (contour is not { Length: >= 2 }) continue;
            for (var index = 0; index < contour.Length; index++)
            {
                var point = contour[index];
                var next = contour[(index + 1) % contour.Length];
                if (TryGetSegmentIntersection(edge.Start, edge.End, point, next, out _)) return 0f;
                best = Math.Min(
                    best,
                    Distance(point, ClosestPointOnSegment(point, edge.Start, edge.End)));
                best = Math.Min(
                    best,
                    Distance(edge.Start, ClosestPointOnSegment(edge.Start, point, next)));
                best = Math.Min(
                    best,
                    Distance(edge.End, ClosestPointOnSegment(edge.End, point, next)));
            }
        }

        return best;
    }

    private static bool TryGetTerrainEdgeNormal(
        IReadOnlyList<PointF[]> terrainContours,
        IReadOnlyList<PointF> terrainContour,
        PointF start,
        PointF end,
        double contourWinding,
        PointF referenceNormal,
        out PointF normal)
    {
        normal = PointF.Empty;
        var edge = new PointF(end.X - start.X, end.Y - start.Y);
        var length = MathF.Sqrt(edge.X * edge.X + edge.Y * edge.Y);
        if (length <= 0.000001f) return false;

        var leftNormal = new PointF(-edge.Y / length, edge.X / length);
        var rightNormal = new PointF(-leftNormal.X, -leftNormal.Y);

        // Clipper-normalized islands and holes already carry a consistent
        // winding. Use it first so a terrain with thousands of vertices does
        // not perform a full point-in-region probe for every edge.
        if (Math.Abs(contourWinding) > 0.000001d)
        {
            normal = contourWinding > 0d ? rightNormal : leftNormal;
            return true;
        }

        var midpoint = new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        var probeDistance = Math.Min(TerrainProbeDistance, Math.Max(0.02f, length * 0.2f));
        for (var probe = probeDistance; probe >= 0.005f; probe *= 0.25f)
        {
            var leftProbe = new PointF(
                midpoint.X + leftNormal.X * probe,
                midpoint.Y + leftNormal.Y * probe);
            var rightProbe = new PointF(
                midpoint.X + rightNormal.X * probe,
                midpoint.Y + rightNormal.Y * probe);
            var leftInside = PointInContours(leftProbe, terrainContours);
            var rightInside = PointInContours(rightProbe, terrainContours);
            if (leftInside != rightInside)
            {
                normal = leftInside ? rightNormal : leftNormal;
                return true;
            }
        }

        normal = Dot(leftNormal, referenceNormal) >= 0f ? leftNormal : rightNormal;
        return true;
    }

    private static bool TryGetConvexCollision(
        CollisionPolygon first,
        CollisionPolygon second,
        PointF normalHint,
        out CollisionHit hit)
    {
        hit = default;
        var minimumOverlap = float.MaxValue;
        var minimumAxis = PointF.Empty;
        for (var polygonIndex = 0; polygonIndex < 2; polygonIndex++)
        {
            var polygon = polygonIndex == 0 ? first.Points : second.Points;
            for (var pointIndex = 0; pointIndex < polygon.Length; pointIndex++)
            {
                var start = polygon[pointIndex];
                var end = polygon[(pointIndex + 1) % polygon.Length];
                var edge = new PointF(end.X - start.X, end.Y - start.Y);
                var length = MathF.Sqrt(edge.X * edge.X + edge.Y * edge.Y);
                if (length <= 0.000001f) continue;

                var axis = new PointF(-edge.Y / length, edge.X / length);
                ProjectPolygon(first.Points, axis, out var firstMinimum, out var firstMaximum);
                ProjectPolygon(second.Points, axis, out var secondMinimum, out var secondMaximum);
                var overlap = MathF.Min(firstMaximum, secondMaximum)
                    - MathF.Max(firstMinimum, secondMinimum);
                if (overlap <= CollisionEpsilon) return false;
                if (overlap < minimumOverlap)
                {
                    minimumOverlap = overlap;
                    minimumAxis = axis;
                }
            }
        }

        if (minimumOverlap == float.MaxValue) return false;
        var direction = new PointF(
            (second.Bounds.Left + second.Bounds.Right - first.Bounds.Left - first.Bounds.Right) * 0.5f,
            (second.Bounds.Top + second.Bounds.Bottom - first.Bounds.Top - first.Bounds.Bottom) * 0.5f);
        if (direction.X * direction.X + direction.Y * direction.Y <= 0.000001f)
        {
            direction = normalHint;
        }

        if (direction.X * direction.X + direction.Y * direction.Y > 0.000001f)
        {
            if (Dot(direction, minimumAxis) < 0f)
            {
                minimumAxis = new PointF(-minimumAxis.X, -minimumAxis.Y);
            }
        }

        var firstSupport = SupportPoint(first.Points, minimumAxis, maximize: true);
        var secondSupport = SupportPoint(second.Points, minimumAxis, maximize: false);
        hit = new CollisionHit(
            minimumAxis,
            minimumOverlap,
            new PointF(
                (firstSupport.X + secondSupport.X) * 0.5f,
                (firstSupport.Y + secondSupport.Y) * 0.5f));
        return true;
    }

    private static void ProjectPolygon(
        IReadOnlyList<PointF> polygon,
        PointF axis,
        out float minimum,
        out float maximum)
    {
        minimum = float.MaxValue;
        maximum = float.MinValue;
        foreach (var point in polygon)
        {
            var projection = Dot(point, axis);
            minimum = Math.Min(minimum, projection);
            maximum = Math.Max(maximum, projection);
        }
    }

    private static PointF SupportPoint(
        IReadOnlyList<PointF> points,
        PointF direction,
        bool maximize)
    {
        var best = maximize ? float.MinValue : float.MaxValue;
        var sum = PointF.Empty;
        var count = 0;
        foreach (var point in points)
        {
            var value = Dot(point, direction);
            if (maximize ? value > best + SupportPointTolerance : value < best - SupportPointTolerance)
            {
                best = value;
                sum = point;
                count = 1;
            }
            else if (MathF.Abs(value - best) <= SupportPointTolerance)
            {
                sum = new PointF(sum.X + point.X, sum.Y + point.Y);
                count++;
            }
        }

        return count == 0
            ? PointF.Empty
            : new PointF(sum.X / count, sum.Y / count);
    }

    private static bool TryGetGroundContact(
        CollisionGeometry geometry,
        float ground,
        out CollisionHit hit)
    {
        hit = default;
        var maximumY = float.MinValue;
        var support = PointF.Empty;
        var supportCount = 0;
        foreach (var part in geometry.Parts)
        {
            foreach (var point in part.Points)
            {
                if (point.Y > maximumY + 0.05f)
                {
                    maximumY = point.Y;
                    support = point;
                    supportCount = 1;
                }
                else if (MathF.Abs(point.Y - maximumY) <= 0.05f)
                {
                    support = new PointF(support.X + point.X, support.Y + point.Y);
                    supportCount++;
                }
            }
        }

        if (supportCount == 0 || maximumY <= ground + CollisionEpsilon) return false;
        hit = new CollisionHit(
            new PointF(0f, -1f),
            maximumY - ground,
            new PointF(support.X / supportCount, ground));
        return true;
    }

    private static bool TryGetSurfaceTangent(
        IReadOnlyList<PointF[]> contours,
        PointF contactPoint,
        PointF preferredTangent,
        out PointF tangent)
    {
        tangent = PointF.Empty;
        var preferredLength = MathF.Sqrt(
            preferredTangent.X * preferredTangent.X
            + preferredTangent.Y * preferredTangent.Y);
        if (preferredLength <= 0.000001f) preferredTangent = new PointF(1f, 0f);
        else preferredTangent = new PointF(
            preferredTangent.X / preferredLength,
            preferredTangent.Y / preferredLength);

        var bestScore = float.MaxValue;
        foreach (var contour in contours)
        {
            if (contour is not { Length: >= 2 }) continue;
            for (var index = 0; index < contour.Length; index++)
            {
                var start = contour[index];
                var end = contour[(index + 1) % contour.Length];
                var edge = new PointF(end.X - start.X, end.Y - start.Y);
                var length = MathF.Sqrt(edge.X * edge.X + edge.Y * edge.Y);
                if (length <= 0.000001f) continue;
                var candidate = new PointF(edge.X / length, edge.Y / length);
                var alignment = MathF.Abs(Dot(candidate, preferredTangent));
                var distance = Distance(contactPoint, ClosestPointOnSegment(contactPoint, start, end));
                var score = distance * 0.1f + (1f - alignment) * 64f;
                if (score >= bestScore) continue;
                bestScore = score;
                tangent = candidate;
            }
        }

        return bestScore < float.MaxValue;
    }

    private static void AlignFragmentToSurface(
        ref FragmentState state,
        IReadOnlyList<PointF[]> localContours,
        PointF contactPoint,
        PointF targetTangent,
        PointF surfaceNormal,
        float? maximumRotationDelta = null)
    {
        var targetLength = MathF.Sqrt(targetTangent.X * targetTangent.X + targetTangent.Y * targetTangent.Y);
        if (targetLength <= 0.000001f) return;
        targetTangent = new PointF(targetTangent.X / targetLength, targetTangent.Y / targetLength);
        surfaceNormal = NormalizeDirection(surfaceNormal, new PointF(0f, -1f));
        var towardSurface = new PointF(-surfaceNormal.X, -surfaceNormal.Y);

        var sine = MathF.Sin(state.Angle);
        var cosine = MathF.Cos(state.Angle);
        var maximumSupport = float.MinValue;
        foreach (var contour in localContours)
        {
            foreach (var point in contour)
            {
                var worldOffset = new PointF(
                    point.X * cosine - point.Y * sine,
                    point.X * sine + point.Y * cosine);
                maximumSupport = Math.Max(maximumSupport, Dot(worldOffset, towardSurface));
            }
        }

        var bestScore = float.MaxValue;
        var bestAngle = 0f;
        foreach (var collisionEdge in state.Geometry.Edges)
        {
            var start = TransformLocalPoint(collisionEdge.Start, state.Position, sine, cosine);
            var end = TransformLocalPoint(collisionEdge.End, state.Position, sine, cosine);
            var edge = new PointF(end.X - start.X, end.Y - start.Y);
            var length = MathF.Sqrt(edge.X * edge.X + edge.Y * edge.Y);
            if (length <= 0.000001f) continue;
            var candidate = new PointF(edge.X / length, edge.Y / length);
            var alignment = MathF.Abs(Dot(candidate, targetTangent));
            var edgeNormal = NormalizeDirection(
                new PointF(
                    collisionEdge.Normal.X * cosine - collisionEdge.Normal.Y * sine,
                    collisionEdge.Normal.X * sine + collisionEdge.Normal.Y * cosine),
                towardSurface);
            var facingAlignment = Dot(edgeNormal, towardSurface);
            var distance = Distance(contactPoint, ClosestPointOnSegment(contactPoint, start, end));
            var midpoint = new PointF(
                (start.X + end.X) * 0.5f,
                (start.Y + end.Y) * 0.5f);
            var support = Dot(
                new PointF(midpoint.X - state.Position.X, midpoint.Y - state.Position.Y),
                towardSurface);
            var supportDeficit = Math.Max(0f, maximumSupport - support);
            var score = distance * 0.05f
                + (1f - alignment) * 128f
                + (1f - MathF.Max(0f, facingAlignment)) * 160f
                + supportDeficit * 0.05f;
            if (score >= bestScore) continue;
            bestScore = score;
            bestAngle = MathF.Atan2(edge.Y, edge.X);
        }

        if (bestScore == float.MaxValue) return;
        var targetAngle = MathF.Atan2(targetTangent.Y, targetTangent.X);
        var delta = NormalizeLineAngle(targetAngle - bestAngle);
        if (maximumRotationDelta is { } maximumDelta
            && float.IsFinite(maximumDelta))
        {
            delta = Math.Clamp(delta, -maximumDelta, maximumDelta);
        }

        if (MathF.Abs(delta) > 0.00001f) state.Angle += delta;
    }

    private static float NormalizeLineAngle(float angle)
    {
        while (angle > MathF.PI * 0.5f) angle -= MathF.PI;
        while (angle < -MathF.PI * 0.5f) angle += MathF.PI;
        return angle;
    }

    private static RectangleF SweptCollisionBounds(
        CollisionGeometry geometry,
        PointF startPosition,
        float startAngle,
        PointF endPosition,
        float endAngle)
    {
        var startBounds = TransformCollisionBounds(geometry.Bounds, startPosition, startAngle);
        var endBounds = TransformCollisionBounds(geometry.Bounds, endPosition, endAngle);
        var radius = GeometryRadius(geometry.Contours);
        var angleDelta = MathF.Abs(endAngle - startAngle);
        var rotationPadding = !float.IsFinite(radius)
            ? 0f
            : Math.Min(radius * Math.Min(angleDelta, 2f), radius * 2f);
        if (!float.IsFinite(rotationPadding)) rotationPadding = 0f;

        return RectangleF.FromLTRB(
            Math.Min(startBounds.Left, endBounds.Left) - rotationPadding - CollisionEpsilon,
            Math.Min(startBounds.Top, endBounds.Top) - rotationPadding - CollisionEpsilon,
            Math.Max(startBounds.Right, endBounds.Right) + rotationPadding + CollisionEpsilon,
            Math.Max(startBounds.Bottom, endBounds.Bottom) + rotationPadding + CollisionEpsilon);
    }

    private static RectangleF TransformCollisionBounds(
        RectangleF bounds,
        PointF position,
        float angle)
    {
        var sine = MathF.Sin(angle);
        var cosine = MathF.Cos(angle);
        var topLeft = TransformLocalPoint(new PointF(bounds.Left, bounds.Top), position, sine, cosine);
        var topRight = TransformLocalPoint(new PointF(bounds.Right, bounds.Top), position, sine, cosine);
        var bottomLeft = TransformLocalPoint(new PointF(bounds.Left, bounds.Bottom), position, sine, cosine);
        var bottomRight = TransformLocalPoint(new PointF(bounds.Right, bounds.Bottom), position, sine, cosine);
        var left = MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomLeft.X, bottomRight.X));
        var right = MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomLeft.X, bottomRight.X));
        var top = MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomLeft.Y, bottomRight.Y));
        var bottom = MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomLeft.Y, bottomRight.Y));
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static CollisionGeometry BuildCollisionGeometry(
        IReadOnlyList<PointF[]> contours,
        PointF anchor,
        bool triangulate = true,
        bool buildEdgeIndex = false,
        CancellationToken cancellationToken = default)
    {
        var rawContours = contours
            .Where(contour => contour is { Length: >= 3 })
            .Select(contour => NormalizeCollisionContour(contour, anchor))
            .Where(contour => contour.Length >= 3)
            .ToArray();
        var normalizedContours = NormalizeCollisionRegion(rawContours);
        var parts = triangulate
            ? BuildConvexCollisionParts(normalizedContours)
                .Where(part => part.Length >= 3)
                .Select(part => new CollisionPolygon(part, BoundsFromPoints(part)))
                .Where(part => part.Bounds.Width > 0f && part.Bounds.Height > 0f)
                .ToArray()
            : [];
        var bounds = BoundsFromContours(normalizedContours);
        var edges = BuildCollisionEdges(normalizedContours, cancellationToken);
        return new CollisionGeometry(
            parts,
            normalizedContours,
            bounds,
            edges,
            buildEdgeIndex && edges.Length > 0
                ? new CollisionEdgeIndex(edges, bounds)
                : null);
    }

    private static PointF[][] NormalizeCollisionRegion(IReadOnlyList<PointF[]> contours)
    {
        if (contours.Count == 0) return [];

        var paths = ToCollisionPaths(contours);
        if (paths.Count == 0) return [];
        try
        {
            var union = Clipper.Union(paths, FillRule.EvenOdd);
            var normalized = FromCollisionPaths(union);
            return normalized.Length > 0 ? normalized : contours.ToArray();
        }
        catch (Exception exception) when (exception is
            ClipperLibException or OverflowException or ArgumentException or InvalidOperationException)
        {
            // Keep the cleaned contours available for the exact boundary
            // fallback. A bad legacy contour must not disable all terrain.
            return contours.ToArray();
        }
    }

    private static Paths64 ToCollisionPaths(IReadOnlyList<PointF[]> contours)
    {
        var paths = new Paths64(contours.Count);
        foreach (var contour in contours)
        {
            var path = ToCollisionPath(contour);
            if (path.Count >= 3 && Math.Abs(Clipper.Area(path)) >= CollisionMinimumArea)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private static Path64 ToCollisionPath(IReadOnlyList<PointF> points)
    {
        var path = new Path64(points.Count);
        foreach (var point in points)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) continue;
            path.Add(new Point64(
                checked((long)Math.Round(point.X * CoordinateScale, MidpointRounding.AwayFromZero)),
                checked((long)Math.Round(point.Y * CoordinateScale, MidpointRounding.AwayFromZero))));
        }

        return Clipper.StripDuplicates(path, true);
    }

    private static PointF[][] FromCollisionPaths(Paths64 paths)
    {
        var contours = new List<PointF[]>(paths.Count);
        foreach (var path in paths)
        {
            if (path.Count < 3 || Math.Abs(Clipper.Area(path)) < CollisionMinimumArea) continue;
            var contour = new PointF[path.Count];
            for (var index = 0; index < path.Count; index++)
            {
                contour[index] = new PointF(
                    (float)(path[index].X / CoordinateScale),
                    (float)(path[index].Y / CoordinateScale));
            }

            if (contour.Length >= 3) contours.Add(contour);
        }

        return contours.ToArray();
    }

    private static CollisionEdge[] BuildCollisionEdges(
        IReadOnlyList<PointF[]> contours,
        CancellationToken cancellationToken = default)
    {
        var edges = new List<CollisionEdge>();
        foreach (var contour in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (contour is not { Length: >= 2 }) continue;
            var winding = SignedArea(contour);
            for (var index = 0; index < contour.Length; index++)
            {
                if ((index & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                var start = contour[index];
                var end = contour[(index + 1) % contour.Length];
                if (!TryGetTerrainEdgeNormal(
                        contours,
                        contour,
                        start,
                        end,
                        winding,
                        new PointF(0f, -1f),
                        out var normal))
                {
                    continue;
                }

                edges.Add(new CollisionEdge(start, end, normal));
            }
        }

        return edges.ToArray();
    }

    private static PointF[] NormalizeCollisionContour(
        IReadOnlyList<PointF> contour,
        PointF anchor)
    {
        var points = new List<PointF>(contour.Count);
        foreach (var point in contour)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) continue;
            var normalized = new PointF(point.X - anchor.X, point.Y - anchor.Y);
            if (points.Count == 0
                || DistanceSquared(points[^1], normalized) > 0.000001f)
            {
                points.Add(normalized);
            }
        }

        if (points.Count > 1 && DistanceSquared(points[0], points[^1]) <= 0.000001f)
        {
            points.RemoveAt(points.Count - 1);
        }

        return points.ToArray();
    }

    private static PointF[][] BuildConvexCollisionParts(IReadOnlyList<PointF[]> contours)
    {
        if (contours.Count == 0) return [];
        if (contours.Count == 1 && IsConvexPolygon(contours[0])) return [contours[0]];

        try
        {
            var paths = new PathsD(contours.Select(contour =>
                new PathD(contour.Select(point => new PointD(point.X, point.Y)))));
            if (Clipper.Triangulate(
                    paths,
                    CollisionTriangulationPrecision,
                    out var triangles,
                    false) == TriangulateResult.success)
            {
                    var result = triangles
                    .Where(triangle => triangle.Count >= 3)
                    .Select(triangle => triangle
                        .Select(point => new PointF((float)point.x, (float)point.y))
                        .ToArray())
                    .Where(triangle => Math.Abs(SignedArea(triangle)) > 0.000001d)
                    .ToArray();
                if (result.Length > 0) return result;
            }
        }
        catch (Exception exception) when (exception is
            ClipperLibException or OverflowException or ArgumentException or InvalidOperationException)
        {
            // Ear clipping below keeps a malformed or unsupported Clipper input
            // from silently reverting to a coarse bounding shape.
        }

        // Ear clipping is valid for simple islands only. Applying it to a
        // hole would fill the hole and create false terrain collisions.
        var dominantWinding = contours
            .Select(SignedArea)
            .Where(area => Math.Abs(area) > 0.000001d)
            .OrderByDescending(area => Math.Abs(area))
            .Select(Math.Sign)
            .FirstOrDefault();
        if (dominantWinding == 0) return [];

        return contours
            .Where(contour => Math.Sign(SignedArea(contour)) == dominantWinding)
            .SelectMany(TriangulateByEarClipping)
            .ToArray();
    }

    private static PointF[][] TriangulateByEarClipping(IReadOnlyList<PointF> contour)
    {
        if (contour.Count < 3) return [];
        var points = contour.ToList();
        if (SignedArea(points) < 0d) points.Reverse();
        var remaining = Enumerable.Range(0, points.Count).ToList();
        var triangles = new List<PointF[]>(Math.Max(1, points.Count - 2));
        var guard = points.Count * points.Count;
        while (remaining.Count > 3 && guard-- > 0)
        {
            var clipped = false;
            for (var index = 0; index < remaining.Count; index++)
            {
                var previous = points[remaining[(index + remaining.Count - 1) % remaining.Count]];
                var current = points[remaining[index]];
                var next = points[remaining[(index + 1) % remaining.Count]];
                if (Cross(previous, current, next) <= 0.000001f) continue;

                var containsPoint = false;
                for (var otherIndex = 0; otherIndex < remaining.Count; otherIndex++)
                {
                    var candidate = points[remaining[otherIndex]];
                    if (candidate == previous || candidate == current || candidate == next) continue;
                    if (PointInTriangle(candidate, previous, current, next))
                    {
                        containsPoint = true;
                        break;
                    }
                }

                if (containsPoint) continue;
                triangles.Add([previous, current, next]);
                remaining.RemoveAt(index);
                clipped = true;
                break;
            }

            if (!clipped) return [];
        }

        if (remaining.Count == 3)
        {
            triangles.Add([
                points[remaining[0]],
                points[remaining[1]],
                points[remaining[2]]]);
        }

        return triangles.ToArray();
    }

    private static bool IsConvexPolygon(IReadOnlyList<PointF> polygon)
    {
        var sign = 0d;
        for (var index = 0; index < polygon.Count; index++)
        {
            var cross = Cross(
                polygon[index],
                polygon[(index + 1) % polygon.Count],
                polygon[(index + 2) % polygon.Count]);
            if (Math.Abs(cross) <= 0.000001f) continue;
            if (sign == 0d) sign = Math.Sign(cross);
            else if (sign * cross < 0d) return false;
        }

        return sign != 0d;
    }

    private static bool PointInTriangle(PointF point, PointF first, PointF second, PointF third)
    {
        var firstCross = Cross(first, second, point);
        var secondCross = Cross(second, third, point);
        var thirdCross = Cross(third, first, point);
        return firstCross >= -0.000001f
            && secondCross >= -0.000001f
            && thirdCross >= -0.000001f;
    }

    private static float Cross(PointF first, PointF second, PointF third)
        => (second.X - first.X) * (third.Y - first.Y)
            - (second.Y - first.Y) * (third.X - first.X);

    private static double SignedArea(IReadOnlyList<PointF> contour)
    {
        var area = 0d;
        for (var index = 0; index < contour.Count; index++)
        {
            var current = contour[index];
            var next = contour[(index + 1) % contour.Count];
            area += (double)current.X * next.Y - (double)next.X * current.Y;
        }

        return area * 0.5d;
    }

    private static PointF ClosestPointOnAnyContour(
        PointF point,
        IReadOnlyList<PointF[]> contours)
    {
        var best = PointF.Empty;
        var bestDistance = float.MaxValue;
        foreach (var contour in contours)
        {
            if (contour is not { Length: >= 2 }) continue;
            for (var index = 0; index < contour.Length; index++)
            {
                var candidate = ClosestPointOnSegment(
                    point,
                    contour[index],
                    contour[(index + 1) % contour.Length]);
                var distance = Distance(point, candidate);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private static bool TryGetSegmentIntersection(
        PointF firstStart,
        PointF firstEnd,
        PointF secondStart,
        PointF secondEnd,
        out PointF intersection)
    {
        intersection = PointF.Empty;
        var firstDirection = new PointF(
            firstEnd.X - firstStart.X,
            firstEnd.Y - firstStart.Y);
        var secondDirection = new PointF(
            secondEnd.X - secondStart.X,
            secondEnd.Y - secondStart.Y);
        var offset = new PointF(
            secondStart.X - firstStart.X,
            secondStart.Y - firstStart.Y);
        var denominator = CrossVector(firstDirection, secondDirection);
        var firstLengthSquared = Dot(firstDirection, firstDirection);
        var secondLengthSquared = Dot(secondDirection, secondDirection);
        if (firstLengthSquared <= 0.000001f || secondLengthSquared <= 0.000001f)
        {
            if (firstLengthSquared <= 0.000001f
                && PointOnSegment(firstStart, secondStart, secondEnd))
            {
                intersection = firstStart;
                return true;
            }

            if (secondLengthSquared <= 0.000001f
                && PointOnSegment(secondStart, firstStart, firstEnd))
            {
                intersection = secondStart;
                return true;
            }

            return false;
        }

        var scale = Math.Max(
            1d,
            Math.Max(
                Math.Abs((double)firstDirection.X * secondDirection.Y),
                Math.Abs((double)firstDirection.Y * secondDirection.X)));
        var epsilon = SegmentIntersectionEpsilon * scale;
        if (Math.Abs(denominator) > epsilon)
        {
            var firstParameter = (double)CrossVector(offset, secondDirection) / denominator;
            var secondParameter = (double)CrossVector(offset, firstDirection) / denominator;
            const double parameterTolerance = 0.000001d;
            if (firstParameter < -parameterTolerance
                || firstParameter > 1d + parameterTolerance
                || secondParameter < -parameterTolerance
                || secondParameter > 1d + parameterTolerance)
            {
                return false;
            }

            firstParameter = Math.Clamp(firstParameter, 0d, 1d);
            intersection = new PointF(
                (float)(firstStart.X + firstDirection.X * firstParameter),
                (float)(firstStart.Y + firstDirection.Y * firstParameter));
            return float.IsFinite(intersection.X) && float.IsFinite(intersection.Y);
        }

        if (Math.Abs(CrossVector(offset, firstDirection)) > epsilon) return false;

        // Collinear edges still count as contact. Return the midpoint of their
        // overlap to give the solver a stable contact location.
        var firstParameterSecondStart = (double)Dot(offset, firstDirection) / firstLengthSquared;
        var firstParameterSecondEnd = (double)Dot(
                new PointF(secondEnd.X - firstStart.X, secondEnd.Y - firstStart.Y),
                firstDirection)
            / firstLengthSquared;
        var overlapStart = Math.Max(0d, Math.Min(firstParameterSecondStart, firstParameterSecondEnd));
        var overlapEnd = Math.Min(1d, Math.Max(firstParameterSecondStart, firstParameterSecondEnd));
        if (overlapEnd < overlapStart - 0.000001d) return false;

        var parameter = Math.Clamp((overlapStart + overlapEnd) * 0.5d, 0d, 1d);
        intersection = new PointF(
            (float)(firstStart.X + firstDirection.X * parameter),
            (float)(firstStart.Y + firstDirection.Y * parameter));
        return float.IsFinite(intersection.X) && float.IsFinite(intersection.Y);
    }

    private static bool PointOnSegment(PointF point, PointF start, PointF end)
    {
        var closest = ClosestPointOnSegment(point, start, end);
        return DistanceSquared(point, closest) <= PointContainmentTolerance * PointContainmentTolerance;
    }

    private static PointF ClosestPointOnSegment(PointF point, PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0.000001f) return start;
        var t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0f, 1f);
        return new PointF(start.X + dx * t, start.Y + dy * t);
    }

    private static float CrossVector(PointF first, PointF second)
        => first.X * second.Y - first.Y * second.X;

    private static PointF NormalizeDirection(PointF direction, PointF fallback)
    {
        var lengthSquared = direction.X * direction.X + direction.Y * direction.Y;
        if (float.IsFinite(lengthSquared) && lengthSquared > 0.000001f)
        {
            var length = MathF.Sqrt(lengthSquared);
            return new PointF(direction.X / length, direction.Y / length);
        }

        var fallbackLengthSquared = fallback.X * fallback.X + fallback.Y * fallback.Y;
        if (!float.IsFinite(fallbackLengthSquared) || fallbackLengthSquared <= 0.000001f)
        {
            return new PointF(0f, -1f);
        }

        var fallbackLength = MathF.Sqrt(fallbackLengthSquared);
        return new PointF(fallback.X / fallbackLength, fallback.Y / fallbackLength);
    }

    private static CollisionGeometry TransformCollisionGeometry(
        CollisionGeometry geometry,
        PointF position,
        float angle)
    {
        var sine = MathF.Sin(angle);
        var cosine = MathF.Cos(angle);
        var parts = geometry.Parts
            .Select(part =>
            {
                var points = part.Points
                    .Select(point => TransformLocalPoint(point, position, sine, cosine))
                    .ToArray();
                return new CollisionPolygon(points, BoundsFromPoints(points));
            })
            .ToArray();
        var contours = geometry.Contours
            .Select(contour => contour
                .Select(point => TransformLocalPoint(point, position, sine, cosine))
                .ToArray())
            .ToArray();
        var edges = geometry.Edges
            .Select(edge =>
            {
                var start = TransformLocalPoint(edge.Start, position, sine, cosine);
                var end = TransformLocalPoint(edge.End, position, sine, cosine);
                return new CollisionEdge(
                    start,
                    end,
                    NormalizeDirection(
                        new PointF(
                            edge.Normal.X * cosine - edge.Normal.Y * sine,
                            edge.Normal.X * sine + edge.Normal.Y * cosine),
                        new PointF(0f, -1f)));
            })
            .ToArray();
        return new CollisionGeometry(
            parts,
            contours,
            BoundsFromContours(contours),
            edges,
            null);
    }

    private static PointF TransformLocalPoint(
        PointF point,
        PointF position,
        float sine,
        float cosine)
        => new(
            position.X + point.X * cosine - point.Y * sine,
            position.Y + point.X * sine + point.Y * cosine);

    private static RectangleF BoundsFromPoints(IReadOnlyList<PointF> points)
    {
        if (points.Count == 0) return RectangleF.Empty;
        var left = points[0].X;
        var right = points[0].X;
        var top = points[0].Y;
        var bottom = points[0].Y;
        for (var index = 1; index < points.Count; index++)
        {
            left = Math.Min(left, points[index].X);
            right = Math.Max(right, points[index].X);
            top = Math.Min(top, points[index].Y);
            bottom = Math.Max(bottom, points[index].Y);
        }

        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static bool BoundsOverlap(RectangleF first, RectangleF second)
    {
        return first.Left <= second.Right + CollisionEpsilon
            && first.Right + CollisionEpsilon >= second.Left
            && first.Top <= second.Bottom + CollisionEpsilon
            && first.Bottom + CollisionEpsilon >= second.Top;
    }

    private static RectangleF SegmentBounds(PointF start, PointF end)
        => RectangleF.FromLTRB(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X),
            Math.Max(start.Y, end.Y));

    private static bool SegmentsBoundsOverlap(
        PointF firstStart,
        PointF firstEnd,
        PointF secondStart,
        PointF secondEnd)
    {
        var firstLeft = Math.Min(firstStart.X, firstEnd.X);
        var firstRight = Math.Max(firstStart.X, firstEnd.X);
        var firstTop = Math.Min(firstStart.Y, firstEnd.Y);
        var firstBottom = Math.Max(firstStart.Y, firstEnd.Y);
        var secondLeft = Math.Min(secondStart.X, secondEnd.X);
        var secondRight = Math.Max(secondStart.X, secondEnd.X);
        var secondTop = Math.Min(secondStart.Y, secondEnd.Y);
        var secondBottom = Math.Max(secondStart.Y, secondEnd.Y);
        return firstLeft <= secondRight + CollisionEpsilon
            && firstRight + CollisionEpsilon >= secondLeft
            && firstTop <= secondBottom + CollisionEpsilon
            && firstBottom + CollisionEpsilon >= secondTop;
    }

    private static bool EdgeBoundsOverlap(CollisionEdge edge, RectangleF bounds)
    {
        var left = Math.Min(edge.Start.X, edge.End.X);
        var right = Math.Max(edge.Start.X, edge.End.X);
        var top = Math.Min(edge.Start.Y, edge.End.Y);
        var bottom = Math.Max(edge.Start.Y, edge.End.Y);
        return left <= bounds.Right + CollisionEpsilon
            && right + CollisionEpsilon >= bounds.Left
            && top <= bounds.Bottom + CollisionEpsilon
            && bottom + CollisionEpsilon >= bounds.Top;
    }

    private static bool PointInBounds(PointF point, RectangleF bounds)
    {
        return point.X >= bounds.Left - CollisionEpsilon
            && point.X <= bounds.Right + CollisionEpsilon
            && point.Y >= bounds.Top - CollisionEpsilon
            && point.Y <= bounds.Bottom + CollisionEpsilon;
    }

    private static float Distance(PointF first, PointF second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float GeometryRadius(IReadOnlyList<PointF[]> contours)
    {
        var radius = 0f;
        foreach (var contour in contours)
        {
            foreach (var point in contour)
            {
                radius = Math.Max(radius, Distance(PointF.Empty, point));
            }
        }

        return radius;
    }

    private static bool IsRestingState(FragmentState state)
    {
        var velocityLimit = VectorUnits.FromPixels(RestingVelocityPixels);
        var velocitySquared = state.Velocity.X * state.Velocity.X
            + state.Velocity.Y * state.Velocity.Y;
        return float.IsFinite(velocitySquared)
            && velocitySquared <= velocityLimit * velocityLimit
            && float.IsFinite(state.AngularVelocity)
            && MathF.Abs(state.AngularVelocity) <= RestingAngularVelocity;
    }

    private static PointF CollisionNormal(int firstIndex, int secondIndex)
    {
        var angle = (firstIndex * 92821 + secondIndex * 68917) * 0.0001f;
        return new PointF(MathF.Cos(angle), MathF.Sin(angle));
    }

    private static float Dot(PointF left, PointF right) => left.X * right.X + left.Y * right.Y;

    private static PointF Reconstruct(PointF normal, PointF tangent, float normalValue, float tangentValue)
        => new(normal.X * normalValue + tangent.X * tangentValue, normal.Y * normalValue + tangent.Y * tangentValue);

    private static float DistanceSquared(PointF first, PointF second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return dx * dx + dy * dy;
    }

    private static bool HasUsableArea(RectangleF bounds)
        => bounds.Width > 0f && bounds.Height > 0f;

    private static bool IsFinite(RectangleF bounds)
        => float.IsFinite(bounds.Left)
            && float.IsFinite(bounds.Top)
            && float.IsFinite(bounds.Right)
            && float.IsFinite(bounds.Bottom);

    private readonly record struct CollisionPolygon(PointF[] Points, RectangleF Bounds);

    private sealed class CollisionEdgeIndex
    {
        private const int MaximumGridAxis = 128;
        private const int MaximumCellsPerEdge = 64;

        private readonly float _left;
        private readonly float _top;
        private readonly float _cellSize;
        private readonly int _columns;
        private readonly int _rows;
        private readonly Dictionary<long, int[]> _buckets;
        private readonly int[] _overflowEdges;
        private readonly int[] _queryMarks;
        private int _queryGeneration;

        public CollisionEdgeIndex(IReadOnlyList<CollisionEdge> edges, RectangleF bounds)
        {
            _left = bounds.Left;
            _top = bounds.Top;
            var longestSide = Math.Max(bounds.Width, bounds.Height);
            var targetAxis = Math.Clamp(
                (int)Math.Ceiling(Math.Sqrt(Math.Max(1, edges.Count) * 2d)),
                8,
                MaximumGridAxis);
            _cellSize = float.IsFinite(longestSide) && longestSide > CollisionEpsilon
                ? Math.Max(longestSide / targetAxis, CollisionEpsilon)
                : 1f;
            _columns = GridDimension(bounds.Width, _cellSize);
            _rows = GridDimension(bounds.Height, _cellSize);

            var buckets = new Dictionary<long, List<int>>();
            var overflowEdges = new List<int>();
            for (var edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
            {
                var edgeBounds = SegmentBounds(edges[edgeIndex].Start, edges[edgeIndex].End);
                var minimumColumn = GetColumn(edgeBounds.Left);
                var maximumColumn = GetColumn(edgeBounds.Right);
                var minimumRow = GetRow(edgeBounds.Top);
                var maximumRow = GetRow(edgeBounds.Bottom);
                var coveredCells = (long)(maximumColumn - minimumColumn + 1)
                    * (maximumRow - minimumRow + 1L);
                if (coveredCells > MaximumCellsPerEdge)
                {
                    overflowEdges.Add(edgeIndex);
                    continue;
                }

                for (var column = minimumColumn; column <= maximumColumn; column++)
                {
                    for (var row = minimumRow; row <= maximumRow; row++)
                    {
                        var key = CellKey(column, row);
                        if (!buckets.TryGetValue(key, out var bucket))
                        {
                            bucket = new List<int>();
                            buckets.Add(key, bucket);
                        }

                        bucket.Add(edgeIndex);
                    }
                }
            }

            _buckets = buckets.ToDictionary(
                item => item.Key,
                item => item.Value.ToArray());
            _overflowEdges = overflowEdges.ToArray();
            _queryMarks = new int[edges.Count];
        }

        public void CollectCandidates(RectangleF bounds, List<int> destination)
        {
            destination.Clear();
            if (!IsFinite(bounds)
                || bounds.Right < _left - CollisionEpsilon
                || bounds.Left > _left + _columns * _cellSize + CollisionEpsilon
                || bounds.Bottom < _top - CollisionEpsilon
                || bounds.Top > _top + _rows * _cellSize + CollisionEpsilon)
            {
                return;
            }

            var generation = NextQueryGeneration();
            foreach (var edgeIndex in _overflowEdges)
            {
                AddCandidate(edgeIndex, generation, destination);
            }

            var minimumColumn = GetColumn(bounds.Left);
            var maximumColumn = GetColumn(bounds.Right);
            var minimumRow = GetRow(bounds.Top);
            var maximumRow = GetRow(bounds.Bottom);
            for (var column = minimumColumn; column <= maximumColumn; column++)
            {
                for (var row = minimumRow; row <= maximumRow; row++)
                {
                    if (!_buckets.TryGetValue(CellKey(column, row), out var bucket)) continue;
                    foreach (var edgeIndex in bucket)
                    {
                        AddCandidate(edgeIndex, generation, destination);
                    }
                }
            }
        }

        private int NextQueryGeneration()
        {
            if (_queryGeneration == int.MaxValue)
            {
                Array.Clear(_queryMarks, 0, _queryMarks.Length);
                _queryGeneration = 1;
            }
            else
            {
                _queryGeneration++;
            }

            return _queryGeneration;
        }

        private void AddCandidate(int edgeIndex, int generation, List<int> destination)
        {
            if (_queryMarks[edgeIndex] == generation) return;
            _queryMarks[edgeIndex] = generation;
            destination.Add(edgeIndex);
        }

        private int GetColumn(float value)
            => GetCell(value, _left, _cellSize, _columns);

        private int GetRow(float value)
            => GetCell(value, _top, _cellSize, _rows);

        private static int GetCell(float value, float origin, float cellSize, int count)
        {
            var coordinate = Math.Floor((value - origin) / cellSize);
            if (coordinate <= 0d) return 0;
            if (coordinate >= count - 1) return count - 1;
            return (int)coordinate;
        }

        private static int GridDimension(float extent, float cellSize)
        {
            if (!float.IsFinite(extent) || extent <= CollisionEpsilon) return 1;
            var dimension = Math.Ceiling(extent / cellSize);
            if (dimension >= MaximumGridAxis) return MaximumGridAxis;
            return Math.Max(1, (int)dimension);
        }

        private static long CellKey(int column, int row)
            => ((long)column << 32) | (uint)row;
    }

    private sealed record CollisionGeometry(
        CollisionPolygon[] Parts,
        PointF[][] Contours,
        RectangleF Bounds,
        CollisionEdge[] Edges,
        CollisionEdgeIndex? EdgeIndex);

    private readonly record struct CollisionEdge(
        PointF Start,
        PointF End,
        PointF Normal);

    private readonly record struct CollisionDirection(
        PointF Direction,
        CollisionEdge? Surface);

    private readonly record struct CollisionHit(
        PointF Normal,
        float Penetration,
        PointF ContactPoint,
        PointF SurfaceTangent = default);

    private struct FragmentState(
        PointF start,
        PointF velocity,
        float angle,
        float angularVelocity,
        CollisionGeometry geometry)
    {
        public PointF Start = start;
        public PointF Position = start;
        public PointF Velocity = velocity;
        public float Angle = angle;
        public float AngularVelocity = angularVelocity;
        public CollisionGeometry Geometry = geometry;
    }
}
