# Set or check the version the crate and the package carry.
#
# Cargo requires the crate version as a literal in rust/Cargo.toml and MSBuild takes the package
# version from the package project, so the number lives in two files, and cargo writes a third copy
# into rust/Cargo.lock for the crate itself. --set writes the one version it is given into all three;
# --check proves they agree, and that a tag names the same version. Neither commits and neither tags:
# the tag is the release act, so it stays a deliberate one.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# PowerShell 7.4+ can turn a native command's stderr into a terminating error, and cargo writes
# progress there.
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ScriptName = 'version.ps1'
$Manifest = 'rust/Cargo.toml'
$Lockfile = 'rust/Cargo.lock'
$Project = 'dotnet/src/RustTrafilatura/RustTrafilatura.csproj'
$Crate = 'rust-trafilatura-ffi'
# SemVer 2.0.0 without build metadata. NuGet does not carry build metadata in a package identity while
# cargo accepts it, so a version the two ecosystems read differently cannot be the one number the two
# manifests are checked against. The pre-release part is semver.org's own rule, leading zeros included:
# 1.0.0-rc.1 and 1.0.0-rc.1.2 are versions, 1.0.0- and 1.0.0+build.5 are not.
$VersionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?$'

function Fail {
    param([Parameter(Mandatory = $true)][string]$Message)
    $Host.UI.WriteErrorLine("$ScriptName`: $Message")
    exit 1
}

function Show-Usage {
    @"
Usage: scripts/$ScriptName --set <version> | --check [--tag v<version>]

The version the crate and the package carry lives in two files:
  $Manifest, the literal cargo requires
  $Project, the one MSBuild packs from
cargo writes a third copy into $Lockfile. Both modes read all three.

  --set <version>  Write <version> into the manifest and the project, let cargo rewrite the
                   lockfile entry, then check that the three agree. Refuses a version that is not
                   <major>.<minor>.<patch>[-<prerelease>], and refuses to write into a file that
                   has uncommitted changes. It does not commit and does not tag.
  --check          Report the version the three files carry, and fail unless they agree and it is
                   <major>.<minor>.<patch>[-<prerelease>].
  --tag v<version> With --check: fail unless this tag names exactly that version, which is what a
                   release tag has to name.
  -h, --help       Show this message.
"@
}

# The `version = ` line cargo reads. Anchored, because `rust-version = "1.98.1"` carries the same
# words and is not the crate version.
function Get-ManifestVersion {
    $match = [regex]::Match([System.IO.File]::ReadAllText($Manifest), '(?m)^version = "(?<version>[^"]*)"$')
    if ($match.Success) { $match.Groups['version'].Value } else { '' }
}

function Get-ProjectVersion {
    $match = [regex]::Match([System.IO.File]::ReadAllText($Project), '<Version>(?<version>[^<]*)</Version>')
    if ($match.Success) { $match.Groups['version'].Value } else { '' }
}

# The crate is one [[package]] block among the dependency's, so the block is located by name and the
# version is read from it: the first `version = ` line in the file belongs to whichever dependency
# sorts first, not to the crate.
function Get-LockVersion {
    $inside = $false
    foreach ($line in [System.IO.File]::ReadAllLines($Lockfile)) {
        if ($line -match '^name = "(?<name>[^"]*)"$') {
            $inside = $Matches['name'] -eq $Crate
            continue
        }
        if ($inside -and $line -match '^version = "(?<version>[^"]*)"$') {
            return $Matches['version']
        }
    }
    return ''
}

function Check-Version {
    param([string]$Tag = '')

    $version = Get-ManifestVersion
    $lock = Get-LockVersion
    $package = Get-ProjectVersion

    if (-not $version) { Fail "no version line in $Manifest" }
    if (-not $lock) { Fail "no [[package]] block for $Crate in $Lockfile" }
    if (-not $package) { Fail "no <Version> element in $Project" }

    if ($version -notmatch $VersionPattern) { Fail "$version in $Manifest is not <major>.<minor>.<patch>[-<prerelease>]" }

    if ($version -ne $package) {
        Fail "$Manifest carries $version while $Project carries $package. The native library reports the crate version and the package is named after the project's, so the same number has to be in both: set it with --set."
    }

    if ($version -ne $lock) {
        Fail "$Manifest carries $version while $Lockfile records $lock for $Crate. Run a cargo command in rust/ that is not --locked to rewrite the entry, then check again."
    }

    if ($Tag -and $Tag -ne "v$version") {
        Fail "the tag is $Tag while the sources carry $version, so this run would release $version under $Tag. Tag v$version, or set the version the tag names with --set."
    }

    $Host.UI.WriteLine("$ScriptName`: $version in $Manifest, $Lockfile and $Project")
    if ($Tag) { $Host.UI.WriteLine("$ScriptName`: $Tag names it") }
}

function Set-Version {
    param([Parameter(Mandatory = $true)][string]$NewVersion)

    if ($NewVersion -notmatch $VersionPattern) {
        Fail "$NewVersion is not <major>.<minor>.<patch>[-<prerelease>], which is the shape a release tag names (v$NewVersion)"
    }

    # The check is against HEAD rather than against the index: a staged edit is as uncommitted as an
    # unstaged one, and a rewrite laid over it would be indistinguishable from it in the diff, so one of
    # the two would be lost. The lockfile is named because --set rewrites it as well.
    & git diff --quiet HEAD -- $Manifest $Lockfile $Project
    if ($LASTEXITCODE -ne 0) {
        Fail "$Manifest, $Lockfile or $Project has uncommitted changes, staged or not. Commit or stash them first, so this rewrite is the only change in the diff."
    }

    $manifestText = [System.IO.File]::ReadAllText($Manifest)
    $projectText = [System.IO.File]::ReadAllText($Project)
    $manifestPattern = '(?m)^version = "[^"]*"$'
    $projectPattern = '<Version>[^<]*</Version>'

    if ([regex]::Matches($manifestText, $manifestPattern).Count -ne 1) {
        Fail "$Manifest does not carry exactly one version line this script can rewrite, so which one is the crate's is not a question it can answer"
    }
    if ([regex]::Matches($projectText, $projectPattern).Count -ne 1) {
        Fail "$Project does not carry exactly one <Version> element this script can rewrite, so which one the package uses is not a question it can answer"
    }

    $previous = Get-ManifestVersion
    if ($previous -eq $NewVersion) { Fail "$Manifest already carries $NewVersion" }

    # BOM-less UTF-8, and the newlines come from the text that was read: both files are LF in the
    # repository and stay that way, whichever shell rewrote them. Both rewrites are built and checked
    # before either is written, so a pattern that matches nothing cannot leave the pair disagreeing in
    # the tree: it fails here, with both targets untouched.
    $updatedManifest = [regex]::Replace($manifestText, $manifestPattern, 'version = "' + $NewVersion + '"')
    $updatedProject = [regex]::Replace($projectText, $projectPattern, '<Version>' + $NewVersion + '</Version>')
    if (-not [regex]::IsMatch($updatedManifest, '(?m)^version = "' + [regex]::Escape($NewVersion) + '"$')) {
        Fail "$Manifest has no version line to rewrite, so nothing was changed"
    }
    if (-not $updatedProject.Contains('<Version>' + $NewVersion + '</Version>')) {
        Fail "$Project has no <Version> element to rewrite, so nothing was changed"
    }

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Manifest, $updatedManifest, $encoding)
    [System.IO.File]::WriteAllText($Project, $updatedProject, $encoding)

    if ((Get-ManifestVersion) -ne $NewVersion) { Fail "rewriting $Manifest did not produce $NewVersion" }
    if ((Get-ProjectVersion) -ne $NewVersion) { Fail "rewriting $Project did not produce $NewVersion" }

    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
        Fail "$Manifest carries $NewVersion now but cargo is not on PATH, so $Lockfile still records $previous. Run a cargo command in rust/ without --locked, then re-run --check."
    }

    # cargo writes the crate's own lockfile entry as part of a resolution rather than as a command of
    # its own, and --locked afterwards is what proves the entry it wrote matches the manifest.
    Push-Location rust
    try {
        & cargo metadata --format-version 1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "cargo could not resolve rust/, so $Lockfile was not rewritten" }
        & cargo metadata --locked --format-version 1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "cargo resolved rust/ but $Lockfile still disagrees with $Manifest" }
    }
    finally {
        Pop-Location
    }

    Check-Version

    $Host.UI.WriteLine("$ScriptName`: the staged native library is stale now, because the crate version is part of rust/ and the build stamp covers it: rebuild it with scripts/build.ps1")
    $Host.UI.WriteLine("$ScriptName`: commit $Manifest, $Lockfile and $Project, then tag v$NewVersion")
}

$Mode = ''
$Tag = ''
$NewVersion = ''

foreach ($Argument in $args) {
    switch (($Argument -replace '^--?', '').ToLowerInvariant()) {
        'set' {
            if ($Mode -eq 'check') { Fail '--set and --check are separate runs' }
            $Mode = 'set'
            # The version is the argument after --set rather than a switch value, so it is taken from
            # the position the loop has reached.
            $NewVersion = 'pending'
        }
        'check' {
            if ($Mode -eq 'set') { Fail '--set and --check are separate runs' }
            $Mode = 'check'
        }
        'tag' { $Tag = 'pending' }
        'h' { Show-Usage; exit 0 }
        'help' { Show-Usage; exit 0 }
        default {
            if ($NewVersion -eq 'pending') { $NewVersion = $Argument }
            elseif ($Tag -eq 'pending') { $Tag = $Argument }
            else { Fail "unknown argument: $Argument" }
        }
    }
}

if ($NewVersion -eq 'pending') { Fail '--set needs a <version>' }
if ($Tag -eq 'pending') { Fail '--tag needs a v<version>' }

if (-not $Mode) { Fail 'nothing to do: pass --set <version> or --check' }
if ($Tag -and $Mode -ne 'check') { Fail '--tag applies to --check' }
# --set takes its version positionally, so a second one, or one beside --check, is a mistyped
# argument rather than a value to use.
if ($Mode -eq 'check' -and $NewVersion) { Fail "unknown argument: $NewVersion" }

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

if (-not (Test-Path -PathType Leaf $Manifest)) { Fail "missing manifest: $Manifest" }
if (-not (Test-Path -PathType Leaf $Lockfile)) { Fail "missing lockfile: $Lockfile" }
if (-not (Test-Path -PathType Leaf $Project)) { Fail "missing package project: $Project" }

if ($Mode -eq 'set') {
    Set-Version -NewVersion $NewVersion
}
else {
    Check-Version -Tag $Tag
}
