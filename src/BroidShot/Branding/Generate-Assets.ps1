$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$taskOutput = Join-Path $taskRoot 'artifacts/branding/tools'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Generator.Program.cs.txt') -Destination (Join-Path $taskOutput 'Program.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Generator.csproj.txt') -Destination (Join-Path $taskOutput 'Branding.Generator.csproj')
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools/cli-home'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools/nuget'
Push-Location $taskRoot
try {
    & $taskDotnet restore (Join-Path $taskOutput 'Branding.Generator.csproj') --configfile (Join-Path $taskRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Logo generator restore failed.' }
    & $taskDotnet run --project (Join-Path $taskOutput 'Branding.Generator.csproj') --no-restore -- $taskRoot
    if ($LASTEXITCODE -ne 0) { throw 'Logo generator failed.' }
} finally { Pop-Location }
