param(
    [int]$Width = 1100,
    [int]$Height = 270,
    [int]$FrameWidth = 14,
    [ValidateSet("Low", "Medium", "High")]
    [string]$FrameHeight = "Medium",
    [string]$DisabledOutputPath = "artifacts\ui-verification\timeline-compact-onion-handle-disabled.png",
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
$sceneType.GetMethod("SetOnionSkinRange", $instanceFlags).Invoke($scene, @(3, 4)) | Out-Null

$timelineType = $assembly.GetType("VectorAnimationEngine.TimelineStrip", $true)
$timeline = $timelineType.GetConstructor(
    $instanceFlags,
    $null,
    [Type[]] @($sceneType),
    $null).Invoke(@($scene))
$timelineType.GetProperty("ActiveTrackIndex").SetValue($timeline, 0)
$timelineType.GetProperty("CurrentFrame").SetValue($timeline, 12)
$timelineType.GetProperty("FrameWidth").SetValue($timeline, $FrameWidth)
$frameHeightType = $assembly.GetType("VectorAnimationEngine.TimelineFrameHeightPreset", $true)
$frameHeightPreset = [Enum]::Parse($frameHeightType, $FrameHeight)
$timelineType.GetProperty("FrameHeightPreset").SetValue($timeline, $frameHeightPreset)

$watch = $localizationType.GetMethod(
    "Watch",
    [Reflection.BindingFlags] "Public,Static",
    $null,
    [Type[]] @([Windows.Forms.Control]),
    $null)
$watch.Invoke($null, @($timeline))

$form = New-Object Windows.Forms.Form
$bitmap = $null
$disabledBitmap = $null
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
    $handleBounds = $timelineType.GetMethod("OnionSkinRangeHandleBounds", $instanceFlags)
    $hitTest = $timelineType.GetMethod("HitTestOnionSkinRangeHandle", $instanceFlags)
    $disabledBounds = [Drawing.Rectangle]$handleBounds.Invoke($timeline, @($nextHandle, $layout))
    $disabledHit = $hitTest.Invoke($timeline, @([Drawing.Point]::new(500, 48), $layout))
    if (-not $disabledBounds.IsEmpty -or $disabledHit -ne $noneHandle) {
        throw "The onion-skin range handle remains visible or interactive while onion skin is disabled."
    }

    $disabledBitmap = New-Object Drawing.Bitmap($Width, $Height)
    $timeline.DrawToBitmap($disabledBitmap, (New-Object Drawing.Rectangle(0, 0, $Width, $Height)))
    $resolvedDisabledOutput = Join-Path $root $DisabledOutputPath
    $disabledBitmap.Save($resolvedDisabledOutput, [Drawing.Imaging.ImageFormat]::Png)

    $sceneType.GetMethod("ToggleLayerOnionSkin", $instanceFlags).Invoke($scene, @(0)) | Out-Null
    $timelineType.GetMethod("RefreshOnionSkinControls", $instanceFlags).Invoke($timeline, @())
    [Windows.Forms.Application]::DoEvents()
    $layout = $timelineType.GetMethod("CreateLayout", $instanceFlags).Invoke($timeline, @())
    $bounds = [Drawing.Rectangle]$handleBounds.Invoke(
        $timeline,
        @($nextHandle, $layout))
    if ($bounds.Width -ne 10 -or $bounds.Height -ne 10) {
        throw "The onion-skin range handle is not using the compact 10px hit target: $bounds"
    }

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
    $activeFrameWidth = $timelineType.GetProperty("FrameWidth").GetValue($timeline)
    if ($activeFrameWidth -ne [Math]::Clamp($FrameWidth, 8, 32)) {
        throw "The timeline frame-width setting was not applied: requested=$FrameWidth active=$activeFrameWidth"
    }
    $trackLeft = $layout.GetType().GetProperty("TrackLeft").GetValue($layout)
    $firstVisibleFrame = $timelineType.GetField("_firstVisibleFrame", $instanceFlags).GetValue($timeline)
    $frameFromX = $timelineType.GetMethod("FrameFromX", $instanceFlags)
    $thirdFrameX = [int]$trackLeft + [int]$activeFrameWidth * 3 + [int]($activeFrameWidth / 2)
    $thirdVisibleFrame = $frameFromX.Invoke($timeline, @($thirdFrameX, $layout))
    $expectedThirdVisibleFrame = [int]$firstVisibleFrame + 3
    if ($thirdVisibleFrame -ne $expectedThirdVisibleFrame) {
        throw "Timeline frame hit testing did not use the active frame width: expected=$expectedThirdVisibleFrame actual=$thirdVisibleFrame"
    }
    $frameWidthInput = $timelineType.GetField("_frameWidthInput", $instanceFlags).GetValue($timeline)
    $frameWidthSlider = $timelineType.GetField("_frameWidthSlider", $instanceFlags).GetValue($timeline)
    $fpsLabel = $timelineType.GetField("_playbackFpsLabel", $instanceFlags).GetValue($timeline)
    if ([int]$frameWidthInput.Value -ne $activeFrameWidth -or [int]$frameWidthSlider.Value -ne $activeFrameWidth) {
        throw "Timeline frame-width controls were not synchronized with the active width."
    }
    if ($frameWidthInput.Visible -and $fpsLabel.Visible -and $frameWidthInput.Right -gt $fpsLabel.Left) {
        throw "Timeline frame-width controls overlap the FPS controls."
    }
    $expectedFrameHeight = switch ($FrameHeight) {
        "Low" { 16 }
        "High" { 28 }
        default { 21 }
    }
    $activeFrameHeight = $timelineType.GetProperty("FrameHeight").GetValue($timeline)
    $activeHeightPreset = $timelineType.GetProperty("FrameHeightPreset").GetValue($timeline)
    $frameHeightInput = $timelineType.GetField("_frameHeightInput", $instanceFlags).GetValue($timeline)
    $frameHeightLabel = $timelineType.GetField("_frameHeightLabel", $instanceFlags).GetValue($timeline)
    $frameWidthLabel = $timelineType.GetField("_frameWidthLabel", $instanceFlags).GetValue($timeline)
    if ($activeFrameHeight -ne $expectedFrameHeight -or $activeHeightPreset -ne $frameHeightPreset) {
        throw "The timeline frame-height preset was not applied: preset=$activeHeightPreset height=$activeFrameHeight"
    }
    if ($frameHeightInput.SelectedIndex -ne [int]$frameHeightPreset) {
        throw "The timeline frame-height dropdown was not synchronized with the active preset."
    }
    if ($frameHeightInput.Visible -and $frameWidthLabel.Visible -and $frameHeightInput.Right -gt $frameWidthLabel.Left) {
        throw "Timeline frame-height controls overlap the frame-width controls."
    }
    $visibleTrackCapacity = $timelineType.GetMethod("VisibleTrackCapacity", $instanceFlags).Invoke($timeline, @($layout))
    $rowTop = $layout.GetType().GetProperty("RowTop").GetValue($layout)
    $rowBottom = $layout.GetType().GetProperty("RowBottom").GetValue($layout)
    $expectedTrackCapacity = [Math]::Max(1, [int][Math]::Floor(($rowBottom - $rowTop) / $expectedFrameHeight))
    if ($visibleTrackCapacity -ne $expectedTrackCapacity) {
        throw "Timeline visible-track capacity did not use the active row height: expected=$expectedTrackCapacity actual=$visibleTrackCapacity"
    }
    $script:frameWidthCommitCount = 0
    $commitHandler = [EventHandler] { $script:frameWidthCommitCount++ }
    $timeline.add_FrameWidthCommitted($commitHandler)
    $temporaryWidth = if ($activeFrameWidth -lt 32) { $activeFrameWidth + 1 } else { $activeFrameWidth - 1 }
    $frameWidthSlider.Value = $temporaryWidth
    $frameWidthSlider.Value = $activeFrameWidth
    $deadline = [DateTime]::UtcNow.AddMilliseconds(420)
    while ([DateTime]::UtcNow -lt $deadline) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 16
    }
    $timeline.remove_FrameWidthCommitted($commitHandler)
    if ($script:frameWidthCommitCount -ne 1) {
        throw "Timeline frame-width changes were not debounced into one commit: $script:frameWidthCommitCount"
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
    "timeline_active_frame_cell_width=$activeFrameWidth"
    "timeline_frame_height_preset=$activeHeightPreset"
    "timeline_active_frame_height=$activeFrameHeight"
    "timeline_visible_track_capacity=$visibleTrackCapacity"
    "timeline_frame_width_debounced_commits=$script:frameWidthCommitCount"
    "timeline_row_height=$rowHeight"
    "timeline_onion_disabled_bounds=$disabledBounds"
    "timeline_onion_disabled_hit=$disabledHit"
    "timeline_onion_handle_bounds=$bounds"
    "timeline_onion_preview_area_hit=$rulerPreviewHit"
    "timeline_onion_disabled_screenshot=$resolvedDisabledOutput"
    "timeline_compact_screenshot=$resolvedOutput"
}
finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if ($null -ne $disabledBitmap) { $disabledBitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $timeline.IsDisposed) { $timeline.Dispose() }
}
