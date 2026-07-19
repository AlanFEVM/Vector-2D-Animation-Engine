param(
    [int]$Samples = 90,
    [double]$BudgetMilliseconds = 10
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$failures = [Collections.Generic.List[string]]::new()

function Measure-Interaction([scriptblock]$action, [int]$count) {
    $values = [double[]]::new($count)
    for ($index = 0; $index -lt $count; $index++) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        & $action $index
        $watch.Stop()
        $values[$index] = $watch.Elapsed.TotalMilliseconds
    }
    [Array]::Sort($values)
    $p95Index = [Math]::Min($values.Length - 1, [Math]::Max(0, [int][Math]::Ceiling($values.Length * 0.95) - 1))
    return [pscustomobject]@{
        Average = ($values | Measure-Object -Average).Average
        Median = $values[[int][Math]::Floor(($values.Length - 1) * 0.5)]
        P95 = $values[$p95Index]
        Maximum = $values[-1]
        OverBudget = @($values | Where-Object { $_ -gt $BudgetMilliseconds })
    }
}

function Assert-Budget([string]$name, $measurement) {
    "$($name)_avg_ms=$($measurement.Average.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "$($name)_median_ms=$($measurement.Median.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "$($name)_p95_ms=$($measurement.P95.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "$($name)_max_ms=$($measurement.Maximum.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    if ($measurement.OverBudget.Count -gt 0) {
        "$($name)_over_budget_ms=$(($measurement.OverBudget | ForEach-Object { $_.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture) }) -join ',')"
    }
    if ($measurement.P95 -gt $BudgetMilliseconds) {
        $script:failures.Add("$name exceeded the $BudgetMilliseconds ms P95 interaction budget: $($measurement.P95.ToString('0.000')) ms.")
    }
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"
$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$toolModeType = $assembly.GetType("VectorAnimationEngine.ToolMode", $true)
$brushShapeType = $assembly.GetType("VectorAnimationEngine.BrushShape", $true)
$tipKindType = $assembly.GetType("VectorAnimationEngine.TraditionalBrushTipKind", $true)
$lineTool = [Enum]::Parse($toolModeType, "Line")
$rectangleTool = [Enum]::Parse($toolModeType, "Rectangle")
$squareTip = [Enum]::Parse($tipKindType, "Square")
$stampBrush = $brushShapeType.GetMethod("CreateTraditionalBrush", $staticFlags).Invoke($null, @($squareTip, 65, 25))
$main = [Activator]::CreateInstance($mainType, $true)

try {
    $main.ShowInTaskbar = $false
    $main.Opacity = 0.01
    $main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $main.Location = [Drawing.Point]::new(-30000, -30000)
    $main.Size = [Drawing.Size]::new(1100, 720)
    $main.Show()
    [Windows.Forms.Application]::DoEvents()

    $scene = $mainType.GetField("_scene", $flags).GetValue($main)
    $stage = $mainType.GetField("_stage", $flags).GetValue($main)
    $scene.CreateEmpty(1, 24)
    for ($index = 0; $index -lt 1000; $index++) {
        $column = $index % 40
        $row = [Math]::Floor($index / 40)
        $scene.AddObject(
            0,
            [Drawing.PointF]::new(-12000 + $column * 180, -9000 + $row * 180),
            [Drawing.SizeF]::new(70, 50),
            [single]0,
            [single]0,
            [Drawing.Color]::FromArgb(255, 38, 62, 70),
            [Drawing.Color]::Transparent,
            [uint32]6,
            [Enum]::Parse($assembly.GetType("VectorAnimationEngine.ShapeKind", $true), "Rectangle")) | Out-Null
    }
    $scene.GetType().GetMethod("CompleteDeferredBuild", $flags).Invoke($scene, @())
    $hierarchy = $mainType.GetField("_hierarchyPanel", $flags).GetValue($main)
    $hierarchy.RefreshScene()
    $mainType.GetMethod("UpdateInspector", $flags).Invoke($main, @())
    [Windows.Forms.Application]::DoEvents()
    $stage.SetVisibleWorldWidth([single]2400)
    $center = $stage.ScreenToWorld([Drawing.Point]::new([int]($stage.Width / 2), [int]($stage.Height / 2)))
    $updateDrawingPreview = $mainType.GetMethod("UpdateDrawingPreview", $flags)
    $addDrawnObject = $mainType.GetMethod("AddDrawnObject", $flags)
    $setFreehandPreview = $stage.GetType().GetMethod("SetFreehandPreview", $flags)

    $updateDrawingPreview.Invoke($main, @($center, [Drawing.PointF]::new($center.X + 300, $center.Y + 120), $rectangleTool))
    $stage.Refresh()

    $shapePreview = Measure-Interaction {
        param($index)
        $end = [Drawing.PointF]::new($center.X + 260 + $index, $center.Y + 100 + [Math]::Sin($index * 0.13) * 80)
        $updateDrawingPreview.Invoke($main, @($center, $end, $rectangleTool))
        $stage.Refresh()
    } $Samples
    Assert-Budget "shape_preview_frame" $shapePreview

    $linePreview = Measure-Interaction {
        param($index)
        $end = [Drawing.PointF]::new($center.X + 320 + $index, $center.Y + [Math]::Cos($index * 0.11) * 140)
        $updateDrawingPreview.Invoke($main, @($center, $end, $lineTool))
        $stage.Refresh()
    } $Samples
    Assert-Budget "line_preview_frame" $linePreview

    $points = [Drawing.PointF[]]::new(256)
    $diameters = [single[]]::new(256)
    for ($index = 0; $index -lt $points.Length; $index++) {
        $points[$index] = [Drawing.PointF]::new(
            $center.X - 500 + $index * 4,
            $center.Y + [Math]::Sin($index * 0.09) * 180)
        $diameters[$index] = 30 + ($index % 31)
    }

    $fixedPreview = Measure-Interaction {
        param($index)
        $setFreehandPreview.Invoke($stage, [object[]]@($points, [Drawing.Color]::Coral, [single]45, $null, $null))
        $stage.Refresh()
    } $Samples
    Assert-Budget "fixed_brush_preview_frame" $fixedPreview

    $pressurePreview = Measure-Interaction {
        param($index)
        $setFreehandPreview.Invoke($stage, [object[]]@($points, [Drawing.Color]::Teal, [single]45, $diameters, $null))
        $stage.Refresh()
    } $Samples
    Assert-Budget "pressure_brush_preview_frame" $pressurePreview

    $stampPoints = [Drawing.PointF[]]::new(96)
    for ($index = 0; $index -lt $stampPoints.Length; $index++) { $stampPoints[$index] = $points[$index * 2] }
    $stampPreview = Measure-Interaction {
        param($index)
        $setFreehandPreview.Invoke($stage, [object[]]@($stampPoints, [Drawing.Color]::Gold, [single]45, $null, $stampBrush))
        $stage.Refresh()
    } $Samples
    Assert-Budget "stamp_brush_preview_frame" $stampPreview

    $lineCommit = Measure-Interaction {
        param($index)
        $start = [Drawing.PointF]::new($center.X - 300, $center.Y + $index * 8)
        $end = [Drawing.PointF]::new($center.X + 300, $center.Y + $index * 8)
        $addDrawnObject.Invoke($main, @($start, $end, $lineTool))
    } 40
    Assert-Budget "line_commit" $lineCommit

    $shapeCommit = Measure-Interaction {
        param($index)
        $start = [Drawing.PointF]::new($center.X - 160 + $index * 12, $center.Y - 120)
        $end = [Drawing.PointF]::new($center.X + 160 + $index * 12, $center.Y + 120)
        $addDrawnObject.Invoke($main, @($start, $end, $rectangleTool))
    } 40
    Assert-Budget "shape_commit" $shapeCommit

    if (-not $stage.LastFrameUsedDirect2D) { throw "Drawing interaction budget verification did not use Direct2D." }
    $objectsRoot = $hierarchy.GetType().GetField("_objectsRoot", $flags).GetValue($hierarchy)
    if (($objectsRoot.Text -ne "Objects (1080)") -or
        ($objectsRoot.Nodes.Count -ne 513) -or
        ($objectsRoot.Nodes[$objectsRoot.Nodes.Count - 1].Text -ne "+ 568 more objects")) {
        throw "Incremental hierarchy refresh did not preserve the capped object nodes and overflow count."
    }
    "drawing_interaction_budget_ms=$BudgetMilliseconds"
    "drawing_interaction_direct2d=True"
    "drawing_interaction_hierarchy_objects=1080"
    "drawing_interaction_hierarchy_nodes=513"
    if ($failures.Count -gt 0) { throw ($failures -join " ") }
}
finally {
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
