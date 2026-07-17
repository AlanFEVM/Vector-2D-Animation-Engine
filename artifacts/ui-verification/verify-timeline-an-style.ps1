param(
    [int]$Width = 900,
    [int]$Height = 230,
    [string]$OutputPath = "artifacts\ui-verification\timeline-an-style.png"
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
$sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(5, 30))
$model = $sceneType.GetProperty("Timeline").GetValue($scene)
$tracks = $model.GetType().GetProperty("Tracks").GetValue($model)

for ($index = 0; $index -lt $tracks.Count; $index++) {
    $model.SetTrackDuration($tracks[$index].Id, 30) | Out-Null
}
$model.InsertKeyframe($tracks[0].Id, 0) | Out-Null
$model.InsertBlankKeyframe($tracks[0].Id, 8) | Out-Null
$model.InsertKeyframe($tracks[0].Id, 13) | Out-Null
$model.InsertBlankKeyframe($tracks[0].Id, 21) | Out-Null
$model.InsertBlankKeyframe($tracks[1].Id, 0) | Out-Null
$model.InsertKeyframe($tracks[1].Id, 6) | Out-Null
$model.InsertBlankKeyframe($tracks[1].Id, 17) | Out-Null
$model.InsertKeyframe($tracks[2].Id, 0) | Out-Null
$model.InsertKeyframe($tracks[2].Id, 10) | Out-Null
$model.InsertBlankKeyframe($tracks[3].Id, 0) | Out-Null
$model.InsertBlankKeyframe($tracks[3].Id, 12) | Out-Null

$timelineType = $assembly.GetType("VectorAnimationEngine.TimelineStrip", $true)
$timeline = $timelineType.GetConstructor(
    $flags,
    $null,
    [Type[]] @($sceneType),
    $null).Invoke(@($scene))
$timelineType.GetProperty("ActiveTrackIndex").SetValue($timeline, 1)
$timelineType.GetProperty("CurrentFrame").SetValue($timeline, 13)

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

    $firstExposure = $tracks[0].EvaluateExposure(4)
    $blankExposure = $tracks[0].EvaluateExposure(10)
    $secondExposure = $tracks[0].EvaluateExposure(16)
    if (-not $firstExposure.HasContent -or $blankExposure.HasContent -or -not $secondExposure.HasContent) {
        throw "The AN-style verification fixture did not create populated/blank/populated exposure spans."
    }

    $bitmap = New-Object Drawing.Bitmap($Width, $Height)
    $timeline.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $Width, $Height)))
    $resolvedOutput = Join-Path $root $OutputPath
    $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)

    "timeline_an_populated_blank_spans=True"
    "timeline_an_screenshot=$resolvedOutput"
}
finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $timeline.IsDisposed) { $timeline.Dispose() }
}
