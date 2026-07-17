$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ShapeGradientCaptureNative {
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"
$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$stageType = $assembly.GetType("VectorAnimationEngine.StageControl", $true)
$scene = [Activator]::CreateInstance($sceneType, $true)
$sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(1, 24))

$brushPath = [Drawing.PointF[]] @(
    [Drawing.PointF]::new(-210, 20),
    [Drawing.PointF]::new(-130, -115),
    [Drawing.PointF]::new(75, -135),
    [Drawing.PointF]::new(205, -35),
    [Drawing.PointF]::new(155, 105),
    [Drawing.PointF]::new(-70, 130),
    [Drawing.PointF]::new(-155, 55),
    [Drawing.PointF]::new(105, 20)
)
$brushShapeType = $assembly.GetType("VectorAnimationEngine.BrushShape", $true)
$tipKindType = $assembly.GetType("VectorAnimationEngine.TraditionalBrushTipKind", $true)
$roundTip = [Enum]::Parse($tipKindType, "Round")
$brushShape = $brushShapeType.GetMethod("CreateTraditionalBrush", $staticFlags).Invoke(
    $null,
    @($roundTip, 100, 0))
$addBrush = $sceneType.GetMethods($flags) |
    Where-Object { $_.Name -eq "AddSoftBrushStroke" -and $_.GetParameters().Count -eq 8 } |
    Select-Object -First 1
$objects = $addBrush.Invoke($scene, [object[]] @(
    0, $brushPath, [single]72, [Drawing.Color]::Gold, $brushShape, [uint32]64, 8, $true))
$objectIndex = $objects[0]
$mapping = $sceneType.GetMethod("GetObjectBoundaryContours", $flags).Invoke($scene, @($objectIndex))
$points = @($mapping | ForEach-Object { $_ })
$left = ($points | Measure-Object -Property X -Minimum).Minimum
$right = ($points | Measure-Object -Property X -Maximum).Maximum
$top = ($points | Measure-Object -Property Y -Minimum).Minimum
$bottom = ($points | Measure-Object -Property Y -Maximum).Maximum
$center = [Drawing.PointF]::new([single](($left + $right) * 0.5), [single](($top + $bottom) * 0.5))
$sceneType.GetMethod("SetShapeRadialGradient", $flags).Invoke(
    $scene,
    [object[]] @($objectIndex, [Drawing.Color]::Gold, [Drawing.Color]::RoyalBlue, $center))
$sceneType.GetMethod("SetShapeGradientMapping", $flags).Invoke($scene, @($objectIndex, $mapping))

$stage = [Activator]::CreateInstance(
    $stageType,
    $flags,
    $null,
    [object[]] @($scene),
    $null)
$form = New-Object Windows.Forms.Form
$bitmap = $null
try {
    $form.ShowInTaskbar = $false
    $form.TopMost = $true
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = [Drawing.Point]::new(60, 60)
    $form.ClientSize = [Drawing.Size]::new(520, 400)
    $stage.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($stage)
    $stageType.GetMethod("SetVisibleWorldWidth", $flags).Invoke($stage, @([single]620))
    $form.Show()
    $form.Activate()
    $form.BringToFront()
    for ($index = 0; $index -lt 12; $index++) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 16
    }
    $stage.Invalidate()
    $stage.Update()
    [Windows.Forms.Application]::DoEvents()

    $usedDirect2D = $stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)
    if (-not $usedDirect2D) { throw "Shape radial Stage verification did not use Direct2D." }

    $bitmap = [Drawing.Bitmap]::new($stage.ClientSize.Width, $stage.ClientSize.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try {
            if (-not [ShapeGradientCaptureNative]::PrintWindow($form.Handle, $hdc, 2)) {
                throw "Could not capture the shape radial Stage HWND."
            }
        } finally {
            $graphics.ReleaseHdc($hdc)
        }
    } finally {
        $graphics.Dispose()
    }

    $distinct = [Collections.Generic.HashSet[int]]::new()
    for ($y = 0; $y -lt $bitmap.Height; $y += 4) {
        for ($x = 0; $x -lt $bitmap.Width; $x += 4) {
            $distinct.Add($bitmap.GetPixel($x, $y).ToArgb()) | Out-Null
        }
    }
    if ($distinct.Count -lt 32) { throw "Shape radial Stage capture did not contain a visible gradient." }

    $output = Join-Path $root "artifacts\ui-verification\shape-radial-stage.png"
    $bitmap.Save($output, [Drawing.Imaging.ImageFormat]::Png)
    "shape_radial_direct2d=True"
    "shape_radial_distinct_colors=$($distinct.Count)"
    "shape_radial_stage_screenshot=$output"
} finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $stage.IsDisposed) { $stage.Dispose() }
}
