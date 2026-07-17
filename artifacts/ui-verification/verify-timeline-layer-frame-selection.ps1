param(
    [string]$OutputPath = "artifacts\ui-verification\timeline-layer-frame-selection.png"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$scene = [Activator]::CreateInstance($sceneType, $true)
$sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(5, 20))
$timelineModel = $sceneType.GetProperty("Timeline").GetValue($scene)
$tracks = $timelineModel.GetType().GetProperty("Tracks").GetValue($timelineModel)

$timelineType = $assembly.GetType("VectorAnimationEngine.TimelineStrip", $true)
$timeline = $timelineType.GetConstructor(
    $flags,
    $null,
    [Type[]] @($sceneType),
    $null).Invoke(@($scene))
$form = New-Object Windows.Forms.Form
$bitmap = $null
try {
    $form.ShowInTaskbar = $false
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object Drawing.Point(-32000, -32000)
    $form.ClientSize = New-Object Drawing.Size(820, 210)
    $timeline.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($timeline)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()

    $layout = $timelineType.GetMethod("CreateLayout", $flags).Invoke($timeline, @())
    $rowHeight = $timelineType.GetField("RowHeight", $staticFlags).GetRawConstantValue()
    $frameWidth = $timelineType.GetField("FrameCellWidth", $staticFlags).GetRawConstantValue()
    $onMouseDown = $timelineType.GetMethod("OnMouseDown", $flags)
    $onMouseUp = $timelineType.GetMethod("OnMouseUp", $flags)

    $layerPoint = [Drawing.Point]::new(100, $layout.RowTop + 2 * $rowHeight + [int]($rowHeight / 2))
    $onMouseDown.Invoke($timeline, @([Windows.Forms.MouseEventArgs]::new(
        [Windows.Forms.MouseButtons]::Left, 1, $layerPoint.X, $layerPoint.Y, 0)))
    $onMouseUp.Invoke($timeline, @([Windows.Forms.MouseEventArgs]::new(
        [Windows.Forms.MouseButtons]::Left, 1, $layerPoint.X, $layerPoint.Y, 0)))

    $framePoint = [Drawing.Point]::new(
        $layout.TrackLeft + 9 * $frameWidth + [int]($frameWidth / 2),
        $layout.RowTop + 3 * $rowHeight + [int]($rowHeight / 2))
    $onMouseDown.Invoke($timeline, @([Windows.Forms.MouseEventArgs]::new(
        [Windows.Forms.MouseButtons]::Left, 1, $framePoint.X, $framePoint.Y, 0)))
    $onMouseUp.Invoke($timeline, @([Windows.Forms.MouseEventArgs]::new(
        [Windows.Forms.MouseButtons]::Left, 1, $framePoint.X, $framePoint.Y, 0)))
    [Windows.Forms.Application]::DoEvents()

    $activeTrack = $timelineType.GetProperty("ActiveTrackIndex").GetValue($timeline)
    $selectedLayers = $timelineType.GetProperty("SelectedLayerTargetIds").GetValue($timeline)
    $selectedFrames = $timelineType.GetProperty("SelectedFrameCells").GetValue($timeline)
    if ($activeTrack -ne 3 -or
        $selectedLayers.Count -ne 1 -or
        $selectedLayers[0] -ne $tracks[3].TargetId -or
        $selectedFrames.Count -ne 1 -or
        $selectedFrames[0].TrackId -ne $tracks[3].Id -or
        $selectedFrames[0].Frame -ne 9) {
        throw "Frame selection left the layer highlight state inconsistent."
    }

    $bitmap = New-Object Drawing.Bitmap($timeline.Width, $timeline.Height)
    $timeline.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $timeline.Width, $timeline.Height)))
    $resolvedOutput = Join-Path $root $OutputPath
    $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)

    "timeline_active_track=$activeTrack"
    "timeline_selected_layers=$($selectedLayers.Count)"
    "timeline_selected_frame=$($selectedFrames[0].Frame)"
    "timeline_layer_frame_selection_screenshot=$resolvedOutput"
}
finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $timeline.IsDisposed) { $timeline.Dispose() }
}
