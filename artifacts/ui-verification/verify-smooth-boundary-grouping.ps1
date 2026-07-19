$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SmoothBoundaryCaptureNative {
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
            if (-not [SmoothBoundaryCaptureNative]::PrintWindow($form.Handle, $hdc, 2)) {
                throw "Could not capture the smooth boundary verification HWND."
            }
        } finally {
            $graphics.ReleaseHdc($hdc)
        }
    } finally {
        $graphics.Dispose()
    }
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$stageType = $assembly.GetType("VectorAnimationEngine.StageControl", $true)
$keyType = $assembly.GetType("VectorAnimationEngine.DrawingElementKey", $true)
$hitType = $assembly.GetType("VectorAnimationEngine.DrawingElementHit", $true)
$elementKindType = $assembly.GetType("VectorAnimationEngine.DrawingElementKind", $true)
$boundaryKind = [Enum]::Parse($elementKindType, "BoundaryStroke")

$scene = [Activator]::CreateInstance($sceneType, $true)
$scene.CreateEmpty(1, 24)
$contour = [Collections.Generic.List[Drawing.PointF]]::new()
$contour.Add([Drawing.PointF]::new(-240, -140))
$contour.Add([Drawing.PointF]::new(160, -140))
$curveStart = $contour[$contour.Count - 1]
$control1 = [Drawing.PointF]::new(160, -40)
$control2 = [Drawing.PointF]::new(260, 40)
$curveEnd = [Drawing.PointF]::new(160, 140)
for ($sample = 1; $sample -le 16; $sample++) {
    $t = $sample / 16.0
    $inverse = 1 - $t
    $contour.Add([Drawing.PointF]::new(
        $curveStart.X * $inverse * $inverse * $inverse +
            3 * $control1.X * $inverse * $inverse * $t +
            3 * $control2.X * $inverse * $t * $t +
            $curveEnd.X * $t * $t * $t,
        $curveStart.Y * $inverse * $inverse * $inverse +
            3 * $control1.Y * $inverse * $inverse * $t +
            3 * $control2.Y * $inverse * $t * $t +
            $curveEnd.Y * $t * $t * $t))
}
$contour.Add([Drawing.PointF]::new(-200, 140))
$contour.Add($contour[0])

$contours = [Array]::CreateInstance([Drawing.PointF[]], 1)
$contours.SetValue($contour.ToArray(), 0)
$path = $scene.AddPathObjectContours(
    0,
    $contours,
    [single]16,
    [Drawing.Color]::FromArgb(255, 79, 179, 162),
    [Drawing.Color]::White,
    [uint32]32)
$parts = $scene.GetBoundaryParts($path, 0)
$smoothPart = $parts | Sort-Object { $_.Points.Length } -Descending | Select-Object -First 1
if ($parts.Count -ne 4 -or $smoothPart.Points.Length -lt 12) {
    throw "Smooth boundary grouping failed: path=$path, objects=$($scene.ObjectCount), stroke=$($scene.Stroke[$path]), parts=$($parts.Count), longest=$($smoothPart.Points.Length)."
}

$hitValues = @($parts | ForEach-Object {
    $key = [Activator]::CreateInstance($keyType, $flags, $null, [object[]]@($path, $boundaryKind, $_.PartIndex), $null)
    [Activator]::CreateInstance($hitType, $flags, $null, [object[]]@($key, [single]0, [single]0, [single]1), $null)
})
$hits = [Array]::CreateInstance($hitType, $hitValues.Count)
for ($index = 0; $index -lt $hitValues.Count; $index++) { $hits.SetValue($hitValues[$index], $index) }
$primary = $hits.GetValue($smoothPart.PartIndex)
$stage = [Activator]::CreateInstance($stageType, $flags, $null, [object[]]@($scene), $null)
$form = [Windows.Forms.Form]::new()
try {
    $form.ShowInTaskbar = $false
    $form.TopMost = $true
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = [Drawing.Point]::new(60, 60)
    $form.ClientSize = [Drawing.Size]::new(760, 500)
    $stage.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($stage)
    $stage.SetVisibleWorldWidth([single]650)
    $stage.SetSelection([int[]]@($path), $path)
    $stage.SetSelectedElements($hits, $primary)
    $form.Show()
    $form.Activate()
    $form.BringToFront()
    Pump-Ui 12

    if (-not $stage.LastFrameUsedDirect2D) {
        throw "Smooth boundary verification did not use Direct2D."
    }
    $bezierArguments = [object[]]@($primary, [Drawing.PointF]::Empty, [Drawing.PointF]::Empty, [Drawing.PointF]::Empty, [Drawing.PointF]::Empty)
    if ($stageType.GetMethod("TryGetEditableBezierWorldPoints", $flags).Invoke($stage, $bezierArguments)) {
        throw "A sampled smooth boundary was still exposed as a false straight Bezier segment."
    }

    $renderer = $stageType.GetField("_direct2DRenderer", $flags).GetValue($stage)
    $renderer.GetType().GetField("_disabled", $flags).SetValue($renderer, $true)
    $stage.Invalidate()
    Pump-Ui 6
    $output = Join-Path $root "artifacts\ui-verification\smooth-boundary-grouping-gdi.png"
    Capture-Window $form $output

    "smooth_boundary_parts=$($parts.Count)"
    "smooth_boundary_curve_samples=$($smoothPart.Points.Length)"
    "smooth_boundary_direct2d=state-ok"
    "smooth_boundary_gdi=$output"
}
finally {
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $stage.IsDisposed) { $stage.Dispose() }
}
