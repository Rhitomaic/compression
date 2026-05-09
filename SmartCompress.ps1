#Requires -Version 5.1
# SmartCompress launcher for Windows.
#
# HOW TO RUN:
#   First time: double-click init.bat  (unblocks this file, then deletes itself)
#   After that: right-click this file -> "Run with PowerShell"

$REPO_URL = "https://github.com/Mitzingdash/compression.git"
$ROOT     = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
$REPO_DIR = Join-Path $ROOT "compression"
$VENV_DIR = Join-Path $REPO_DIR ".venv"
$PYTHON   = Join-Path $VENV_DIR "Scripts\python.exe"
$PIP      = Join-Path $VENV_DIR "Scripts\pip.exe"

function Write-OK   { param($m) Write-Host "  OK   $m" -ForegroundColor Green }
function Write-Step { param($m) Write-Host "  ...  $m" }
function Write-Warn { param($m) Write-Host "  [!]  $m" -ForegroundColor Yellow }
function Write-Err  { param($m) Write-Host "`n  [ERROR] $m" -ForegroundColor Red; throw $m }

function Refresh-Path {
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("Path","User")
}

function Install-Python {
    if (Get-Command choco -ErrorAction SilentlyContinue) {
        Write-Step "Installing Python via Chocolatey..."
        choco install python --yes --no-progress
        return
    }
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Step "Installing Python via winget..."
        winget install Python.Python.3.12 --silent --accept-package-agreements --accept-source-agreements
        return
    }
    Write-Step "Downloading Python 3.12 installer..."
    $url = "https://www.python.org/ftp/python/3.12.7/python-3.12.7-amd64.exe"
    $tmp = Join-Path $env:TEMP "sc-python-installer.exe"
    (New-Object System.Net.WebClient).DownloadFile($url, $tmp)
    Write-Step "Installing Python (this may take a moment)..."
    Start-Process $tmp -ArgumentList @("/quiet", "InstallAllUsers=0", "PrependPath=1", "Include_launcher=0") -Wait
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
}

function Install-Git {
    if (Get-Command choco -ErrorAction SilentlyContinue) {
        Write-Step "Installing Git via Chocolatey..."
        choco install git --yes --no-progress
        return
    }
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Step "Installing Git via winget..."
        winget install Git.Git --silent --accept-package-agreements --accept-source-agreements
        return
    }
    Write-Step "Finding latest Git release..."
    try {
        $rel   = Invoke-RestMethod "https://api.github.com/repos/git-for-windows/git/releases/latest"
        $asset = $rel.assets | Where-Object { $_.name -like "*64-bit.exe" } | Select-Object -First 1
        $url   = $asset.browser_download_url
    } catch {
        Write-Err "Could not find Git installer. Install manually from https://git-scm.com"
    }
    $tmp = Join-Path $env:TEMP "sc-git-installer.exe"
    Write-Step "Downloading Git installer..."
    (New-Object System.Net.WebClient).DownloadFile($url, $tmp)
    Write-Step "Installing Git (this may take a moment)..."
    Start-Process $tmp -ArgumentList @("/VERYSILENT", "/NORESTART", "/NOCANCEL", "/SP-") -Wait
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
}

try {

Write-Host ""
Write-Host "  ============================================" -ForegroundColor DarkCyan
Write-Host "    SmartCompress Launcher" -ForegroundColor Cyan
Write-Host "  ============================================" -ForegroundColor DarkCyan
Write-Host ""

# ── Python ────────────────────────────────────────────────────────────────────
# Skip Microsoft Store stubs - they open the Store instead of running Python.
$PY = $null
foreach ($cmd in @("python", "python3")) {
    $c = Get-Command $cmd -ErrorAction SilentlyContinue
    if ($c -and $c.Source -notlike "*WindowsApps*") { $PY = $c; break }
}

if (-not $PY) {
    Write-Warn "Python not found."
    $ans = Read-Host "  Install Python automatically? [Y/n]"
    if ($ans -eq "" -or $ans -match "^[Yy]") {
        Install-Python
        Refresh-Path
        foreach ($cmd in @("python", "python3")) {
            $c = Get-Command $cmd -ErrorAction SilentlyContinue
            if ($c -and $c.Source -notlike "*WindowsApps*") { $PY = $c; break }
        }
        if (-not $PY) {
            Write-Warn "Installed - PATH needs a refresh. Close this window and run again."
            throw "NEEDS_RESTART"
        }
        Write-OK "Python installed."
    } else {
        Write-Err "Python 3.10+ required. Install from https://python.org (tick 'Add Python to PATH')."
    }
}

$pyver = & $PY.Source -c "import sys; print(str(sys.version_info.major) + '.' + str(sys.version_info.minor))"
if (-not $pyver -or $pyver -notmatch '^\d+\.\d+') {
    Write-Err "Could not read Python version. Reinstall Python from https://python.org (tick 'Add Python to PATH')."
}
if ([version]$pyver -lt [version]"3.10") {
    Write-Err "Python 3.10+ required (you have $pyver). Get a newer version from https://python.org"
}
Write-OK "Python $pyver"

# ── Git ───────────────────────────────────────────────────────────────────────
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Warn "Git not found."
    $ans = Read-Host "  Install Git automatically? [Y/n]"
    if ($ans -eq "" -or $ans -match "^[Yy]") {
        Install-Git
        Refresh-Path
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
            Write-Warn "Installed - PATH needs a refresh. Close this window and run again."
            throw "NEEDS_RESTART"
        }
        Write-OK "Git installed."
    } else {
        Write-Err "Git required. Install from https://git-scm.com"
    }
}
Write-OK "Git"
Write-Host ""

# ── Clone or update ───────────────────────────────────────────────────────────
if (-not (Test-Path (Join-Path $REPO_DIR ".git"))) {
    Write-Step "First run - cloning SmartCompress..."
    git clone $REPO_URL $REPO_DIR --quiet
    if ($LASTEXITCODE -ne 0) { Write-Err "Clone failed. Check your internet connection." }
    Write-OK "Cloned."
} else {
    Write-Step "Checking for updates..."
    Push-Location $REPO_DIR
    git fetch origin main --quiet 2>$null
    $local_hash  = git rev-parse HEAD 2>$null
    $remote_hash = git rev-parse origin/main 2>$null
    if ($local_hash -ne $remote_hash) {
        $old_ver = "?"
        try { $old_ver = (Get-Content "config.json" -Raw | ConvertFrom-Json).version } catch {}
        git pull origin main --quiet 2>$null
        $new_ver = "?"
        try { $new_ver = (Get-Content "config.json" -Raw | ConvertFrom-Json).version } catch {}
        Write-OK "Updated  v$old_ver -> v$new_ver"
    } else {
        $ver = "?"
        try { $ver = (Get-Content "config.json" -Raw | ConvertFrom-Json).version } catch {}
        Write-OK "Already up to date  (v$ver)"
    }
    Pop-Location
}
Write-Host ""

# ── Virtual environment ───────────────────────────────────────────────────────
if (-not (Test-Path $VENV_DIR)) {
    Write-Step "Creating virtual environment..."
    & $PY.Source -m venv $VENV_DIR
    if ($LASTEXITCODE -ne 0) { Write-Err "Failed to create virtual environment." }
    Write-OK "Virtual environment ready."
}

# ── Dependencies ──────────────────────────────────────────────────────────────
Write-Step "Checking dependencies..."
& $PIP install -r (Join-Path $REPO_DIR "requirements.txt") --upgrade
if ($LASTEXITCODE -ne 0) { Write-Err "Failed to install dependencies - see errors above." }
Write-OK "Dependencies ready."
Write-Host ""

# ── Launch ────────────────────────────────────────────────────────────────────
$env:SC_OUT_DIR = $ROOT
& $PYTHON (Join-Path $REPO_DIR "compress.py")

} catch {
    if ("$_" -ne "NEEDS_RESTART") {
        Write-Host ""
        Write-Host "  Something went wrong - see the error above." -ForegroundColor Red
    }
} finally {
    Write-Host ""
    Read-Host "  Press Enter to exit"
}
