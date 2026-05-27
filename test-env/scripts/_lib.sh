#!/usr/bin/env bash
# Shared helpers. Source from each script with:
#   source "$(dirname "$0")/_lib.sh"

set -euo pipefail

# Resolve repo root and test-env root regardless of where the script was invoked from.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[1]}")" && pwd)"
TEST_ENV_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${TEST_ENV_DIR}/.." && pwd)"

COMPOSE_FILE="${TEST_ENV_DIR}/docker-compose.yml"
JELLYFIN_BASE_URL="http://localhost:8096"
ADMIN_USERNAME="admin"
ADMIN_PASSWORD="admin"

log()  { printf "\033[1;34m[+] %s\033[0m\n" "$*"; }
warn() { printf "\033[1;33m[!] %s\033[0m\n" "$*" >&2; }
die()  { printf "\033[1;31m[x] %s\033[0m\n" "$*" >&2; exit 1; }

require_tool() {
  local tool="$1"
  if ! command -v "$tool" >/dev/null 2>&1; then
    die "Missing required tool: '${tool}'. Please install it and re-run."
  fi
}

require_baseline_tools() {
  require_tool docker
  require_tool curl
  require_tool jq
  require_tool tar
  require_tool zstd
  require_tool dotnet
}

compose() {
  docker compose -f "${COMPOSE_FILE}" "$@"
}

wait_for_jellyfin() {
  local max_attempts=60
  local attempt=0
  log "Waiting for Jellyfin to respond on ${JELLYFIN_BASE_URL} ..."
  while (( attempt < max_attempts )); do
    if curl -sf -o /dev/null "${JELLYFIN_BASE_URL}/System/Info/Public"; then
      log "Jellyfin is up."
      return 0
    fi
    attempt=$((attempt + 1))
    sleep 2
  done
  die "Jellyfin did not become ready within $((max_attempts * 2)) seconds."
}

authenticate_admin() {
  # Echoes the AccessToken on stdout.
  local response
  response="$(curl -sf -X POST \
    -H "Content-Type: application/json" \
    -H "Authorization: MediaBrowser Client=\"jellyfin-sso-test\", Device=\"test-env\", DeviceId=\"test-env-script\", Version=\"1.0.0\"" \
    -d "{\"Username\":\"${ADMIN_USERNAME}\",\"Pw\":\"${ADMIN_PASSWORD}\"}" \
    "${JELLYFIN_BASE_URL}/Users/AuthenticateByName")"
  jq -er '.AccessToken' <<<"$response"
}
