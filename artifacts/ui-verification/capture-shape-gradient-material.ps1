[CmdletBinding()]
param(
    [string]$OutputPath = "artifacts\ui-verification\material-editor-shape-gradient-active.png",
    [int]$Width = 296,
    [int]$Height = 524
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assemblyPath = Join-Path $repoRoot "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"
$resolvedOutput = Join-Path $repoRoot $OutputPath
$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
$panelType = $assembly.GetType("VectorAnimationEngine.MaterialEditorPanel", $true)
$gradientKindType = $assembly.GetType("VectorAnimationEngine.GradientKind", $true)
$shapeRadial = [Enum]::Parse($gradientKindType, "ShapeRadial")
$setGradientKind = $panelType.GetMethod(
    "SetGradientKind",
    [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic)

$panel = [Activator]::CreateInstance($panelType, $true)
$form = New-Object Windows.Forms.Form
$bitmap = $null
try {
    $form.ShowInTaskbar = $false
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object Drawing.Point(-32000, -32000)
    $form.ClientSize = New-Object Drawing.Size($Width, $Height)
    $panel.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($panel)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $setGradientKind.Invoke($panel, @($shapeRadial))
    $panel.PerformLayout()
    [Windows.Forms.Application]::DoEvents()

    $outputDirectory = Split-Path -Parent $resolvedOutput
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $bitmap = New-Object Drawing.Bitmap($Width, $Height)
    $panel.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $Width, $Height)))
    $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)
} finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $panel.IsDisposed) { $panel.Dispose() }
}

[pscustomobject]@{ Output = $resolvedOutput; Width = $Width; Height = $Height } | ConvertTo-Json -Compress
