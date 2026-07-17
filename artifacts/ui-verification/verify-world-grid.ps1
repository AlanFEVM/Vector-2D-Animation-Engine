$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WorldGridCaptureNative {
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

function Pump-Ui([int]$frames = 8) {
    for ($index = 0; $index -lt $frames; $index++) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 16
    }
}

function Capture-Grid($form, $stage, [string]$path) {
    $stage.Invalidate()
    $stage.Update()
    Pump-Ui 3
    $bitmap = [Drawing.Bitmap]::new($stage.ClientSize.Width, $stage.ClientSize.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try {
            if (-not [WorldGridCaptureNative]::PrintWindow($form.Handle, $hdc, 2)) {
                throw "Could not capture the world-grid HWND."
            }
        } finally {
            $graphics.ReleaseHdc($hdc)
        }
    } finally {
        $graphics.Dispose()
    }

    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    return $bitmap
}

$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$stageType = $assembly.GetType("VectorAnimationEngine.StageControl", $true)
$layoutType = $assembly.GetType("VectorAnimationEngine.WorldGridLayout", $true)
$scene = [Activator]::CreateInstance($sceneType, $true)
$sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(1, 24))
$stage = [Activator]::CreateInstance($stageType, $flags, $null, [object[]] @($scene), $null)
$form = New-Object Windows.Forms.Form
$defaultBitmap = $null
$closeBitmap = $null
$gdiBitmap = $null
try {
    $form.ShowInTaskbar = $false
    $form.TopMost = $true
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = [Drawing.Point]::new(60, 60)
    $form.ClientSize = [Drawing.Size]::new(760, 520)
    $stage.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($stage)
    $form.Show()
    $form.Activate()
    Pump-Ui 10

    $defaultOpacity = $stageType.GetProperty("WorldGridOpacity", $flags).GetValue($stage)
    if ([Math]::Abs($defaultOpacity - 0.1) -gt 0.0001) {
        throw "The Stage world grid did not default to 10% opacity: $defaultOpacity"
    }

    $setWidth = $stageType.GetMethod("SetVisibleWorldWidth", $flags)
    $setWidth.Invoke($stage, @([single]4000))
    Pump-Ui 5
    if (-not $stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)) {
        throw "The default world-grid capture did not use Direct2D."
    }
    $defaultPath = Join-Path $root "artifacts\ui-verification\world-grid-default.png"
    $defaultBitmap = Capture-Grid $form $stage $defaultPath

    $setWidth.Invoke($stage, @([single]300))
    Pump-Ui 5
    $pixelsPerWorldUnit = $stageType.GetMethod("WorldLengthToScreen", $flags).Invoke($stage, @([single]1))
    $scale = $layoutType.GetMethod("Resolve", $staticFlags).Invoke($null, @([single]$pixelsPerWorldUnit))
    if ([Math]::Abs($scale.StepWorld - 10) -gt 0.001) {
        throw "The closest grid level did not stop at 10 vu: $($scale.StepWorld)"
    }
    $closePath = Join-Path $root "artifacts\ui-verification\world-grid-10vu.png"
    $closeBitmap = Capture-Grid $form $stage $closePath

    $centerX = [int]($closeBitmap.Width / 2)
    $centerY = [int]($closeBitmap.Height / 2)
    $origin = $closeBitmap.GetPixel($centerX, $centerY)
    $xAxis = $closeBitmap.GetPixel($centerX + 80, $centerY)
    $yAxis = $closeBitmap.GetPixel($centerX, $centerY + 80)
    if ($origin.GetBrightness() -lt 0.1 -or $xAxis.R -le $xAxis.G -or $yAxis.G -le $yAxis.R) {
        throw "The world origin or colored X/Y axes were not visible in the Direct2D capture."
    }

    $renderer = $stageType.GetField("_direct2DRenderer", $flags).GetValue($stage)
    $renderer.GetType().GetField("_disabled", $flags).SetValue($renderer, $true)
    $setWidth.Invoke($stage, @([single]4000))
    Pump-Ui 5
    $gdiPath = Join-Path $root "artifacts\ui-verification\world-grid-gdi.png"
    $gdiBitmap = Capture-Grid $form $stage $gdiPath
    if ($stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)) {
        throw "The GDI world-grid capture did not disable Direct2D."
    }

    $gdiOrigin = $gdiBitmap.GetPixel([int]($gdiBitmap.Width / 2), [int]($gdiBitmap.Height / 2))
    if ($gdiOrigin.GetBrightness() -lt 0.1) {
        throw "The GDI fallback did not render the world origin."
    }

    "world_grid_default=$defaultPath"
    "world_grid_10vu=$closePath"
    "world_grid_gdi=$gdiPath"
    "world_grid_minimum_step=$($scale.StepWorld)vu"
    "world_grid_default_opacity=$([Math]::Round($defaultOpacity * 100))%"
    "world_grid_direct2d=True"
    "world_grid_gdi_fallback=True"
}
finally {
    if ($null -ne $defaultBitmap) { $defaultBitmap.Dispose() }
    if ($null -ne $closeBitmap) { $closeBitmap.Dispose() }
    if ($null -ne $gdiBitmap) { $gdiBitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $stage.IsDisposed) { $stage.Dispose() }
}
