#!/usr/bin/env bash
# Shared helpers used by snapshot-{create,refresh}.sh and by the CLI wrappers
# (up.sh / down.sh / reload.sh / provision.sh).
#
# Most orchestration logic has moved into test-env/SSO-Auth.TestEnv/.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[1]}")" && pwd)"
TEST_ENV_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${TEST_ENV_DIR}/.." && pwd)"

load_env_defaults() {
  local file="$1"
  [[ -f "${file}" ]] || return 0
  local key value
  while IFS='=' read -r key value; do
    key="${key//[[:space:]]/}"
    [[ -z "${key}" || "${key}" == \#* ]] && continue
    [[ -n "${!key:-}" ]] && continue
    value="${value%\"}"
    value="${value#\"}"
    export "${key}=${value}"
  done <"${file}"
}

load_env_defaults "${TEST_ENV_DIR}/.env"
load_env_defaults "${REPO_ROOT}/versions.env"

COMPOSE_FILE="${TEST_ENV_DIR}/docker-compose.yml"
JELLYFIN_BASE_URL="http://localhost:8096"
ADMIN_USERNAME="admin"
ADMIN_PASSWORD="admin"

TEST_ENV_PROJECT="${REPO_ROOT}/test-env/SSO-Auth.TestEnv"
TEST_ENV_DLL="${TEST_ENV_PROJECT}/bin/Debug/net9.0/SSO-Auth.TestEnv.dll"

log() { printf "\033[1;34m[+] %s\033[0m\n" "$*"; }
warn() { printf "\033[1;33m[!] %s\033[0m\n" "$*" >&2; }
die() {
  printf "\033[1;31m[x] %s\033[0m\n" "$*" >&2
  exit 1
}

require_tool() {
  local tool="$1"
  if ! command -v "$tool" >/dev/null 2>&1; then
    die "Missing required tool: '${tool}'. Please install it and re-run."
  fi
}

# Used by snapshot-{create,refresh}.sh only. CLI wrappers don't need this.
require_baseline_tools() {
  require_tool docker
  require_tool curl
  require_tool jq
  require_tool tar
  require_tool zstd
  require_tool dotnet
}

# Used by snapshot-{create,refresh}.sh only. CLI wrappers don't need this.
compose() {
  docker compose -f "${COMPOSE_FILE}" "$@"
}

# Used by snapshot-{create,refresh}.sh only. CLI wrappers don't need this.
wait_for_jellyfin() {
  local max_attempts=60
  local attempt=0
  log "Waiting for Jellyfin to respond on ${JELLYFIN_BASE_URL} ..."
  while ((attempt < max_attempts)); do
    if curl -sf -o /dev/null "${JELLYFIN_BASE_URL}/System/Info/Public"; then
      log "Jellyfin is up."
      return 0
    fi
    attempt=$((attempt + 1))
    sleep 2
  done
  die "Jellyfin did not become ready within $((max_attempts * 2)) seconds."
}

# Used by snapshot-refresh.sh only. CLI wrappers don't need this.
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

# Build the CLI on first use so subsequent invocations can run --no-build for speed.
run_test_env_cli() {
  require_tool dotnet
  if [[ ! -f "${TEST_ENV_DLL}" ]]; then
    dotnet build "${TEST_ENV_PROJECT}/SSO-Auth.TestEnv.csproj" >/dev/null
  fi
  exec dotnet run --no-build --project "${TEST_ENV_PROJECT}" -- "$@"
}
