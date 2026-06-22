#!/usr/bin/env bash
# Migrate an existing snapshot forward to a new Jellyfin version.
#
# Usage: ./snapshot-refresh.sh <old-version> <new-version>
#   e.g. ./snapshot-refresh.sh 10.11.10 10.11.11
#
# Steps:
#   1. Confirm the old snapshot exists and the new one does not.
#   2. Extract the old snapshot into a clean .data directory.
#   3. Start Jellyfin pinned to the new version. Jellyfin runs its config
#      migrations during startup.
#   4. Run a tiny smoke check: confirm /System/Info/Public returns ProductName.
#   5. Stop Jellyfin gracefully (so config is flushed).
#   6. Tar the migrated config into snapshots/jellyfin-${NEW}.tar.zst.
#   7. Tear down and leave the volume in place ONLY if step 4 failed, for
#      manual inspection.
#
# The old snapshot is never modified or deleted. Old snapshots are recovery
# points — keep them.

set -euo pipefail
source "$(dirname "$0")/_lib.sh"

require_baseline_tools

if [[ $# -ne 2 ]]; then
  die "Usage: $0 <old-version> <new-version>
Example: $0 10.11.10 10.11.11"
fi

OLD_VERSION="$1"
NEW_VERSION="$2"
OLD_SNAPSHOT="${TEST_ENV_DIR}/snapshots/jellyfin-${OLD_VERSION}.tar.zst"
NEW_SNAPSHOT="${TEST_ENV_DIR}/snapshots/jellyfin-${NEW_VERSION}.tar.zst"
CONFIG_DIR="${TEST_ENV_DIR}/.data/jellyfin/config"

if [[ ! -f "${OLD_SNAPSHOT}" ]]; then
  die "Old snapshot not found: ${OLD_SNAPSHOT}"
fi
if [[ -f "${NEW_SNAPSHOT}" ]]; then
  die "New snapshot already exists: ${NEW_SNAPSHOT}. Delete it first if you want to rebuild."
fi

log "Preparing clean .data directory ..."
rm -rf "${TEST_ENV_DIR}/.data"
mkdir -p "${CONFIG_DIR}"

log "Extracting ${OLD_SNAPSHOT##*/} ..."
zstd -dc "${OLD_SNAPSHOT}" | tar --no-same-owner -C "${CONFIG_DIR}" -xf -

log "Publishing plugin ..."
dotnet publish -c Release "${REPO_ROOT}/SSO-Auth/SSO-Auth.csproj" -o "${TEST_ENV_DIR}/.publish"

log "Starting Jellyfin ${NEW_VERSION} ..."
JELLYFIN_VERSION="${NEW_VERSION}" compose up -d jellyfin

wait_for_jellyfin

log "Smoke checking server identity ..."
PUBLIC_INFO="$(curl -sf "${JELLYFIN_BASE_URL}/System/Info/Public")"
if ! jq -e '.ProductName == "Jellyfin Server"' <<<"${PUBLIC_INFO}" > /dev/null; then
  warn "Smoke check failed. Leaving containers running for inspection."
  warn "Public info response:"
  printf '%s\n' "${PUBLIC_INFO}" >&2
  die "Migration aborted. Investigate, then run scripts/down.sh --volumes to clean up."
fi

log "Smoke check passed. Authenticating as ${ADMIN_USERNAME} ..."
if ! authenticate_admin > /dev/null; then
  warn "Could not authenticate as ${ADMIN_USERNAME} after migration."
  warn "Leaving containers running for inspection."
  die "Migration aborted. Investigate, then run scripts/down.sh --volumes to clean up."
fi

log "Stopping Jellyfin gracefully ..."
compose stop jellyfin

log "Compressing migrated config to ${NEW_SNAPSHOT##*/} ..."
tar --no-xattrs -C "${CONFIG_DIR}" -cf - . | zstd -19 -o "${NEW_SNAPSHOT}"

log "Tearing down ..."
compose down

log "Migration complete:"
ls -lh "${OLD_SNAPSHOT}" "${NEW_SNAPSHOT}"

cat <<EOF

Next:
  1. Update versions.env to set
       JELLYFIN_SNAPSHOT_VERSION=${NEW_VERSION}
     (and JELLYFIN_VERSION too, if you are also bumping the image).
  2. Update the snapshots README inventory to note the new snapshot.
  3. Review and commit:
       git add ${NEW_SNAPSHOT#${REPO_ROOT}/} versions.env test-env/snapshots/README.md
EOF
