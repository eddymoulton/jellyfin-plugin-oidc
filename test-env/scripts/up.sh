#!/usr/bin/env bash
# Bring up the test environment from cold.
#
# Steps:
#   1. Publish the plugin to test-env/.publish/
#   2. If test-env/.data/jellyfin/config is empty, extract the snapshot for the
#      pinned Jellyfin version into it.
#   3. docker compose up -d
#   4. Wait for Jellyfin to be healthy.
#   5. Run provision.sh to register the Dex OIDC provider with the plugin.
#
# Idempotent: re-running won't re-extract the snapshot if config is already
# populated. To start fresh, run scripts/down.sh --volumes first.

set -euo pipefail
source "$(dirname "$0")/_lib.sh"

require_baseline_tools

JELLYFIN_VERSION="${JELLYFIN_VERSION:-10.11.10}"
if [[ -f "${TEST_ENV_DIR}/.env" ]]; then
  # shellcheck disable=SC1091
  source "${TEST_ENV_DIR}/.env"
fi

SNAPSHOT_PATH="${TEST_ENV_DIR}/snapshots/jellyfin-${JELLYFIN_VERSION}.tar.zst"
CONFIG_DIR="${TEST_ENV_DIR}/.data/jellyfin/config"

if [[ ! -f "${SNAPSHOT_PATH}" ]]; then
  die "No snapshot for Jellyfin ${JELLYFIN_VERSION} at ${SNAPSHOT_PATH}.
Run scripts/snapshot-create.sh to produce one, or set JELLYFIN_VERSION to a
version with an existing snapshot in test-env/snapshots/."
fi

log "Publishing plugin to ${TEST_ENV_DIR}/.publish ..."
dotnet publish -c Release "${REPO_ROOT}/SSO-Auth/SSO-Auth.csproj" -o "${TEST_ENV_DIR}/.publish"

# Restore snapshot only if the config dir is empty/missing — preserves manual
# tweaks between runs.
if [[ ! -d "${CONFIG_DIR}" ]] || [[ -z "$(ls -A "${CONFIG_DIR}" 2>/dev/null)" ]]; then
  log "Config directory is empty — restoring snapshot ${SNAPSHOT_PATH##*/} ..."
  mkdir -p "${CONFIG_DIR}"
  zstd -dc "${SNAPSHOT_PATH}" | tar --no-same-owner -C "${CONFIG_DIR}" -xf -
else
  log "Config directory already populated — skipping snapshot restore."
fi

log "Starting containers ..."
JELLYFIN_VERSION="${JELLYFIN_VERSION}" compose up -d

wait_for_jellyfin

log "Provisioning SSO provider ..."
"${SCRIPT_DIR}/provision.sh"

cat <<EOF

================================================================================
  Test environment is up.

  Jellyfin:  ${JELLYFIN_BASE_URL}
  Admin:     ${ADMIN_USERNAME} / ${ADMIN_PASSWORD}

  Seeded test users (password is "password" for all):
    admin@test.local      groups: jellyfin-admin
    user@test.local       groups: jellyfin-users
    noaccess@test.local   groups: (none)

  To start the OIDC login flow, open:
    ${JELLYFIN_BASE_URL}/sso/OID/start/dex

  Useful commands:
    scripts/reload.sh           # rebuild plugin + restart Jellyfin
    scripts/down.sh             # stop containers
    scripts/down.sh --volumes   # stop + wipe Jellyfin state
================================================================================
EOF
