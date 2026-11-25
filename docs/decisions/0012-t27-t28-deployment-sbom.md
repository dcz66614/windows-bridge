# ADR 0012 — T27 deployment package + T28 SBOM (production-readiness fixes)

Date: 2026-11-13. Status: package + docs complete; NOT deployed
(domain + host undecided; image never built here — no Docker).

## Two production bugs fixed in RelayApp
1. **The signing PFX was checked but never loaded.** The
   Production fail-closed check verified the file existed, but
   the OpenIddict server always used development certificates —
   every restart would have minted a fresh signing key and
   invalidated all tokens. Fixed: Production loads the PFX
   (password from `RelayService:SigningCertificate:Password`)
   as both signing and encryption certificate, and throws the
   clear error before configuration when the path is missing.
   Pinned by `ProductionStartupTests` (3 tests): no PFX →
   throws; wrong password → throws; valid PFX → starts and
   `/healthz` 200.
2. **DataProtection keys were not persisted.** Cookies and
   antiforgery tokens would have died on every restart. Fixed:
   keys persist to `<db-dir>/keys/` (part of the backup set);
   the test forces a Protect operation and asserts the directory
   exists.
3. **Forwarded headers**: the relay now trusts
   X-Forwarded-For/Proto from loopback proxies only, so
   OpenIddict issuer/redirect URLs use the public HTTPS origin
   behind Caddy.

## Deployment package (`deploy/`)
- `Dockerfile` (multi-stage, .NET 10 SDK → runtime-deps,
  non-root uid 10001, /healthz healthcheck), `docker-compose.yml`
  (relay + Caddy with automatic TLS), `Caddyfile` (HSTS,
  hardening headers, log rotation), `.env.example` (domain
  placeholder `bridge.example.invalid`).
- `scripts/generate-signing-cert.sh` (self-signed PFX for
  TOKEN signing, not TLS), `scripts/backup.sh` (relay.db +
  WAL + keys/ + PFX), `scripts/restore.sh` (restores files,
  then EXPIRES every live lease and approved consent — a
  restore must never keep accepting historic commands).
- `deploy/README.md` (ops manual): first deployment, two-account
  isolation + revocation gate BEFORE inviting the owner, config
  reference, backup/restore, health/limits/logs, upgrades,
  known limits. YAML validated; shell scripts `bash -n` clean.
- The relay publishes for linux-x64 (157M, verified this turn).

## T28 SBOM + release notes (`release/`)
- `generate-sbom.py` reads the transitive closure from
  `project.assets.json` and licenses from the local NuGet cache
  `.nuspec` (never guessed; UNKNOWN reported). 162 packages:
  MIT 118, Apache-2.0 38, BSD-3-Clause 5, xunit.abstractions
  unresolved (license URL only — must be confirmed before a
  public release). Two parser bugs found and fixed while
  generating: Element truthiness (`<license>` has text but no
  children, so `or` fell through) and the nuspec namespace
  (2013/01 vs 2013/05 — now matched by local name); `type=file`
  licenses are identified by reading the actual LICENSE.txt.
- `SBOM.md` + `RELEASE_NOTES_0.1.0.md` (version, artifacts +
  SHA256, supported systems, verified vs unverified, known
  limits, recovery, uninstall). The project's own license is
  still PENDING_SAM_DECISION; the notes say so explicitly.

## Verification
- Full suite 158 green (23+16+5+59+55); solution 0 warnings
  0 errors. The relay linux-x64 publish succeeds.
