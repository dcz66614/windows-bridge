# Windows Bridge relay — production deployment (T27)

Status: **DRAFT — not deployed.** The domain in `.env.example` is the
placeholder `bridge.example.invalid`; the image has not been built
here (this VM has no Docker). Everything below is ready for review;
the only remaining decisions are the real domain and the host.

## Architecture (single instance)

```
Internet --HTTPS--> Caddy (TLS, Let's Encrypt) --HTTP loopback--> relay:5000
                         relay-data volume: /data/relay.db + /data/keys/
                         secret: signing.pfx (OpenIddict token signing)
```

- One relay instance, one SQLite database (WAL), one DataProtection
  key ring, one signing PFX. No multi-instance deployment in v1
  (leases are in-memory-validated against the single database;
  horizontal scaling is out of scope).
- TLS terminates at Caddy. The relay listens on loopback only and
  trusts `X-Forwarded-*` from the loopback proxy only
  (`RelayApp`: `KnownProxies = loopback`).
- The relay fails closed in Production without the signing PFX
  (see `ProductionStartupTests`: no PFX → startup throws; wrong
  password → startup throws; valid PFX → `/healthz` 200).

## First deployment

1. Pick a host with Docker + a public IP and a domain pointing at it.
2. Copy this directory to the host. `cp .env.example .env` and set:
   - `BRIDGE_DOMAIN` — the real domain.
   - `SIGNING_CERT_PATH` / `SIGNING_CERT_PASSWORD` — generate the
     PFX with `scripts/generate-signing-cert.sh` and back it up
     offline. Losing it invalidates every token; devices must
     re-pair.
3. `docker compose up -d --build`. Caddy obtains the TLS
   certificate automatically.
4. Check `curl https://$BRIDGE_DOMAIN/healthz` → `{"status":"ok",...}`.
5. **Before inviting the owner to bind a daily computer**: create
   two independent test accounts, complete the isolation check
   (account A cannot see B's devices — both return 404) and the
   revocation check (revoke A's device → its live WSS is evicted
   and its refresh token cannot mint a new access token). Only
   then invite the owner. Do not exceed the verified capacity on
   the first public test.

## Configuration reference

| Setting | Source | Notes |
|---|---|---|
| `RelayService:DatabasePath` | env `RelayService__DatabasePath` | `/data/relay.db` in compose |
| `RelayService:Urls` | env `RelayService__Urls` | `http://127.0.0.1:5000` in compose |
| `RelayService:SigningCertificate:Path` | Docker secret `/run/secrets/signing.pfx` | PFX, required in Production |
| `RelayService:SigningCertificate:Password` | env from `.env` | never in the repo |
| DataProtection keys | `/data/keys/` (auto) | part of the backup set |
| Access token lifetime | 10 min (code) | refresh 14 days, rolling, zero reuse leeway |

## Backups and restore

- Backup: `scripts/backup.sh` — stops the relay, copies
  `relay.db` (+ WAL) and `keys/`, copies the signing PFX.
  Schedule it daily; keep copies off the host.
- Restore: `scripts/restore.sh <backup-dir>` — restores the files
  and then **expires every live lease and approved consent** in
  the restored database. A restore can roll revocation backwards,
  so no historic command is accepted afterwards: every controller
  must request a fresh consent from its device user. Devices stay
  paired; pairings are not cleared.
- The database uses `EnsureCreated()` (no EF migrations yet).
  Schema changes in a future version must ship a migration plan
  before they ship code.

## Health, limits, logs

- Health: `GET /healthz` (anonymous, version + liveness only).
  Compose healthchecks the relay every 30s and only starts Caddy
  once the relay is healthy.
- Disk: the `relay-data` volume holds the database + keys; the
  compose file sets no quota — set a host-level quota or monitor
  it (SQLite grows with command records; no retention job exists
  yet — see "Known limits").
- Logs: the relay logs to stdout (Docker logs, JSON via Caddy's
  access log, 50MB × 5 files). Rotate Docker logs at the host
  level (`log-driver` options) if the host fills up.
- Rate limiting: not in the app. Add it at the edge (Caddy
  plugin or provider WAF) before opening registration publicly.
  Pairing init already has a per-(client, fingerprint) limiter
  (6/hour) in the app.

## Upgrades

1. Back up first (`scripts/backup.sh`).
2. Pull the new image / rebuild, `docker compose up -d`.
3. Check `/healthz` and the OpenID discovery document
   (`/.well-known/openid-configuration`).
4. If the new version fails to start, `docker compose down`
   and restore the previous image tag; the database is
   forward-compatible within 0.1.x (no migrations yet).
5. The Windows agent's own update flow is separate
   (`UpdateManifest` verification, T26); the relay never pushes
   agent updates.

## Known limits (v1)

- No EF migrations (`EnsureCreated` only).
- No command-record retention job; the database grows with use.
- No rate limiting in the app (edge only).
- No multi-instance / HA deployment.
- `bridge.example.invalid` is the documentation placeholder;
  the real domain is the owner's decision.
