param([switch]$Publish, [switch]$Verify)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $taskDotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\cli-home'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_ROOT = Split-Path -Parent $taskDotnet
    function Invoke-Dotnet([string[]]$Arguments) { & $taskDotnet @Arguments; if ($LASTEXITCODE -ne 0) { throw "dotnet başarısız: $Arguments" } }

    # The app references BroidShot.Core and every registered module, so this builds them too.
    Invoke-Dotnet -Arguments @('restore', 'src/BroidShot/BroidShot.csproj', '--configfile', 'NuGet.Config')
    Invoke-Dotnet -Arguments @('build', 'src/BroidShot/BroidShot.csproj', '--no-restore', '-c', 'Release')

    # Module projects (src/BroidShot.Modules.*) are discovered, so a module builds before it is registered in the app.
    $taskModules = @(Get-ChildItem -LiteralPath 'src' -Directory -Filter 'BroidShot.Modules.*' |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter '*.csproj' -File } | Sort-Object FullName | ForEach-Object { $_.FullName })
    foreach ($taskProject in $taskModules) {
        Invoke-Dotnet -Arguments @('restore', $taskProject, '--configfile', 'NuGet.Config')
        Invoke-Dotnet -Arguments @('build', $taskProject, '--no-restore', '-c', 'Release')
    }

    if ($Verify) {
        # Existing verifications first, then every other tests/*/*.Verification.csproj (module tests are picked up automatically).
        $taskKnown = @('tests/Editor.Verification/Editor.Verification.csproj', 'tests/Capture.Verification/Capture.Verification.csproj',
            'tests/Lifecycle.Verification/Lifecycle.Verification.csproj', 'tests/Desktop.Verification/Desktop.Verification.csproj',
            'src/BroidShot/Services/Verification/Services.Verification.csproj', 'tests/Core.Verification/Core.Verification.csproj') |
            ForEach-Object { (Resolve-Path -LiteralPath $_).Path }
        $taskDiscovered = @(Get-ChildItem -LiteralPath 'tests' -Directory |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter '*.Verification.csproj' -File } |
            Sort-Object FullName | ForEach-Object { $_.FullName } | Where-Object { $taskKnown -notcontains $_ })
        foreach ($taskProject in @($taskKnown) + $taskDiscovered) {
            Invoke-Dotnet -Arguments @('restore', $taskProject, '--configfile', 'NuGet.Config')
            Invoke-Dotnet -Arguments @('run', '--project', $taskProject, '--no-restore', '-c', 'Verification')
        }
    }
    if ($Publish) {
        & (Join-Path $PSScriptRoot 'publish.ps1')
    }
} finally { Pop-Location }
