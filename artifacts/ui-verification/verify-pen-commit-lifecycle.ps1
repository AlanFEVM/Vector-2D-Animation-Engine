$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$toolType = $assembly.GetType("VectorAnimationEngine.ToolMode", $true)
$main = [Activator]::CreateInstance($mainType, $true)

function Invoke-PenDown([Drawing.Point]$point) {
    $mainType.GetMethod("BeginPenSegment", $flags).Invoke($main, @($point))
}

function Pen-MouseEvent([Windows.Forms.MouseButtons]$button, [int]$clicks, [Drawing.Point]$point) {
    return [Windows.Forms.MouseEventArgs]::new($button, $clicks, $point.X, $point.Y, 0)
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
    $mainType.GetMethod("ActivateTool", $flags).Invoke($main, @([Enum]::Parse($toolType, "SimplePen")))

    $draggingField = $mainType.GetField("_penSegmentDragging", $flags)
    $startField = $mainType.GetField("_penStartWorld", $flags)
    $endField = $mainType.GetField("_penEndWorld", $flags)
    $controlField = $mainType.GetField("_penControlWorld", $flags)
    $cancel = $mainType.GetMethod("CancelPenCurve", $flags)

    $doubleStart = [Drawing.Point]::new(300, 280)
    $doubleEnd = [Drawing.Point]::new(303, 280)
    Invoke-PenDown $doubleStart
    Invoke-PenDown $doubleEnd
    if (-not $draggingField.GetValue($main)) {
        throw "The short double-click fixture did not create a pending Simple Pen segment."
    }
    $mainType.GetMethod("StageMouseDoubleClick", $flags).Invoke(
        $main,
        @($stage, (Pen-MouseEvent ([Windows.Forms.MouseButtons]::Left) 2 $doubleEnd)))
    $mainType.GetMethod("StageMouseUp", $flags).Invoke(
        $main,
        @($stage, (Pen-MouseEvent ([Windows.Forms.MouseButtons]::Left) 1 $doubleEnd)))
    if ($scene.ObjectCount -ne 1 -or
        $null -ne $startField.GetValue($main) -or
        $null -ne $endField.GetValue($main) -or
        $draggingField.GetValue($main)) {
        throw "A valid Simple Pen segment recognized as a double-click was dropped or committed more than once."
    }

    $captureStart = [Drawing.Point]::new(420, 260)
    $captureEnd = [Drawing.Point]::new(520, 330)
    Invoke-PenDown $captureStart
    Invoke-PenDown $captureEnd
    if (-not $draggingField.GetValue($main) -or -not $stage.Capture) {
        throw "The capture-loss fixture did not begin a captured Pen drag."
    }
    $mainType.GetMethod("FinishLostPointerCapture", $flags).Invoke($main, @())
    if ($scene.ObjectCount -ne 2 -or
        $draggingField.GetValue($main) -or
        $null -eq $startField.GetValue($main)) {
        throw "Capture loss dropped the pending Pen segment or lost its continuation anchor."
    }
    $cancel.Invoke($main, @()) | Out-Null

    $curveStart = [Drawing.Point]::new(620, 250)
    $curveEnd = [Drawing.Point]::new(720, 320)
    $curveControl = [Drawing.Point]::new(760, 390)
    Invoke-PenDown $curveStart
    Invoke-PenDown $curveEnd
    $mainType.GetMethod("StageMouseUp", $flags).Invoke(
        $main,
        @($stage, (Pen-MouseEvent ([Windows.Forms.MouseButtons]::Left) 1 $curveControl)))
    if ($scene.ObjectCount -ne 3 -or $draggingField.GetValue($main)) {
        throw "A normal Simple Pen curve drag did not commit exactly one segment."
    }
    $lastObject = $scene.ObjectCount - 1
    $storedControl = [Drawing.PointF]::new($scene.CurveControlX[$lastObject], $scene.CurveControlY[$lastObject])
    $expectedControl = $stage.ScreenToWorld($curveControl)
    if ([Math]::Abs($storedControl.X - $expectedControl.X) -gt 1 -or
        [Math]::Abs($storedControl.Y - $expectedControl.Y) -gt 1) {
        throw "Simple Pen MouseUp did not preserve the final control-point position: actual=$storedControl expected=$expectedControl"
    }

    $cancelStart = [Drawing.Point]::new(360, 420)
    $cancelEnd = [Drawing.Point]::new(480, 460)
    Invoke-PenDown $cancelStart
    Invoke-PenDown $cancelEnd
    $cancel.Invoke($main, @()) | Out-Null
    if ($scene.ObjectCount -ne 3 -or $draggingField.GetValue($main) -or $stage.Capture) {
        throw "Cancelling a pending Pen drag committed geometry or retained mouse capture."
    }

    "pen_double_click_commit=ok"
    "pen_capture_loss_commit=ok"
    "pen_mouse_up_control_commit=ok"
    "pen_cancel_releases_capture=ok"
    "pen_committed_segments=$($scene.ObjectCount)"
}
finally {
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
