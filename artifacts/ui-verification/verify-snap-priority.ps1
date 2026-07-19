$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
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
    $settings = $mainType.GetField("_drawSettings", $flags).GetValue($main)
    $scene.CreateEmpty(1, 24)
    $adaptiveStep = [single]$stage.GetType().GetProperty("AdaptiveGridSnapStep", $flags).GetValue($stage)
    $pointer = [Drawing.PointF]::new($adaptiveStep * 1.494, $adaptiveStep * 0.506)
    $requestedEndpoint = [Drawing.PointF]::new($pointer.X - 1, $pointer.Y + 1)
    $line = $scene.AddLineSegment(
        0,
        [Drawing.PointF]::new(-80, -40),
        $requestedEndpoint,
        [single]25,
        [Drawing.Color]::Transparent,
        [Drawing.Color]::White,
        [uint32]6)
    $endpointArguments = [object[]] @($line, $false, [Drawing.PointF]::Empty)
    $endpointMethod = $scene.GetType().GetMethod("TryGetLineEndpoint")
    if (-not $endpointMethod.Invoke($scene, $endpointArguments)) {
        throw "The Stage snapping fixture could not read its line endpoint."
    }
    $objectEndpoint = [Drawing.PointF]$endpointArguments[2]

    $settings.SnapEnabled = $true
    $settings.SnapToGrid = $true
    $settings.SnapToObjects = $true
    $settings.GridSize = [single]128
    $resolve = $mainType.GetMethod("ResolveDrawingLineEndpoint", $flags)
    $objectResult = [Drawing.PointF]$resolve.Invoke($main, @($pointer))

    $settings.SnapToObjects = $false
    $gridResult = [Drawing.PointF]$resolve.Invoke($main, @($pointer))
    if ([Math]::Abs($objectResult.X - $objectEndpoint.X) -gt 0.001 -or
        [Math]::Abs($objectResult.Y - $objectEndpoint.Y) -gt 0.001) {
        throw "Object snapping was overridden by the grid: result=$objectResult expected=$objectEndpoint"
    }
    if ([Math]::Abs($gridResult.X - $adaptiveStep) -gt 0.001 -or
        [Math]::Abs($gridResult.Y - $adaptiveStep) -gt 0.001) {
        throw "Grid fallback did not resolve to the current visible grid: result=$gridResult step=$adaptiveStep"
    }

    "object_snap_result=$($objectResult.X),$($objectResult.Y)"
    "grid_fallback_result=$($gridResult.X),$($gridResult.Y)"
    "adaptive_grid_step=$($adaptiveStep)vu"
    "snap_priority=object-before-grid"
}
finally {
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
