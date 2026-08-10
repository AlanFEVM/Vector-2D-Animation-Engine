[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
$skillsRoot = Join-Path $repoRoot ".agents\skills"
$errors = [Collections.Generic.List[string]]::new()
$skillNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$linkCount = 0

function Add-WorkflowError {
    param([Parameter(Mandatory = $true)][string]$Message)
    $errors.Add($Message)
}

$agentsFile = Join-Path $repoRoot "AGENTS.md"
if (-not (Test-Path -LiteralPath $agentsFile -PathType Leaf)) {
    Add-WorkflowError "Missing root AGENTS.md"
} else {
    $agentsText = [IO.File]::ReadAllText($agentsFile)
    if ($agentsText.IndexOf('$vector2d-coordinate-agents', [StringComparison]::Ordinal) -lt 0) {
        Add-WorkflowError "AGENTS.md does not route multi-Agent work to `$vector2d-coordinate-agents"
    }
}

$skillDirectories = @(Get-ChildItem -LiteralPath $skillsRoot -Directory | Sort-Object Name)
if ($skillDirectories.Count -eq 0) {
    Add-WorkflowError "No skills found under .agents/skills"
}

foreach ($skillDirectory in $skillDirectories) {
    $skillFile = Join-Path $skillDirectory.FullName "SKILL.md"
    if (-not (Test-Path -LiteralPath $skillFile -PathType Leaf)) {
        Add-WorkflowError "Missing SKILL.md: $($skillDirectory.FullName)"
        continue
    }

    $skillText = [IO.File]::ReadAllText($skillFile)
    $skillLineCount = @($skillText -split '\r?\n').Count
    if ($skillLineCount -gt 500) {
        Add-WorkflowError "SKILL.md exceeds 500 lines: $skillFile ($skillLineCount)"
    }

    $frontmatter = [regex]::Match(
        $skillText,
        '\A---\r?\n(?<value>.*?)\r?\n---(?:\r?\n|$)',
        [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $frontmatter.Success) {
        Add-WorkflowError "Invalid frontmatter: $skillFile"
        continue
    }

    $nameMatch = [regex]::Match($frontmatter.Groups["value"].Value, '(?m)^name:\s*(?<value>[a-z0-9][a-z0-9-]*)\s*$')
    $descriptionMatch = [regex]::Match($frontmatter.Groups["value"].Value, '(?m)^description:\s*(?<value>.+?)\s*$')
    foreach ($frontmatterLine in @($frontmatter.Groups["value"].Value -split '\r?\n')) {
        if (-not [string]::IsNullOrWhiteSpace($frontmatterLine) -and
                $frontmatterLine -notmatch '^(name|description):\s*.+$') {
            Add-WorkflowError "Unsupported SKILL.md frontmatter field in $skillFile`: $frontmatterLine"
        }
    }
    if (-not $nameMatch.Success) {
        Add-WorkflowError "Missing or invalid skill name: $skillFile"
        continue
    }

    $name = $nameMatch.Groups["value"].Value
    if ($name.Length -gt 64) {
        Add-WorkflowError "Skill name exceeds 64 characters: $name"
    }
    if ($name -cne $skillDirectory.Name) {
        Add-WorkflowError "Skill name '$name' does not match directory '$($skillDirectory.Name)'"
    }
    if (-not $skillNames.Add($name)) {
        Add-WorkflowError "Duplicate skill name: $name"
    }
    if (-not $descriptionMatch.Success -or
            $descriptionMatch.Groups["value"].Value -match 'TODO' -or
            $descriptionMatch.Groups["value"].Value.Length -lt 40) {
        Add-WorkflowError "Missing or placeholder description: $skillFile"
    }

    $manifestFile = Join-Path $skillDirectory.FullName "agents\openai.yaml"
    if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
        Add-WorkflowError "Missing agents/openai.yaml for $name"
    } else {
        $manifestText = [IO.File]::ReadAllText($manifestFile)
        $displayNameMatch = [regex]::Match($manifestText, '(?m)^\s*display_name:\s*"(?<value>.+)"\s*$')
        $shortDescriptionMatch = [regex]::Match($manifestText, '(?m)^\s*short_description:\s*"(?<value>.+)"\s*$')
        $promptMatch = [regex]::Match($manifestText, '(?m)^\s*default_prompt:\s*"(?<value>.*)"\s*$')
        $skillToken = '$' + $name
        if (-not $displayNameMatch.Success) {
            Add-WorkflowError "Missing quoted display_name for $name"
        }
        if (-not $shortDescriptionMatch.Success -or
                $shortDescriptionMatch.Groups["value"].Value.Length -lt 25 -or
                $shortDescriptionMatch.Groups["value"].Value.Length -gt 64) {
            Add-WorkflowError "short_description for $name must be a quoted 25-64 character string"
        }
        if (-not $promptMatch.Success -or $promptMatch.Groups["value"].Value.IndexOf($skillToken, [StringComparison]::Ordinal) -lt 0) {
            Add-WorkflowError "default_prompt for $name must mention $skillToken"
        }
    }

    $markdownFiles = @($skillFile)
    $referenceRoot = Join-Path $skillDirectory.FullName "references"
    if (Test-Path -LiteralPath $referenceRoot -PathType Container) {
        $referenceFiles = @(Get-ChildItem -LiteralPath $referenceRoot -Recurse -File -Filter *.md)
        $markdownFiles += @($referenceFiles | Select-Object -ExpandProperty FullName)
        foreach ($referenceFile in $referenceFiles) {
            if ($referenceFile.DirectoryName -ne $referenceRoot) {
                Add-WorkflowError "Nested reference files are not allowed: $($referenceFile.FullName)"
                continue
            }
            $referenceLink = "references/" + $referenceFile.Name
            if ($skillText.IndexOf($referenceLink, [StringComparison]::Ordinal) -lt 0) {
                Add-WorkflowError "Reference is not linked directly from SKILL.md: $($referenceFile.FullName)"
            }
        }
    }

    foreach ($markdownFile in $markdownFiles) {
        $markdown = [IO.File]::ReadAllText($markdownFile)
        foreach ($link in [regex]::Matches($markdown, '(?<!!)\[[^\]]*\]\((?<target>[^)]+)\)')) {
            $target = $link.Groups["target"].Value.Trim().Trim('<', '>')
            if ($target.Length -eq 0 -or $target.StartsWith('#') -or $target -match '^[a-z][a-z0-9+.-]*:') {
                continue
            }

            $relativePath = ($target -split '#', 2)[0]
            $resolvedPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $markdownFile) ([Uri]::UnescapeDataString($relativePath))))
            $linkCount++
            if (-not (Test-Path -LiteralPath $resolvedPath)) {
                Add-WorkflowError "Broken link '$target' in $markdownFile"
            }
        }
    }
}

if (Test-Path -LiteralPath $agentsFile -PathType Leaf) {
    foreach ($skillName in $skillNames) {
        $skillToken = '$' + $skillName
        if ($agentsText.IndexOf($skillToken, [StringComparison]::Ordinal) -lt 0) {
            Add-WorkflowError "AGENTS.md does not route skill $skillToken"
        }
    }
}

$benchmarkMapFile = Join-Path $skillsRoot "vector2d-validate-change\references\benchmark-map.md"
if (-not (Test-Path -LiteralPath $benchmarkMapFile -PathType Leaf)) {
    Add-WorkflowError "Missing benchmark partial map: $benchmarkMapFile"
} else {
    $benchmarkMapText = [IO.File]::ReadAllText($benchmarkMapFile)
    foreach ($benchmarkFile in @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "native\App") -File -Filter "Benchmark*.cs")) {
        if ($benchmarkMapText.IndexOf($benchmarkFile.Name, [StringComparison]::Ordinal) -lt 0) {
            Add-WorkflowError "Benchmark partial is not routed in benchmark-map.md: $($benchmarkFile.Name)"
        }
    }
}

$powerShellFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot ".agents") -Recurse -File -Filter *.ps1)
foreach ($powerShellFile in $powerShellFiles) {
    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile(
        $powerShellFile.FullName,
        [ref]$tokens,
        [ref]$parseErrors) | Out-Null
    foreach ($parseError in @($parseErrors)) {
        Add-WorkflowError "PowerShell parse error in $($powerShellFile.FullName): $($parseError.Message)"
    }
}

$validationScript = Join-Path $skillsRoot "vector2d-validate-change\scripts\invoke-validation.ps1"
if (Test-Path -LiteralPath $validationScript -PathType Leaf) {
    try {
        $validationPlan = (& $validationScript -Suite All -Plan | ConvertFrom-Json)
        $expectedSuites = @("Build", "Launcher", "Release", "Timeline", "Pressure", "Freehand", "Stress", "Render")
        foreach ($expectedSuite in $expectedSuites) {
            if ($validationPlan.Suites -notcontains $expectedSuite) {
                Add-WorkflowError "Validation All plan does not include suite: $expectedSuite"
            }
        }
        if ($validationPlan.Builds -notcontains "ReleaseManager" -or
                $validationPlan.Builds -notcontains "SingleExeRelease") {
            Add-WorkflowError "Validation All plan does not include Release Manager and single-EXE builds"
        }
    } catch {
        Add-WorkflowError "Validation plan smoke test failed: $($_.Exception.Message)"
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

[pscustomobject]@{
    Skills = $skillDirectories.Count
    MarkdownLinks = $linkCount
    PowerShellScripts = $powerShellFiles.Count
    Status = "ok"
} | ConvertTo-Json -Compress
