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
    if (-not $nameMatch.Success) {
        Add-WorkflowError "Missing or invalid skill name: $skillFile"
        continue
    }

    $name = $nameMatch.Groups["value"].Value
    if ($name -cne $skillDirectory.Name) {
        Add-WorkflowError "Skill name '$name' does not match directory '$($skillDirectory.Name)'"
    }
    if (-not $skillNames.Add($name)) {
        Add-WorkflowError "Duplicate skill name: $name"
    }
    if (-not $descriptionMatch.Success -or $descriptionMatch.Groups["value"].Value -match 'TODO') {
        Add-WorkflowError "Missing or placeholder description: $skillFile"
    }

    $manifestFile = Join-Path $skillDirectory.FullName "agents\openai.yaml"
    if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
        Add-WorkflowError "Missing agents/openai.yaml for $name"
    } else {
        $manifestText = [IO.File]::ReadAllText($manifestFile)
        $promptMatch = [regex]::Match($manifestText, '(?m)^\s*default_prompt:\s*"(?<value>.*)"\s*$')
        $skillToken = '$' + $name
        if (-not $promptMatch.Success -or $promptMatch.Groups["value"].Value.IndexOf($skillToken, [StringComparison]::Ordinal) -lt 0) {
            Add-WorkflowError "default_prompt for $name must mention $skillToken"
        }
    }

    $markdownFiles = @($skillFile)
    $referenceRoot = Join-Path $skillDirectory.FullName "references"
    if (Test-Path -LiteralPath $referenceRoot -PathType Container) {
        $markdownFiles += @(Get-ChildItem -LiteralPath $referenceRoot -Recurse -File -Filter *.md | Select-Object -ExpandProperty FullName)
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
