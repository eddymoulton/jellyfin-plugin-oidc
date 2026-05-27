#!/usr/bin/env bash
# Stop the test environment. Pass --volumes (or -v) to also wipe the bind-mounted
# .data/ directory and the .publish/ build output.

set -euo pipefail
source "$(dirname "$0")/_lib.sh"

WIPE=0
case "${1:-}" in
  -v|--volumes) WIPE=1 ;;
  "") ;;
  *) die "Unknown argument: $1 (use --volumes to wipe state)" ;;
esac

log "Stopping containers ..."
compose down

if (( WIPE )); then
  log "Wiping .data/ and .publish/ ..."
  rm -rf "${TEST_ENV_DIR}/.data" "${TEST_ENV_DIR}/.publish"
fi

log "Done."
