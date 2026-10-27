param([string]$Version = '0.4.0', [switch]$Verify)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be a three-part release number, for example 0.4.1.' }
Push-Location $PSScriptRoot
try {
    $taskDotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\cli-home'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\nuget'
    $env:DOTNET_ROOT = Split-Path -Parent $taskDotnet
    function Invoke-ReleaseDotnet([string[]]$Arguments) {
        & $taskDotnet @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $Arguments" }
    }
    if ($Verify) {
        foreach ($taskTest in @('Core','Updates')) {
            $taskProject = "tests/$taskTest.Verification/$taskTest.Verification.csproj"
            Invoke-ReleaseDotnet -Arguments @('restore', $taskProject, '--configfile', 'NuGet.Config', '--disable-parallel', '-m:1', '-nr:false')
            Invoke-ReleaseDotnet -Arguments @('build', $taskProject, '--no-restore', '-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
            Invoke-ReleaseDotnet -Arguments @("tests/$taskTest.Verification/bin/Release/net10.0-windows10.0.19041.0/$taskTest.Verification.dll")
        }
    }
    Invoke-ReleaseDotnet -Arguments @('restore', 'src/BroidShot/BroidShot.csproj', '--configfile', 'NuGet.Config', '-r', 'win-x64', '-p:SelfContained=true', '--disable-parallel', '-m:1', '-nr:false')
    $taskOutput = Join-Path $PSScriptRoot 'artifacts\BroidShot'
    if (@(Get-Process -Name BroidShot -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $taskOutput 'BroidShot.exe') }).Count -gt 0) {
        throw 'Uygulamayı tepsi menüsünden kapatıp paketi yeniden oluşturun.'
    }
    $taskStage = Join-Path $PSScriptRoot ('artifacts\.publish-' + [Guid]::NewGuid().ToString('N'))
    Invoke-ReleaseDotnet -Arguments @('publish', 'src/BroidShot/BroidShot.csproj', '--no-restore', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $taskStage,
        '-m:1', '-nr:false', '-p:UseSharedCompilation=false', "-p:Version=$Version")
    Copy-Item -LiteralPath 'README.md' -Destination (Join-Path $taskStage 'README.md')
    $taskFiles = @(Get-ChildItem -LiteralPath $taskStage -Recurse -File | Sort-Object FullName | ForEach-Object {
        @{ Path = [IO.Path]::GetRelativePath($taskStage, $_.FullName).Replace('\','/'); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    @{ Version = $Version; Files = $taskFiles } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskStage 'BroidShot.package.json') -Encoding utf8
    $taskZip = Join-Path $PSScriptRoot 'artifacts\BroidShot-win-x64.zip'
    Compress-Archive -Path (Join-Path $taskStage '*') -DestinationPath $taskZip -Force
    $taskHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($taskZip + '.sha256') -Value "$taskHash  BroidShot-win-x64.zip" -Encoding ascii
    if (Test-Path -LiteralPath $taskOutput) {
        $taskResolved = (Resolve-Path -LiteralPath $taskOutput).ProviderPath
        if ($taskResolved -ne $taskOutput -or $taskResolved -notlike "$PSScriptRoot\artifacts\*") { throw 'Unexpected publish output path.' }
        if ((Get-Item -LiteralPath $taskOutput).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Publish output is a link.' }
        if (@(Get-ChildItem -LiteralPath $taskOutput -Recurse -Force -Attributes ReparsePoint).Count -gt 0) { throw 'Publish output contains links.' }
        Remove-Item -LiteralPath $taskOutput -Recurse -Force
    }
    Move-Item -LiteralPath $taskStage -Destination $taskOutput
    Write-Output "Release $Version ready locally: $taskOutput"
    Write-Output "SHA-256: $taskHash"
} finally { Pop-Location }
