#!/usr/bin/env sh
# Resets the persistent development database (Gateway:Dev:Persist). Stop the gateway first.
# The audit anchor must be removed together with the database, otherwise the next start reports an
# audit chain integrity violation (the anchor would point to a sequence the new database does not have).
set -eu
dir="${1:-.data}"
rm -f "$dir/dev.db" "$dir/dev.db-wal" "$dir/dev.db-shm" "$dir/dev.db.audit-anchor.json"
echo "Dev database in '$dir' reset."
