#requires -Version 7.0
# Checks before a push: app build (warnings are errors), tests, README download links. Release
# packaging lives in pushRelease.ps1.
param([Alias('h')][switch]$Help)
$ErrorActionPreference = 'Stop'
if ($Help) { Write-Host 'prePush.ps1'; return }
. "$PSScriptRoot\scriptHelper.ps1"
Set-Location -LiteralPath $repoRoot
runNativeCommand dotnet @('build', $csproj, '-p:Platform=x64', '-nologo') 'dotnet build'
runNativeCommand dotnet @('test', "$srcRoot\${projectName}Tests\${projectName}Tests.csproj", '-nologo') 'dotnet test'
& "$PSScriptRoot\updateReadme.ps1"
Write-Host 'Pre-push checks passed. No commit or push was performed.'
closeOut 0
