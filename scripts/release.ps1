# Produce artifacts/RustTrafilatura.<version>.nupkg from the staged native libraries.
#
# Every RID in the package layout has to be staged before a package that claims to support it is
# built, and the README and the three license files have to be present, so a package is refused rather
# than published half-complete. Packing itself is delegated to the package project; this script
# decides what is packable and never pushes unless --push says so.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# PowerShell 7.4+ can turn a native command's stderr into a terminating error, and dotnet writes
# progress there.
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ScriptName = 'release.ps1'
$Rids = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
# The library file each RID stages and the property name that puts it in the package. The mapping
# lives here because this script is what decides which RIDs are packable, and the project reads one
# boolean per RID rather than a list: a semicolon-separated RID list cannot cross a command line as
# one MSBuild property value.
$Libraries = @{
    'win-x64'     = @{ File = 'rust_trafilatura_ffi.dll'; Property = 'RuntimeRid_win_x64' }
    'linux-x64'   = @{ File = 'librust_trafilatura_ffi.so'; Property = 'RuntimeRid_linux_x64' }
    'linux-arm64' = @{ File = 'librust_trafilatura_ffi.so'; Property = 'RuntimeRid_linux_arm64' }
    'osx-x64'     = @{ File = 'librust_trafilatura_ffi.dylib'; Property = 'RuntimeRid_osx_x64' }
    'osx-arm64'   = @{ File = 'librust_trafilatura_ffi.dylib'; Property = 'RuntimeRid_osx_arm64' }
}
$PackageFiles = @('README.md', 'LICENSE', 'LICENSE-GO.txt', 'LICENSE-READABILITY-ENCODING')
$Project = 'dotnet/src/RustTrafilatura/RustTrafilatura.csproj'

function Fail {
    param([Parameter(Mandatory = $true)][string]$Message)
    $Host.UI.WriteErrorLine("$ScriptName`: $Message")
    exit 1
}

function Show-Usage {
    @"
Usage: scripts/$ScriptName [--allow-partial] [--push]

Packs artifacts/RustTrafilatura.<version>.nupkg from the native libraries staged under
dotnet/src/RustTrafilatura/runtimes/<rid>/native/, with the README and the three license
files at the package root. The library files are the package content: the build stamp that
sits beside each one is build evidence and is never packed.

  --allow-partial  Pack the RIDs that are staged and report them, instead of refusing when one
                   of the five is missing. The RIDs that are present must still be complete.
  --push           Publish the package with the NUGET_API_KEY environment variable. A push
                   that is already on the feed is skipped, not an error.
  -h, --help       Show this message.
"@ | Write-Host
}

# Parsed by hand instead of through param(): both shells must accept exactly the same arguments, and
# the `--flag` spelling has to work in the form the other shell documents.
$AllowPartial = $false
$Push = $false
$Help = $false
for ($i = 0; $i -lt $args.Count; $i++) {
    $argument = [string]$args[$i]
    if ($argument -notlike '-*') { Fail "unknown argument: $argument" }
    switch (($argument -replace '^--?', '').ToLowerInvariant()) {
        'allow-partial' { $AllowPartial = $true }
        'push' { $Push = $true }
        'h' { $Help = $true }
        'help' { $Help = $true }
        default { Fail "unknown argument: $argument" }
    }
}

if ($Help) {
    Show-Usage
    exit 0
}

# Checked before anything is packed: a missing key discovered after a successful pack would leave a
# package behind that looks like the result of a release that never happened.
if ($Push -and -not $env:NUGET_API_KEY) {
    Fail '--push needs the NUGET_API_KEY environment variable, which is not set. Nothing was packed or pushed.'
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot $Project
$ArtifactsDir = Join-Path $RepoRoot 'artifacts'

if (-not (Test-Path -PathType Leaf $ProjectPath)) { Fail "missing package project: $ProjectPath" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet not found on PATH' }

# The package README and the license files have to be at the repository root, which is where the pack
# entries in the project read them from.
foreach ($name in $PackageFiles) {
    $path = Join-Path $RepoRoot $name
    if (-not (Test-Path -PathType Leaf $path)) {
        Fail "missing package file: $path. Every package must ship the README and the three license files."
    }
}

# The version is read from the project rather than repeated here, so the packed file and the
# assembly metadata cannot disagree.
$Version = ([xml](Get-Content -LiteralPath $ProjectPath -Raw)).Project.PropertyGroup.Version
if (-not $Version) { Fail "no <Version> in $ProjectPath" }
$Package = Join-Path $ArtifactsDir "RustTrafilatura.$Version.nupkg"

# The staged libraries, one property per RID, plus the marker that tells the project a selection was
# made. Anything not staged is reported below and left out of the package rather than staged here.
$RuntimeArguments = [System.Collections.Generic.List[string]]::new()
$RuntimeArguments.Add('-p:RuntimeRidsSelected=true')
$Missing = [System.Collections.Generic.List[string]]::new()
$Staged = [System.Collections.Generic.List[string]]::new()
foreach ($rid in $Rids) {
    $library = Join-Path $RepoRoot "dotnet/src/RustTrafilatura/runtimes/$rid/native/$($Libraries[$rid].File)"
    if (Test-Path -PathType Leaf $library) {
        $Staged.Add($rid)
        $RuntimeArguments.Add("-p:$($Libraries[$rid].Property)=true")
    }
    else {
        $Missing.Add("$rid ($library)")
    }
}

if ($Missing.Count -gt 0) {
    $message = "the native library is not staged for $($Missing.Count) of $($Rids.Count) RIDs: $($Missing -join '; '). " +
    'Stage the missing RIDs with scripts/build.ps1 --rid <rid> (or scripts/build.sh). ' +
    'A local pack can pack what is staged with --allow-partial, which is not a release package.'
    if (-not $AllowPartial) { Fail $message }
    $Host.UI.WriteErrorLine("$ScriptName`: warning: $message")
}

if ($Staged.Count -eq 0) { Fail 'no native library is staged for any RID, so the package would contain no library at all. Stage one with scripts/build.ps1 or scripts/build.sh.' }

Write-Host "$ScriptName`: version   $Version"
Write-Host "$ScriptName`: packed    $($Staged.Count) of $($Rids.Count) RIDs: $($Staged -join ', ')"
if ($Missing.Count -gt 0) { Write-Host "$ScriptName`: omitted   $($Missing -join ', ')" }

# A stale package from an earlier pack would be indistinguishable from this one, and `dotnet pack`
# overwrites in place, so the path it is about to write is removed first.
if (Test-Path -PathType Leaf $Package) { Remove-Item -LiteralPath $Package -Force }

dotnet pack $ProjectPath -c Release -o $ArtifactsDir @RuntimeArguments
if ($LASTEXITCODE -ne 0) { Fail "dotnet pack failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -PathType Leaf $Package)) { Fail "dotnet pack reported success but wrote no package: $Package" }

$Size = (Get-Item -LiteralPath $Package).Length
Write-Host "$ScriptName`: package   $Package ($Size bytes)"

# The package-content tests read what was just written, so a package missing a RID, a license file,
# the README, an asset or carrying a build stamp fails here rather than in a consumer's install. The
# default suite excludes them because a checkout that has never packed has nothing for them to read.
$TestsProject = Join-Path $RepoRoot 'dotnet/tests/RustTrafilatura.Tests/RustTrafilatura.Tests.csproj'
if (-not (Test-Path -PathType Leaf $TestsProject)) { Fail "missing test project: $TestsProject" }
Write-Host "$ScriptName`: checking  $TestsProject (Package category)"
dotnet test $TestsProject -c Release --filter 'Category=Package'
if ($LASTEXITCODE -ne 0) { Fail "the package-content tests failed with exit code $LASTEXITCODE, so $Package is not a package this repository will ship" }

if ($Push) {
    # --skip-duplicate makes a re-run of a release idempotent instead of a failure.
    dotnet nuget push $Package --api-key $env:NUGET_API_KEY --skip-duplicate
    if ($LASTEXITCODE -ne 0) { Fail "dotnet nuget push failed with exit code $LASTEXITCODE" }
    Write-Host "$ScriptName`: pushed    $Package"
}
