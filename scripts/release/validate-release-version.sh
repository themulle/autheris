#!/usr/bin/env bash
set -euo pipefail

# Core comparison function
compare_versions() {
  local tag="$1"
  local raw_dll_version="$2"
  local chart_version="$3"
  local chart_app_version="$4"
  local rendered_image_tag="$5"
  local image_ref="${6:-}"
  local check_docker="${7:-true}"

  local expected_version="${tag#v}"
  local dll_version="${raw_dll_version%%+*}"

  echo "==> Validating release versions for tag '$tag' (expected version: '$expected_version')..."
  echo "    DLL InformationalVersion: $raw_dll_version (stripped: $dll_version)"
  echo "    Helm Chart version:       $chart_version"
  echo "    Helm Chart appVersion:    $chart_app_version"
  echo "    Rendered image tag:       $rendered_image_tag"

  # 1. DLL InformationalVersion comparison
  if [ "$dll_version" != "$expected_version" ]; then
    echo "::error::Version mismatch: DLL ProductVersion '$dll_version' does not match expected tag version '$expected_version'" >&2
    return 1
  fi

  # 2. Helm Chart version comparison
  if [ "$chart_version" != "$expected_version" ]; then
    echo "::error::Version mismatch: Helm Chart version '$chart_version' does not match expected tag version '$expected_version'" >&2
    return 1
  fi

  # 3. Helm Chart appVersion comparison
  if [ "$chart_app_version" != "$expected_version" ]; then
    echo "::error::Version mismatch: Helm Chart appVersion '$chart_app_version' does not match expected tag version '$expected_version'" >&2
    return 1
  fi

  # 4. Rendered Deployment image tag comparison
  if [ "$rendered_image_tag" != "$expected_version" ]; then
    echo "::error::Version mismatch: Rendered Deployment image tag '$rendered_image_tag' does not match expected tag version '$expected_version'" >&2
    return 1
  fi

  # 5. Check that the tag exists in the registry
  if [ "$check_docker" = "true" ] && [ -n "$image_ref" ]; then
    local target_image="${image_ref}:${rendered_image_tag}"
    echo "==> Verifying image exists in registry: $target_image"
    if ! docker buildx imagetools inspect "$target_image" >/dev/null 2>&1; then
      echo "::error::Registry check failed: image tag '$target_image' was not found in registry" >&2
      return 1
    fi
  fi

  echo "==> All release versions match '$expected_version'."
  return 0
}

run_self_tests() {
  echo "Running validate-release-version self-tests..."

  # Test 1: Matching versions pass
  if ! compare_versions "v1.7.0" "1.7.0+sha.1234567" "1.7.0" "1.7.0" "1.7.0" "" "false" >/dev/null 2>&1; then
    echo "FAIL: Expected match case to pass" >&2
    exit 1
  fi

  # Test 2: DLL version mismatch fails
  if compare_versions "v1.7.0" "1.6.0+sha.1234567" "1.7.0" "1.7.0" "1.7.0" "" "false" >/dev/null 2>&1; then
    echo "FAIL: Expected DLL mismatch to fail" >&2
    exit 1
  fi

  # Test 3: Chart version mismatch fails
  if compare_versions "v1.7.0" "1.7.0" "1.6.0" "1.7.0" "1.7.0" "" "false" >/dev/null 2>&1; then
    echo "FAIL: Expected Chart version mismatch to fail" >&2
    exit 1
  fi

  # Test 4: Chart appVersion mismatch fails
  if compare_versions "v1.7.0" "1.7.0" "1.7.0" "1.6.0" "1.7.0" "" "false" >/dev/null 2>&1; then
    echo "FAIL: Expected Chart appVersion mismatch to fail" >&2
    exit 1
  fi

  # Test 5: Rendered image tag mismatch fails
  if compare_versions "v1.7.0" "1.7.0" "1.7.0" "1.7.0" "1.6.0" "" "false" >/dev/null 2>&1; then
    echo "FAIL: Expected rendered image tag mismatch to fail" >&2
    exit 1
  fi

  # Test 6: Tag without 'v' prefix also works
  if ! compare_versions "1.7.0" "1.7.0" "1.7.0" "1.7.0" "1.7.0" "" "false" >/dev/null 2>&1; then
    echo "FAIL: Expected un-prefixed tag to match" >&2
    exit 1
  fi

  echo "All self-tests passed."
  exit 0
}

if [ "$#" -ge 1 ] && [ "$1" = "--self-test" ]; then
  run_self_tests
fi

if [ "$#" -ne 4 ]; then
  echo "Usage: $0 <tag> <dll> <chart-tgz> <image-ref>" >&2
  echo "       $0 --self-test" >&2
  exit 1
fi

TAG="$1"
DLL="$2"
CHART_TGZ="$3"
IMAGE_REF="$4"

if [ ! -f "$DLL" ]; then
  echo "::error::DLL file not found: $DLL" >&2
  exit 1
fi

if [ ! -f "$CHART_TGZ" ]; then
  echo "::error::Chart package not found: $CHART_TGZ" >&2
  exit 1
fi

# Extract InformationalVersion using pwsh
RAW_DLL_VER=$(pwsh -NoProfile -c "(Get-Item '$DLL').VersionInfo.ProductVersion" 2>/dev/null || echo "")
if [ -z "$RAW_DLL_VER" ]; then
  echo "::error::Could not extract ProductVersion from DLL '$DLL'" >&2
  exit 1
fi

# Extract chart version and appVersion using helm show chart
CHART_INFO=$(helm show chart "$CHART_TGZ")
CHART_VER=$(echo "$CHART_INFO" | grep '^version:' | awk '{print $2}' | tr -d '"'\''')
CHART_APP_VER=$(echo "$CHART_INFO" | grep '^appVersion:' | awk '{print $2}' | tr -d '"'\''')

# Extract rendered deployment image tag using helm template
RENDERED_DEPLOY=$(helm template "$CHART_TGZ")
RENDERED_IMG=$(echo "$RENDERED_DEPLOY" | grep -E 'image:\s*"?.*' | head -n 1 | awk '{print $2}' | tr -d '"'\''')
RENDERED_TAG="${RENDERED_IMG##*:}"

compare_versions "$TAG" "$RAW_DLL_VER" "$CHART_VER" "$CHART_APP_VER" "$RENDERED_TAG" "$IMAGE_REF" "true"
