param(
    [int]$Layers = 120,
    [int]$Objects = 30000,
    [int]$Samples = 6
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

function Median([double[]]$values) {
    $ordered = @($values | Sort-Object)
    if ($ordered.Count % 2 -eq 1) { return $ordered[[int]($ordered.Count / 2)] }
    return ($ordered[$ordered.Count / 2 - 1] + $ordered[$ordered.Count / 2]) * 0.5
}

function Pump-Ui {
    [Windows.Forms.Application]::DoEvents()
    [Windows.Forms.Application]::DoEvents()
}

$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$editType = $assembly.GetType("VectorAnimationEngine.MainForm+TimelineEditKind", $true)
$main = [Activator]::CreateInstance($mainType, $true)

try {
    $main.ShowInTaskbar = $false
    $main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $main.Location = [Drawing.Point]::new(-30000, -30000)
    $main.Size = [Drawing.Size]::new(1320, 820)
    $main.Show()
    Pump-Ui

    $project = $mainType.GetField("_project", $flags).GetValue($main)
    $drawingObject = $project.GetType().GetProperty("DrawingObjects").GetValue($project)[0]
    $scene = $drawingObject.GetType().GetProperty("Scene", $flags).GetValue($drawingObject)
    $scene.Generate($Layers, $Objects, [long]$Objects * 120)
    $mainType.GetMethod("BindActiveDrawingObjectScene", $flags).Invoke($main, @($false))
    $timelineStrip = $mainType.GetField("_timeline", $flags).GetValue($main)
    $insertKeyframe = [Enum]::Parse($editType, "InsertKeyframe")
    $execute = $mainType.GetMethod("ExecuteTimelineEdit", $flags)
    $undo = $mainType.GetMethod("UndoLastEdit", $flags)
    Pump-Ui

    $snapshotWatch = [Diagnostics.Stopwatch]::StartNew()
    $snapshot = $scene.CreateSnapshot()
    $snapshotWatch.Stop()
    [GC]::KeepAlive($snapshot)

    $modelClone = [Activator]::CreateInstance($scene.GetType(), $true)
    $modelClone.RestoreSnapshot($scene.CreateSnapshot())
    $modelTrack = $modelClone.Timeline.FindTrackByTargetId($modelClone.LayerIds[0])
    $modelWatch = [Diagnostics.Stopwatch]::StartNew()
    $modelChanged = $modelClone.InsertTimelineKeyframe(0, 100)
    $modelWatch.Stop()
    if (-not $modelChanged) { throw "The model keyframe performance fixture did not insert its keyframe." }

    $times = [Collections.Generic.List[double]]::new()
    for ($sample = 0; $sample -lt $Samples; $sample++) {
        $track = $scene.Timeline.FindTrackByTargetId($scene.LayerIds[0])
        $timelineStrip.SelectSingleFrame($track.Id, 100)
        Pump-Ui
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $changed = $execute.Invoke($main, [object[]] @($insertKeyframe, $null))
        $watch.Stop()
        if (-not $changed) { throw "F6 performance sample $sample did not insert a keyframe." }
        $times.Add($watch.Elapsed.TotalMilliseconds)
        if (-not $undo.Invoke($main, @())) { throw "F6 performance sample $sample could not be undone." }
        Pump-Ui
    }

    $ordered = @($times | Sort-Object)
    "timeline_keyframe_layers=$Layers"
    "timeline_keyframe_objects=$Objects"
    "timeline_keyframe_snapshot_ms=$($snapshotWatch.Elapsed.TotalMilliseconds.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "timeline_keyframe_model_ms=$($modelWatch.Elapsed.TotalMilliseconds.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "timeline_keyframe_ui_median_ms=$((Median $times.ToArray()).ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "timeline_keyframe_ui_p95_ms=$($ordered[[Math]::Min($ordered.Count - 1, [int][Math]::Floor($ordered.Count * 0.95))].ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
}
finally {
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
