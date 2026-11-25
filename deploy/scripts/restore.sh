#!/usr/bin/env bash
# Restore the relay from a backup directory (see backup.sh).
# CRITICAL (kit T27): a database restore can roll revocation
# information backwards. After restoring, ALL live leases must be
# invalidated and authorization re-established — never restore an
# old snapshot and keep accepting historic commands.
# This script restores the files, then RESTARTS the relay with an
# empty lease state by clearing the live lease tables. Devices stay
# paired (pairing is a separate table) but every controller must
# request a fresh consent from the device user.
# Usage: ./restore.sh <backup-dir>
set -euo pipefail
BACKUP="${1:?usage: restore.sh <backup-dir>}"
[ -f "$BACKUP/relay.db" ] || { echo "no relay.db in $BACKUP"; exit 1; }
docker compose stop relay
docker run --rm -v bridge-relay_relay-data:/data -v "$PWD/$BACKUP:/in" \
  alpine sh -c 'cp /in/relay.db /data/relay.db; rm -rf /data/keys; cp -r /in/keys /data/keys 2>/dev/null || true'
# Invalidate every live lease and consent session in the restored DB.
docker run --rm -v bridge-relay_relay-data:/data alpine sh -c \
  'apk add --no-cache sqlite >/dev/null 2>&1; sqlite3 /data/relay.db "UPDATE ControlLeases SET Status='\''Expired'\'' WHERE Status='\''Active'\''; UPDATE ConsentSessions SET Status='\''Expired'\'' WHERE Status='\''Approved'\'';"'
docker compose start relay
echo "Restored. All live leases and approved consents are expired."
echo "Every controller must request a fresh consent from its device user."
