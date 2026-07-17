param(
    [int]$PointerSamples = 2000,
    [int]$EndpointSamples = 240,
    [int]$NearbyLines = 1000
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
}
finally {
    if ($null -ne $stage) { $stage.remove_Invalidated($invalidatedHandler) }
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
