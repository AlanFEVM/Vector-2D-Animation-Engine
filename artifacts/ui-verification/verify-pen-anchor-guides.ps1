$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class PenGuideCaptureNative {
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@

function Pump-Ui([int]$frames) {
    for ($index = 0; $index -lt $frames; $index++) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 16
    }
}

function Capture-Stage($form, $stage, [string]$path) {
    $bitmap = [Drawing.Bitmap]::new($stage.ClientSize.Width, $stage.ClientSize.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try {
            if (-not [PenGuideCaptureNative]::PrintWindow($form.Handle, $hdc, 2)) {
                throw "Could not capture the pen-guide Stage HWND."
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

function Count-GuidePixels($bitmap, [int]$coordinate, [bool]$vertical) {
    $count = 0
    $limit = if ($vertical) { $bitmap.Height } else { $bitmap.Width }
    for ($position = 0; $position -lt $limit; $position++) {
        $x = if ($vertical) { $coordinate } else { $position }
        $y = if ($vertical) { $position } else { $coordinate }
        $color = $bitmap.GetPixel($x, $y)
        if ($color.G -gt ($color.R + 24) -and $color.B -gt ($color.R + 24)) { $count++ }
    }
    return $count
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$stageType = $assembly.GetType("VectorAnimationEngine.StageControl", $true)
$scene = [Activator]::CreateInstance($sceneType, $true)
$sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(1, 24))
$sceneType.GetMethod("AddCurveSegment", $flags).Invoke($scene, [object[]] @(
    0,
    [Drawing.PointF]::new(-180, 80),
    [Drawing.PointF]::new(0, -120),
    [Drawing.PointF]::new(180, 80),
    [single]8,
    [Drawing.Color]::Transparent,
    [Drawing.Color]::White,
    [uint32]12,
    [Enum]::Parse($assembly.GetType("VectorAnimationEngine.LineEndpointStyle", $true), "Round"),
    [Enum]::Parse($assembly.GetType("VectorAnimationEngine.LineEndpointStyle", $true), "Round"))) | Out-Null

$stage = [Activator]::CreateInstance($stageType, $flags, $null, [object[]] @($scene), $null)
$form = New-Object Windows.Forms.Form
$directBitmap = $null
$gdiBitmap = $null
try {
    $form.ShowInTaskbar = $false
    $form.TopMost = $true
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = [Drawing.Point]::new(60, 60)
    $form.ClientSize = [Drawing.Size]::new(720, 480)
    $stage.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($stage)
    $stageType.GetMethod("SetVisibleWorldWidth", $flags).Invoke($stage, @([single]620))
    $guidePoint = [Drawing.PointF]::new(40, 20)
    $stageType.GetMethod("SetPenAnchorGuides", $flags).Invoke($stage, @($guidePoint, $true, $true, $false, $true))
    $form.Show()
    $form.Activate()
    $form.BringToFront()
    Pump-Ui 12

    if (-not $stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)) {
        throw "Pen-guide verification did not use Direct2D."
    }
    $screenPoint = $stageType.GetMethod("WorldToScreen", $flags, $null, [Type[]] @([single], [single]), $null).Invoke(
        $stage,
        @([single]$guidePoint.X, [single]$guidePoint.Y))
    $guideX = [Math]::Clamp([int][Math]::Round($screenPoint.X), 0, $stage.Width - 1)
    $guideY = [Math]::Clamp([int][Math]::Round($screenPoint.Y), 0, $stage.Height - 1)
    $directPath = Join-Path $root "artifacts\ui-verification\pen-anchor-guides-direct2d.png"
    $directBitmap = Capture-Stage $form $stage $directPath
    $directVertical = Count-GuidePixels $directBitmap $guideX $true
    $directHorizontal = Count-GuidePixels $directBitmap $guideY $false

    $renderer = $stageType.GetField("_direct2DRenderer", $flags).GetValue($stage)
    $renderer.GetType().GetField("_disabled", $flags).SetValue($renderer, $true)
    $stage.Invalidate()
    Pump-Ui 6
    if ($stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)) {
        throw "Pen-guide GDI verification did not disable Direct2D."
    }
    $gdiPath = Join-Path $root "artifacts\ui-verification\pen-anchor-guides-gdi.png"
    $gdiBitmap = Capture-Stage $form $stage $gdiPath
    $gdiVertical = Count-GuidePixels $gdiBitmap $guideX $true
    $gdiHorizontal = Count-GuidePixels $gdiBitmap $guideY $false

    if ($directVertical -lt 24 -or $directHorizontal -lt 24 -or $gdiVertical -lt 24 -or $gdiHorizontal -lt 24) {
        throw "Pen anchor guides were not visible in both renderers: D2D=$directVertical/$directHorizontal GDI=$gdiVertical/$gdiHorizontal"
    }

    "pen_anchor_guides_direct2d=$directPath"
    "pen_anchor_guides_gdi=$gdiPath"
    "pen_anchor_guide_pixels_direct2d=$directVertical/$directHorizontal"
    "pen_anchor_guide_pixels_gdi=$gdiVertical/$gdiHorizontal"
}
finally {
    if ($null -ne $directBitmap) { $directBitmap.Dispose() }
    if ($null -ne $gdiBitmap) { $gdiBitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $stage.IsDisposed) { $stage.Dispose() }
}
