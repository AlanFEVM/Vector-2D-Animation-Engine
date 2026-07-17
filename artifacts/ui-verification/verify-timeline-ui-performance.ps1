param(
    [int]$FrameSwitches = 240,
    [int]$LayerSwitches = 160,
    [string]$OutputPath = "artifacts\ui-verification\timeline-ui-performance.png"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$scene = [Activator]::CreateInstance($sceneType, $true)
$sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(80, 240))

$timelineType = $assembly.GetType("VectorAnimationEngine.TimelineStrip", $true)
$timeline = $timelineType.GetConstructor(
    $flags,
    $null,
    [Type[]] @($sceneType),
    $null).Invoke(@($scene))

$form = New-Object Windows.Forms.Form
$bitmap = $null
$invalidations = [Collections.Generic.List[Drawing.Rectangle]]::new()
$boundsChanges = 0
$invalidatedHandler = [Windows.Forms.InvalidateEventHandler] {
    param($sender, $eventArgs)
    $invalidations.Add($eventArgs.InvalidRect)
}
$boundsChangedHandler = [EventHandler] { $script:boundsChanges++ }

try {
    $form.ShowInTaskbar = $false
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object Drawing.Point(-32000, -32000)
    $form.ClientSize = New-Object Drawing.Size(1100, 480)
    $timeline.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($timeline)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()

    $timeline.add_Invalidated($invalidatedHandler)
    foreach ($control in $timeline.Controls) {
        $control.add_LocationChanged($boundsChangedHandler)
        $control.add_SizeChanged($boundsChangedHandler)
    }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    for ($index = 0; $index -lt $FrameSwitches; $index++) {
        $timelineType.GetProperty("CurrentFrame").SetValue($timeline, 1 + ($index % 28))
        [Windows.Forms.Application]::DoEvents()
    }
    for ($index = 0; $index -lt $LayerSwitches; $index++) {
        $timelineType.GetProperty("ActiveTrackIndex").SetValue($timeline, $index % 16)
        [Windows.Forms.Application]::DoEvents()
    }
    $watch.Stop()

    $fullInvalidations = @($invalidations | Where-Object {
        $_.X -le 0 -and $_.Y -le 0 -and $_.Width -ge $timeline.ClientSize.Width -and $_.Height -ge $timeline.ClientSize.Height
    }).Count
    if ($fullInvalidations -ne 0) {
        throw "Frame/layer switching invalidated the full timeline $fullInvalidations time(s)."
    }
    if ($boundsChanges -ne 0) {
        throw "Timeline header controls changed bounds $boundsChanges time(s) during frame/layer switching."
    }

    $bitmap = New-Object Drawing.Bitmap($timeline.Width, $timeline.Height)
    $timeline.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $timeline.Width, $timeline.Height)))
    $resolvedOutput = Join-Path $root $OutputPath
    $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)

    "timeline_ui_switches=$($FrameSwitches + $LayerSwitches)"
    "timeline_ui_elapsed_ms=$($watch.Elapsed.TotalMilliseconds.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture))"
    "timeline_ui_full_invalidations=$fullInvalidations"
    "timeline_ui_header_bounds_changes=$boundsChanges"
    "timeline_ui_performance_screenshot=$resolvedOutput"
}
finally {
    $timeline.remove_Invalidated($invalidatedHandler)
    foreach ($control in $timeline.Controls) {
        $control.remove_LocationChanged($boundsChangedHandler)
        $control.remove_SizeChanged($boundsChangedHandler)
    }
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $timeline.IsDisposed) { $timeline.Dispose() }
}
