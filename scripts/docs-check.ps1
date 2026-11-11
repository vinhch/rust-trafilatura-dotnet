# Run scripts/docs-check.sh with bash and exit with its code. On Windows the bash is Git for Windows' bin/bash.exe,
# found from the git executable: a bare bash can resolve to WSL's System32\bash.exe.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptName = 'docs-check.ps1'

# Parsed by hand instead of through param(): both shells must accept exactly the same arguments.
if ($args.Count -gt 0) {
    $argument = [string]$args[0]
    if ($argument -in @('-h', '--help')) {
        Write-Host "Usage: scripts/$ScriptName [-h | --help]: no arguments; prints one line per broken pointer and exits 1 if there are any."
        exit 0
    }
    $Host.UI.WriteErrorLine("$ScriptName`: unknown argument: $argument")
    exit 1
}

$Script = Join-Path $PSScriptRoot 'docs-check.sh'

if ($IsWindows) {
    # Each git.exe's directory and its ancestors are searched for bin\bash.exe: git.exe sits in cmd\ or
    # mingw64\bin\ of the same install, and both lead to its root.
    $gitPaths = @(Get-Command git -CommandType Application -All | ForEach-Object { $_.Source })
    if ($gitPaths.Count -eq 0) { throw "$ScriptName`: git not found on PATH, so Git for Windows bash.exe cannot be located" }
    $bash = $null
    foreach ($gitPath in $gitPaths) {
        $dir = Split-Path -Parent $gitPath
        while ($dir -and -not $bash) {
            $candidate = Join-Path $dir 'bin\bash.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $bash = $candidate }
            $dir = Split-Path -Parent $dir
        }
        if ($bash) { break }
    }
    if (-not $bash) {
        throw "$ScriptName`: Git for Windows bash.exe not found: expected <Git>\bin\bash.exe, searched upward from $($gitPaths -join ', ')"
    }
}
else {
    $bash = (Get-Command bash -CommandType Application).Source
}

& $bash $Script
exit $LASTEXITCODE
