# Jellyfin Snapshots

Each `jellyfin-X.Y.Z.tar.zst` file is a zstd-compressed tar of a
`/config` directory captured from a Jellyfin container after the
first-run wizard.

## Contents of a snapshot

A snapshot contains:

- Admin user `admin` / `admin` (Jellyfin local auth).
- One library `Movies` pointed at `/media`.
- Default Jellyfin configuration with the wizard marked complete.
- The SSO plugin loaded but with NO provider configurations.

Provider configurations are NOT in the snapshot — they are applied at
runtime by `scripts/provision.sh`, which registers every `seed/*.json` file
as a provider named after the file (e.g. `seed/dex.json` -> provider `dex`).
`folderRoleMapping` entries may reference a library by name (e.g. `"Movies"`);
provisioning resolves these to the live library id. This keeps SSO config
reviewable in source.

## Inventory

| Snapshot                    | Jellyfin version | Notes                         |
| --------------------------- | ---------------- | ----------------------------- |
| `jellyfin-10.11.10.tar.zst` | 10.11.10         | Baseline. Created via wizard. |

## Updating

The canonical version pins live in `versions.env` at the repo root:

```
JELLYFIN_VERSION=10.11.11           # server image the tests run against
JELLYFIN_SNAPSHOT_VERSION=10.11.10  # snapshot the tests boot from
```

`JELLYFIN_SNAPSHOT_VERSION` is decoupled from `JELLYFIN_VERSION` on purpose: a
newer image can boot an older snapshot, migrating its config forward
in-container at startup. So there are two kinds of update.

**Bump the image only** (e.g. validate a new patch against the existing seed
config) — raise `JELLYFIN_VERSION` in `versions.env` and leave
`JELLYFIN_SNAPSHOT_VERSION` alone. No new snapshot is needed; the existing
`jellyfin-${JELLYFIN_SNAPSHOT_VERSION}.tar.zst` is reused.

**Capture a fresh snapshot for the new version** — migrate the old snapshot
forward, then point the snapshot pin at it:

```bash
# Migrate the snapshot forward (boots the new image off the old snapshot)
test-env/scripts/snapshot-refresh.sh 10.11.10 10.11.11

# Then set JELLYFIN_SNAPSHOT_VERSION=10.11.11 in versions.env,
# update the inventory table below, and commit:
#   versions.env
#   test-env/snapshots/jellyfin-10.11.11.tar.zst
#   test-env/snapshots/README.md
```

Old snapshots are recovery points — do not delete them.

If migration fails or the migrated config behaves badly, rebuild the
baseline from scratch with `scripts/snapshot-create.sh`.
