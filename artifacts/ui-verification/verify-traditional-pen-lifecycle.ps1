$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$stageType = $assembly.GetType("VectorAnimationEngine.StageControl", $true)
$toolType = $assembly.GetType("VectorAnimationEngine.ToolMode", $true)
$main = [Activator]::CreateInstance($mainType, $true)

function Pen-MouseEvent([Drawing.Point]$point) {
    return [Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Left, 1, $point.X, $point.Y, 0)
}

function Begin-PenPoint([Drawing.Point]$point) {
    $mainType.GetMethod("BeginTraditionalPenPoint", $flags).Invoke($main, @($point))
}

function End-PenPoint($stage, [Drawing.Point]$point) {
    $mainType.GetMethod("StageMouseUp", $flags).Invoke($main, @($stage, (Pen-MouseEvent $point)))
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
    $mainType.GetMethod("ActivateTool", $flags).Invoke($main, @([Enum]::Parse($toolType, "Pen")))

    $first = [Drawing.Point]::new(360, 320)
    $second = [Drawing.Point]::new(520, 320)
    $third = [Drawing.Point]::new(650, 390)
    $thirdHandle = [Drawing.Point]::new(720, 300)

    Begin-PenPoint $first
    End-PenPoint $stage $first
    if ($scene.ObjectCount -ne 0) {
        throw "The first traditional Pen anchor committed geometry before a segment existed."
    }

    Begin-PenPoint $second
    End-PenPoint $stage $second
    if ($scene.ObjectCount -ne 1) {
        throw "Traditional Pen click-click did not commit exactly one straight segment."
    }
    $selectedObjects = $stageType.GetProperty("SelectedObjects", $flags).GetValue($stage)
    $selectionAnimating = $stageType.GetProperty("SelectionHighlightAnimating", $flags).GetValue($stage)
    if ($selectedObjects.Count -ne 0 -or $selectionAnimating) {
        throw "Traditional Pen segment commit left the pulsing selection highlight active during drawing."
    }

    Begin-PenPoint $third
    $mainType.GetMethod("UpdateTraditionalPenHandles", $flags).Invoke($main, @($thirdHandle))
    $previewProperty = $stageType.GetProperty("DrawingPreviewCurveSegments", $flags)
    $previewBefore = $previewProperty.GetValue($stage)
    if ($previewBefore.Count -ne 1) {
        throw "Traditional Pen preview did not expose one cubic Bezier segment."
    }
    $beforeFirstControl = $previewBefore[0].Control1
    $beforeSecondControl = $previewBefore[0].Control2
    $perturbedHandle = [Drawing.Point]::new($thirdHandle.X + 1, $thirdHandle.Y)
    $mainType.GetMethod("UpdateTraditionalPenHandles", $flags).Invoke($main, @($perturbedHandle))
    $previewAfter = $previewProperty.GetValue($stage)
    $afterFirstControl = $previewAfter[0].Control1
    $afterSecondControl = $previewAfter[0].Control2
    $beforeFirstScreen = $stage.WorldToScreen([single]$beforeFirstControl.X, [single]$beforeFirstControl.Y)
    $beforeSecondScreen = $stage.WorldToScreen([single]$beforeSecondControl.X, [single]$beforeSecondControl.Y)
    $afterFirstScreen = $stage.WorldToScreen([single]$afterFirstControl.X, [single]$afterFirstControl.Y)
    $afterSecondScreen = $stage.WorldToScreen([single]$afterSecondControl.X, [single]$afterSecondControl.Y)
    $firstControlDelta = [Math]::Abs($afterFirstScreen.X - $beforeFirstScreen.X) + [Math]::Abs($afterFirstScreen.Y - $beforeFirstScreen.Y)
    $secondControlDelta = [Math]::Abs($afterSecondScreen.X - $beforeSecondScreen.X) + [Math]::Abs($afterSecondScreen.Y - $beforeSecondScreen.Y)
    if ($firstControlDelta -gt 3 -or $secondControlDelta -gt 3) {
        throw "A one-pixel handle movement caused a discontinuous traditional Pen preview jump: $firstControlDelta/$secondControlDelta px."
    }
    End-PenPoint $stage $perturbedHandle
    if ($scene.ObjectCount -ne 2) {
        throw "Traditional Pen drag did not commit exactly one cubic segment."
    }
    $storedControl1 = [Drawing.PointF]::new($scene.CurveControlX[1], $scene.CurveControlY[1])
    $storedControl2 = [Drawing.PointF]::new($scene.CurveControl2X[1], $scene.CurveControl2Y[1])
    if (-not [single]::IsFinite($storedControl1.X) -or -not [single]::IsFinite($storedControl1.Y) -or
        -not [single]::IsFinite($storedControl2.X) -or -not [single]::IsFinite($storedControl2.Y)) {
        throw "Traditional Pen cubic controls were not committed as finite independent points."
    }

    Begin-PenPoint $first
    End-PenPoint $stage $first
    $currentAnchor = $mainType.GetField("_traditionalPenCurrentAnchor", $flags).GetValue($main)
    if ($null -ne $currentAnchor -or $stage.Capture) {
        throw "Clicking the first traditional Pen anchor did not close and clear the path session."
    }

    $pathStart = [Drawing.PointF]::new(0, 0)
    $pathEnd = [Drawing.PointF]::new(0, 0)
    $pathStartArgs = [object[]] @(0, $true, $pathStart)
    $pathEndArgs = [object[]] @(0, $false, $pathEnd)
    $scene.GetType().GetMethod("TryGetLineEndpoint", $flags).Invoke($scene, $pathStartArgs) | Out-Null
    $scene.GetType().GetMethod("TryGetLineEndpoint", $flags).Invoke($scene, $pathEndArgs) | Out-Null
    $pathStart = $pathStartArgs[2]
    $pathEnd = $pathEndArgs[2]
    $pathMidpoint = [Drawing.PointF]::new(($pathStart.X + $pathEnd.X) * 0.5, ($pathStart.Y + $pathEnd.Y) * 0.5)
    $pathScreen = $stage.WorldToScreen([single]$pathMidpoint.X, [single]$pathMidpoint.Y)
    $pathPoint = [Drawing.Point]::new([int][Math]::Round($pathScreen.X), [int][Math]::Round($pathScreen.Y))
    $mainType.GetMethod("StageMouseDown", $flags).Invoke($main, @($stage, (Pen-MouseEvent $pathPoint)))
    $selectedElements = $stageType.GetProperty("SelectedElements", $flags).GetValue($stage)
    $selectedPathObjects = $stageType.GetProperty("SelectedObjects", $flags).GetValue($stage)
    $pathHandlesVisible = $stageType.GetProperty("PenPathHandlesVisible", $flags).GetValue($stage)
    $pathSelected = $selectedPathObjects.Count -gt 0
    if (-not $pathSelected -or $selectedPathObjects.Count -ne $scene.ObjectCount -or -not $pathHandlesVisible) {
        throw "Traditional Pen one-click selection did not expose the complete connected path and all editable handles: selected=$pathSelected, selectedObjects=$($selectedPathObjects.Count), objects=$($scene.ObjectCount), elements=$($selectedElements.Count), handles=$pathHandlesVisible."
    }

    $editObject = 1
    $pathObjectCount = $scene.ObjectCount
    $controlBefore = [Drawing.PointF]::new($scene.CurveControlX[$editObject], $scene.CurveControlY[$editObject])
    $oppositeControlBefore = [Drawing.PointF]::new($scene.CurveControl2X[$editObject], $scene.CurveControl2Y[$editObject])
    $controlScreen = $stage.WorldToScreen([single]$controlBefore.X, [single]$controlBefore.Y)
    $controlPoint = [Drawing.Point]::new([int][Math]::Round($controlScreen.X), [int][Math]::Round($controlScreen.Y))
    $editStarted = $mainType.GetMethod("TryBeginTraditionalPenSelectedHandleEdit", $flags).Invoke($main, @($controlPoint))
    if (-not $editStarted) {
        throw "Traditional Pen direct path editing did not hit an existing curve control point."
    }
    $selectedElementsAfterEditStart = $stageType.GetProperty("SelectedElements", $flags).GetValue($stage)
    if ($selectedElementsAfterEditStart.Count -ne $selectedElements.Count) {
        throw "Starting a Traditional Pen handle edit discarded the connected path selection."
    }
    $controlTarget = [Drawing.Point]::new($controlPoint.X + 36, $controlPoint.Y - 24)
    $mainType.GetMethod("StageMouseMove", $flags).Invoke($main, @($stage, (Pen-MouseEvent $controlTarget)))
    End-PenPoint $stage $controlTarget
    $controlAfter = [Drawing.PointF]::new($scene.CurveControlX[$editObject], $scene.CurveControlY[$editObject])
    $oppositeControlAfter = [Drawing.PointF]::new($scene.CurveControl2X[$editObject], $scene.CurveControl2Y[$editObject])
    if ($scene.ObjectCount -ne $pathObjectCount) {
        throw "Traditional Pen handle editing materialized selected topology parts instead of preserving the original line objects."
    }
    if ([Math]::Abs($controlAfter.X - $controlBefore.X) -lt 1 -and [Math]::Abs($controlAfter.Y - $controlBefore.Y) -lt 1) {
        throw "Traditional Pen direct editing selected but did not move the curve control point."
    }
    if ([Math]::Abs($oppositeControlAfter.X - $oppositeControlBefore.X) -gt 0.01 -or
        [Math]::Abs($oppositeControlAfter.Y - $oppositeControlBefore.Y) -gt 0.01) {
        throw "Moving the first cubic control also changed the second control."
    }

    $control2Screen = $stage.WorldToScreen([single]$oppositeControlAfter.X, [single]$oppositeControlAfter.Y)
    $control2Point = [Drawing.Point]::new([int][Math]::Round($control2Screen.X), [int][Math]::Round($control2Screen.Y))
    $control2EditStarted = $mainType.GetMethod("TryBeginTraditionalPenSelectedHandleEdit", $flags).Invoke($main, @($control2Point))
    if (-not $control2EditStarted) {
        throw "Traditional Pen direct path editing did not hit the second cubic control point."
    }
    $control1BeforeSecondEdit = [Drawing.PointF]::new($scene.CurveControlX[$editObject], $scene.CurveControlY[$editObject])
    $control2Target = [Drawing.Point]::new($control2Point.X - 28, $control2Point.Y + 22)
    $mainType.GetMethod("StageMouseMove", $flags).Invoke($main, @($stage, (Pen-MouseEvent $control2Target)))
    End-PenPoint $stage $control2Target
    $control1AfterSecondEdit = [Drawing.PointF]::new($scene.CurveControlX[$editObject], $scene.CurveControlY[$editObject])
    $control2After = [Drawing.PointF]::new($scene.CurveControl2X[$editObject], $scene.CurveControl2Y[$editObject])
    if ([Math]::Abs($control2After.X - $oppositeControlAfter.X) -lt 1 -and
        [Math]::Abs($control2After.Y - $oppositeControlAfter.Y) -lt 1) {
        throw "Traditional Pen direct editing selected but did not move the second cubic control point."
    }
    if ([Math]::Abs($control1AfterSecondEdit.X - $control1BeforeSecondEdit.X) -gt 0.01 -or
        [Math]::Abs($control1AfterSecondEdit.Y - $control1BeforeSecondEdit.Y) -gt 0.01) {
        throw "Moving the second cubic control also changed the first control."
    }

    $sharedBefore = [Drawing.PointF]::new(0, 0)
    $neighborBefore = [Drawing.PointF]::new(0, 0)
    $sharedBeforeArgs = [object[]] @(0, $false, $sharedBefore)
    $neighborBeforeArgs = [object[]] @(1, $true, $neighborBefore)
    $scene.GetType().GetMethod("TryGetLineEndpoint", $flags).Invoke($scene, $sharedBeforeArgs) | Out-Null
    $scene.GetType().GetMethod("TryGetLineEndpoint", $flags).Invoke($scene, $neighborBeforeArgs) | Out-Null
    $sharedBefore = $sharedBeforeArgs[2]
    $neighborBefore = $neighborBeforeArgs[2]
    $sharedScreen = $stage.WorldToScreen([single]$sharedBefore.X, [single]$sharedBefore.Y)
    $sharedPoint = [Drawing.Point]::new([int][Math]::Round($sharedScreen.X), [int][Math]::Round($sharedScreen.Y))
    $endpointEditStarted = $mainType.GetMethod("TryBeginTraditionalPenSelectedHandleEdit", $flags).Invoke($main, @($sharedPoint))
    if (-not $endpointEditStarted) {
        throw "Traditional Pen direct path editing did not hit a selected shared endpoint."
    }
    $sharedTarget = [Drawing.Point]::new($sharedPoint.X + 24, $sharedPoint.Y + 18)
    $mainType.GetMethod("StageMouseMove", $flags).Invoke($main, @($stage, (Pen-MouseEvent $sharedTarget)))
    End-PenPoint $stage $sharedTarget
    $sharedAfterArgs = [object[]] @(0, $false, [Drawing.PointF]::Empty)
    $neighborAfterArgs = [object[]] @(1, $true, [Drawing.PointF]::Empty)
    $scene.GetType().GetMethod("TryGetLineEndpoint", $flags).Invoke($scene, $sharedAfterArgs) | Out-Null
    $scene.GetType().GetMethod("TryGetLineEndpoint", $flags).Invoke($scene, $neighborAfterArgs) | Out-Null
    $sharedAfter = $sharedAfterArgs[2]
    $neighborAfter = $neighborAfterArgs[2]
    $sharedMove = [Math]::Abs($sharedAfter.X - $sharedBefore.X) + [Math]::Abs($sharedAfter.Y - $sharedBefore.Y)
    $sharedGap = [Math]::Abs($sharedAfter.X - $neighborAfter.X) + [Math]::Abs($sharedAfter.Y - $neighborAfter.Y)
    if ($sharedMove -lt 1 -or $sharedGap -gt 1 -or $scene.ObjectCount -ne $pathObjectCount) {
        throw "Traditional Pen direct endpoint editing did not preserve and move the shared path anchor: move=$sharedMove, gap=$sharedGap."
    }

    "traditional_pen_first_anchor=ok"
    "traditional_pen_straight_segment=ok"
    "traditional_pen_cubic_segment=ok"
    "traditional_pen_preview_stability=ok"
    "traditional_pen_selection_pulse_suppressed=ok"
    "traditional_pen_close_path=ok"
    "traditional_pen_path_selection=ok"
    "traditional_pen_all_handles=ok"
    "traditional_pen_control_edit=ok"
    "traditional_pen_independent_controls=ok"
    "traditional_pen_shared_endpoint_edit=ok"
    "traditional_pen_objects=$($scene.ObjectCount)"
}
finally {
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
