param(
    [ValidateSet('full', 'header', 'workspace-tabs', 'vault', 'toolbar', 'stage', 'inspector', 'timeline')]
    [string]$Panel = 'inspector',

    [ValidateSet('none', 'object', 'tool', 'document', 'appearance', 'camera', 'scene')]
    [string]$InspectorPage = 'none',

    [string]$Name = '',
    [string]$OutputDirectory = 'docs/encyclopedia/public/images/panels',
    [int]$WaitMilliseconds = 500
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WikiPanelCaptureNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
'@

$process = Get-Process | Where-Object {
    $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like '*Vector 2D Animation Engine*'
} | Select-Object -First 1

if (-not $process) {
    throw 'Vector 2D Animation Engine window was not found.'
}

$handle = [IntPtr]$process.MainWindowHandle
[WikiPanelCaptureNative]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
if ([WikiPanelCaptureNative]::IsIconic($handle)) {
    throw 'The Vector 2D Animation Engine window is minimized. Restore it before capturing.'
}

$rect = [WikiPanelCaptureNative+RECT]::new()
if (-not [WikiPanelCaptureNative]::GetWindowRect($handle, [ref]$rect)) {
    throw 'Unable to read the Vector 2D Animation Engine window bounds.'
}

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -lt 760 -or $height -lt 520) {
    throw "The editor window is too small for wiki capture: ${width}x${height}."
}

# Normalized against the complete editor window. Keep these profiles aligned with MainForm layout.
$profiles = @{
    'full'           = @(0.000, 0.000, 1.000, 1.000)
    'header'         = @(0.000, 0.000, 1.000, 0.105)
    'workspace-tabs' = @(0.000, 0.030, 0.300, 0.075)
    'vault'          = @(0.000, 0.090, 0.150, 0.650)
    'toolbar'        = @(0.145, 0.105, 0.050, 0.430)
    'stage'          = @(0.168, 0.105, 0.635, 0.630)
    'inspector'      = @(0.795, 0.090, 0.200, 0.650)
    'timeline'       = @(0.000, 0.725, 0.995, 0.270)
}

# Inspector rail buttons are vertically stable relative to the main window.
$inspectorPageY = @{
    'object'     = 0.073
    'tool'       = 0.116
    'document'   = 0.145
    'appearance' = 0.188
    'camera'     = 0.223
    'scene'      = 0.258
}

[WikiPanelCaptureNative]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 200

if ($InspectorPage -ne 'none') {
    $clickX = $rect.Left + $width - [Math]::Max(18, [int]($width * 0.009))
    $clickY = $rect.Top + [int]($height * $inspectorPageY[$InspectorPage])
    [WikiPanelCaptureNative]::SetCursorPos($clickX, $clickY) | Out-Null
    [WikiPanelCaptureNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [WikiPanelCaptureNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $WaitMilliseconds
    $Panel = 'inspector'
}

$profile = $profiles[$Panel]
$cropX = [Math]::Max(0, [int]($width * $profile[0]))
$cropY = [Math]::Max(0, [int]($height * $profile[1]))
$cropWidth = [Math]::Min($width - $cropX, [int]($width * $profile[2]))
$cropHeight = [Math]::Min($height - $cropY, [int]($height * $profile[3]))

if ($Panel -eq 'toolbar') {
    Add-Type -AssemblyName UIAutomationClient
    $windowElement = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $toolbarCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Tools')
    $toolbarElements = $windowElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $toolbarCondition)
    $visibleToolbars = @($toolbarElements | Where-Object {
        -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0
    })
    if ($visibleToolbars.Count -ne 1) {
        throw "Expected one visible Tools control, found $($visibleToolbars.Count)."
    }
    $toolbarBounds = $visibleToolbars[0].Current.BoundingRectangle
    $cropX = [int][Math]::Floor($toolbarBounds.Left) - $rect.Left
    $cropY = [int][Math]::Floor($toolbarBounds.Top) - $rect.Top
    $cropWidth = [int][Math]::Ceiling($toolbarBounds.Right) - $rect.Left - $cropX
    $cropHeight = [int][Math]::Ceiling($toolbarBounds.Bottom) - $rect.Top - $cropY
    if ($cropX -lt 0 -or $cropY -lt 0 -or $cropX + $cropWidth -gt $width -or $cropY + $cropHeight -gt $height) {
        throw 'The toolbar bounds are outside the captured editor window.'
    }
}

$bitmap = [System.Drawing.Bitmap]::new($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
}
finally {
    $graphics.Dispose()
}

$cropRect = [System.Drawing.Rectangle]::new($cropX, $cropY, $cropWidth, $cropHeight)
$cropped = $bitmap.Clone($cropRect, $bitmap.PixelFormat)
$bitmap.Dispose()

try {
    $resolvedOutput = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory))
    [System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
    $baseName = if ($Name) { $Name } elseif ($InspectorPage -ne 'none') { "inspector-$InspectorPage" } else { $Panel }
    $safeName = $baseName -replace '[^A-Za-z0-9._-]', '-'
    $outputPath = Join-Path $resolvedOutput "$safeName.png"
    $cropped.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output $outputPath
}
finally {
    $cropped.Dispose()
}
