# Produce artifacts/RustTrafilatura.<version>.nupkg from the staged native libraries.
#
# Every RID in the package layout has to be staged before a package that claims to support it is
# built, and the README, the license files and the third-party notices have to be present, so a package is refused rather
# than published half-complete. Each staged library is also checked for the test-hooks exports, which
# exist only in a build of the test suite and must not reach a shipped artifact. Packing itself is
# delegated to the package project; this script decides what is packable and never pushes unless
# --push says so.

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
$PackageFiles = @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')
$Project = 'dotnet/src/RustTrafilatura/RustTrafilatura.csproj'
# The two exports the `test-hooks` feature adds to rust/src/lib.rs. The panicking one is an export a
# release build must not carry, and the counter beside it comes from the same feature, so the shipped
# library must hold neither.
$ForbiddenExports = @('rt_panic_for_test', 'rt_live_buffers_for_test')

function Fail {
    param([Parameter(Mandatory = $true)][string]$Message)
    $Host.UI.WriteErrorLine("$ScriptName`: $Message")
    exit 1
}

# The build stamp cannot answer this: the feature changes no source path and no dependency the digest
# covers, so a test-hooks library and a release library of the same tree carry the same stamp. The
# export names are searched for in the library file rather than read through a symbol-table tool,
# because the linker writes each export's name into the file on every platform and no such tool is
# installed on every host that runs this script. ISO-8859-1 maps every byte to one character, so the
# search sees the bytes the linker wrote.
function Assert-NoTestHooks {
    param(
        [Parameter(Mandatory = $true)][string]$Rid,
        [Parameter(Mandatory = $true)][string]$Library
    )
    $bytes = [System.IO.File]::ReadAllBytes($Library)
    $text = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)
    foreach ($export in $ForbiddenExports) {
        if ($text.Contains($export)) {
            Fail "the staged library for $Rid exports $export, so it was built with the test-hooks feature. That export must not exist in a shipped artifact: rebuild it with scripts/build.ps1 (or scripts/build.sh), which builds the default feature set."
        }
    }
}

function Show-Usage {
    @"
Usage: scripts/$ScriptName [--allow-partial] [--push | --push-only]

Packs artifacts/RustTrafilatura.<version>.nupkg from the native libraries staged under
dotnet/src/RustTrafilatura/runtimes/<rid>/native/, with the README, the license files and the third-party notices at the package root.
The library files are the package content: the build stamp that sits beside each one is build
evidence and is never packed.
files at the package root. The library files are the package content: the build stamp that
sits beside each one is build evidence and is never packed. A staged library that exports a
test-hooks symbol is refused, because that feature is built only for the test suite.

  --allow-partial  Pack the RIDs that are staged and report them, instead of refusing when one
                   of the five is missing. The RIDs that are present must still be complete.
                   Refused together with --push and --push-only: what it packs is not a release
                   package.
  --push           Publish the package with the NUGET_API_KEY environment variable. A push
                   that is already on the feed is skipped, not an error.
  --push-only      Publish the package already in artifacts/ and pack nothing, which is what the
                   tag-gated publish job uses so the bytes it publishes are the ones the package
                   job built and attested. Requires NUGET_API_KEY and that file, and requires
                   RELEASE_TAG to name this version (v<version>).
  -h, --help       Show this message.
"@ | Write-Host
}

# Parsed by hand instead of through param(): both shells must accept exactly the same arguments, and
# the `--flag` spelling has to work in the form the other shell documents.
$AllowPartial = $false
$Push = $false
$PushOnly = $false
$Help = $false
for ($i = 0; $i -lt $args.Count; $i++) {
    $argument = [string]$args[$i]
    if ($argument -notlike '-*') { Fail "unknown argument: $argument" }
    switch (($argument -replace '^--?', '').ToLowerInvariant()) {
        'allow-partial' { $AllowPartial = $true }
        'push' { $Push = $true }
        'push-only' { $PushOnly = $true }
        'h' { $Help = $true }
        'help' { $Help = $true }
        default { Fail "unknown argument: $argument" }
    }
}

if ($Help) {
    Show-Usage
    exit 0
}

if ($Push -and $PushOnly) {
    Fail '--push and --push-only are mutually exclusive: --push packs and pushes, --push-only pushes the package already in artifacts/.'
}

# A partial package is what this script itself calls "not a release package", so a push must not be
# able to carry one: publishing is the one step nothing later can undo.
if ($AllowPartial -and ($Push -or $PushOnly)) {
    Fail '--allow-partial cannot be combined with --push or --push-only: --allow-partial packs the RIDs that happen to be staged and is not a release package, so a push would publish an incomplete one. Stage all five RIDs and pack without --allow-partial.'
}

# Checked before anything is packed: a missing key discovered after a successful pack would leave a
# package behind that looks like the result of a release that never happened.
if (($Push -or $PushOnly) -and -not $env:NUGET_API_KEY) {
    Fail '--push and --push-only need the NUGET_API_KEY environment variable, which is not set. Nothing was packed or pushed.'
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot $Project
$ArtifactsDir = Join-Path $RepoRoot 'artifacts'

if (-not (Test-Path -PathType Leaf $ProjectPath)) { Fail "missing package project: $ProjectPath" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet not found on PATH' }

# The version is read from the project rather than repeated here, so the packed file and the
# assembly metadata cannot disagree.
$Version = ([xml](Get-Content -LiteralPath $ProjectPath -Raw)).Project.PropertyGroup.Version
if (-not $Version) { Fail "no <Version> in $ProjectPath" }
$Package = Join-Path $ArtifactsDir "RustTrafilatura.$Version.nupkg"

# The package is pushed as it was packed, and only its presence is checked: repacking here would
# publish bytes the `package` job's provenance attestation does not cover, and what the file contains
# was already asserted where it was packed.
if ($PushOnly) {
    if (-not (Test-Path -PathType Leaf $Package)) {
        Fail "no package to push: missing file $Package. --push-only publishes the package the package job built; pack one with scripts/release.ps1 first."
    }
    # The file name comes from the project's <Version> and nothing else ties it to the tag that
    # triggered this run, so the tag is checked here: a release tagged against the wrong commit would
    # otherwise publish whichever version the checkout happens to carry.
    if (-not $env:RELEASE_TAG) {
        Fail "--push-only needs the RELEASE_TAG environment variable, the tag this release is for, so a mistagged run cannot publish a version nobody tagged. It must be v$Version."
    }
    if ($env:RELEASE_TAG -ne "v$Version") {
        Fail "RELEASE_TAG is $($env:RELEASE_TAG) while the package is version $Version, so this run would publish a version the tag does not name. Tag the commit that carries $Version (v$Version) and re-run."
    }
    Write-Host "$ScriptName`: pushing   $Package"
    # --skip-duplicate makes a re-run of a release idempotent instead of a failure.
    dotnet nuget push $Package --api-key $env:NUGET_API_KEY --skip-duplicate
    if ($LASTEXITCODE -ne 0) { Fail "dotnet nuget push failed with exit code $LASTEXITCODE" }
    Write-Host "$ScriptName`: pushed    $Package"
    exit 0
}

# The package README and the license files have to be at the repository root, which is where the pack
# entries in the project read them from.
foreach ($name in $PackageFiles) {
    $path = Join-Path $RepoRoot $name
    if (-not (Test-Path -PathType Leaf $path)) {
        Fail "missing package file: $path. Every package must ship the README, the license files and the third-party notices."
    }
}

# The staged libraries, one property per RID, plus the marker that tells the project a selection was
# made. Anything not staged is reported below and left out of the package rather than staged here.
$RuntimeArguments = [System.Collections.Generic.List[string]]::new()
$RuntimeArguments.Add('-p:RuntimeRidsSelected=true')
$Missing = [System.Collections.Generic.List[string]]::new()
$Staged = [System.Collections.Generic.List[string]]::new()
foreach ($rid in $Rids) {
    $library = Join-Path $RepoRoot "dotnet/src/RustTrafilatura/runtimes/$rid/native/$($Libraries[$rid].File)"
    if (Test-Path -PathType Leaf $library) {
        Assert-NoTestHooks -Rid $rid -Library $library
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
