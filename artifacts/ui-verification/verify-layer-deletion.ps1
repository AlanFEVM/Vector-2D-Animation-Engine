$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class LayerDeletionMessageBoxAutomation
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public static bool Handled { get; private set; }

    public static void BeginChoice(int commandId)
    {
        Handled = false;
        var thread = new Thread(() =>
        {
            for (var attempt = 0; attempt < 160; attempt++)
            {
                Thread.Sleep(25);
                foreach (var title in new[] { "Delete Layers", "删除图层" })
                {
                    var window = FindWindow(null, title);
                    if (window == IntPtr.Zero) continue;
                    PostMessage(window, 0x0111, new IntPtr(commandId), IntPtr.Zero);
                    Handled = true;
                    return;
                }
            }
        }) { IsBackground = true };
        thread.Start();
    }
}
"@

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

function Invoke-MessageBoxChoice($menuItem, [int]$commandId) {
    [LayerDeletionMessageBoxAutomation]::BeginChoice($commandId)
    $menuItem.PerformClick()
    if (-not [LayerDeletionMessageBoxAutomation]::Handled) {
        throw "The batch layer deletion confirmation dialog was not shown."
    }
}

$mainType = $assembly.GetType("VectorAnimationEngine.MainForm", $true)
$main = [Activator]::CreateInstance($mainType, $true)

try {
    $main.ShowInTaskbar = $false
    $main.Opacity = 0.01
    $main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $main.Location = [Drawing.Point]::new(40, 40)
    $main.Size = [Drawing.Size]::new(1280, 800)
    $main.Show()
    Pump-Ui 450

    $project = $mainType.GetField("_project", $flags).GetValue($main)
    $drawingObject = $project.GetType().GetProperty("DrawingObjects").GetValue($project)[0]
    $scene = $drawingObject.GetType().GetProperty("Scene", $flags).GetValue($drawingObject)
    1..3 | ForEach-Object { $scene.AddLayer("Delete Test $_") | Out-Null }

    $mainType.GetMethod("BindActiveDrawingObjectScene", $flags).Invoke($main, @($false))
    $timeline = $mainType.GetField("_timeline", $flags).GetValue($main)
    $timeline.RefreshTimeline()
    Pump-Ui 120

    $timelineType = $timeline.GetType()
    $selectLayer = $timelineType.GetMethod("SelectLayerTrack", $flags)
    $opening = $timelineType.GetMethod("HandleLayerContextMenuOpening", $flags)
    $removeItem = $timelineType.GetField("_removeLayersMenuItem", $flags).GetValue($timeline)
    $layerCount = $scene.GetType().GetProperty("LayerCount", $flags)

    $selectLayer.Invoke($timeline, @([int]1, [Windows.Forms.Keys]::None))
    $opening.Invoke($timeline, @($null, [ComponentModel.CancelEventArgs]::new()))
    if (-not $removeItem.Enabled -or $removeItem.Text -notin @("Delete Layer", "删除图层")) {
        throw "The single-layer deletion command was not available in the layer context menu."
    }
    $removeItem.PerformClick()
    Pump-Ui 120
    if ($layerCount.GetValue($scene) -ne 3) { throw "Single-layer deletion did not remove exactly one layer." }

    if (-not $mainType.GetMethod("UndoLastEdit", $flags).Invoke($main, @())) {
        throw "Single-layer deletion did not create an undo entry."
    }
    Pump-Ui 120
    if ($layerCount.GetValue($scene) -ne 4) { throw "Undo did not restore the deleted layer." }

    $selectLayer.Invoke($timeline, @([int]1, [Windows.Forms.Keys]::None))
    $selectLayer.Invoke($timeline, @([int]2, [Windows.Forms.Keys]::Control))
    $opening.Invoke($timeline, @($null, [ComponentModel.CancelEventArgs]::new()))
    if ($removeItem.Text -notin @("Delete Selected Layers", "删除所选图层")) {
        throw "The context menu did not expose its batch layer deletion label."
    }

    Invoke-MessageBoxChoice $removeItem 7
    Pump-Ui 80
    if ($layerCount.GetValue($scene) -ne 4) { throw "Canceling batch layer deletion changed the model." }

    Invoke-MessageBoxChoice $removeItem 6
    Pump-Ui 100
    if ($layerCount.GetValue($scene) -ne 2) { throw "Confirming batch layer deletion did not remove both selected layers." }

    $feedbackTargets = $timelineType.GetField("_layerFeedbackTargets", $flags).GetValue($timeline)
    if ($feedbackTargets.Count -ne 2 -or @($feedbackTargets | Where-Object { [string]$_.Kind -ne "Remove" }).Count -ne 0) {
        throw "Batch layer deletion did not trigger removal feedback for both deleted rows."
    }

    "layer_delete_context_menu=True"
    "layer_delete_single_without_confirmation=True"
    "layer_delete_batch_cancel_preserved=True"
    "layer_delete_batch_confirm_removed=2"
    "layer_delete_feedback_targets=$($feedbackTargets.Count)"
}
finally {
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
