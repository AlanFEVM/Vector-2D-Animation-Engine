using Clipper2Lib;
using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunRandomFractureGeometryRegression()
    {
        var sourceContours = new[]
        {
            new[]
            {
                new PointF(-600, -450),
                new PointF(600, -450),
                new PointF(600, 450),
                new PointF(-600, 450)
            }
        };
        var linearOptions = new RandomFractureOptions
        {
            FragmentCountLimit = 12,
            EdgeCount = 3,
            FragmentRandomness = 1f,
            RandomSeed = 20260828,
            Mode = RandomFractureMode.Linear
        };

        AssertRandomFracture(
            RandomFractureGenerator.TryGenerate(
                sourceContours,
                linearOptions,
                out var linear,
                out var sourceBounds,
                out var error),
            error);
        if (linear.Length < 2
            || linear.Length > linearOptions.FragmentCountLimit
            || linear.Sum(fragment => fragment.Area) < 1_000_000d)
        {
            throw new InvalidOperationException("Linear Random Fracture did not preserve the source region or fragment limit.");
        }

        AssertRandomFracture(
            RandomFractureGenerator.TryGenerate(
                sourceContours,
                linearOptions,
                out var repeat,
                out _,
                out error),
            error);
        if (!RandomFractureFragmentsMatch(linear, repeat))
        {
            throw new InvalidOperationException("Random Fracture was not deterministic for a fixed seed.");
        }

        var detailedOptions = linearOptions with { EdgeCount = 12 };
        AssertRandomFracture(
            RandomFractureGenerator.TryGenerate(
                sourceContours,
                detailedOptions,
                out var detailed,
                out _,
                out error),
            error);
        var coarseBoundaryPoints = linear.Sum(fragment => fragment.Contours.Sum(contour => contour.Length));
        var detailedBoundaryPoints = detailed.Sum(fragment => fragment.Contours.Sum(contour => contour.Length));
        if (detailedBoundaryPoints <= coarseBoundaryPoints)
        {
            throw new InvalidOperationException("Linear Random Fracture edge count did not change boundary detail.");
        }

        var bezierScene = new VectorScene();
        bezierScene.CreateEmpty(layers: 1, frameCount: 2);
        bezierScene.EditFrame = 0;
        const float bezierRadius = 400f;
        const float bezierKappa = 0.5522848f;
        var bezierPathObject = bezierScene.AppendPathBezierObjectContours(
            0,
            [
                [
                    new PathBezierNode(
                        new PointF(0, -bezierRadius),
                        new PointF(-bezierRadius * bezierKappa, -bezierRadius),
                        new PointF(bezierRadius * bezierKappa, -bezierRadius)),
                    new PathBezierNode(
                        new PointF(bezierRadius, 0),
                        new PointF(bezierRadius, -bezierRadius * bezierKappa),
                        new PointF(bezierRadius, bezierRadius * bezierKappa)),
                    new PathBezierNode(
                        new PointF(0, bezierRadius),
                        new PointF(bezierRadius * bezierKappa, bezierRadius),
                        new PointF(-bezierRadius * bezierKappa, bezierRadius)),
                    new PathBezierNode(
                        new PointF(-bezierRadius, 0),
                        new PointF(-bezierRadius, bezierRadius * bezierKappa),
                        new PointF(-bezierRadius, -bezierRadius * bezierKappa))
                ]
            ],
            0,
            Color.SteelBlue,
            Color.Transparent,
            64);
        if (bezierPathObject < 0
            || !bezierScene.TryGetPathBezierWorldContours(bezierPathObject, out var bezierNodes)
            || bezierNodes.Length != 1)
        {
            throw new InvalidOperationException("Random Fracture Bezier precision setup failed.");
        }

        var bezierContours = bezierScene.GetDistortedObjectBoundaryContours(bezierPathObject);
        var hasSubUnitCurveSamples = bezierContours
            .SelectMany(contour => contour)
            .Any(point => MathF.Abs(point.X - MathF.Round(point.X)) > 0.01f
                || MathF.Abs(point.Y - MathF.Round(point.Y)) > 0.01f);
        if (!hasSubUnitCurveSamples)
        {
            throw new InvalidOperationException(
                "Random Fracture discarded sub-unit precision when extracting Bezier path contours.");
        }

        AssertRandomFracture(
            RandomFractureGenerator.TryGenerate(
                bezierContours,
                linearOptions with { FragmentCountLimit = 8, EdgeCount = 8 },
                out var bezierFragments,
                out _,
                out error),
            error);
        var bezierSourcePath = new Path64(bezierContours[0]
            .Select(point => new Point64(
                (long)Math.Round(point.X * 1000d, MidpointRounding.AwayFromZero),
                (long)Math.Round(point.Y * 1000d, MidpointRounding.AwayFromZero))));
        var bezierSourceArea = Math.Abs(Clipper.Area(bezierSourcePath)) / 1_000_000d;
        var bezierFragmentArea = bezierFragments.Sum(fragment => fragment.Area);
        if (bezierFragments.Length < 2
            || bezierFragmentArea < bezierSourceArea * 0.995d
            || bezierFragmentArea > bezierSourceArea * 1.005d)
        {
            throw new InvalidOperationException(
                "Random Fracture did not preserve the precise Bezier source region.");
        }

        var pointOptions = linearOptions with
        {
            Mode = RandomFractureMode.Pointillize,
            EdgeCount = 8,
            FragmentRandomness = 0.72f
        };
        AssertRandomFracture(
            RandomFractureGenerator.TryGenerate(
                sourceContours,
                pointOptions,
                out var pointillized,
                out _,
                out error),
            error);
        if (pointillized.Length < 2 || pointillized.Length > pointOptions.FragmentCountLimit)
        {
            throw new InvalidOperationException("Pointillize Random Fracture did not honor the fragment limit.");
        }

        var directionalOptions = pointOptions with
        {
            GenerateAnimation = true,
            AnimationFrames = 6,
            FractureStrength = 20,
            DirectionAngleDegrees = -90,
            GravityPixelsPerFrameSquared = 0,
            AirResistancePercent = 0,
            ForceAlgorithm = RandomFractureForceAlgorithm.Directional,
            AllowOverlap = true
        };
        var directionalMotions = RandomFractureGenerator.Simulate(
            pointillized,
            sourceBounds,
            directionalOptions);
        if (directionalMotions.Length != directionalOptions.AnimationFrames + 1
            || directionalMotions[0].Any(motion => motion.Offset != PointF.Empty)
            || directionalMotions[1].Any(motion => motion.Offset.Y >= 0 || Math.Abs(motion.Offset.X) > 0.1f))
        {
            throw new InvalidOperationException("Directional fracture force did not produce a deterministic upward simulation.");
        }

        var noOverlapOptions = directionalOptions with
        {
            ForceAlgorithm = RandomFractureForceAlgorithm.Random,
            FractureStrength = 180,
            AnimationFrames = 10,
            AllowOverlap = false,
            RandomSeed = 8801
        };
        var noOverlapMotions = RandomFractureGenerator.Simulate(
            pointillized,
            sourceBounds,
            noOverlapOptions);
        for (var frame = 1; frame < noOverlapMotions.Length; frame++)
        {
            for (var firstIndex = 0; firstIndex < pointillized.Length; firstIndex++)
            {
                for (var secondIndex = firstIndex + 1; secondIndex < pointillized.Length; secondIndex++)
                {
                    var overlapArea = RandomFractureIntersectionArea(
                        pointillized[firstIndex],
                        noOverlapMotions[frame][firstIndex],
                        pointillized[secondIndex],
                        noOverlapMotions[frame][secondIndex]);
                    if (overlapArea > 0.25d)
                    {
                        throw new InvalidOperationException(
                            $"Random Fracture contour collision solver left overlapping fragments at frame {frame}: "
                            + $"first={firstIndex}, second={secondIndex}, area={overlapArea:0.###}.");
                    }
                }
            }
        }

        var terrainFragment = new RandomFractureFragment(
            new[]
            {
                new[]
                {
                    new PointF(-40, -40),
                    new PointF(40, -40),
                    new PointF(40, 40),
                    new PointF(-40, 40)
                }
            },
            PointF.Empty,
            new RectangleF(-40, -40, 80, 80),
            6400,
            0);
        var terrainOptions = directionalOptions with
        {
            FractureStrength = 0,
            GravityPixelsPerFrameSquared = 8,
            GroundPositionPercent = 400,
            BouncePercent = 0,
            AllowOverlap = false,
            EnableFragmentCollisions = true
        };
        var freeMotions = RandomFractureGenerator.Simulate(
            new[] { terrainFragment },
            new RectangleF(-1000, -1000, 2000, 5000),
            terrainOptions);
        var terrainMotions = RandomFractureGenerator.Simulate(
            new[] { terrainFragment },
            new RectangleF(-1000, -1000, 2000, 5000),
            terrainOptions,
            new[]
            {
                new[]
                {
                    new PointF(-800, 100),
                    new PointF(800, 100),
                    new PointF(800, 300),
                    new PointF(-800, 300)
                }
            });
        var terrainWithOverlapAllowedMotions = RandomFractureGenerator.Simulate(
            new[] { terrainFragment },
            new RectangleF(-1000, -1000, 2000, 5000),
            terrainOptions with { AllowOverlap = true },
            new[]
            {
                new[]
                {
                    new PointF(-800, 100),
                    new PointF(800, 100),
                    new PointF(800, 300),
                    new PointF(-800, 300)
                }
            });
        if (freeMotions.Length < 2
            || terrainMotions.Length < 2
            || terrainWithOverlapAllowedMotions.Length < 2
            || !float.IsFinite(terrainMotions[1][0].Offset.Y)
            || terrainMotions[1][0].Offset.Y >= freeMotions[1][0].Offset.Y - 10f
            || terrainWithOverlapAllowedMotions[1][0].Offset.Y >= freeMotions[1][0].Offset.Y - 10f)
        {
            throw new InvalidOperationException("Random Fracture collision terrain did not block a falling fragment, including when fragment overlap is allowed.");
        }

        var thinTerrain = new[]
        {
            new[]
            {
                new PointF(-900, 80),
                new PointF(900, 80),
                new PointF(900, 90),
                new PointF(-900, 90)
            }
        };
        var edgeCrossingOptions = terrainOptions with
        {
            GenerateAnimation = true,
            AnimationFrames = 1,
            GravityPixelsPerFrameSquared = 0,
            RotationStrengthDegrees = 0,
            GroundPositionPercent = 400
        };
        var edgeCrossingFragment = terrainFragment with
        {
            Center = new PointF(0, 85),
            Bounds = new RectangleF(-40, 45, 80, 80),
            Contours = new[]
            {
                new[]
                {
                    new PointF(-40, 45),
                    new PointF(40, 45),
                    new PointF(40, 125),
                    new PointF(-40, 125)
                }
            }
        };
        var edgeCrossingMotions = RandomFractureGenerator.Simulate(
            new[] { edgeCrossingFragment },
            new RectangleF(-1000, -1000, 2000, 5000),
            edgeCrossingOptions,
            thinTerrain);
        if (edgeCrossingMotions.Length != 2
            || Math.Abs(edgeCrossingMotions[1][0].Offset.Y) < 5f
            || Math.Abs(edgeCrossingMotions[1][0].Rotation) > 0.0001f)
        {
            throw new InvalidOperationException(
                "Random Fracture missed a thin terrain crossing when no fragment vertex was inside the terrain.");
        }

        var highSpeedCrossingOptions = edgeCrossingOptions with
        {
            FractureStrength = 500,
            DirectionAngleDegrees = 90,
            ForceAlgorithm = RandomFractureForceAlgorithm.Directional,
            BouncePercent = 0
        };
        var highSpeedFragment = edgeCrossingFragment with
        {
            Center = new PointF(0, -40),
            Bounds = new RectangleF(-20, -41, 40, 2),
            Contours = new[]
            {
                new[]
                {
                    new PointF(-20, -41),
                    new PointF(20, -41),
                    new PointF(20, -39),
                    new PointF(-20, -39)
                }
            }
        };
        var highSpeedCrossingMotions = RandomFractureGenerator.Simulate(
            new[] { highSpeedFragment },
            new RectangleF(-1000, -1000, 2000, 5000),
            highSpeedCrossingOptions,
            thinTerrain);
        if (highSpeedCrossingMotions.Length != 2
            || highSpeedCrossingMotions[1][0].Offset.Y >= 200f)
        {
            throw new InvalidOperationException(
                "Random Fracture tunneled through a thin collision terrain at high speed.");
        }

        var slopedTerrain = new[]
        {
            new[]
            {
                new PointF(-1200, 260),
                new PointF(1200, -140),
                new PointF(1200, 500),
                new PointF(-1200, 900)
            }
        };
        var slopeOptions = terrainOptions with
        {
            GenerateAnimation = true,
            AnimationFrames = 8,
            GravityPixelsPerFrameSquared = 8,
            RotationStrengthDegrees = 28,
            GroundPositionPercent = 400,
            BouncePercent = 0
        };
        var slopeMotions = RandomFractureGenerator.Simulate(
            new[]
            {
                terrainFragment with
                {
                    Center = PointF.Empty,
                    Bounds = new RectangleF(-40, -40, 80, 80)
                }
            },
            new RectangleF(-1200, -1200, 2400, 6000),
            slopeOptions,
            slopedTerrain);
        var expectedSlopeAngle = MathF.Atan2(-400f, 2400f);
        var slopeAngleDelta = NormalizeTestAngle(slopeMotions[^1][0].Rotation - expectedSlopeAngle);
        var perpendicularSlopeAngleDelta = NormalizeTestAngle(
            slopeMotions[^1][0].Rotation - expectedSlopeAngle - MathF.PI * 0.5f);
        var slopeAngleStable = slopeMotions.Length < 3
            || Enumerable.Range(2, slopeMotions.Length - 2)
                .All(frame => Math.Abs(slopeMotions[frame][0].Rotation - slopeMotions[^1][0].Rotation) < 0.0001f);
        if (slopeMotions.Length < 2
            || (MathF.Abs(slopeAngleDelta) > 0.03f
                && MathF.Abs(perpendicularSlopeAngleDelta) > 0.03f)
            || !slopeAngleStable
            || !float.IsFinite(slopeMotions[^1][0].Rotation))
        {
            throw new InvalidOperationException(
                "Random Fracture did not settle a fragment to the contacted terrain slope.");
        }

        var stabilityTerrain = new[]
        {
            new[]
            {
                new PointF(-1600, 120),
                new PointF(-800, 100),
                new PointF(0, 120),
                new PointF(800, 100),
                new PointF(1600, 120),
                new PointF(1600, 800),
                new PointF(-1600, 800)
            }
        };
        var stabilityOptions = slopeOptions with
        {
            AnimationFrames = 24,
            FractureStrength = 0,
            GravityPixelsPerFrameSquared = 8,
            RotationStrengthDegrees = 45,
            AirResistancePercent = 2,
            BouncePercent = 0
        };
        var stabilityMotions = RandomFractureGenerator.Simulate(
            new[]
            {
                terrainFragment with
                {
                    Center = PointF.Empty,
                    Bounds = new RectangleF(-40, -40, 80, 80)
                }
            },
            new RectangleF(-1600, -1000, 3200, 5000),
            stabilityOptions,
            stabilityTerrain);
        var stabilityTailStart = Math.Max(2, stabilityMotions.Length - 6);
        var stabilityTailIsQuiet = Enumerable.Range(
                stabilityTailStart,
                stabilityMotions.Length - stabilityTailStart)
            .All(frame =>
                DistanceSquaredTest(
                    stabilityMotions[frame][0].Offset,
                    stabilityMotions[frame - 1][0].Offset) <= 0.01f
                && Math.Abs(
                    stabilityMotions[frame][0].Rotation
                    - stabilityMotions[frame - 1][0].Rotation) <= 0.001f);
        if (stabilityMotions.Length != stabilityOptions.AnimationFrames + 1
            || !stabilityTailIsQuiet
            || !float.IsFinite(stabilityMotions[^1][0].Rotation))
        {
            throw new InvalidOperationException(
                "Random Fracture contact stabilization left rotational or positional jitter on complex terrain.");
        }

        var holeOuter = new[]
        {
            new PointF(-800, 200),
            new PointF(800, 200),
            new PointF(800, 800),
            new PointF(-800, 800)
        };
        var hole = new[]
        {
            new PointF(-200, 300),
            new PointF(-200, 500),
            new PointF(200, 500),
            new PointF(200, 300)
        };
        var holeFragment = terrainFragment with
        {
            Center = new PointF(0, 400),
            Bounds = new RectangleF(-40, 360, 80, 80),
            Contours = new[]
            {
                new[]
                {
                    new PointF(-40, 360),
                    new PointF(40, 360),
                    new PointF(40, 440),
                    new PointF(-40, 440)
                }
            }
        };
        var holeOptions = edgeCrossingOptions with { RotationStrengthDegrees = 0 };
        var holeMotions = RandomFractureGenerator.Simulate(
            new[] { holeFragment },
            new RectangleF(-1000, -1000, 2000, 5000),
            holeOptions,
            new[] { holeOuter, hole });
        var solidMotions = RandomFractureGenerator.Simulate(
            new[] { holeFragment },
            new RectangleF(-1000, -1000, 2000, 5000),
            holeOptions,
            new[] { holeOuter });
        if (holeMotions.Length != 2
            || Math.Abs(holeMotions[1][0].Offset.X) > 0.01f
            || Math.Abs(holeMotions[1][0].Offset.Y) > 0.01f
            || solidMotions.Length != 2
            || Math.Abs(solidMotions[1][0].Offset.Y) < 5f)
        {
            throw new InvalidOperationException(
                "Random Fracture did not preserve a complex terrain hole while resolving solid terrain contact.");
        }

        var concaveTerrain = new[]
        {
            new[]
            {
                new PointF(-800, 200),
                new PointF(800, 200),
                new PointF(800, 800),
                new PointF(500, 800),
                new PointF(500, 500),
                new PointF(-500, 500),
                new PointF(-500, 800),
                new PointF(-800, 800)
            }
        };
        var concaveVoidFragment = holeFragment with
        {
            Center = new PointF(0, 650),
            Bounds = new RectangleF(-40, 610, 80, 80),
            Contours = new[]
            {
                new[]
                {
                    new PointF(-40, 610),
                    new PointF(40, 610),
                    new PointF(40, 690),
                    new PointF(-40, 690)
                }
            }
        };
        var concaveVoidMotions = RandomFractureGenerator.Simulate(
            new[] { concaveVoidFragment },
            new RectangleF(-1000, -1000, 2000, 5000),
            holeOptions,
            concaveTerrain);
        if (concaveVoidMotions.Length != 2
            || Math.Abs(concaveVoidMotions[1][0].Offset.X) > 0.01f
            || Math.Abs(concaveVoidMotions[1][0].Offset.Y) > 0.01f)
        {
            throw new InvalidOperationException(
                "Random Fracture reported a collision inside the empty pocket of a concave terrain.");
        }

        var terrainPriorityOptions = edgeCrossingOptions with
        {
            FractureStrength = 0,
            GravityPixelsPerFrameSquared = 8,
            GroundPositionPercent = 0,
            BouncePercent = 0
        };
        var terrainPriorityFragment = terrainFragment with
        {
            Center = new PointF(0, -20),
            Bounds = new RectangleF(-40, -60, 80, 80),
            Contours = new[]
            {
                new[]
                {
                    new PointF(-40, -60),
                    new PointF(40, -60),
                    new PointF(40, 20),
                    new PointF(-40, 20)
                }
            }
        };
        var terrainPriorityMotions = RandomFractureGenerator.Simulate(
            new[] { terrainPriorityFragment },
            new RectangleF(-1000, -100, 2000, 100),
            terrainPriorityOptions,
            new[]
            {
                new[]
                {
                    new PointF(-800, 100),
                    new PointF(800, 100),
                    new PointF(800, 120),
                    new PointF(-800, 120)
                }
            });
        if (terrainPriorityMotions.Length != 2
            || terrainPriorityMotions[1][0].Offset.Y <= 40f
            || terrainPriorityMotions[1][0].Offset.Y >= 200f)
        {
            throw new InvalidOperationException(
                "Random Fracture stopped on the implicit ground instead of the authored terrain.");
        }

        var complexTerrainPointCount = 768;
        var complexTerrain = new PointF[complexTerrainPointCount * 2];
        for (var pointIndex = 0; pointIndex < complexTerrainPointCount; pointIndex++)
        {
            var t = pointIndex / (float)(complexTerrainPointCount - 1);
            var x = -2400f + t * 4800f;
            var y = 220f + MathF.Sin(t * MathF.Tau * 18f) * 32f;
            complexTerrain[pointIndex] = new PointF(x, y);
            complexTerrain[complexTerrain.Length - 1 - pointIndex] = new PointF(x, 2600f);
        }

        var complexTerrainOptions = terrainOptions with
        {
            GenerateAnimation = true,
            AnimationFrames = 2,
            FractureStrength = 0,
            GravityPixelsPerFrameSquared = 24,
            GroundPositionPercent = 400,
            BouncePercent = 0,
            RotationStrengthDegrees = 0
        };
        var complexTerrainStarted = Stopwatch.GetTimestamp();
        var complexTerrainMotions = RandomFractureGenerator.Simulate(
            new[] { terrainFragment },
            new RectangleF(-2400, -1000, 4800, 5000),
            complexTerrainOptions,
            new[] { complexTerrain });
        var complexTerrainMilliseconds = Stopwatch.GetElapsedTime(complexTerrainStarted).TotalMilliseconds;
        if (complexTerrainMotions.Length != complexTerrainOptions.AnimationFrames + 1
            || complexTerrainMotions.Any(frame => frame.Length != 1)
            || !complexTerrainMotions.All(frame =>
                float.IsFinite(frame[0].Offset.X)
                && float.IsFinite(frame[0].Offset.Y)
                && float.IsFinite(frame[0].Rotation)))
        {
            throw new InvalidOperationException("Random Fracture could not simulate a complex collision terrain.");
        }
        Console.WriteLine($"random_fracture_complex_terrain_ms={complexTerrainMilliseconds:0.###}");

        var manyFragmentOptions = complexTerrainOptions with
        {
            FragmentCountLimit = 62,
            EdgeCount = 8,
            RandomSeed = 20260831,
            AnimationFrames = 36,
            FractureStrength = 28,
            GravityPixelsPerFrameSquared = 0.8f,
            RotationStrengthDegrees = 3,
            AllowOverlap = false,
            EnableFragmentCollisions = true
        };
        AssertRandomFracture(
            RandomFractureGenerator.TryGenerate(
                sourceContours,
                manyFragmentOptions,
                out var manyFragments,
                out var manySourceBounds,
                out error),
            error);
        if (manyFragments.Length != manyFragmentOptions.FragmentCountLimit)
        {
            throw new InvalidOperationException(
                $"Random Fracture performance setup produced {manyFragments.Length} fragments instead of 62.");
        }

        var manyFragmentStarted = Stopwatch.GetTimestamp();
        var manyFragmentMotions = RandomFractureGenerator.Simulate(
            manyFragments,
            manySourceBounds,
            manyFragmentOptions,
            new[] { complexTerrain });
        var manyFragmentMilliseconds = Stopwatch.GetElapsedTime(manyFragmentStarted).TotalMilliseconds;
        if (manyFragmentMotions.Length != manyFragmentOptions.AnimationFrames + 1
            || manyFragmentMotions.Any(frame => frame.Length != manyFragments.Length)
            || manyFragmentMotions.Any(frame => frame.Any(motion =>
                !float.IsFinite(motion.Offset.X)
                || !float.IsFinite(motion.Offset.Y)
                || !float.IsFinite(motion.Rotation)))
            || manyFragmentMilliseconds > 5000d)
        {
            throw new InvalidOperationException(
                $"Random Fracture 62-fragment collision simulation exceeded its 5s safety budget ({manyFragmentMilliseconds:0.###} ms).");
        }
        Console.WriteLine($"random_fracture_62_fragments_ms={manyFragmentMilliseconds:0.###}");

        var terrainScene = new VectorScene();
        terrainScene.CreateEmpty(layers: 1, frameCount: 4);
        terrainScene.EditFrame = 0;
        var terrainLayer = terrainScene.AddCollisionTerrainLayer();
        var transparentTerrainObject = terrainLayer >= 0
            ? terrainScene.AppendPathObjectContours(
                terrainLayer,
                thinTerrain,
                0,
                Color.Transparent,
                Color.Transparent,
                8)
            : -1;
        if (transparentTerrainObject < 0
            || Color.FromArgb(terrainScene.Argb[transparentTerrainObject]).A == 0
            || !terrainScene.FillAutoMergeProtected[transparentTerrainObject]
            || terrainScene.ShouldRenderLayerContent(terrainLayer)
            || terrainScene.TileCount.Any(count => count != 0)
            || terrainScene.OverviewCount.Any(count => count != 0))
        {
            throw new InvalidOperationException(
                "Collision terrain was not normalized for collision or leaked into ordinary rendering summaries.");
        }

        var overlappingTerrainObject = terrainScene.AppendPathObjectContours(
            terrainLayer,
            new[]
            {
                new[]
                {
                    new PointF(-600, 82),
                    new PointF(600, 82),
                    new PointF(600, 92),
                    new PointF(-600, 92)
                }
            },
            0,
            Color.Transparent,
            Color.Transparent,
            8);
        var terrainObjectCountBeforeNormalization = terrainScene.ObjectCount;
        var normalizedTerrain = terrainScene.NormalizePaintForInteractiveCommit(
            [transparentTerrainObject, overlappingTerrainObject],
            frame: 0);
        if (overlappingTerrainObject < 0
            || terrainScene.ObjectCount != terrainObjectCountBeforeNormalization
            || normalizedTerrain.Length != 0
            || !terrainScene.FillAutoMergeProtected[overlappingTerrainObject]
            || terrainScene.TileCount.Any(count => count != 0)
            || terrainScene.OverviewCount.Any(count => count != 0))
        {
            throw new InvalidOperationException(
                "Collision terrain was treated as ordinary paint during interactive normalization.");
        }

        var normalized = new RandomFractureOptions
        {
            FragmentCountLimit = 1,
            EdgeCount = 99,
            FragmentRandomness = float.NaN,
            FractureStrength = float.PositiveInfinity,
            AnimationFrames = 0,
            GroundPositionPercent = float.NaN
        }.Normalize();
        if (normalized.FragmentCountLimit != 2
            || normalized.EdgeCount != 32
            || Math.Abs(normalized.FragmentRandomness - 0.72f) > 0.0001f
            || Math.Abs(normalized.FractureStrength - 28f) > 0.0001f
            || normalized.AnimationFrames != 1
            || Math.Abs(normalized.GroundPositionPercent - 100f) > 0.0001f)
        {
            throw new InvalidOperationException("Random Fracture options did not normalize unsafe values.");
        }

        Console.WriteLine($"random_fracture_linear_fragments={linear.Length}");
        Console.WriteLine($"random_fracture_pointillize_fragments={pointillized.Length}");
        Console.WriteLine($"random_fracture_edge_points={coarseBoundaryPoints}->{detailedBoundaryPoints}");
        Console.WriteLine("random_fracture_geometry=ok");
    }

    private static void RunRandomFractureTimelineRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(layers: 2, frameCount: 12);
        var strokeWidth = VectorUnits.StrokePointsToUnits(2);
        scene.EditFrame = 0;
        var source = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(1200, 900),
            0,
            strokeWidth,
            Color.CornflowerBlue,
            Color.White,
            128,
            ShapeKind.Rectangle);
        var companion = scene.AddObject(
            0,
            new PointF(1800, 0),
            new SizeF(160, 120),
            0,
            0,
            Color.Gold,
            Color.Transparent,
            24,
            ShapeKind.Rectangle);
        var otherLayerObject = scene.AddObject(
            1,
            new PointF(-1800, 0),
            new SizeF(160, 120),
            0,
            0,
            Color.MediumSeaGreen,
            Color.Transparent,
            24,
            ShapeKind.Rectangle);
        if (source < 0 || companion < 0 || otherLayerObject < 0)
        {
            throw new InvalidOperationException("Random Fracture timeline setup could not create source objects.");
        }

        var sourceOrder = scene.ObjectOrder[source];
        var companionOrder = scene.ObjectOrder[companion];
        var otherLayerOrder = scene.ObjectOrder[otherLayerObject];
        if (!scene.InsertTimelineKeyframe(0, 2))
        {
            throw new InvalidOperationException("Random Fracture timeline setup could not create an existing target keyframe.");
        }

        scene.EditFrame = 2;
        var sentinel = scene.AddObject(
            0,
            new PointF(0, 1600),
            new SizeF(120, 90),
            0,
            0,
            Color.Magenta,
            Color.Transparent,
            16,
            ShapeKind.Rectangle);
        var sentinelOrder = sentinel >= 0 ? scene.ObjectOrder[sentinel] : 0;
        scene.EditFrame = 0;
        var sourceSceneObjectCount = scene.ObjectCount;
        var sourceStackObjectCount = Enumerable.Range(0, scene.ObjectCount)
            .Count(index => scene.ObjectLayer[index] == 0
                && scene.ObjectOrder[index] == sourceOrder);

        var options = new RandomFractureOptions
        {
            FragmentCountLimit = 8,
            EdgeCount = 7,
            FragmentRandomness = 0.8f,
            RandomSeed = 714,
            PreserveStroke = true,
            Mode = RandomFractureMode.Pointillize,
            GenerateAnimation = true,
            AnimationFrames = 4,
            FractureStrength = 22,
            GravityPixelsPerFrameSquared = 0.6f,
            GroundPositionPercent = 125,
            BouncePercent = 25,
            AllowOverlap = true
        };

        if (!scene.TryCreateRandomFracturePreview(
                source,
                0,
                options,
                out var preview,
                out _,
                out var error))
        {
            throw new InvalidOperationException($"Random Fracture preview failed: {error}");
        }
        if (scene.ObjectCount != sourceSceneObjectCount
            || scene.ShapeKind[source] != ShapeKind.Rectangle
            || preview.ObjectCount == 0
            || preview.ObjectCount > options.FragmentCountLimit
            || !Enumerable.Range(0, preview.ObjectCount).All(index => preview.ShapeKind[index] == ShapeKind.Path))
        {
            throw new InvalidOperationException("Random Fracture preview polluted the source scene or did not create independent Path fragments.");
        }
        if (!scene.TryCreateRandomFracturePreview(
                source,
                0,
                options,
                out var startPreview,
                out _,
                out error,
                previewFrame: 0)
            || startPreview.ObjectCount != preview.ObjectCount
            || !Enumerable.Range(0, preview.ObjectCount).Any(index =>
                Math.Abs(preview.X[index] - startPreview.X[index]) > 0.01f
                || Math.Abs(preview.Y[index] - startPreview.Y[index]) > 0.01f
                || Math.Abs(preview.Angle[index] - startPreview.Angle[index]) > 0.0001f))
        {
            throw new InvalidOperationException("Random Fracture preview did not honor the requested preview frame.");
        }

        if (!scene.TryApplyRandomFracture(source, 0, options with { GenerateAnimation = false }, out var produced, out error))
        {
            throw new InvalidOperationException($"Random Fracture single-frame apply failed: {error}");
        }
        var singleFragmentLayers = produced
            .Select(index => (int)scene.ObjectLayer[index])
            .Distinct()
            .ToArray();
        var singleFolderLayers = singleFragmentLayers
            .Select(scene.GetLayerParentIndex)
            .Distinct()
            .ToArray();
        if (produced.Length == 0
            || scene.ObjectCount != sourceSceneObjectCount - sourceStackObjectCount + produced.Length
            || produced.Any(index => scene.ShapeKind[index] != ShapeKind.Path
                || !scene.FillAutoMergeProtected[index]
                || scene.Stroke[index] != strokeWidth
                || scene.ObjectOrder[index] != sourceOrder
                || !scene.TryGetPathLocalContours(index, out _))
            || singleFragmentLayers.Length != produced.Length
            || singleFolderLayers.Length != 1
            || scene.LayerKinds[singleFolderLayers[0]] != DrawingLayerKind.Folder
            || !singleFragmentLayers.All(layer =>
                scene.LayerKinds[layer] == DrawingLayerKind.Drawing
                && scene.LayerParentIds[layer] == scene.LayerIds[singleFolderLayers[0]]
                && Enumerable.Range(0, scene.ObjectCount).Count(index => scene.ObjectLayer[index] == layer) == 1
                && scene.Timeline.EvaluateTargetExposure(scene.LayerIds[layer], 0).HasContent)
            || Enumerable.Range(0, scene.ObjectCount).Any(index =>
                scene.ObjectLayer[index] == 0
                && scene.ObjectOrder[index] == sourceOrder))
        {
            throw new InvalidOperationException("Random Fracture did not create one independent fragment layer per fragment.");
        }

        var animationScene = new VectorScene();
        animationScene.CreateEmpty(layers: 2, frameCount: 12);
        animationScene.EditFrame = 0;
        var animationSource = animationScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(1200, 900),
            0,
            strokeWidth,
            Color.CornflowerBlue,
            Color.White,
            128,
            ShapeKind.Rectangle);
        var animationCompanion = animationScene.AddObject(
            0,
            new PointF(1800, 0),
            new SizeF(160, 120),
            0,
            0,
            Color.Gold,
            Color.Transparent,
            24,
            ShapeKind.Rectangle);
        var animationOtherLayer = animationScene.AddObject(
            1,
            new PointF(-1800, 0),
            new SizeF(160, 120),
            0,
            0,
            Color.MediumSeaGreen,
            Color.Transparent,
            24,
            ShapeKind.Rectangle);
        var animationSourceOrder = animationScene.ObjectOrder[animationSource];
        var animationCompanionOrder = animationScene.ObjectOrder[animationCompanion];
        var animationOtherLayerOrder = animationScene.ObjectOrder[animationOtherLayer];
        var animationOtherLayerId = animationScene.LayerIds[animationScene.ObjectLayer[animationOtherLayer]];
        if (!animationScene.InsertTimelineKeyframe(0, 2))
        {
            throw new InvalidOperationException("Random Fracture animation setup could not create a target keyframe.");
        }

        animationScene.EditFrame = 2;
        var animationSentinel = animationScene.AddObject(
            0,
            new PointF(0, 1600),
            new SizeF(120, 90),
            0,
            0,
            Color.Magenta,
            Color.Transparent,
            16,
            ShapeKind.Rectangle);
        var animationSentinelOrder = animationScene.ObjectOrder[animationSentinel];
        animationScene.EditFrame = 0;

        var terrainContour = new[]
        {
            new PointF(-1200, 520),
            new PointF(1200, 520),
            new PointF(1200, 700),
            new PointF(-1200, 700)
        };
        var terrainLayer = animationScene.AddCollisionTerrainLayer();
        var terrainLayerId = terrainLayer >= 0
            ? animationScene.LayerIds[terrainLayer]
            : string.Empty;
        var terrainObject = terrainLayer >= 0
            ? animationScene.AppendPathObjectContours(
                terrainLayer,
                new[] { terrainContour },
                0,
                Color.DimGray,
                Color.Transparent,
                32)
            : -1;
        animationScene.ActiveLayer = 0;
        if (terrainObject < 0 || string.IsNullOrWhiteSpace(terrainLayerId))
        {
            throw new InvalidOperationException("Random Fracture timeline setup could not create collision terrain.");
        }

        if (!animationScene.TryApplyRandomFracture(animationSource, 0, options, out var animationFragments, out error))
        {
            throw new InvalidOperationException($"Random Fracture animation apply failed: {error}");
        }

        var animationFragmentLayers = animationFragments
            .Select(index => (int)animationScene.ObjectLayer[index])
            .Distinct()
            .OrderBy(layer => layer)
            .ToArray();
        var animationFolderLayers = animationFragmentLayers
            .Select(animationScene.GetLayerParentIndex)
            .Distinct()
            .ToArray();
        var terrainLayerAfterFracture = Array.IndexOf(animationScene.LayerIds, terrainLayerId);
        var otherLayerAfterFracture = Array.IndexOf(animationScene.LayerIds, animationOtherLayerId);
        var terrainObjectAfterFracture = terrainLayerAfterFracture >= 0
            ? Enumerable.Range(0, animationScene.ObjectCount)
                .FirstOrDefault(index => animationScene.ObjectLayer[index] == terrainLayerAfterFracture
                    && animationScene.Argb[index] == Color.DimGray.ToArgb(),
                    -1)
            : -1;
        var activeAtStart = ActiveObjectsForRandomFracture(animationScene, 0);
        var activeAtTarget = ActiveObjectsForRandomFracture(animationScene, 2);
        var targetExposure = animationScene.Timeline.EvaluateTargetExposure(animationScene.LayerIds[0], 2);
        if (animationFragmentLayers.Length != animationFragments.Length
            || animationFolderLayers.Length != 1
            || animationScene.LayerKinds[animationFolderLayers[0]] != DrawingLayerKind.Folder
            || !animationFragmentLayers.All(layer =>
                animationScene.LayerKinds[layer] == DrawingLayerKind.Drawing
                && animationScene.LayerParentIds[layer] == animationScene.LayerIds[animationFolderLayers[0]]
                && Enumerable.Range(0, animationScene.ObjectCount).Count(index => animationScene.ObjectLayer[index] == layer)
                    == options.AnimationFrames + 1
                && animationScene.Timeline.FindTrackByTargetId(animationScene.LayerIds[layer]) is { } track
                && track.Keyframes.Count == options.AnimationFrames + 1
                && track.EvaluateExposure(0).HasContent
                && track.EvaluateExposure(options.AnimationFrames).IsKeyframe
                && track.EvaluateExposure(options.AnimationFrames).HasContent)
            || terrainLayerAfterFracture < 0
            || animationScene.LayerKinds[terrainLayerAfterFracture] != DrawingLayerKind.Mask
            || Math.Abs(animationScene.LayerOpacity[terrainLayerAfterFracture] - 0.35f) > 0.0001f
            || !string.IsNullOrWhiteSpace(animationScene.LayerMaskIds[terrainLayerAfterFracture])
            || terrainObjectAfterFracture < 0
            || !targetExposure.IsKeyframe
            || !targetExposure.HasContent
            || activeAtStart.Count(index => animationFragmentLayers.Contains(animationScene.ObjectLayer[index]))
                != animationFragments.Length
            || activeAtTarget.Count(index => animationFragmentLayers.Contains(animationScene.ObjectLayer[index]))
                != animationFragments.Length
            || !activeAtTarget.Any(index => animationScene.ObjectOrder[index] == animationSentinelOrder)
            || !activeAtTarget.Any(index => animationScene.ObjectOrder[index] == animationCompanionOrder)
            || !activeAtTarget.Any(index => animationScene.ObjectLayer[index] == otherLayerAfterFracture
                && animationScene.ObjectOrder[index] == animationOtherLayerOrder))
        {
            throw new InvalidOperationException("Random Fracture animation did not create one timeline track per fragment.");
        }

        var startFragments = Enumerable.Range(0, animationScene.ObjectCount)
            .Where(index => animationFragmentLayers.Contains(animationScene.ObjectLayer[index])
                && animationScene.ObjectKeyframeFrame[index] == 0
                && animationScene.ObjectOrder[index] == animationSourceOrder)
            .OrderBy(index => animationScene.ObjectLayer[index])
            .ToArray();
        var endFragments = Enumerable.Range(0, animationScene.ObjectCount)
            .Where(index => animationFragmentLayers.Contains(animationScene.ObjectLayer[index])
                && animationScene.ObjectKeyframeFrame[index] == options.AnimationFrames
                && animationScene.ObjectOrder[index] == animationSourceOrder)
            .OrderBy(index => animationScene.ObjectLayer[index])
            .ToArray();
        if (startFragments.Length != animationFragments.Length
            || endFragments.Length != animationFragments.Length
            || !startFragments.Zip(endFragments).Any(pair =>
                Math.Abs(animationScene.X[pair.First] - animationScene.X[pair.Second]) > 0.01f
                || Math.Abs(animationScene.Y[pair.First] - animationScene.Y[pair.Second]) > 0.01f))
        {
            throw new InvalidOperationException("Random Fracture animation did not materialize moved fragment Cels.");
        }

        if (Enumerable.Range(0, animationScene.ObjectCount).Any(index =>
                animationScene.ObjectLayer[index] == 0
                && animationScene.ObjectOrder[index] == animationSourceOrder)
            || !Enumerable.Range(0, animationScene.ObjectCount).Any(index =>
                animationScene.ObjectOrder[index] == animationCompanionOrder)
            || !Enumerable.Range(0, animationScene.ObjectCount).Any(index =>
                animationScene.ObjectOrder[index] == animationOtherLayerOrder))
        {
            throw new InvalidOperationException("Random Fracture animation object overwrite did not preserve stable unrelated object ownership.");
        }

        RunRandomFractureAnimatedTerrainRegression();
        RunRandomFractureGradientRegression();
        Console.WriteLine($"random_fracture_animation_fragments={animationFragments.Length}");
        Console.WriteLine("random_fracture_timeline=ok");
    }

    private static void RunRandomFractureAnimatedTerrainRegression()
    {
        const int startFrame = 3;
        const int terrainTweenEndFrame = 6;
        const int blankTerrainFrame = 7;
        const int animationFrames = 7;
        var options = new RandomFractureOptions
        {
            FragmentCountLimit = 6,
            EdgeCount = 6,
            FragmentRandomness = 0.45f,
            RandomSeed = 20260907,
            PreserveStroke = false,
            Mode = RandomFractureMode.Pointillize,
            ForceAlgorithm = RandomFractureForceAlgorithm.Directional,
            DirectionAngleDegrees = -90,
            FractureStrength = 0,
            RotationStrengthDegrees = 0,
            GenerateAnimation = true,
            AnimationFrames = animationFrames,
            GravityPixelsPerFrameSquared = 8,
            AirResistancePercent = 0,
            BouncePercent = 0,
            GroundPositionPercent = 400,
            AllowOverlap = true,
            EnableFragmentCollisions = true
        };

        var dynamicSetup = CreateRandomFractureAnimatedTerrainScene(
            animateTerrain: true,
            addBlankTerrainKeyframe: false,
            startFrame,
            terrainTweenEndFrame,
            blankTerrainFrame);
        var dynamicScene = dynamicSetup.Scene;

        VectorScene CreatePreview(VectorScene sourceScene, int sourceObject, int previewFrame)
        {
            if (!sourceScene.TryCreateRandomFracturePreview(
                    sourceObject,
                    startFrame,
                    options,
                    out var preview,
                    out _,
                    out var error,
                    previewFrame))
            {
                throw new InvalidOperationException(
                    $"Random Fracture animated terrain preview failed at frame {previewFrame}: {error}");
            }

            return preview;
        }

        var dynamicStartPreview = CreatePreview(dynamicScene, dynamicSetup.SourceObject, 0);
        var dynamicTweenPreview = CreatePreview(
            dynamicScene,
            dynamicSetup.SourceObject,
            terrainTweenEndFrame - startFrame);
        var heldTerrainPreview = CreatePreview(
            dynamicScene,
            dynamicSetup.SourceObject,
            blankTerrainFrame - startFrame);
        if (dynamicStartPreview.RandomFracturePreviewTerrainFrame != startFrame
            || dynamicTweenPreview.RandomFracturePreviewTerrainFrame != terrainTweenEndFrame
            || heldTerrainPreview.RandomFracturePreviewTerrainFrame != blankTerrainFrame
            || dynamicScene.EditFrame != startFrame)
        {
            throw new InvalidOperationException(
                "Random Fracture preview did not select the terrain at start plus preview frame without moving the document frame.");
        }

        var staticSetup = CreateRandomFractureAnimatedTerrainScene(
            animateTerrain: false,
            addBlankTerrainKeyframe: false,
            startFrame,
            terrainTweenEndFrame,
            blankTerrainFrame);
        var staticTweenPreview = CreatePreview(staticSetup.Scene, staticSetup.SourceObject, terrainTweenEndFrame - startFrame);
        var dynamicTweenStates = RandomFracturePreviewStates(dynamicTweenPreview);
        var staticTweenStates = RandomFracturePreviewStates(staticTweenPreview);
        if (dynamicTweenStates.Length < 1
            || dynamicTweenStates.Length != staticTweenStates.Length
            || !dynamicTweenStates.Zip(staticTweenStates).Any(pair =>
                pair.First.Center.Y < pair.Second.Center.Y - 5f))
        {
            throw new InvalidOperationException(
                "Random Fracture did not use the real terrain Shape Tween while simulating the fragment motion.");
        }

        var geometryRevisionBeforeBlank = dynamicScene.GeometryRevision;
        dynamicScene.EditFrame = blankTerrainFrame;
        if (!dynamicScene.InsertTimelineBlankKeyframe(dynamicSetup.TerrainLayer, blankTerrainFrame))
        {
            throw new InvalidOperationException(
                "Random Fracture animated terrain setup could not insert its blank terrain keyframe.");
        }

        dynamicScene.EditFrame = startFrame;
        if (dynamicScene.GeometryRevision != geometryRevisionBeforeBlank)
        {
            throw new InvalidOperationException(
                "Random Fracture terrain exposure regression changed geometry while invalidating its plan cache.");
        }

        var blankTerrainPreview = CreatePreview(dynamicScene, dynamicSetup.SourceObject, blankTerrainFrame - startFrame);
        var finalTerrainPreview = CreatePreview(dynamicScene, dynamicSetup.SourceObject, animationFrames);
        RunRandomFractureTerrainOverlayRegression(
            dynamicScene, dynamicTweenPreview, blankTerrainPreview, startFrame);
        var heldTerrainStates = RandomFracturePreviewStates(heldTerrainPreview);
        var blankTerrainStates = RandomFracturePreviewStates(blankTerrainPreview);
        if (blankTerrainPreview.RandomFracturePreviewTerrainFrame != blankTerrainFrame
            || finalTerrainPreview.RandomFracturePreviewTerrainFrame != startFrame + animationFrames
            || heldTerrainStates.Length != blankTerrainStates.Length
            || !blankTerrainStates.Zip(heldTerrainStates).Any(pair =>
                pair.First.Center.Y > pair.Second.Center.Y + 2f))
        {
            throw new InvalidOperationException(
                "Random Fracture did not invalidate its cached terrain plan when a sleeping terrain exposure became blank.");
        }

        var applySetup = CreateRandomFractureAnimatedTerrainScene(
            animateTerrain: true,
            addBlankTerrainKeyframe: true,
            startFrame,
            terrainTweenEndFrame,
            blankTerrainFrame);
        applySetup.Scene.EditFrame = startFrame;
        if (!applySetup.Scene.TryApplyRandomFracture(
                applySetup.SourceObject,
                startFrame,
                options,
                out var appliedFragments,
                out var applyError))
        {
            throw new InvalidOperationException($"Random Fracture animated terrain apply failed: {applyError}");
        }

        var finalTerrainStates = RandomFracturePreviewStates(finalTerrainPreview);
        var appliedStates = RandomFractureAppliedStates(
            applySetup.Scene,
            appliedFragments,
            startFrame + animationFrames);
        if (appliedStates.Length != finalTerrainStates.Length
            || !appliedStates.Zip(finalTerrainStates).All(pair =>
                PointsWithin(pair.First.Center, pair.Second.Center, 0.05f)
                && Math.Abs(pair.First.Rotation - pair.Second.Rotation) <= 0.0001f))
        {
            throw new InvalidOperationException(
                "Random Fracture animated terrain preview and apply diverged for the same seed and sampled terrain frame.");
        }

        RunRandomFractureMovingThinTerrainRegression();
        RunRandomFractureMultipleTerrainLayersRegression();
        Console.WriteLine("random_fracture_animated_terrain=ok");
    }

    private static void RunRandomFractureMovingThinTerrainRegression()
    {
        PointF[][] Rectangle(float top, float bottom) =>
        [
            [new(-900, top), new(900, top), new(900, bottom), new(-900, bottom)]
        ];
        var fragment = new RandomFractureFragment(
            [[new(-10, -10), new(10, -10), new(10, 10), new(-10, 10)]],
            PointF.Empty, new RectangleF(-10, -10, 20, 20), 400, 0);
        var options = new RandomFractureOptions
        {
            GenerateAnimation = true,
            AnimationFrames = 1,
            FractureStrength = 0,
            RotationStrengthDegrees = 0,
            GravityPixelsPerFrameSquared = 0,
            AirResistancePercent = 0,
            BouncePercent = 0,
            AllowOverlap = true
        };
        foreach (var direction in new[] { -1, 1 })
        {
            var before = Rectangle(-80 * direction - 5, -80 * direction + 5);
            var after = Rectangle(80 * direction - 5, 80 * direction + 5);
            var motion = RandomFractureGenerator.Simulate(
                [fragment], fragment.Bounds, options, before, [before, after])[1][0];
            if (motion.Offset.Y * direction < 85
                || RandomFractureIntersectionArea(fragment, motion,
                    fragment with { Contours = after }, default) > 0.01)
            {
                throw new InvalidOperationException(
                    $"Moving thin terrain did not push a stationary fragment in direction {direction}: {motion.Offset}.");
            }
        }
        Console.WriteLine("random_fracture_moving_thin_terrain=ok");
    }

    private static void RunRandomFractureMultipleTerrainLayersRegression()
    {
        const int terrainTweenEndFrame = 4;
        const int animationFrames = terrainTweenEndFrame;

        PointF[][] Rectangle(float left, float top, float right, float bottom) =>
        [
            [
                new PointF(left, top),
                new PointF(right, top),
                new PointF(right, bottom),
                new PointF(left, bottom)
            ]
        ];

        var scene = new VectorScene();
        scene.CreateEmpty(layers: 1, frameCount: animationFrames + 4);
        scene.EditFrame = 0;
        var sourceObject = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(1800, 800),
            0,
            0,
            Color.CornflowerBlue,
            Color.Transparent,
            128,
            ShapeKind.Rectangle);
        if (sourceObject < 0)
        {
            throw new InvalidOperationException(
                "Random Fracture multi-terrain setup could not create its source object.");
        }

        int AddTweenedTerrain(
            string name,
            PointF[][] startContours,
            PointF[][] endContours)
        {
            scene.EditFrame = 0;
            var layer = scene.AddCollisionTerrainLayer(name);
            var startObject = scene.AppendPathObjectContours(
                layer,
                startContours,
                0,
                Color.DimGray,
                Color.Transparent,
                32);
            if (startObject < 0)
            {
                throw new InvalidOperationException(
                    $"Random Fracture multi-terrain setup could not create '{name}' start geometry.");
            }

            scene.EditFrame = terrainTweenEndFrame;
            if (!scene.InsertTimelineKeyframe(layer, terrainTweenEndFrame))
            {
                throw new InvalidOperationException(
                    $"Random Fracture multi-terrain setup could not create '{name}' endpoint keyframe.");
            }

            var copiedEndpoint = Enumerable.Range(0, scene.ObjectCount)
                .FirstOrDefault(index => scene.ObjectLayer[index] == layer
                    && scene.ObjectKeyframeFrame[index] == terrainTweenEndFrame,
                    -1);
            if (copiedEndpoint < 0 || !scene.RemoveObjectAt(copiedEndpoint))
            {
                throw new InvalidOperationException(
                    $"Random Fracture multi-terrain setup could not replace '{name}' endpoint geometry.");
            }

            if (scene.AppendPathObjectContours(
                    layer,
                    endContours,
                    0,
                    Color.DimGray,
                    Color.Transparent,
                    32) < 0)
            {
                throw new InvalidOperationException(
                    $"Random Fracture multi-terrain setup could not create '{name}' end geometry.");
            }

            scene.EditFrame = 0;
            if (!scene.TryCreateTimelineTween(
                    layer,
                    0,
                    terrainTweenEndFrame,
                    TimelineTweenKind.Shape,
                    out var tweenError))
            {
                throw new InvalidOperationException(
                    $"Random Fracture multi-terrain setup could not tween '{name}': {tweenError}");
            }

            return layer;
        }

        var westStart = Rectangle(-1000, 520, -40, 600);
        var westEnd = Rectangle(-1000, 360, -40, 440);
        var eastStart = Rectangle(40, 760, 1000, 840);
        var eastEnd = Rectangle(40, 400, 1000, 480);
        var westStartBounds = new RectangleF(-1000, 520, 960, 80);
        var westMiddleBounds = new RectangleF(-1000, 440, 960, 80);
        var westEndBounds = new RectangleF(-1000, 360, 960, 80);
        var eastStartBounds = new RectangleF(40, 760, 960, 80);
        var eastMiddleBounds = new RectangleF(40, 580, 960, 80);
        var eastEndBounds = new RectangleF(40, 400, 960, 80);
        var westLayer = AddTweenedTerrain("West Terrain", westStart, westEnd);
        var eastLayer = AddTweenedTerrain("East Terrain", eastStart, eastEnd);
        scene.ActiveLayer = 0;

        if (!scene.RenameLayer(eastLayer, "East Moving Terrain")
            || scene.GetCollisionTerrainLayers().Length != 2
            || !scene.GetCollisionTerrainLayers().Contains(westLayer)
            || !scene.GetCollisionTerrainLayers().Contains(eastLayer))
        {
            throw new InvalidOperationException(
                "Random Fracture did not preserve both collision terrain roles after renaming a layer.");
        }

        var getTerrainContoursByFrame = RequireMethod(
            typeof(VectorScene),
            "GetCollisionTerrainContoursByFrame",
            [typeof(int), typeof(int), typeof(CancellationToken)]);

        PointF[][]?[] ReadTerrainContours()
        {
            return getTerrainContoursByFrame.Invoke(
                    scene,
                    [0, animationFrames + 1, CancellationToken.None])
                as PointF[][]?[]
                ?? throw new InvalidOperationException(
                    "Random Fracture multi-terrain contour sampling returned no frame data.");
        }

        static bool HasBounds(PointF[][]? contours, RectangleF expected)
        {
            return contours is not null
                && contours.Any(contour =>
                    TryGetRandomFractureGradientContoursBounds([contour], out var actual)
                    && Math.Abs(actual.Left - expected.Left) <= 2f
                    && Math.Abs(actual.Top - expected.Top) <= 2f
                    && Math.Abs(actual.Right - expected.Right) <= 2f
                    && Math.Abs(actual.Bottom - expected.Bottom) <= 2f);
        }

        var terrainContoursByFrame = ReadTerrainContours();
        if (terrainContoursByFrame.Length != animationFrames + 1
            || !HasBounds(terrainContoursByFrame[0], westStartBounds)
            || !HasBounds(terrainContoursByFrame[0], eastStartBounds)
            || !HasBounds(terrainContoursByFrame[animationFrames / 2], westMiddleBounds)
            || !HasBounds(terrainContoursByFrame[animationFrames / 2], eastMiddleBounds)
            || !HasBounds(terrainContoursByFrame[animationFrames], westEndBounds)
            || !HasBounds(terrainContoursByFrame[animationFrames], eastEndBounds))
        {
            throw new InvalidOperationException(
                "Random Fracture did not combine both animated terrain layers at each sampled frame.");
        }

        if (!scene.SetLayerVisible(eastLayer, false))
        {
            throw new InvalidOperationException(
                "Random Fracture multi-terrain setup could not hide the secondary terrain layer.");
        }

        var hiddenTerrainContours = ReadTerrainContours();
        if (!HasBounds(hiddenTerrainContours[0], westStartBounds)
            || HasBounds(hiddenTerrainContours[0], eastStartBounds))
        {
            throw new InvalidOperationException(
                "Random Fracture included a hidden terrain layer in collision contour sampling.");
        }

        scene.SetLayerVisible(eastLayer, true);
        scene.EditFrame = 0;
        var options = new RandomFractureOptions
        {
            FragmentCountLimit = 8,
            EdgeCount = 6,
            FragmentRandomness = 0.45f,
            RandomSeed = 20260907,
            PreserveStroke = false,
            Mode = RandomFractureMode.Pointillize,
            ForceAlgorithm = RandomFractureForceAlgorithm.Directional,
            DirectionAngleDegrees = -90,
            FractureStrength = 0,
            RotationStrengthDegrees = 0,
            GenerateAnimation = true,
            AnimationFrames = animationFrames,
            GravityPixelsPerFrameSquared = 8,
            AirResistancePercent = 0,
            BouncePercent = 0,
            GroundPositionPercent = 400,
            AllowOverlap = true,
            EnableFragmentCollisions = true
        };

        if (!scene.TryCreateRandomFracturePreview(
                sourceObject,
                0,
                options,
                out var allTerrainPreview,
                out _,
                out var previewError,
                previewFrame: animationFrames)
            || allTerrainPreview.ObjectCount < 2
            || allTerrainPreview.RandomFracturePreviewTerrainFrame != animationFrames)
        {
            throw new InvalidOperationException(
                $"Random Fracture multi-terrain preview failed: {previewError}");
        }

        var allTerrainStates = RandomFracturePreviewStates(allTerrainPreview);
        if (!scene.SetLayerVisible(eastLayer, false))
        {
            throw new InvalidOperationException(
                "Random Fracture multi-terrain setup could not invalidate its hidden-layer preview.");
        }

        if (!scene.TryCreateRandomFracturePreview(
                sourceObject,
                0,
                options,
                out var hiddenEastPreview,
                out _,
                out previewError,
                previewFrame: animationFrames))
        {
            throw new InvalidOperationException(
                $"Random Fracture hidden-terrain preview failed: {previewError}");
        }

        var hiddenEastStates = RandomFracturePreviewStates(hiddenEastPreview);
        if (allTerrainStates.Length != hiddenEastStates.Length
            || !allTerrainStates.Zip(hiddenEastStates).Any(pair =>
                Math.Abs(pair.First.Center.X - pair.Second.Center.X) > 2f
                || Math.Abs(pair.First.Center.Y - pair.Second.Center.Y) > 2f
                || Math.Abs(pair.First.Rotation - pair.Second.Rotation) > 0.0001f))
        {
            throw new InvalidOperationException(
                "Random Fracture preview did not respond to removing the hidden animated terrain layer.");
        }

        scene.SetLayerVisible(eastLayer, true);
        Console.WriteLine("random_fracture_multiple_terrain_layers=ok");
    }

    private static void RunRandomFractureTerrainOverlayRegression(
        VectorScene scene, VectorScene movingPreview, VectorScene blankPreview, int frame)
    {
        using var stage = new StageControl(scene) { Size = new Size(320, 200), Frame = frame };
        stage.BindScene(scene);
        var draw = RequireMethod(typeof(StageControl), "DrawCollisionTerrainOverlay",
            [typeof(Graphics), typeof(VectorScene)]);
        int[] Draw(VectorScene? preview)
        {
            stage.BindDragPreviewScene(preview, scene);
            using var bitmap = new Bitmap(stage.Width, stage.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                draw.Invoke(stage, [graphics, scene]);
            }
            return Enumerable.Range(0, bitmap.Width * bitmap.Height)
                .Select(index => bitmap.GetPixel(index % bitmap.Width, index / bitmap.Width).ToArgb())
                .ToArray();
        }
        var original = Draw(null);
        var moved = Draw(movingPreview);
        var blank = Draw(blankPreview);
        var restored = Draw(null);
        if (!original.Any(pixel => pixel != 0)
            || !moved.Any(pixel => pixel != 0)
            || original.SequenceEqual(moved)
            || blank.Any(pixel => pixel != 0)
            || !original.SequenceEqual(restored)
            || stage.Frame != frame)
        {
            throw new InvalidOperationException(
                "Terrain overlay did not follow the preview, clear its blank frame, and restore after cancellation.");
        }
        Console.WriteLine("random_fracture_terrain_overlay=ok");
    }

    private static (VectorScene Scene, int SourceObject, int TerrainLayer) CreateRandomFractureAnimatedTerrainScene(
        bool animateTerrain,
        bool addBlankTerrainKeyframe,
        int startFrame,
        int terrainTweenEndFrame,
        int blankTerrainFrame)
    {
        var scene = new VectorScene();
        scene.CreateEmpty(layers: 1, frameCount: 16);
        scene.EditFrame = 0;
        var sourceObject = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(180, 180),
            0,
            0,
            Color.CornflowerBlue,
            Color.Transparent,
            128,
            ShapeKind.Rectangle);
        scene.EditFrame = startFrame;
        var terrainLayer = scene.AddCollisionTerrainLayer();
        var startTerrain = new[]
        {
            new PointF(-520, 90),
            new PointF(520, 90),
            new PointF(520, 150),
            new PointF(-520, 150)
        };
        if (scene.AppendPathObjectContours(
                terrainLayer,
                new[] { startTerrain },
                0,
                Color.DimGray,
                Color.Transparent,
                32) < 0)
        {
            throw new InvalidOperationException("Random Fracture animated terrain setup could not create its start terrain.");
        }

        if (animateTerrain)
        {
            scene.EditFrame = terrainTweenEndFrame;
            if (!scene.InsertTimelineKeyframe(terrainLayer, terrainTweenEndFrame))
            {
                throw new InvalidOperationException(
                    "Random Fracture animated terrain setup could not create the Shape Tween endpoint.");
            }

            var copiedEndpoint = Enumerable.Range(0, scene.ObjectCount)
                .FirstOrDefault(index => scene.ObjectLayer[index] == terrainLayer
                    && scene.ObjectKeyframeFrame[index] == terrainTweenEndFrame,
                    -1);
            if (copiedEndpoint < 0 || !scene.RemoveObjectAt(copiedEndpoint))
            {
                throw new InvalidOperationException(
                    "Random Fracture animated terrain setup could not replace the Shape Tween endpoint.");
            }

            var endTerrain = new[]
            {
                new PointF(-520, 20),
                new PointF(520, -20),
                new PointF(520, 40),
                new PointF(-520, 80)
            };
            if (scene.AppendPathObjectContours(
                    terrainLayer,
                    new[] { endTerrain },
                    0,
                    Color.DimGray,
                    Color.Transparent,
                    32) < 0)
            {
                throw new InvalidOperationException(
                    "Random Fracture animated terrain setup could not create the Shape Tween endpoint geometry.");
            }

            scene.EditFrame = startFrame;
            if (!scene.TryCreateTimelineTween(
                    terrainLayer,
                    startFrame,
                    terrainTweenEndFrame,
                    TimelineTweenKind.Shape,
                    out var tweenError))
            {
                throw new InvalidOperationException(
                    $"Random Fracture animated terrain setup could not create its Shape Tween: {tweenError}");
            }

            var track = scene.Timeline.FindTrackByTargetId(scene.LayerIds[terrainLayer]);
            if (track?.EvaluateTween(startFrame + 1) is not { Kind: TimelineTweenKind.Shape })
            {
                throw new InvalidOperationException(
                    "Random Fracture animated terrain setup did not retain a real Mask Shape Tween.");
            }

            if (addBlankTerrainKeyframe)
            {
                scene.EditFrame = blankTerrainFrame;
                if (!scene.InsertTimelineBlankKeyframe(terrainLayer, blankTerrainFrame))
                {
                    throw new InvalidOperationException(
                        "Random Fracture animated terrain setup could not create its blank endpoint exposure.");
                }
            }
        }

        scene.EditFrame = startFrame;
        return (scene, sourceObject, terrainLayer);
    }

    private static (PointF Center, float Rotation)[] RandomFracturePreviewStates(VectorScene preview)
    {
        return Enumerable.Range(0, preview.ObjectCount)
            .OrderBy(index => preview.ObjectLayer[index])
            .Select(index => (new PointF(preview.X[index], preview.Y[index]), preview.Angle[index]))
            .ToArray();
    }

    private static (PointF Center, float Rotation)[] RandomFractureAppliedStates(
        VectorScene scene,
        IReadOnlyList<int> startObjects,
        int frame)
    {
        var fragmentLayerIds = startObjects
            .Where(index => (uint)index < scene.ObjectCount)
            .Select(index => scene.LayerIds[scene.ObjectLayer[index]])
            .ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.ObjectKeyframeFrame[index] == frame
                && fragmentLayerIds.Contains(scene.LayerIds[scene.ObjectLayer[index]]))
            .OrderBy(index => scene.ObjectLayer[index])
            .Select(index => (new PointF(scene.X[index], scene.Y[index]), scene.Angle[index]))
            .ToArray();
    }

    private static void RunRandomFractureGradientRegression()
    {
        var stops = new[]
        {
            new GradientStop(0f, Color.FromArgb(232, 232, 44, 64)),
            new GradientStop(0.18f, Color.FromArgb(96, 24, 208, 120)),
            new GradientStop(0.46f, Color.FromArgb(0, 56, 112, 224)),
            new GradientStop(0.73f, Color.FromArgb(176, 248, 176, 32)),
            new GradientStop(1f, Color.FromArgb(244, 96, 32, 208))
        };
        var sourceContours = new[]
        {
            new[]
            {
                new PointF(-640, -420),
                new PointF(420, -520),
                new PointF(690, -120),
                new PointF(580, 390),
                new PointF(-120, 520),
                new PointF(-650, 280),
                new PointF(-760, -80)
            }
        };
        var gradientStart = new PointF(-760, -460);
        var gradientEnd = new PointF(700, 440);
        var gradientPath = new[]
        {
            new PointF(-720, -350),
            new PointF(-390, -170),
            new PointF(-20, 20),
            new PointF(340, 190),
            new PointF(680, 360)
        };
        var options = new RandomFractureOptions
        {
            FragmentCountLimit = 7,
            EdgeCount = 7,
            FragmentRandomness = 0.68f,
            RandomSeed = 20260901,
            PreserveStroke = false,
            Mode = RandomFractureMode.Pointillize,
            ForceAlgorithm = RandomFractureForceAlgorithm.Directional,
            DirectionAngleDegrees = -35,
            FractureStrength = 14,
            RotationStrengthDegrees = 9,
            GenerateAnimation = true,
            AnimationFrames = 5,
            GravityPixelsPerFrameSquared = 0,
            AirResistancePercent = 0,
            BouncePercent = 0,
            AllowOverlap = true,
            EnableFragmentCollisions = false
        };

        foreach (var kind in new[]
        {
            GradientKind.Linear,
            GradientKind.Radial,
            GradientKind.ShapeRadial
        })
        {
            var previewSetup = CreateRandomFractureGradientScene(
                kind,
                stops,
                sourceContours,
                gradientStart,
                gradientEnd,
                gradientPath);
            var source = previewSetup.Scene;
            var sourceObject = previewSetup.ObjectIndex;
            var sourcePath = kind == GradientKind.Linear
                && source.TryGetGradientPathWorldPoints(sourceObject, out var capturedPath)
                ? capturedPath
                : Array.Empty<PointF>();
            var sourceMapping = kind == GradientKind.ShapeRadial
                ? source.GetShapeGradientMappingContours(sourceObject)
                : Array.Empty<PointF[]>();

            if (!source.TryCreateRandomFracturePreview(
                    sourceObject,
                    0,
                    options,
                    out var startPreview,
                    out _,
                    out var error,
                    previewFrame: 0)
                || !source.TryCreateRandomFracturePreview(
                    sourceObject,
                    0,
                    options,
                    out var endPreview,
                    out _,
                    out error,
                    previewFrame: options.AnimationFrames))
            {
                throw new InvalidOperationException(
                    $"Random Fracture {kind} gradient preview failed: {error}");
            }

            if (startPreview.ObjectCount < 2
                || startPreview.ObjectCount != endPreview.ObjectCount
                || !Enumerable.Range(0, endPreview.ObjectCount).All(index =>
                    endPreview.GetGradientKind(index) == kind
                    && endPreview.GetGradientStops(index).SequenceEqual(stops)))
            {
                throw new InvalidOperationException(
                    $"Random Fracture {kind} preview did not preserve all gradient stops.");
            }

            for (var index = 0; index < endPreview.ObjectCount; index++)
            {
                var startCenter = new PointF(startPreview.X[index], startPreview.Y[index]);
                var motion = new RandomFractureMotion(
                    new PointF(
                        endPreview.X[index] - startCenter.X,
                        endPreview.Y[index] - startCenter.Y),
                    endPreview.Angle[index]);
                var expectedStart = TransformRandomFractureGradientPoint(
                    gradientStart,
                    startCenter,
                    motion);
                var expectedEnd = TransformRandomFractureGradientPoint(
                    gradientEnd,
                    startCenter,
                    motion);
                if (!PointsWithin(endPreview.GetGradientStart(index), expectedStart, 2f)
                    || !PointsWithin(endPreview.GetGradientEnd(index), expectedEnd, 2f))
                {
                    throw new InvalidOperationException(
                        $"Random Fracture {kind} preview did not move gradient coordinates with the fragment.");
                }

                if (kind == GradientKind.Linear)
                {
                    if (!startPreview.TryGetGradientPathWorldPoints(index, out var startPath)
                        || !endPreview.TryGetGradientPathWorldPoints(index, out var endPath)
                        || startPath.Length != sourcePath.Length
                        || endPath.Length != sourcePath.Length
                        || !startPath.Zip(sourcePath).All(pair => PointsNear(pair.First, pair.Second))
                        || !endPath.Zip(sourcePath.Select(point =>
                            TransformRandomFractureGradientPoint(point, startCenter, motion)))
                            .All(pair => PointsWithin(pair.First, pair.Second, 2f)))
                    {
                        throw new InvalidOperationException(
                            "Random Fracture linear gradient trajectory was lost or rotated twice.");
                    }
                }

                if (kind == GradientKind.ShapeRadial)
                {
                    var expectedMapping = sourceMapping
                        .Select(contour => contour
                            .Select(point => TransformRandomFractureGradientPoint(point, startCenter, motion))
                            .ToArray())
                        .ToArray();
                    if (!endPreview.TryGetShapeGradientMappingWorldContours(index, out var actualMapping)
                        || !RandomFractureGradientContoursBoundsNear(actualMapping, expectedMapping, 2f))
                    {
                        throw new InvalidOperationException(
                            "Random Fracture ShapeRadial gradient did not retain the source mapping contour.");
                    }
                }
            }

            var applySetup = CreateRandomFractureGradientScene(
                kind,
                stops,
                sourceContours,
                gradientStart,
                gradientEnd,
                gradientPath);
            var applySource = applySetup.Scene;
            if (!applySource.TryApplyRandomFracture(
                    applySetup.ObjectIndex,
                    0,
                    options,
                    out var appliedFragments,
                    out error))
            {
                throw new InvalidOperationException(
                    $"Random Fracture {kind} gradient apply failed: {error}");
            }

            var appliedGradientObjects = Enumerable.Range(0, applySource.ObjectCount)
                .Where(index => applySource.GetGradientKind(index) == kind)
                .ToArray();
            var expectedObjectCount = appliedFragments.Length * (options.AnimationFrames + 1);
            if (appliedGradientObjects.Length != expectedObjectCount
                || appliedGradientObjects.Any(index =>
                    !applySource.GetGradientStops(index).SequenceEqual(stops)))
            {
                throw new InvalidOperationException(
                    $"Random Fracture {kind} animation did not preserve complex gradient materials on every Cel.");
            }
        }

        Console.WriteLine("random_fracture_gradient=ok");
    }

    private static (VectorScene Scene, int ObjectIndex) CreateRandomFractureGradientScene(
        GradientKind kind,
        IReadOnlyList<GradientStop> stops,
        IReadOnlyList<PointF[]> contours,
        PointF start,
        PointF end,
        IReadOnlyList<PointF> path)
    {
        var scene = new VectorScene();
        scene.CreateEmpty(layers: 1, frameCount: 8);
        scene.EditFrame = 0;
        var objectIndex = scene.AppendPathObjectContours(
            0,
            contours,
            0,
            Color.Transparent,
            Color.Transparent,
            64);
        if (objectIndex < 0) throw new InvalidOperationException("Random Fracture gradient setup could not create a Path.");

        scene.SetGradientPaint(objectIndex, kind, stops, start, end);
        if (kind == GradientKind.Linear) scene.SetGradientPath(objectIndex, path);
        return (scene, objectIndex);
    }

    private static PointF TransformRandomFractureGradientPoint(
        PointF point,
        PointF center,
        RandomFractureMotion motion)
    {
        var sine = MathF.Sin(motion.Rotation);
        var cosine = MathF.Cos(motion.Rotation);
        var localX = point.X - center.X;
        var localY = point.Y - center.Y;
        return new PointF(
            center.X + motion.Offset.X + localX * cosine - localY * sine,
            center.Y + motion.Offset.Y + localX * sine + localY * cosine);
    }

    private static bool RandomFractureGradientContoursBoundsNear(
        IReadOnlyList<PointF[]> actual,
        IReadOnlyList<PointF[]> expected,
        float tolerance)
    {
        if (!TryGetRandomFractureGradientContoursBounds(actual, out var actualBounds)
            || !TryGetRandomFractureGradientContoursBounds(expected, out var expectedBounds))
        {
            return false;
        }

        return Math.Abs(actualBounds.Left - expectedBounds.Left) <= tolerance
            && Math.Abs(actualBounds.Top - expectedBounds.Top) <= tolerance
            && Math.Abs(actualBounds.Right - expectedBounds.Right) <= tolerance
            && Math.Abs(actualBounds.Bottom - expectedBounds.Bottom) <= tolerance;
    }

    private static bool TryGetRandomFractureGradientContoursBounds(
        IReadOnlyList<PointF[]> contours,
        out RectangleF bounds)
    {
        bounds = RectangleF.Empty;
        var hasPoint = false;
        foreach (var contour in contours)
        {
            foreach (var point in contour)
            {
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) continue;
                bounds = hasPoint
                    ? RectangleF.Union(bounds, new RectangleF(point.X, point.Y, 0, 0))
                    : new RectangleF(point.X, point.Y, 0, 0);
                hasPoint = true;
            }
        }

        return hasPoint;
    }

    private static List<int> ActiveObjectsForRandomFracture(VectorScene scene, int frame)
    {
        return Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.IsObjectActive(index, frame))
            .ToList();
    }

    private static bool RandomFractureFragmentsMatch(
        IReadOnlyList<RandomFractureFragment> first,
        IReadOnlyList<RandomFractureFragment> second)
    {
        if (first.Count != second.Count) return false;
        for (var index = 0; index < first.Count; index++)
        {
            if (first[index].SeedIndex != second[index].SeedIndex
                || Math.Abs(first[index].Area - second[index].Area) > 0.001
                || first[index].Contours.Length != second[index].Contours.Length)
            {
                return false;
            }

            for (var contourIndex = 0; contourIndex < first[index].Contours.Length; contourIndex++)
            {
                var firstContour = first[index].Contours[contourIndex];
                var secondContour = second[index].Contours[contourIndex];
                if (firstContour.Length != secondContour.Length) return false;
                for (var pointIndex = 0; pointIndex < firstContour.Length; pointIndex++)
                {
                    if (!PointsNear(firstContour[pointIndex], secondContour[pointIndex])) return false;
                }
            }
        }

        return true;
    }

    private static double RandomFractureIntersectionArea(
        RandomFractureFragment first,
        RandomFractureMotion firstMotion,
        RandomFractureFragment second,
        RandomFractureMotion secondMotion)
    {
        var firstPaths = RandomFractureContoursToPaths(first.Contours, first.Center, firstMotion);
        var secondPaths = RandomFractureContoursToPaths(second.Contours, second.Center, secondMotion);
        return firstPaths.Count == 0 || secondPaths.Count == 0
            ? 0d
            : Math.Abs(Clipper.Intersect(firstPaths, secondPaths, FillRule.EvenOdd).Sum(Clipper.Area)) / 1_000_000d;
    }

    private static Paths64 RandomFractureContoursToPaths(
        IReadOnlyList<PointF[]> contours,
        PointF center,
        RandomFractureMotion motion)
    {
        var paths = new Paths64();
        var sine = MathF.Sin(motion.Rotation);
        var cosine = MathF.Cos(motion.Rotation);
        foreach (var contour in contours)
        {
            var path = new Path64();
            foreach (var point in contour)
            {
                var localX = point.X - center.X;
                var localY = point.Y - center.Y;
                var world = new PointF(
                    center.X + motion.Offset.X + localX * cosine - localY * sine,
                    center.Y + motion.Offset.Y + localX * sine + localY * cosine);
                path.Add(new Point64(
                    (long)Math.Round(world.X * 1000d, MidpointRounding.AwayFromZero),
                    (long)Math.Round(world.Y * 1000d, MidpointRounding.AwayFromZero)));
            }

            if (path.Count >= 3) paths.Add(path);
        }

        return paths;
    }

    private static void AssertRandomFracture(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException($"Random Fracture generation failed: {error}");
    }

    private static float NormalizeTestAngle(float angle)
    {
        while (angle > MathF.PI * 0.5f) angle -= MathF.PI;
        while (angle < -MathF.PI * 0.5f) angle += MathF.PI;
        return angle;
    }

    private static float DistanceSquaredTest(PointF first, PointF second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return dx * dx + dy * dy;
    }
}
