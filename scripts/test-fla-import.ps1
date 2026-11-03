param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\native\bin\Release\net8.0-windows\VectorAnimationEngine.dll'),
    [switch]$Probe
)

$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path
$AssemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

if ($Probe) {
    $bin = Split-Path -Parent $AssemblyPath
    [Runtime.Loader.AssemblyLoadContext]::Default.add_Resolving({
        param($context, $name)
        $candidate = Join-Path $bin ($name.Name + '.dll')
        if (Test-Path -LiteralPath $candidate) { return $context.LoadFromAssemblyPath($candidate) }
        return $null
    })
    $assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $flags = [Reflection.BindingFlags]'Static,NonPublic'
    $importer = $assembly.GetType('VectorAnimationEngine.FlashDocumentImporter', $true)
    $result = $null
    $phase = 'import'
    try {
        $result = $importer.GetMethod('Load', $flags).Invoke($null, @([string]$Source, $true))
        $phase = 'save'
        $store = $assembly.GetType('VectorAnimationEngine.ProjectVaultStore', $true)
        $manifest = Join-Path $OutputDirectory 'Project\Imported.v2dProject'
        $manifest = $store.GetMethod('Save', $flags).Invoke($null, @($result.Project, [string]$manifest))
        $phase = 'reopen'
        $reopened = $store.GetMethod('Load', $flags).Invoke($null, @([string]$manifest))
        $phase = 'compare'
        foreach ($collection in @('DrawingObjects', 'Scenes')) {
            $before = @($result.Project.$collection | ForEach-Object Id | Sort-Object)
            $after = @($reopened.$collection | ForEach-Object Id | Sort-Object)
            if (($before -join ',') -cne ($after -join ',')) { throw "Save/Open changed $collection IDs." }
            foreach ($original in $result.Project.$collection) {
                $restored = $reopened.$collection | Where-Object Id -CEQ $original.Id | Select-Object -First 1
                if ($original.FrameCount -ne $restored.FrameCount) { throw "Save/Open changed frame count: $($original.Id)." }
                $originalGeometry = if ($collection -eq 'DrawingObjects') { $original.Scene } else { $original.DrawingScene }
                $restoredGeometry = if ($collection -eq 'DrawingObjects') { $restored.Scene } else { $restored.DrawingScene }
                if ($originalGeometry.ObjectCount -ne $restoredGeometry.ObjectCount) {
                    throw "Save/Open changed geometry count: $($original.Id)."
                }
                $originalKeys = $original.Timeline.CreateSnapshot() | ConvertTo-Json -Depth 60 -Compress
                $restoredKeys = $restored.Timeline.CreateSnapshot() | ConvertTo-Json -Depth 60 -Compress
                if ($originalKeys -cne $restoredKeys) { throw "Save/Open changed timeline: $($original.Id)." }
                $originalInstances = @($original.Instances | Sort-Object Id) | ConvertTo-Json -Depth 60 -Compress
                $restoredInstances = @($restored.Instances | Sort-Object Id) | ConvertTo-Json -Depth 60 -Compress
                if ($originalInstances -cne $restoredInstances) { throw "Save/Open changed instance data: $($original.Id)." }
            }
        }
        [pscustomobject]@{ Path=$Source; Status='ok'; Phase=$phase; Frames=$result.FrameCount; Diagnostics=@($result.Diagnostics) } |
            ConvertTo-Json -Depth 8 -Compress
    } catch {
        $failure = $_.Exception
        while ($failure.InnerException) { $failure = $failure.InnerException }
        [pscustomobject]@{ Path=$Source; Status='failed'; Phase=$phase; Error=$failure.Message; Type=$failure.GetType().FullName } |
            ConvertTo-Json -Compress
    } finally {
        if ($result) { $null = $importer.GetMethod('DeleteTemporaryImages', $flags).Invoke($null, @(,$result.TemporaryFiles)) }
    }
    exit
}

$files = if (Test-Path -LiteralPath $Source -PathType Container) {
    @(Get-ChildItem -LiteralPath $Source -Recurse -File -Filter '*.fla' | Sort-Object FullName)
} else { @(Get-Item -LiteralPath $Source) }
if ($files.Count -eq 0) { throw 'No FLA files found.' }
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'inventory.csv')) { throw 'Use a new output directory for each run.' }
$files | Select-Object FullName, Length | Export-Csv (Join-Path $OutputDirectory 'inventory.csv') -NoTypeInformation
$buildHash = (Get-FileHash -LiteralPath $AssemblyPath).Hash
[pscustomobject]@{ Path=$AssemblyPath; Hash=$buildHash } |
    ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'build.json')
$failed = 0
$records = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $files.Count; $index++) {
    $caseDirectory = Join-Path $OutputDirectory ('case-{0:D4}' -f ($index + 1))
    [IO.Directory]::CreateDirectory($caseDirectory) | Out-Null
    $output = @(& pwsh -NoProfile -STA -File $PSCommandPath -Source $files[$index].FullName -OutputDirectory $caseDirectory -AssemblyPath $AssemblyPath -Probe)
    $code = $LASTEXITCODE
    try {
        if ($code -ne 0) { throw "Probe exited with code $code." }
        $record = ($output -join "`n") | ConvertFrom-Json
        if ($record.Status -notin @('ok', 'failed')) { throw 'Probe returned no valid status.' }
    } catch {
        $record = [pscustomobject]@{ Path=$files[$index].FullName; Status='failed'; Phase='harness'; Error=$_.Exception.Message }
    }
    $record | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $caseDirectory 'result.json')
    $records.Add($record)
    if ($record.Status -eq 'failed') { $failed++ }
    Write-Host "$($index + 1)/$($files.Count) $($record.Status): $($files[$index].Name)"
}
$records | Select-Object Path, Status, Phase, Error |
    Export-Csv (Join-Path $OutputDirectory 'results.csv') -NoTypeInformation
$unchangedBuild = (Get-FileHash -LiteralPath $AssemblyPath).Hash -eq $buildHash
[pscustomobject]@{
    Total=$files.Count; Passed=($files.Count - $failed); Failed=$failed
    AssemblyHash=$buildHash; UnchangedBuild=$unchangedBuild
    Phases=@($records | Group-Object Status, Phase | Select-Object Name, Count)
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'summary.json')
if (!$unchangedBuild) { throw 'The importer assembly changed during the scan; results do not describe one fixed build.' }
Write-Host "Checked $($files.Count) files; $failed failed. Results: $OutputDirectory"
if ($failed -gt 0) { exit 1 }
