param(
    [string]$OutputPath = "artifacts\ui-verification\polar-grid-zoomed-clipped.png"
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Core" -or [Environment]::Version.Major -lt 8) {
    throw "Run this script with PowerShell 7 on .NET 8: pwsh -STA -File $PSCommandPath"
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$stageType = $assembly.GetType("VectorAnimationEngine.StageControl", $true)
$gridType = $assembly.GetType("VectorAnimationEngine.WorldGridType", $true)
$scene = [Activator]::CreateInstance($sceneType, $true)
$scene.CreateEmpty()
$stage = [Activator]::CreateInstance($stageType, $flags, $null, @($scene), $null)
$form = [Windows.Forms.Form]::new()
$bitmap = $null
try {
    $form.ShowInTaskbar = $false
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = [Drawing.Point]::new(-30000, -30000)
    $form.ClientSize = [Drawing.Size]::new(640, 420)
    $stage.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($stage)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()

    $stageType.GetProperty("WorldGridType", $flags).SetValue($stage, [Enum]::Parse($gridType, "Polar"))
    $stage.SetVisibleWorldWidth([single]250)
    $stage.Pan([single]-12800000, [single]-7680000)
    for ($index = 0; $index -lt 4; $index++) {
        $stage.Invalidate()
        $stage.Update()
        [Windows.Forms.Application]::DoEvents()
    }
    if (-not $stage.LastFrameUsedDirect2D) {
        throw "The zoomed polar-grid verification did not render through Direct2D."
    }

    $bitmap = [Drawing.Bitmap]::new($stage.Width, $stage.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $stageType.GetMethod("DrawGdi", $flags).Invoke($stage, @($graphics))
    }
    finally {
        $graphics.Dispose()
    }

    $background = $bitmap.GetPixel(0, 0).ToArgb()
    $gridSamples = 0
    for ($y = 0; $y -lt $bitmap.Height; $y += 2) {
        for ($x = 0; $x -lt $bitmap.Width; $x += 2) {
            if ($bitmap.GetPixel($x, $y).ToArgb() -ne $background) { $gridSamples++ }
        }
    }
    if ($gridSamples -lt 80) {
        throw "The clipped polar grid became blank or discontinuous: samples=$gridSamples"
    }

    $resolvedOutput = Join-Path $root $OutputPath
    $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)
    "polar_grid_zoom=64"
    "polar_grid_camera=5000000,3000000"
    $commandMilliseconds = $stageType.GetProperty("LastDirect2DCommandMilliseconds", $flags).GetValue($stage)
    "polar_grid_direct2d_command_ms=$commandMilliseconds"
    "polar_grid_non_background_samples=$gridSamples"
    "polar_grid_screenshot=$resolvedOutput"
}
finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $stage.IsDisposed) { $stage.Dispose() }
}
