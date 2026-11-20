param(
    [string]$AssemblyPath = 'native/bin/Debug/net8.0-windows/VectorAnimationEngine.dll',
    [string]$OutputDirectory = 'docs/encyclopedia/public/images/cursors'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$assembly = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath))
$stageType = $assembly.GetType('VectorAnimationEngine.StageControl', $true)
$method = $stageType.GetMethod('CreateSelectionArrowCursorBitmap', [System.Reflection.BindingFlags]'NonPublic,Static')
if (-not $method) { throw 'The engine assembly does not expose the cursor bitmap factory.' }
$feedbackType = $method.GetParameters()[1].ParameterType
$output = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory))
[System.IO.Directory]::CreateDirectory($output) | Out-Null
foreach ($white in @($false, $true)) {
    foreach ($feedback in @('None', 'Move', 'Bend', 'Node', 'Insert')) {
        $bitmap = $method.Invoke($null, @($white, [Enum]::Parse($feedbackType, $feedback), 192))
        try {
            $arrow = if ($white) { 'white' } else { 'black' }
            $path = Join-Path $output "$arrow-$($feedback.ToLowerInvariant()).png"
            $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Output $path
        }
        finally { $bitmap.Dispose() }
    }
}
