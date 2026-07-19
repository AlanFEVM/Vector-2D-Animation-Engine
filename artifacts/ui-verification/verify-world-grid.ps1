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
$spiralBitmap = $null
$spiralZoomBitmap = $null
$spiralDistantBitmap = $null
$spiralGdiBitmap = $null
$polarBitmap = $null
$polarZoomBitmap = $null
$polarGdiBitmap = $null
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
    $defaultPixelsPerWorldUnit = $stageType.GetMethod("WorldLengthToScreen", $flags).Invoke($stage, @([single]1))
    $defaultScale = $layoutType.GetMethod("Resolve", $staticFlags).Invoke($null, @([single]$defaultPixelsPerWorldUnit))
    $defaultSnapStep = $stageType.GetProperty("AdaptiveGridSnapStep", $flags).GetValue($stage)
    if ([Math]::Abs($defaultSnapStep - $defaultScale.StepWorld) -gt 0.001) {
        throw "The default grid display and snapping levels diverged: display=$($defaultScale.StepWorld) snap=$defaultSnapStep"
    }
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
    $closeSnapStep = $stageType.GetProperty("AdaptiveGridSnapStep", $flags).GetValue($stage)
    if ([Math]::Abs($closeSnapStep - $scale.StepWorld) -gt 0.001) {
        throw "The close grid display and snapping levels diverged: display=$($scale.StepWorld) snap=$closeSnapStep"
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

    $gridType = $assembly.GetType("VectorAnimationEngine.WorldGridType", $true)
    $goldenSpiral = [Enum]::Parse($gridType, "GoldenSpiral")
    $polar = [Enum]::Parse($gridType, "Polar")
    $cartesian = [Enum]::Parse($gridType, "Cartesian")
    $stageType.GetProperty("WorldGridType", $flags).SetValue($stage, $goldenSpiral)
    $stageType.GetProperty("WorldGridOpacity", $flags).SetValue($stage, [single]0.45)
    $setWidth.Invoke($stage, @([single]$scene.StageWidth))
    Pump-Ui 3
    $resolveGoldenGrid = $stageType.GetMethod("ResolveGoldenSpiralGrid", $flags)
    $goldenGeometry = $resolveGoldenGrid.Invoke($stage, $null)
    $goldenStart = $goldenGeometry.SpiralPoints[0]
    $cartesianOrigin = $stage.WorldToScreen([single]0, [single]0)
    $goldenStartScreen = $stage.WorldToScreen($goldenStart.X, $goldenStart.Y)
    if ([Math]::Abs($goldenStart.X) -gt 0.001 -or
        [Math]::Abs($goldenStart.Y) -gt 0.001 -or
        [Math]::Abs($goldenStartScreen.X - $cartesianOrigin.X) -gt 0.001 -or
        [Math]::Abs($goldenStartScreen.Y - $cartesianOrigin.Y) -gt 0.001) {
        throw "The golden-spiral start did not share the Cartesian world origin: world=$goldenStart screen=$goldenStartScreen origin=$cartesianOrigin"
    }
    $normalA = $stage.WorldToScreen($goldenGeometry.SpiralPoints[160].X, $goldenGeometry.SpiralPoints[160].Y)
    $normalB = $stage.WorldToScreen($goldenGeometry.SpiralPoints[176].X, $goldenGeometry.SpiralPoints[176].Y)
    $normalDistance = [Math]::Sqrt(
        [Math]::Pow($normalA.X - $normalB.X, 2) +
        [Math]::Pow($normalA.Y - $normalB.Y, 2))
    $spiralPath = Join-Path $root "artifacts\ui-verification\world-grid-golden-spiral.png"
    $spiralBitmap = Capture-Grid $form $stage $spiralPath
    if (-not $stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)) {
        throw "The golden-spiral grid capture did not use Direct2D."
    }
    $goldPixels = 0
    for ($y = 0; $y -lt $spiralBitmap.Height; $y += 2) {
        for ($x = 0; $x -lt $spiralBitmap.Width; $x += 2) {
            $color = $spiralBitmap.GetPixel($x, $y)
            if ($color.R -gt ($color.G + 5) -and $color.G -gt ($color.B + 20)) { $goldPixels++ }
        }
    }
    if ($goldPixels -lt 80) {
        throw "The Direct2D golden-spiral grid did not contain a visible spiral: pixels=$goldPixels"
    }

    $setWidth.Invoke($stage, @([single]($scene.StageWidth * 0.5)))
    Pump-Ui 3
    $zoomA = $stage.WorldToScreen($goldenGeometry.SpiralPoints[160].X, $goldenGeometry.SpiralPoints[160].Y)
    $zoomB = $stage.WorldToScreen($goldenGeometry.SpiralPoints[176].X, $goldenGeometry.SpiralPoints[176].Y)
    $zoomDistance = [Math]::Sqrt(
        [Math]::Pow($zoomA.X - $zoomB.X, 2) +
        [Math]::Pow($zoomA.Y - $zoomB.Y, 2))
    $zoomRatio = $zoomDistance / $normalDistance
    if ([Math]::Abs($zoomRatio - 2) -gt 0.001) {
        throw "The golden-spiral grid did not follow the Stage zoom: ratio=$zoomRatio"
    }
    $spiralZoomPath = Join-Path $root "artifacts\ui-verification\world-grid-golden-spiral-zoom.png"
    $spiralZoomBitmap = Capture-Grid $form $stage $spiralZoomPath

    $stage.ZoomAt(
        [Drawing.Point]::new([int]($stage.Width / 2), [int]($stage.Height / 2)),
        [single]0.001)
    Pump-Ui 3
    $distantGeometry = $resolveGoldenGrid.Invoke($stage, $null)
    $distantBounds = $stage.VisibleWorldBounds()
    $distantOuter = $distantGeometry.SpiralPoints[-1]
    $distantOuterRadius = [Math]::Sqrt(
        [Math]::Pow($distantOuter.X, 2) +
        [Math]::Pow($distantOuter.Y, 2))
    $maximumVisibleRadius = [Math]::Sqrt(
        [Math]::Pow([Math]::Max([Math]::Abs($distantBounds.Left), [Math]::Abs($distantBounds.Right)), 2) +
        [Math]::Pow([Math]::Max([Math]::Abs($distantBounds.Top), [Math]::Abs($distantBounds.Bottom)), 2))
    $distantSnapStep = $stageType.GetProperty("AdaptiveGridSnapStep", $flags).GetValue($stage)
    $guideSnapRatio = $distantGeometry.Bounds.Right / $distantSnapStep
    if ($distantOuterRadius -lt $maximumVisibleRadius * 5 -or
        $distantGeometry.Bounds.Left -gt $distantBounds.Left -or
        $distantGeometry.Bounds.Right -lt $distantBounds.Right -or
        $distantGeometry.Bounds.Top -gt $distantBounds.Top -or
        $distantGeometry.Bounds.Bottom -lt $distantBounds.Bottom -or
        [Math]::Abs($guideSnapRatio - [Math]::Round($guideSnapRatio)) -gt 0.001) {
        throw "The golden spiral did not extend beyond the distant viewport or adapt its guides to the current snap step."
    }
    $spiralDistantPath = Join-Path $root "artifacts\ui-verification\world-grid-golden-spiral-distant.png"
    $spiralDistantBitmap = Capture-Grid $form $stage $spiralDistantPath
    $distantGoldPixels = 0
    for ($y = 0; $y -lt $spiralDistantBitmap.Height; $y += 2) {
        for ($x = 0; $x -lt $spiralDistantBitmap.Width; $x += 2) {
            $color = $spiralDistantBitmap.GetPixel($x, $y)
            if ($color.R -gt ($color.G + 5) -and $color.G -gt ($color.B + 20)) { $distantGoldPixels++ }
        }
    }
    if ($distantGoldPixels -lt 80) {
        throw "The distant golden-spiral grid collapsed instead of filling the viewport: pixels=$distantGoldPixels"
    }

    $stageType.GetProperty("WorldGridType", $flags).SetValue($stage, $polar)
    $setWidth.Invoke($stage, @([single]$scene.StageWidth))
    Pump-Ui 3
    $resolvePolarGrid = $stageType.GetMethod("ResolvePolarGrid", $flags)
    $polarGeometry = $resolvePolarGrid.Invoke($stage, $null)
    $polarOrigin = $stage.WorldToScreen($polarGeometry.Origin.X, $polarGeometry.Origin.Y)
    $cartesianOrigin = $stage.WorldToScreen([single]0, [single]0)
    if ($polarGeometry.Circles.Length -lt 2 -or
        $polarGeometry.DiameterSegments.Length -ne 12 -or
        [Math]::Abs($polarOrigin.X - $cartesianOrigin.X) -gt 0.001 -or
        [Math]::Abs($polarOrigin.Y - $cartesianOrigin.Y) -gt 0.001) {
        throw "The polar grid did not share the Cartesian origin or generate its circles and radial guides."
    }
    $polarRadius = $polarGeometry.Circles[0].Radius
    $polarRadiusPoint = $stage.WorldToScreen([single]$polarRadius, [single]0)
    $normalPolarRadiusPixels = [Math]::Abs($polarRadiusPoint.X - $polarOrigin.X)
    $polarPath = Join-Path $root "artifacts\ui-verification\world-grid-polar.png"
    $polarBitmap = Capture-Grid $form $stage $polarPath
    if (-not $stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)) {
        throw "The polar-grid capture did not use Direct2D."
    }

    $setWidth.Invoke($stage, @([single]($scene.StageWidth * 0.5)))
    Pump-Ui 3
    $zoomPolarOrigin = $stage.WorldToScreen([single]0, [single]0)
    $zoomPolarRadiusPoint = $stage.WorldToScreen([single]$polarRadius, [single]0)
    $zoomPolarRadiusPixels = [Math]::Abs($zoomPolarRadiusPoint.X - $zoomPolarOrigin.X)
    $polarZoomRatio = $zoomPolarRadiusPixels / $normalPolarRadiusPixels
    if ([Math]::Abs($polarZoomRatio - 2) -gt 0.001) {
        throw "The polar grid did not follow the Stage zoom: ratio=$polarZoomRatio"
    }
    $polarZoomPath = Join-Path $root "artifacts\ui-verification\world-grid-polar-zoom.png"
    $polarZoomBitmap = Capture-Grid $form $stage $polarZoomPath

    $renderer = $stageType.GetField("_direct2DRenderer", $flags).GetValue($stage)
    $renderer.GetType().GetField("_disabled", $flags).SetValue($renderer, $true)
    $stageType.GetProperty("WorldGridType", $flags).SetValue($stage, $goldenSpiral)
    $setWidth.Invoke($stage, @([single]$scene.StageWidth))
    Pump-Ui 3
    $spiralGdiPath = Join-Path $root "artifacts\ui-verification\world-grid-golden-spiral-gdi.png"
    $spiralGdiBitmap = Capture-Grid $form $stage $spiralGdiPath
    if ($stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)) {
        throw "The GDI golden-spiral grid capture did not disable Direct2D."
    }

    $stageType.GetProperty("WorldGridType", $flags).SetValue($stage, $polar)
    Pump-Ui 3
    $polarGdiPath = Join-Path $root "artifacts\ui-verification\world-grid-polar-gdi.png"
    $polarGdiBitmap = Capture-Grid $form $stage $polarGdiPath
    if ($stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)) {
        throw "The GDI polar-grid capture did not disable Direct2D."
    }

    $stageType.GetProperty("WorldGridType", $flags).SetValue($stage, $cartesian)
    $stageType.GetProperty("WorldGridOpacity", $flags).SetValue($stage, [single]$defaultOpacity)
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
    "world_grid_golden_spiral=$spiralPath"
    "world_grid_golden_spiral_zoom=$spiralZoomPath"
    "world_grid_golden_spiral_distant=$spiralDistantPath"
    "world_grid_golden_spiral_gdi=$spiralGdiPath"
    "world_grid_golden_spiral_pixels=$goldPixels"
    "world_grid_golden_spiral_distant_pixels=$distantGoldPixels"
    "world_grid_golden_spiral_origin=$($goldenStart.X),$($goldenStart.Y)"
    "world_grid_golden_spiral_zoom_ratio=$($zoomRatio.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "world_grid_polar=$polarPath"
    "world_grid_polar_zoom=$polarZoomPath"
    "world_grid_polar_gdi=$polarGdiPath"
    "world_grid_polar_origin=$($polarGeometry.Origin.X),$($polarGeometry.Origin.Y)"
    "world_grid_polar_zoom_ratio=$($polarZoomRatio.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "world_grid_gdi=$gdiPath"
    "world_grid_minimum_step=$($scale.StepWorld)vu"
    "world_grid_default_snap_step=$($defaultSnapStep)vu"
    "world_grid_close_snap_step=$($closeSnapStep)vu"
    "world_grid_default_opacity=$([Math]::Round($defaultOpacity * 100))%"
    "world_grid_direct2d=True"
    "world_grid_gdi_fallback=True"
}
finally {
    if ($null -ne $defaultBitmap) { $defaultBitmap.Dispose() }
    if ($null -ne $closeBitmap) { $closeBitmap.Dispose() }
    if ($null -ne $spiralBitmap) { $spiralBitmap.Dispose() }
    if ($null -ne $spiralZoomBitmap) { $spiralZoomBitmap.Dispose() }
    if ($null -ne $spiralDistantBitmap) { $spiralDistantBitmap.Dispose() }
    if ($null -ne $spiralGdiBitmap) { $spiralGdiBitmap.Dispose() }
    if ($null -ne $polarBitmap) { $polarBitmap.Dispose() }
    if ($null -ne $polarZoomBitmap) { $polarZoomBitmap.Dispose() }
    if ($null -ne $polarGdiBitmap) { $polarGdiBitmap.Dispose() }
    if ($null -ne $gdiBitmap) { $gdiBitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $stage.IsDisposed) { $stage.Dispose() }
}
