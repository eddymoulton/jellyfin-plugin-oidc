#!/usr/bin/env bash
# Register the Dex OIDC provider with the SSO plugin via its REST API.
#
# Idempotent: the plugin's Add endpoint is upsert semantics, and we tolerate
# "provider already exists" responses.

set -euo pipefail
source "$(dirname "$0")/_lib.sh"

require_tool curl
require_tool jq

PROVIDER_NAME="dex"
SEED_FILE="${TEST_ENV_DIR}/seed/dex-provider.json"

if [[ ! -f "${SEED_FILE}" ]]; then
  die "Missing seed file: ${SEED_FILE}"
fi

log "Authenticating as ${ADMIN_USERNAME} ..."
TOKEN="$(authenticate_admin)"

log "Minting an API key ..."
# Reuse an existing key if one named test-env exists, otherwise create one.
EXISTING_KEYS="$(curl -sf -H "Authorization: MediaBrowser Token=\"${TOKEN}\"" \
  "${JELLYFIN_BASE_URL}/Auth/Keys")"

API_KEY="$(jq -r '.Items[] | select(.AppName == "test-env") | .AccessToken' <<<"${EXISTING_KEYS}" | head -n1)"

if [[ -z "${API_KEY}" ]]; then
  log "Creating a new API key ..."
  curl -sf -X POST \
    -H "Authorization: MediaBrowser Token=\"${TOKEN}\"" \
    "${JELLYFIN_BASE_URL}/Auth/Keys?App=test-env" > /dev/null

  EXISTING_KEYS="$(curl -sf -H "Authorization: MediaBrowser Token=\"${TOKEN}\"" \
    "${JELLYFIN_BASE_URL}/Auth/Keys")"
  API_KEY="$(jq -r '.Items[] | select(.AppName == "test-env") | .AccessToken' <<<"${EXISTING_KEYS}" | head -n1)"
fi

if [[ -z "${API_KEY}" ]]; then
  die "Failed to obtain an API key."
fi

log "Registering provider '${PROVIDER_NAME}' from ${SEED_FILE##*/} ..."
curl -sf -X POST \
  -H "Content-Type: application/json" \
  --data-binary "@${SEED_FILE}" \
  "${JELLYFIN_BASE_URL}/sso/OID/Add/${PROVIDER_NAME}?api_key=${API_KEY}" > /dev/null

log "Verifying provider registration ..."
REGISTERED="$(curl -sf "${JELLYFIN_BASE_URL}/sso/OID/Get?api_key=${API_KEY}")"
if ! jq -e --arg name "${PROVIDER_NAME}" '.[$name]' <<<"${REGISTERED}" > /dev/null; then
  die "Provider '${PROVIDER_NAME}' did not appear in /sso/OID/Get response:
${REGISTERED}"
fi

log "Provider '${PROVIDER_NAME}' registered."
