$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ShapeBrushPreviewCaptureNative {
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"
$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$gradientKindType = $assembly.GetType("VectorAnimationEngine.GradientKind", $true)
$gradientStopType = $assembly.GetType("VectorAnimationEngine.GradientStop", $true)
$toolModeType = $assembly.GetType("VectorAnimationEngine.ToolMode", $true)

$main = [Activator]::CreateInstance($mainType, $true)
$bitmap = $null
try {
    $main.ShowInTaskbar = $false
    $main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $main.Location = [Drawing.Point]::new(40, 40)
    $main.Size = [Drawing.Size]::new(1180, 760)
    $main.Show()
    [Windows.Forms.Application]::DoEvents()

    $scene = $mainType.GetField("_scene", $flags).GetValue($main)
    $stage = $mainType.GetField("_stage", $flags).GetValue($main)
    $material = $mainType.GetField("_materialEditor", $flags).GetValue($main)
    $brushShape = $mainType.GetField("_brushShape", $flags).GetValue($main)
    $sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(1, 24))
    $stage.GetType().GetMethod("BindScene", $flags).Invoke($stage, @($scene))
    $stage.GetType().GetMethod("SetVisibleWorldWidth", $flags).Invoke($stage, @([single]1100))

    $color = [Drawing.Color]::MediumTurquoise
    $material.GetType().GetProperty("Fill", $flags).SetValue($material, $color)
    $stops = [Array]::CreateInstance($gradientStopType, 3)
    $stops.SetValue([Activator]::CreateInstance($gradientStopType, @([single]0, [Drawing.Color]::MediumTurquoise)), 0)
    $stops.SetValue([Activator]::CreateInstance($gradientStopType, @([single]0.5, [Drawing.Color]::PaleTurquoise)), 1)
    $stops.SetValue([Activator]::CreateInstance($gradientStopType, @([single]1, [Drawing.Color]::White)), 2)
    $shapeRadial = [Enum]::Parse($gradientKindType, "ShapeRadial")
    $material.GetType().GetMethods($flags) |
        Where-Object { $_.Name -eq "SetGradient" -and $_.GetParameters().Count -eq 2 -and $_.GetParameters()[0].ParameterType -eq $gradientKindType } |
        Select-Object -First 1 |
        ForEach-Object { $_.Invoke($material, @($shapeRadial, $stops)) | Out-Null }

    $firstPath = [Drawing.PointF[]] @(
        [Drawing.PointF]::new(-360, -40),
        [Drawing.PointF]::new(-210, -130),
        [Drawing.PointF]::new(-50, -40),
        [Drawing.PointF]::new(80, 40)
    )
    $secondPath = [Drawing.PointF[]] @(
        [Drawing.PointF]::new(-80, 20),
        [Drawing.PointF]::new(70, -70),
        [Drawing.PointF]::new(230, 20),
        [Drawing.PointF]::new(360, 100)
    )
    $diameter = [single]190
    $addBrush = $sceneType.GetMethods($flags) |
        Where-Object { $_.Name -eq "AddSoftBrushStroke" -and $_.GetParameters().Count -eq 8 } |
        Select-Object -First 1
    $firstObjects = $addBrush.Invoke($scene, [object[]] @(0, $firstPath, $diameter, $color, $brushShape, [uint32]32, 8, $true))

    $mainType.GetField("_freehandColor", $flags).SetValue($main, $color)
    $mainType.GetField("_freehandStrokeUnits", $flags).SetValue($main, $diameter)
    $applyGradient = $mainType.GetMethods($flags) |
        Where-Object { $_.Name -eq "ApplyFillGradientToBrushObjects" -and $_.GetParameters().Count -eq 4 } |
        Select-Object -First 1
    $applyGradient.Invoke($main, @($firstObjects, $firstPath, $firstPath[0], $firstPath[-1])) | Out-Null

    $samples = $mainType.GetField("_freehandSamples", $flags).GetValue($main)
    $samples.Clear()
    foreach ($point in $secondPath) { $samples.Add($point) }
    $mainType.GetField("_freehandDrawing", $flags).SetValue($main, $true)
    $mainType.GetField("_freehandBrushStroke", $flags).SetValue($main, $true)
    $mainType.GetField("_freehandPressureBrush", $flags).SetValue($main, $false)
    $mainType.GetField("_freehandErasing", $flags).SetValue($main, $false)
    $mainType.GetField("_tool", $flags).SetValue($main, [Enum]::Parse($toolModeType, "Brush"))
    $mainType.GetMethod("UpdateFreehandPreview", $flags).Invoke($main, @()) | Out-Null
    for ($index = 0; $index -lt 10; $index++) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 16
    }

    $preview = $stage.GetType().GetProperty("DragPreviewScene", $flags).GetValue($stage)
    $freehandVisible = $stage.GetType().GetProperty("FreehandPreviewVisible", $flags).GetValue($stage)
    $hidden = $stage.GetType().GetMethod("IsHiddenByDragPreview", $flags).Invoke($stage, @($scene, $firstObjects[0]))
    $direct2D = $stage.GetType().GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)
    if ($null -eq $preview -or $preview.ObjectCount -ne 1 -or $freehandVisible -or -not $hidden -or -not $direct2D) {
        throw "The live shape-gradient brush preview did not replace the matching source through Direct2D."
    }

    $bitmap = [Drawing.Bitmap]::new($main.Width, $main.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try {
            if (-not [ShapeBrushPreviewCaptureNative]::PrintWindow($main.Handle, $hdc, 2)) {
                throw "Could not capture the live shape-gradient brush preview."
            }
        } finally {
            $graphics.ReleaseHdc($hdc)
        }
    } finally {
        $graphics.Dispose()
    }

    $output = Join-Path $root "artifacts\ui-verification\shape-gradient-brush-live-preview.png"
    $bitmap.Save($output, [Drawing.Imaging.ImageFormat]::Png)
    "shape_gradient_live_preview=True"
    "shape_gradient_preview_objects=$($preview.ObjectCount)"
    "shape_gradient_preview_source_hidden=$hidden"
    "shape_gradient_preview_direct2d=$direct2D"
    "shape_gradient_preview_screenshot=$output"
} finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
