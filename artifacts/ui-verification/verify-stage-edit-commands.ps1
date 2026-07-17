$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

function Pump-Ui([int]$milliseconds) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $milliseconds) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 8
    }
    [Windows.Forms.Application]::DoEvents()
}

function Invoke-CanvasShortcut($main, $mainType, [Windows.Forms.Keys]$keys) {
    $message = [Windows.Forms.Message]::Create($main.Handle, 0, [IntPtr]::Zero, [IntPtr]::Zero)
    $arguments = [object[]] @($message, $keys)
    return $mainType.GetMethod("ProcessCmdKey", $flags).Invoke($main, $arguments)
}

$localizationType = $assembly.GetType("VectorAnimationEngine.UiLocalization", $true)
$languageType = $assembly.GetType("VectorAnimationEngine.UiLanguage", $true)
$localizationType.GetMethod("SetLanguage").Invoke(
    $null,
    @([Enum]::Parse($languageType, "SimplifiedChinese")))

$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$sceneType = $assembly.GetType("VectorAnimationEngine.VectorScene", $true)
$shapeType = $assembly.GetType("VectorAnimationEngine.ShapeKind", $true)
$rectangle = [Enum]::Parse($shapeType, "Rectangle")
$triangle = [Enum]::Parse($shapeType, "Triangle")
$addObject = $sceneType.GetMethods($flags) |
    Where-Object { $_.Name -eq "AddObject" -and $_.GetParameters().Count -eq 10 } |
    Select-Object -First 1
$main = [Activator]::CreateInstance($mainType, $true)
$menuBitmap = $null

try {
    $main.ShowInTaskbar = $false
    $main.Opacity = 0.01
    $main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $main.Location = [Drawing.Point]::new(40, 40)
    $main.Size = [Drawing.Size]::new(1280, 800)
    $main.Show()
    Pump-Ui 500

    $scene = $mainType.GetField("_scene", $flags).GetValue($main)
    $stage = [Windows.Forms.Control]$mainType.GetField("_stage", $flags).GetValue($main)
    $setSelection = $mainType.GetMethod(
        "SetSelection",
        $flags,
        $null,
        [Type[]] @([int]),
        $null)

    $sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(1, 24))
    $first = $addObject.Invoke($scene, [object[]] @(
        0, [Drawing.PointF]::new(-100, 0), [Drawing.SizeF]::new(120, 80), [single]0, [single]0,
        [Drawing.Color]::Teal, [Drawing.Color]::Transparent, [uint32]8, $rectangle, 0))
    $cutTarget = $addObject.Invoke($scene, [object[]] @(
        0, [Drawing.PointF]::new(100, 0), [Drawing.SizeF]::new(120, 80), [single]0, [single]0,
        [Drawing.Color]::Coral, [Drawing.Color]::Transparent, [uint32]8, $triangle, 0))
    $setSelection.Invoke($main, @($cutTarget))
    $stage.Focus() | Out-Null
    $countBeforeCut = $sceneType.GetProperty("ObjectCount").GetValue($scene)
    $cutHandled = Invoke-CanvasShortcut $main $mainType ([Windows.Forms.Keys]::Control -bor [Windows.Forms.Keys]::X)
    $countAfterCut = $sceneType.GetProperty("ObjectCount").GetValue($scene)
    $clipboard = $mainType.GetField("_clipboardObjects", $flags).GetValue($main)
    if (-not $cutHandled -or $countAfterCut -ne ($countBeforeCut - 1) -or $clipboard.Count -ne 1) {
        throw "Ctrl+X did not copy and remove the selected drawing object."
    }

    $sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(1, 24))
    $flipTarget = $addObject.Invoke($scene, [object[]] @(
        0, [Drawing.PointF]::new(0, 0), [Drawing.SizeF]::new(140, 100), [single]0, [single]0,
        [Drawing.Color]::Coral, [Drawing.Color]::Transparent, [uint32]8, $triangle, 0))
    $setSelection.Invoke($main, @($flipTarget))
    $flipHorizontal = [Windows.Forms.ToolStripMenuItem]$mainType.GetField("_flipHorizontalMenuItem", $flags).GetValue($main)
    $flipVertical = [Windows.Forms.ToolStripMenuItem]$mainType.GetField("_flipVerticalMenuItem", $flags).GetValue($main)
    $flipHorizontal.PerformClick()
    $flipVertical.PerformClick()
    $shapeKinds = $sceneType.GetProperty("ShapeKind").GetValue($scene)
    if ($shapeKinds[$flipTarget].ToString() -ne "Path") {
        throw "The Stage flip commands did not preserve mirrored asymmetric geometry as a Path."
    }

    $sceneType.GetMethod("CreateEmpty", $flags).Invoke($scene, @(1, 24))
    $back = $addObject.Invoke($scene, [object[]] @(
        0, [Drawing.PointF]::new(0, 0), [Drawing.SizeF]::new(180, 120), [single]0, [single]0,
        [Drawing.Color]::Teal, [Drawing.Color]::Transparent, [uint32]8, $rectangle, 0))
    $middle = $addObject.Invoke($scene, [object[]] @(
        0, [Drawing.PointF]::new(0, 0), [Drawing.SizeF]::new(160, 100), [single]0, [single]0,
        [Drawing.Color]::Coral, [Drawing.Color]::Transparent, [uint32]8, $rectangle, 0))
    $front = $addObject.Invoke($scene, [object[]] @(
        0, [Drawing.PointF]::new(0, 0), [Drawing.SizeF]::new(140, 80), [single]0, [single]0,
        [Drawing.Color]::Gold, [Drawing.Color]::Transparent, [uint32]8, $rectangle, 0))
    $orders = $sceneType.GetProperty("ObjectOrder").GetValue($scene)
    $setSelection.Invoke($main, @($back))
    $stage.Focus() | Out-Null
    $forwardHandled = Invoke-CanvasShortcut $main $mainType ([Windows.Forms.Keys]::Control -bor [Windows.Forms.Keys]::Up)
    $forwardOrder = $orders[$back]
    $backwardHandled = Invoke-CanvasShortcut $main $mainType ([Windows.Forms.Keys]::Control -bor [Windows.Forms.Keys]::Down)
    if (-not $forwardHandled -or -not $backwardHandled -or $forwardOrder -ne 2 -or $orders[$back] -ne 1) {
        throw "Ctrl+Up/Ctrl+Down did not move the selected object exactly one level."
    }

    $bringForward = [Windows.Forms.ToolStripMenuItem]$mainType.GetField("_bringForwardMenuItem", $flags).GetValue($main)
    $sendBackward = [Windows.Forms.ToolStripMenuItem]$mainType.GetField("_sendBackwardMenuItem", $flags).GetValue($main)
    if ($flipHorizontal.Text -ne "水平翻转" -or
        $flipVertical.Text -ne "垂直翻转" -or
        $bringForward.Text -ne "上移一层" -or
        $sendBackward.Text -ne "下移一层" -or
        $bringForward.ShortcutKeys -ne ([Windows.Forms.Keys]::Control -bor [Windows.Forms.Keys]::Up) -or
        $sendBackward.ShortcutKeys -ne ([Windows.Forms.Keys]::Control -bor [Windows.Forms.Keys]::Down)) {
        throw "The localized Stage context commands or shortcut hints are incorrect."
    }

    $projectType = $assembly.GetType("VectorAnimationEngine.VectorProject", $true)
    $instanceType = $assembly.GetType("VectorAnimationEngine.DrawingObjectInstanceDefinition", $true)
    $project = $mainType.GetField("_project", $flags).GetValue($main)
    $child = $projectType.GetMethod("AddDrawingObject", $flags).Invoke($project, @("Clipboard Child"))
    $childScene = $child.GetType().GetProperty("Scene").GetValue($child)
    $addObject.Invoke($childScene, [object[]] @(
        0, [Drawing.PointF]::new(0, 0), [Drawing.SizeF]::new(120, 80), [single]0, [single]0,
        [Drawing.Color]::CornflowerBlue, [Drawing.Color]::Transparent, [uint32]8, $rectangle, 0)) | Out-Null
    $drawingObjects = $projectType.GetProperty("DrawingObjects").GetValue($project)
    $container = $drawingObjects[0]
    $tryAddInstance = $projectType.GetMethod(
        "TryAddDrawingObjectInstance",
        $flags,
        $null,
        [Type[]] @([string], [string], [Drawing.PointF], $instanceType.MakeByRefType()),
        $null)
    $containerId = $container.GetType().GetProperty("Id").GetValue($container)
    $childId = $child.GetType().GetProperty("Id").GetValue($child)
    $firstArguments = [object[]] @($containerId, $childId, [Drawing.PointF]::new(0, 0), $null)
    $secondArguments = [object[]] @($containerId, $childId, [Drawing.PointF]::new(200, 0), $null)
    if (-not $tryAddInstance.Invoke($project, $firstArguments) -or
        -not $tryAddInstance.Invoke($project, $secondArguments)) {
        throw "Could not create nested instances for Stage command verification."
    }
    $firstInstance = $firstArguments[3]
    $secondInstance = $secondArguments[3]
    $instanceType.GetMethod("SetPositionAtFrame", $flags).Invoke(
        $firstInstance,
        @(4, [Drawing.PointF]::new(40, 30))) | Out-Null
    $mainType.GetMethod("BindActiveDrawingObjectScene", $flags).Invoke($main, @($false))
    $setInstanceSelection = $mainType.GetMethod(
        "SetSceneInstanceSelection",
        $flags,
        $null,
        [Type[]] @($instanceType, [bool]),
        $null)
    $setInstanceSelection.Invoke($main, @($firstInstance, $false))
    $stage.Focus() | Out-Null
    $nestedCutHandled = Invoke-CanvasShortcut $main $mainType ([Windows.Forms.Keys]::Control -bor [Windows.Forms.Keys]::X)
    $instanceClipboard = $mainType.GetField("_clipboardDrawingObjectInstances", $flags).GetValue($main)
    $nestedPasteHandled = Invoke-CanvasShortcut $main $mainType ([Windows.Forms.Keys]::Control -bor [Windows.Forms.Keys]::V)
    $instances = $container.GetType().GetProperty("Instances").GetValue($container)
    $pastedInstance = $instances[$instances.Count - 1]
    $pastedKeyframes = $instanceType.GetProperty("StateKeyframes").GetValue($pastedInstance)
    $pastedPosition = $instanceType.GetMethod("EvaluatePosition", $flags).Invoke($pastedInstance, @(4))
    if (-not $nestedCutHandled -or
        -not $nestedPasteHandled -or
        $instanceClipboard.Count -ne 1 -or
        $instances.Count -ne 2 -or
        @($pastedKeyframes | Where-Object { $_.Frame -eq 4 }).Count -ne 1 -or
        [Math]::Abs($pastedPosition.X - 136) -gt 0.001 -or
        [Math]::Abs($pastedPosition.Y - 126) -gt 0.001) {
        throw "Ctrl+X/Ctrl+V did not preserve nested-instance animation state."
    }

    $flipHorizontal.PerformClick()
    $flippedState = $instanceType.GetMethod("EvaluateState", $flags).Invoke($pastedInstance, @(0))
    $stage.Focus() | Out-Null
    $nestedBackwardHandled = Invoke-CanvasShortcut $main $mainType ([Windows.Forms.Keys]::Control -bor [Windows.Forms.Keys]::Down)
    $nestedForwardHandled = Invoke-CanvasShortcut $main $mainType ([Windows.Forms.Keys]::Control -bor [Windows.Forms.Keys]::Up)
    if ($flippedState.ScaleX -ge 0 -or
        -not $nestedBackwardHandled -or
        -not $nestedForwardHandled -or
        -not [Object]::ReferenceEquals($instances[$instances.Count - 1], $pastedInstance)) {
        throw "Nested-instance flip or single-layer stack shortcuts failed."
    }

    $showMenu = $mainType.GetMethod("ShowStageContextMenu", $flags)
    $worldToScreen = $stage.GetType().GetMethod(
        "WorldToScreen",
        $flags,
        $null,
        [Type[]] @([single], [single]),
        $null)
    $instanceScreen = $worldToScreen.Invoke(
        $stage,
        @([single]$flippedState.X, [single]$flippedState.Y))
    $showMenu.Invoke($main, @([Drawing.Point]::Round($instanceScreen)))
    Pump-Ui 250
    $menu = [Windows.Forms.ContextMenuStrip]$mainType.GetField("_stageContextMenu", $flags).GetValue($main)
    $menu.PerformLayout()
    $menuBitmap = [Drawing.Bitmap]::new($menu.Width, $menu.Height)
    $menu.DrawToBitmap($menuBitmap, [Drawing.Rectangle]::new(0, 0, $menu.Width, $menu.Height))
    $screenshot = Join-Path $root "artifacts\ui-verification\stage-context-transform-menu.png"
    $menuBitmap.Save($screenshot, [Drawing.Imaging.ImageFormat]::Png)

    "stage_ctrl_x_cut=True"
    "stage_flip_horizontal_vertical=True"
    "stage_ctrl_up_down_stack=True"
    "stage_context_menu_localized=True"
    "stage_nested_instance_cut_paste=True"
    "stage_nested_instance_flip_stack=True"
    "stage_context_menu_screenshot=$screenshot"
}
finally {
    if ($null -ne $menuBitmap) { $menuBitmap.Dispose() }
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
