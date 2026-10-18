$ErrorActionPreference = 'Stop'

function Complete-Pair($python, $pythonw) {
    if ([string]::IsNullOrWhiteSpace($python) -and [string]::IsNullOrWhiteSpace($pythonw)) {
        throw 'ZHULONG_PYTHON or ZHULONG_PYTHONW is required when using an override.'
    }
    if ([string]::IsNullOrWhiteSpace($python)) {
        $python = Join-Path (Split-Path -Path $pythonw -Parent) 'python.exe'
    }
    if ([string]::IsNullOrWhiteSpace($pythonw)) {
        $pythonw = Join-Path (Split-Path -Path $python -Parent) 'pythonw.exe'
    }
    if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
        throw "Python executable not found: $python"
    }
    if (-not (Test-Path -LiteralPath $pythonw -PathType Leaf)) {
        throw "Pythonw executable not found: $pythonw"
    }
    $python = (Resolve-Path -LiteralPath $python).Path
    $pythonw = (Resolve-Path -LiteralPath $pythonw).Path
    if ((Split-Path -Path $python -Parent) -ine (Split-Path -Path $pythonw -Parent)) {
        throw "python.exe and pythonw.exe must be in the same directory: $python ; $pythonw"
    }
    $versionText = & $python -X utf8 -c "import sys; print('%s.%s' % sys.version_info[:2])" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $versionText) {
        throw "Failed to execute Python: $python"
    }
    $version = [version]$versionText
    if ($version -lt [version]'3.13') {
        throw "Python 3.13+ is required, but $python is $version"
    }
    return "PY=$python`nPYW=$pythonw"
}

if ($env:ZHULONG_PYTHON -or $env:ZHULONG_PYTHONW) {
    Complete-Pair $env:ZHULONG_PYTHON $env:ZHULONG_PYTHONW
    exit 0
}

$candidates = @()
$commands = Get-Command -Name 'python.exe' -All -ErrorAction SilentlyContinue
foreach ($command in $commands) {
    if ($command.Source) { $candidates += $command.Source }
}
if ($env:LOCALAPPDATA) {
    $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe')
}
if ($env:ProgramFiles) {
    $candidates += (Join-Path $env:ProgramFiles 'Python313\python.exe')
}

$seen = @{}
foreach ($candidate in $candidates) {
    $key = $candidate.ToLowerInvariant()
    if ($seen.ContainsKey($key)) { continue }
    $seen[$key] = $true
    try {
        Complete-Pair $candidate $null
        exit 0
    } catch {
        # Try the next installation; WindowsApps aliases may not be real interpreters.
    }
}

throw 'Python 3.13+ with python.exe and pythonw.exe was not found. Set ZHULONG_PYTHON or ZHULONG_PYTHONW explicitly.'
