#!/usr/bin/env bash
# SmartCompress launcher for Linux / macOS.
#
# HOW TO RUN:
#   chmod +x SmartCompress.sh
#   ./SmartCompress.sh

set -euo pipefail

REPO_URL="https://github.com/Mitzingdash/compression.git"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$SCRIPT_DIR/compression"
VENV_DIR="$REPO_DIR/.venv"
PYTHON="$VENV_DIR/bin/python"
PIP="$VENV_DIR/bin/pip"

ok()   { echo "  OK   $1"; }
step() { echo "  ...  $1"; }
abort() { echo ""; echo "  [ERROR] $1"; exit 1; }

install_hint_python() {
    echo "  Install Python 3.10+ with:"
    if   command -v apt     &>/dev/null; then echo "    sudo apt install python3 python3-venv python3-pip"
    elif command -v dnf     &>/dev/null; then echo "    sudo dnf install python3 python3-pip"
    elif command -v pacman  &>/dev/null; then echo "    sudo pacman -S python python-pip"
    elif command -v brew    &>/dev/null; then echo "    brew install python3"
    else                                      echo "    https://python.org"
    fi
}

install_hint_git() {
    echo "  Install Git with:"
    if   command -v apt     &>/dev/null; then echo "    sudo apt install git"
    elif command -v dnf     &>/dev/null; then echo "    sudo dnf install git"
    elif command -v pacman  &>/dev/null; then echo "    sudo pacman -S git"
    elif command -v brew    &>/dev/null; then echo "    brew install git"
    else                                      echo "    https://git-scm.com"
    fi
}

echo ""
echo "  ============================================"
echo "    SmartCompress Launcher"
echo "  ============================================"
echo ""

# ── Python ────────────────────────────────────────────────────────────────────
PY=""
if   command -v python3 &>/dev/null; then PY=python3
elif command -v python  &>/dev/null; then PY=python
fi

if [ -z "$PY" ]; then
    echo "  [!] Python not found."
    install_hint_python
    exit 1
fi

PYVER=$($PY -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')")
PYMAJ=$(echo "$PYVER" | cut -d. -f1)
PYMIN=$(echo "$PYVER" | cut -d. -f2)
if [ "$PYMAJ" -lt 3 ] || { [ "$PYMAJ" -eq 3 ] && [ "$PYMIN" -lt 10 ]; }; then
    echo "  [!] Python 3.10+ required (you have $PYVER)."
    install_hint_python
    exit 1
fi
ok "Python $PYVER"

# ── Git ───────────────────────────────────────────────────────────────────────
if ! command -v git &>/dev/null; then
    echo "  [!] Git not found."
    install_hint_git
    exit 1
fi
ok "Git"
echo ""

# ── Clone or update ───────────────────────────────────────────────────────────
if [ ! -d "$REPO_DIR/.git" ]; then
    step "First run - cloning SmartCompress..."
    git clone "$REPO_URL" "$REPO_DIR" --quiet
    ok "Cloned."
else
    step "Checking for updates..."
    cd "$REPO_DIR"
    git fetch origin main --quiet 2>/dev/null || true
    LOCAL=$(git rev-parse HEAD 2>/dev/null)
    REMOTE=$(git rev-parse origin/main 2>/dev/null)
    if [ "$LOCAL" != "$REMOTE" ]; then
        OLD_VER=$($PY -c "import json; print(json.load(open('config.json'))['version'])" 2>/dev/null || echo "?")
        git pull origin main --quiet 2>/dev/null
        NEW_VER=$($PY -c "import json; print(json.load(open('config.json'))['version'])" 2>/dev/null || echo "?")
        ok "Updated  v$OLD_VER -> v$NEW_VER"
    else
        VER=$($PY -c "import json; print(json.load(open('config.json'))['version'])" 2>/dev/null || echo "?")
        ok "Already up to date  (v$VER)"
    fi
    cd "$SCRIPT_DIR"
fi
echo ""

# ── Virtual environment ───────────────────────────────────────────────────────
if [ ! -d "$VENV_DIR" ]; then
    step "Creating virtual environment..."
    $PY -m venv "$VENV_DIR"
    ok "Virtual environment ready."
fi

# ── Dependencies ──────────────────────────────────────────────────────────────
step "Checking dependencies..."
"$PIP" install -r "$REPO_DIR/requirements.txt" --quiet --upgrade 2>/dev/null
ok "Dependencies ready."
echo ""

# ── Launch ────────────────────────────────────────────────────────────────────
"$PYTHON" "$REPO_DIR/compress.py"
