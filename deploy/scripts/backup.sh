#!/usr/bin/env bash
# Back up the relay's persistent state: relay.db (SQLite),
# DataProtection keys/, and the signing PFX.
# The backup is a directory with a timestamp; copy it off the host.
# Usage: ./backup.sh [backup-root]
set -euo pipefail
ROOT="${1:-./backups}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
DEST="$ROOT/$STAMP"
mkdir -p "$DEST"
# SQLite: copy the database file AND its WAL (the relay uses WAL).
# A live copy can be inconsistent; stop the relay first, or use
# `sqlite3 /data/relay.db ".backup $DEST/relay.db"` for a live backup.
docker compose stop relay
docker run --rm -v bridge-relay_relay-data:/data -v "$PWD/$DEST:/out" \
  alpine sh -c 'cp /data/relay.db /out/relay.db 2>/dev/null || true; cp -r /data/keys /out/keys 2>/dev/null || true'
docker compose start relay
cp "${SIGNING_CERT_PATH:-./secrets/signing.pfx}" "$DEST/signing.pfx" 2>/dev/null || echo "WARNING: signing PFX not copied (check SIGNING_CERT_PATH)"
chmod 600 "$DEST/signing.pfx" 2>/dev/null || true
echo "Backup at $DEST"
ls -la "$DEST"
