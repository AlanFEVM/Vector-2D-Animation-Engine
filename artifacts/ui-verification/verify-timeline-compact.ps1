param(
    [int]$Width = 1100,
    [int]$Height = 270,
    [string]$OutputPath = "artifacts\ui-verification\timeline-compact-onion-handle.png"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$instanceFlags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

$localizationType = $assembly.GetType("VectorAnimationEngine.UiLocalization", $true)
$languageType = $assembly.GetType("VectorAnimationEngine.UiLanguage", $true)
$chinese = [Enum]::Parse($languageType, "SimplifiedChinese")
$localizationType.GetMethod("SetLanguage").Invoke($null, @($chinese))

$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$scene = [Activator]::CreateInstance($sceneType, $true)
$sceneType.GetMethod("CreateEmpty", $instanceFlags).Invoke($scene, @(6, 48))
$sceneType.GetMethod("ToggleLayerOnionSkin", $instanceFlags).Invoke($scene, @(0)) | Out-Null
$sceneType.GetMethod("SetOnionSkinRange", $instanceFlags).Invoke($scene, @(3, 4)) | Out-Null

$timelineType = $assembly.GetType("VectorAnimationEngine.TimelineStrip", $true)
$timeline = $timelineType.GetConstructor(
    $instanceFlags,
    $null,
    [Type[]] @($sceneType),
    $null).Invoke(@($scene))
$timelineType.GetProperty("ActiveTrackIndex").SetValue($timeline, 0)
$timelineType.GetProperty("CurrentFrame").SetValue($timeline, 12)

$watch = $localizationType.GetMethod(
    "Watch",
    [Reflection.BindingFlags] "Public,Static",
    $null,
    [Type[]] @([Windows.Forms.Control]),
    $null)
$watch.Invoke($null, @($timeline))

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
    $timeline.PerformLayout()
    [Windows.Forms.Application]::DoEvents()

    $layout = $timelineType.GetMethod("CreateLayout", $instanceFlags).Invoke($timeline, @())
    $handleType = $assembly.GetType("VectorAnimationEngine.TimelineOnionSkinRangeHandle", $true)
    $nextHandle = [Enum]::Parse($handleType, "Next")
    $noneHandle = [Enum]::Parse($handleType, "None")
    $bounds = [Drawing.Rectangle]$timelineType.GetMethod("OnionSkinRangeHandleBounds", $instanceFlags).Invoke(
        $timeline,
        @($nextHandle, $layout))
    if ($bounds.Width -ne 10 -or $bounds.Height -ne 10) {
        throw "The onion-skin range handle is not using the compact 10px hit target: $bounds"
    }

    $hitTest = $timelineType.GetMethod("HitTestOnionSkinRangeHandle", $instanceFlags)
    $handlePoint = [Drawing.Point]::new($bounds.Left + 5, $bounds.Top + 5)
    $rulerPreviewPoint = [Drawing.Point]::new($bounds.Left + 5, 40)
    $handleHit = $hitTest.Invoke($timeline, @($handlePoint, $layout))
    $rulerPreviewHit = $hitTest.Invoke($timeline, @($rulerPreviewPoint, $layout))
    if ($handleHit -ne $nextHandle -or $rulerPreviewHit -ne $noneHandle) {
        throw "The onion-skin handle hit target still intercepts the ruler preview area."
    }

    $frameCellWidth = $timelineType.GetField("FrameCellWidth", $staticFlags).GetRawConstantValue()
    $rowHeight = $timelineType.GetField("RowHeight", $staticFlags).GetRawConstantValue()
    if ($frameCellWidth -ne 14 -or $rowHeight -ne 21) {
        throw "The timeline cells are not using the compact 14x21 metrics."
    }

    $timelineType.GetMethod("OnMouseMove", $instanceFlags).Invoke(
        $timeline,
        @([Windows.Forms.MouseEventArgs]::new(
            [Windows.Forms.MouseButtons]::None,
            0,
            $bounds.Left + 5,
            $bounds.Top + 5,
            0)))
    [Windows.Forms.Application]::DoEvents()

    $bitmap = New-Object Drawing.Bitmap($Width, $Height)
    $timeline.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $Width, $Height)))
    $resolvedOutput = Join-Path $root $OutputPath
    $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)

    "timeline_frame_cell_width=$frameCellWidth"
    "timeline_row_height=$rowHeight"
    "timeline_onion_handle_bounds=$bounds"
    "timeline_onion_preview_area_hit=$rulerPreviewHit"
    "timeline_compact_screenshot=$resolvedOutput"
}
finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $timeline.IsDisposed) { $timeline.Dispose() }
}
