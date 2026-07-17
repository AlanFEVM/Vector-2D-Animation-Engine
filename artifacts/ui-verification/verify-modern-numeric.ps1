$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NumericInputNativeMethods
{
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
}
"@

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$numericType = $assembly.GetType("VectorAnimationEngine.ModernNumericUpDown", $true)
$numeric = [Activator]::CreateInstance($numericType, $true)
$editorField = $numericType.GetField("_editor", [Reflection.BindingFlags] "Instance,NonPublic")
$editor = [Windows.Forms.TextBox]$editorField.GetValue($numeric)
$valueProperty = $numericType.GetProperty("Value")
$originalCursor = [Windows.Forms.Cursor]::Position
$form = New-Object Windows.Forms.Form

try {
    $form.ShowInTaskbar = $false
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object Drawing.Point(40, 40)
    $form.ClientSize = New-Object Drawing.Size(220, 60)
    $form.Opacity = 0.01
    $numeric.SetBounds(10, 10, 180, 28)
    $valueProperty.SetValue($numeric, [decimal]10)
    $form.Controls.Add($numeric)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()

    $start = $editor.PointToScreen((New-Object Drawing.Point(12, 6)))
    [NumericInputNativeMethods]::SetCursorPos($start.X, $start.Y) | Out-Null
    [NumericInputNativeMethods]::SendMessage($editor.Handle, 0x0201, [IntPtr]1, [IntPtr]::Zero) | Out-Null
    [NumericInputNativeMethods]::SetCursorPos($start.X + 20, $start.Y) | Out-Null
    [NumericInputNativeMethods]::SendMessage($editor.Handle, 0x0200, [IntPtr]1, [IntPtr](12 + 20 + (6 -shl 16))) | Out-Null
    [NumericInputNativeMethods]::SendMessage($editor.Handle, 0x0202, [IntPtr]0, [IntPtr](12 + 20 + (6 -shl 16))) | Out-Null
    [Windows.Forms.Application]::DoEvents()

    $scrubValue = [decimal]$valueProperty.GetValue($numeric)
    $scrubSelectionLength = $editor.SelectionLength

    [NumericInputNativeMethods]::SetCursorPos($start.X, $start.Y) | Out-Null
    [NumericInputNativeMethods]::SendMessage($editor.Handle, 0x0201, [IntPtr]1, [IntPtr]::Zero) | Out-Null
    [NumericInputNativeMethods]::SendMessage($editor.Handle, 0x0202, [IntPtr]0, [IntPtr]::Zero) | Out-Null
    [Windows.Forms.Application]::DoEvents()
    $clickFocused = $editor.Focused
    $clickSelectedAll = $editor.SelectionLength -eq $editor.TextLength

    $editor.Text = "42"
    $editor.SelectAll()
    $keyboardTextBefore = $editor.Text
    [NumericInputNativeMethods]::SendMessage($editor.Handle, 0x0100, [IntPtr]13, [IntPtr]::Zero) | Out-Null
    [NumericInputNativeMethods]::SendMessage($editor.Handle, 0x0101, [IntPtr]13, [IntPtr]::Zero) | Out-Null
    [Windows.Forms.Application]::DoEvents()
    $keyboardTextAfter = $editor.Text
    $keyboardValue = [decimal]$valueProperty.GetValue($numeric)
    $sizeCursor = $editor.Cursor.Handle -eq [Windows.Forms.Cursors]::SizeWE.Handle

    if ($scrubValue -le 10 -or $scrubSelectionLength -ne 0) {
        throw "Horizontal scrubbing did not change the value without selecting text."
    }
    if (-not $clickFocused -or -not $clickSelectedAll -or $keyboardValue -ne 42) {
        throw "Click-to-keyboard numeric editing failed: focused=$clickFocused, selectedAll=$clickSelectedAll, before=$keyboardTextBefore, after=$keyboardTextAfter, value=$keyboardValue."
    }
    if (-not $sizeCursor) {
        throw "The numeric editor did not expose the horizontal resize cursor."
    }

    "numeric_scrub_value=$scrubValue"
    "numeric_scrub_selection_length=$scrubSelectionLength"
    "numeric_click_selected_all=$clickSelectedAll"
    "numeric_keyboard_value=$keyboardValue"
    "numeric_size_we_cursor=$sizeCursor"
}
finally {
    [NumericInputNativeMethods]::SetCursorPos($originalCursor.X, $originalCursor.Y) | Out-Null
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $numeric.IsDisposed) { $numeric.Dispose() }
}
