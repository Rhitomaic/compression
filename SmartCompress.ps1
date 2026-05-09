#Requires -Version 5.1
# SmartCompress launcher for Windows.
#
# HOW TO RUN:
#   Right-click this file -> "Run with PowerShell"
#
# If Windows says it's blocked, run this once in PowerShell then try again:
#   Set-ExecutionPolicy -Scope CurrentUser RemoteSigned

$REPO_URL = "https://github.com/Mitzingdash/compression.git"
$ROOT     = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
$REPO_DIR = Join-Path $ROOT "compression"
$VENV_DIR = Join-Path $REPO_DIR ".venv"
$PYTHON   = Join-Path $VENV_DIR "Scripts\python.exe"
$PIP      = Join-Path $VENV_DIR "Scripts\pip.exe"

function Write-OK   { param($m) Write-Host "  OK   $m" -ForegroundColor Green }
function Write-Step { param($m) Write-Host "  ...  $m" }
function Write-Warn { param($m) Write-Host "  [!]  $m" -ForegroundColor Yellow }
function Write-Abort {
    param($m)
    Write-Host "`n  [ERROR] $m" -ForegroundColor Red
    Read-Host "`n  Press Enter to exit"
    exit 1
}
function Refresh-Path {
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("Path","User")
}

Write-Host ""
Write-Host "  ============================================" -ForegroundColor DarkCyan
Write-Host "    SmartCompress Launcher" -ForegroundColor Cyan
Write-Host "  ============================================" -ForegroundColor DarkCyan
Write-Host ""

# ── Python ────────────────────────────────────────────────────────────────────
$PY = Get-Command python -ErrorAction SilentlyContinue
if (-not $PY) { $PY = Get-Command python3 -ErrorAction SilentlyContinue }

if (-not $PY) {
    Write-Warn "Python not found."
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        $ans = Read-Host "  Install Python automatically? [Y/n]"
        if ($ans -eq "" -or $ans -match "^[Yy]") {
            Write-Step "Installing Python 3.12 via winget..."
            winget install Python.Python.3.12 --silent --accept-package-agreements --accept-source-agreements
            Refresh-Path
            $PY = Get-Command python -ErrorAction SilentlyContinue
            if (-not $PY) {
                Write-Warn "Installed - but PATH needs a refresh. Close this window and run the launcher again."
                Read-Host "`n  Press Enter to exit"
                exit 0
            }
            Write-OK "Python installed."
        } else {
            Write-Abort "Python 3.10+ required. Install from https://python.org (tick 'Add Python to PATH')."
        }
    } else {
        Write-Abort "Python not found. Install Python 3.10+ from https://python.org (tick 'Add Python to PATH')."
    }
}

$pyver = & $PY.Source -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')" 2>$null
if ([version]$pyver -lt [version]"3.10") {
    Write-Abort "Python 3.10+ required (you have $pyver). Get it from https://python.org"
}
Write-OK "Python $pyver"

# ── Git ───────────────────────────────────────────────────────────────────────
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Warn "Git not found."
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        $ans = Read-Host "  Install Git automatically? [Y/n]"
        if ($ans -eq "" -or $ans -match "^[Yy]") {
            Write-Step "Installing Git via winget..."
            winget install Git.Git --silent --accept-package-agreements --accept-source-agreements
            Refresh-Path
            if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
                Write-Warn "Installed - but PATH needs a refresh. Close this window and run the launcher again."
                Read-Host "`n  Press Enter to exit"
                exit 0
            }
            Write-OK "Git installed."
        } else {
            Write-Abort "Git required. Install from https://git-scm.com"
        }
    } else {
        Write-Abort "Git not found. Install from https://git-scm.com"
    }
}
Write-OK "Git"
Write-Host ""

# ── Clone or update ───────────────────────────────────────────────────────────
if (-not (Test-Path (Join-Path $REPO_DIR ".git"))) {
    Write-Step "First run - cloning SmartCompress..."
    git clone $REPO_URL $REPO_DIR --quiet
    if ($LASTEXITCODE -ne 0) { Write-Abort "Clone failed. Check your internet connection." }
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
    if ($LASTEXITCODE -ne 0) { Write-Abort "Failed to create virtual environment." }
    Write-OK "Virtual environment ready."
}

# ── Dependencies ──────────────────────────────────────────────────────────────
Write-Step "Checking dependencies..."
& $PIP install -r (Join-Path $REPO_DIR "requirements.txt") --quiet --upgrade 2>$null
Write-OK "Dependencies ready."
Write-Host ""

# ── Launch ────────────────────────────────────────────────────────────────────
& $PYTHON (Join-Path $REPO_DIR "compress.py")

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Read-Host "  Press Enter to exit"
}
