#requires -Version 7.0
# Checks before a push: app build (warnings are errors), tests, installer build, README download
# links. Release zip and upload live in pushRelease.ps1.
param([Alias('h')][switch]$Help)
$ErrorActionPreference = 'Stop'
if ($Help) { Write-Host 'prePush.ps1'; return }
. "$PSScriptRoot\scriptHelper.ps1"
Set-Location -LiteralPath $repoRoot
runNativeCommand dotnet @('build', $csproj, '-p:Platform=x64', '-nologo') 'dotnet build'
runNativeCommand dotnet @('test', "$srcRoot\${projectName}Tests\${projectName}Tests.csproj", '-nologo') 'dotnet test'
invokeScript 'buildInstaller.ps1'
invokeScript 'updateReadme.ps1'
Write-Host 'Pre-push checks passed. No commit or push was performed.'
closeOut 0
