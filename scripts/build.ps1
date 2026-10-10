# Build the native library for one RID and copy it to
# dotnet/src/RustTrafilatura/runtimes/<rid>/native/.
#
# The copy is accompanied by a build stamp that ties the artifact to the rust/ tree and the toolchain
# it was built from. --check verifies a stamped artifact instead of building one, which is how
# scripts/test.ps1 refuses to run the suite against a stale or placeholder library.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# PowerShell 7.4+ can turn a native command's stderr into a terminating error, and
# cargo writes its progress there.
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ScriptName = 'build.ps1'
$Rids = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
$Triples = @{
    'win-x64'     = 'x86_64-pc-windows-msvc'
    'linux-x64'   = 'x86_64-unknown-linux-gnu'
    'linux-arm64' = 'aarch64-unknown-linux-gnu'
    'osx-x64'     = 'x86_64-apple-darwin'
    'osx-arm64'   = 'aarch64-apple-darwin'
}
$StampName = 'build-stamp.txt'

# Bumped when the stamp's fields or the digest's construction change, so an old stamp is rejected
# with a message about the format rather than one about a mismatched hash.
$StampFormat = '1'

function Fail {
    param([Parameter(Mandatory = $true)][string]$Message)
    $Host.UI.WriteErrorLine("$ScriptName`: $Message")
    exit 1
}

function Show-Usage {
    $ridList = $Rids -join ', '
    @"
Usage: scripts/$ScriptName [--rid <rid>] [--check]

Builds rust-trafilatura-ffi for one RID and copies the shared library to
dotnet/src/RustTrafilatura/runtimes/<rid>/native/, beside a build stamp that
records the rust/ tree and the toolchain the library came from.

  --rid <rid>  Target RID ($ridList). Defaults to the host RID.
  --check      Verify the staged library against its stamp and exit; build nothing.
  -h, --help   Show this message.
"@ | Write-Host
}

function Get-HostRid {
    if ($env:OS -eq 'Windows_NT') {
        # MSYS-style shells report the shell's own architecture, which stays x86_64
        # under emulation, so prefer the native architecture when Windows supplies it.
        $arch = $env:PROCESSOR_ARCHITECTURE
        if ($env:PROCESSOR_ARCHITEW6432) { $arch = $env:PROCESSOR_ARCHITEW6432 }
        if ($arch -eq 'ARM64') { return 'win-arm64' }
        return 'win-x64'
    }

    $osName = (& uname -s).Trim()
    if ($LASTEXITCODE -ne 0) { Fail "uname failed with exit code $LASTEXITCODE" }
    if ($osName -eq 'Darwin') { $os = 'osx' }
    elseif ($osName -eq 'Linux') { $os = 'linux' }
    else { Fail "unsupported host OS: $osName" }

    $machine = (& uname -m).Trim()
    if ($LASTEXITCODE -ne 0) { Fail "uname failed with exit code $LASTEXITCODE" }
    if ($machine -in @('x86_64', 'amd64')) { $arch = 'x64' }
    elseif ($machine -in @('arm64', 'aarch64')) { $arch = 'arm64' }
    else { Fail "unsupported host architecture: $machine" }

    return "$os-$arch"
}

# Parsed by hand instead of through param(): a param() block rejects `--rid` in the
# `& .\scripts\build.ps1 --rid <rid>` form, which is the spelling build.sh documents.
# Accepts --rid and the PowerShell-native -Rid, plus -h/--help; anything else is an error.
$Rid = ''
$Help = $false
$Check = $false
for ($i = 0; $i -lt $args.Count; $i++) {
    $argument = [string]$args[$i]
    if ($argument -notlike '-*') { Fail "unknown argument: $argument" }
    switch (($argument -replace '^--?', '').ToLowerInvariant()) {
        'rid' {
            if ($i + 1 -ge $args.Count) { Fail '--rid requires a value' }
            if ($Rid) { Fail '--rid given more than once' }
            $Rid = [string]$args[$i + 1]
            if (-not $Rid) { Fail '--rid requires a non-empty value' }
            $i++
        }
        'check' { $Check = $true }
        'h' { $Help = $true }
        'help' { $Help = $true }
        default { Fail "unknown argument: $argument" }
    }
}

if ($Help) {
    Show-Usage
    exit 0
}

if (-not $Rid) { $Rid = Get-HostRid }
# Case-sensitive, unlike PowerShell's default comparison: the RID becomes a directory
# name under runtimes/, and the two shells must accept exactly the same five strings.
if ($Rids -cnotcontains $Rid) {
    Fail "unsupported RID: $Rid (expected one of: $($Rids -join ', '))"
}
$Triple = $Triples[$Rid]
$Library = 'librust_trafilatura_ffi.so'
if ($Rid -like 'win-*') { $Library = 'rust_trafilatura_ffi.dll' }
elseif ($Rid -like 'osx-*') { $Library = 'librust_trafilatura_ffi.dylib' }

$RepoRoot = Split-Path -Parent $PSScriptRoot
$RustDir = Join-Path $RepoRoot 'rust'
$Manifest = Join-Path $RustDir 'Cargo.toml'

if (-not (Test-Path -PathType Container $RustDir)) { Fail "missing directory: $RustDir" }
if (-not (Test-Path -PathType Leaf $Manifest)) { Fail "missing manifest: $Manifest" }
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { Fail 'cargo not found on PATH' }
if (-not (Get-Command rustc -ErrorAction SilentlyContinue)) { Fail 'rustc not found on PATH' }

# Cargo resolves a relative CARGO_TARGET_DIR against the working directory, which
# this script never changes, so the same value locates the artifact here.
$TargetDir = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $RustDir 'target' }
$Artifact = Join-Path (Join-Path (Join-Path $TargetDir $Triple) 'release') $Library
$DestDir = Join-Path $RepoRoot "dotnet/src/RustTrafilatura/runtimes/$Rid/native"
$DestLibrary = Join-Path $DestDir $Library
$StampPath = Join-Path $DestDir $StampName

# The toolchain is part of the stamp: an artifact built by a different rustc is not the artifact the
# current tree would produce.
$Toolchain = (& rustc --version)
if ($LASTEXITCODE -ne 0) { Fail "rustc --version failed with exit code $LASTEXITCODE" }
$Toolchain = ([string]$Toolchain).Trim()

# ---------------------------------------------------------------------------------------------
# Build stamp
#
# The stamp is what makes "the staged library corresponds to the current sources" a checkable
# statement. A modification time cannot carry it: a fresh clone gives every file the same time, so
# an artifact staged before a source edit is indistinguishable from one staged after. The digest
# therefore covers the paths and contents of the files the cdylib is built from, Cargo.toml,
# Cargo.lock and the toolchain version.
#
# The construction here is duplicated in scripts/build.sh and has to stay byte-identical: the two
# shells write interchangeable stamps, so scripts/test.ps1 and scripts/test.sh each verify whichever
# build script wrote the one on disk.
# ---------------------------------------------------------------------------------------------

function Get-FileHashHex {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-TextHashHex {
    param([Parameter(Mandatory = $true)][string]$Text)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

# The files the cdylib is built from, as slash-separated paths relative to rust/, in ordinal order
# (which is byte order for the ASCII paths of this tree). tests/, benches/ and examples/ are left out
# because none of them is linked into the cdylib, so editing one must not invalidate the stamp and
# force a rebuild; target/ is build output.
function Get-SourcePaths {
    $paths = [System.Collections.Generic.List[string]]::new()
    Get-ChildItem -LiteralPath $RustDir -Recurse -File -Filter '*.rs' | ForEach-Object {
        $relative = $_.FullName.Substring($RustDir.Length + 1).Replace('\', '/')
        if ($relative -notlike 'target/*' -and $relative -notlike 'tests/*' -and
            $relative -notlike 'benches/*' -and $relative -notlike 'examples/*') {
            $paths.Add($relative)
        }
    }
    $paths.Add('Cargo.toml')
    $paths.Add('Cargo.lock')

    $sorted = $paths.ToArray()
    [Array]::Sort($sorted, [System.StringComparer]::Ordinal)
    return $sorted
}

# One digest over the toolchain version and every source file's path and content hash. The toolchain
# string is inside the digest rather than stored beside it, so an artifact built by a different
# rustc cannot verify. The lines are joined with LF explicitly: the same bytes are what
# scripts/build.sh hashes, whatever platform either script runs on.
function Get-SourcesHash {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("toolchain $Toolchain")
    foreach ($relative in Get-SourcePaths) {
        $lines.Add("$relative $(Get-FileHashHex -Path (Join-Path $RustDir $relative))")
    }
    return Get-TextHashHex -Text (([string]::Join("`n", $lines)) + "`n")
}

function Get-StampField {
    param([Parameter(Mandatory = $true)][string]$Name)
    foreach ($line in [System.IO.File]::ReadAllLines($StampPath)) {
        if ($line.StartsWith("$Name=", [System.StringComparison]::Ordinal)) {
            return $line.Substring($Name.Length + 1)
        }
    }
    return ''
}

function Write-Stamp {
    $content = [string]::Join("`n", @(
            "stamp=$StampFormat",
            "rid=$Rid",
            "toolchain=$Toolchain",
            "sources=$(Get-SourcesHash)",
            "artifact=$(Get-FileHashHex -Path $DestLibrary)"
        )) + "`n"
    # Written explicitly as BOM-less UTF-8 with LF endings, because scripts/build.sh reads the same
    # file with sed and the digest it verifies has to be the same bytes.
    [System.IO.File]::WriteAllText($StampPath, $content, [System.Text.UTF8Encoding]::new($false))
}

function Assert-Stamp {
    $stampFormatValue = Get-StampField -Name 'stamp'
    if ($stampFormatValue -ne $StampFormat) {
        Fail "the build stamp $StampPath is not format $StampFormat (or has no stamp= line). Rewrite it with scripts/build.ps1 (or scripts/build.sh)."
    }

    $stampRid = Get-StampField -Name 'rid'
    if ($stampRid -cne $Rid) {
        Fail "the build stamp $StampPath records rid $stampRid, but this is the $Rid directory. Stage $Rid with scripts/build.ps1 --rid $Rid (or scripts/build.sh --rid $Rid)."
    }

    $stampSources = Get-StampField -Name 'sources'
    $currentSources = Get-SourcesHash
    if ($stampSources -cne $currentSources) {
        Fail "the staged library is stale: the build stamp $StampPath records sources $stampSources, the current rust/ tree hashes to $currentSources. The library was built before the sources, Cargo.lock or the toolchain changed. Rebuild it with scripts/build.ps1 (or scripts/build.sh)."
    }

    $stampArtifact = Get-StampField -Name 'artifact'
    $currentArtifact = Get-FileHashHex -Path $DestLibrary
    if ($stampArtifact -cne $currentArtifact) {
        Fail "the staged library does not match its build stamp: $DestLibrary hashes to $currentArtifact, the stamp records $stampArtifact, so the file is not the one the build produced - typically a placeholder, a partial copy or a different build. Rebuild it with scripts/build.ps1 (or scripts/build.sh)."
    }

    Write-Host "$ScriptName`: stamp ok      $StampPath"
    Write-Host "$ScriptName`: toolchain     $(Get-StampField -Name 'toolchain')"
    Write-Host "$ScriptName`: sources       $currentSources"
    Write-Host "$ScriptName`: artifact      $currentArtifact"
}

if ($Check) {
    Write-Host "$ScriptName`: checking $Rid ($Triple)"
    if (-not (Test-Path -PathType Container $DestDir)) {
        Fail "the native library is not staged for $Rid`: missing directory $DestDir. Stage it with scripts/build.ps1 (or scripts/build.sh)."
    }
    if (-not (Test-Path -PathType Leaf $DestLibrary)) {
        Fail "the native library is not staged for $Rid`: missing file $DestLibrary. Stage it with scripts/build.ps1 (or scripts/build.sh)."
    }
    if (-not (Test-Path -PathType Leaf $StampPath)) {
        Fail "no build stamp beside the staged library: missing file $StampPath. Nothing records which rust/ tree $DestLibrary came from, so a stale or placeholder copy would pass unnoticed. Rebuild and stamp it with scripts/build.ps1 (or scripts/build.sh)."
    }
    Assert-Stamp
    exit 0
}

Write-Host "$ScriptName`: building $Rid ($Triple)"
Write-Host "$ScriptName`: toolchain     $Toolchain"
& cargo build --locked --release --manifest-path $Manifest --target $Triple
if ($LASTEXITCODE -ne 0) { Fail "cargo build failed with exit code $LASTEXITCODE" }

if (-not (Test-Path -PathType Leaf $Artifact)) { Fail "expected artifact not found: $Artifact" }

New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
Copy-Item -Path $Artifact -Destination $DestLibrary -Force
Write-Stamp

Write-Host "$ScriptName`: source      $Artifact"
Write-Host "$ScriptName`: destination $DestLibrary"
Write-Host "$ScriptName`: stamp       $StampPath"
