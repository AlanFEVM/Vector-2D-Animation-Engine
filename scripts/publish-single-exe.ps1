[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [string]$ResultPath,
    [string]$CancellationPath,
    [string]$ExpectedManifestSha256,
    [string]$ExpectedPropsSha256
)

$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$nativeProject = Join-Path $repoRoot "native\VectorAnimationEngine.Native.csproj"
$bootstrapProject = Join-Path $repoRoot "distribution-launcher\VectorAnimationEngine.DistributionLauncher.csproj"
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$releaseNotesPath = Join-Path $repoRoot "release\release-notes.json"
$rid = "win-x64"
$maximumExeBytes = 5L * 1024 * 1024
$publishMutex = [Threading.Mutex]::new($false, "Local\Vector2DAnimationEngineReleasePublish")
$publishLockAcquired = $false
$resolvedResultPath = $null
$resolvedCancellationPath = $null

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

function Write-JsonResult {
    param([Parameter(Mandatory = $true)]$Value)

    $json = $Value | ConvertTo-Json -Compress
    if (-not [string]::IsNullOrWhiteSpace($resolvedResultPath)) {
        $resultDirectory = Split-Path -Parent $resolvedResultPath
        if (-not [string]::IsNullOrWhiteSpace($resultDirectory)) {
            New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
        }
        $temporaryResult = "$resolvedResultPath.$([Guid]::NewGuid().ToString('N')).tmp"
        Write-Utf8NoBom -Path $temporaryResult -Content ($json + [Environment]::NewLine)
        Move-Item -LiteralPath $temporaryResult -Destination $resolvedResultPath -Force
    }
    Write-Output "V2D_RELEASE_RESULT=$json"
}

function Assert-PublishNotCancelled {
    if (-not [string]::IsNullOrWhiteSpace($resolvedCancellationPath) `
            -and (Test-Path -LiteralPath $resolvedCancellationPath -PathType Leaf)) {
        Write-Utf8NoBom -Path $resolvedCancellationPath -Content "acknowledged"
        Write-Output "V2D_RELEASE_CANCELLED=1"
        throw "Release publishing was cancelled before formal output commit."
    }
}

function Get-NormalizedDirectoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootPath = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) { return $rootPath }
    return $fullPath.TrimEnd([char[]]"\/")
}

function Assert-ReleaseSourcesUnchanged {
    if ([string]::IsNullOrWhiteSpace($ExpectedManifestSha256) `
            -and [string]::IsNullOrWhiteSpace($ExpectedPropsSha256)) {
        return
    }
    if ($ExpectedManifestSha256 -notmatch '^[0-9A-Fa-f]{64}$' `
            -or $ExpectedPropsSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "Expected release source fingerprints must both be 64-character SHA-256 values."
    }

    $actualManifestSha256 = (Get-FileHash -LiteralPath $releaseNotesPath -Algorithm SHA256).Hash
    $actualPropsSha256 = (Get-FileHash -LiteralPath $propsPath -Algorithm SHA256).Hash
    if ($actualManifestSha256 -ne $ExpectedManifestSha256 `
            -or $actualPropsSha256 -ne $ExpectedPropsSha256) {
        throw "Release source files changed after Release Manager loaded them. Reload before publishing."
    }
}

function Assert-FileVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedProductVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedFileVersion
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Versioned release file was not generated: $Path"
    }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $productVersion = ([string]$info.ProductVersion).Split('+', 2)[0]
    if ($productVersion -ne $ExpectedProductVersion -or [string]$info.FileVersion -ne $ExpectedFileVersion) {
        throw "Release metadata mismatch for $Path. ProductVersion=$productVersion FileVersion=$($info.FileVersion); expected $ExpectedProductVersion / $ExpectedFileVersion."
    }
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

[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$sourceVersion = [string]$props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $sourceVersion
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid release version: $Version"
}
if ($Version -ne $sourceVersion) {
    throw "Release version $Version does not match Directory.Build.props version $sourceVersion. Save or select the source version before publishing."
}
$assemblyVersion = "$Version.0"
$versionPropertiesMatch = ([string]$props.Project.PropertyGroup.AssemblyVersion) -eq $assemblyVersion `
    -and ([string]$props.Project.PropertyGroup.FileVersion) -eq $assemblyVersion `
    -and ([string]$props.Project.PropertyGroup.InformationalVersion) -eq $Version
if (-not $versionPropertiesMatch) {
    throw "Directory.Build.props version properties are not synchronized for $Version."
}
if (-not (Test-Path -LiteralPath $releaseNotesPath -PathType Leaf)) {
    throw "Release notes manifest is missing: $releaseNotesPath"
}
$releaseNotes = Get-Content -LiteralPath $releaseNotesPath -Raw | ConvertFrom-Json
$matchingNotes = @($releaseNotes.releases | Where-Object { [string]$_.version -eq $Version })
if ($matchingNotes.Count -ne 1) {
    throw "Release notes manifest must contain exactly one entry for version $Version."
}
$placeholderTexts = @(
    "New section",
    "新分区",
    "Describe the release highlight.",
    "描述本版本的主要变化。",
    "Describe this change.",
    "描述本项改动。")
$selectedNote = $matchingNotes[0]
$selectedNoteText = @(
    $selectedNote.locales.en.sections | ForEach-Object { @([string]$_.heading) + @($_.items | ForEach-Object { [string]$_ }) }
    $selectedNote.locales.'zh-CN'.sections | ForEach-Object { @([string]$_.heading) + @($_.items | ForEach-Object { [string]$_ }) })
if (@($selectedNoteText | Where-Object { $placeholderTexts -contains $_ }).Count -gt 0) {
    throw "Release notes for version $Version still contain placeholder content."
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Get-NormalizedDirectoryPath (Join-Path $repoRoot "artifacts\release")
} else {
    $OutputDirectory = Get-NormalizedDirectoryPath $OutputDirectory
}
$defaultOutputDirectory = Get-NormalizedDirectoryPath (Join-Path $repoRoot "artifacts\release")
$normalizedRepoRoot = Get-NormalizedDirectoryPath $repoRoot
if ($OutputDirectory.Equals(
        $normalizedRepoRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The repository root is reserved for the development hot-reload launcher."
}

$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ("v2d-single-exe-" + [Guid]::NewGuid().ToString("N"))
$applicationDirectory = Join-Path $stagingRoot "application"
$bootstrapDirectory = Join-Path $stagingRoot "bootstrap"
$payloadArchive = Join-Path $stagingRoot "application.zip"
$exeName = "VectorAnimationEngine-$Version-$rid.exe"
$finalExe = Join-Path $OutputDirectory $exeName
if ($OutputDirectory.EndsWith([string][IO.Path]::DirectorySeparatorChar)) {
    $outputRoot = $OutputDirectory
} else {
    $outputRoot = $OutputDirectory + [IO.Path]::DirectorySeparatorChar
}

if (-not [string]::IsNullOrWhiteSpace($ResultPath)) {
    $resolvedResultPath = [IO.Path]::GetFullPath($ResultPath)
    if ($resolvedResultPath.Equals($OutputDirectory, [StringComparison]::OrdinalIgnoreCase) `
            -or $resolvedResultPath.StartsWith($outputRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "ResultPath must be outside the formal release output directory."
    }
    $resultDirectory = Split-Path -Parent $resolvedResultPath
    if (-not [string]::IsNullOrWhiteSpace($resultDirectory)) {
        New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
        $resultProbe = Join-Path $resultDirectory (".v2d-result-probe-" + [Guid]::NewGuid().ToString("N"))
        try { Write-Utf8NoBom -Path $resultProbe -Content "probe" }
        finally { if (Test-Path -LiteralPath $resultProbe) { Remove-Item -LiteralPath $resultProbe -Force } }
    }
}
if (-not [string]::IsNullOrWhiteSpace($CancellationPath)) {
    $resolvedCancellationPath = [IO.Path]::GetFullPath($CancellationPath)
    if ($resolvedCancellationPath.Equals($OutputDirectory, [StringComparison]::OrdinalIgnoreCase) `
            -or $resolvedCancellationPath.StartsWith($outputRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "CancellationPath must be outside the formal release output directory."
    }
    if (-not [string]::IsNullOrWhiteSpace($resolvedResultPath) `
            -and $resolvedCancellationPath.Equals($resolvedResultPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "CancellationPath and ResultPath must be different files."
    }
}

$locationPushed = $false
try {
    try {
        $publishLockAcquired = $publishMutex.WaitOne(0)
    } catch [Threading.AbandonedMutexException] {
        $publishLockAcquired = $true
    }
    if (-not $publishLockAcquired) {
        throw "Another Vector 2D Animation Engine release publish is already running."
    }
    Assert-ReleaseSourcesUnchanged
    Assert-PublishNotCancelled

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
        "-p:Version=$Version",
        "-p:AssemblyVersion=$assemblyVersion",
        "-p:FileVersion=$assemblyVersion",
        "-p:InformationalVersion=$Version",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $applicationDirectory)
    Assert-PublishNotCancelled

    Invoke-DotnetChecked @(
        (Join-Path $applicationDirectory "VectorAnimationEngine.dll"),
        "--validate-release-notes")
    Assert-PublishNotCancelled

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
    Assert-PublishNotCancelled

    Invoke-DotnetChecked @(
        "publish", $bootstrapProject,
        "-c", "Release",
        "-p:EmbeddedPayloadPath=$payloadArchive",
        "-p:Version=$Version",
        "-p:AssemblyVersion=$assemblyVersion",
        "-p:FileVersion=$assemblyVersion",
        "-p:InformationalVersion=$Version",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $bootstrapDirectory)
    Assert-PublishNotCancelled

    $builtExe = Join-Path $bootstrapDirectory "VectorAnimationEngine.exe"
    if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) {
        throw "The single-file bootstrap was not generated: $builtExe"
    }
    Assert-FileVersion `
        -Path (Join-Path $applicationDirectory "VectorAnimationEngine.dll") `
        -ExpectedProductVersion $Version `
        -ExpectedFileVersion $assemblyVersion
    Assert-FileVersion `
        -Path $builtExe `
        -ExpectedProductVersion $Version `
        -ExpectedFileVersion $assemblyVersion

    $validation = Start-Process -FilePath $builtExe -ArgumentList "--validate-single-exe" -Wait -PassThru
    if ($validation.ExitCode -ne 0) {
        throw "Embedded payload validation failed with exit code $($validation.ExitCode)."
    }
    Assert-PublishNotCancelled

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
    }

    $transactionId = [Guid]::NewGuid().ToString("N")
    $pendingExe = Join-Path $OutputDirectory ("." + $exeName + ".$transactionId.tmp")
    $backupDirectory = Join-Path $OutputDirectory ".v2d-release-backup-$transactionId"
    $newFinalInstalled = $false
    try {
        Copy-Item -LiteralPath $builtExe -Destination $pendingExe
        Assert-FileVersion `
            -Path $pendingExe `
            -ExpectedProductVersion $Version `
            -ExpectedFileVersion $assemblyVersion
        $pendingSize = (Get-Item -LiteralPath $pendingExe).Length
        if ($pendingSize -ne $size) {
            throw "Pending release EXE size changed during output staging."
        }
        $hash = (Get-FileHash -LiteralPath $pendingExe -Algorithm SHA256).Hash
        Assert-ReleaseSourcesUnchanged
        Assert-PublishNotCancelled

        # Cancellation is deliberately ignored after this point so commit and rollback stay coherent.
        New-Item -ItemType Directory -Path $backupDirectory | Out-Null
        foreach ($existingOutput in $existingOutputs) {
            Move-Item -LiteralPath $existingOutput.FullName -Destination $backupDirectory
        }
        Move-Item -LiteralPath $pendingExe -Destination $finalExe
        $newFinalInstalled = $true

        $outputs = @(Get-ChildItem -LiteralPath $OutputDirectory -Force | Where-Object {
            -not $_.FullName.Equals($backupDirectory, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($outputs.Count -ne 1 -or $outputs[0].PSIsContainer -or $outputs[0].Extension -ne ".exe") {
            throw "Release output must contain exactly one EXE."
        }
        if ((Get-Item -LiteralPath $finalExe).Length -ne $pendingSize) {
            throw "Committed release EXE size does not match the verified pending artifact."
        }

        Write-JsonResult ([ordered]@{
            schemaVersion = 1
            version = $Version
            path = $finalExe
            sizeBytes = $pendingSize
            maximumBytes = $maximumExeBytes
            sha256 = $hash
        })
        Remove-Item -LiteralPath $backupDirectory -Recurse -Force
        $backupDirectory = $null
    } catch {
        if (-not [string]::IsNullOrWhiteSpace($resolvedResultPath) `
                -and (Test-Path -LiteralPath $resolvedResultPath -PathType Leaf)) {
            Remove-Item -LiteralPath $resolvedResultPath -Force
        }
        if ($newFinalInstalled -and (Test-Path -LiteralPath $finalExe -PathType Leaf)) {
            Remove-Item -LiteralPath $finalExe -Force
        }
        if (-not [string]::IsNullOrWhiteSpace($backupDirectory) `
                -and (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
            foreach ($backupEntry in @(Get-ChildItem -LiteralPath $backupDirectory -Force)) {
                Move-Item -LiteralPath $backupEntry.FullName -Destination $OutputDirectory -Force
            }
        }
        throw
    } finally {
        if (Test-Path -LiteralPath $pendingExe) {
            Remove-Item -LiteralPath $pendingExe -Force
        }
        if (-not [string]::IsNullOrWhiteSpace($backupDirectory) `
                -and (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
            Remove-Item -LiteralPath $backupDirectory -Recurse -Force
        }
    }
}
finally {
    if ($locationPushed) { Pop-Location }
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
    if ($publishLockAcquired) {
        try { $publishMutex.ReleaseMutex() } catch { }
    }
    $publishMutex.Dispose()
}
