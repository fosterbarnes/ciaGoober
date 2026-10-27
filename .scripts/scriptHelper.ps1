#requires -Version 7.0
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
$projectName = 'ciaGoober'
$projectExeName = "$projectName.exe"
$solution = "$repoRoot\$projectName.sln"
$srcRoot = "$repoRoot\src"
$csproj = "$srcRoot\$projectName\$projectName.csproj"
$dotnetFramework = 'net10.0-windows'
$versionFolder = "$repoRoot\.version"
$version = "$versionFolder\version"
$versionBuild = "$versionFolder\versionBuild"
$versionTag = "$versionFolder\versionTag"
$buildNotes = "$repoRoot\buildNotes.txt"
$readme = "$repoRoot\README.md"
$installerFolder = "$repoRoot\.installer"
$installerOutput = "$installerFolder\Output"
$publishFolder = "$repoRoot\publish"
$iconDir = "$repoRoot\.res\icon"
$iconPng = "$iconDir\icon.png"
$iconIco = "$iconDir\icon.ico"
$wizardSmallPng = "$iconDir\installer-wizard-small.png"
$wizardLargePng = "$iconDir\installer-wizard-large.png"
$licenseFile = "$repoRoot\LICENSE"
$appPublisher = 'fosterbarnes'
$appURL = "https://github.com/$appPublisher/$projectName"
$ghRepo = "$appPublisher/$projectName"
$buildNotesMd = "$repoRoot\buildNotes.md"
# Private word list for version tags (copied from oc, gitignored).
$yapWords = "$repoRoot\.res\yapBot.json"
# Commit subject labels in decision order; AGENTS.md (buildNotes.txt section) defines each.
$commitLabels = 'fix', 'feat.', 'patch', 'perf.', 'deps.', 'scripts', 'cleanup', 'docs', 'misc.'
$versionContents = ([IO.File]::ReadAllText($version)).Trim()
# Releases and README links always use the version; versionTag is only the commit codename.
$releaseTag = "v$versionContents"
$imageSizes = @(16, 32, 48, 64, 128, 256)
$pngInstallerSizes = @(512, 256, 128, 64, 32)
# README download buttons are shared SVGs in fosterbarnes/res/btn.
$buttonURL = 'https://raw.githubusercontent.com/fosterbarnes/res/main/btn'
# x64 only: bannertool.exe and makerom.exe are x64 builds.
$buildTargets = @(
    @{ Architecture = 'x64'; RuntimeIdentifier = 'win-x64'; BinFolder = "$publishFolder\build\x64"; ExePath = "$publishFolder\build\x64\$projectExeName"; InstallerName = "$projectName-x64-installer.exe"; InstallerScript = "$installerFolder\$projectName.x64.installer.iss"; InstallerButton = 'x64Installer.svg'; PortableButton = 'x64Portable.svg' }
)
$noBom = New-Object System.Text.UTF8Encoding $false
$weztermExe = (Get-Command wezterm.exe -ErrorAction SilentlyContinue)?.Source

function writeFileNoBom {
    param([Parameter(Mandatory)][string]$LiteralPath, [Parameter(Mandatory)][string]$Content)
    [IO.File]::WriteAllText($LiteralPath, $Content, $noBom)
}

function requireVersion {
    $contents = [IO.File]::ReadAllText($version).Trim()
    if ($contents -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid $version`: expected one version in X.Y.Z format" }
    $contents
}

# Bumps one part of X.Y.Z, zeroes the lower parts and rolls a new version tag.
function bumpVersion {
    param([ValidateSet('Patch', 'Minor', 'Major')][string]$Part = 'Patch')
    $current = requireVersion
    [int[]]$parts = $current.Split('.')
    switch ($Part) {
        'Major' { $parts = @(($parts[0] + 1), 0, 0) }
        'Minor' { $parts = @($parts[0], ($parts[1] + 1), 0) }
        'Patch' { $parts[2]++ }
    }
    $next = $parts -join '.'
    writeFileNoBom -LiteralPath $version -Content "$next`n"
    "Version $current -> $next ($(newVersionTag))"
}

function newVersionTag { setVersionTag (getVersionTags 1) }

# Returns $Count distinct random camelCase two-word tags without writing anything.
function getVersionTags {
    param([int]$Count = 1)
    if (-not (Test-Path -LiteralPath $yapWords)) {
        throw "Missing word list $yapWords. Copy it from oc\.res\yapBot.json; it is private and gitignored."
    }
    $words = @(Get-Content -LiteralPath $yapWords -Raw | ConvertFrom-Json | Where-Object { $_ -match '^[A-Za-z]+$' })
    if (-not $words.Count) { throw "No usable words found in $yapWords." }
    $tags = [Collections.Generic.HashSet[string]]::new()
    while ($tags.Count -lt $Count) {
        $first = $words[[Security.Cryptography.RandomNumberGenerator]::GetInt32($words.Count)]
        $second = $words[[Security.Cryptography.RandomNumberGenerator]::GetInt32($words.Count)]
        [void]$tags.Add($first.ToLowerInvariant() + $second.Substring(0, 1).ToUpperInvariant() + $second.Substring(1).ToLowerInvariant())
    }
    @($tags)
}

function setVersionTag {
    param([Parameter(Mandatory)][string]$Tag)
    if ($Tag -cnotmatch '^[a-z]+[A-Z][a-z]*$') { throw "Invalid tag '$Tag': expected one camelCase two-word tag" }
    writeFileNoBom -LiteralPath $versionTag -Content "$Tag`n"
    $Tag
}

function requireVersionTag {
    if (-not (Test-Path -LiteralPath $versionTag)) {
        throw "Missing $versionTag`: run .scripts\bumpVersion.ps1 -RerollTag to generate one."
    }
    $contents = [IO.File]::ReadAllText($versionTag).Trim()
    if ($contents -cnotmatch '^[a-z]+[A-Z][a-z]*$') { throw "Invalid $versionTag`: expected one camelCase two-word tag" }
    $contents
}

# AGENTS.md Revision: a push with code changes needs bumpVersion.ps1. Docs and notes alone do not.
# Returns the changed or untracked code paths when the version still matches HEAD, else nothing.
# A HEAD without .version/version (the first commit) needs no bump.
function getUnbumpedCodeChanges {
    $headVersion = (& git -C $repoRoot show HEAD:.version/version 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -or $headVersion -ne (requireVersion)) { return @() }
    $changed = @(& git -C $repoRoot diff --name-only HEAD)
    if ($LASTEXITCODE) { throw "git diff failed (exit $LASTEXITCODE)." }
    $changed += @(& git -C $repoRoot ls-files -o --exclude-standard)
    if ($LASTEXITCODE) { throw "git ls-files failed (exit $LASTEXITCODE)." }
    @($changed | Where-Object { $_ -notmatch '(\.md$|^\.version/)' })
}

# buildNotes.txt is local, so git cannot tell a new message from the last push's: it is new when
# line 1 is set and differs from the last commit subject.
function testBuildNotesChanged {
    if (-not (Test-Path -LiteralPath $buildNotes)) { return $false }
    $subject = @([IO.File]::ReadAllLines($buildNotes))[0]
    if (-not "$subject".Trim()) { return $false }
    $head = (& git -C $repoRoot log -1 --format=%s | Out-String).Trim()
    if ($LASTEXITCODE) { throw "git log failed (exit $LASTEXITCODE)." }
    "$subject".Trim() -ne $head
}

# buildNotes.md entries (newest first) dated after the last commit. Headings start with
# "## yyyy-MM-dd (~HH:mm)", which sorts as text, so the walk stops at the first older entry.
function getNewBuildNotesEntries {
    if (-not (Test-Path -LiteralPath $buildNotesMd)) { return @() }
    $head = (& git -C $repoRoot log -1 --format=%cd --date=format-local:'%Y-%m-%d (~%H:%M)' | Out-String).Trim()
    if ($LASTEXITCODE) { throw "git log failed (exit $LASTEXITCODE)." }
    $entries = [Collections.Generic.List[string]]::new()
    $inHistory = $false
    foreach ($line in [IO.File]::ReadAllLines($buildNotesMd)) {
        if (-not $inHistory) { $inHistory = $line -eq '# Historical entries (newest first)'; continue }
        if (-not $entries.Count -and -not $line.StartsWith('## ')) { continue }
        if ($line -match '^## (\d{4}-\d{2}-\d{2} \(~\d{2}:\d{2}\))' -and
            [string]::CompareOrdinal($Matches[1], $head) -le 0) { break }
        $entries.Add($line)
    }
    @($entries)
}

# Throws unless $Subject is "<Prefix> [<label> > <summary>]", optionally "; <label> > <summary>".
# Summaries: no edge spaces, no trailing period, none of [ ] > ;. .NET regex is case-sensitive.
function assertCommitSubject {
    param([Parameter(Mandatory)][string]$Subject, [Parameter(Mandatory)][string]$Prefix)
    $labels = ($commitLabels | ForEach-Object { [regex]::Escape($_) }) -join '|'
    $segment = "($labels) > [^\s\[\]>;](?:[^\[\]>;]*[^\s.\[\]>;])?"
    $match = [regex]::Match($Subject, "^$([regex]::Escape($Prefix)) \[$segment(?:; $segment)?\]$")
    if (-not $match.Success) {
        throw "Line 1 must be '$Prefix [<label> > <summary>]', optionally '; <label> > <summary>'. " +
            "Labels: $($commitLabels -join ' ')"
    }
    $first, $second = $match.Groups[1].Value, $match.Groups[2].Value
    if ($second -and $first -eq $second) { throw "Line 1 repeats label '$first'; merge the segments." }
    if ($second -and 'misc.' -in $first, $second) { throw "'misc.' must be the only segment." }
    if ($Subject -match '[^\x20-\x7E]') { throw 'Line 1 must be plain ASCII.' }
    if ($Subject.Length -gt 72) { throw "Line 1 is $($Subject.Length) characters; the limit is 72." }
}

function setVerBuild {
    param([Parameter(Mandatory)][string]$Platform)
    writeFileNoBom -LiteralPath $versionBuild -Content ($Platform.Trim() + "`n")
}

function checkVerBuild {
    param([string]$Architecture)
    if (Test-Path -LiteralPath $versionBuild) { return }
    setVerBuild ([string]::IsNullOrWhiteSpace($Architecture) ? 'x64' : $Architecture)
}

function getArchitecture {
    param([string[]]$FlagArgs)
    $found = @()
    foreach ($arg in @($FlagArgs)) {
        if ([string]::IsNullOrWhiteSpace("$arg")) { continue }
        switch -Regex ("$arg".Trim()) {
            '(?i)^(x86|--x86|-x86|--86|-86)$' { $found += 'x86' }
            '(?i)^(x64|--x64|-x64|--64|-64)$' { $found += 'x64' }
            '(?i)^(arm64|--arm64|-arm64|--arm|-arm)$' { $found += 'arm64' }
            '(?i)^(--help|-h)$' { return 'help' }
            default { throw "Unknown architecture flag: $arg" }
        }
    }
    $unique = @($found | Select-Object -Unique)
    if ($unique.Count -gt 1) { throw "Conflicting architecture flags: $($unique -join ', ')" }
    if ($unique.Count -eq 1) { return $unique[0] }
    $null
}

function getBuildTargets {
    param([string]$Architecture)
    if (-not $Architecture) { return $buildTargets }
    $target = @($buildTargets | Where-Object Architecture -eq $Architecture)
    if (-not $target) { throw "No build target for architecture: $Architecture" }
    return ,$target[0]
}

function deleteDir {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
    if (Test-Path -LiteralPath $Path) { throw "Could not remove '$Path'." }
}

function publishCleanup {
    param([Parameter(Mandatory)][string]$BinFolder)
    Get-ChildItem -LiteralPath $BinFolder -Filter '*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force
}

function runNativeCommand {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)]$ArgumentList, [Parameter(Mandatory)][string]$Name)
    & $FilePath @ArgumentList
    if ($LASTEXITCODE) { throw "$Name failed (exit $LASTEXITCODE)." }
}

# Runs a sibling script in-process as part of a pipeline, so its closeOut does not count down or exit.
function invokeScript {
    param([Parameter(Mandatory)][string]$Name, [hashtable]$Params = @{})
    $previousPipeline = $env:BASE_BUILD_PIPELINE
    try {
        $env:BASE_BUILD_PIPELINE = '1'
        & "$PSScriptRoot\$Name" @Params
    } finally {
        $env:BASE_BUILD_PIPELINE = $previousPipeline
    }
}

function writeClearedLine {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][int]$PadWidth, [switch]$NoNewline)
    Write-Host "`r$Text$(' ' * [Math]::Max(0, $PadWidth - $Text.Length))" -NoNewline:$NoNewline
}

function closeOut {
    param([int]$Seconds = 5, [switch]$KeepOpen)
    if ($KeepOpen) { return }
    if ($env:BASE_BUILD_PIPELINE) { return }
    if ($Seconds -lt 0) { $Seconds = 0 }
    if ($Seconds -gt 0) {
        $pad = "closing after $Seconds seconds..."
        foreach ($n in $Seconds..1) {
            writeClearedLine -Text "closing after $n seconds..." -PadWidth $pad.Length -NoNewline
            Start-Sleep -Seconds 1
        }
        writeClearedLine -Text 'closing...' -PadWidth $pad.Length
    }
    $caller = $MyInvocation.PSCommandPath
    if ([string]::IsNullOrWhiteSpace($caller)) { return }
    $argv = [Environment]::GetCommandLineArgs()
    $fileArg = $null
    for ($i = 0; $i -lt $argv.Length; $i++) {
        if ("$($argv[$i])" -match '^(?i)-File$|^(?i)-f$') {
            if ($i + 1 -lt $argv.Length) { $fileArg = $argv[$i + 1] }
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($fileArg)) { return }
    try {
        $fileFull = [IO.Path]::GetFullPath($fileArg)
        $callerFull = [IO.Path]::GetFullPath($caller)
    } catch { return }
    if (-not [string]::Equals($fileFull, $callerFull, [StringComparison]::OrdinalIgnoreCase)) { return }
    try {
        if ($env:SCRIPT_OWN_PANE -and $env:WEZTERM_PANE -and $weztermExe) { & $weztermExe @('cli', 'kill-pane', '--pane-id', $env:WEZTERM_PANE) }
    } catch { }
    [Environment]::Exit(0)
}

function convertToIco {
    param([Parameter(Mandatory)][string]$InputPath)
    $source = (Resolve-Path -LiteralPath $InputPath).Path
    if ([IO.Path]::GetExtension($source).ToLowerInvariant() -eq '.svg') { throw 'SVG input is not supported.' }
    $output = [IO.Path]::ChangeExtension($source, '.ico')
    $arguments = [Collections.Generic.List[string]]::new()
    $arguments.Add($source)
    foreach ($size in $imageSizes) {
        $arguments.AddRange(@('(', '-clone', '0', '-resize', "${size}x${size}", ')'))
    }
    $arguments.Add('-delete'); $arguments.Add('0'); $arguments.Add($output)
    runNativeCommand -FilePath 'magick' -ArgumentList $arguments.ToArray() -Name 'ImageMagick ICO conversion'
    $output
}

function resizePng {
    param(
        [Parameter(Mandatory)][string]$InputPath,
        [int[]]$Sizes = $imageSizes
    )
    $source = (Resolve-Path -LiteralPath $InputPath).Path
    if ([IO.Path]::GetExtension($source).ToLowerInvariant() -eq '.svg') { throw 'SVG input is not supported.' }
    $directory = [IO.Path]::GetDirectoryName($source)
    $baseName = [IO.Path]::GetFileNameWithoutExtension($source)
    foreach ($size in $Sizes) {
        if ($size -le 0) { throw "Image size must be positive: $size" }
        $output = "$directory\${baseName}${size}.png"
        runNativeCommand -FilePath 'magick' -ArgumentList @($source, '-resize', "${size}x${size}", $output) -Name "ImageMagick PNG resize ($size)"
        $output
    }
}

function writeWizardImages {
    param([string]$InputPath = $iconPng)
    $source = (Resolve-Path -LiteralPath $InputPath).Path
    if ([IO.Path]::GetExtension($source).ToLowerInvariant() -eq '.svg') { throw 'SVG input is not supported.' }
    runNativeCommand -FilePath 'magick' -ArgumentList @($source, '-resize', '256x256', $wizardSmallPng) -Name 'ImageMagick wizard small resize'
    runNativeCommand -FilePath 'magick' -ArgumentList @($source, '-background', 'black', '-gravity', 'center', '-resize', '240x459', '-extent', '240x459', $wizardLargePng) -Name 'ImageMagick wizard large resize'
}

function prepareInstallerImages {
    param([string]$InputPath = $iconPng)
    resizePng -InputPath $InputPath -Sizes $pngInstallerSizes
    writeWizardImages -InputPath $InputPath
}

function ensureInstallerImages {
    if (-not (Test-Path -LiteralPath $iconPng)) { throw "Missing source icon: $iconPng" }
    if (-not (Test-Path -LiteralPath $iconIco)) { convertToIco -InputPath $iconPng | Out-Null }
    if (-not (Test-Path -LiteralPath $wizardSmallPng) -or -not (Test-Path -LiteralPath $wizardLargePng)) {
        writeWizardImages -InputPath $iconPng
    }
}

function openUrl {
    param([Parameter(Mandatory)][string]$Url)
    if ([string]::IsNullOrWhiteSpace($Url)) { throw 'openUrl requires a URL.' }
    Start-Process $Url
}

function buildAssetName {
    param([Parameter(Mandatory)][ValidateSet('Installer', 'Portable')][string]$Kind, [Parameter(Mandatory)][string]$Architecture)
    $extension = if ($Kind -eq 'Installer') { 'exe' } else { 'zip' }
    "${projectName}_v${versionContents}_windows-${Architecture}.${extension}"
}

function buildAll {
    param([string]$Architecture)
    $previousPipeline = $env:BASE_BUILD_PIPELINE
    try {
        $env:BASE_BUILD_PIPELINE = '1'
        deleteDir $publishFolder
        $params = @{}; if ($Architecture) { $params.Architecture = $Architecture }
        runNativeCommand -FilePath "$PSScriptRoot\buildInstaller.ps1" -ArgumentList $params -Name 'buildInstaller.ps1'
        New-Item -ItemType Directory -Path $publishFolder -Force | Out-Null
        foreach ($target in (getBuildTargets $Architecture)) {
            Compress-Archive -Path "$($target.BinFolder)\*" -DestinationPath "$publishFolder\$(buildAssetName -Kind Portable -Architecture $target.Architecture)" -Force
            Copy-Item -LiteralPath "$installerOutput\$($target.InstallerName)" -Destination "$publishFolder\$(buildAssetName -Kind Installer -Architecture $target.Architecture)" -Force
        }
        runNativeCommand -FilePath "$PSScriptRoot\updateReadme.ps1" -ArgumentList @{} -Name 'updateReadme.ps1'
    } finally {
        $env:BASE_BUILD_PIPELINE = $previousPipeline
    }
}

Set-Location -LiteralPath $repoRoot
