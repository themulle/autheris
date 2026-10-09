#!/usr/bin/env bash
# ==============================================================================
# dep12-clean-git-history.sh
# Runner for DEP-12 git history cleanup tool
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PYTHON_BIN="${PYTHON:-python3}"

if ! command -v "$PYTHON_BIN" >/dev/null 2>&1; then
    PYTHON_BIN="python"
fi

if ! command -v "$PYTHON_BIN" >/dev/null 2>&1; then
    echo "ERROR: python3 or python not found." >&2
    exit 1
fi

exec "$PYTHON_BIN" "${SCRIPT_DIR}/dep12-clean-git-history.py" "$@"
