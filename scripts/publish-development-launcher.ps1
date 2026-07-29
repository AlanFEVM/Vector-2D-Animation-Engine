[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$launcherProject = Join-Path $repoRoot "launcher\VectorAnimationEngine.Launcher.csproj"
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ("v2d-development-launcher-" + [Guid]::NewGuid().ToString("N"))
$publishDirectory = Join-Path $stagingRoot "publish"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "VectorAnimationEngine.exe"
} else {
    $OutputPath = [IO.Path]::GetFullPath($OutputPath)
}

function Invoke-DotnetChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    Write-Host ("dotnet " + ($Arguments -join " "))
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE"
    }
}

try {
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    Invoke-DotnetChecked @(
        "publish", $launcherProject,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "false",
        "-p:PublishSingleFile=true",
        "-p:PublishReadyToRun=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $publishDirectory)

    $outputs = @(Get-ChildItem -LiteralPath $publishDirectory -File)
    if ($outputs.Count -ne 1 -or $outputs[0].Name -ne "VectorAnimationEngine.exe") {
        throw "Development launcher publish must produce exactly one VectorAnimationEngine.exe."
    }

    $builtExe = $outputs[0].FullName
    $validation = Start-Process `
        -FilePath $builtExe `
        -ArgumentList "--validate-development-launcher" `
        -WorkingDirectory $repoRoot `
        -Wait `
        -PassThru
    if ($validation.ExitCode -ne 0) {
        throw "Development launcher validation failed with exit code $($validation.ExitCode)."
    }

    $outputDirectory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    Copy-Item -LiteralPath $builtExe -Destination $OutputPath -Force

    $installedValidation = Start-Process `
        -FilePath $OutputPath `
        -ArgumentList "--validate-development-launcher" `
        -WorkingDirectory $repoRoot `
        -Wait `
        -PassThru
    if ($installedValidation.ExitCode -ne 0) {
        throw "Installed development launcher validation failed with exit code $($installedValidation.ExitCode)."
    }

    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($OutputPath)
    [pscustomobject]@{
        Purpose = "development-hot-reload-launcher"
        Path = $OutputPath
        SizeBytes = (Get-Item -LiteralPath $OutputPath).Length
        Product = $info.ProductName
        Version = $info.ProductVersion
        Sha256 = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
    } | ConvertTo-Json -Compress
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
