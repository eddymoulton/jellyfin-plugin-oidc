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
runtime by `scripts/provision.sh` from `seed/dex-provider.json`. This keeps
SSO config reviewable in source.

## Inventory

| Snapshot                    | Jellyfin version | Notes                         |
| --------------------------- | ---------------- | ----------------------------- |
| `jellyfin-10.11.10.tar.zst` | 10.11.10         | Baseline. Created via wizard. |

## Updating

When Jellyfin releases a new version:

```bash
# Update the pin
sed -i '' 's/JELLYFIN_VERSION=.*/JELLYFIN_VERSION=10.11.11/' test-env/.env.example

# Migrate the snapshot forward
test-env/scripts/snapshot-refresh.sh 10.11.10 10.11.11

# Update this README's inventory table, then commit:
#   test-env/.env.example
#   test-env/snapshots/jellyfin-10.11.11.tar.zst
#   test-env/snapshots/README.md
```

Old snapshots are recovery points — do not delete them.

If migration fails or the migrated config behaves badly, rebuild the
baseline from scratch with `scripts/snapshot-create.sh`.
