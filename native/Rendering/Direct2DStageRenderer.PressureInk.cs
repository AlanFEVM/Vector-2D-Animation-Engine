using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private readonly Dictionary<PathBezierNode[][], ID2D1Geometry> _pressureInkGeometry = [];
    private (PointF Origin, PointF AxisX, PointF AxisY) _pressureInkView;

    private void ClearPressureInkGeometry()
    {
        foreach (var geometry in _pressureInkGeometry.Values) geometry.Dispose();
        _pressureInkGeometry.Clear();
    }

    private bool DrawPressureInkPreview(StageControl stage)
    {
        var pieces = stage.PressureInkPieces;
        if (pieces.Count == 0)
        {
            ClearPressureInkGeometry();
            return false;
        }
        var view = (stage.WorldToScreen(0, 0), stage.WorldToScreen(1, 0), stage.WorldToScreen(0, 1));
        if (_pressureInkView != view) ClearPressureInkGeometry();
        _pressureInkView = view;
        var active = new HashSet<PathBezierNode[][]>(pieces);
        foreach (var key in _pressureInkGeometry.Keys.Where(key => !active.Contains(key)).ToArray())
        {
            _pressureInkGeometry[key].Dispose();
            _pressureInkGeometry.Remove(key);
        }
        var geometries = new ID2D1Geometry[pieces.Count];
        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            if (!_pressureInkGeometry.TryGetValue(piece, out var geometry))
            {
                var path = _factory!.CreatePathGeometry();
                try
                {
                    using var sink = path.Open();
                    sink.SetFillMode(FillMode.Winding);
                    AppendBezierFigures(sink, stage, piece);
                    sink.Close();
                }
                catch { path.Dispose(); throw; }
                geometry = path;
                _pressureInkGeometry.Add(piece, geometry);
            }
            geometries[index] = geometry;
        }
        // One winding fill across all pieces prevents alpha accumulation and
        // shared-edge antialias seams. Completed geometries are reused unchanged.
        using var group = _factory!.CreateGeometryGroup(FillMode.Winding, geometries, (uint)geometries.Length);
        _target!.FillGeometry(group, BrushFor(stage.PressureInkColor.ToArgb()));
        return true;
    }
}
