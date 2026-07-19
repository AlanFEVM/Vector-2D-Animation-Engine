param(
    [string]$DefaultOutputPath = "artifacts\ui-verification\workspace-panel-toggles-default.png",
    [string]$AnimationOutputPath = "artifacts\ui-verification\workspace-panel-toggles-animation.png",
    [string]$HiddenOutputPath = "artifacts\ui-verification\workspace-panel-toggles-hidden.png"
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Core" -or [Environment]::Version.Major -lt 8) {
    throw "Run this script with PowerShell 7 on .NET 8: pwsh -STA -File $PSCommandPath"
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class WorkspacePanelKeyboardInput
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
"@

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

function Pump-Ui([int]$frames) {
    for ($index = 0; $index -lt $frames; $index++) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 16
    }
}

function Send-FunctionKey([byte]$virtualKey) {
    $sent = $false
    try {
        [WorkspacePanelKeyboardInput]::keybd_event($virtualKey, 0, 0, [UIntPtr]::Zero)
        $sent = $true
        [Windows.Forms.Application]::DoEvents()
    }
    finally {
        if ($sent) {
            [WorkspacePanelKeyboardInput]::keybd_event($virtualKey, 0, 2, [UIntPtr]::Zero)
            [Windows.Forms.Application]::DoEvents()
        }
    }
}

function Save-ControlImage([Windows.Forms.Control]$control, [string]$relativePath) {
    $bitmap = [Drawing.Bitmap]::new($control.Width, $control.Height)
    try {
        $control.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $control.Width, $control.Height))
        $resolved = Join-Path $root $relativePath
        $bitmap.Save($resolved, [Drawing.Imaging.ImageFormat]::Png)
        return $resolved
    }
    finally {
        $bitmap.Dispose()
    }
}

$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$motionType = $assembly.GetType("VectorAnimationEngine.UiMotion", $true)
$main = [Activator]::CreateInstance($mainType, $true)
try {
    $main.ShowInTaskbar = $false
    $main.Opacity = 0.02
    $main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $main.Location = [Drawing.Point]::new(40, 40)
    $main.Size = [Drawing.Size]::new(1280, 800)
    $main.Show()
    [WorkspacePanelKeyboardInput]::SetForegroundWindow($main.Handle) | Out-Null
    Pump-Ui 8

    $inspector = $mainType.GetField("_inspectorHost", $flags).GetValue($main)
    $timeline = $mainType.GetField("_timeline", $flags).GetValue($main)
    $propertiesButton = $mainType.GetField("_propertiesPanelButton", $flags).GetValue($main)
    $timelineButton = $mainType.GetField("_timelinePanelButton", $flags).GetValue($main)
    $animationTimer = $mainType.GetField("_workspacePanelAnimationTimer", $flags).GetValue($main)
    function Set-AnimationProgress([double]$progress) {
        $duration = [double]$mainType.GetField("_workspacePanelAnimationDurationMilliseconds", $flags).GetValue($main)
        $elapsedTicks = [long][Math]::Round(
            [Diagnostics.Stopwatch]::Frequency * $duration * $progress / 1000.0)
        $mainType.GetField("_workspacePanelAnimationStartedTimestamp", $flags).SetValue(
            $main,
            [Diagnostics.Stopwatch]::GetTimestamp() - $elapsedTicks)
        $animationTimer.Stop()
        $mainType.GetMethod("TickWorkspacePanelAnimation", $flags).Invoke($main, $null)
    }
    if ($propertiesButton.Icon.ToString() -ne "PropertiesPanel" -or
        $timelineButton.Icon.ToString() -ne "TimelinePanel" -or
        $propertiesButton.Left -ge $timelineButton.Left -or
        $propertiesButton.Right -gt $timelineButton.Left) {
        throw "The title-bar panel buttons are missing, reversed, or overlapping."
    }
    if (-not $motionType.GetMethod("IsActive", $staticFlags).Invoke($null, @($propertiesButton)) -or
        -not $motionType.GetMethod("IsActive", $staticFlags).Invoke($null, @($timelineButton))) {
        throw "Visible workspace panels did not initialize their title-bar buttons as active."
    }
    $defaultScreenshot = Save-ControlImage $main $DefaultOutputPath

    Send-FunctionKey 0x70
    Pump-Ui 16
    if ($inspector.Visible -or $inspector.Width -ne 0) {
        throw "A real F1 key message did not hide the inspector panel."
    }
    Send-FunctionKey 0x70
    Pump-Ui 16
    if (-not $inspector.Visible -or $inspector.Width -ne 324) {
        throw "A real F1 key message did not restore the inspector panel."
    }

    $propertiesButton.PerformClick()
    if (-not $animationTimer.Enabled) { throw "The properties button did not start its panel animation." }
    Set-AnimationProgress 0.2
    if ($inspector.Width -le 0 -or $inspector.Width -ge 324) {
        throw "The properties button did not enter an intermediate inspector width: width=$($inspector.Width)."
    }
    $animationScreenshot = Save-ControlImage $main $AnimationOutputPath
    Set-AnimationProgress 1.0
    if ($inspector.Visible -or $inspector.Width -ne 0 -or
        $motionType.GetMethod("IsActive", $staticFlags).Invoke($null, @($propertiesButton))) {
        throw "The properties button did not finish hiding and deactivating the inspector panel."
    }
    $propertiesButton.PerformClick()
    Set-AnimationProgress 1.0
    if (-not $inspector.Visible -or $inspector.Width -ne 324) {
        throw "The properties button did not restore the inspector panel to its full width."
    }

    $timeline.Height = 236
    Pump-Ui 2
    Send-FunctionKey 0x71
    Pump-Ui 16
    if ($timeline.Visible -or $timeline.Height -ne 0) {
        $timelineOpen = $mainType.GetField("_timelinePanelOpen", $flags).GetValue($main)
        throw "A real F2 key message did not hide the timeline panel: open=$timelineOpen visible=$($timeline.Visible) height=$($timeline.Height) timer=$($animationTimer.Enabled)."
    }
    Send-FunctionKey 0x71
    Pump-Ui 16
    if (-not $timeline.Visible -or $timeline.Height -ne 236) {
        throw "A real F2 key message did not restore the user-adjusted timeline height."
    }

    $timelineButton.PerformClick()
    if (-not $animationTimer.Enabled) { throw "The timeline button did not start its panel animation." }
    Set-AnimationProgress 0.5
    if ($timeline.Height -le 0 -or $timeline.Height -ge 236) {
        throw "The timeline button did not enter an intermediate timeline height: height=$($timeline.Height)."
    }
    Set-AnimationProgress 1.0
    if ($timeline.Visible -or $timeline.Height -ne 0 -or
        $motionType.GetMethod("IsActive", $staticFlags).Invoke($null, @($timelineButton))) {
        throw "The timeline button did not finish hiding and deactivating the timeline panel."
    }
    $timelineButton.PerformClick()
    Set-AnimationProgress 1.0
    if (-not $timeline.Visible -or $timeline.Height -ne 236) {
        throw "The timeline button did not restore the user-adjusted timeline height: height=$($timeline.Height)."
    }

    $propertiesButton.PerformClick()
    $timelineButton.PerformClick()
    Set-AnimationProgress 1.0
    Pump-Ui 12
    if ($inspector.Visible -or $timeline.Visible -or $inspector.Width -ne 0 -or $timeline.Height -ne 0) {
        throw "The title-bar buttons did not hide both workspace panels."
    }
    if ($motionType.GetMethod("IsActive", $staticFlags).Invoke($null, @($propertiesButton)) -or
        $motionType.GetMethod("IsActive", $staticFlags).Invoke($null, @($timelineButton))) {
        throw "Hidden workspace panels left their title-bar buttons active."
    }
    $hiddenScreenshot = Save-ControlImage $main $HiddenOutputPath

    $renameMenu = $timeline.GetType().GetField("_renameLayerMenuItem", $flags).GetValue($timeline)
    if ($renameMenu.ShortcutKeys -ne ([Windows.Forms.Keys]::Shift -bor [Windows.Forms.Keys]::F2)) {
        throw "Timeline layer rename still conflicts with the global F2 panel shortcut."
    }

    "workspace_panel_buttons=properties,timeline"
    "workspace_panel_f1=animated-toggle-ok"
    "workspace_panel_f2=animated-toggle-ok"
    "workspace_panel_timeline_restored_height=236"
    "workspace_panel_layer_rename_shortcut=Shift+F2"
    "workspace_panel_default_screenshot=$defaultScreenshot"
    "workspace_panel_animation_screenshot=$animationScreenshot"
    "workspace_panel_hidden_screenshot=$hiddenScreenshot"
}
finally {
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
