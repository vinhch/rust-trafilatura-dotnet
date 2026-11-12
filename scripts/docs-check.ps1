# Run scripts/docs-check.sh with bash and exit with its code. On Windows the bash is Git for Windows' bin/bash.exe,
# found from `git --exec-path` of each git on PATH: a bare bash can resolve to WSL's System32\bash.exe.

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
    # Git for Windows' root is three levels above `git --exec-path` (<root>\<mingw64|ucrt64|clangarm64>\libexec\git-core),
    # so a shim or a launcher in cmd\ leads to the same root. A git that fails or prints nothing is skipped, and so is
    # one whose root holds no bin\bash.exe (MSYS2 and Cygwin report /usr/lib/git-core).
    $gitPaths = @(Get-Command git -CommandType Application -All | ForEach-Object { $_.Source })
    if ($gitPaths.Count -eq 0) { throw "$ScriptName`: git not found on PATH, so Git for Windows bash.exe cannot be located" }
    $bash = $null
    $tried = @()
    foreach ($gitPath in $gitPaths) {
        $candidate = $null
        $LASTEXITCODE = 0
        try {
            # All output is captured: stopping the pipeline at the first line would skip the exit code.
            $execPath = @(& $gitPath --exec-path 2>$null) | Select-Object -First 1
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($execPath)) { throw 'no exec path' }
            $root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $execPath))
            $candidate = Join-Path $root 'bin\bash.exe'
            $tried += "$gitPath -> $execPath"
        }
        catch {
            $tried += "$gitPath -> failed"
            continue
        }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $bash = $candidate
            break
        }
    }
    if (-not $bash) {
        throw "$ScriptName`: Git for Windows bash.exe not found: expected <Git>\bin\bash.exe above git --exec-path, tried $($tried -join '; ')"
    }
}
else {
    $bash = (Get-Command bash -CommandType Application).Source
}

& $bash $Script
exit $LASTEXITCODE
