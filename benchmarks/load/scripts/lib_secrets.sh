#!/usr/bin/env bash
# Sourced by the benchmark scripts. Provides per-run random credentials instead of
# committed ones. Values come from the environment, else from the (untracked)
# .bench-secrets.env file in benchmarks/load, else they are generated and persisted there.
_LIB_BENCH_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
_SECRETS_FILE="${_LIB_BENCH_DIR}/.bench-secrets.env"

if [ -f "${_SECRETS_FILE}" ]; then
    # shellcheck disable=SC1090
    . "${_SECRETS_FILE}"
fi

if [ -z "${BENCH_DB_PASSWORD:-}" ] || [ -z "${BENCH_HASURA_ADMIN_SECRET:-}" ]; then
    command -v openssl >/dev/null 2>&1 || { echo "ERROR: openssl is required to generate benchmark credentials" >&2; exit 1; }
    BENCH_DB_PASSWORD="${BENCH_DB_PASSWORD:-$(openssl rand -hex 24)}"
    BENCH_HASURA_ADMIN_SECRET="${BENCH_HASURA_ADMIN_SECRET:-$(openssl rand -hex 24)}"
    ( umask 077; printf 'BENCH_DB_PASSWORD=%s\nBENCH_HASURA_ADMIN_SECRET=%s\n' \
        "${BENCH_DB_PASSWORD}" "${BENCH_HASURA_ADMIN_SECRET}" > "${_SECRETS_FILE}" )
fi
export BENCH_DB_PASSWORD BENCH_HASURA_ADMIN_SECRET
