# ADR 0006 — T12 consent sessions + control leases

Date: 2026-11-13. Status: accepted (T12 PASS, 15/15 lease tests,
53/53 RelayService tests, 97 tests total across all suites).

## Two different things
- **Consent session** (long approval): the control client asks
  (`POST /api/v1/leases`, 202 pending); the device gets a
  `lease_update` frame (`approval_requested`); the DEVICE USER
  approves or denies locally via the device token
  (`POST /api/v1/devices/{id}/consent/{id}/decide`). The server
  never approves on the user's behalf. Default 2 h, max explicit
  8 h, min 60 s (for tests). Denied → `consent_denied`; expired
  → `consent_expired`.
- **Control lease** (short liveness): acquired under an approved
  consent (`POST /api/v1/leases/{id}/acquire`). One WRITE lease
  per device per interactive session; a second writer gets 409
  `lease_held` WITH the holder and expiry. Observe leases are
  shared (any number of readers). TTL 60 s default, 5–60 s.
  Renewal extends the MONOTONIC deadline (Stopwatch ticks), so a
  wall-clock rollback cannot extend control. Releasing a lease
  never revokes the binding.

## Commands (docs/04 §6 envelope, now enforced)
Exactly 11 fields, unknown fields rejected; UUID command/lease
ids; epoch and sequence are positive ints; `issued_at` at most
2 s in the future (`clock_skew` 400); TTL in (0, 30 s]
(`bad_ttl` 400); `expires_at` in the past → 410
`command_expired`. Checks, in order: envelope → token scope →
device ownership → capability → idempotency (stored receipt;
same id + different binding → 409 `idempotency_conflict`) →
lease (Active, holder, consent Approved, epoch == current
connection epoch, monotonic deadline live) → sequence ==
lease.NextSequence (else 409 `stale_sequence`) → dispatch.
`GET /api/v1/commands/{id}` returns the stored receipt.
Offline → 503 `device_offline` (arch table, not 409).

## Local state is highest priority
The device reports paused/locked over the channel
(`lease_update` frame); the server trusts the report and
returns 423 (`local_paused` / `local_locked`, next_action
`local_resume`) for new commands and renewals. A pause is
latched: only a local resume clears it; the lease stays Active
through a pause. The agent ALSO refuses to execute while
paused/locked (`DeviceChannelClient.LocalPaused/LocalLocked`),
even if the server dispatched before learning the state —
tested by pausing locally without reporting.
A reconnect bumps the epoch and stales all old leases
(`stale_lease`); lock screen, logout, user switch, pause,
revocation and connection replacement all end the old epoch.

## Decisions
- **Lazy expiry.** Leases and consents expire on access
  (every command/acquire/renew checks the monotonic deadline
  and marks the row). The background sweeper does NO database
  work — see the hazard below.
- **The sweeper hazard (verified by bisection).** A background
  service that creates and disposes a DI scope (or resolves a
  DbContext in one) killed the root provider under
  WebApplicationFactory: later requests failed with
  `ObjectDisposedException: Cannot access a disposed object
  (IServiceProvider)`. `DbContextOptions` is scoped, so the
  singleton sweeper cannot hold it either. Consequence: the
  sweeper only does in-memory work (stale WS connections,
  command TTLs). Stale lease/consent rows that are never
  accessed again accumulate until a maintenance pass — a
  future admin endpoint can run the (removed) cleanup.
- **Error shape** is always `{error:{code, message,
  next_action}}` with next_action from the arch §5 set.
