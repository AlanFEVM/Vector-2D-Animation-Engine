$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

function Pump-Ui([int]$milliseconds) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $milliseconds) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 8
    }
    [Windows.Forms.Application]::DoEvents()
}

function Capture-Control($control) {
    $bitmap = [Drawing.Bitmap]::new($control.Width, $control.Height)
    $control.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
    return $bitmap
}

function Count-ChangedPixels($baseline, $candidate, [Drawing.Rectangle]$bounds) {
    $changed = 0
    for ($y = $bounds.Top; $y -lt $bounds.Bottom; $y++) {
        for ($x = $bounds.Left; $x -lt $bounds.Right; $x++) {
            $before = $baseline.GetPixel($x, $y)
            $after = $candidate.GetPixel($x, $y)
            $delta = [Math]::Abs($before.R - $after.R) +
                [Math]::Abs($before.G - $after.G) +
                [Math]::Abs($before.B - $after.B)
            if ($delta -gt 18) { $changed++ }
        }
    }
    return $changed
}

$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$timelineType = $assembly.GetType("VectorAnimationEngine.TimelineStrip", $true)
$scene = [Activator]::CreateInstance($sceneType, $true)
$sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(1, 24))
$timeline = [Activator]::CreateInstance($timelineType, $flags, $null, [object[]] @($scene), $null)
$form = New-Object Windows.Forms.Form
$initial = $null
$added = $null
$twoLayerBaseline = $null
$removed = $null

try {
    $form.ShowInTaskbar = $false
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = [Drawing.Point]::new(40, 40)
    $form.ClientSize = [Drawing.Size]::new(900, 240)
    $timeline.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($timeline)
    $form.Show()
    Pump-Ui 180

    $sampleBounds = [Drawing.Rectangle]::new(0, 58, 900, 54)
    $initial = Capture-Control $timeline
    $scene.AddLayer("Animated Add") | Out-Null
    Pump-Ui 92
    $added = Capture-Control $timeline
    $addPixels = Count-ChangedPixels $initial $added $sampleBounds
    $layerTargets = $timelineType.GetField("_layerFeedbackTargets", $flags).GetValue($timeline)
    if ($addPixels -lt 80 -or $layerTargets.Count -ne 1 -or [string]$layerTargets[0].Kind -ne "Add") {
        throw "Timeline layer addition feedback was not detected: pixels=$addPixels targets=$($layerTargets.Count)."
    }

    Pump-Ui 420
    $twoLayerBaseline = Capture-Control $timeline
    $model = $sceneType.GetProperty("Timeline", $flags).GetValue($scene)
    $model.Clear()
    Pump-Ui 82
    $removed = Capture-Control $timeline
    $removePixels = Count-ChangedPixels $twoLayerBaseline $removed $sampleBounds
    $layerTargets = $timelineType.GetField("_layerFeedbackTargets", $flags).GetValue($timeline)
    if ($removePixels -lt 80 -or $layerTargets.Count -ne 2 -or @($layerTargets | Where-Object { [string]$_.Kind -ne "Remove" }).Count -ne 0) {
        throw "Timeline layer removal feedback did not preserve the removed rows: pixels=$removePixels targets=$($layerTargets.Count)."
    }

    $contact = [Drawing.Bitmap]::new(900, 152)
    $graphics = [Drawing.Graphics]::FromImage($contact)
    try {
        $graphics.Clear([Drawing.Color]::FromArgb(18, 20, 22))
        $font = [Drawing.Font]::new("Segoe UI", 9, [Drawing.FontStyle]::Bold)
        $labelBrush = [Drawing.SolidBrush]::new([Drawing.Color]::White)
        try {
            $graphics.DrawString("Add layer  pixels=$addPixels", $font, $labelBrush, 8, 4)
            $graphics.DrawImage($added, [Drawing.Rectangle]::new(0, 24, 900, 54), $sampleBounds, [Drawing.GraphicsUnit]::Pixel)
            $graphics.DrawString("Remove layer  pixels=$removePixels", $font, $labelBrush, 8, 80)
            $graphics.DrawImage($removed, [Drawing.Rectangle]::new(0, 98, 900, 54), $sampleBounds, [Drawing.GraphicsUnit]::Pixel)
        } finally {
            $labelBrush.Dispose()
            $font.Dispose()
        }
    } finally {
        $graphics.Dispose()
    }

    $output = Join-Path $root "artifacts\ui-verification\timeline-layer-feedback.png"
    $contact.Save($output, [Drawing.Imaging.ImageFormat]::Png)
    $contact.Dispose()

    Pump-Ui 360
    $layerTimer = $timelineType.GetField("_layerFeedbackTimer", $flags).GetValue($timeline)
    $layerTargets = $timelineType.GetField("_layerFeedbackTargets", $flags).GetValue($timeline)
    if ($layerTimer.Enabled -or $layerTargets.Count -ne 0) {
        throw "Timeline layer feedback did not settle and stop its timer."
    }

    "timeline_layer_feedback=$output"
    "timeline_layer_add_pixels=$addPixels"
    "timeline_layer_remove_pixels=$removePixels"
    "timeline_layer_feedback_timer_settled=True"
}
finally {
    if ($null -ne $initial) { $initial.Dispose() }
    if ($null -ne $added) { $added.Dispose() }
    if ($null -ne $twoLayerBaseline) { $twoLayerBaseline.Dispose() }
    if ($null -ne $removed) { $removed.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    $timeline.Dispose()
}
