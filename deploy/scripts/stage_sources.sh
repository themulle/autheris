#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

SOURCE_CANDIDATES=(
    "${ROOT_DIR}/../gql"
    "/root/gql"
    "${ROOT_DIR}/gql"
)

TARGET_DIR="${ROOT_DIR}/src_build"

if [ -d "${TARGET_DIR}/src" ]; then
    echo "[stage_sources] Source code is already staged at ${TARGET_DIR}."
    exit 0
fi

SOURCE_FOUND=""
for candidate in "${SOURCE_CANDIDATES[@]}"; do
    if [ -d "${candidate}/src" ]; then
        SOURCE_FOUND="${candidate}"
        break
    fi
done

if [ -z "${SOURCE_FOUND}" ]; then
    echo "[stage_sources] ERROR: Could not locate Autheris source repository." >&2
    exit 1
fi

echo "[stage_sources] Staging source code from ${SOURCE_FOUND} to ${TARGET_DIR}..."
mkdir -p "${TARGET_DIR}"

# Copy projects excluding bin and obj
rsync -av --exclude="bin" --exclude="obj" --exclude=".git" --exclude=".vs" \
    "${SOURCE_FOUND}/src" \
    "${SOURCE_FOUND}/Directory.Build.props" \
    "${SOURCE_FOUND}/Autheris.sln" \
    "${TARGET_DIR}/" || {
    # Fallback to tar/cp if rsync is not available
    mkdir -p "${TARGET_DIR}/src"
    cp "${SOURCE_FOUND}/Directory.Build.props" "${TARGET_DIR}/"
    cp "${SOURCE_FOUND}/Autheris.sln" "${TARGET_DIR}/"
    cp -r "${SOURCE_FOUND}/src" "${TARGET_DIR}/"
    find "${TARGET_DIR}" -type d \( -name "bin" -o -name "obj" \) -exec rm -rf {} + 2>/dev/null || true
}

EXT_CANDIDATES=(
    "${ROOT_DIR}/../gql_extensions"
    "/root/gql_extensions"
    "${ROOT_DIR}/gql_extensions"
)
EXT_FOUND=""
for candidate in "${EXT_CANDIDATES[@]}"; do
    if [ -d "${candidate}/src" ]; then
        EXT_FOUND="${candidate}"
        break
    fi
done

if [ -n "${EXT_FOUND}" ]; then
    echo "[stage_sources] Staging GqlGateway.Extensions from ${EXT_FOUND} to ${TARGET_DIR}/gql_extensions..."
    mkdir -p "${TARGET_DIR}/gql_extensions"
    rsync -av --exclude="bin" --exclude="obj" --exclude=".git" --exclude=".vs" \
        "${EXT_FOUND}/src" \
        "${EXT_FOUND}/Directory.Build.props" \
        "${TARGET_DIR}/gql_extensions/" || {
        mkdir -p "${TARGET_DIR}/gql_extensions/src"
        cp "${EXT_FOUND}/Directory.Build.props" "${TARGET_DIR}/gql_extensions/"
        cp -r "${EXT_FOUND}/src" "${TARGET_DIR}/gql_extensions/"
        find "${TARGET_DIR}/gql_extensions" -type d \( -name "bin" -o -name "obj" \) -exec rm -rf {} + 2>/dev/null || true
    }
fi

echo "[stage_sources] Successfully staged source code."
