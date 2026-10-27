#requires -Version 7.0
param([Alias('h')][switch]$Help, [string]$Architecture)
$ErrorActionPreference = 'Stop'
if ($Help) { Write-Host 'build.ps1 [-x64]'; return }
. "$PSScriptRoot\scriptHelper.ps1"
Write-Host "=== building $projectName... ==="
Set-Location -LiteralPath $repoRoot
checkVerBuild $Architecture
$targetArchitecture = getArchitecture @($Architecture)
$targets = getBuildTargets $targetArchitecture
runNativeCommand dotnet @('restore', $csproj) 'dotnet restore'
foreach ($target in $targets) {
    deleteDir $target.BinFolder
    New-Item -ItemType Directory -Path $target.BinFolder -Force | Out-Null
    runNativeCommand dotnet @('publish', $csproj, '-c', 'Release', '-r', $target.RuntimeIdentifier, '--no-self-contained', '-p:PublishReadyToRun=true', '-o', $target.BinFolder) "dotnet publish $($target.Architecture)"
    Copy-Item -LiteralPath $version -Destination "$($target.BinFolder)\Version" -Force
    publishCleanup $target.BinFolder
}
closeOut 0
