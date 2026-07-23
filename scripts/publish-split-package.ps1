[CmdletBinding()]
param(
    [string]$Version,
    [string]$RuntimeVersion,
    [switch]$FullPackage,
    [switch]$PatchOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = Join-Path $repoRoot "artifacts\validation"
$nativeProject = Join-Path $repoRoot "native\VectorAnimationEngine.Native.csproj"
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$rid = "win-x64"

function Invoke-DotnetChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    Write-Host ("dotnet " + ($Arguments -join " "))
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE"
    }
}

function Assert-NewPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        throw "Refusing to overwrite existing package output: $Path"
    }
}

function Copy-DirectoryExact {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Required source directory is missing: $Source"
    }
    $destinationParent = Split-Path -Parent $Destination
    if (-not [string]::IsNullOrWhiteSpace($destinationParent)) {
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Assert-ArchiveRoots {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$ContainerName,
        [Parameter(Mandatory = $true)][string[]]$ExpectedRoots
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $prefix = "$ContainerName/"
        $roots = @($archive.Entries | ForEach-Object {
            $name = $_.FullName.Replace([char]92, [char]47)
            if ($name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                $name = $name.Substring($prefix.Length)
            }
            if (-not [string]::IsNullOrWhiteSpace($name)) {
                ($name -split '/')[0]
            }
        } | Sort-Object -Unique)
    }
    finally {
        $archive.Dispose()
    }

    $unexpected = @($roots | Where-Object { $_ -notin $ExpectedRoots })
    $missing = @($ExpectedRoots | Where-Object { $_ -notin $roots })
    if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
        throw "Archive root validation failed for $ArchivePath. Expected: $($ExpectedRoots -join ', '); actual: $($roots -join ', ')"
    }
}

function New-SplitAppHost {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetRoot,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $sdkDirectory = Get-ChildItem -LiteralPath (Join-Path $DotnetRoot "sdk") -Directory |
        Where-Object { $_.Name -match '^9\.0\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1
    if ($null -eq $sdkDirectory) {
        throw "A stable .NET 9 SDK is required to generate the app-relative split-package host."
    }

    $hostModelPath = Join-Path $sdkDirectory.FullName "Sdks\Microsoft.NET.Sdk\tools\net472\Microsoft.NET.HostModel.dll"
    if (-not (Test-Path -LiteralPath $hostModelPath -PathType Leaf)) {
        throw "The .NET 9 SDK HostModel is missing: $hostModelPath"
    }
    $hostPackDirectory = Get-ChildItem -LiteralPath (Join-Path $DotnetRoot "packs\Microsoft.NETCore.App.Host.win-x64") -Directory |
        Where-Object { $_.Name -match '^9\.0\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1
    if ($null -eq $hostPackDirectory) {
        throw "The .NET 9 win-x64 apphost pack is not installed."
    }
    [string]$appHostSource = Join-Path ([string]$hostPackDirectory.FullName) "runtimes\win-x64\native\apphost.exe"
    if (-not (Test-Path -LiteralPath $appHostSource -PathType Leaf)) {
        throw "The .NET 9 win-x64 apphost template is missing: $appHostSource"
    }

    Add-Type -Path $hostModelPath
    $options = New-Object "Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions"
    $options.Location = [Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions+SearchLocation]::AppRelative
    $options.AppRelativeDotNet = "..\.Runtime"
    $createMethod = [Microsoft.NET.HostModel.AppHost.HostWriter].GetMethods() |
        Where-Object { $_.Name -eq "CreateAppHost" -and $_.GetParameters().Count -eq 8 }
    [string]$destinationPath = $Destination
    [string]$appBinaryPath = ".V2DEngine\VectorAnimationEngine.dll"
    [object[]]$createArguments = @(
        $appHostSource,
        $destinationPath,
        $appBinaryPath,
        $true,
        $null,
        $false,
        $false,
        $options.PSObject.BaseObject)
    $createMethod.Invoke($null, $createArguments) | Out-Null
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $Version = [string]$props.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Invalid release version: $Version"
}
if ($FullPackage -and $PatchOnly) {
    throw "-FullPackage and the legacy -PatchOnly switch cannot be used together."
}

$buildFullPackage = [bool]$FullPackage

$dotnetPath = $null
$dotnetRoot = $null
$coreRuntimeSource = $null
$desktopRuntimeSource = $null
$hostFxrSource = $null
if ($buildFullPackage) {
    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    $dotnetRoot = Split-Path -Parent $dotnetPath
    $desktopRuntimeRoot = Join-Path $dotnetRoot "shared\Microsoft.WindowsDesktop.App"
    if ([string]::IsNullOrWhiteSpace($RuntimeVersion)) {
        $RuntimeVersion = Get-ChildItem -LiteralPath $desktopRuntimeRoot -Directory |
            Where-Object { $_.Name -match '^8\.0\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            Select-Object -First 1 -ExpandProperty Name
    }
    if ([string]::IsNullOrWhiteSpace($RuntimeVersion)) {
        throw "No installed .NET 8 Windows Desktop runtime was found under $desktopRuntimeRoot"
    }

    $coreRuntimeSource = Join-Path $dotnetRoot "shared\Microsoft.NETCore.App\$RuntimeVersion"
    $desktopRuntimeSource = Join-Path $dotnetRoot "shared\Microsoft.WindowsDesktop.App\$RuntimeVersion"
    $hostFxrSource = Join-Path $dotnetRoot "host\fxr\$RuntimeVersion"
    foreach ($required in @($dotnetPath, $coreRuntimeSource, $desktopRuntimeSource, $hostFxrSource)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "The selected private runtime is incomplete: $required"
        }
    }
}

$patchName = "VectorAnimationEngine-$Version-patch-$rid"
$patchRoot = Join-Path $artifactRoot $patchName
$patchAppDirectory = Join-Path $patchRoot ".V2DEngine"
$patchZip = Join-Path $artifactRoot "$patchName.zip"
$patchChecksumPath = Join-Path $artifactRoot "$patchName.sha256"
$packageName = "VectorAnimationEngine-$Version-split-$rid"
$packageRoot = Join-Path $artifactRoot $packageName
$packageZip = Join-Path $artifactRoot "$packageName.zip"
$checksumPath = Join-Path $artifactRoot "$packageName.sha256"

Assert-NewPath $patchRoot
Assert-NewPath $patchZip
Assert-NewPath $patchChecksumPath
if ($buildFullPackage) {
    Assert-NewPath $packageRoot
    Assert-NewPath $packageZip
    Assert-NewPath $checksumPath
}

New-Item -ItemType Directory -Path $patchAppDirectory -Force | Out-Null
Push-Location $repoRoot
try {
    Invoke-DotnetChecked @("restore", $nativeProject, "-r", $rid)
    Invoke-DotnetChecked @(
        "publish", $nativeProject,
        "-c", "Release",
        "-r", $rid,
        "--self-contained", "false",
        "-p:PublishReadyToRun=false",
        "-p:UseAppHost=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "--no-restore",
        "-o", $patchAppDirectory)

    $releaseMetadata = [ordered]@{
        layoutVersion = 1
        product = "Vector 2D Animation Engine"
        version = $Version
        rid = $rid
        targetFramework = "net8.0-windows"
        compatibleRuntime = "8.0.x"
        readyToRun = $false
        packageType = "application-patch"
    } | ConvertTo-Json
    Write-Utf8NoBom -Path (Join-Path $patchAppDirectory "release.json") -Content ($releaseMetadata + [Environment]::NewLine)

    Compress-Archive -LiteralPath $patchAppDirectory -DestinationPath $patchZip -CompressionLevel Optimal
    Assert-ArchiveRoots -ArchivePath $patchZip -ContainerName $patchName -ExpectedRoots @(".V2DEngine")
    $patchHash = (Get-FileHash -LiteralPath $patchZip -Algorithm SHA256).Hash
    Write-Utf8NoBom -Path $patchChecksumPath -Content ("$patchHash *$patchName.zip" + [Environment]::NewLine)

    if ($buildFullPackage) {
        New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
        $launcherPath = Join-Path $packageRoot "VectorAnimationEngine.exe"
        $appHostArguments = @{
            DotnetRoot = $dotnetRoot
            Destination = $launcherPath
        }
        New-SplitAppHost @appHostArguments
        if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
            throw "The app-relative distribution launcher was not generated: $launcherPath"
        }
        $unexpectedRootFiles = @(Get-ChildItem -LiteralPath $packageRoot -File |
            Where-Object { $_.Name -ne "VectorAnimationEngine.exe" })
        if ($unexpectedRootFiles.Count -gt 0) {
            throw "The distribution launcher produced unexpected root files: $($unexpectedRootFiles.Name -join ', ')"
        }

        Copy-DirectoryExact -Source $patchAppDirectory -Destination (Join-Path $packageRoot ".V2DEngine")
        $runtimeDirectory = Join-Path $packageRoot ".Runtime"
        New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
        Copy-Item -LiteralPath $dotnetPath -Destination $runtimeDirectory
        foreach ($notice in @("LICENSE.txt", "ThirdPartyNotices.txt")) {
            $noticePath = Join-Path $dotnetRoot $notice
            if (Test-Path -LiteralPath $noticePath) {
                Copy-Item -LiteralPath $noticePath -Destination $runtimeDirectory
            }
        }
        Copy-DirectoryExact -Source $hostFxrSource -Destination (Join-Path $runtimeDirectory "host\fxr\$RuntimeVersion")
        Copy-DirectoryExact -Source $coreRuntimeSource -Destination (Join-Path $runtimeDirectory "shared\Microsoft.NETCore.App\$RuntimeVersion")
        Copy-DirectoryExact -Source $desktopRuntimeSource -Destination (Join-Path $runtimeDirectory "shared\Microsoft.WindowsDesktop.App\$RuntimeVersion")

        Compress-Archive -LiteralPath $packageRoot -DestinationPath $packageZip -CompressionLevel Optimal
        Assert-ArchiveRoots -ArchivePath $packageZip -ContainerName $packageName -ExpectedRoots @(
            ".Runtime",
            ".V2DEngine",
            "VectorAnimationEngine.exe")

        $hashTargets = @(
            Join-Path $packageRoot "VectorAnimationEngine.exe"
            Join-Path $packageRoot ".Runtime\dotnet.exe"
            Join-Path $packageRoot ".V2DEngine\VectorAnimationEngine.dll"
            $patchZip
            $packageZip
        )
        $hashLines = foreach ($target in $hashTargets) {
            $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            $relative = $target.Substring($artifactRoot.TrimEnd('\').Length + 1).Replace('\', '/')
            "$hash *$relative"
        }
        Write-Utf8NoBom -Path $checksumPath -Content (($hashLines -join [Environment]::NewLine) + [Environment]::NewLine)
    }
}
finally {
    Pop-Location
}

[pscustomobject]@{
    Version = $Version
    RuntimeVersion = if ($buildFullPackage) { $RuntimeVersion } else { $null }
    Mode = if ($buildFullPackage) { "FullWithPatch" } else { "Patch" }
    PatchOnly = -not $buildFullPackage
    PatchDirectory = $patchRoot
    PatchArchive = $patchZip
    PatchChecksumManifest = $patchChecksumPath
    FullPackageDirectory = if ($buildFullPackage) { $packageRoot } else { $null }
    FullPackageArchive = if ($buildFullPackage) { $packageZip } else { $null }
    FullPackageChecksumManifest = if ($buildFullPackage) { $checksumPath } else { $null }
} | ConvertTo-Json -Compress
