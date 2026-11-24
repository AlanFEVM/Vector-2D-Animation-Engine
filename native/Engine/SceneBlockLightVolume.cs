namespace VectorAnimationEngine;

// High is retained for saved scenes whose bitmap shading used the original profile.
internal enum SceneBlockLightQuality { Low, Medium, High, HighDetailed }

// Only the top admits sky; the cut faces do not create an extra air passage.
internal sealed class SceneBlockLightVolume
{
    internal const int MaximumLevel = 15;
    internal const int MaximumCells = 8 * 1024 * 1024;
    internal const int Reach = MaximumLevel - 1;
    internal const int HighFaceSampleReach = 2;
    internal const int DetailedOcclusionReach = 2;
    private readonly int _width, _height, _depth, _slice;
    private readonly SceneBlockLightQuality _quality;
    private readonly bool _nightVision;
    private readonly byte[] _materials, _sky, _block;
    private float _cutLight = -1f;
    private readonly List<int>[] _levels = Enumerable.Range(0, 16).Select(_ => new List<int>()).ToArray();

    private readonly record struct Box(int Left, int Top, int Near, int Right, int Bottom, int Far);
    internal readonly record struct Face(uint Shadows, uint Warmth);

    internal SceneBlockLightVolume(int width, int height, int depth,
        SceneBlockLightQuality quality = SceneBlockLightQuality.Low, bool nightVision = false)
    {
        if (!CanFit(width, height, depth)) throw new InvalidDataException("Invalid block light volume.");
        (_width, _height, _depth, _slice) = (width, height, depth, width * height);
        _quality = quality;
        _nightVision = nightVision;
        _materials = new byte[_slice * depth];
        _sky = new byte[_materials.Length];
        _block = new byte[_materials.Length];
    }

    internal static bool CanFit(int width, int height, int depth) => width > 0 && height > 0 && depth > 0
        && (long)width * height <= MaximumCells / depth;

    private int Index(SceneBlockGrid.Cell cell) => (cell.Depth * _height + cell.Y) * _width + cell.X;
    private bool Contains(SceneBlockGrid.Cell cell) => (uint)cell.X < _width && (uint)cell.Y < _height && (uint)cell.Depth < _depth;
    private bool Opaque(SceneBlockGrid.Cell cell) => Contains(cell) && Opaque(_materials[Index(cell)]);
    private static bool Opaque(byte material) => (material & 128) != 0;
    private static int Filter(byte material) => material >> 4 & 7;
    private static int Emission(byte material) => material & 15;

    internal void Set(SceneBlockGrid.Cell cell, bool opaque, int filter, int emission, int point)
        => _materials[Index(cell)] = (byte)((opaque ? 128 : filter << 4) | Math.Max(emission, opaque ? 0 : point));

    internal int Sky(SceneBlockGrid.Cell cell) => Contains(cell) ? _sky[Index(cell)] : cell.Y < 0 ? MaximumLevel : 0;
    internal int Block(SceneBlockGrid.Cell cell) => Contains(cell) ? _block[Index(cell)] : 0;

    internal void Build(CancellationToken token = default)
    {
        var box = new Box(0, 0, 0, _width - 1, _height - 1, _depth - 1);
        Solve(box, sunlight: true, token);
        Solve(box, sunlight: false, token);
    }

    internal void Relight(SceneBlockGrid.Cell cell, bool opaque, int filter, int emission, int point)
    {
        _cutLight = -1f;
        var index = Index(cell);
        var old = _materials[index];
        Set(cell, opaque, filter, emission, point);
        var next = _materials[index];
        if (old == next) return;
        // Direct sky can change all the way down the column. All other paths
        // lose at least one level per step; values outside these boxes are invariant.
        var left = Math.Max(0, cell.X - Reach);
        var right = Math.Min(_width - 1, cell.X + Reach);
        var near = Math.Max(0, cell.Depth - Reach);
        var far = Math.Min(_depth - 1, cell.Depth + Reach);
        if ((old & 240) != (next & 240)) Solve(new(left, 0, near, right, _height - 1, far), sunlight: true);
        Solve(new(left, Math.Max(0, cell.Y - Reach), near, right, Math.Min(_height - 1, cell.Y + Reach), far), sunlight: false);
    }

    private void Solve(Box box, bool sunlight, CancellationToken token = default)
    {
        var light = sunlight ? _sky : _block;
        foreach (var level in _levels) level.Clear();
        for (var z = box.Near; z <= box.Far; z++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = box.Left; x <= box.Right; x++)
            {
                var direct = MaximumLevel;
                for (var y = box.Top; y <= box.Bottom; y++)
                {
                    var index = (z * _height + y) * _width + x;
                    var material = _materials[index];
                    var opaque = Opaque(material);
                    var loss = Math.Max(1, Filter(material));
                    if (sunlight && (opaque || direct != MaximumLevel || Filter(material) != 0))
                        direct = opaque ? 0 : Math.Max(0, direct - loss);
                    var level = sunlight ? direct : Emission(material);
                    if (!opaque)
                    {
                        // Fixed outside values are valid even during a light-removal edit.
                        if (x == box.Left && x > 0) level = Math.Max(level, light[index - 1] - loss);
                        if (x == box.Right && x + 1 < _width) level = Math.Max(level, light[index + 1] - loss);
                        if (y == box.Top && y > 0) level = Math.Max(level, light[index - _width] - loss);
                        if (y == box.Bottom && y + 1 < _height) level = Math.Max(level, light[index + _width] - loss);
                        if (z == box.Near && z > 0) level = Math.Max(level, light[index - _slice] - loss);
                        if (z == box.Far && z + 1 < _depth) level = Math.Max(level, light[index + _slice] - loss);
                    }
                    light[index] = (byte)level;
                    if (level > 1) _levels[level].Add(index);
                }
            }
        }

        for (var level = MaximumLevel; level > 1; level--)
        {
            token.ThrowIfCancellationRequested();
            foreach (var index in _levels[level])
            {
                if (light[index] != level) continue;
                var x = index % _width;
                var y = index / _width % _height;
                var z = index / _slice;
                if (x > box.Left) Spread(index - 1, level);
                if (x < box.Right) Spread(index + 1, level);
                if (y > box.Top) Spread(index - _width, level);
                if (y < box.Bottom) Spread(index + _width, level);
                if (z > box.Near) Spread(index - _slice, level);
                if (z < box.Far) Spread(index + _slice, level);
            }
            _levels[level].Clear();
            if (_levels[level].Capacity > 262_144) _levels[level].Capacity = 0;
        }

        void Spread(int index, int from)
        {
            var candidate = from - Math.Max(1, Filter(_materials[index]));
            if (light[index] >= candidate || Opaque(_materials[index])) return;
            light[index] = (byte)candidate;
            if (candidate > 1) _levels[candidate].Add(index);
        }
    }

    internal Face Shade(SceneBlockGrid.Cell cell)
    {
        uint shadows = 0, warmth = 0;
        var opaque = Opaque(cell);
        // A cell is a covered glass face only when the cell directly in front of it is
        // solid (no air gap). Hiding that cover reveals this cell, so give it the global
        // glass-face brightness instead of its own local sample. Cells separated from the
        // glass by air, and the frontmost depth layer, keep their own lighting.
        var buried = opaque && cell.Depth > 0 && CoveredInFront(cell);
        var cutFace = opaque && (cell.Depth == 0 || buried);
        var detailed = _quality == SceneBlockLightQuality.HighDetailed;
        var plane = opaque && cell.Depth > 0 ? cell.Depth - 1 : cell.Depth;
        var p = new SceneBlockGrid.Cell(cell.X, cell.Y, plane);
        var ownEmission = Emission(_materials[Index(cell)]);
        var centerSky = Sky(p);
        var centerBlock = Math.Max(Block(p), ownEmission);
        // Approximate the missing sample in front of the section using exposed edges.
        // Buried cells reuse the frontmost plane so hiding their covering layer reveals
        // a lit cross-section instead of the dark sample taken inside the solid body.
        if (cutFace)
        {
            var face = new SceneBlockGrid.Cell(p.X, p.Y, 0);
            centerSky = Math.Max(Sky(face with { Y = face.Y - 1 }),
                Math.Max(Sky(face with { X = face.X - 1 }), Sky(face with { X = face.X + 1 })));
            centerBlock = Math.Max(centerBlock, Math.Max(Block(face with { Y = face.Y - 1 }),
                Math.Max(Block(face with { X = face.X - 1 }), Block(face with { X = face.X + 1 }))));
        }

        for (var corner = 0; corner < 4; corner++)
        {
            var dx = (corner & 1) == 0 ? -1 : 1;
            var dy = corner < 2 ? -1 : 1;
            var side = p with { X = p.X + dx };
            var vertical = p with { Y = p.Y + dy };
            var diagonal = new SceneBlockGrid.Cell(side.X, vertical.Y, plane);
            var coveredSide = Opaque(side);
            var coveredVertical = Opaque(vertical);
            var coveredDiagonal = Opaque(diagonal);
            var sky = (float)centerSky;
            var block = (float)centerBlock;
            var weight = 1;
            Sample(side, coveredSide);
            Sample(vertical, coveredVertical);
            if (!coveredSide || !coveredVertical) Sample(diagonal, coveredDiagonal);
            sky /= weight;
            block = Math.Max(ownEmission, block / weight);
            if (cutFace && (_quality == SceneBlockLightQuality.High || detailed))
                sky = SampleCutVertex(cell.X + (corner & 1), cell.Y + (corner >> 1));
            var level = Math.Max(sky, block);
            var light = LightFor(level, cutFace);
            // A buried cell only becomes visible once its cover layer is hidden. Give it the
            // global glass-face brightness instead of its own local sample, so the revealed
            // layer is flat. Cells already exposed in a deep layer keep their own lighting.
            if (buried)
                light = Math.Max(ComputeCutLight(), LightFor(block, true));
            var ao = !opaque || cutFace ? 0 : coveredSide && coveredVertical ? .25f
                : (coveredSide ? .09f : 0) + (coveredVertical ? .09f : 0) + (coveredDiagonal ? .06f : 0);
            if (detailed && opaque && !cutFace)
                ao = DetailedOcclusion(p, dx, dy, coveredSide, coveredVertical, coveredDiagonal);
            var dark = (int)MathF.Round(255 * (1 - light * (1 - ao)));
            var warm = (int)MathF.Round(52 * block / MaximumLevel * Math.Max(0, 1 - sky / MaximumLevel));
            shadows |= (uint)dark << (corner * 8);
            warmth |= (uint)warm << (corner * 8);

            void Sample(SceneBlockGrid.Cell sample, bool covered)
            {
                if (covered || (uint)sample.X >= _width || sample.Y >= _height) return;
                sky += Sky(sample);
                block += Block(sample);
                weight++;
            }
        }
        return new(shadows, warmth);
    }

    // True only when the cell directly in front of this one is solid. Any air gap between
    // this cell and the glass face means it is not treated as a covered glass face.
    private bool CoveredInFront(SceneBlockGrid.Cell cell)
        => Opaque(cell with { Depth = cell.Depth - 1 });

    private float LightFor(float level, bool cutFace)
    {
        var floor = _quality switch
        {
            SceneBlockLightQuality.Medium => cutFace ? .32f : .24f,
            SceneBlockLightQuality.High => cutFace ? .55f : .29f,
            SceneBlockLightQuality.HighDetailed => cutFace ? .55f : .46f,
            _ => .14f
        };
        var response = level / (60f - 3f * level);
        if (_quality is SceneBlockLightQuality.High or SceneBlockLightQuality.HighDetailed)
            response = .55f * response + .45f * level / MaximumLevel;
        if (_nightVision) floor = Math.Max(floor, .55f);
        return !_nightVision && _quality == SceneBlockLightQuality.Low
            ? .14f + .86f * level / (60f - 3f * level)
            : floor + (1f - floor) * response;
    }

    // One global cut brightness for every glass face, averaged over the lower half of the
    // visible cross-section itself (opaque glass-face cells only, never the sky or air
    // above the terrain). Buried cells reuse it so a revealed layer is flat instead of
    // ramping from bright at the surface to black below.
    private float ComputeCutLight()
    {
        if (_cutLight >= 0f) return _cutLight;
        var sum = 0f;
        var count = 0;
        var lower = _height / 2;
        for (var y = lower; y < _height; y++)
            for (var x = 0; x < _width; x++)
            {
                if (!Opaque(new SceneBlockGrid.Cell(x, y, 0))) continue;
                var sky = Math.Max(Sky(new SceneBlockGrid.Cell(x, y - 1, 0)),
                    Math.Max(Sky(new SceneBlockGrid.Cell(x - 1, y, 0)), Sky(new SceneBlockGrid.Cell(x + 1, y, 0))));
                sum += LightFor(sky, true);
                count++;
            }
        _cutLight = count == 0 ? LightFor(0f, true) : sum / count;
        return _cutLight;
    }

    private float DetailedOcclusion(SceneBlockGrid.Cell front, int dx, int dy, bool side, bool vertical, bool diagonal)
    {
        // Strong contact shadows at the shared edges, with a softer contribution
        // from nearby ledges closer to the camera. Multiple hits on the same
        // sight line count once, so a thick wall cannot accumulate black bands.
        var contact = side && vertical ? .34f
            : (side ? .15f : 0) + (vertical ? .17f : 0) + (diagonal ? .10f : 0);
        var blockedRays = 0;
        for (var ray = 0; ray < 3; ray++)
        {
            var ox = ray == 1 ? 0 : dx;
            var oy = ray == 0 ? 0 : dy;
            var sample = new SceneBlockGrid.Cell(front.X + ox * DetailedOcclusionReach,
                front.Y + oy * DetailedOcclusionReach, front.Depth - 1);
            if (Opaque(sample) || Opaque(sample with { Depth = front.Depth - 2 }))
                blockedRays++;
        }
        return contact + (1f - contact) * blockedRays * .06f;
    }

    // Adjacent cut tiles share the same sky sample at each vertex. This is display
    // fill for exposed cross-sections, never a light source in the playable volume.
    private float SampleCutVertex(int vertexX, int vertexY)
    {
        var sum = 0;
        var weight = 0;
        for (var dy = -HighFaceSampleReach; dy < HighFaceSampleReach; dy++)
            for (var dx = -HighFaceSampleReach; dx < HighFaceSampleReach; dx++)
            {
                var x = vertexX + dx;
                var y = vertexY + dy;
                if ((uint)x >= _width || y >= _height) continue;
                var w = (dx is -1 or 0 ? 2 : 1) * (dy is -1 or 0 ? 2 : 1);
                sum += w * Sky(new(x, y, 0));
                weight += w;
            }
        return (float)sum / weight;
    }
}
