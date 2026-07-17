param(
    [int]$Width = 760,
    [int]$Height = 220,
    [string]$OutputPath = "artifacts\ui-verification\timeline-selection-overlay.png"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$instanceFlags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$scene = [Activator]::CreateInstance($sceneType, $true)
$sceneType.GetMethod("CreateEmpty", $instanceFlags).Invoke($scene, @(6, 48))

$timelineType = $assembly.GetType("VectorAnimationEngine.TimelineStrip", $true)
$timeline = $timelineType.GetConstructor(
    $instanceFlags,
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
    $form.ClientSize = New-Object Drawing.Size($Width, $Height)
    $timeline.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($timeline)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()

    $layout = $timelineType.GetMethod("CreateLayout", $instanceFlags).Invoke($timeline, @())
    $frameCellWidth = $timelineType.GetField("FrameCellWidth", $staticFlags).GetRawConstantValue()
    $rowHeight = $timelineType.GetField("RowHeight", $staticFlags).GetRawConstantValue()
    $start = [Drawing.Point]::new($layout.TrackLeft + 4 * $frameCellWidth + 7, $layout.RowTop + $rowHeight + 10)
    $end = [Drawing.Point]::new($layout.TrackLeft + 10 * $frameCellWidth + 7, $layout.RowTop + 3 * $rowHeight + 10)

    $timelineType.GetMethod("OnMouseDown", $instanceFlags).Invoke(
        $timeline,
        @([Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Left, 1, $start.X, $start.Y, 0)))
    $timelineType.GetMethod("OnMouseMove", $instanceFlags).Invoke(
        $timeline,
        @([Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Left, 0, $end.X, $end.Y, 0)))
    $timelineType.GetMethod("OnMouseUp", $instanceFlags).Invoke(
        $timeline,
        @([Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Left, 1, $end.X, $end.Y, 0)))
    [Windows.Forms.Application]::DoEvents()

    $selected = $timelineType.GetProperty("SelectedFrameCells").GetValue($timeline)
    if ($selected.Count -ne 21) {
        throw "The drag selection did not retain its 3x7 frame cells: $($selected.Count)"
    }

    $bitmap = New-Object Drawing.Bitmap($Width, $Height)
    $timeline.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $Width, $Height)))
    $resolvedOutput = Join-Path $root $OutputPath
    $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)

    "timeline_selected_cells=$($selected.Count)"
    "timeline_selection_screenshot=$resolvedOutput"
}
finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $timeline.IsDisposed) { $timeline.Dispose() }
}
