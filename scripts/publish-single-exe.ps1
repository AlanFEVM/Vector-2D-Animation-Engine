[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$nativeProject = Join-Path $repoRoot "native\VectorAnimationEngine.Native.csproj"
$bootstrapProject = Join-Path $repoRoot "distribution-launcher\VectorAnimationEngine.DistributionLauncher.csproj"
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$rid = "win-x64"
$maximumExeBytes = 5L * 1024 * 1024

function Invoke-DotnetChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    Write-Host ("dotnet " + ($Arguments -join " "))
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE"
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$ArchivePath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sourceRoot = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd([char[]]"\/")
    $stream = [IO.File]::Open($ArchivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $fixedTimestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            foreach ($file in @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Sort-Object FullName)) {
                $entryName = $file.FullName.Substring($sourceRoot.Length + 1).Replace([char]92, [char]47)
                $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTimestamp
                $entryStream = $entry.Open()
                try {
                    $sourceStream = $file.OpenRead()
                    try { $sourceStream.CopyTo($entryStream) }
                    finally { $sourceStream.Dispose() }
                } finally {
                    $entryStream.Dispose()
                }
            }
        } finally {
            $archive.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $Version = [string]$props.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Invalid release version: $Version"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\release"
} else {
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
}
$defaultOutputDirectory = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\release"))
if ($OutputDirectory.TrimEnd([char[]]"\/").Equals(
        $repoRoot.TrimEnd([char[]]"\/"),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The repository root is reserved for the development hot-reload launcher."
}

$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ("v2d-single-exe-" + [Guid]::NewGuid().ToString("N"))
$applicationDirectory = Join-Path $stagingRoot "application"
$bootstrapDirectory = Join-Path $stagingRoot "bootstrap"
$payloadArchive = Join-Path $stagingRoot "application.zip"
$exeName = "VectorAnimationEngine-$Version-$rid.exe"
$finalExe = Join-Path $OutputDirectory $exeName

$locationPushed = $false
try {
    New-Item -ItemType Directory -Path $applicationDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $bootstrapDirectory -Force | Out-Null
    Push-Location $repoRoot
    $locationPushed = $true

    Invoke-DotnetChecked @(
        "publish", $nativeProject,
        "-c", "Release",
        "-r", $rid,
        "--self-contained", "false",
        "-p:PublishReadyToRun=false",
        "-p:UseAppHost=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $applicationDirectory)

    $releaseMetadata = [ordered]@{
        layoutVersion = 3
        product = "Vector 2D Animation Engine"
        version = $Version
        rid = $rid
        targetFramework = "net8.0-windows"
        compatibleRuntime = "8.0.x"
        runtimeArchitecture = "x64"
        runtimeAcquisition = "bootstrap-download"
        readyToRun = $false
        packageType = "embedded-single-exe"
    } | ConvertTo-Json
    Write-Utf8NoBom -Path (Join-Path $applicationDirectory "release.json") -Content ($releaseMetadata + [Environment]::NewLine)

    New-DeterministicZip -SourceDirectory $applicationDirectory -ArchivePath $payloadArchive

    Invoke-DotnetChecked @(
        "publish", $bootstrapProject,
        "-c", "Release",
        "-p:EmbeddedPayloadPath=$payloadArchive",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $bootstrapDirectory)

    $builtExe = Join-Path $bootstrapDirectory "VectorAnimationEngine.exe"
    if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) {
        throw "The single-file bootstrap was not generated: $builtExe"
    }

    $validation = Start-Process -FilePath $builtExe -ArgumentList "--validate-single-exe" -Wait -PassThru
    if ($validation.ExitCode -ne 0) {
        throw "Embedded payload validation failed with exit code $($validation.ExitCode)."
    }

    $size = (Get-Item -LiteralPath $builtExe).Length
    if ($size -ge $maximumExeBytes) {
        throw "Release EXE is $size bytes; it must be smaller than $maximumExeBytes bytes."
    }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $existingOutputs = @(Get-ChildItem -LiteralPath $OutputDirectory -Force)
    if ($existingOutputs.Count -gt 0) {
        if (-not $OutputDirectory.Equals($defaultOutputDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Custom output directory must be empty: $OutputDirectory"
        }
        $existingOutputs | Remove-Item -Recurse -Force
    }
    Copy-Item -LiteralPath $builtExe -Destination $finalExe

    $outputs = @(Get-ChildItem -LiteralPath $OutputDirectory -File)
    if ($outputs.Count -ne 1 -or $outputs[0].Extension -ne ".exe") {
        throw "Release output must contain exactly one EXE."
    }

    $hash = (Get-FileHash -LiteralPath $finalExe -Algorithm SHA256).Hash
    [pscustomobject]@{
        Version = $Version
        Path = $finalExe
        SizeBytes = (Get-Item -LiteralPath $finalExe).Length
        MaximumBytes = $maximumExeBytes
        Sha256 = $hash
    } | ConvertTo-Json -Compress
}
finally {
    if ($locationPushed) { Pop-Location }
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
