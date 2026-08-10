[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $repoRoot "release-manager\VectorAnimationEngine.ReleaseManager.csproj"

Push-Location $repoRoot
try {
    & dotnet run --project $project -- $repoRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Release Manager exited with code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
