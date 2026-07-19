$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Core" -or [Environment]::Version.Major -lt 8) {
    throw "Run this script with PowerShell 7 on .NET 8: pwsh -STA -File $PSCommandPath"
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class SpacePanKeyboardInput
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
[Windows.Forms.Application]::EnableVisualStyles()

$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$toolType = $assembly.GetType("VectorAnimationEngine.ToolMode", $true)
$main = [Activator]::CreateInstance($mainType, $true)

function Read-Field([string]$name) {
    return $mainType.GetField($name, $flags).GetValue($main)
}

function Mouse-Event([Drawing.Point]$point) {
    return [Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Left, 1, $point.X, $point.Y, 0)
}

function Find-FocusableButton([Windows.Forms.Control]$control) {
    foreach ($child in $control.Controls) {
        if ($child -is [Windows.Forms.ButtonBase] -and $child.CanFocus) {
            return $child
        }
        $button = Find-FocusableButton $child
        if ($null -ne $button) { return $button }
    }
    return $null
}

try {
    $main.ShowInTaskbar = $false
    $main.Opacity = 0.01
    $main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $main.Location = [Drawing.Point]::new(40, 40)
    $main.Size = [Drawing.Size]::new(1100, 720)
    $main.Show()
    [Windows.Forms.Application]::DoEvents()

    $stage = Read-Field "_stage"
    [SpacePanKeyboardInput]::SetForegroundWindow($main.Handle) | Out-Null
    $focusedButton = Find-FocusableButton $main
    if ($null -eq $focusedButton -or -not $focusedButton.Focus()) {
        throw "The keyboard routing fixture could not retain focus on a tool button."
    }
    [Windows.Forms.Cursor]::Position = $stage.PointToScreen(
        [Drawing.Point]::new([Math]::Max(1, $stage.Width / 2), [Math]::Max(1, $stage.Height / 2)))
    $spaceKeyDownSent = $false
    try {
        [SpacePanKeyboardInput]::keybd_event(0x20, 0, 0, [UIntPtr]::Zero)
        $spaceKeyDownSent = $true
        [Windows.Forms.Application]::DoEvents()
        if (-not (Read-Field "_spacePanHeld")) {
            throw "A real Space key-down was rejected while a tool button retained focus and the pointer was over the stage."
        }
    }
    finally {
        if ($spaceKeyDownSent) {
            [SpacePanKeyboardInput]::keybd_event(0x20, 0, 2, [UIntPtr]::Zero)
            [Windows.Forms.Application]::DoEvents()
        }
    }
    if ((Read-Field "_spacePanHeld") -or (Read-Field "_spacePanKeyDown")) {
        throw "A real Space key-up did not release temporary canvas pan."
    }

    $stage.Focus() | Out-Null
    [Windows.Forms.Application]::DoEvents()
    $mainType.GetMethod("ActivateTool", $flags).Invoke($main, @([Enum]::Parse($toolType, "Rectangle")))
    $toolBefore = Read-Field "_tool"
    $originBefore = $stage.WorldToScreen([single]0, [single]0)

    $mainType.GetMethod("OnKeyDown", $flags).Invoke(
        $main,
        @([Windows.Forms.KeyEventArgs]::new([Windows.Forms.Keys]::Space)))
    if (-not (Read-Field "_spacePanHeld") -or $stage.Cursor -ne [Windows.Forms.Cursors]::Hand) {
        throw "Holding Space did not enter the temporary hand state."
    }

    $start = [Drawing.Point]::new(420, 300)
    $finish = [Drawing.Point]::new(467, 274)
    $mainType.GetMethod("StageMouseDown", $flags).Invoke($main, @($stage, (Mouse-Event $start)))
    if (-not (Read-Field "_spacePanPointerActive") -or $stage.Cursor -ne [Windows.Forms.Cursors]::SizeAll) {
        throw "Space plus left mouse did not begin a captured canvas pan."
    }
    $mainType.GetMethod("StageMouseMove", $flags).Invoke($main, @($stage, (Mouse-Event $finish)))
    $mainType.GetMethod("StageMouseUp", $flags).Invoke($main, @($stage, (Mouse-Event $finish)))

    $originAfter = $stage.WorldToScreen([single]0, [single]0)
    if ([Math]::Abs(($originAfter.X - $originBefore.X) - 47) -gt 0.1 -or
        [Math]::Abs(($originAfter.Y - $originBefore.Y) + 26) -gt 0.1) {
        throw "The canvas did not follow the temporary pan drag: before=$originBefore after=$originAfter"
    }
    if (-not (Read-Field "_spacePanHeld") -or (Read-Field "_spacePanPointerActive")) {
        throw "Mouse release did not preserve only the temporary Space hold state."
    }
    if ((Read-Field "_tool") -ne $toolBefore) {
        throw "Temporary canvas pan changed the active drawing tool."
    }

    $mainType.GetMethod("OnKeyUp", $flags).Invoke(
        $main,
        @([Windows.Forms.KeyEventArgs]::new([Windows.Forms.Keys]::Space)))
    if ((Read-Field "_spacePanKeyDown") -or (Read-Field "_spacePanHeld") -or (Read-Field "_spacePanPointerActive")) {
        throw "Releasing Space did not clear the temporary pan state."
    }
    if ($stage.Cursor -eq [Windows.Forms.Cursors]::Hand -or $stage.Cursor -eq [Windows.Forms.Cursors]::SizeAll) {
        throw "Releasing Space did not restore the active tool cursor."
    }

    $mainType.GetMethod("OnKeyDown", $flags).Invoke(
        $main,
        @([Windows.Forms.KeyEventArgs]::new([Windows.Forms.Keys]::Space)))
    $message = [Windows.Forms.Message]::Create($main.Handle, 0, [IntPtr]::Zero, [IntPtr]::Zero)
    $commandArgs = [object[]] @($message, [Windows.Forms.Keys]::Escape)
    $escapeHandled = $mainType.GetMethod("ProcessCmdKey", $flags).Invoke($main, $commandArgs)
    if (-not $escapeHandled -or (Read-Field "_spacePanHeld") -or (Read-Field "_spacePanPointerActive")) {
        throw "Escape did not cancel the temporary canvas pan state."
    }

    "space_canvas_pan_delta=47,-26"
    "space_canvas_pan_real_keyboard_with_button_focus=ok"
    "space_canvas_pan_tool_preserved=$toolBefore"
    "space_canvas_pan_escape_cancel=ok"
    "space_canvas_pan_state_restored=ok"
}
finally {
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
