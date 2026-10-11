# Fetch the pages the C# benchmarks read: markusmobius/content-extractor-benchmark at a pinned commit,
# sparse-checked out to its files/ directory under dotnet/benchmarks/corpus/. Fetching that one commit
# with a blob filter downloads about 30 MB rather than the whole repository history and result files.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# PowerShell 7.4+ can turn a native command's stderr into a terminating error, and git writes its
# progress there.
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ScriptName = 'corpus.ps1'
$Commit = '6bb08131deacc9a32860605018f74991b076518b'
$Repository = 'https://github.com/markusmobius/content-extractor-benchmark.git'
# What that commit holds under files/, read from the repository's own tree. The byte total is the
# blobs' own size, which is why the checkout below turns line-ending conversion off: a converted
# checkout is 892 KB larger and would look like a different corpus.
$ExpectedPages = 1057
$ExpectedBytes = 151009417

function Fail {
    param([Parameter(Mandatory = $true)][string]$Message)
    $Host.UI.WriteErrorLine("$ScriptName`: $Message")
    exit 1
}

function Show-Usage {
    @"
Usage: scripts/$ScriptName [--check]

Fetches the benchmark corpus into dotnet/benchmarks/corpus/$Commit`: that commit of
$Repository, checked out sparsely to its files/ directory, about 30 MB
and under a minute. A second run finds the corpus in place and fetches nothing.

  --check      Verify the corpus instead of fetching it, and exit non-zero when it is
               missing or does not match the pin.
  -h, --help   Show this message.
"@ | Write-Host
}

function Get-PageCount {
    param([Parameter(Mandatory = $true)][string]$Directory)
    if (-not (Test-Path -PathType Container "$Directory/files")) { return 0 }
    return @(Get-ChildItem -File -Path "$Directory/files" -Filter *.html).Count
}

function Get-ByteCount {
    param([Parameter(Mandatory = $true)][string]$Directory)
    if (-not (Test-Path -PathType Container "$Directory/files")) { return 0 }
    $total = (Get-ChildItem -File -Path "$Directory/files" -Filter *.html |
        Measure-Object -Property Length -Sum).Sum
    if ($null -eq $total) { return 0 }
    return [long]$total
}

# Both halves of the pin, plus the commit when the cache still carries its own git directory.
function Test-Corpus {
    param([Parameter(Mandatory = $true)][string]$Directory)
    if ((Get-PageCount -Directory $Directory) -ne $ExpectedPages) { return $false }
    if ((Get-ByteCount -Directory $Directory) -ne $ExpectedBytes) { return $false }
    if (Test-Path -PathType Container "$Directory/.git") {
        $head = (& git -C $Directory rev-parse HEAD 2>$null | Select-Object -First 1)
        # Case-sensitive, unlike PowerShell's default comparison.
        if ($null -eq $head -or $head.Trim() -cne $Commit) { return $false }
    }
    return $true
}

# Parsed by hand instead of through param(): a param() block rejects `-h` in the
# `& .\scripts\corpus.ps1 -h` form, and both shells must accept exactly the same arguments.
$Check = $false
for ($i = 0; $i -lt $args.Count; $i++) {
    $argument = [string]$args[$i]
    if ($argument -notlike '-*') { Fail "unknown argument: $argument" }
    switch (($argument -replace '^--?', '').ToLowerInvariant()) {
        'h' { Show-Usage; exit 0 }
        'help' { Show-Usage; exit 0 }
        'check' { $Check = $true }
        default { Fail "unknown argument: $argument" }
    }
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Manifest = Join-Path $RepoRoot 'rust/Cargo.toml'
$Cache = Join-Path $RepoRoot "dotnet/benchmarks/corpus/$Commit"

if (-not (Test-Path -PathType Leaf $Manifest)) {
    Fail "missing manifest: $Manifest (this script has to run from its repository)"
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'git not found on PATH' }

if ($Check) {
    if (-not (Test-Corpus -Directory $Cache)) {
        Fail "the corpus in $Cache is missing or does not match the pinned commit $Commit; run scripts/$ScriptName"
    }
    Write-Host "$ScriptName`: corpus ok   $Cache"
    exit 0
}

if (Test-Corpus -Directory $Cache) {
    Write-Host "$ScriptName`: corpus already fetched   $Cache"
    exit 0
}

Write-Host "$ScriptName`: fetching $Commit"
if (Test-Path $Cache) { Remove-Item -Recurse -Force $Cache }
New-Item -ItemType Directory -Force -Path $Cache | Out-Null
& git -C $Cache init --quiet
if ($LASTEXITCODE -ne 0) { Fail "git init failed with exit code $LASTEXITCODE" }
& git -C $Cache remote add origin $Repository
if ($LASTEXITCODE -ne 0) { Fail "git remote add failed with exit code $LASTEXITCODE" }
# The commit is the pin: fetching it by hash means a moved branch cannot change what is measured.
& git -C $Cache fetch --quiet --depth 1 --filter=blob:none origin $Commit
if ($LASTEXITCODE -ne 0) { Fail "fetching $Commit from $Repository failed with exit code $LASTEXITCODE" }
& git -C $Cache config core.autocrlf false
if ($LASTEXITCODE -ne 0) { Fail "git config failed with exit code $LASTEXITCODE" }
& git -C $Cache config core.eol lf
if ($LASTEXITCODE -ne 0) { Fail "git config failed with exit code $LASTEXITCODE" }
& git -C $Cache sparse-checkout set files
if ($LASTEXITCODE -ne 0) { Fail "git sparse-checkout failed with exit code $LASTEXITCODE" }
& git -C $Cache checkout --quiet FETCH_HEAD
if ($LASTEXITCODE -ne 0) { Fail "checking out the fetched commit failed with exit code $LASTEXITCODE" }

if (-not (Test-Corpus -Directory $Cache)) {
    Fail "the fetched corpus does not match the pin ($ExpectedPages pages, $ExpectedBytes bytes): $Cache"
}

Write-Host "$ScriptName`: corpus $ExpectedPages pages, $ExpectedBytes bytes   $Cache"
