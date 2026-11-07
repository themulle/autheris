#!/usr/bin/env bash
set -euo pipefail

BASE="${1:-origin/main}"
PR_LABELS="${PR_LABELS:-}"

if ! git rev-parse --verify "$BASE" >/dev/null 2>&1; then
    if git rev-parse --verify origin/main >/dev/null 2>&1; then
        BASE="origin/main"
    elif git rev-parse --verify main >/dev/null 2>&1; then
        BASE="main"
    fi
fi

net_src_lines=$(git diff --numstat "$BASE"...HEAD -- src \
    ':(exclude)src/**/Migrations/**' \
    ':(exclude)**/*.g.cs' \
    ':(exclude)**/*.Designer.cs' \
    ':(exclude)**/Generated/**' | awk '
    $1 != "-" && $2 != "-" {
        added += $1
        deleted += $2
    }
    END {
        print (added - deleted) + 0
    }
')

echo "net_src_lines=${net_src_lines}"

if [ "${net_src_lines}" -gt 400 ]; then
    if [[ "${PR_LABELS}" =~ (^|,)(size-exception|security|freeze-exception)(,|$) ]]; then
        echo "PR net src lines (${net_src_lines}) exceeds 400, but waived by PR label in '${PR_LABELS}'."
        exit 0
    else
        echo "Error: PR net src lines (${net_src_lines}) exceeds 400 line ceiling." >&2
        echo "Add 'size-exception', 'security', or 'freeze-exception' label if justified." >&2
        exit 1
    fi
fi
