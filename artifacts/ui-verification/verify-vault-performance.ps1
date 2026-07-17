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
$main = [Activator]::CreateInstance($mainType, $true)
$main.Opacity = 0.01
$main.ShowInTaskbar = $false
$main.StartPosition = [Windows.Forms.FormStartPosition]::Manual
$main.Location = New-Object Drawing.Point(40, 40)
$main.Size = New-Object Drawing.Size(1280, 800)

$panel = $null
$panelHost = $null
$panelBitmap = $null
try {
    $main.Show()
    Pump-Ui 600
    $stage = [Windows.Forms.Control]$mainType.GetField("_stage", $flags).GetValue($main)
    $drawer = [Windows.Forms.Control]$mainType.GetField("_vaultDrawer", $flags).GetValue($main)
    $vaultButton = [Windows.Forms.Control]$mainType.GetField("_vaultButton", $flags).GetValue($main)
    $vaultPanel = $mainType.GetField("_libraryVaultPanel", $flags).GetValue($main)
    $visibleWidthField = $mainType.GetField("_vaultDrawerVisibleWidth", $flags)
    $drawerTimer = [Windows.Forms.Timer]$mainType.GetField("_vaultDrawerTimer", $flags).GetValue($main)
    $vaultPanelType = $vaultPanel.GetType()
    $projectList = [Windows.Forms.ListView]$vaultPanelType.GetField("_projectObjects", $flags).GetValue($vaultPanel)
    $firstRowBefore = if ($projectList.Items.Count -gt 0) { $projectList.Items[0] } else { $null }
    $stageWidth = $stage.Width
    $sampledStageWidths = New-Object Collections.Generic.List[int]
    $sampledDrawerLefts = New-Object Collections.Generic.List[int]
    $timerDrawerWidths = New-Object Collections.Generic.List[int]
    $timerSampleHandler = [EventHandler] {
        param($sender, $eventArgs)
        $timerDrawerWidths.Add([int]$visibleWidthField.GetValue($main))
    }
    $drawerTimer.add_Tick($timerSampleHandler)
    $toggle = $mainType.GetMethod("ToggleVaultDrawer", $flags)

    $toggle.Invoke($main, @())
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt 240) {
        [Windows.Forms.Application]::DoEvents()
        $sampledStageWidths.Add($stage.Width)
        $sampledDrawerLefts.Add([int]$visibleWidthField.GetValue($main))
        Start-Sleep -Milliseconds 8
    }
    [Windows.Forms.Application]::DoEvents()
    $firstRowAfter = if ($projectList.Items.Count -gt 0) { $projectList.Items[0] } else { $null }

    if (@($sampledStageWidths | Where-Object { $_ -ne $stageWidth }).Count -gt 0) {
        throw "Opening the Vault changed the Stage width during animation."
    }
    if ($drawer.Left -ne 0 -or [int]$visibleWidthField.GetValue($main) -ne 306 -or -not $drawer.Visible) {
        throw "The Vault drawer did not finish opening at its stable overlay position."
    }
    if ($vaultButton.Parent.Left -lt $vaultPanel.Right) {
        throw "The Vault tool button was covered by the overlay drawer."
    }
    $openToolStripLeft = $vaultButton.Parent.Left
    $uniqueDrawerLefts = @($timerDrawerWidths | Select-Object -Unique)
    if ($uniqueDrawerLefts.Count -lt 3) {
        throw "The Vault drawer did not expose enough intermediate animation positions: $($uniqueDrawerLefts -join ',')."
    }
    if ($null -ne $firstRowBefore -and -not [Object]::ReferenceEquals($firstRowBefore, $firstRowAfter)) {
        throw "Opening an unchanged Vault rebuilt the project-object ListView rows."
    }

    $toggle.Invoke($main, @())
    Pump-Ui 240
    $drawerTimer.remove_Tick($timerSampleHandler)
    if ($drawer.Visible -or [int]$visibleWidthField.GetValue($main) -ne 0 -or $stage.Width -ne $stageWidth) {
        throw "Closing the Vault did not restore the hidden overlay state without resizing the Stage."
    }

    $projectType = $assembly.GetType("VectorAnimationEngine.VectorProject", $true)
    $project = [Activator]::CreateInstance($projectType, $true)
    $addDrawingObject = $projectType.GetMethod("AddDrawingObject", $flags)
    for ($index = 1; $index -lt 300; $index++) {
        $addDrawingObject.Invoke($project, @("Object $index")) | Out-Null
    }

    $localizationType = $assembly.GetType("VectorAnimationEngine.UiLocalization", $true)
    $languageType = $assembly.GetType("VectorAnimationEngine.UiLanguage", $true)
    $chinese = [Enum]::Parse($languageType, "SimplifiedChinese")
    $localizationType.GetMethod("SetLanguage").Invoke($null, @($chinese))

    $panelType = $assembly.GetType("VectorAnimationEngine.LibraryVaultPanel", $true)
    $panel = [Activator]::CreateInstance($panelType, $true)
    $localizationType.GetMethod(
        "Watch",
        [Reflection.BindingFlags] "Public,Static",
        $null,
        [Type[]] @([Windows.Forms.Control]),
        $null).Invoke($null, @($panel))
    $panelHost = New-Object Windows.Forms.Form
    $panelHost.ShowInTaskbar = $false
    $panelHost.FormBorderStyle = [Windows.Forms.FormBorderStyle]::None
    $panelHost.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $panelHost.Location = New-Object Drawing.Point(50, 50)
    $panelHost.ClientSize = New-Object Drawing.Size(306, 620)
    $panelHost.Opacity = 0.01
    $panel.Dock = [Windows.Forms.DockStyle]::Fill
    $panelHost.Controls.Add($panel)
    $panelHost.Show()
    [Windows.Forms.Application]::DoEvents()

    $bind = $panelType.GetMethod("BindProject", $flags)
    $buildWatch = [Diagnostics.Stopwatch]::StartNew()
    $bind.Invoke($panel, [object[]] @($project, $null)) | Out-Null
    [Windows.Forms.Application]::DoEvents()
    $buildWatch.Stop()

    $largeList = [Windows.Forms.ListView]$panelType.GetField("_projectObjects", $flags).GetValue($panel)
    $largeList.Items[0].Selected = $true
    $panel.PerformLayout()
    [Windows.Forms.Application]::DoEvents()
    $panelBitmap = New-Object Drawing.Bitmap(306, 620)
    $panel.DrawToBitmap($panelBitmap, (New-Object Drawing.Rectangle(0, 0, 306, 620)))
    $screenshotPath = Join-Path $root "artifacts\ui-verification\vault-compact-projects-306.png"
    $panelBitmap.Save($screenshotPath, [Drawing.Imaging.ImageFormat]::Png)

    $largeFirstRow = $largeList.Items[0]
    $refresh = $panelType.GetMethod("RefreshProjectObjects", $flags)
    $refreshWatch = [Diagnostics.Stopwatch]::StartNew()
    for ($iteration = 0; $iteration -lt 100; $iteration++) {
        $refresh.Invoke($panel, @()) | Out-Null
    }
    $refreshWatch.Stop()
    $largeFirstRowAfter = $largeList.Items[0]
    $cachedAverage = $refreshWatch.Elapsed.TotalMilliseconds / 100

    if ($largeList.Items.Count -ne 300) {
        throw "The large Vault verification did not bind all project objects."
    }
    if (-not [Object]::ReferenceEquals($largeFirstRow, $largeFirstRowAfter)) {
        throw "Cached Vault refreshes rebuilt ListView rows."
    }

    "vault_stage_width=$stageWidth"
    "vault_animation_unique_stage_widths=$(@($sampledStageWidths | Select-Object -Unique).Count)"
    "vault_animation_unique_positions=$($uniqueDrawerLefts.Count)"
    "vault_open_tool_strip_left=$openToolStripLeft"
    "vault_large_project_rows=$($largeList.Items.Count)"
    "vault_large_project_first_build_ms=$([Math]::Round($buildWatch.Elapsed.TotalMilliseconds, 3))"
    "vault_cached_refresh_avg_ms=$([Math]::Round($cachedAverage, 3))"
    "vault_cached_rows_reused=$([Object]::ReferenceEquals($largeFirstRow, $largeFirstRowAfter))"
    "vault_compact_screenshot=$screenshotPath"
}
finally {
    if ($null -ne $panelBitmap) { $panelBitmap.Dispose() }
    if ($null -ne $panelHost) {
        if (-not $panelHost.IsDisposed) { $panelHost.Close() }
        $panelHost.Dispose()
    }
    if ($null -ne $panel -and -not $panel.IsDisposed) { $panel.Dispose() }
    if (-not $main.IsDisposed) { $main.Close() }
    $main.Dispose()
}
