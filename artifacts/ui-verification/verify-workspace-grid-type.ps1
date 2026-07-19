$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"
$languageType = $assembly.GetType("VectorAnimationEngine.UiLanguage", $true)
$localizationType = $assembly.GetType("VectorAnimationEngine.UiLocalization", $true)
$localizationType.GetMethod("SetLanguage", $staticFlags).Invoke(
    $null,
    @([Enum]::Parse($languageType, "SimplifiedChinese")))

$tabsType = $assembly.GetType("VectorAnimationEngine.WorkspaceTabs", $true)
$gridType = $assembly.GetType("VectorAnimationEngine.WorldGridType", $true)
$goldenSpiral = [Enum]::Parse($gridType, "GoldenSpiral")
$polarGrid = [Enum]::Parse($gridType, "Polar")
$tabs = [Activator]::CreateInstance($tabsType, $true)
$localizationType.GetMethod("Watch", $staticFlags, $null, [Type[]]@([Windows.Forms.Control]), $null).Invoke($null, @($tabs))
$form = [Windows.Forms.Form]::new()
$bitmap = $null
try {
    $form.ShowInTaskbar = $false
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = [Drawing.Point]::new(-30000, -30000)
    $form.ClientSize = [Drawing.Size]::new(760, 44)
    $tabs.Dock = [Windows.Forms.DockStyle]::Fill
    $form.Controls.Add($tabs)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()

    $tabsType.GetProperty("WorldGridType", $flags).SetValue($tabs, $goldenSpiral)
    $tabs.PerformLayout()
    [Windows.Forms.Application]::DoEvents()

    $button = $tabsType.GetField("_gridTypeButton", $flags).GetValue($tabs)
    $cartesian = $tabsType.GetField("_cartesianGridItem", $flags).GetValue($tabs)
    $spiral = $tabsType.GetField("_goldenSpiralGridItem", $flags).GetValue($tabs)
    $polar = $tabsType.GetField("_polarGridItem", $flags).GetValue($tabs)
    if (($button.Icon.ToString() -ne "GoldenSpiral") -or
        $cartesian.Checked -or
        (-not $spiral.Checked) -or
        ($cartesian.Text -ne "直角坐标网格") -or
        ($spiral.Text -ne "黄金比例螺旋线") -or
        ($polar.Text -ne "极坐标网格")) {
        throw "The top grid selector did not update its icon, checked state, or Chinese menu labels."
    }

    $tabsType.GetProperty("WorldGridType", $flags).SetValue($tabs, $polarGrid)
    [Windows.Forms.Application]::DoEvents()
    if (($button.Icon.ToString() -ne "PolarGrid") -or
        $cartesian.Checked -or
        $spiral.Checked -or
        (-not $polar.Checked)) {
        throw "The top grid selector did not activate the polar-grid icon and checked state."
    }

    $bitmap = [Drawing.Bitmap]::new($tabs.Width, $tabs.Height)
    $tabs.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $tabs.Width, $tabs.Height))
    $output = Join-Path $root "artifacts\ui-verification\workspace-tabs-polar-grid-zh.png"
    $bitmap.Save($output, [Drawing.Imaging.ImageFormat]::Png)
    "workspace_grid_type=Polar"
    "workspace_grid_cartesian_label=$($cartesian.Text)"
    "workspace_grid_polar_label=$($polar.Text)"
    "workspace_grid_spiral_label=$($spiral.Text)"
    "workspace_grid_type_screenshot=$output"
}
finally {
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if (-not $tabs.IsDisposed) { $tabs.Dispose() }
}
