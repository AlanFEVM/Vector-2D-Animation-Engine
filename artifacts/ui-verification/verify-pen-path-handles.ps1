$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class PenPathCaptureNative {
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

function Capture-Window($form, [string]$path, [uint32]$flags = 2) {
    $bitmap = [Drawing.Bitmap]::new($form.ClientSize.Width, $form.ClientSize.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try {
            if (-not [PenPathCaptureNative]::PrintWindow($form.Handle, $hdc, $flags)) {
                throw "Could not capture the Pen path handle HWND."
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

function Count-HandlePixels($bitmap, [Drawing.PointF]$point, [bool]$control) {
    $count = 0
    $centerX = [int][Math]::Round($point.X)
    $centerY = [int][Math]::Round($point.Y)
    for ($y = [Math]::Max(0, $centerY - 7); $y -le [Math]::Min($bitmap.Height - 1, $centerY + 7); $y++) {
        for ($x = [Math]::Max(0, $centerX - 7); $x -le [Math]::Min($bitmap.Width - 1, $centerX + 7); $x++) {
            $color = $bitmap.GetPixel($x, $y)
            $matches = if ($control) {
                $color.B -gt ($color.R + 50) -and $color.G -gt ($color.R + 30)
            } else {
                $color.R -gt 200 -and $color.G -gt 180 -and $color.B -lt 225
            }
            if ($matches) { $count++ }
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
$keyType = $assembly.GetType("VectorAnimationEngine.DrawingElementKey", $true)
$hitType = $assembly.GetType("VectorAnimationEngine.DrawingElementHit", $true)
$elementKindType = $assembly.GetType("VectorAnimationEngine.DrawingElementKind", $true)
$endpointStyleType = $assembly.GetType("VectorAnimationEngine.LineEndpointStyle", $true)
$strokeKind = [Enum]::Parse($elementKindType, "Stroke")
$roundStyle = [Enum]::Parse($endpointStyleType, "Round")

$scene = [Activator]::CreateInstance($sceneType, $true)
$sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(1, 24))
$segments = @(
    @([Drawing.PointF]::new(-240, 30), [Drawing.PointF]::new(-220, -90), [Drawing.PointF]::new(-130, -110), [Drawing.PointF]::new(-80, 20)),
    @([Drawing.PointF]::new(-80, 20), [Drawing.PointF]::new(-45, 130), [Drawing.PointF]::new(40, 145), [Drawing.PointF]::new(80, 20)),
    @([Drawing.PointF]::new(80, 20), [Drawing.PointF]::new(125, -100), [Drawing.PointF]::new(205, -75), [Drawing.PointF]::new(240, 30)))
$indices = [Collections.Generic.List[int]]::new()
foreach ($segment in $segments) {
    $index = $sceneType.GetMethod("AddCubicCurveSegment", $flags).Invoke($scene, [object[]] @(
        0, $segment[0], $segment[1], $segment[2], $segment[3], [single]8,
        [Drawing.Color]::Transparent, [Drawing.Color]::White, [uint32]12,
        $roundStyle, $roundStyle))
    $indices.Add($index)
}

$seedKey = [Activator]::CreateInstance($keyType, $flags, $null, [object[]] @($indices[0], $strokeKind, 0), $null)
$seedHit = [Activator]::CreateInstance($hitType, $flags, $null, [object[]] @($seedKey, [single]0, [single]0, [single]1), $null)
$pathHits = $mainType.GetMethod("TraditionalPenPathElements", $staticFlags).Invoke($null, @($scene, $seedHit, 0))
if ($pathHits.Count -ne 3) { throw "Expected three connected Pen path segments, found $($pathHits.Count)." }

$stage = [Activator]::CreateInstance($stageType, $flags, $null, [object[]] @($scene), $null)
$form = [Windows.Forms.Form]::new()
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
    $stageType.GetMethod("SetVisibleWorldWidth", $flags).Invoke($stage, @([single]680))
    $stageType.GetMethod("SetSelection", $flags).Invoke($stage, @($indices.ToArray(), $indices[0]))
    $stageType.GetMethod("SetSelectedElements", $flags).Invoke($stage, @($pathHits, $pathHits[0]))
    $stageType.GetMethod("SetPenPathHandlesVisible", $flags).Invoke($stage, @($true))
    $form.Show()
    $form.Activate()
    $form.BringToFront()
    Pump-Ui 12

    if (-not $stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)) {
        throw "Pen path handle verification did not use Direct2D."
    }
    $controlScreens = $segments | ForEach-Object {
        $stage.WorldToScreen([single]$_[1].X, [single]$_[1].Y)
        $stage.WorldToScreen([single]$_[2].X, [single]$_[2].Y)
    }
    $endpointWorld = @($segments[0][0], $segments[0][3], $segments[1][3], $segments[2][3])
    $endpointScreens = $endpointWorld | ForEach-Object { $stage.WorldToScreen([single]$_.X, [single]$_.Y) }
    $renderer = $stageType.GetField("_direct2DRenderer", $flags).GetValue($stage)
    $renderer.GetType().GetField("_disabled", $flags).SetValue($renderer, $true)
    $stage.Invalidate()
    Pump-Ui 6
    if ($stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)) {
        throw "Pen path handle GDI verification did not disable Direct2D."
    }
    $gdiPath = Join-Path $root "artifacts\ui-verification\pen-path-handles-gdi.png"
    $gdiBitmap = Capture-Window $form $gdiPath
    $gdiControls = @($controlScreens | ForEach-Object { Count-HandlePixels $gdiBitmap $_ $true })
    $gdiEndpoints = @($endpointScreens | ForEach-Object { Count-HandlePixels $gdiBitmap $_ $false })

    if (($gdiControls.Where({ $_ -lt 4 }).Count -gt 0) -or
        ($gdiEndpoints.Where({ $_ -lt 4 }).Count -gt 0)) {
        throw "Not all Pen path handles were visible in the pixel-checkable renderer: controls=$gdiControls endpoints=$gdiEndpoints."
    }

    "pen_path_handles_direct2d=state-ok"
    "pen_path_handles_gdi=$gdiPath"
    "pen_path_control_pixels_gdi=$gdiControls"
    "pen_path_endpoint_pixels_gdi=$gdiEndpoints"
}
finally {
    if ($null -ne $gdiBitmap) { $gdiBitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $stage.IsDisposed) { $stage.Dispose() }
}
