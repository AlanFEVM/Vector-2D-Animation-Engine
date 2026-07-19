param([int]$Samples = 48)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$shapeKindType = $assembly.GetType("VectorAnimationEngine.ShapeKind", $true)
$rectangle = [Enum]::Parse($shapeKindType, "Rectangle")
$main = [Activator]::CreateInstance($mainType, $true)
$timings = @{}

function Measure-Part([string]$name, [scriptblock]$action) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $action
    $watch.Stop()
    Add-Timing $name $watch.Elapsed.TotalMilliseconds
}

function Add-Timing([string]$name, [double]$milliseconds) {
    if (-not $timings.ContainsKey($name)) { $null = $timings[$name] = [Collections.Generic.List[double]]::new() }
    $timings[$name].Add($milliseconds)
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
    $hierarchy = $mainType.GetField("_hierarchyPanel", $flags).GetValue($main)
    $scene.CreateEmpty(1, 24)
    for ($index = 0; $index -lt 1000; $index++) {
        $scene.AddObject(
            0,
            [Drawing.PointF]::new(-12000 + ($index % 40) * 180, -9000 + [Math]::Floor($index / 40) * 180),
            [Drawing.SizeF]::new(70, 50),
            [single]0,
            [single]0,
            [Drawing.Color]::DarkSlateGray,
            [Drawing.Color]::Transparent,
            [uint32]6,
            $rectangle) | Out-Null
    }
    $scene.GetType().GetMethod("CompleteDeferredBuild", $flags).Invoke($scene, @())
    $hierarchy.RefreshScene()
    $mainType.GetMethod("UpdateInspector", $flags).Invoke($main, @())
    [Windows.Forms.Application]::DoEvents()

    $captureUndo = $mainType.GetMethod("CaptureUndoSnapshot", $flags)
    $setSelection = $mainType.GetMethod("SetSelection", $flags, $null, [Type[]]@([int]), $null)
    $localScope = $mainType.GetMethod("LocalLineMergeScope", $flags)
    $mergeLines = $mainType.GetMethod("MergeCompatibleLinesAfterDrawingOperation", $flags)
    $normalizePaint = $mainType.GetMethod("NormalizeNewPaintObjects", $flags)
    $updateInspector = $mainType.GetMethod("UpdateInspector", $flags)

    for ($index = 0; $index -lt $Samples; $index++) {
        Measure-Part "line_snapshot" { $captureUndo.Invoke($main, @()) } | Out-Null
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $line = $scene.AddLineSegment(
            0,
            [Drawing.PointF]::new(-600, 1000 + $index * 12),
            [Drawing.PointF]::new(600, 1000 + $index * 12),
            [single]24,
            [Drawing.Color]::Transparent,
            [Drawing.Color]::White,
            [uint32]6)
        $watch.Stop()
        Add-Timing "line_append" $watch.Elapsed.TotalMilliseconds
        Measure-Part "line_select" { $setSelection.Invoke($main, @($line)) } | Out-Null
        $seed = [int[]]::new(1)
        $seed[0] = $line
        $invokeArguments = [object[]]::new(1)
        $invokeArguments[0] = $seed
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $scope = [int[]]$localScope.Invoke($main, $invokeArguments)
        $watch.Stop()
        Add-Timing "line_scope" $watch.Elapsed.TotalMilliseconds
        $invokeArguments[0] = $scope
        Measure-Part "line_merge" { $mergeLines.Invoke($main, $invokeArguments) } | Out-Null
        Measure-Part "line_hierarchy" { $hierarchy.RefreshScene() } | Out-Null
        Measure-Part "line_inspector" { $updateInspector.Invoke($main, @()) } | Out-Null
    }

    for ($index = 0; $index -lt $Samples; $index++) {
        Measure-Part "shape_snapshot" { $captureUndo.Invoke($main, @()) } | Out-Null
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $shape = $scene.AddObject(
            0,
            [Drawing.PointF]::new(-160 + $index * 12, 0),
            [Drawing.SizeF]::new(320, 240),
            [single]0,
            [single]0,
            [Drawing.Color]::Teal,
            [Drawing.Color]::Transparent,
            [uint32]24,
            $rectangle)
        $watch.Stop()
        Add-Timing "shape_append" $watch.Elapsed.TotalMilliseconds
        $seed = [int[]]::new(1)
        $seed[0] = $shape
        $invokeArguments = [object[]]::new(1)
        $invokeArguments[0] = $seed
        Measure-Part "shape_normalize" { $normalizePaint.Invoke($main, $invokeArguments) } | Out-Null
        Measure-Part "shape_select" { $setSelection.Invoke($main, @($shape)) } | Out-Null
        Measure-Part "shape_hierarchy" { $hierarchy.RefreshScene() } | Out-Null
        Measure-Part "shape_inspector" { $updateInspector.Invoke($main, @()) } | Out-Null
    }

    foreach ($name in $timings.Keys | Sort-Object) {
        $values = $timings[$name].ToArray()
        [Array]::Sort($values)
        $p95 = $values[[Math]::Min($values.Length - 1, [int][Math]::Ceiling($values.Length * 0.95) - 1)]
        "$($name)_median_ms=$($values[[int][Math]::Floor(($values.Length - 1) * 0.5)].ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
        "$($name)_p95_ms=$($p95.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
        "$($name)_max_ms=$($values[-1].ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    }
}
finally {
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
