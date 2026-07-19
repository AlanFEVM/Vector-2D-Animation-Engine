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
$commandType = $assembly.GetType("VectorAnimationEngine.TimelineCommand", $true)
$cellType = $assembly.GetType("VectorAnimationEngine.TimelineFrameCell", $true)
$scene = [Activator]::CreateInstance($sceneType, $true)
$sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(1, 24))
$model = $sceneType.GetProperty("Timeline", $flags).GetValue($scene)
$track = $model.GetType().GetProperty("Tracks", $flags).GetValue($model)[0]
$model.SetTrackDuration($track.Id, 24) | Out-Null
$timeline = [Activator]::CreateInstance($timelineType, $flags, $null, [object[]] @($scene), $null)
$playFeedback = $timelineType.GetMethod("PlayCommandFeedback", $flags)
$form = New-Object Windows.Forms.Form
$baseline = $null
$captures = New-Object System.Collections.Generic.List[Drawing.Bitmap]

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

    $baseline = Capture-Control $timeline
    $sampleBounds = [Drawing.Rectangle]::new(226, 54, 210, 42)
    $commands = [Enum]::GetValues($commandType)
    $pixelCounts = @{}

    foreach ($command in $commands) {
        $cells = [Array]::CreateInstance($cellType, 3)
        for ($index = 0; $index -lt 3; $index++) {
            $cell = [Activator]::CreateInstance(
                $cellType,
                $flags,
                $null,
                [object[]] @([string]$track.Id, [int](2 + $index)),
                $null)
            $cells.SetValue($cell, $index)
        }

        $playFeedback.Invoke($timeline, [object[]] @($command, $cells))
        Pump-Ui 88
        $capture = Capture-Control $timeline
        $changed = Count-ChangedPixels $baseline $capture $sampleBounds
        if ($changed -lt 8) {
            throw "Timeline feedback was not visible for $command`: changed_pixels=$changed"
        }
        $pixelCounts[$command.ToString()] = $changed
        $captures.Add($capture)
    }

    Pump-Ui 520
    $activeFeedback = $timelineType.GetField("_commandFeedback", $flags).GetValue($timeline)
    $feedbackTimer = $timelineType.GetField("_commandFeedbackTimer", $flags).GetValue($timeline)
    if ($null -ne $activeFeedback -or $feedbackTimer.Enabled) {
        throw "Timeline feedback did not settle and stop its animation timer."
    }

    $cellWidth = 450
    $cellHeight = 92
    $columns = 2
    $rows = [int][Math]::Ceiling($captures.Count / $columns)
    $contact = [Drawing.Bitmap]::new($cellWidth * $columns, $cellHeight * $rows)
    $graphics = [Drawing.Graphics]::FromImage($contact)
    try {
        $graphics.Clear([Drawing.Color]::FromArgb(18, 20, 22))
        $font = [Drawing.Font]::new("Segoe UI", 9, [Drawing.FontStyle]::Bold)
        $labelBrush = [Drawing.SolidBrush]::new([Drawing.Color]::White)
        try {
            for ($index = 0; $index -lt $captures.Count; $index++) {
                $column = $index % $columns
                $row = [int][Math]::Floor($index / [double]$columns)
                $left = $column * $cellWidth
                $top = $row * $cellHeight
                $label = $commands[$index].ToString()
                $graphics.DrawString("$label  pixels=$($pixelCounts[$label])", $font, $labelBrush, $left + 8, $top + 4)
                $destination = [Drawing.Rectangle]::new($left + 8, $top + 24, 420, 60)
                $graphics.DrawImage($captures[$index], $destination, $sampleBounds, [Drawing.GraphicsUnit]::Pixel)
            }
        } finally {
            $labelBrush.Dispose()
            $font.Dispose()
        }
    } finally {
        $graphics.Dispose()
    }

    $output = Join-Path $root "artifacts\ui-verification\timeline-command-feedback.png"
    $contact.Save($output, [Drawing.Imaging.ImageFormat]::Png)
    $contact.Dispose()
    "timeline_command_feedback=$output"
    foreach ($command in $commands) {
        "timeline_feedback_$($command.ToString())_pixels=$($pixelCounts[$command.ToString()])"
    }
    "timeline_feedback_timer_settled=True"
}
finally {
    if ($null -ne $baseline) { $baseline.Dispose() }
    foreach ($capture in $captures) { $capture.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    $timeline.Dispose()
}
