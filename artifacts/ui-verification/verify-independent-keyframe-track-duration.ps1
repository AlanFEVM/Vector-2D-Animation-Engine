$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

function Pump-Ui([int]$milliseconds) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $milliseconds) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 8
    }
    [Windows.Forms.Application]::DoEvents()
}

$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$editType = $assembly.GetType("VectorAnimationEngine.MainForm+TimelineEditKind", $true)
$main = [Activator]::CreateInstance($mainType, $true)
$bitmap = $null

try {
    $main.ShowInTaskbar = $false
    $main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $main.Location = [Drawing.Point]::new(40, 40)
    $main.Size = [Drawing.Size]::new(1320, 820)
    $main.Show()
    Pump-Ui 420

    $project = $mainType.GetField("_project", $flags).GetValue($main)
    $drawingObject = $project.GetType().GetProperty("DrawingObjects").GetValue($project)[0]
    $scene = $drawingObject.GetType().GetProperty("Scene", $flags).GetValue($drawingObject)
    $scene.AddLayer("Independent B") | Out-Null
    $scene.AddLayer("Independent C") | Out-Null
    $scene.AddObject(
        0,
        [Drawing.PointF]::Empty,
        [Drawing.SizeF]::new(24, 24),
        0,
        0,
        [Drawing.Color]::Teal,
        4,
        [Enum]::Parse($assembly.GetType("VectorAnimationEngine.ShapeKind", $true), "Rectangle")) | Out-Null

    $mainType.GetMethod("BindActiveDrawingObjectScene", $flags).Invoke($main, @($false))
    $timelineStrip = $mainType.GetField("_timeline", $flags).GetValue($main)
    $timeline = $drawingObject.GetType().GetProperty("Timeline", $flags).GetValue($drawingObject)
    $tracks = @($timeline.GetType().GetProperty("Tracks", $flags).GetValue($timeline))
    $timeline.SetTrackDuration($tracks[0].Id, 23) | Out-Null
    $timeline.SetTrackDuration($tracks[1].Id, 7) | Out-Null
    $timeline.SetTrackDuration($tracks[2].Id, 23) | Out-Null
    $timelineStrip.RefreshTimeline()
    $mainType.GetMethod("ApplyBoundTimelineDuration", $flags).Invoke($main, @([int]0))
    $insertKeyframe = [Enum]::Parse($editType, "InsertKeyframe")
    $insertBlankKeyframe = [Enum]::Parse($editType, "InsertBlankKeyframe")

    $timelineStrip.ActiveTrackIndex = 1
    $mainType.GetMethod("SetFrame", $flags).Invoke($main, @([int]3, $true))
    $heldBlankChanged = $mainType.GetMethod("ExecuteTimelineEdit", $flags).Invoke(
        $main,
        [object[]] @($insertBlankKeyframe, $null))
    Pump-Ui 120
    $frame = $mainType.GetField("_frame", $flags).GetValue($main)
    $blankAt3 = @($tracks[1].Keyframes | Where-Object { $_.Frame -eq 3 -and [string]$_.Kind -eq "Blank" }).Count -eq 1
    if (-not $heldBlankChanged -or $frame -ne 3 -or -not $blankAt3) {
        throw "F7 on a held frame did not insert at the playhead: changed=$heldBlankChanged frame=$frame blank=$blankAt3."
    }
    if (-not $mainType.GetMethod("UndoLastEdit", $flags).Invoke($main, @())) {
        throw "F7 held-frame insertion did not create an undo entry."
    }

    $tracks = @($timeline.GetType().GetProperty("Tracks", $flags).GetValue($timeline))
    $timelineStrip.ActiveTrackIndex = 0
    $mainType.GetMethod("SetFrame", $flags).Invoke($main, @([int]0, $true))
    $keyBlankChanged = $mainType.GetMethod("ExecuteTimelineEdit", $flags).Invoke(
        $main,
        [object[]] @($insertBlankKeyframe, $null))
    Pump-Ui 120
    $frame = $mainType.GetField("_frame", $flags).GetValue($main)
    $blankAt1 = @($tracks[0].Keyframes | Where-Object { $_.Frame -eq 1 -and [string]$_.Kind -eq "Blank" }).Count -eq 1
    if (-not $keyBlankChanged -or $frame -ne 1 -or -not $blankAt1) {
        throw "F7 on a keyframe did not advance exactly one frame: changed=$keyBlankChanged frame=$frame blank=$blankAt1."
    }
    if (-not $mainType.GetMethod("UndoLastEdit", $flags).Invoke($main, @())) {
        throw "F7 keyframe advance did not create an undo entry."
    }

    $tracks = @($timeline.GetType().GetProperty("Tracks", $flags).GetValue($timeline))
    $timelineStrip.SelectSingleFrame($tracks[0].Id, 53)
    $preInsertFrame = $mainType.GetField("_frame", $flags).GetValue($main)
    if ($preInsertFrame -ne 22) {
        throw "The beyond-end selection fixture did not keep its playhead at the previous track end: frame=$preInsertFrame."
    }

    $changed = $mainType.GetMethod("ExecuteTimelineEdit", $flags).Invoke(
        $main,
        [object[]] @($insertKeyframe, $null))
    Pump-Ui 220

    $frame = $mainType.GetField("_frame", $flags).GetValue($main)
    $keyAt53 = @($tracks[0].Keyframes | Where-Object { $_.Frame -eq 53 -and [string]$_.Kind -eq "Populated" }).Count -eq 1
    if (-not $changed -or
        $frame -ne 53 -or
        -not $keyAt53 -or
        $tracks[0].Duration -ne 54 -or
        $tracks[1].Duration -ne 7 -or
        $tracks[2].Duration -ne 23) {
        throw "F6 ignored the selected frame beyond the track end: changed=$changed frame=$frame key=$keyAt53 durations=$($tracks[0].Duration),$($tracks[1].Duration),$($tracks[2].Duration)."
    }

    if (-not $mainType.GetMethod("UndoLastEdit", $flags).Invoke($main, @())) {
        throw "F6 beyond the track end did not create an undo entry."
    }
    $tracks = @($timeline.GetType().GetProperty("Tracks", $flags).GetValue($timeline))
    $timelineStrip.SelectSingleFrame($tracks[1].Id, 53)
    $blankChanged = $mainType.GetMethod("ExecuteTimelineEdit", $flags).Invoke(
        $main,
        [object[]] @($insertBlankKeyframe, $null))
    Pump-Ui 220

    $frame = $mainType.GetField("_frame", $flags).GetValue($main)
    $blankAt53 = @($tracks[1].Keyframes | Where-Object { $_.Frame -eq 53 -and [string]$_.Kind -eq "Blank" }).Count -eq 1
    if (-not $blankChanged -or
        $frame -ne 53 -or
        -not $blankAt53 -or
        $tracks[0].Duration -ne 23 -or
        $tracks[1].Duration -ne 54 -or
        $tracks[2].Duration -ne 23) {
        throw "F7 ignored the selected frame beyond the track end: changed=$blankChanged frame=$frame blank=$blankAt53 durations=$($tracks[0].Duration),$($tracks[1].Duration),$($tracks[2].Duration)."
    }

    $bitmap = [Drawing.Bitmap]::new($timelineStrip.Width, $timelineStrip.Height)
    $timelineStrip.DrawToBitmap(
        $bitmap,
        [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
    $output = Join-Path $root "artifacts\ui-verification\timeline-independent-keyframe-track-duration.png"
    $bitmap.Save($output, [Drawing.Imaging.ImageFormat]::Png)

    "keyframe_beyond_end_frame=53"
    "keyframe_beyond_end_duration=54"
    "blank_keyframe_held_target_frame=3"
    "blank_keyframe_key_target_frame=1"
    "blank_keyframe_beyond_end_frame=53"
    "blank_keyframe_beyond_end_duration=$($tracks[1].Duration)"
    "keyframe_unrelated_durations=$($tracks[0].Duration),$($tracks[2].Duration)"
    "keyframe_independent_duration_image=$output"
}
finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
