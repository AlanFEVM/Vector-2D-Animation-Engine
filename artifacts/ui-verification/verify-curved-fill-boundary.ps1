$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class CurvedFillBoundaryCaptureNative {
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

function Capture-Window($form, [string]$path) {
    $bitmap = [Drawing.Bitmap]::new($form.ClientSize.Width, $form.ClientSize.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try {
            if (-not [CurvedFillBoundaryCaptureNative]::PrintWindow($form.Handle, $hdc, 2)) {
                throw "Could not capture the curved fill boundary HWND."
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

function Count-TealPixels($bitmap, [Drawing.PointF]$point) {
    $count = 0
    $centerX = [int][Math]::Round($point.X)
    $centerY = [int][Math]::Round($point.Y)
    for ($y = $centerY - 4; $y -le $centerY + 4; $y++) {
        for ($x = $centerX - 4; $x -le $centerX + 4; $x++) {
            $color = $bitmap.GetPixel($x, $y)
            if ($color.G -gt ($color.R + 45) -and $color.B -gt ($color.R + 45)) { $count++ }
        }
    }
    return $count
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"
$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$stageType = $assembly.GetType("VectorAnimationEngine.StageControl", $true)
$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$shapeKindType = $assembly.GetType("VectorAnimationEngine.ShapeKind", $true)
$endpointStyleType = $assembly.GetType("VectorAnimationEngine.LineEndpointStyle", $true)
$rectangle = [Enum]::Parse($shapeKindType, "Rectangle")
$roundStyle = [Enum]::Parse($endpointStyleType, "Round")

$scene = [Activator]::CreateInstance($sceneType, $true)
$scene.CreateEmpty(1, 24)
$fill = $scene.AddObject(
    0,
    [Drawing.PointF]::Empty,
    [Drawing.SizeF]::new(200, 100),
    [single]0,
    [single]0,
    [Drawing.Color]::Teal,
    [Drawing.Color]::Transparent,
    [uint32]12,
    $rectangle)
$line = $sceneType.GetMethod("AddCubicCurveSegment", $flags).Invoke($scene, [object[]] @(
    0,
    [Drawing.PointF]::new(-100, -50),
    [Drawing.PointF]::new(-60, -140),
    [Drawing.PointF]::new(60, -140),
    [Drawing.PointF]::new(100, -50),
    [single]8,
    [Drawing.Color]::Transparent,
    [Drawing.Color]::White,
    [uint32]12,
    $roundStyle,
    $roundStyle))
$links = $sceneType.GetMethod("CaptureFillBoundaryLineLinks", $flags).Invoke($scene, @($line, 0))
$updated = $sceneType.GetMethod("UpdateFillBoundaryLineLinks", $flags).Invoke(
    $scene,
    [object[]] @([object]$links, $true))
$expandedPoint = [Drawing.PointF]::new(0, -80)
if ($links.Count -ne 1 -or $links[0].SegmentCount -ne 1 -or -not $updated -or -not $scene.FillContainsPoint($fill, $expandedPoint)) {
    throw "The curved line did not expand the straight fill boundary before HWND rendering."
}
$curveHit = $scene.HitTestElement([Drawing.PointF]::new(0, -117.5), 0, [single]2)
$pathHits = $mainType.GetMethod("TraditionalPenPathElements", $staticFlags).Invoke($null, @($scene, $curveHit, 0))
if ($pathHits.Count -ne 1 -or $pathHits[0].StartT -gt 0.001 -or $pathHits[0].EndT -lt 0.999) {
    throw "The expanded fill boundary split its linked curve into $($pathHits.Count) selected parts."
}

$stage = [Activator]::CreateInstance($stageType, $flags, $null, [object[]] @($scene), $null)
$form = [Windows.Forms.Form]::new()
$direct2DBitmap = $null
$gdiBitmap = $null
try {
    $form.ShowInTaskbar = $false
    $form.TopMost = $true
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = [Drawing.Point]::new(60, 60)
    $form.ClientSize = [Drawing.Size]::new(760, 500)
    $stage.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($stage)
    $stage.SetVisibleWorldWidth([single]500)
    $stage.SetSelection([int[]] @($line), $line)
    $stage.SetSelectedElements($pathHits, $pathHits[0])
    $stage.SetPenPathHandlesVisible($true)
    $form.Show()
    $form.Activate()
    $form.BringToFront()
    Pump-Ui 12

    if (-not $stage.LastFrameUsedDirect2D) {
        throw "Curved fill boundary verification did not use Direct2D."
    }
    $screenPoint = $stage.WorldToScreen($expandedPoint.X, $expandedPoint.Y)
    $direct2DPath = Join-Path $root "artifacts\ui-verification\curved-fill-boundary-direct2d.png"
    $direct2DBitmap = Capture-Window $form $direct2DPath
    $direct2DPixels = Count-TealPixels $direct2DBitmap $screenPoint

    $renderer = $stageType.GetField("_direct2DRenderer", $flags).GetValue($stage)
    $renderer.GetType().GetField("_disabled", $flags).SetValue($renderer, $true)
    $stage.Invalidate()
    Pump-Ui 6
    $gdiPath = Join-Path $root "artifacts\ui-verification\curved-fill-boundary-gdi.png"
    $gdiBitmap = Capture-Window $form $gdiPath
    $gdiPixels = Count-TealPixels $gdiBitmap $screenPoint

    if ($direct2DPixels -lt 20 -or $gdiPixels -lt 20) {
        throw "The expanded fill was not visible in both renderers: Direct2D=$direct2DPixels GDI=$gdiPixels."
    }

    "curved_fill_boundary_links=$($links.Count)"
    "curved_fill_boundary_selected_parts=$($pathHits.Count)"
    "curved_fill_boundary_direct2d_pixels=$direct2DPixels"
    "curved_fill_boundary_gdi_pixels=$gdiPixels"
    "curved_fill_boundary_direct2d=$direct2DPath"
    "curved_fill_boundary_gdi=$gdiPath"
}
finally {
    if ($null -ne $direct2DBitmap) { $direct2DBitmap.Dispose() }
    if ($null -ne $gdiBitmap) { $gdiBitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $stage.IsDisposed) { $stage.Dispose() }
}
