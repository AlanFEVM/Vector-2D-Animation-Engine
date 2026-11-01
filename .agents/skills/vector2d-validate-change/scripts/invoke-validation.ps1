[CmdletBinding()]
param(
    [ValidateSet("Build", "Launcher", "Release", "Timeline", "Pressure", "Freehand", "Stress", "Render", "GpuOptics", "SelectionPerf", "All")]
    [string[]]$Suite = @("Build"),

    [switch]$Restore,
    [switch]$NoBuild,
    [switch]$EnforcePerformanceBudget,
    [switch]$ContinueOnFailure,
    [switch]$Plan,

    [ValidateRange(0, 3600)]
    [int]$LockTimeoutSeconds = 0
)

$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
$nativeProject = "native\VectorAnimationEngine.Native.csproj"
$launcherProject = "launcher\VectorAnimationEngine.Launcher.csproj"
$releaseManagerProject = "release-manager\VectorAnimationEngine.ReleaseManager.csproj"
$singleExePublishScript = "scripts\publish-single-exe.ps1"
$nativeDll = "native\bin\Release\net8.0-windows\VectorAnimationEngine.dll"
$benchmarkArguments = [ordered]@{
    Timeline = "--bench-timeline"
    Pressure = "--bench-pressure"
    Freehand = "--bench-freehand"
    Stress = "--bench"
    Render = "--bench-render"
    GpuOptics = "--bench-gpu-optics"
    SelectionPerf = "--bench-selection-perf"
}

function Invoke-DotnetChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    Write-Host ("dotnet " + ($Arguments -join " "))
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE"
    }
}

function Get-ValidationMutexName {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $normalizedRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([char[]]"\/").ToUpperInvariant()
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($normalizedRoot))
    } finally {
        $sha256.Dispose()
    }

    $hash = ([BitConverter]::ToString($hashBytes)).Replace("-", "").Substring(0, 24)
    return "Local\Vector2D.Validation.$hash"
}

$selected = if ($Suite -contains "All") {
    @("Build", "Launcher", "Release", "Timeline", "Pressure", "Freehand", "Stress", "Render", "GpuOptics")
} else {
    @($Suite | Select-Object -Unique)
}

$benchmarkSuites = @($selected | Where-Object { $benchmarkArguments.Contains($_) })
$shouldBuildNative = ($selected -contains "Build") -or ($benchmarkSuites.Count -gt 0 -and -not $NoBuild)
$shouldBuildLauncher = $selected -contains "Launcher"
$shouldValidateRelease = $selected -contains "Release"
$builds = @(
    if ($shouldBuildNative) { "Native" }
    if ($shouldBuildLauncher) { "Launcher" }
    if ($shouldValidateRelease) { "ReleaseManager"; "SingleExeRelease" }
)
$results = [ordered]@{}
$stepNames = @(
    if ($shouldBuildNative) { "NativeBuild" }
    if ($shouldBuildLauncher) { "Launcher" }
    if ($shouldValidateRelease) { "Release" }
    $benchmarkSuites
)
foreach ($name in $stepNames) {
    $results[$name] = [pscustomobject]@{
        Name = $name
        Status = if ($Plan) { "Planned" } else { "NotRun" }
        DurationMilliseconds = 0
        ExitCode = $null
        FailedBudgets = @()
        Failure = $null
    }
}
$runError = $null

function Invoke-ValidationStep {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [switch]$Required
    )

    $result = $results[$Name]
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action | ForEach-Object { Write-Host $_ }
        $result.Status = if ($result.FailedBudgets.Count -gt 0) { "PassedWithWarnings" } else { "Passed" }
    } catch {
        $result.Status = "Failed"
        $result.Failure = $_.Exception.Message
        if ($Required -or -not $ContinueOnFailure) { throw }
        Write-Warning "$Name failed: $($result.Failure)"
    } finally {
        $watch.Stop()
        $result.DurationMilliseconds = [Math]::Round($watch.Elapsed.TotalMilliseconds, 2)
    }
}

function New-ValidationSummary {
    param([Parameter(Mandatory = $true)][string]$Mode)

    [pscustomobject]@{
        Mode = $Mode
        Suites = @($selected)
        Builds = @($builds)
        Benchmarks = @($benchmarkSuites)
        NativeBuild = $shouldBuildNative
        LauncherBuild = $shouldBuildLauncher
        ReleaseBuild = $shouldValidateRelease
        Restore = [bool]$Restore
        PerformanceBudgetEnforced = [bool]$EnforcePerformanceBudget
        ContinueOnFailure = [bool]$ContinueOnFailure
        BenchmarkArguments = $benchmarkArguments
        Status = if ($Mode -eq "Plan") { "Planned" }
            elseif ($runError -or @($results.Values | Where-Object Status -eq "Failed").Count -gt 0) { "Failed" }
            elseif (@($results.Values | Where-Object Status -eq "PassedWithWarnings").Count -gt 0) { "PassedWithWarnings" }
            else { "Passed" }
        Results = @($results.Values)
        Error = if ($runError) { $runError.Exception.Message } else { $null }
    }
}

if ($Plan) {
    New-ValidationSummary -Mode "Plan" | ConvertTo-Json -Depth 6 -Compress
    return
}

$mutexName = Get-ValidationMutexName -RepositoryRoot $repoRoot
$validationMutex = [Threading.Mutex]::new($false, $mutexName)
$ownsMutex = $false

try {
    try {
        $ownsMutex = $validationMutex.WaitOne([TimeSpan]::FromSeconds($LockTimeoutSeconds))
    } catch [Threading.AbandonedMutexException] {
        $ownsMutex = $true
        Write-Warning "Recovered abandoned validation mutex for repository: $repoRoot"
    }

    if (-not $ownsMutex) {
        throw "Validation is already running for '$repoRoot'. Retry after it completes or increase -LockTimeoutSeconds (current: $LockTimeoutSeconds)."
    }

    $locationPushed = $false
    try {
        Push-Location $repoRoot
        $locationPushed = $true

        if ($shouldBuildNative) {
            Invoke-ValidationStep -Name "NativeBuild" -Required -Action {
                if ($Restore) { Invoke-DotnetChecked @("restore", $nativeProject) }
                Invoke-DotnetChecked @("build", $nativeProject, "-c", "Release", "--no-restore")
            }
        }

        if ($shouldBuildLauncher) {
            Invoke-ValidationStep -Name "Launcher" -Action {
                if ($Restore) { Invoke-DotnetChecked @("restore", $launcherProject) }
                Invoke-DotnetChecked @("build", $launcherProject, "-c", "Release", "--no-restore")
                Invoke-DotnetChecked @(
                    "run", "--project", $launcherProject,
                    "-c", "Release", "--no-build", "--",
                    "--validate-development-launcher")
            }
        }

        if ($shouldValidateRelease) {
            Invoke-ValidationStep -Name "Release" -Action {
                if ($Restore) { Invoke-DotnetChecked @("restore", $releaseManagerProject) }
                Invoke-DotnetChecked @("build", $releaseManagerProject, "-c", "Release", "--no-restore")
                $validationReleaseDirectory = Join-Path $repoRoot (
                    "artifacts\validation\single-exe-" + [Guid]::NewGuid().ToString("N"))
                try {
                    & $singleExePublishScript -OutputDirectory $validationReleaseDirectory
                    $releaseFiles = @(Get-ChildItem -LiteralPath $validationReleaseDirectory -File)
                    if ($releaseFiles.Count -ne 1 -or $releaseFiles[0].Extension -ne ".exe") {
                        throw "Single-EXE release validation did not produce exactly one EXE."
                    }
                    $bootstrapValidation = Start-Process -FilePath $releaseFiles[0].FullName -ArgumentList "--validate-single-exe" -WindowStyle Hidden -Wait -PassThru
                    if ($bootstrapValidation.ExitCode -ne 0) {
                        throw "Single-EXE bootstrap validation failed with exit code $($bootstrapValidation.ExitCode)."
                    }
                } finally {
                    if (Test-Path -LiteralPath $validationReleaseDirectory) {
                        $validationRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\validation")) + [IO.Path]::DirectorySeparatorChar
                        if (-not [IO.Path]::GetFullPath($validationReleaseDirectory).StartsWith($validationRoot, [StringComparison]::OrdinalIgnoreCase)) {
                            throw "Refusing to remove validation output outside '$validationRoot'."
                        }
                        Remove-Item -LiteralPath $validationReleaseDirectory -Recurse -Force
                    }
                }
            }
        }

        foreach ($name in $benchmarkSuites) {
            Invoke-ValidationStep -Name $name -Action {
                if (-not (Test-Path -LiteralPath $nativeDll -PathType Leaf)) {
                    throw "Native Release DLL not found: $nativeDll"
                }
                if ($name -eq "GpuOptics" -and $env:VECTOR_BENCH_SCENE_PERFORMANCE -eq "1") {
                    throw "GpuOptics requires VECTOR_BENCH_SCENE_PERFORMANCE to be unset; that variable replaces optics checks with the manual scene performance probe."
                }
                $argument = $benchmarkArguments[$name]
                Write-Host "dotnet $nativeDll $argument"
                $output = [Collections.Generic.List[string]]::new()
                & dotnet $nativeDll $argument 2>&1 | ForEach-Object {
                    $line = $_.ToString()
                    $output.Add($line)
                    Write-Host $line
                }
                $exitCode = $LASTEXITCODE
                $results[$name].ExitCode = $exitCode
                $failedBudgets = @($output | Where-Object { $_.ToString() -match "_budget_met=false\s*$" } | ForEach-Object { $_.ToString() })
                $results[$name].FailedBudgets = $failedBudgets
                if ($exitCode -ne 0) {
                    throw "$name validation failed with exit code $exitCode"
                }

                if ($failedBudgets.Count -gt 0) {
                    if ($EnforcePerformanceBudget) {
                        throw "$name validation exceeded a performance budget: $($failedBudgets -join ', ')"
                    }
                    Write-Warning "$name exceeded a performance budget (not enforced): $($failedBudgets -join ', ')"
                }
            }
        }
    } finally {
        if ($locationPushed) { Pop-Location }
    }
} catch {
    $runError = $_
} finally {
    try {
        if ($ownsMutex) { $validationMutex.ReleaseMutex() }
    } finally {
        $validationMutex.Dispose()
    }
    New-ValidationSummary -Mode "Run" | ConvertTo-Json -Depth 6 -Compress
}

if ($runError) { throw $runError }
if (@($results.Values | Where-Object Status -eq "Failed").Count -gt 0) {
    throw "Validation failed; see Results for failed and unexecuted steps."
}
