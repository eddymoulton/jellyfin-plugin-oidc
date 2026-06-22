#!/usr/bin/env bash
# Capture a baseline Jellyfin config snapshot.
#
# Usage: ./snapshot-create.sh
#
# Brings up Jellyfin with an EMPTY config volume, pauses for you to walk through
# the first-run wizard manually in a browser, then stops Jellyfin and tars up the
# resulting config directory into snapshots/jellyfin-${JELLYFIN_VERSION}.tar.zst.
#
# Run this:
#   * Once, at initial setup
#   * Whenever you want to rebuild the baseline from scratch
#
# For the routine "Jellyfin version bumped, migrate the existing snapshot forward"
# workflow, use snapshot-refresh.sh instead.

set -euo pipefail
source "$(dirname "$0")/_lib.sh"

require_baseline_tools

# JELLYFIN_VERSION is loaded by _lib.sh
SNAPSHOT_PATH="${TEST_ENV_DIR}/snapshots/jellyfin-${JELLYFIN_VERSION}.tar.zst"
CONFIG_DIR="${TEST_ENV_DIR}/.data/jellyfin/config"

if [[ -e "${SNAPSHOT_PATH}" ]]; then
  die "Snapshot already exists at ${SNAPSHOT_PATH}. Delete it first if you want to rebuild."
fi

log "Cleaning any existing .data directory ..."
rm -rf "${TEST_ENV_DIR}/.data"

log "Publishing plugin so it loads during wizard (helps verify it survives the wizard) ..."
dotnet publish -c Release "${REPO_ROOT}/SSO-Auth/SSO-Auth.csproj" -o "${TEST_ENV_DIR}/.publish"

log "Starting Jellyfin (version ${JELLYFIN_VERSION}) ..."
JELLYFIN_VERSION="${JELLYFIN_VERSION}" compose up -d jellyfin

wait_for_jellyfin

cat <<EOF

================================================================================
  MANUAL STEP: Complete the Jellyfin first-run wizard.

  1. Open ${JELLYFIN_BASE_URL} in your browser.
  2. Step through the wizard:
       - Display language: English (or your preference)
       - Username: ${ADMIN_USERNAME}
       - Password: ${ADMIN_PASSWORD}
       - Add at least one media library:
           Content type: Movies
           Display name: Movies
           Folder:       /media
       - Accept the defaults for the remaining steps.
  3. Confirm you can log in as ${ADMIN_USERNAME} / ${ADMIN_PASSWORD}.
  4. Sign out (so the snapshot isn't tied to an active session).
  5. Return here and press ENTER.
================================================================================

EOF

read -r -p "Press ENTER once the wizard is complete ..."

log "Stopping Jellyfin gracefully so the config is flushed to disk ..."
compose stop jellyfin

log "Compressing config directory to ${SNAPSHOT_PATH} ..."
tar --no-xattrs -C "${CONFIG_DIR}" -cf - . | zstd -19 -o "${SNAPSHOT_PATH}"

log "Tearing down ..."
compose down

log "Snapshot written:"
ls -lh "${SNAPSHOT_PATH}"

cat <<EOF

Next: review the snapshot and commit it.
  git add ${SNAPSHOT_PATH#${REPO_ROOT}/}
  git status
EOF
