[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TypeName,

    [string]$AssemblyPath = "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll",
    [string]$OutputPath = "artifacts\ui-verification\control.png",

    [ValidateRange(1, 8192)]
    [int]$Width = 320,

    [ValidateRange(1, 8192)]
    [int]$Height = 520
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSEdition -ne "Core" -or [Environment]::Version.Major -lt 8) {
    throw "Run this script with PowerShell 7 on .NET 8: pwsh -STA -File $PSCommandPath"
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
$resolvedAssembly = if ([IO.Path]::IsPathRooted($AssemblyPath)) {
    $AssemblyPath
} else {
    Join-Path $repoRoot $AssemblyPath
}
$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $repoRoot $OutputPath
}

if (-not (Test-Path -LiteralPath $resolvedAssembly -PathType Leaf)) {
    throw "Assembly not found: $resolvedAssembly. Build the native Release project first."
}

$outputDirectory = Split-Path -Parent $resolvedOutput
if ($outputDirectory) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$assembly = [Reflection.Assembly]::LoadFrom($resolvedAssembly)
$qualifiedName = if ($TypeName.Contains(".")) { $TypeName } else { "VectorAnimationEngine.$TypeName" }
$type = $assembly.GetType($qualifiedName, $false)
if ($null -eq $type) {
    throw "Control type not found: $qualifiedName"
}

$control = [Activator]::CreateInstance($type, $true)
if ($control -isnot [Windows.Forms.Control]) {
    if ($control -is [IDisposable]) { $control.Dispose() }
    throw "Type is not a WinForms Control: $qualifiedName"
}

$form = New-Object Windows.Forms.Form
$bitmap = $null
try {
    $form.ShowInTaskbar = $false
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object Drawing.Point(-32000, -32000)
    $form.ClientSize = New-Object Drawing.Size($Width, $Height)
    $control.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($control)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $control.PerformLayout()
    [Windows.Forms.Application]::DoEvents()

    $bitmap = New-Object Drawing.Bitmap($Width, $Height)
    $control.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $Width, $Height)))
    $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)
} finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $control.IsDisposed) { $control.Dispose() }
}

[pscustomobject]@{
    Type = $qualifiedName
    Width = $Width
    Height = $Height
    Output = $resolvedOutput
} | ConvertTo-Json -Compress
