$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class LineJunctionCaptureNative {
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
            if (-not [LineJunctionCaptureNative]::PrintWindow($form.Handle, $hdc, 2)) {
                throw "Could not capture the line-junction Stage HWND."
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

function Measure-HubCoverage($bitmap, [Drawing.PointF]$center, [single]$radius) {
    $colored = 0
    $samples = 0
    $limit = [int][Math]::Floor($radius * 0.8)
    for ($dy = -$limit; $dy -le $limit; $dy++) {
        for ($dx = -$limit; $dx -le $limit; $dx++) {
            if ($dx * $dx + $dy * $dy -gt $limit * $limit) { continue }
            $x = [Math]::Clamp([int][Math]::Round($center.X) + $dx, 0, $bitmap.Width - 1)
            $y = [Math]::Clamp([int][Math]::Round($center.Y) + $dy, 0, $bitmap.Height - 1)
            $color = $bitmap.GetPixel($x, $y)
            $samples++
            if ($color.R -gt 170 -and $color.R -gt ($color.G + 35) -and $color.G -gt 55) { $colored++ }
        }
    }
    return $colored / [double][Math]::Max(1, $samples)
}

function Measure-SharpExtension($bitmap, [Drawing.PointF]$center) {
    $right = [int][Math]::Round($center.X)
    for ($y = 0; $y -lt $bitmap.Height; $y++) {
        for ($x = $right; $x -lt $bitmap.Width; $x++) {
            $color = $bitmap.GetPixel($x, $y)
            if ($color.R -gt 170 -and $color.R -gt ($color.G + 35) -and $color.G -gt 55) {
                $right = [Math]::Max($right, $x)
            }
        }
    }
    return $right - $center.X
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$stageType = $assembly.GetType("VectorAnimationEngine.StageControl", $true)
$endpointStyleType = $assembly.GetType("VectorAnimationEngine.LineEndpointStyle", $true)
$sharp = [Enum]::Parse($endpointStyleType, "Sharp")
$scene = [Activator]::CreateInstance($sceneType, $true)
$sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(1, 24))
$stroke = [single]80
$addLine = $sceneType.GetMethod("AddLineSegment", $flags, $null, [Type[]] @(
    [int], [Drawing.PointF], [Drawing.PointF], [single], [Drawing.Color], [Drawing.Color], [uint32], $endpointStyleType, $endpointStyleType), $null)
foreach ($endpoint in @(
    [Drawing.PointF]::new(-480, -180),
    [Drawing.PointF]::new(-480, 0),
    [Drawing.PointF]::new(-480, 180))) {
    $addLine.Invoke($scene, [object[]] @(
        0,
        [Drawing.PointF]::Empty,
        $endpoint,
        $stroke,
        [Drawing.Color]::Transparent,
        [Drawing.Color]::Coral,
        [uint32]8,
        $sharp,
        $sharp)) | Out-Null
}

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
    $stageType.GetMethod("SetVisibleWorldWidth", $flags).Invoke($stage, @([single]1400))
    $stage.SetSelection([int[]] @(0, 1, 2), 2)
    $form.Show()
    $form.Activate()
    $form.BringToFront()
    Pump-Ui 10

    if (-not $stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)) {
        throw "Line-junction verification did not use Direct2D."
    }
    $center = $stageType.GetMethod("WorldToScreen", $flags, $null, [Type[]] @([single], [single]), $null).Invoke($stage, @([single]0, [single]0))
    $radius = $stageType.GetMethod("WorldLengthToScreen", $flags).Invoke($stage, @([single]($stroke * 0.5)))
    $directPath = Join-Path $root "artifacts\ui-verification\line-junction-direct2d.png"
    $directBitmap = Capture-Stage $form $stage $directPath
    $directCoverage = Measure-HubCoverage $directBitmap $center $radius
    $directExtension = Measure-SharpExtension $directBitmap $center

    $renderer = $stageType.GetField("_direct2DRenderer", $flags).GetValue($stage)
    $renderer.GetType().GetField("_disabled", $flags).SetValue($renderer, $true)
    $stage.Invalidate()
    Pump-Ui 6
    if ($stageType.GetProperty("LastFrameUsedDirect2D", $flags).GetValue($stage)) {
        throw "Line-junction GDI verification did not disable Direct2D."
    }
    $gdiPath = Join-Path $root "artifacts\ui-verification\line-junction-gdi.png"
    $gdiBitmap = Capture-Stage $form $stage $gdiPath
    $gdiCoverage = Measure-HubCoverage $gdiBitmap $center $radius
    $gdiExtension = Measure-SharpExtension $gdiBitmap $center

    if ($directExtension -lt ($radius * 1.25) -or $gdiExtension -lt ($radius * 1.25)) {
        throw "The multi-line sharp junction did not form one pointed envelope in both renderers: D2D=$directExtension GDI=$gdiExtension"
    }

    "line_junction_direct2d=$directPath"
    "line_junction_gdi=$gdiPath"
    "line_junction_hub_coverage_direct2d=$($directCoverage.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "line_junction_hub_coverage_gdi=$($gdiCoverage.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture))"
    "line_junction_tip_extension_direct2d=$($directExtension.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture))"
    "line_junction_tip_extension_gdi=$($gdiExtension.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture))"
}
finally {
    if ($null -ne $directBitmap) { $directBitmap.Dispose() }
    if ($null -ne $gdiBitmap) { $gdiBitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $stage.IsDisposed) { $stage.Dispose() }
}
