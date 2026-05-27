#!/usr/bin/env bash
# Fast iteration loop: republish the plugin and restart Jellyfin.
# Use after changing plugin source code.

set -euo pipefail
source "$(dirname "$0")/_lib.sh"

require_tool dotnet
require_tool docker

log "Publishing plugin ..."
dotnet publish -c Release "${REPO_ROOT}/SSO-Auth/SSO-Auth.csproj" -o "${TEST_ENV_DIR}/.publish"

log "Restarting Jellyfin ..."
compose restart jellyfin

wait_for_jellyfin

log "Confirming the plugin loaded:"
docker logs --since 1m jellyfin-sso-test 2>&1 | grep -i "SSO-Auth" || warn "No SSO-Auth log line in the last minute — check 'docker logs jellyfin-sso-test' manually."

log "Reload complete."
