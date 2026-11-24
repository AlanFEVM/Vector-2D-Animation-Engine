[CmdletBinding()]
param(
    [string]$AssemblyPath = 'artifacts/wiki-export-engine/VectorAnimationEngine.dll',
    [string]$OutputDirectory = 'artifacts/wiki-export',
    [ValidateSet('Dark', 'White', 'Charcoal')]
    [string]$Theme = 'Charcoal',
    [ValidatePattern('^#[0-9a-fA-F]{6}$')]
    [string]$WorkspaceColor = '#585454',
    [ValidateSet('Square', 'SubtleRounded')]
    [string]$CornerStyle = 'SubtleRounded',
    [bool]$SimpleSelectionHighlight = $true,
    [ValidateRange(0, 100)]
    [int]$GridOpacity = 100,
    [ValidateSet('All', 'tool-palette', 'stage-area', 'stage-context-menu', 'stage-context-menu-selection', 'scene-canvas-start-detail', 'scene-canvas-dialog', 'stage-view-controls', 'grid-cartesian', 'grid-polar', 'grid-golden-spiral', 'properties-panel', 'properties-object')]
    [string[]]$Image = @('All'),
    [switch]$SkipBuild,
    [switch]$UpdateWiki
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or [Environment]::Version.Major -lt 8) {
    throw 'Use PowerShell 7 on .NET 8 or later.'
}
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run with pwsh -STA -File scripts/export-wiki-images.ps1.'
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::SetHighDpiMode([Windows.Forms.HighDpiMode]::DpiUnaware) | Out-Null
[Windows.Forms.Application]::EnableVisualStyles()
[Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
[Windows.Forms.Application]::SetUnhandledExceptionMode([Windows.Forms.UnhandledExceptionMode]::ThrowException)
$repo = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) {
    & dotnet build (Join-Path $repo 'native/VectorAnimationEngine.Native.csproj') -c Debug -o (Split-Path -Parent (Join-Path $repo $AssemblyPath))
    if ($LASTEXITCODE -ne 0) { throw 'Engine build failed; no images were replaced.' }
}
$assemblyFile = [IO.Path]::GetFullPath((Join-Path $repo $AssemblyPath))
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
$runDirectory = Join-Path $output ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom($assemblyFile)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static'

function Engine-Type([string]$Name) {
    return $assembly.GetType("VectorAnimationEngine.$Name", $true)
}
function Field($Object, [string]$Name) {
    $field = $Object.GetType().GetField($Name, $flags)
    if (-not $field) { throw "Engine field not found: $Name" }
    return $field.GetValue($Object)
}
function Call($Object, [string]$Name, [object[]]$Arguments = @()) {
    $method = $Object.GetType().GetMethod($Name, $flags)
    if (-not $method) { throw "Engine method not found: $Name" }
    return $method.Invoke($Object, $Arguments)
}
function Settle {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt 250) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 10
    }
}
$records = [Collections.Generic.List[object]]::new()
function Save-Image([Drawing.Bitmap]$Bitmap, [string]$Name, [string]$Renderer) {
    if ('All' -notin $Image -and [IO.Path]::GetFileNameWithoutExtension($Name) -notin $Image) { return }
    if ($Bitmap.Width -lt 20 -or $Bitmap.Height -lt 20) { throw "Invalid image size: $Name" }
    $colors = [Collections.Generic.HashSet[int]]::new()
    for ($row = 0; $row -lt $Bitmap.Height; $row += 5) {
        for ($column = 0; $column -lt $Bitmap.Width; $column += 5) {
            $colors.Add($Bitmap.GetPixel($column, $row).ToArgb()) | Out-Null
        }
    }
    if ($colors.Count -lt 8) { throw "Image appears blank: $Name" }
    $path = Join-Path $runDirectory $Name
    $Bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $records.Add([pscustomobject]@{
        File = $Name; Width = $Bitmap.Width; Height = $Bitmap.Height
        Renderer = $Renderer; SampleColors = $colors.Count
        Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    })
}
function Export-Control([Windows.Forms.Control]$Control, [string]$Name) {
    $Control.PerformLayout()
    $bitmap = [Drawing.Bitmap]::new($Control.Width, $Control.Height)
    try {
        $Control.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
        Save-Image $bitmap $Name 'WinForms.DrawToBitmap'
    }
    finally { $bitmap.Dispose() }
}

$previousSettings = $env:V2D_APPLICATION_SETTINGS_PATH
$settingsFile = Join-Path $runDirectory 'settings.json'
$settings = [Activator]::CreateInstance((Engine-Type 'ApplicationSettings'), $true)
$settings.Language = [Enum]::Parse((Engine-Type 'UiLanguage'), 'SimplifiedChinese')
$settings.ColorTheme = [Enum]::Parse((Engine-Type 'ApplicationColorTheme'), $Theme)
$settings.WorkspaceColorArgb = [Drawing.ColorTranslator]::FromHtml($WorkspaceColor).ToArgb()
$settings.WindowCornerStyle = [Enum]::Parse((Engine-Type 'WindowCornerStyle'), $CornerStyle)
$settings.SimpleSelectionHighlightEnabled = $SimpleSelectionHighlight
$env:V2D_APPLICATION_SETTINGS_PATH = $settingsFile
$store = Engine-Type 'ApplicationSettingsStore'
if (-not $store.GetMethod('TrySave', $flags).Invoke($null, @($settings))) {
    throw 'Could not create isolated export settings.'
}
$form = $null
$dialog = $null
$dialogHost = $null
try {
    (Engine-Type 'Theme').GetMethod('ConfigureCornerStyle', $flags).Invoke($null, @($settings.WindowCornerStyle)) | Out-Null
    (Engine-Type 'Theme').GetMethod('Configure', $flags).Invoke($null, @($settings.ColorTheme, 210, 170)) | Out-Null
    (Engine-Type 'UiLocalization').GetMethod('SetLanguage', $flags).Invoke($null, @($settings.Language)) | Out-Null
    $form = [Activator]::CreateInstance((Engine-Type 'MainForm'), $true)
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = [Drawing.Point]::new(-30000, -30000)
    $form.ShowInTaskbar = $false
    $form.Opacity = 0
    $form.Size = [Drawing.Size]::new(1600, 1000)
    $form.Show()
    Settle
    $form.MinimumSize = [Drawing.Size]::Empty
    $form.AutoScaleMode = [Windows.Forms.AutoScaleMode]::None
    $form.ClientSize = [Drawing.Size]::new(1850, 1150)
    $form.PerformLayout()
    Settle
    (Field $form '_timer').Stop()
    $stage = Field $form '_stage'
    $stage.BackColor = [Drawing.ColorTranslator]::FromHtml($WorkspaceColor)
    if ($stage.GetType().GetProperty('SimpleSelectionHighlightEnabled', $flags).GetValue($stage) -ne $SimpleSelectionHighlight) {
        throw 'The stage selection highlight setting did not match the requested export profile.'
    }
    $scene = Field $form '_scene'
    $scene.CreateEmpty()
    $triangle = [Drawing.PointF[]]@(
        [Drawing.PointF]::new(-3500, 1800), [Drawing.PointF]::new(-1500, -1800), [Drawing.PointF]::new(500, 1800))
    $rectangle = [Drawing.PointF[]]@(
        [Drawing.PointF]::new(1600, -1800), [Drawing.PointF]::new(6500, -1800),
        [Drawing.PointF]::new(6500, 1800), [Drawing.PointF]::new(1600, 1800))
    foreach ($shape in @(@($triangle, [Drawing.Color]::FromArgb(190, 54, 66)), @($rectangle, [Drawing.Color]::FromArgb(82, 173, 93)))) {
        $contours = [Collections.Generic.List[Drawing.PointF[]]]::new()
        $contours.Add($shape[0])
        $scene.AddPathObjectContours(0, $contours, 120, $shape[1], [Drawing.Color]::White, [uint32]4) | Out-Null
    }
    $stage.Fit()
    Call $form 'FitStageBounds' @([Drawing.RectangleF]::new(-7000, -4800, 18000, 10000)) | Out-Null
    $tabs = Field $form '_workspaceTabs'
    $tabs.WorldGridOpacity = $GridOpacity
    Call $form 'RefreshToolButtons' | Out-Null
    Export-Control (Field $form '_toolPalette') 'tool-palette.png'
    $welcome = Field $form '_canvasWelcome'
    $welcome.Visible = $true
    try {
        Call $form 'LayoutCanvasWelcome' | Out-Null
        Export-Control $welcome 'scene-canvas-start-detail.png'
    }
    finally { $welcome.Visible = $false }

    # Compose tightly cropped real controls in their original top-to-bottom order.
    $gridControls = Field $tabs '_gridControls'
    $snapping = Field $form '_drawSnappingStrip'
    $metrics = Field $form '_stageMetrics'
    $actions = @($metrics.Controls | Where-Object { $_.Visible -and $_.GetType().Name -eq 'SvgIconButton' })
    if ($actions.Count -lt 4) { throw 'The stage view buttons could not be located.' }
    $actionBounds = $actions[0].Bounds
    foreach ($action in $actions) { $actionBounds = [Drawing.Rectangle]::Union($actionBounds, $action.Bounds) }
    $parts = @(
        @($gridControls, $gridControls.ClientRectangle),
        @($snapping, $snapping.ClientRectangle),
        @($metrics, $actionBounds)
    )
    $controlWidth = ($parts | ForEach-Object { $_[1].Width } | Measure-Object -Maximum).Maximum
    $controlHeight = ($parts | ForEach-Object { $_[1].Height } | Measure-Object -Sum).Sum + 16
    $controlImage = [Drawing.Bitmap]::new([int]$controlWidth, [int]$controlHeight)
    $controlGraphics = [Drawing.Graphics]::FromImage($controlImage)
    try {
        $controlGraphics.Clear($tabs.BackColor)
        $y = 0
        foreach ($part in $parts) {
            $control = $part[0]
            $region = $part[1]
            $partImage = [Drawing.Bitmap]::new($control.Width, $control.Height)
            try {
                $control.DrawToBitmap($partImage, $control.ClientRectangle)
                $target = [Drawing.Rectangle]::new($controlImage.Width - $region.Width, $y, $region.Width, $region.Height)
                $controlGraphics.DrawImage($partImage, $target, $region, [Drawing.GraphicsUnit]::Pixel)
            }
            finally { $partImage.Dispose() }
            $y += $region.Height + 8
        }
        Save-Image $controlImage 'stage-view-controls.png' 'WinForms control bounds; three cropped rows'
    }
    finally { $controlGraphics.Dispose(); $controlImage.Dispose() }
    foreach ($grid in @(@('Cartesian', 'grid-cartesian.png'), @('Polar', 'grid-polar.png'), @('GoldenSpiral', 'grid-golden-spiral.png'))) {
        $stage.WorldGridType = [Enum]::Parse((Engine-Type 'WorldGridType'), $grid[0])
        $bitmap = [Drawing.Bitmap]::new($stage.Width, $stage.Height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            Call $stage 'DrawGdiFrame' @($graphics) | Out-Null
            Save-Image $bitmap $grid[1] 'StageControl.DrawGdiFrame (2D GDI)'
        }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
    $stage.WorldGridType = [Enum]::Parse((Engine-Type 'WorldGridType'), 'Cartesian')
    $full = [Drawing.Bitmap]::new($form.ClientSize.Width, $form.ClientSize.Height)
    $stageBitmap = [Drawing.Bitmap]::new($stage.Width, $stage.Height)
    $stageGraphics = [Drawing.Graphics]::FromImage($stageBitmap)
    try {
        $surface = [Drawing.Graphics]::FromImage($full)
        try {
            $surface.Clear($form.BackColor)
            for ($index = $form.Controls.Count - 1; $index -ge 0; $index--) {
                $child = $form.Controls[$index]
                if (-not $child.Visible -or $child.Width -lt 1 -or $child.Height -lt 1) { continue }
                $childImage = [Drawing.Bitmap]::new($child.Width, $child.Height)
                try {
                    $child.DrawToBitmap($childImage, [Drawing.Rectangle]::new(0, 0, $child.Width, $child.Height))
                    $surface.DrawImageUnscaled($childImage, $child.Location)
                }
                finally { $childImage.Dispose() }
            }
        }
        finally { $surface.Dispose() }
        Call $stage 'DrawGdiFrame' @($stageGraphics) | Out-Null
        $graphics = [Drawing.Graphics]::FromImage($full)
        try {
            $position = $form.PointToClient($stage.PointToScreen([Drawing.Point]::Empty))
            $graphics.DrawImageUnscaled($stageBitmap, $position)
            $toolbar = Field $form '_toolPalette'
            $toolbarPosition = $form.PointToClient($toolbar.PointToScreen([Drawing.Point]::Empty))
            $toolbarImage = [Drawing.Bitmap]::new($toolbar.Width, $toolbar.Height)
            try {
                $toolbar.DrawToBitmap($toolbarImage, [Drawing.Rectangle]::new(0, 0, $toolbar.Width, $toolbar.Height))
                $graphics.DrawImageUnscaled($toolbarImage, $toolbarPosition)
            }
            finally { $toolbarImage.Dispose() }
            $left = [Math]::Max($position.X + 2, $toolbarPosition.X + $toolbar.Width + 4)
            $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(230, 70, 45), 4)
            try { $graphics.DrawRectangle($pen, $left, $position.Y + 2, $position.X + $stage.Width - $left - 2, $stage.Height - 4) }
            finally { $pen.Dispose() }
        }
        finally { $graphics.Dispose() }
        Save-Image $full 'stage-area.png' 'WinForms + Stage 2D GDI; stage outlined'
    }
    finally { $stageGraphics.Dispose(); $stageBitmap.Dispose(); $full.Dispose() }
    $menu = Field $form '_stageContextMenu'
    $menu.Opacity = 0
    Call $form 'ClearSelection' @($false) | Out-Null
    Call $form 'ShowStageContextMenu' @([Drawing.Point]::new(4, 4)) | Out-Null
    Settle
    try { Export-Control $menu 'stage-context-menu.png' }
    finally { $menu.Close() }
    if (-not (Call $form 'SelectAllObjectsInCurrentFrame')) { throw 'Could not select the sample drawing objects.' }
    Call $form 'ShowStageContextMenu' @([Drawing.Point]::new(4, 4)) | Out-Null
    Settle
    try { Export-Control $menu 'stage-context-menu-selection.png' }
    finally { $menu.Close() }
    $constructor = (Engine-Type 'SceneCanvasDialog').GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 3 }
    $project = Field $form '_project'
    $resolverType = [Func[object, string]].GetGenericTypeDefinition().MakeGenericType(@((Engine-Type 'ImageAssetDefinition'), [string]))
    $typedResolver = [Management.Automation.LanguagePrimitives]::ConvertTo({ param($asset) return $null }, $resolverType)
    $dialog = $constructor.Invoke(@($null, $project.ImageAssets, $typedResolver))
    $dialog.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $dialog.Location = [Drawing.Point]::new(-30000, -30000)
    $dialog.Opacity = 0
    $dialogHost = [Windows.Forms.Form]::new()
    $dialogHost.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $dialogHost.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $dialogHost.Location = [Drawing.Point]::new(-30000, -30000)
    $dialogHost.ShowInTaskbar = $false
    $dialogHost.Opacity = 0
    $dialogHost.ClientSize = $dialog.ClientSize
    $dialogHost.Padding = $dialog.Padding
    $dialogHost.BackColor = $dialog.BackColor
    $dialogChildren = @($dialog.Controls)
    foreach ($child in $dialogChildren) { $dialogHost.Controls.Add($child) }
    $dialogHost.Show()
    Settle
    Export-Control $dialogHost 'scene-canvas-dialog.png'
    if ('All' -in $Image -or 'properties-panel' -in $Image -or 'properties-object' -in $Image) {
        $form.ClientSize = [Drawing.Size]::new(1850, 850)
        $form.PerformLayout()
        Settle
        if (-not (Field $form '_inspectorPanelOpen')) { Call $form 'ToggleInspectorPanel' | Out-Null }
        $objectCategory = [Enum]::Parse((Engine-Type 'MainForm+InspectorCategory'), 'None')
        Call $form 'RevealInspectorCategory' @($objectCategory, 'wiki object example') | Out-Null
        Settle
        Export-Control (Field $form '_inspectorHost') 'properties-object.png'
        Call $form 'ActivateTool' @([Enum]::Parse((Engine-Type 'ToolMode'), 'Text')) | Out-Null
        Settle
        Export-Control (Field $form '_inspectorHost') 'properties-panel.png'
    }
    $manifest = [ordered]@{
        Schema = 1; Theme = $Theme; WorkspaceColor = $WorkspaceColor; Language = 'SimplifiedChinese'
        GridOpacity = $GridOpacity; DeviceDpi = $form.DeviceDpi
        CornerStyle = $CornerStyle; SimpleSelectionHighlight = $SimpleSelectionHighlight
        EngineSha256 = (Get-FileHash -LiteralPath $assemblyFile).Hash
        Files = $records.ToArray()
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDirectory 'manifest.json') -Encoding utf8
    if ($UpdateWiki) {
        $destination = Join-Path $repo 'docs/encyclopedia/public/images'
        foreach ($record in $records) {
            $target = Join-Path $destination $record.File
            $temporary = "$target.export-$([Guid]::NewGuid().ToString('N')).tmp"
            try {
                Copy-Item -LiteralPath (Join-Path $runDirectory $record.File) -Destination $temporary
                [IO.File]::Move($temporary, $target, $true)
            }
            finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
        }
    }
    Write-Output (Join-Path $runDirectory 'manifest.json')
}
finally {
    if ($dialogHost) { $dialogHost.Dispose() }
    if ($dialog) { $dialog.Dispose() }
    if ($form) { $form.Dispose() }
    $env:V2D_APPLICATION_SETTINGS_PATH = $previousSettings
}
