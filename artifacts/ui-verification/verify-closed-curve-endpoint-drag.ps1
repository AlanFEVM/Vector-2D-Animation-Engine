param(
    [int]$Samples = 48,
    [double]$BudgetMilliseconds = 10
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$shapeKindType = $assembly.GetType("VectorAnimationEngine.ShapeKind", $true)
$endpointStyleType = $assembly.GetType("VectorAnimationEngine.LineEndpointStyle", $true)
$editHandleType = $assembly.GetType("VectorAnimationEngine.EditHandleKind", $true)
$rectangle = [Enum]::Parse($shapeKindType, "Rectangle")
$round = [Enum]::Parse($endpointStyleType, "Round")
$lineEnd = [Enum]::Parse($editHandleType, "LineEnd")
$main = [Activator]::CreateInstance($mainType, $true)

try {
    $main.ShowInTaskbar = $false
    $main.Opacity = 0.01
    $main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $main.Location = [Drawing.Point]::new(-30000, -30000)
    $main.Size = [Drawing.Size]::new(1000, 680)
    $main.Show()
    [Windows.Forms.Application]::DoEvents()

    $scene = $mainType.GetField("_scene", $flags).GetValue($main)
    $stage = $mainType.GetField("_stage", $flags).GetValue($main)
    $scene.CreateEmpty(1, 24)
    $fill = $scene.AddObject(
        0,
        [Drawing.PointF]::Empty,
        [Drawing.SizeF]::new(200, 100),
        [single]0,
        [single]0,
        [Drawing.Color]::Teal,
        [Drawing.Color]::Transparent,
        [uint32]12,
        $rectangle)

    $addCubic = $scene.GetType().GetMethod("AddCubicCurveSegment", $flags)
    function Add-Curve($start, $control1, $control2, $end) {
        return $addCubic.Invoke($scene, [object[]]@(
            0,
            $start,
            $control1,
            $control2,
            $end,
            [single]8,
            [Drawing.Color]::Transparent,
            [Drawing.Color]::White,
            [uint32]12,
            $round,
            $round))
    }

    $top = Add-Curve `
        ([Drawing.PointF]::new(-100, -50)) `
        ([Drawing.PointF]::new(-45, -90)) `
        ([Drawing.PointF]::new(45, -90)) `
        ([Drawing.PointF]::new(100, -50))
    $right = Add-Curve `
        ([Drawing.PointF]::new(100, -50)) `
        ([Drawing.PointF]::new(135, -20)) `
        ([Drawing.PointF]::new(135, 20)) `
        ([Drawing.PointF]::new(100, 50))
    Add-Curve `
        ([Drawing.PointF]::new(100, 50)) `
        ([Drawing.PointF]::new(45, 82)) `
        ([Drawing.PointF]::new(-45, 82)) `
        ([Drawing.PointF]::new(-100, 50)) | Out-Null
    Add-Curve `
        ([Drawing.PointF]::new(-100, 50)) `
        ([Drawing.PointF]::new(-130, 20)) `
        ([Drawing.PointF]::new(-130, -20)) `
        ([Drawing.PointF]::new(-100, -50)) | Out-Null

    $anchor = [Drawing.PointF]::new(100, -50)
    $mainType.GetMethod("SetSelection", $flags, $null, [Type[]]@([int]), $null).Invoke($main, @($top))
    $mainType.GetField("_activeHandle", $flags).SetValue($main, $lineEnd)
    $mainType.GetField("_startWorld", $flags).SetValue($main, [Nullable[Drawing.PointF]]$anchor)
    $mainType.GetMethod("CaptureEditStart", $flags).Invoke($main, @($top))

    $connectedEdits = $mainType.GetField("_lineEndpointEditStarts", $flags).GetValue($main)
    if ($connectedEdits.Count -ne 2) {
        throw "Closed curve fixture captured $($connectedEdits.Count) endpoints instead of the shared pair."
    }

    $queue = $mainType.GetMethod("QueueMoveSelectedFromPointer", $flags)
    $tick = $mainType.GetMethod("TickLineDragPreview", $flags)
    $values = [double[]]::new($Samples)
    $last = $anchor
    for ($index = 0; $index -lt $Samples; $index++) {
        $last = [Drawing.PointF]::new(112 + $index, -62 - $index * 0.5)
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $queue.Invoke($main, @($last))
        $tick.Invoke($main, @())
        $stage.Refresh()
        $watch.Stop()
        $values[$index] = $watch.Elapsed.TotalMilliseconds
    }

    [Array]::Sort($values)
    $p95 = $values[[Math]::Min($values.Length - 1, [int][Math]::Ceiling($values.Length * 0.95) - 1)]
    if ($p95 -gt $BudgetMilliseconds) {
        throw "Closed curve endpoint drag exceeded the $BudgetMilliseconds ms P95 budget: $($p95.ToString('0.000')) ms."
    }

    $topEndpointArguments = [object[]]@($top, $false, [Drawing.PointF]::Empty)
    $rightEndpointArguments = [object[]]@($right, $true, [Drawing.PointF]::Empty)
    $tryGetEndpoint = $scene.GetType().GetMethod("TryGetLineEndpoint", $flags)
    if (-not $tryGetEndpoint.Invoke($scene, $topEndpointArguments) -or
        -not $tryGetEndpoint.Invoke($scene, $rightEndpointArguments)) {
        throw "Closed curve fixture could not read the dragged endpoints."
    }

    $pathArguments = [object[]]@($fill, $null)
    if (-not $scene.GetType().GetMethod("TryGetPathWorldContours", $flags).Invoke($scene, $pathArguments)) {
        throw "Closed curve fill was not updated to an editable path during the drag."
    }
    $contours = $pathArguments[1]
    $expected = [Drawing.PointF]::new([Math]::Round($last.X), [Math]::Round($last.Y))
    $fillFollows = @($contours | ForEach-Object { $_ }) | Where-Object {
        [Math]::Abs($_.X - $expected.X) -le 0.001 -and [Math]::Abs($_.Y - $expected.Y) -le 0.001
    }
    $topEndpoint = [Drawing.PointF]$topEndpointArguments[2]
    $rightEndpoint = [Drawing.PointF]$rightEndpointArguments[2]
    if ([Math]::Abs($topEndpoint.X - $expected.X) -gt 0.001 -or
        [Math]::Abs($topEndpoint.Y - $expected.Y) -gt 0.001 -or
        [Math]::Abs($rightEndpoint.X - $expected.X) -gt 0.001 -or
        [Math]::Abs($rightEndpoint.Y - $expected.Y) -gt 0.001 -or
        $fillFollows.Count -eq 0) {
        throw "The closed curve endpoint or linked fill did not reach the latest pointer sample: pointer=$expected, top=$topEndpoint, right=$rightEndpoint, fillMatches=$($fillFollows.Count)."
    }
    if (-not $stage.LastFrameUsedDirect2D) {
        throw "Closed curve endpoint drag verification did not use Direct2D."
    }

    "closed_curve_endpoint_samples=$Samples"
    "closed_curve_endpoint_p95_ms=$($p95.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "closed_curve_endpoint_max_ms=$($values[-1].ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "closed_curve_endpoint_fill_follows=True"
    "closed_curve_endpoint_direct2d=True"
}
finally {
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
