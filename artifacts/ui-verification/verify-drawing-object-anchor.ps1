[CmdletBinding()]
param(
    [string]$AssemblyPath = "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll",
    [string]$OutputPath = "artifacts\ui-verification\drawing-object-anchor-panel-zh.png"
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Core" -or [Environment]::Version.Major -lt 8) {
    throw "Run this script with PowerShell 7 on .NET 8: pwsh -STA -File $PSCommandPath"
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root $AssemblyPath))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"

function Engine-Type([string]$name) {
    return $assembly.GetType("VectorAnimationEngine.$name", $true)
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Pump-Ui([int]$frames = 8) {
    for ($index = 0; $index -lt $frames; $index++) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 16
    }
}

$localizationType = Engine-Type "UiLocalization"
$languageType = Engine-Type "UiLanguage"
$localizationType.GetMethod("SetLanguage", $staticFlags).Invoke(
    $null,
    @([Enum]::Parse($languageType, "SimplifiedChinese")))

$mainType = Engine-Type "MainForm"
$workspaceType = Engine-Type "WorkspaceView"
$sceneType = Engine-Type "VectorScene"
$shapeType = Engine-Type "ShapeKind"
$instanceType = Engine-Type "DrawingObjectInstanceDefinition"
$main = [Activator]::CreateInstance($mainType, $true)
$bitmap = $null
try {
    $main.ShowInTaskbar = $false
    $main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $main.Location = [Drawing.Point]::new(-30000, -30000)
    $main.Size = [Drawing.Size]::new(1280, 800)
    $main.Show()
    Pump-Ui 8

    $project = $mainType.GetField("_project", $flags).GetValue($main)
    $drawingObject = $project.DrawingObjects[0]
    $drawingScene = $drawingObject.Scene
    $drawingScene.CreateEmpty(1, 24)
    $addObject = $sceneType.GetMethods($flags) |
        Where-Object { $_.Name -eq "AddObject" -and $_.GetParameters().Count -eq 10 } |
        Select-Object -First 1
    $addObject.Invoke($drawingScene, [object[]] @(
        0,
        [Drawing.PointF]::new(80, 30),
        [Drawing.SizeF]::new(220, 140),
        [single]0,
        [single]0,
        [Drawing.Color]::Teal,
        [Drawing.Color]::Transparent,
        [uint32]8,
        [Enum]::Parse($shapeType, "Rectangle"),
        0)) | Out-Null

    $stage = $mainType.GetField("_stage", $flags).GetValue($main)
    $mouseDown = $mainType.GetMethod("StageMouseDown", $flags)
    $mouseUp = $mainType.GetMethod("StageMouseUp", $flags)
    $basicPoint = $stage.WorldToScreen([single]80, [single]30)
    $basicClick = [Windows.Forms.MouseEventArgs]::new(
        [Windows.Forms.MouseButtons]::Left,
        1,
        [int]$basicPoint.X,
        [int]$basicPoint.Y,
        0)
    $mouseDown.Invoke($main, @($stage, $basicClick))
    $mouseUp.Invoke($main, @($stage, $basicClick))
    Pump-Ui 3
    Assert-True ($mainType.GetField("_selectedObject", $flags).GetValue($main) -eq 0) `
        "Select could not choose basic drawing geometry through a click."
    $mainType.GetMethod("ClearSelection", $flags).Invoke($main, $null)

    $scene = $project.Scenes[0]
    $addInstance = $project.GetType().GetMethods($flags) |
        Where-Object { $_.Name -eq "TryAddSceneInstance" -and $_.GetParameters().Count -eq 5 } |
        Select-Object -First 1
    $instanceArgs = [object[]] @(
        $scene.Id,
        $drawingObject.Id,
        [Drawing.PointF]::new(320, 220),
        [single]0,
        $null)
    Assert-True $addInstance.Invoke($project, $instanceArgs) "Could not create the anchor verification instance."
    $instance = $instanceArgs[4]

    $showWorkspace = $mainType.GetMethod("ShowWorkspace", $flags)
    $showWorkspace.Invoke($main, @([Enum]::Parse($workspaceType, "SceneEditor")))
    Pump-Ui 8
    $scenePoint = $stage.WorldToScreen([single]400, [single]250)
    $sceneClick = [Windows.Forms.MouseEventArgs]::new(
        [Windows.Forms.MouseButtons]::Left,
        1,
        [int]$scenePoint.X,
        [int]$scenePoint.Y,
        0)
    $mouseDown.Invoke($main, @($stage, $sceneClick))
    $mouseUp.Invoke($main, @($stage, $sceneClick))
    Pump-Ui 3
    Assert-True ($mainType.GetField("_selectedSceneInstanceId", $flags).GetValue($main) -eq $instance.Id) `
        "Select could not choose a scene drawing-object instance through a click."
    $setInstanceSelection = $mainType.GetMethod(
        "SetSceneInstanceSelection",
        $flags,
        $null,
        [Type[]] @($instanceType, [bool]),
        $null)
    $setInstanceSelection.Invoke($main, @($instance, $false))
    $mainType.GetMethod("UpdateInspector", $flags).Invoke($main, $null)
    Pump-Ui 8

    $panel = [Windows.Forms.Control]$mainType.GetField("_drawingObjectInstancePanel", $flags).GetValue($main)
    $scenePage = $mainType.GetField("_sceneEditPage", $flags).GetValue($main)
    Assert-True ($panel.Visible -and [object]::ReferenceEquals($panel.Parent, $scenePage.Content)) `
        "The drawing-object controls were not shown in the Scene Edit inspector."

    $anchorX = $panel.GetType().GetField("_anchorX", $flags).GetValue($panel)
    $anchorY = $panel.GetType().GetField("_anchorY", $flags).GetValue($panel)
    $anchorX.Value = [decimal]35
    $anchorY.Value = [decimal]-20
    Pump-Ui 8
    Assert-True ($drawingObject.Anchor -eq [Drawing.PointF]::new(35, -20)) `
        "Editing Anchor X/Y did not update the shared drawing-object definition."

    $instancePosition = $instance.EvaluatePosition(0)
    Assert-True ($stage.DrawingObjectAnchorVisible -and $stage.DrawingObjectAnchor -eq $instancePosition) `
        "The Stage anchor marker did not follow the compensated instance registration point."

    $bitmap = [Drawing.Bitmap]::new(296, 260)
    $panel.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, 296, 260))
    $resolvedOutput = Join-Path $root $OutputPath
    $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)

    $showWorkspace.Invoke($main, @([Enum]::Parse($workspaceType, "BasicDrawing")))
    Pump-Ui 5
    $basicPage = $mainType.GetField("_basicInspectorPage", $flags).GetValue($main)
    Assert-True ([object]::ReferenceEquals($panel.Parent, $basicPage.Content)) `
        "The drawing-object controls did not return to the Basic Drawing inspector."

    "drawing_object_anchor=$($drawingObject.Anchor.X),$($drawingObject.Anchor.Y)"
    "instance_anchor=$($instancePosition.X),$($instancePosition.Y)"
    "scene_edit_panel=True"
    "basic_drawing_panel=True"
    "basic_click_selection=True"
    "scene_click_selection=True"
    "localized_panel=$resolvedOutput"
}
finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
