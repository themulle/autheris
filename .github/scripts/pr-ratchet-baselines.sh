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

BASELINES_DIR="tests/Autheris.Tests.Architecture/Baselines"
FILES=(
    "${BASELINES_DIR}/endpoint-count.txt"
    "${BASELINES_DIR}/options-class-count.txt"
    "${BASELINES_DIR}/enabled-flag-count.txt"
    "${BASELINES_DIR}/application-folders.txt"
    "${BASELINES_DIR}/plan-id-comment-counts.txt"
)

raises=()

for file in "${FILES[@]}"; do
    if [ ! -f "$file" ]; then
        continue
    fi

    # If the file does not exist in the base ref, skip raise comparison (baseline initialization)
    if ! git cat-file -e "${BASE}:${file}" 2>/dev/null; then
        continue
    fi

    filename="$(basename "$file")"

    case "$filename" in
        "endpoint-count.txt"|"options-class-count.txt"|"enabled-flag-count.txt")
            base_val=$(git show "${BASE}:${file}" | grep -E '^total\|' | head -n1 | cut -d'|' -f2 || true)
            curr_val=$(grep -E '^total\|' "$file" | head -n1 | cut -d'|' -f2 || true)
            if [ -n "$base_val" ] && [ -n "$curr_val" ]; then
                if [ "$curr_val" -gt "$base_val" ]; then
                    raises+=("${filename} raised from ${base_val} to ${curr_val}")
                fi
            fi
            ;;
        "application-folders.txt")
            base_folders=$(git show "${BASE}:${file}" | grep -v '^[[:space:]]*#' | grep -v '^[[:space:]]*$' | sort -u || true)
            curr_folders=$(grep -v '^[[:space:]]*#' "$file" | grep -v '^[[:space:]]*$' | sort -u || true)
            added_folders=$(comm -13 <(echo "$base_folders") <(echo "$curr_folders") | grep -v '^[[:space:]]*$' || true)
            if [ -n "$added_folders" ]; then
                while IFS= read -r f; do
                    [ -n "$f" ] && raises+=("${filename} added folder ${f}")
                done <<< "$added_folders"
            fi
            ;;
        "plan-id-comment-counts.txt")
            declare -A base_map=()
            while IFS='|' read -r proj count; do
                [[ -z "$proj" || "$proj" =~ ^[[:space:]]*# ]] && continue
                proj=$(echo "$proj" | xargs)
                count=$(echo "$count" | xargs)
                base_map["$proj"]="$count"
            done < <(git show "${BASE}:${file}" 2>/dev/null || true)

            while IFS='|' read -r proj count; do
                [[ -z "$proj" || "$proj" =~ ^[[:space:]]*# ]] && continue
                proj=$(echo "$proj" | xargs)
                count=$(echo "$count" | xargs)
                base_count="${base_map[$proj]:-}"
                if [ -n "$base_count" ]; then
                    if [ "$count" -gt "$base_count" ]; then
                        raises+=("${filename} ${proj} raised from ${base_count} to ${count}")
                    fi
                else
                    if [ "$count" -gt 0 ]; then
                        raises+=("${filename} ${proj} new count ${count}")
                    fi
                fi
            done < "$file"
            ;;
    esac
done

if [ ${#raises[@]} -eq 0 ]; then
    ratchet_raises=""
else
    ratchet_raises=$(IFS=, ; echo "${raises[*]}")
fi

echo "ratchet_raises=${ratchet_raises}"

if [ ${#raises[@]} -gt 0 ]; then
    if [[ "${PR_LABELS}" =~ (^|,)(freeze-exception)(,|$) ]]; then
        echo "Ratchet baseline raise waived by 'freeze-exception' label in PR_LABELS (${PR_LABELS})."
        exit 0
    else
        echo "Error: Ratchet baseline(s) raised without 'freeze-exception' label: ${ratchet_raises}" >&2
        echo "Raising ratchet baselines requires label 'freeze-exception'." >&2
        exit 1
    fi
fi
