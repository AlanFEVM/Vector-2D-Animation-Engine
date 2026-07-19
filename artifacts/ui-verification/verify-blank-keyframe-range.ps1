$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$cellType = $assembly.GetType("VectorAnimationEngine.TimelineFrameCell", $true)
$editType = $assembly.GetType("VectorAnimationEngine.MainForm+TimelineEditKind", $true)
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
    $scene.CreateEmpty(1, 12)
    $scene.AddLineSegment(
        0,
        [Drawing.PointF]::new(-100, 0),
        [Drawing.PointF]::new(100, 0),
        [single]20,
        [Drawing.Color]::Transparent,
        [Drawing.Color]::White,
        [uint32]6) | Out-Null
    if (-not $scene.InsertTimelineKeyframe(0, 3) -or -not $scene.InsertTimelineKeyframe(0, 4)) {
        throw "The blank-keyframe range fixture could not create independent populated Cels."
    }
    if ($scene.ObjectCount -ne 3) {
        throw "The blank-keyframe range fixture did not clone one object into each populated Cel."
    }

    $track = $scene.Timeline.FindTrackByTargetId($scene.LayerIds[0])
    $cells = [Array]::CreateInstance($cellType, 3)
    $cellConstructor = $cellType.GetConstructor(
        $flags,
        $null,
        [Type[]] @([string], [int]),
        $null)
    for ($index = 0; $index -lt 3; $index++) {
        $cell = $cellConstructor.Invoke([object[]] @($track.Id, (3 + $index)))
        $cells.SetValue($cell, $index)
    }

    $edit = [Enum]::Parse($editType, "InsertBlankKeyframe")
    $changed = $mainType.GetMethod("ExecuteTimelineEdit", $flags).Invoke($main, @($edit, $cells))
    if (-not $changed) {
        throw "The multi-frame blank-keyframe command reported no change."
    }

    $keyframes = @($track.Keyframes)
    if ($keyframes.Count -ne 2 -or
        $keyframes[0].Frame -ne 0 -or $keyframes[0].Kind.ToString() -ne "Populated" -or
        $keyframes[1].Frame -ne 5 -or $keyframes[1].Kind.ToString() -ne "Blank") {
        $description = ($keyframes | ForEach-Object { "$($_.Frame):$($_.Kind)" }) -join ","
        throw "The selected range did not collapse to held frames plus one ending blank keyframe: $description"
    }
    if ($scene.ObjectCount -ne 1) {
        throw "Clearing the first n-1 selected keyframes did not remove their independently owned Cel objects."
    }

    $frame3 = $track.EvaluateExposure(3)
    $frame4 = $track.EvaluateExposure(4)
    $frame5 = $track.EvaluateExposure(5)
    if ($frame3.IsKeyframe -or $frame4.IsKeyframe -or
        $frame3.SourceKeyframeFrame -ne 0 -or $frame4.SourceKeyframeFrame -ne 0 -or
        -not $frame3.HasContent -or -not $frame4.HasContent -or
        -not $frame5.IsKeyframe -or $frame5.HasContent) {
        throw "The selected range did not evaluate as n-1 held frames followed by one blank keyframe."
    }

    "blank_keyframe_range=3-5"
    "blank_keyframe_extension_frames=3,4"
    "blank_keyframe_created_at=5"
    "blank_keyframe_remaining_objects=$($scene.ObjectCount)"
}
finally {
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
