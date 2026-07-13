[CmdletBinding()]
param(
    [ValidateSet("Build", "Launcher", "Timeline", "Pressure", "Freehand", "Stress", "Render", "All")]
    [string[]]$Suite = @("Build"),

    [switch]$Restore,
    [switch]$NoBuild,
    [switch]$EnforcePerformanceBudget
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

$selected = if ($Suite -contains "All") {
    @("Build", "Timeline", "Pressure", "Freehand", "Stress", "Render")
} else {
    @($Suite | Select-Object -Unique)
}

$benchmarkSuites = @($selected | Where-Object { $benchmarkArguments.Contains($_) })
$shouldBuildNative = ($selected -contains "Build") -or ($benchmarkSuites.Count -gt 0 -and -not $NoBuild)

Push-Location $repoRoot
try {
    if ($Restore) {
        if ($shouldBuildNative) { Invoke-DotnetChecked @("restore", $nativeProject) }
        if ($selected -contains "Launcher") { Invoke-DotnetChecked @("restore", $launcherProject) }
    }

    if ($shouldBuildNative) {
        Invoke-DotnetChecked @("build", $nativeProject, "-c", "Release", "--no-restore")
    }

    if ($selected -contains "Launcher") {
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
    Pop-Location
}

[pscustomobject]@{
    Suites = $selected
    NativeBuild = $shouldBuildNative
    PerformanceBudgetEnforced = [bool]$EnforcePerformanceBudget
} | ConvertTo-Json -Compress
