using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed class MixingBrushRegionAccumulator
{
    private const double MinimumSegmentLength = 0.000001d;
    private const float CoverageEpsilon = 0.5f / 255f;
    private const float GridFilterRadius = MixingBrushRegionData.MinimumVertexSpacing;

    private static readonly float[] SrgbToLinearLookup = CreateSrgbToLinearLookup();

    private readonly record struct GridKey(long X, long Y);
    private readonly record struct GridCell(long X, long Y);
    private readonly record struct PaintState(Color Color, PigmentMixState Pigment);
    private readonly record struct WorkingSample(Color Color, PigmentMixState Pigment, bool HasPigment);

    private struct VertexPaint
    {
        internal Color SettledColor;
        internal PigmentMixState SettledPigment;
        internal bool HasSettledPigment;
        internal Color PassColor;
        internal PigmentMixState PassPigment;
        internal bool HasPassPigment;
        internal float PassTargetAlpha;
        internal double LastTouchDistance;
    }

    private readonly Color _loadedColor;
    private readonly MixingBrushSettings _settings;
    private readonly float _diameter;
    private readonly float _radius;
    private readonly double _passReleaseDistance;
    private readonly Func<PointF, Color?>? _committedSampler;
    private readonly PigmentMixState _loadedPigment;
    private readonly bool _isPigment;
    private readonly float _pickupRate;
    private readonly float _recoveryRate;
    private readonly float _coreOpacity;
    private readonly Dictionary<GridKey, VertexPaint> _paint = new();
    private readonly HashSet<GridCell> _affectedCells = [];
    private readonly List<GridCell> _orderedAffectedCells = [];
    private readonly List<double> _controlDistances = [];
    private Color _carriedColor;
    private PigmentMixState _carriedPigment;
    private PointF _lastInputPoint;
    private PointF _lastControlPoint;
    private PaintState _lastDepositState;
    private double _totalInputDistance;
    private double _nextControlDistance = MixingBrushRegionData.MinimumVertexSpacing;
    private double _lastControlDistance;
    private bool _hasInput;
    private bool _hasControl;
    private bool _affectedCellOrderDirty;
    private MixingBrushRegionData? _cachedRegion;
    private MixingBrushRegionData? _cachedPreviewRegion;

    internal MixingBrushRegionAccumulator(
        Color loadedColor,
        MixingBrushSettings settings,
        float diameter,
        Func<PointF, Color?>? committedSampler)
    {
        _loadedColor = loadedColor;
        _settings = new MixingBrushSettings(
            settings.Mode,
            settings.Strength,
            settings.Viscosity,
            settings.PaintLoad,
            settings.Influence);
        _diameter = float.IsFinite(diameter)
            ? Math.Max(VectorUnits.MinimumStrokeUnits, diameter)
            : VectorUnits.MinimumStrokeUnits;
        _radius = _diameter * 0.5f;
        _passReleaseDistance = MixingBrushRegionData.MinimumVertexSpacing * 1.5d;
        _committedSampler = committedSampler;
        _carriedColor = loadedColor;
        _loadedPigment = PaintColorMixer.CreatePigmentState(loadedColor);
        _carriedPigment = _loadedPigment;
        _isPigment = _settings.Mode == BrushMixingMode.Pigment;
        _pickupRate = _settings.Strength
            * Lerp(4f, 0.35f, _settings.Viscosity)
            * Lerp(1f, 0.1f, _settings.PaintLoad);
        _recoveryRate = _settings.Influence
            * Lerp(0.25f, 1.5f, _settings.PaintLoad)
            * Lerp(1f, 0.35f, _settings.Viscosity);
        _coreOpacity = _isPigment
            ? 1f
            : Lerp(0.12f, 0.28f, _settings.PaintLoad);
    }

    internal bool IsComplete { get; private set; }
    internal double TotalDistance => _totalInputDistance;
    internal int WorkingVertexCount => _paint.Count;
    internal IReadOnlyList<double> ControlDistances => _controlDistances;

    internal void Append(PointF point)
    {
        if (IsComplete)
        {
            throw new InvalidOperationException("Cannot append to a completed mixing-brush region.");
        }
        if (!IsFinite(point)) return;
        point = VectorUnits.Quantize(point);

        if (!_hasInput)
        {
            _hasInput = true;
            _lastInputPoint = point;
            ProcessControlPoint(
                point,
                controlDistance: 0d,
                travelledDistance: MixingBrushRegionData.MinimumVertexSpacing);
            return;
        }

        var start = _lastInputPoint;
        var segmentLength = Distance(start, point);
        _lastInputPoint = point;
        if (!double.IsFinite(segmentLength) || segmentLength <= MinimumSegmentLength) return;

        var segmentStartDistance = _totalInputDistance;
        var segmentEndDistance = segmentStartDistance + segmentLength;
        var tolerance = MixingBrushRegionData.MinimumVertexSpacing * 0.0000001d;
        while (_nextControlDistance <= segmentEndDistance + tolerance)
        {
            var controlDistance = Math.Min(_nextControlDistance, segmentEndDistance);
            var amount = Math.Clamp(
                (_nextControlDistance - segmentStartDistance) / segmentLength,
                0d,
                1d);
            ProcessControlPoint(
                Lerp(start, point, amount),
                controlDistance,
                MixingBrushRegionData.MinimumVertexSpacing);
            _nextControlDistance += MixingBrushRegionData.MinimumVertexSpacing;
        }

        _totalInputDistance = segmentEndDistance;
    }

    internal void Complete()
    {
        if (IsComplete) return;
        IsComplete = true;
        if (!_hasInput || !_hasControl) return;

        var remainingDistance = _totalInputDistance - _lastControlDistance;
        if (remainingDistance <= MinimumSegmentLength) return;
        ProcessControlPoint(
            _lastInputPoint,
            _totalInputDistance,
            remainingDistance);
    }

    internal bool TryCreateRegion(out MixingBrushRegionData region)
    {
        return TryCreateRegionCore(out region, canonicalize: true, validate: true);
    }

    // Preview meshes are generated from the accumulator's fixed grid, so expensive
    // overlap and spacing validation can wait until the final committed region.
    internal bool TryCreatePreviewRegion(out MixingBrushRegionData region)
    {
        return TryCreateRegionCore(out region, canonicalize: false, validate: false);
    }

    private bool TryCreateRegionCore(
        out MixingBrushRegionData region,
        bool canonicalize,
        bool validate)
    {
        var cached = validate ? _cachedRegion : _cachedPreviewRegion;
        if (cached is not null)
        {
            region = cached;
            return true;
        }

        if (canonicalize && _affectedCellOrderDirty)
        {
            _orderedAffectedCells.Sort(static (first, second) =>
            {
                var comparison = first.Y.CompareTo(second.Y);
                return comparison != 0 ? comparison : first.X.CompareTo(second.X);
            });
            _affectedCellOrderDirty = false;
        }
        if (_orderedAffectedCells.Count == 0)
        {
            region = new MixingBrushRegionData([], []);
            return false;
        }

        var vertices = new List<MixingBrushRegionVertex>(
            Math.Max(_paint.Count, _orderedAffectedCells.Count));
        var indices = new List<int>(_orderedAffectedCells.Count * 6);
        var vertexIndices = new Dictionary<GridKey, int>(
            Math.Max(_paint.Count, _orderedAffectedCells.Count));

        int VertexIndex(GridKey key)
        {
            if (vertexIndices.TryGetValue(key, out var index)) return index;
            index = vertices.Count;
            vertexIndices[key] = index;
            vertices.Add(new MixingBrushRegionVertex(GridPoint(key), OutputColor(key).ToArgb()));
            return index;
        }

        foreach (var cell in _orderedAffectedCells)
        {
            var topLeft = VertexIndex(new GridKey(cell.X, cell.Y));
            var topRight = VertexIndex(new GridKey(cell.X + 1, cell.Y));
            var bottomRight = VertexIndex(new GridKey(cell.X + 1, cell.Y + 1));
            var bottomLeft = VertexIndex(new GridKey(cell.X, cell.Y + 1));
            indices.Add(topLeft);
            indices.Add(topRight);
            indices.Add(bottomRight);
            indices.Add(topLeft);
            indices.Add(bottomRight);
            indices.Add(bottomLeft);
        }

        MixingBrushRegionData result;
        if (validate)
        {
            if (!MixingBrushRegionData.TryNormalize(vertices, indices, out result)
                || !MixingBrushRegionData.HasMinimumVertexSpacing(result.Vertices))
            {
                region = new MixingBrushRegionData([], []);
                return false;
            }
        }
        else
        {
            result = new MixingBrushRegionData(vertices.ToArray(), indices.ToArray());
        }

        if (validate) _cachedRegion = result;
        else _cachedPreviewRegion = result;
        region = result;
        return true;
    }

    private void ProcessControlPoint(
        PointF point,
        double controlDistance,
        double travelledDistance)
    {
        point = VectorUnits.Quantize(point);
        AdvanceCarriedColor(point, controlDistance, travelledDistance);
        var depositState = new PaintState(_carriedColor, _carriedPigment);
        if (_hasControl)
        {
            RasterizeSegment(
                _lastControlPoint,
                point,
                _lastControlDistance,
                controlDistance,
                _lastDepositState,
                depositState);
        }
        else
        {
            RasterizeSegment(
                point,
                point,
                controlDistance,
                controlDistance,
                depositState,
                depositState);
        }

        _hasControl = true;
        _lastControlPoint = point;
        _lastControlDistance = controlDistance;
        _lastDepositState = depositState;
        _controlDistances.Add(controlDistance);
    }

    private void AdvanceCarriedColor(
        PointF point,
        double controlDistance,
        double travelledDistance)
    {
        if (travelledDistance <= 0d) return;

        var committed = _committedSampler?.Invoke(point);
        var working = SampleWorkingField(point, controlDistance);
        var surface = CompositeLinear(
            committed is { A: > 0 } committedColor ? committedColor : Color.Transparent,
            working.Color);
        if (surface.A > 0)
        {
            if (_settings.Strength <= 0f) return;

            var pickupAmount = ExponentialResponse(travelledDistance, _pickupRate)
                * (surface.A / 255f);
            if (_isPigment)
            {
                var surfacePigment = SurfacePigment(committed, working, surface);
                _carriedPigment = PigmentMixState.Blend(
                    _carriedPigment,
                    surfacePigment,
                    pickupAmount);
                _carriedColor = PaintColorMixer.PigmentStateToColor(
                    _carriedPigment,
                    _carriedColor.A);
            }
            else
            {
                var opaqueSurface = Color.FromArgb(
                    _carriedColor.A,
                    surface.R,
                    surface.G,
                    surface.B);
                _carriedColor = PaintColorMixer.Mix(
                    _carriedColor,
                    opaqueSurface,
                    pickupAmount,
                    BrushMixingMode.Optical);
            }
            return;
        }

        if (_settings.Influence <= 0f) return;
        var recoveryAmount = ExponentialResponse(travelledDistance, _recoveryRate);
        if (_isPigment)
        {
            _carriedPigment = PigmentMixState.Blend(
                _carriedPigment,
                _loadedPigment,
                recoveryAmount);
            _carriedColor = PaintColorMixer.PigmentStateToColor(
                _carriedPigment,
                _carriedColor.A);
        }
        else
        {
            _carriedColor = PaintColorMixer.Mix(
                _carriedColor,
                _loadedColor,
                recoveryAmount,
                BrushMixingMode.Optical);
        }
    }

    private PigmentMixState SurfacePigment(
        Color? committed,
        WorkingSample working,
        Color composite)
    {
        if (!working.HasPigment)
        {
            return PaintColorMixer.CreatePigmentState(composite);
        }
        if (committed is not { A: > 0 } committedColor)
        {
            return working.Pigment;
        }

        var backdropAlpha = committedColor.A / 255f;
        var sourceAlpha = working.Color.A / 255f;
        var outputAlpha = sourceAlpha + backdropAlpha * (1f - sourceAlpha);
        if (outputAlpha <= CoverageEpsilon) return working.Pigment;
        var sourceWeight = sourceAlpha / outputAlpha;
        return PigmentMixState.Blend(
            PaintColorMixer.CreatePigmentState(committedColor),
            working.Pigment,
            sourceWeight);
    }

    private void RasterizeSegment(
        PointF start,
        PointF end,
        double startDistance,
        double endDistance,
        PaintState startState,
        PaintState endState)
    {
        var left = Math.Min(start.X, end.X) - _radius - GridFilterRadius;
        var right = Math.Max(start.X, end.X) + _radius + GridFilterRadius;
        var top = Math.Min(start.Y, end.Y) - _radius - GridFilterRadius;
        var bottom = Math.Max(start.Y, end.Y) + _radius + GridFilterRadius;
        if (!TryGridRange(left, right, out var minimumX, out var maximumX)
            || !TryGridRange(top, bottom, out var minimumY, out var maximumY))
        {
            return;
        }

        var segmentX = end.X - start.X;
        var segmentY = end.Y - start.Y;
        var segmentLengthSquared = segmentX * segmentX + segmentY * segmentY;
        var maximumPaintDistance = _radius + GridFilterRadius;
        var maximumPaintDistanceSquared = maximumPaintDistance * maximumPaintDistance;
        for (var gridY = minimumY; gridY <= maximumY; gridY++)
        {
            for (var gridX = minimumX; gridX <= maximumX; gridX++)
            {
                var key = new GridKey(gridX, gridY);
                var point = GridPoint(key);
                var amount = segmentLengthSquared <= 0.000001f
                    ? 1f
                    : Math.Clamp(
                        ((point.X - start.X) * segmentX + (point.Y - start.Y) * segmentY)
                            / segmentLengthSquared,
                        0f,
                        1f);
                var nearest = new PointF(
                    start.X + segmentX * amount,
                    start.Y + segmentY * amount);
                var distanceX = (double)point.X - nearest.X;
                var distanceY = (double)point.Y - nearest.Y;
                var distanceSquared = distanceX * distanceX + distanceY * distanceY;
                if (distanceSquared >= maximumPaintDistanceSquared) continue;
                var distance = Math.Sqrt(distanceSquared);
                var coverage = CoverageAtDistance((float)distance);
                if (coverage <= CoverageEpsilon) continue;

                var pathDistance = startDistance + (endDistance - startDistance) * amount;
                var deposit = InterpolatePaintState(startState, endState, amount);
                DepositAtVertex(key, deposit, coverage, pathDistance);
            }
        }
    }

    private void DepositAtVertex(
        GridKey key,
        PaintState deposit,
        float coverage,
        double pathDistance)
    {
        ref var vertex = ref CollectionsMarshal.GetValueRefOrAddDefault(
            _paint,
            key,
            out var exists);
        if (exists && pathDistance - vertex.LastTouchDistance > _passReleaseDistance)
        {
            SettlePass(ref vertex);
        }

        var targetAlpha = Math.Clamp(
            deposit.Color.A / 255f * _coreOpacity * coverage,
            0f,
            1f);
        var previousTargetAlpha = vertex.PassTargetAlpha;
        vertex.LastTouchDistance = pathDistance;
        if (targetAlpha <= previousTargetAlpha + CoverageEpsilon) return;

        var incrementalAlpha = previousTargetAlpha >= 1f - CoverageEpsilon
            ? 0f
            : (targetAlpha - previousTargetAlpha) / (1f - previousTargetAlpha);
        vertex.PassTargetAlpha = targetAlpha;
        if (incrementalAlpha <= CoverageEpsilon) return;

        var source = Color.FromArgb(
            Math.Clamp((int)MathF.Round(incrementalAlpha * 255f), 1, 255),
            deposit.Color.R,
            deposit.Color.G,
            deposit.Color.B);
        var previousPassAlpha = vertex.PassColor.A / 255f;
        vertex.PassColor = CompositeLinear(vertex.PassColor, source);
        if (_isPigment)
        {
            vertex.PassPigment = BlendPigmentSourceOver(
                vertex.PassPigment,
                previousPassAlpha,
                vertex.HasPassPigment,
                deposit.Pigment,
                incrementalAlpha);
            vertex.HasPassPigment = true;
        }
        MarkAffectedCells(key);
        _cachedRegion = null;
        _cachedPreviewRegion = null;
    }

    private WorkingSample SampleWorkingField(PointF point, double pathDistance)
    {
        var spacing = MixingBrushRegionData.MinimumVertexSpacing;
        var gridX = Math.Floor(point.X / spacing);
        var gridY = Math.Floor(point.Y / spacing);
        if (!TryLong(gridX, out var cellX) || !TryLong(gridY, out var cellY)) return default;

        var left = (float)(cellX * (double)spacing);
        var top = (float)(cellY * (double)spacing);
        var x = Math.Clamp((point.X - left) / spacing, 0f, 1f);
        var y = Math.Clamp((point.Y - top) / spacing, 0f, 1f);
        if (x >= y)
        {
            return InterpolateWorking(
                EligiblePaint(new GridKey(cellX, cellY), pathDistance),
                EligiblePaint(new GridKey(cellX + 1, cellY), pathDistance),
                EligiblePaint(new GridKey(cellX + 1, cellY + 1), pathDistance),
                new PointF(1f - x, x - y));
        }

        return InterpolateWorking(
            EligiblePaint(new GridKey(cellX, cellY), pathDistance),
            EligiblePaint(new GridKey(cellX + 1, cellY + 1), pathDistance),
            EligiblePaint(new GridKey(cellX, cellY + 1), pathDistance),
            new PointF(1f - y, x));
    }

    private WorkingSample EligiblePaint(GridKey key, double pathDistance)
    {
        if (!_paint.TryGetValue(key, out var vertex)) return default;

        var includePass = pathDistance - vertex.LastTouchDistance > _passReleaseDistance;
        if (!includePass || vertex.PassColor.A == 0)
        {
            return new WorkingSample(
                vertex.SettledColor,
                vertex.SettledPigment,
                vertex.HasSettledPigment);
        }

        var color = CompositeLinear(vertex.SettledColor, vertex.PassColor);
        var pigment = BlendPigmentSourceOver(
            vertex.SettledPigment,
            vertex.SettledColor.A / 255f,
            vertex.HasSettledPigment,
            vertex.PassPigment,
            vertex.PassColor.A / 255f);
        return new WorkingSample(color, pigment, vertex.HasSettledPigment || vertex.HasPassPigment);
    }

    private static WorkingSample InterpolateWorking(
        WorkingSample first,
        WorkingSample second,
        WorkingSample third,
        PointF weights)
    {
        var color = MixingBrushRegionData.InterpolatePremultipliedLinear(
            first.Color.ToArgb(),
            second.Color.ToArgb(),
            third.Color.ToArgb(),
            weights);
        var thirdWeight = 1f - weights.X - weights.Y;
        var firstWeight = first.Color.A / 255f * weights.X;
        var secondWeight = second.Color.A / 255f * weights.Y;
        var lastWeight = third.Color.A / 255f * thirdWeight;
        var totalWeight = firstWeight + secondWeight + lastWeight;
        if (totalWeight <= CoverageEpsilon
            || !first.HasPigment && !second.HasPigment && !third.HasPigment)
        {
            return new WorkingSample(color, default, false);
        }

        var pigment = default(PigmentMixState);
        var accumulated = 0f;
        if (first.HasPigment && firstWeight > 0f)
        {
            pigment = first.Pigment;
            accumulated = firstWeight;
        }
        if (second.HasPigment && secondWeight > 0f)
        {
            pigment = accumulated <= 0f
                ? second.Pigment
                : PigmentMixState.Blend(
                    pigment,
                    second.Pigment,
                    secondWeight / (accumulated + secondWeight));
            accumulated += secondWeight;
        }
        if (third.HasPigment && lastWeight > 0f)
        {
            pigment = accumulated <= 0f
                ? third.Pigment
                : PigmentMixState.Blend(
                    pigment,
                    third.Pigment,
                    lastWeight / (accumulated + lastWeight));
            accumulated += lastWeight;
        }
        return new WorkingSample(color, pigment, accumulated > 0f);
    }

    private void SettlePass(ref VertexPaint vertex)
    {
        if (vertex.PassColor.A > 0)
        {
            var settledAlpha = vertex.SettledColor.A / 255f;
            var passAlpha = vertex.PassColor.A / 255f;
            vertex.SettledPigment = BlendPigmentSourceOver(
                vertex.SettledPigment,
                settledAlpha,
                vertex.HasSettledPigment,
                vertex.PassPigment,
                passAlpha);
            vertex.HasSettledPigment |= vertex.HasPassPigment;
            vertex.SettledColor = CompositeLinear(vertex.SettledColor, vertex.PassColor);
        }
        vertex.PassColor = Color.Transparent;
        vertex.PassPigment = default;
        vertex.HasPassPigment = false;
        vertex.PassTargetAlpha = 0f;
    }

    private Color OutputColor(GridKey key)
    {
        if (!_paint.TryGetValue(key, out var vertex)) return Color.Transparent;
        return CompositeLinear(vertex.SettledColor, vertex.PassColor);
    }

    private void MarkAffectedCells(GridKey key)
    {
        Mark(new GridCell(key.X - 1, key.Y - 1));
        Mark(new GridCell(key.X, key.Y - 1));
        Mark(new GridCell(key.X - 1, key.Y));
        Mark(new GridCell(key.X, key.Y));

        void Mark(GridCell cell)
        {
            if (!_affectedCells.Add(cell)) return;
            _orderedAffectedCells.Add(cell);
            _affectedCellOrderDirty = true;
        }
    }

    private PaintState InterpolatePaintState(PaintState start, PaintState end, float amount)
    {
        if (_isPigment)
        {
            var pigment = PigmentMixState.Blend(start.Pigment, end.Pigment, amount);
            var alpha = Math.Clamp(
                (int)MathF.Round(start.Color.A + (end.Color.A - start.Color.A) * amount),
                0,
                255);
            return new PaintState(PaintColorMixer.PigmentStateToColor(pigment, alpha), pigment);
        }

        return new PaintState(LerpLinearColor(start.Color, end.Color, amount), default);
    }

    private float CoverageAtDistance(float distance) =>
        Math.Clamp(
            0.5f + (_radius - distance) / (GridFilterRadius * 2f),
            0f,
            1f);

    private static PigmentMixState BlendPigmentSourceOver(
        PigmentMixState backdrop,
        float backdropAlpha,
        bool hasBackdrop,
        PigmentMixState source,
        float sourceAlpha)
    {
        if (sourceAlpha <= CoverageEpsilon) return backdrop;
        if (!hasBackdrop || backdropAlpha <= CoverageEpsilon) return source;
        var outputAlpha = sourceAlpha + backdropAlpha * (1f - sourceAlpha);
        return outputAlpha <= CoverageEpsilon
            ? source
            : PigmentMixState.Blend(backdrop, source, sourceAlpha / outputAlpha);
    }

    private static Color CompositeLinear(Color backdrop, Color source)
    {
        var sourceAlpha = source.A / 255f;
        if (sourceAlpha <= 0f) return backdrop;
        var backdropAlpha = backdrop.A / 255f;
        var outputAlpha = sourceAlpha + backdropAlpha * (1f - sourceAlpha);
        if (outputAlpha <= CoverageEpsilon) return Color.Transparent;

        var backdropWeight = backdropAlpha * (1f - sourceAlpha);
        float Channel(byte backdropChannel, byte sourceChannel) => LinearToSrgb(
            (SrgbToLinear(sourceChannel) * sourceAlpha
                + SrgbToLinear(backdropChannel) * backdropWeight) / outputAlpha);
        return Color.FromArgb(
            ToByte(outputAlpha),
            ToByte(Channel(backdrop.R, source.R)),
            ToByte(Channel(backdrop.G, source.G)),
            ToByte(Channel(backdrop.B, source.B)));
    }

    private static Color LerpLinearColor(Color start, Color end, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            Math.Clamp((int)MathF.Round(start.A + (end.A - start.A) * amount), 0, 255),
            ToByte(LinearToSrgb(Lerp(SrgbToLinear(start.R), SrgbToLinear(end.R), amount))),
            ToByte(LinearToSrgb(Lerp(SrgbToLinear(start.G), SrgbToLinear(end.G), amount))),
            ToByte(LinearToSrgb(Lerp(SrgbToLinear(start.B), SrgbToLinear(end.B), amount))));
    }

    private static bool TryGridRange(float minimum, float maximum, out long start, out long end)
    {
        var spacing = MixingBrushRegionData.MinimumVertexSpacing;
        if (!TryLong(Math.Floor(minimum / spacing), out start))
        {
            end = 0;
            return false;
        }
        return TryLong(Math.Ceiling(maximum / spacing), out end);
    }

    private static bool TryLong(double value, out long result)
    {
        if (!double.IsFinite(value) || value < long.MinValue || value > long.MaxValue)
        {
            result = 0;
            return false;
        }
        result = (long)value;
        return true;
    }

    private static PointF GridPoint(GridKey key)
    {
        var spacing = MixingBrushRegionData.MinimumVertexSpacing;
        return new PointF((float)(key.X * (double)spacing), (float)(key.Y * (double)spacing));
    }

    private static float ExponentialResponse(double travelledDistance, float rate)
    {
        if (rate <= 0f) return 0f;
        var exponent = -travelledDistance / MixingBrushRegionData.MinimumVertexSpacing * rate;
        return (float)Math.Clamp(1d - Math.Exp(exponent), 0d, 1d);
    }

    private static bool IsFinite(PointF point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static double Distance(PointF start, PointF end)
    {
        var x = (double)end.X - start.X;
        var y = (double)end.Y - start.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static PointF Lerp(PointF start, PointF end, double amount) => new(
        (float)(start.X + (end.X - (double)start.X) * amount),
        (float)(start.Y + (end.Y - (double)start.Y) * amount));

    private static float Lerp(float start, float end, float amount) =>
        start + (end - start) * amount;

    private static float SrgbToLinear(byte channel)
        => SrgbToLinearLookup[channel];

    private static float[] CreateSrgbToLinearLookup()
    {
        var lookup = new float[256];
        for (var channel = 0; channel < lookup.Length; channel++)
        {
            var value = channel / 255f;
            lookup[channel] = value <= 0.04045f
                ? value / 12.92f
                : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
        }
        return lookup;
    }

    private static float LinearToSrgb(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.0031308f
            ? value * 12.92f
            : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
    }

    private static int ToByte(float value) =>
        Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);
}
