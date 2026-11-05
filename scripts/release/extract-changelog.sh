#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 1 ]; then
  echo "Usage: $0 <version>" >&2
  exit 1
fi

VERSION="${1#v}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CHANGELOG="$REPO_ROOT/CHANGELOG.md"

if [ ! -f "$CHANGELOG" ]; then
  echo "Error: CHANGELOG.md not found at $CHANGELOG" >&2
  exit 1
fi

CONTENT=$(awk -v ver="$VERSION" '
  $0 ~ "^## \\[" ver "\\]" { found=1; print; next }
  found && /^## \[/ { exit }
  found { print }
' "$CHANGELOG")

if [ -z "$(echo "$CONTENT" | tr -d '[:space:]')" ]; then
  echo "Error: Release notes for version $VERSION not found in $CHANGELOG" >&2
  exit 1
fi

BODY=$(echo "$CONTENT" | grep -v "^## \[$VERSION\]" | tr -d '[:space:]')
if [ -z "$BODY" ]; then
  echo "Error: Release notes for version $VERSION are empty in $CHANGELOG" >&2
  exit 1
fi

echo "$CONTENT"
