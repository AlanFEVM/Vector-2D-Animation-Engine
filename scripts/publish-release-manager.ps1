[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $repoRoot "release-manager\VectorAnimationEngine.ReleaseManager.csproj"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\release-manager-exe"
} else {
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
}

$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ("v2d-release-manager-" + [Guid]::NewGuid().ToString("N"))
$stagingExe = Join-Path $stagingRoot "VectorAnimationEngine.ReleaseManager.exe"
$stagingRuntimeConfig = Join-Path $stagingRoot "VectorAnimationEngine.runtimeconfig.json"
$targetExe = Join-Path $OutputDirectory "ReleaseManager.exe"
$targetRuntimeConfig = Join-Path $OutputDirectory "ReleaseManager.runtimeconfig.json"

try {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    & dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $stagingRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Release Manager publish failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $stagingExe -PathType Leaf) -or -not (Test-Path -LiteralPath $stagingRuntimeConfig -PathType Leaf)) {
        throw "Release Manager publish did not produce its executable and runtime configuration."
    }

    Copy-Item -LiteralPath $stagingExe -Destination $targetExe -Force
    Copy-Item -LiteralPath $stagingRuntimeConfig -Destination $targetRuntimeConfig -Force
    $hash = (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash
    [pscustomobject]@{
        Path = $targetExe
        RuntimeConfig = $targetRuntimeConfig
        SizeBytes = (Get-Item -LiteralPath $targetExe).Length
        Sha256 = $hash
    } | ConvertTo-Json -Compress
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
