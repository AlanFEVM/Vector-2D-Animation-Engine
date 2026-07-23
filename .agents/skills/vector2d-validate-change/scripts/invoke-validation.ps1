[CmdletBinding()]
param(
    [ValidateSet("Build", "Launcher", "Timeline", "Pressure", "Freehand", "Stress", "Render", "All")]
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
    @("Build", "Launcher", "Timeline", "Pressure", "Freehand", "Stress", "Render")
} else {
    @($Suite | Select-Object -Unique)
}

$benchmarkSuites = @($selected | Where-Object { $benchmarkArguments.Contains($_) })
$shouldBuildNative = ($selected -contains "Build") -or ($benchmarkSuites.Count -gt 0 -and -not $NoBuild)
$shouldBuildLauncher = $selected -contains "Launcher"
$builds = @(
    if ($shouldBuildNative) { "Native" }
    if ($shouldBuildLauncher) { "Launcher" }
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
        }

        if ($shouldBuildNative) {
            Invoke-DotnetChecked @("build", $nativeProject, "-c", "Release", "--no-restore")
        }

        if ($shouldBuildLauncher) {
            Invoke-DotnetChecked @("build", $launcherProject, "-c", "Release", "--no-restore")
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
