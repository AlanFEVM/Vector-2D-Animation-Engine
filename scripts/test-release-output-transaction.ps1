[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$publishScript = Join-Path $PSScriptRoot "publish-single-exe.ps1"
$parseErrors = $null
$tokens = $null
$syntax = [Management.Automation.Language.Parser]::ParseFile($publishScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
$transaction = $syntax.Find({
    param($node)
    $node -is [Management.Automation.Language.TryStatementAst] `
        -and $node.Body.Statements[0].Extent.Text -eq 'Copy-Item -LiteralPath $builtExe -Destination $pendingExe'
}, $true)
if ($null -eq $transaction) { throw "Could not locate the production release output transaction." }
$transactionScript = [scriptblock]::Create($transaction.Extent.Text)
$testRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ("..\artifacts\release-transaction-tests-" + [Guid]::NewGuid().ToString("N"))))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\artifacts")) + [IO.Path]::DirectorySeparatorChar
if (-not $testRoot.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe test root." }

try {
    foreach ($failureMode in @("None", "BackupCleanup", "Commit", "Rollback", "Cancel")) {
        & {
            param($failureMode)
            $caseRoot = Join-Path $testRoot $failureMode
            $OutputDirectory = Join-Path $caseRoot "output"
            New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
            $builtExe = Join-Path $caseRoot "built.exe"
            $finalExe = Join-Path $OutputDirectory "release.exe"
            $pendingExe = Join-Path $OutputDirectory ".pending.tmp"
            $backupDirectory = Join-Path $caseRoot ".v2d-release-backup-test"
            [IO.File]::WriteAllText($builtExe, "verified new release")
            [IO.File]::WriteAllText($finalExe, "last successful release")
            $existingOutputs = @(Get-ChildItem -LiteralPath $OutputDirectory -Force)
            $size = (Get-Item -LiteralPath $builtExe).Length
            $Version = "0.0.0"
            $assemblyVersion = "0.0.0.0"
            $maximumExeBytes = 5MB
            $resolvedResultPath = $null
            $newFinalInstalled = $false
            $commitCompleted = $false
            $caught = $false
            function Assert-FileVersion { param($Path, $ExpectedProductVersion, $ExpectedFileVersion) }
            function Assert-ReleaseSourcesUnchanged { }
            function Assert-PublishNotCancelled {
                if ($failureMode -eq "Cancel") { throw "Injected cancellation" }
            }
            function Write-JsonResult {
                param($Value)
                if ($failureMode -in @("Commit", "Rollback")) { throw "Injected result write failure" }
            }
            function Remove-Item {
                param($LiteralPath, [switch]$Recurse, [switch]$Force)
                if ($failureMode -eq "BackupCleanup" -and $LiteralPath -eq $backupDirectory) {
                    throw "Injected backup cleanup failure"
                }
                Microsoft.PowerShell.Management\Remove-Item -LiteralPath $LiteralPath -Recurse:$Recurse -Force:$Force
            }
            function Move-Item {
                param($LiteralPath, $Destination, [switch]$Force)
                if ($failureMode -eq "Rollback" -and $LiteralPath.StartsWith($backupDirectory + [IO.Path]::DirectorySeparatorChar)) {
                    throw "Injected backup restore failure"
                }
                Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
            }
            try { . $transactionScript } catch { $caught = $true }
            if ($failureMode -eq "Rollback") {
                $savedBackup = Join-Path $backupDirectory "release.exe"
                if (-not $caught -or -not (Test-Path -LiteralPath $savedBackup) `
                        -or [IO.File]::ReadAllText($savedBackup) -ne "last successful release") {
                    throw "Rollback failure discarded the recoverable old release."
                }
            } else {
                $expected = if ($failureMode -in @("Commit", "Cancel")) { "last successful release" } else { "verified new release" }
                if (-not (Test-Path -LiteralPath $finalExe) -or [IO.File]::ReadAllText($finalExe) -ne $expected) {
                    throw "Unexpected release contents after $failureMode."
                }
                if ($caught -ne ($failureMode -in @("Commit", "Cancel"))) { throw "Unexpected transaction status after $failureMode." }
                if (@(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 1) { throw "Output contains transaction debris." }
            }
            if ($failureMode -eq "BackupCleanup" -and -not (Test-Path -LiteralPath (Join-Path $backupDirectory "release.exe"))) {
                throw "Cleanup failure did not preserve the backup."
            }
            Write-Output "release_output_transaction_$failureMode=ok"
        } $failureMode
    }
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Microsoft.PowerShell.Management\Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
