param([switch]$BuildOutputs)
$ErrorActionPreference = 'Stop'
$taskWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$taskArtifacts = Join-Path $taskWorkspace 'artifacts'
$taskRemoved = 0
function Remove-Generated([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $taskResolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    if (-not $taskResolved.StartsWith($taskWorkspace + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup target is outside this workspace.' }
    if ($taskResolved -eq (Join-Path $taskArtifacts 'BroidShot') -or $taskResolved -like "$taskWorkspace\.tools\*") { throw 'Active application and toolchain are protected.' }
    $taskItem = Get-Item -LiteralPath $taskResolved
    if ($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Cleanup target is a link.' }
    if ($taskItem.PSIsContainer -and @(Get-ChildItem -LiteralPath $taskResolved -Recurse -Force -Attributes ReparsePoint).Count -gt 0) { throw 'Cleanup content contains links.' }
    Remove-Item -LiteralPath $taskResolved -Recurse -Force
    $script:taskRemoved++
    Write-Output ('Removed: ' + [IO.Path]::GetRelativePath($taskWorkspace, $taskResolved))
}
foreach ($taskName in @('BroidShot-win-x64','BroidShot-launcher-win-x64','BroidShot-startup-win-x64','codex-handoff','launcher-preview','lifecycle-validation','ui-test-data','ui-test-data-v02')) {
    Remove-Generated (Join-Path $taskArtifacts $taskName)
}
foreach ($taskPattern in @('BroidShot-*-win-x64.zip','codex-*.log','launcher-*.log','updates-*.log','.publish-*')) {
    Get-ChildItem -LiteralPath $taskArtifacts -Filter $taskPattern -Force -ErrorAction SilentlyContinue | ForEach-Object { Remove-Generated $_.FullName }
}
Remove-Generated (Join-Path $taskArtifacts 'launcher-always-note.md')
if ($BuildOutputs) {
    $taskProjects = @(foreach ($taskTree in @('src','tests','artifacts/branding/tools')) {
        $taskFolder = Join-Path $taskWorkspace $taskTree
        if (Test-Path -LiteralPath $taskFolder) {
            Get-ChildItem -LiteralPath $taskFolder -Filter '*.csproj' -Recurse -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.DirectoryName }
        }
    }) | Sort-Object -Unique
    foreach ($taskProject in $taskProjects) {
        foreach ($taskName in @('bin','obj')) { Remove-Generated (Join-Path $taskProject $taskName) }
    }
    Remove-Generated (Join-Path $taskWorkspace 'src/BroidShot/Services/Verification/binint Verification')
}
Write-Output "Cleaned $taskRemoved generated outputs; source and .tools were preserved."
