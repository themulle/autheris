#!/usr/bin/env bash
# Issue #388 (I388-SC-02): released governance schema artefacts are append-only against the target branch.
#   - migration steps (Persistence/Migrations/Steps/**), the three *BaselineSchema.cs files and the v0001 shape snapshots
#     may be added but never modified, deleted or renamed;
#   - the goldens (Golden/GovernanceBaseline/*, Golden/GovernanceSteps/*) may only grow: the base content must stay a byte prefix.
# Usage: check-governance-immutability.sh <base-ref> [head-ref]   (exit 0 = ok, 1 = violation, 2 = usage or git error)
set -euo pipefail

base_ref="${1:-}"
head_ref="${2:-HEAD}"
if [[ -z "$base_ref" ]]; then
  echo "usage: $0 <base-ref> [head-ref]" >&2
  exit 2
fi

merge_base="$(git merge-base "$base_ref" "$head_ref")" || { echo "cannot compute the merge base of $base_ref and $head_ref" >&2; exit 2; }

immutable=(
  'src/Autheris.Infrastructure/Persistence/Migrations/Steps/'
  'src/Autheris.Infrastructure/Persistence/Migrations/SqliteBaselineSchema.cs'
  'src/Autheris.Infrastructure/Persistence/Migrations/PostgreSqlBaselineSchema.cs'
  'src/Autheris.Infrastructure/Persistence/Migrations/SqlServerBaselineSchema.cs'
  'src/Autheris.Infrastructure/Persistence/Migrations/Shape/governance-shape.sqlite.v0001.json'
  'src/Autheris.Infrastructure/Persistence/Migrations/Shape/governance-shape.postgresql.v0001.json'
  'src/Autheris.Infrastructure/Persistence/Migrations/Shape/governance-shape.sqlserver.v0001.json'
)
append_only=(
  'tests/Autheris.Tests.Architecture/Golden/GovernanceBaseline/'
  'tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/'
)

status=0

# WP-SIMPL-11: the SQLite governance store is removed on purpose (SEC-SIMPL-05). Its artefacts may be deleted; nothing else is exempt.
sqlite_removed='/Migrations/Steps/sqlite/|/Migrations/SqliteBaselineSchema\.cs$|/Migrations/Shape/governance-shape\.sqlite\.|/Golden/GovernanceSteps/checksums\.sqlite\.txt$'

while IFS=$'\t' read -r change path; do
  [[ -z "${path:-}" ]] && continue
  if [[ "$change" == "D" && "$path" =~ $sqlite_removed ]]; then
    continue
  fi
  if [[ "$change" != "A" ]]; then
    echo "VIOLATION: released governance artefact '$path' was changed ($change); add a new step instead." >&2
    status=1
  fi
done < <(git diff --no-renames --name-status "$merge_base" "$head_ref" -- "${immutable[@]}")

while IFS=$'\t' read -r change path; do
  [[ -z "${path:-}" ]] && continue
  case "$change" in
    A) ;;
    D)
      if [[ ! "$path" =~ $sqlite_removed ]]; then
        echo "VIOLATION: golden '$path' was deleted or renamed ($change)." >&2
        status=1
      fi
      ;;
    M)
      base_file="$(mktemp)"; head_file="$(mktemp)"
      git show "$merge_base:$path" > "$base_file"
      git show "$head_ref:$path" > "$head_file"
      if [[ "$path" == */Golden/GovernanceBaseline/v1.5.0.sha256 ]]; then
        # the lines of the removed SQLite governance artefacts may disappear; every other released line must stay a byte prefix
        grep -vi 'sqlite' "$base_file" > "$base_file.f" || true
        mv "$base_file.f" "$base_file"
      fi
      size="$(wc -c < "$base_file")"
      if ! cmp -s -n "$size" "$base_file" "$head_file"; then
        echo "VIOLATION: golden '$path' modifies or removes released lines (append-only)." >&2
        status=1
      fi
      rm -f "$base_file" "$head_file"
      ;;
    *)
      echo "VIOLATION: golden '$path' was deleted or renamed ($change)." >&2
      status=1
      ;;
  esac
done < <(git diff --no-renames --name-status "$merge_base" "$head_ref" -- "${append_only[@]}")

exit "$status"
