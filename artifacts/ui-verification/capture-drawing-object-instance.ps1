param(
    [int]$Width = 324,
    [string]$OutputPath = "artifacts\ui-verification\drawing-object-instance-zh.png"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$localizationType = $assembly.GetType("VectorAnimationEngine.UiLocalization", $true)
$languageType = $assembly.GetType("VectorAnimationEngine.UiLanguage", $true)
$chinese = [Enum]::Parse($languageType, "SimplifiedChinese")
$localizationType.GetMethod("SetLanguage").Invoke($null, @($chinese))

$panelType = $assembly.GetType("VectorAnimationEngine.DrawingObjectInstancePanel", $true)
$instanceType = $assembly.GetType("VectorAnimationEngine.DrawingObjectInstanceDefinition", $true)
$modeType = $assembly.GetType("VectorAnimationEngine.DrawingObjectPlaybackMode", $true)
$panel = [Activator]::CreateInstance($panelType, $true)
$instance = [Activator]::CreateInstance($instanceType, $true)
$instanceType.GetProperty("ScaleX").SetValue($instance, [single]1.6)
$instanceType.GetProperty("ScaleY").SetValue($instance, [single]0.7)
$instanceType.GetProperty("PlaybackFps").SetValue($instance, 24)
$instanceType.GetProperty("PlaybackMode").SetValue($instance, [Enum]::Parse($modeType, "HoldFrame"))
$instanceType.GetProperty("HoldFrame").SetValue($instance, 12)
$setInstance = $panelType.GetMethod(
    "SetInstance",
    [Reflection.BindingFlags] "Public,Instance",
    $null,
    [Type[]] @($instanceType, [int]),
    $null)
$setInstance.Invoke($panel, @($instance, 30))
$watch = $localizationType.GetMethod(
    "Watch",
    [Reflection.BindingFlags] "Public,Static",
    $null,
    [Type[]] @([Windows.Forms.Control]),
    $null)
$watch.Invoke($null, @($panel))

$form = New-Object Windows.Forms.Form
$bitmap = $null
try {
    $form.ShowInTaskbar = $false
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object Drawing.Point(-32000, -32000)
    $form.ClientSize = New-Object Drawing.Size($Width, 180)
    $panel.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($panel)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $panel.PerformLayout()
    [Windows.Forms.Application]::DoEvents()
    $bitmap = New-Object Drawing.Bitmap($Width, 180)
    $panel.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $Width, 180)))
    $resolvedOutput = Join-Path $root $OutputPath
    $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)
    $resolvedOutput
}
finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $panel.IsDisposed) { $panel.Dispose() }
}
