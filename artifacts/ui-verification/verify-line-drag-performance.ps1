param(
    [int]$PointerSamples = 2000,
    [int]$EndpointSamples = 240,
    [int]$NearbyLines = 1000,
    [int]$ConnectedLines = 16
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$main = [Activator]::CreateInstance($mainType, $true)
$invalidations = [Collections.Generic.List[Drawing.Rectangle]]::new()
$invalidatedHandler = [Windows.Forms.InvalidateEventHandler] {
    param($sender, $eventArgs)
    $invalidations.Add($eventArgs.InvalidRect)
}

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
    $line = $scene.AddLineSegment(
        0,
        [Drawing.PointF]::new(-50, 0),
        [Drawing.PointF]::new(50, 0),
        [single]16,
        [Drawing.Color]::Transparent,
        [Drawing.Color]::White,
        [uint32]4)

    $mainType.GetMethod("SetSelection", $flags, $null, [Type[]] @([int]), $null).Invoke($main, @($line))
    $mainType.GetMethod("CaptureEditStart", $flags).Invoke($main, @($line))
    $start = [Drawing.PointF]::new($scene.X[$line], $scene.Y[$line])
    $mainType.GetField("_startWorld", $flags).SetValue($main, [Nullable[Drawing.PointF]]$start)

    $queue = $mainType.GetMethod("QueueMoveSelectedFromPointer", $flags)
    $flush = $mainType.GetMethod("FlushLineDragPreview", $flags)
    $stage.add_Invalidated($invalidatedHandler)
    $invalidations.Clear()

    $first = [Drawing.PointF]::new($start.X + 1, $start.Y + 1)
    $queue.Invoke($main, @($first))
    if ([Math]::Abs($scene.X[$line] - ($start.X + 1)) -gt 0.001 -or
        [Math]::Abs($scene.Y[$line] - ($start.Y + 1)) -gt 0.001) {
        throw "The first line drag sample was not applied immediately."
    }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    for ($index = 2; $index -le $PointerSamples; $index++) {
        $queue.Invoke($main, @([Drawing.PointF]::new($start.X + $index, $start.Y + $index)))
    }
    $watch.Stop()

    if ([Math]::Abs($scene.X[$line] - ($start.X + 1)) -gt 0.001 -or
        [Math]::Abs($scene.Y[$line] - ($start.Y + 1)) -gt 0.001) {
        throw "Queued line drag samples were applied before the preview tick."
    }

    $flush.Invoke($main, @())
    if ([Math]::Abs($scene.X[$line] - ($start.X + $PointerSamples)) -gt 0.001 -or
        [Math]::Abs($scene.Y[$line] - ($start.Y + $PointerSamples)) -gt 0.001) {
        throw "The final queued line drag position was not committed."
    }

    if ($invalidations.Count -gt 2) {
        throw "Line drag produced $($invalidations.Count) invalidations for $PointerSamples pointer samples."
    }

    $timer = $mainType.GetField("_lineDragPreviewTimer", $flags).GetValue($main)
    if ($timer.Enabled) {
        throw "The line drag preview timer remained enabled after flushing."
    }

    "line_drag_pointer_samples=$PointerSamples"
    "line_drag_queued_elapsed_ms=$($watch.Elapsed.TotalMilliseconds.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture))"
    "line_drag_stage_invalidations=$($invalidations.Count)"
    "line_drag_final_position=$($scene.X[$line]),$($scene.Y[$line])"

    $mainType.GetMethod("FinishPointerInteraction", $flags).Invoke($main, @())
    $scene.CreateEmpty(1, 24)
    $stage.SetVisibleWorldWidth([single]2600)
    $center = $stage.ScreenToWorld([Drawing.Point]::new([int]($stage.ClientSize.Width / 2), [int]($stage.ClientSize.Height / 2)))
    $endpointLine = $scene.AddLineSegment(
        0,
        [Drawing.PointF]::new($center.X - 400, $center.Y),
        [Drawing.PointF]::new($center.X, $center.Y),
        [single]16,
        [Drawing.Color]::Transparent,
        [Drawing.Color]::White,
        [uint32]4)
    for ($index = 0; $index -lt $NearbyLines; $index++) {
        $column = $index % 40
        $row = [Math]::Floor($index / 40)
        $x = $center.X - 2400 + $column * 120
        $y = $center.Y - 1500 + $row * 120
        $scene.AddLineSegment(
            0,
            [Drawing.PointF]::new($x, $y),
            [Drawing.PointF]::new($x + 50, $y + 30),
            [single]12,
            [Drawing.Color]::Transparent,
            [Drawing.Color]::Silver,
            [uint32]4) | Out-Null
    }
    $scene.GetType().GetMethod("CompleteDeferredBuild", $flags).Invoke($scene, @())

    $settings = $mainType.GetField("_drawSettings", $flags).GetValue($main)
    $settings.SnapEnabled = $true
    $settings.SnapToObjects = $true
    $settings.SnapToGrid = $false
    $editHandleType = $assembly.GetType("VectorAnimationEngine.EditHandleKind", $true)
    $mainType.GetField("_activeHandle", $flags).SetValue($main, [Enum]::Parse($editHandleType, "LineEnd"))
    $mainType.GetMethod("SetSelection", $flags, $null, [Type[]] @([int]), $null).Invoke($main, @($endpointLine))
    $mainType.GetMethod("CaptureEditStart", $flags).Invoke($main, @($endpointLine))
    $endpointArguments = [object[]] @($endpointLine, $false, [Drawing.PointF]::Empty)
    if (-not $scene.GetType().GetMethod("TryGetLineEndpoint").Invoke($scene, $endpointArguments)) {
        throw "The endpoint drag fixture could not read the line endpoint."
    }
    $endpointStart = [Drawing.PointF]$endpointArguments[2]
    $mainType.GetField("_startWorld", $flags).SetValue($main, [Nullable[Drawing.PointF]]$endpointStart)
    $snapBuckets = $mainType.GetField("_lineEndpointSnapBuckets", $flags).GetValue($main)
    if ($snapBuckets.Count -eq 0) {
        throw "The endpoint drag session did not build its snap candidate cache."
    }

    $tickPreview = $mainType.GetMethod("TickLineDragPreview", $flags)
    $endpointWatch = [Diagnostics.Stopwatch]::StartNew()
    for ($index = 0; $index -lt $EndpointSamples; $index++) {
        $angle = $index * 0.09
        $pointer = [Drawing.PointF]::new(
            $center.X + 500 + [Math]::Cos($angle) * 300,
            $center.Y + [Math]::Sin($angle) * 300)
        $queue.Invoke($main, @($pointer))
        $tickPreview.Invoke($main, @())
    }
    $flush.Invoke($main, @())
    $endpointWatch.Stop()

    $endpointArguments[2] = [Drawing.PointF]::Empty
    if (-not $scene.GetType().GetMethod("TryGetLineEndpoint").Invoke($scene, $endpointArguments)) {
        throw "The endpoint drag result could not be read."
    }
    $endpointResult = [Drawing.PointF]$endpointArguments[2]
    if ([Math]::Abs($endpointResult.X - $endpointStart.X) -lt 0.001 -and
        [Math]::Abs($endpointResult.Y - $endpointStart.Y) -lt 0.001) {
        throw "The cached endpoint drag did not update the selected endpoint."
    }

    $endpointAverageMilliseconds = $endpointWatch.Elapsed.TotalMilliseconds / $EndpointSamples
    if ($endpointAverageMilliseconds -gt 4) {
        throw "Cached endpoint drag exceeded its 4 ms update budget: $($endpointAverageMilliseconds.ToString('0.000')) ms."
    }

    "endpoint_drag_samples=$EndpointSamples"
    "endpoint_drag_nearby_lines=$NearbyLines"
    "endpoint_drag_snap_buckets=$($snapBuckets.Count)"
    "endpoint_drag_avg_ms=$($endpointAverageMilliseconds.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "endpoint_drag_budget_met=True"

    $mainType.GetMethod("FinishPointerInteraction", $flags).Invoke($main, @())
    $scene.CreateEmpty(1, 24)
    $primaryLine = $scene.AddLineSegment(
        0,
        [Drawing.PointF]::new($center.X - 500, $center.Y),
        $center,
        [single]16,
        [Drawing.Color]::Transparent,
        [Drawing.Color]::White,
        [uint32]4)
    for ($index = 1; $index -lt $ConnectedLines; $index++) {
        $angle = -[Math]::PI / 2 + [Math]::PI * ($index - 1) / [Math]::Max(1, $ConnectedLines - 2)
        $outer = [Drawing.PointF]::new(
            $center.X + [Math]::Cos($angle) * 500,
            $center.Y + [Math]::Sin($angle) * 500)
        $scene.AddLineSegment(
            0,
            $outer,
            $center,
            [single]16,
            [Drawing.Color]::Transparent,
            [Drawing.Color]::White,
            [uint32]4) | Out-Null
    }
    for ($index = 0; $index -lt $NearbyLines; $index++) {
        $column = $index % 40
        $row = [Math]::Floor($index / 40)
        $x = $center.X - 1000 + $column * 45
        $y = $center.Y + 300 + $row * 45
        $scene.AddLineSegment(
            0,
            [Drawing.PointF]::new($x, $y),
            [Drawing.PointF]::new($x + 50, $y + 30),
            [single]12,
            [Drawing.Color]::Transparent,
            [Drawing.Color]::Silver,
            [uint32]4) | Out-Null
    }
    $scene.GetType().GetMethod("CompleteDeferredBuild", $flags).Invoke($scene, @())

    $primaryHit = $scene.HitTestElement(
        [Drawing.PointF]::new($center.X - 250, $center.Y),
        0,
        [single]20)
    if (-not $primaryHit.IsValid -or $primaryHit.Key.ObjectIndex -ne $primaryLine) {
        throw "The connected endpoint fixture could not hit its primary line."
    }

    $drawingElementHitType = $assembly.GetType("VectorAnimationEngine.DrawingElementHit", $true)
    $mainType.GetMethod("SetSelection", $flags, $null, [Type[]] @($drawingElementHitType), $null).Invoke($main, @($primaryHit))
    $mainType.GetField("_activeHandle", $flags).SetValue($main, [Enum]::Parse($editHandleType, "LineEnd"))
    $mainType.GetField("_startWorld", $flags).SetValue($main, [Nullable[Drawing.PointF]]$center)

    $captureWatch = [Diagnostics.Stopwatch]::StartNew()
    $mainType.GetMethod("CaptureEditStart", $flags).Invoke($main, @($primaryLine))
    $captureWatch.Stop()
    $connectedEdits = $mainType.GetField("_lineEndpointEditStarts", $flags).GetValue($main).Count
    if ($connectedEdits -ne $ConnectedLines) {
        throw "The connected endpoint fixture captured $connectedEdits of $ConnectedLines line endpoints."
    }

    $firstConnectedDragWatch = [Diagnostics.Stopwatch]::StartNew()
    $queue.Invoke($main, @([Drawing.PointF]::new($center.X + 100, $center.Y + 75)))
    $firstConnectedDragWatch.Stop()
    $connectedContinuousWatch = [Diagnostics.Stopwatch]::StartNew()
    for ($index = 1; $index -le 60; $index++) {
        $queue.Invoke($main, @([Drawing.PointF]::new($center.X + 100 + $index, $center.Y + 75 + $index)))
        $tickPreview.Invoke($main, @())
    }
    $connectedContinuousWatch.Stop()
    $flush.Invoke($main, @())

    $expectedConnectedEndpoint = [Drawing.PointF]::new($center.X + 160, $center.Y + 135)
    for ($index = 0; $index -lt $ConnectedLines; $index++) {
        $connectedEndpointArguments = [object[]] @($index, $false, [Drawing.PointF]::Empty)
        if (-not $scene.GetType().GetMethod("TryGetLineEndpoint").Invoke($scene, $connectedEndpointArguments)) {
            throw "The connected endpoint result could not read line $index."
        }
        $connectedEndpoint = [Drawing.PointF]$connectedEndpointArguments[2]
        if ([Math]::Abs($connectedEndpoint.X - $expectedConnectedEndpoint.X) -gt 0.001 -or
            [Math]::Abs($connectedEndpoint.Y - $expectedConnectedEndpoint.Y) -gt 0.001) {
            throw "Connected line $index did not follow the shared endpoint drag."
        }
    }

    if ($firstConnectedDragWatch.Elapsed.TotalMilliseconds -gt 8) {
        throw "Connected endpoint first drag exceeded its 8 ms update budget: $($firstConnectedDragWatch.Elapsed.TotalMilliseconds.ToString('0.000')) ms."
    }
    $connectedContinuousAverageMilliseconds = $connectedContinuousWatch.Elapsed.TotalMilliseconds / 60
    if ($connectedContinuousAverageMilliseconds -gt 4) {
        throw "Connected endpoint continuous drag exceeded its 4 ms update budget: $($connectedContinuousAverageMilliseconds.ToString('0.000')) ms."
    }

    $endpointStyleType = $assembly.GetType("VectorAnimationEngine.LineEndpointStyle", $true)
    $sharpEndpointStyle = [Enum]::Parse($endpointStyleType, "Sharp")
    for ($index = 0; $index -lt $ConnectedLines; $index++) {
        $scene.SetLineEndpointStyle($index, $false, $sharpEndpointStyle) | Out-Null
    }
    $connectedPaintWatch = [Diagnostics.Stopwatch]::StartNew()
    for ($index = 1; $index -le 60; $index++) {
        $queue.Invoke($main, @([Drawing.PointF]::new($center.X + 160 + $index, $center.Y + 135 + $index)))
        $tickPreview.Invoke($main, @())
        $stage.Refresh()
    }
    $connectedPaintWatch.Stop()
    $connectedPaintAverageMilliseconds = $connectedPaintWatch.Elapsed.TotalMilliseconds / 60
    $lineGeometryCacheBuilds = $stage.GetType().GetProperty("LastDirect2DLineGeometryCacheBuilds", $flags).GetValue($stage)
    $lineGeometryCacheReuses = $stage.GetType().GetProperty("LastDirect2DLineGeometryCacheReuses", $flags).GetValue($stage)
    if (-not $stage.LastFrameUsedDirect2D) {
        throw "The connected endpoint paint benchmark did not use Direct2D."
    }
    if ($connectedPaintAverageMilliseconds -gt 10) {
        throw "Connected endpoint painted frames exceeded their 10 ms budget: $($connectedPaintAverageMilliseconds.ToString('0.000')) ms."
    }
    if ($lineGeometryCacheBuilds -ne 0) {
        throw "Straight connected lines unexpectedly rebuilt $lineGeometryCacheBuilds Direct2D path geometries."
    }

    "connected_endpoint_lines=$ConnectedLines"
    "connected_endpoint_static_render_lines=$NearbyLines"
    "connected_endpoint_capture_ms=$($captureWatch.Elapsed.TotalMilliseconds.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "connected_endpoint_first_drag_ms=$($firstConnectedDragWatch.Elapsed.TotalMilliseconds.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "connected_endpoint_continuous_avg_ms=$($connectedContinuousAverageMilliseconds.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "connected_endpoint_painted_frame_avg_ms=$($connectedPaintAverageMilliseconds.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "connected_endpoint_painted_direct2d=$($stage.LastFrameUsedDirect2D)"
    "connected_endpoint_line_geometry_builds=$lineGeometryCacheBuilds"
    "connected_endpoint_line_geometry_reuses=$lineGeometryCacheReuses"
    "connected_endpoint_painted_frame_budget_met=True"
    "connected_endpoint_first_drag_budget_met=True"
    "connected_endpoint_continuous_drag_budget_met=True"
}
finally {
    if ($null -ne $stage) { $stage.remove_Invalidated($invalidatedHandler) }
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
