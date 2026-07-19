[CmdletBinding()]
param(
    [string]$AssemblyPath = "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll",
    [string]$OutputDirectory = "artifacts\ui-verification",
    [switch]$SkipFocusTests
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSEdition -ne "Core" -or [Environment]::Version.Major -lt 8) {
    throw "Run this script with PowerShell 7 on .NET 8: pwsh -STA -File $PSCommandPath"
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$resolvedAssembly = if ([IO.Path]::IsPathRooted($AssemblyPath)) {
    $AssemblyPath
} else {
    Join-Path $repoRoot $AssemblyPath
}
$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $repoRoot $OutputDirectory
}
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

$assembly = [Reflection.Assembly]::LoadFrom($resolvedAssembly)
$instanceFlags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"

function Get-EngineType([string]$name) {
    return $assembly.GetType("VectorAnimationEngine.$name", $true)
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Set-TestFocus(
    [Windows.Forms.Form]$form,
    [Windows.Forms.Control]$outerControl,
    [Windows.Forms.Control]$focusedControl) {
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $form.Activate() | Out-Null
        $outerControl.Focus() | Out-Null
        $focusedControl.Focus() | Out-Null
        [Windows.Forms.Application]::DoEvents()
        if ($focusedControl.Focused -and [object]::ReferenceEquals([Windows.Forms.Form]::ActiveForm, $form)) {
            return $true
        }
    }
    return $false
}

function Save-ControlImage(
    [Windows.Forms.Control]$control,
    [string]$fileName,
    [int]$width,
    [int]$height) {
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    try {
        $control.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $width, $height))
        $bitmap.Save((Join-Path $resolvedOutput $fileName), [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $bitmap.Dispose()
    }
}

function Find-ClippedText([Windows.Forms.Control]$root) {
    $stack = [Collections.Generic.Stack[Windows.Forms.Control]]::new()
    $stack.Push($root)
    $clipped = [Collections.Generic.List[string]]::new()
    while ($stack.Count -gt 0) {
        $control = $stack.Pop()
        foreach ($child in $control.Controls) { $stack.Push($child) }
        if (-not $control.Visible -or [string]::IsNullOrWhiteSpace($control.Text)) { continue }
        if ($control -isnot [Windows.Forms.Label] -and $control -isnot [Windows.Forms.ButtonBase]) { continue }
        $measured = [Windows.Forms.TextRenderer]::MeasureText(
            $control.Text,
            $control.Font,
            [Drawing.Size]::new(10000, 1000),
            [Windows.Forms.TextFormatFlags]::NoPadding -bor [Windows.Forms.TextFormatFlags]::SingleLine)
        if ($measured.Width -gt $control.ClientSize.Width -or $measured.Height -gt $control.ClientSize.Height) {
            $clipped.Add("$($control.GetType().Name) '$($control.Text)' $($control.ClientSize.Width)x$($control.ClientSize.Height) requires $($measured.Width)x$($measured.Height)")
        }
    }
    return $clipped
}

function Test-ControlText([Windows.Forms.Control]$root, [string[]]$texts) {
    $stack = [Collections.Generic.Stack[Windows.Forms.Control]]::new()
    $stack.Push($root)
    while ($stack.Count -gt 0) {
        $control = $stack.Pop()
        if ($control.Text -in $texts) { return $true }
        foreach ($child in $control.Controls) { $stack.Push($child) }
    }
    return $false
}

$localizationType = Get-EngineType "UiLocalization"
$languageType = Get-EngineType "UiLanguage"
$localizationType.GetMethod("SetLanguage", $staticFlags).Invoke(
    $null,
    @([Enum]::Parse($languageType, "SimplifiedChinese")))
$watchControl = $localizationType.GetMethod("Watch", $staticFlags, $null, [Type[]] @([Windows.Forms.Control]), $null)

# Verify focus dismissal before capturing the larger workbench forms.
if (-not $SkipFocusTests) {
$focusForm = [Windows.Forms.Form]::new()
$focusForm.ShowInTaskbar = $false
$focusForm.StartPosition = [Windows.Forms.FormStartPosition]::Manual
$focusForm.Location = [Drawing.Point]::new(-30000, -30000)
$focusForm.ClientSize = [Drawing.Size]::new(360, 160)
$focusForm.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
$focusPanel = [Windows.Forms.Panel]::new()
$focusPanel.Dock = [Windows.Forms.DockStyle]::Fill
$focusForm.Controls.Add($focusPanel)

$numericType = Get-EngineType "ModernNumericUpDown"
$numeric = [Activator]::CreateInstance($numericType, $true)
$numeric.SetBounds(16, 16, 120, 28)
$focusPanel.Controls.Add($numeric)
$outsideLabel = [Windows.Forms.Label]::new()
$outsideLabel.Text = "外部区域"
$outsideLabel.SetBounds(16, 64, 120, 28)
$focusPanel.Controls.Add($outsideLabel)
$combo = [Windows.Forms.ComboBox]::new()
$combo.DropDownStyle = [Windows.Forms.ComboBoxStyle]::DropDownList
$combo.Items.AddRange([object[]] @("一", "二"))
$combo.SelectedIndex = 0
$combo.SetBounds(160, 16, 120, 28)
$focusPanel.Controls.Add($combo)

$focusForm.Show()
$focusForm.Activate()
[Windows.Forms.Application]::DoEvents()
$editor = $numericType.GetField("_editor", $instanceFlags).GetValue($numeric)
$increaseButton = $numericType.GetField("_increaseButton", $instanceFlags).GetValue($numeric)
$filterType = Get-EngineType "InputFocusDismissalFilter"
$filter = [Activator]::CreateInstance($filterType, $true)
$preFilter = $filterType.GetMethod("PreFilterMessage", $instanceFlags)
$outsideMessage = [Windows.Forms.Message]::Create($outsideLabel.Handle, 0x0201, [IntPtr]::Zero, [IntPtr]::Zero)
$insideMessage = [Windows.Forms.Message]::Create($increaseButton.Handle, 0x0201, [IntPtr]::Zero, [IntPtr]::Zero)

Assert-True (Set-TestFocus $focusForm $numeric $editor) "The numeric editor could not acquire active-form focus for the outside-click test."
$editor.Text = "42"
[Windows.Forms.Application]::DoEvents()
$arguments = [object[]] @($outsideMessage)
$preFilter.Invoke($filter, $arguments) | Out-Null
[Windows.Forms.Application]::DoEvents()
Assert-True ([decimal]$numeric.Value -eq 42) "Clicking outside did not commit the pending numeric value."
Assert-True (-not $numeric.ContainsFocus) "Clicking outside did not remove focus from the numeric input."

Assert-True (Set-TestFocus $focusForm $numeric $editor) "The numeric editor could not reacquire active-form focus for the composite-input test."
$editor.Text = "55"
[Windows.Forms.Application]::DoEvents()
$arguments = [object[]] @($insideMessage)
$preFilter.Invoke($filter, $arguments) | Out-Null
[Windows.Forms.Application]::DoEvents()
Assert-True $numeric.ContainsFocus "Clicking a numeric stepper incorrectly dismissed its composite input focus."
Assert-True ([decimal]$numeric.Value -eq 42) "Clicking inside the numeric input prematurely committed editor text."

$arguments = [object[]] @($outsideMessage)
$preFilter.Invoke($filter, $arguments) | Out-Null
[Windows.Forms.Application]::DoEvents()
Assert-True ([decimal]$numeric.Value -eq 55) "The second outside click did not commit numeric editor text."

Assert-True (Set-TestFocus $focusForm $combo $combo) "The combo box could not acquire active-form focus for the native-popup test."
[Windows.Forms.Application]::DoEvents()
$nativePopupMessage = [Windows.Forms.Message]::Create([IntPtr]::new(12345), 0x0201, [IntPtr]::Zero, [IntPtr]::Zero)
$arguments = [object[]] @($nativePopupMessage)
$preFilter.Invoke($filter, $arguments) | Out-Null
[Windows.Forms.Application]::DoEvents()
Assert-True $combo.ContainsFocus "A native combo-box popup click incorrectly dismissed the combo-box focus."
$focusForm.Close()
$focusForm.Dispose()
}

# Verify the longest Chinese inspector label at both normal and minimum panel widths.
$materialType = Get-EngineType "MaterialEditorPanel"
$material = [Activator]::CreateInstance($materialType, $true)
$materialHost = [Windows.Forms.Form]::new()
$materialHost.ShowInTaskbar = $false
$materialHost.StartPosition = [Windows.Forms.FormStartPosition]::Manual
$materialHost.Location = [Drawing.Point]::new(-30000, -30000)
$materialHost.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
$material.Dock = [Windows.Forms.DockStyle]::Fill
$materialHost.Controls.Add($material)
$watchControl.Invoke($null, @($material)) | Out-Null
$materialHost.ClientSize = [Drawing.Size]::new(296, 680)
$materialHost.Show()
[Windows.Forms.Application]::DoEvents()
Save-ControlImage $material "localized-material-normal.png" 296 680
$materialHost.ClientSize = [Drawing.Size]::new(240, 680)
$materialHost.PerformLayout()
[Windows.Forms.Application]::DoEvents()
Save-ControlImage $material "localized-material-minimum.png" 240 680
$paletteButton = $materialType.GetField("_paletteButton", $instanceFlags).GetValue($material)
$paletteDropDown = $materialType.GetField("_paletteDropDown", $instanceFlags).GetValue($material)
$hexPanel = $materialType.GetField("_hexPanel", $instanceFlags).GetValue($material)
$hexText = $materialType.GetField("_hexText", $instanceFlags).GetValue($material)
Assert-True $paletteButton.Visible "The palette button is hidden in solid-color mode."
Assert-True ($paletteButton.Right -le $paletteButton.Parent.ClientSize.Width) "The solid-color palette button is outside the visible paint row."
$paletteButton.PerformClick()
[Windows.Forms.Application]::DoEvents()
Assert-True $paletteDropDown.Visible "The palette popup did not open in solid-color mode."
$paletteDropDown.Close()
Assert-True ($hexPanel.Visible -and $hexText.Visible) "The persistent Hex editor is not visible while the color details are collapsed."
Assert-True (-not (Test-ControlText $material @("All alpha", "整体透明度"))) "The removed uniform-opacity control is still present."
$materialHost.ClientSize = [Drawing.Size]::new(296, 680)
$materialType.GetMethod("SetColorEditorExpanded", $instanceFlags).Invoke($material, @($true)) | Out-Null
$materialHost.PerformLayout()
[Windows.Forms.Application]::DoEvents()
Assert-True ($hexPanel.Visible -and $hexText.Visible) "The persistent Hex editor disappeared after expanding color details."
Save-ControlImage $material "localized-material-expanded.png" 296 680
$materialHost.Close()
$materialHost.Dispose()

# Capture the localized settings dialog.
$settingsType = Get-EngineType "SettingsDialog"
$settings = [Activator]::CreateInstance($settingsType, $true)
$settings.Location = [Drawing.Point]::new(-30000, -30000)
$settings.StartPosition = [Windows.Forms.FormStartPosition]::Manual
$settings.Show()
[Windows.Forms.Application]::DoEvents()
$settingsClipping = @(Find-ClippedText $settings)
Assert-True ($settingsClipping.Count -eq 0) ("Localized settings text clipping: " + ($settingsClipping -join "; "))
Save-ControlImage $settings "localized-settings.png" $settings.Width $settings.Height
$settings.Close()
$settings.Dispose()

# Capture both supported workbench widths and audit visible labels/buttons.
$mainType = Get-EngineType "MainForm"
$workspaceType = Get-EngineType "WorkspaceView"
$main = [Activator]::CreateInstance($mainType, $true)
$main.Location = [Drawing.Point]::new(-30000, -30000)
$main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
$main.Show()
$timer = $mainType.GetField("_timer", $instanceFlags).GetValue($main)
$timer.Stop()
$main.ClientSize = [Drawing.Size]::new(1480, 920)
$main.PerformLayout()
[Windows.Forms.Application]::DoEvents()
$normalClipping = @(Find-ClippedText $main)
Assert-True ($normalClipping.Count -eq 0) ("Normal-width Chinese text clipping: " + ($normalClipping -join "; "))
Save-ControlImage $main "localized-main-normal.png" 1480 920

$showWorkspace = $mainType.GetMethod("ShowWorkspace", $instanceFlags)
$showWorkspace.Invoke($main, @([Enum]::Parse($workspaceType, "SceneEditor"))) | Out-Null
$main.ClientSize = [Drawing.Size]::new(1120, 720)
$main.PerformLayout()
[Windows.Forms.Application]::DoEvents()
$minimumClipping = @(Find-ClippedText $main)
Assert-True ($minimumClipping.Count -eq 0) ("Minimum-width Chinese text clipping: " + ($minimumClipping -join "; "))
Save-ControlImage $main "localized-main-minimum.png" 1120 720
$main.Close()
$main.Dispose()

[pscustomobject] @{
    FocusTests = if ($SkipFocusTests) { "Skipped" } else { "Passed" }
    Screenshots = @(
        "localized-material-normal.png",
        "localized-material-minimum.png",
        "localized-material-expanded.png",
        "localized-settings.png",
        "localized-main-normal.png",
        "localized-main-minimum.png")
} | ConvertTo-Json -Compress
