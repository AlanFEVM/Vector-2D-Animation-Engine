[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ("vector2d-validation-" + [Guid]::NewGuid().ToString("N"))))
$runnerDirectory = Join-Path $temporaryRoot ".agents\skills\vector2d-validate-change\scripts"
$runner = Join-Path $runnerDirectory "invoke-validation.ps1"
$fixtureDll = Join-Path $temporaryRoot "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"
$originalScenePerformance = $env:VECTOR_BENCH_SCENE_PERFORMANCE
$global:Vector2DValidationRunnerMock = [pscustomobject]@{
    Calls = [Collections.Generic.List[string]]::new()
    FailCommand = ""
    BudgetFailure = $false
    LastHostLine = ""
    StreamedDuringInvocation = $false
}
$checks = 0

function Assert-Runner {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function dotnet {
    $global:Vector2DValidationRunnerMock.Calls.Add(($args -join " "))
    $global:LASTEXITCODE = 0
    if ($global:Vector2DValidationRunnerMock.FailCommand -and ($args -contains $global:Vector2DValidationRunnerMock.FailCommand)) {
        $global:LASTEXITCODE = 7
        "mock_process_failure"
    } elseif ($global:Vector2DValidationRunnerMock.BudgetFailure) {
        "mock_budget_met=false  "
    } else {
        "mock_process_passed"
        $global:Vector2DValidationRunnerMock.StreamedDuringInvocation =
            $global:Vector2DValidationRunnerMock.LastHostLine -eq "mock_process_passed"
    }
}

function Invoke-RunnerScenario {
    param([hashtable]$Parameters)
    $global:Vector2DValidationRunnerMock.Calls.Clear()
    $global:Vector2DValidationRunnerMock.LastHostLine = ""
    $global:Vector2DValidationRunnerMock.StreamedDuringInvocation = $false
    $captured = [Collections.Generic.List[string]]::new()
    $failure = $null
    try {
        & $runner @Parameters 6>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.InformationRecord]) {
                $global:Vector2DValidationRunnerMock.LastHostLine = $_.ToString()
            } else {
                $captured.Add([string]$_)
            }
        }
    } catch {
        $failure = $_
    }
    Assert-Runner ($captured.Count -eq 1) "Expected exactly one JSON summary, including on failure."
    [pscustomobject]@{
        Summary = $captured[0] | ConvertFrom-Json
        Failed = $null -ne $failure
    }
}

try {
    New-Item -ItemType Directory -Path $runnerDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $fixtureDll) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "invoke-validation.ps1") -Destination $runner
    [IO.File]::WriteAllText($fixtureDll, "Mock fixture; never execute as an assembly.")
    $env:VECTOR_BENCH_SCENE_PERFORMANCE = $null

    $scenario = Invoke-RunnerScenario @{ Suite = @("All"); Plan = $true }
    Assert-Runner (-not $scenario.Failed -and $scenario.Summary.Status -eq "Planned") "Plan must not claim a pass."
    Assert-Runner ($scenario.Summary.Suites -contains "GpuOptics") "All must include GPU optics."
    Assert-Runner ($scenario.Summary.BenchmarkArguments.SelectionPerf -eq "--bench-selection-perf") "Selection performance routing is missing."
    Assert-Runner (@($scenario.Summary.Results | Where-Object Status -ne "Planned").Count -eq 0) "Plan steps must remain planned."
    Assert-Runner ($global:Vector2DValidationRunnerMock.Calls.Count -eq 0) "Plan invoked dotnet."
    $checks++

    $global:Vector2DValidationRunnerMock.FailCommand = "--bench-timeline"
    $scenario = Invoke-RunnerScenario @{ Suite = @("Timeline", "Pressure"); NoBuild = $true }
    Assert-Runner ($scenario.Failed -and $scenario.Summary.Status -eq "Failed") "A process failure must fail validation."
    Assert-Runner ($scenario.Summary.Results[0].ExitCode -eq 7 -and $scenario.Summary.Results[1].Status -eq "NotRun") "Fail-fast must preserve the exit code and unexecuted step: $($scenario.Summary | ConvertTo-Json -Depth 6 -Compress)"
    Assert-Runner ($global:Vector2DValidationRunnerMock.Calls.Count -eq 1) "Fail-fast ran another suite."
    $checks++

    $scenario = Invoke-RunnerScenario @{ Suite = @("Timeline", "Pressure"); NoBuild = $true; ContinueOnFailure = $true }
    Assert-Runner ($scenario.Failed -and $scenario.Summary.Results[1].Status -eq "Passed") "ContinueOnFailure must run subsequent suites without hiding failure."
    Assert-Runner ($global:Vector2DValidationRunnerMock.Calls.Count -eq 2) "ContinueOnFailure did not run both suites."
    $checks++

    $global:Vector2DValidationRunnerMock.FailCommand = "build"
    $scenario = Invoke-RunnerScenario @{ Suite = @("Timeline", "Pressure"); ContinueOnFailure = $true }
    Assert-Runner ($scenario.Failed -and $scenario.Summary.Results[0].Name -eq "NativeBuild" -and $scenario.Summary.Results[0].Status -eq "Failed") "Native build failure was not reported."
    Assert-Runner (@($scenario.Summary.Results | Where-Object Status -eq "NotRun").Count -eq 2) "Build failure must not execute stale native binaries."
    Assert-Runner ($global:Vector2DValidationRunnerMock.Calls.Count -eq 1) "A native benchmark ran after a failed build."
    $checks++

    $global:Vector2DValidationRunnerMock.FailCommand = "run"
    $scenario = Invoke-RunnerScenario @{ Suite = @("Launcher", "Timeline"); NoBuild = $true; ContinueOnFailure = $true }
    Assert-Runner ($scenario.Failed -and $scenario.Summary.Results[0].Status -eq "Failed" -and $scenario.Summary.Results[1].Status -eq "Passed") "Independent suite failure must not block later suites in continue mode."
    $checks++

    $global:Vector2DValidationRunnerMock.FailCommand = ""
    $global:Vector2DValidationRunnerMock.BudgetFailure = $true
    $scenario = Invoke-RunnerScenario @{ Suite = @("Stress"); NoBuild = $true; EnforcePerformanceBudget = $true }
    Assert-Runner ($scenario.Failed -and $scenario.Summary.Results[0].ExitCode -eq 0) "Strict budgets must fail even when the process exits zero."
    Assert-Runner ($scenario.Summary.Results[0].FailedBudgets.Count -eq 1) "A budget marker with trailing whitespace was not recorded."
    $checks++

    $scenario = Invoke-RunnerScenario @{ Suite = @("Stress"); NoBuild = $true }
    Assert-Runner (-not $scenario.Failed -and $scenario.Summary.Status -eq "PassedWithWarnings") "Non-enforced budget failures must not look like a clean pass."
    $checks++

    $global:Vector2DValidationRunnerMock.BudgetFailure = $false
    $scenario = Invoke-RunnerScenario @{ Suite = @("GpuOptics", "SelectionPerf"); NoBuild = $true }
    Assert-Runner (-not $scenario.Failed -and $scenario.Summary.Status -eq "Passed") "Focused suite routing failed."
    Assert-Runner ($global:Vector2DValidationRunnerMock.Calls[0].EndsWith("--bench-gpu-optics") -and $global:Vector2DValidationRunnerMock.Calls[1].EndsWith("--bench-selection-perf")) "Focused suites invoked the wrong commands."
    $checks++
    Assert-Runner $global:Vector2DValidationRunnerMock.StreamedDuringInvocation "Benchmark output was buffered until the process returned."
    $checks++

    $env:VECTOR_BENCH_SCENE_PERFORMANCE = "1"
    $scenario = Invoke-RunnerScenario @{ Suite = @("GpuOptics"); NoBuild = $true }
    Assert-Runner ($scenario.Failed -and $global:Vector2DValidationRunnerMock.Calls.Count -eq 0) "GPU optics must not silently run the manual performance probe instead."
    $env:VECTOR_BENCH_SCENE_PERFORMANCE = $null
    $checks++

    Remove-Item -LiteralPath $fixtureDll
    $scenario = Invoke-RunnerScenario @{ Suite = @("Pressure"); NoBuild = $true }
    Assert-Runner ($scenario.Failed -and $scenario.Summary.Results[0].Status -eq "Failed" -and $global:Vector2DValidationRunnerMock.Calls.Count -eq 0) "Missing native binaries must be reported without invoking dotnet."
    $checks++

    [pscustomobject]@{ Checks = $checks; ProductBuildOrRegressionExecuted = $false; Status = "ok" } | ConvertTo-Json -Compress
} finally {
    Remove-Variable -Name Vector2DValidationRunnerMock -Scope Global
    $env:VECTOR_BENCH_SCENE_PERFORMANCE = $originalScenePerformance
    $temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]"\/") + [IO.Path]::DirectorySeparatorChar
    if (-not $temporaryRoot.StartsWith($temporaryParent, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($temporaryRoot) -notmatch '^vector2d-validation-[a-f0-9]{32}$') {
        throw "Refusing to remove unexpected mock workspace: $temporaryRoot"
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
