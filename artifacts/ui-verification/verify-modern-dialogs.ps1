$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"))
$flags = [Reflection.BindingFlags] "Instance,Public,NonPublic"
$staticFlags = [Reflection.BindingFlags] "Static,Public,NonPublic"
[Windows.Forms.Application]::EnableVisualStyles()

function Pump-Ui([int]$milliseconds) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($milliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 12
    }
}

function Capture-Dialog($dialog, $owner, [string]$path) {
    $dialog.Show($owner)
    [Windows.Forms.Application]::DoEvents()
    $motionStarted = $dialog.GetType().BaseType.GetProperty("IsDialogMotionRunning", $flags).GetValue($dialog)
    $motionOrigin = $dialog.GetType().BaseType.GetField("_motionOrigin", $flags).GetValue($dialog)
    $settledLocation = $dialog.GetType().BaseType.GetField("_settledLocation", $flags).GetValue($dialog)
    $startOpacity = $dialog.Opacity
    Pump-Ui 260
    $settledY = $dialog.Top
    if (-not $motionStarted -or $startOpacity -ge 1 -or $dialog.Opacity -lt 0.99 -or $dialog.GetType().BaseType.GetProperty("IsDialogMotionRunning", $flags).GetValue($dialog)) {
        throw "The dialog opening motion did not start and settle correctly: started=$motionStarted opacity=$startOpacity->$($dialog.Opacity)"
    }
    if (($motionOrigin.Y - $settledLocation.Y) -ne 12 -or $settledY -ne $settledLocation.Y) {
        throw "The dialog opening motion did not complete its 12px rise: origin=$($motionOrigin.Y) target=$($settledLocation.Y) settled=$settledY"
    }
    if ($dialog.FormBorderStyle -ne [Windows.Forms.FormBorderStyle]::None) {
        throw "The dialog still uses a system window frame."
    }

    $bitmap = [Drawing.Bitmap]::new($dialog.ClientSize.Width, $dialog.ClientSize.Height)
    $dialog.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    return $bitmap
}

function Find-Button([Windows.Forms.Control]$rootControl, [string]$text) {
    foreach ($control in $rootControl.Controls) {
        if ($control -is [Windows.Forms.Button] -and $control.Text -eq $text) { return $control }
        $found = Find-Button $control $text
        if ($null -ne $found) { return $found }
    }
    return $null
}

$localizationType = $assembly.GetType("VectorAnimationEngine.UiLocalization", $true)
$languageType = $assembly.GetType("VectorAnimationEngine.UiLanguage", $true)
$localizationType.GetMethod("SetLanguage", $staticFlags).Invoke(
    $null,
    @([Enum]::Parse($languageType, "SimplifiedChinese")))

$owner = [Windows.Forms.Form]::new()
$owner.ShowInTaskbar = $false
$owner.StartPosition = [Windows.Forms.FormStartPosition]::Manual
$owner.Location = [Drawing.Point]::new(-30000, -30000)
$owner.ClientSize = [Drawing.Size]::new(900, 700)
$settings = $null
$message = $null
$nameDialog = $null
$settingsBitmap = $null
$messageBitmap = $null
$nameBitmap = $null
$modalMessage = $null
$modalTimer = $null
try {
    $owner.Show()
    [Windows.Forms.Application]::DoEvents()

    $settingsType = $assembly.GetType("VectorAnimationEngine.SettingsDialog", $true)
    $settings = [Activator]::CreateInstance($settingsType, $true)
    $settingsPath = Join-Path $root "artifacts\ui-verification\modern-dialog-settings-zh.png"
    $settingsBitmap = Capture-Dialog $settings $owner $settingsPath
    $settingsTitle = $settingsType.BaseType.GetField("_titleLabel", $flags).GetValue($settings)
    $settingsActions = $settingsType.BaseType.GetProperty("DialogActions", $flags).GetValue($settings)
    if ($settingsTitle.Text -ne "设置" -or $settingsActions.Controls.Count -ne 2) {
        throw "The settings dialog did not use the localized shared title and command area."
    }
    $settings.Close()
    Pump-Ui 240

    $messageType = $assembly.GetType("VectorAnimationEngine.ModernMessageDialog", $true)
    $messageCtor = $messageType.GetConstructor(
        $flags,
        $null,
        [Type[]] @(
            [string],
            [string],
            [Windows.Forms.MessageBoxButtons],
            [Windows.Forms.MessageBoxIcon],
            [Windows.Forms.MessageBoxDefaultButton]),
        $null)
    $message = $messageCtor.Invoke(@(
        "删除 3 个图层及其中的全部内容？`r`n`r`n此操作会移除关联的关键帧和嵌套实例。",
        "删除图层",
        [Windows.Forms.MessageBoxButtons]::YesNo,
        [Windows.Forms.MessageBoxIcon]::Warning,
        [Windows.Forms.MessageBoxDefaultButton]::Button2))
    $messagePath = Join-Path $root "artifacts\ui-verification\modern-dialog-warning-zh.png"
    $messageBitmap = Capture-Dialog $message $owner $messagePath
    $yes = Find-Button $message "是"
    $no = Find-Button $message "否"
    if ($null -eq $yes -or $null -eq $no -or -not [object]::ReferenceEquals($message.AcceptButton, $no)) {
        throw "The warning dialog did not preserve a safe default action and localized buttons."
    }
    if ($yes.FlatAppearance.BorderColor.ToArgb() -eq $no.FlatAppearance.BorderColor.ToArgb()) {
        throw "The destructive and safe warning actions do not have distinct visual hierarchy."
    }
    $safeDefault = $message.AcceptButton.Text
    $message.Close()
    Pump-Ui 240

    $modalMessage = $messageCtor.Invoke(@(
        "确认保留当前内容？",
        "模态动画验证",
        [Windows.Forms.MessageBoxButtons]::YesNo,
        [Windows.Forms.MessageBoxIcon]::Question,
        [Windows.Forms.MessageBoxDefaultButton]::Button2))
    $modalTimer = [Windows.Forms.Timer]::new()
    $modalTimer.Interval = 260
    $modalTimer.add_Tick({
        $modalTimer.Stop()
        $safeAction = Find-Button $modalMessage "否"
        if ($null -ne $safeAction) { $safeAction.PerformClick() }
    })
    $modalTimer.Start()
    $modalResult = $modalMessage.ShowDialog($owner)
    if ($modalResult -ne [Windows.Forms.DialogResult]::No) {
        throw "The animated modal dialog returned the wrong result: $modalResult"
    }

    $nameType = $assembly.GetType("VectorAnimationEngine.MainForm+DrawingObjectNameDialog", $true)
    $nameCtor = $nameType.GetConstructor(
        $flags,
        $null,
        [Type[]] @([string], [string], [string]),
        $null)
    $nameDialog = $nameCtor.Invoke(@("重命名绘制对象", "绘制对象名称", "角色_身体"))
    $namePath = Join-Path $root "artifacts\ui-verification\modern-dialog-name-zh.png"
    $nameBitmap = Capture-Dialog $nameDialog $owner $namePath
    $input = $nameType.GetField("_input", $flags).GetValue($nameDialog)
    if ($input.Width -lt 300 -or $input.Bottom -gt $nameDialog.ClientSize.Height - 58) {
        throw "The modern name dialog input is clipped or overlaps its command area."
    }

    "modern_dialog_settings=$settingsPath"
    "modern_dialog_warning=$messagePath"
    "modern_dialog_name=$namePath"
    "modern_dialog_open_motion=ok"
    "modern_dialog_close_motion=ok"
    "modern_dialog_safe_default=$safeDefault"
    "modern_dialog_modal_result=$modalResult"
}
finally {
    if ($null -ne $settingsBitmap) { $settingsBitmap.Dispose() }
    if ($null -ne $messageBitmap) { $messageBitmap.Dispose() }
    if ($null -ne $nameBitmap) { $nameBitmap.Dispose() }
    if ($null -ne $modalTimer) { $modalTimer.Dispose() }
    foreach ($dialog in @($settings, $message, $nameDialog, $modalMessage)) {
        if ($null -ne $dialog -and -not $dialog.IsDisposed) {
            $dialog.Dispose()
        }
    }
    if (-not $owner.IsDisposed) { $owner.Close() }
    $owner.Dispose()
}
