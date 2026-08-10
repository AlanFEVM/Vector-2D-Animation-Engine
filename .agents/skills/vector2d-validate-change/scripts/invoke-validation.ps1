[CmdletBinding()]
param(
    [ValidateSet("Build", "Launcher", "Release", "Timeline", "Pressure", "Freehand", "Stress", "Render", "All")]
    [string[]]$Suite = @("Build"),

    [switch]$Restore,
    [switch]$NoBuild,
    [switch]$EnforcePerformanceBudget,
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
    @("Build", "Launcher", "Release", "Timeline", "Pressure", "Freehand", "Stress", "Render")
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
    }
}

if ($Plan) {
    New-ValidationSummary -Mode "Plan" | ConvertTo-Json -Compress
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

        if ($Restore) {
            if ($shouldBuildNative) { Invoke-DotnetChecked @("restore", $nativeProject) }
            if ($shouldBuildLauncher) { Invoke-DotnetChecked @("restore", $launcherProject) }
            if ($shouldValidateRelease) { Invoke-DotnetChecked @("restore", $releaseManagerProject) }
        }

        if ($shouldBuildNative) {
            Invoke-DotnetChecked @("build", $nativeProject, "-c", "Release", "--no-restore")
        }

        if ($shouldBuildLauncher) {
            Invoke-DotnetChecked @("build", $launcherProject, "-c", "Release", "--no-restore")
            Invoke-DotnetChecked @(
                "run", "--project", $launcherProject,
                "-c", "Release", "--no-build", "--",
                "--validate-development-launcher")
        }

        if ($shouldValidateRelease) {
            Invoke-DotnetChecked @("build", $releaseManagerProject, "-c", "Release", "--no-restore")
            $validationReleaseDirectory = Join-Path $repoRoot (
                "artifacts\validation\single-exe-" + [Guid]::NewGuid().ToString("N"))
            try {
                & $singleExePublishScript -OutputDirectory $validationReleaseDirectory
                $releaseFiles = @(Get-ChildItem -LiteralPath $validationReleaseDirectory -File)
                if ($releaseFiles.Count -ne 1 -or $releaseFiles[0].Extension -ne ".exe") {
                    throw "Single-EXE release validation did not produce exactly one EXE."
                }
                $bootstrapValidation = Start-Process -FilePath $releaseFiles[0].FullName -ArgumentList "--validate-single-exe" -Wait -PassThru
                if ($bootstrapValidation.ExitCode -ne 0) {
                    throw "Single-EXE bootstrap validation failed with exit code $($bootstrapValidation.ExitCode)."
                }
            } finally {
                if (Test-Path -LiteralPath $validationReleaseDirectory) {
                    Remove-Item -LiteralPath $validationReleaseDirectory -Recurse -Force
                }
            }
        }

        if ($benchmarkSuites.Count -gt 0 -and -not (Test-Path -LiteralPath $nativeDll -PathType Leaf)) {
            throw "Native Release DLL not found: $nativeDll"
        }

        foreach ($name in $benchmarkSuites) {
            $argument = $benchmarkArguments[$name]
            Write-Host "dotnet $nativeDll $argument"
            $output = @(& dotnet $nativeDll $argument 2>&1)
            $exitCode = $LASTEXITCODE
            $output | ForEach-Object { Write-Host $_ }

            if ($exitCode -ne 0) {
                throw "$name validation failed with exit code $exitCode"
            }

            if ($EnforcePerformanceBudget) {
                $failedBudgets = @($output | Where-Object { $_.ToString() -match "_budget_met=false$" })
                if ($failedBudgets.Count -gt 0) {
                    throw "$name validation exceeded a performance budget: $($failedBudgets -join ', ')"
                }
            }
        }
    } finally {
        if ($locationPushed) { Pop-Location }
    }
} finally {
    try {
        if ($ownsMutex) { $validationMutex.ReleaseMutex() }
    } finally {
        $validationMutex.Dispose()
    }
}

New-ValidationSummary -Mode "Run" | ConvertTo-Json -Compress
