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

$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$projectType = $assembly.GetType("VectorAnimationEngine.VectorProject", $true)
$instanceType = $assembly.GetType("VectorAnimationEngine.DrawingObjectInstanceDefinition", $true)
$editType = $assembly.GetType("VectorAnimationEngine.MainForm+TimelineEditKind", $true)
$main = [Activator]::CreateInstance($mainType, $true)

try {
    $main.ShowInTaskbar = $false
    $main.Opacity = 0.01
    $main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $main.Location = [Drawing.Point]::new(40, 40)
    $main.Size = [Drawing.Size]::new(1280, 800)
    $main.Show()
    Pump-Ui 500

    $project = $mainType.GetField("_project", $flags).GetValue($main)
    $child = $projectType.GetMethod("AddDrawingObject", $flags).Invoke($project, @("Selection Child"))
    $drawingObjects = $projectType.GetProperty("DrawingObjects").GetValue($project)
    $container = $drawingObjects[0]
    $tryAddInstance = $projectType.GetMethod(
        "TryAddDrawingObjectInstance",
        $flags,
        $null,
        [Type[]] @([string], [string], [Drawing.PointF], $instanceType.MakeByRefType()),
        $null)
    $addArguments = [object[]] @(
        $container.GetType().GetProperty("Id").GetValue($container),
        $child.GetType().GetProperty("Id").GetValue($child),
        [Drawing.PointF]::new(48, 32),
        $null)
    if (-not $tryAddInstance.Invoke($project, $addArguments)) {
        throw "Could not create the nested drawing-object instance for verification."
    }
    $instance = $addArguments[3]
    Pump-Ui 250

    $mainType.GetMethod("BindActiveDrawingObjectScene", $flags).Invoke($main, @($false))
    $setSelection = $mainType.GetMethod(
        "SetSceneInstanceSelection",
        $flags,
        $null,
        [Type[]] @($instanceType, [bool]),
        $null)
    $setSelection.Invoke($main, @($instance, $false))

    $insertKeyframe = [Enum]::Parse($editType, "InsertKeyframe")
    $changed = $mainType.GetMethod("ExecuteTimelineEdit", $flags).Invoke(
        $main,
        [object[]] @($insertKeyframe, $null))
    Pump-Ui 250

    $instanceId = $instanceType.GetProperty("Id").GetValue($instance)
    $selectedIds = $mainType.GetField("_selectedSceneInstanceIds", $flags).GetValue($main)
    $primaryId = $mainType.GetField("_selectedSceneInstanceId", $flags).GetValue($main)
    $frame = $mainType.GetField("_frame", $flags).GetValue($main)
    $stateKeyframes = $instanceType.GetProperty("StateKeyframes").GetValue($instance)
    $hasFrameOneState = @($stateKeyframes | Where-Object { $_.Frame -eq 1 }).Count -eq 1

    if (-not $changed -or
        $frame -ne 1 -or
        -not $selectedIds.Contains($instanceId) -or
        $primaryId -ne $instanceId -or
        -not $hasFrameOneState) {
        throw "F6 did not preserve the selected drawing-object instance on the next keyframe."
    }

    "keyframe_instance_selection_preserved=True"
    "keyframe_instance_primary_id_preserved=True"
    "keyframe_instance_target_frame=$frame"
    "keyframe_instance_state_key_created=$hasFrameOneState"
}
finally {
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
