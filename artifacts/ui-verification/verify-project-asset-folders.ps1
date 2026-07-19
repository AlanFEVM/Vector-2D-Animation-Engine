param(
    [int]$Width = 308,
    [int]$Height = 300,
    [string]$OutputPath = "artifacts\ui-verification\project-asset-folders.png",
    [string]$NarrowOutputPath = "artifacts\ui-verification\project-asset-folders-narrow.png",
    [string]$HoverOutputPath = "artifacts\ui-verification\project-asset-folder-toggle-hover.png"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

$localizationType = $assembly.GetType("VectorAnimationEngine.UiLocalization", $true)
$languageType = $assembly.GetType("VectorAnimationEngine.UiLanguage", $true)
$localizationType.GetMethod("SetLanguage", $staticFlags).Invoke(
    $null,
    @([Enum]::Parse($languageType, "SimplifiedChinese")))

$projectType = $assembly.GetType("VectorAnimationEngine.VectorProject", $true)
$project = [Activator]::CreateInstance($projectType, $true)
$drawingObjects = $projectType.GetProperty("DrawingObjects", $flags).GetValue($project)
$looseObject = $drawingObjects[0]
$looseObject.Name = "未归档元件"
$bodyObject = $projectType.GetMethod("AddDrawingObject", $flags).Invoke($project, @("身体元件"))
$headObject = $projectType.GetMethod("AddDrawingObject", $flags).Invoke($project, @("头部元件"))

$folderType = $assembly.GetType("VectorAnimationEngine.ProjectAssetFolder", $true)
$rootFolder = $null
$childFolder = $null
$addFolderArgs = [object[]] @("角色", "", $rootFolder)
if (-not $projectType.GetMethod("TryAddAssetFolder", $flags).Invoke($project, $addFolderArgs)) {
    throw "Could not create the root project asset folder."
}
$rootFolder = $addFolderArgs[2]
$addChildArgs = [object[]] @("头部", $rootFolder.Id, $childFolder)
if (-not $projectType.GetMethod("TryAddAssetFolder", $flags).Invoke($project, $addChildArgs)) {
    throw "Could not create the nested project asset folder."
}
$childFolder = $addChildArgs[2]
$unusedFolderArgs = [object[]] @("界面", "", $null)
$projectType.GetMethod("TryAddAssetFolder", $flags).Invoke($project, $unusedFolderArgs) | Out-Null
$projectType.GetMethod("TryMoveDrawingObjectToAssetFolder", $flags).Invoke($project, @($bodyObject.Id, $rootFolder.Id)) | Out-Null
$projectType.GetMethod("TryMoveDrawingObjectToAssetFolder", $flags).Invoke($project, @($headObject.Id, $childFolder.Id)) | Out-Null

$panelType = $assembly.GetType("VectorAnimationEngine.LibraryVaultPanel", $true)
$panel = [Activator]::CreateInstance($panelType, $true)
$frameProvider = [Func[int]] { 0 }
$panelType.GetMethod("BindProject", $flags).Invoke($panel, @($project, $frameProvider))
$panelType.GetMethod("SetActiveDrawingObject", $flags).Invoke($panel, @($headObject.Id))
$localizationType.GetMethod("Watch", $staticFlags, $null, [Type[]] @([Windows.Forms.Control]), $null).Invoke($null, @($panel))

$form = [Windows.Forms.Form]::new()
$bitmap = $null
$narrowBitmap = $null
$hoverBitmap = $null
try {
    $form.ShowInTaskbar = $false
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = [Drawing.Point]::new(-30000, -30000)
    $form.ClientSize = [Drawing.Size]::new($Width, $Height)
    $panel.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($panel)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $panel.PerformLayout()
    [Windows.Forms.Application]::DoEvents()

    $tree = $panelType.GetField("_projectObjects", $flags).GetValue($panel)
    $newFolderButton = $panelType.GetField("_newFolderButton", $flags).GetValue($panel)
    $openButton = $panelType.GetField("_openButton", $flags).GetValue($panel)
    [Windows.Forms.TreeNode]$characters = $tree.Nodes | Where-Object { $_.Text -eq "角色/" } | Select-Object -First 1
    [Windows.Forms.TreeNode]$interface = $tree.Nodes | Where-Object { $_.Text -eq "界面/" } | Select-Object -First 1
    [Windows.Forms.TreeNode]$loose = $tree.Nodes | Where-Object { $_.Text -eq "未归档元件" } | Select-Object -First 1
    [Windows.Forms.TreeNode]$heads = $characters.Nodes | Where-Object { $_.Text -eq "头部/" } | Select-Object -First 1
    [Windows.Forms.TreeNode]$headNode = $heads.Nodes | Where-Object { $_.Text -eq "头部元件" } | Select-Object -First 1
    [Windows.Forms.TreeNode]$bodyNode = $characters.Nodes | Where-Object { $_.Text -eq "身体元件" } | Select-Object -First 1
    if ($null -eq $characters -or $null -eq $interface -or $null -eq $loose -or
        $null -eq $heads -or $null -eq $headNode -or $null -eq $bodyNode) {
        throw "The project asset tree did not preserve its nested folder and drawing-object hierarchy."
    }
    if ($tree.SelectedNode.Text -ne "头部元件" -or -not $characters.IsExpanded -or -not $heads.IsExpanded) {
        throw "The active nested drawing object was not selected and revealed in the project asset tree."
    }
    if (-not $newFolderButton.Visible -or $newFolderButton.Right -gt $openButton.Left) {
        throw "The project asset folder button is hidden or overlaps the Open command."
    }

    $themeType = $assembly.GetType("VectorAnimationEngine.Theme", $true)
    $glyphPoint = [Drawing.Point]::new(
        12 + $characters.Level * [Math]::Max(16, $tree.Indent),
        $characters.Bounds.Top + [Math]::Max(1, $characters.Bounds.Height / 2))
    $isGlyphHit = $themeType.GetMethod("IsTreeExpandGlyphHit", $staticFlags).Invoke(
        $null,
        @($tree, $characters, $glyphPoint))
    if (-not $isGlyphHit) {
        throw "The visible project-folder toggle is outside the custom expand hit target."
    }
    $tree.GetType().GetMethod("OnMouseDown", $flags).Invoke(
        $tree,
        @([Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Left, 1, $glyphPoint.X, $glyphPoint.Y, 0)))
    $tree.GetType().GetMethod("OnMouseUp", $flags).Invoke(
        $tree,
        @([Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Left, 1, $glyphPoint.X, $glyphPoint.Y, 0)))
    if ($characters.IsExpanded) {
        throw "Clicking the project-folder toggle did not collapse the folder."
    }
    $tree.GetType().GetMethod("OnMouseDown", $flags).Invoke(
        $tree,
        @([Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Left, 1, $glyphPoint.X, $glyphPoint.Y, 0)))
    $tree.GetType().GetMethod("OnMouseUp", $flags).Invoke(
        $tree,
        @([Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Left, 1, $glyphPoint.X, $glyphPoint.Y, 0)))
    if (-not $characters.IsExpanded -or -not $heads.IsExpanded) {
        throw "Clicking the collapsed project-folder toggle did not expand the folder."
    }

    $hoverGlyphPoint = [Drawing.Point]::new(
        12 + $heads.Level * [Math]::Max(16, $tree.Indent),
        $heads.Bounds.Top + [Math]::Max(1, $heads.Bounds.Height / 2))
    $tree.GetType().GetMethod("OnMouseMove", $flags).Invoke(
        $tree,
        @([Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::None, 0, $hoverGlyphPoint.X, $hoverGlyphPoint.Y, 0)))
    $tree.Invalidate()
    [Windows.Forms.Application]::DoEvents()
    $hoverBitmap = [Drawing.Bitmap]::new($panel.Width, $panel.Height)
    $panel.DrawToBitmap($hoverBitmap, [Drawing.Rectangle]::new(0, 0, $panel.Width, $panel.Height))
    $resolvedHoverOutput = Join-Path $root $HoverOutputPath
    $hoverBitmap.Save($resolvedHoverOutput, [Drawing.Imaging.ImageFormat]::Png)
    $tree.GetType().GetMethod("OnMouseLeave", $flags).Invoke($tree, @([EventArgs]::Empty))
    [Windows.Forms.Application]::DoEvents()

    $menu = $panelType.GetField("_projectObjectMenu", $flags).GetValue($panel)
    $menuLabels = @($menu.Items | Where-Object { $_ -is [Windows.Forms.ToolStripMenuItem] } | ForEach-Object { $_.Text })
    if ($menuLabels -notcontains "新建文件夹" -or $menuLabels -notcontains "重命名" -or $menuLabels -notcontains "复制副本") {
        throw "The project asset folder menu is missing localized create, rename, or duplicate commands: $($menuLabels -join ', ')"
    }

    $bitmap = [Drawing.Bitmap]::new($panel.Width, $panel.Height)
    $panel.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $panel.Width, $panel.Height))
    $resolvedOutput = Join-Path $root $OutputPath
    $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)

    $form.ClientSize = [Drawing.Size]::new(236, $Height)
    $panel.PerformLayout()
    [Windows.Forms.Application]::DoEvents()
    if ($newFolderButton.Right -gt $openButton.Left) {
        throw "The project asset commands overlap at the narrow supported width."
    }
    $narrowBitmap = [Drawing.Bitmap]::new($panel.Width, $panel.Height)
    $panel.DrawToBitmap($narrowBitmap, [Drawing.Rectangle]::new(0, 0, $panel.Width, $panel.Height))
    $resolvedNarrowOutput = Join-Path $root $NarrowOutputPath
    $narrowBitmap.Save($resolvedNarrowOutput, [Drawing.Imaging.ImageFormat]::Png)

    "project_asset_root_nodes=$($tree.Nodes.Count)"
    "project_asset_nested_folder=$($heads.Text)"
    "project_asset_selected_node=$($tree.SelectedNode.Text)"
    "project_asset_menu_labels=$($menuLabels -join '|')"
    "project_asset_folder_toggle=collapse-expand-ok"
    "project_asset_screenshot=$resolvedOutput"
    "project_asset_narrow_screenshot=$resolvedNarrowOutput"
    "project_asset_hover_screenshot=$resolvedHoverOutput"
}
finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if ($null -ne $narrowBitmap) { $narrowBitmap.Dispose() }
    if ($null -ne $hoverBitmap) { $hoverBitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $panel.IsDisposed) { $panel.Dispose() }
}
