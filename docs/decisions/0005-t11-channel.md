# ADR 0005 — T11 device channel (WSS)

Date: 2026-11-13. Status: accepted (T11 PASS, 12/12 channel tests,
38/38 RelayService tests, 82 tests total across all suites).

## Protocol (docs/04 §10)
Envelope `{type, message_id, schema_version:1, body}` over JSON text
frames; types hello/challenge/heartbeat/heartbeat_ack/command/
command_result/lease_update/revoke/error. Max frame 1 MiB; no
compression in v1; commands are never queued while offline.

## Handshake
1. Device dials `GET /ws/device?device_id=…` with a Bearer token
   carrying `device.connect`.
2. Server sends a `challenge` (32-byte nonce). The device answers
   with `hello`, signing `nonce + ":" + device_id` with its P-256
   key. The server verifies against the stored device record
   (exists, Active, owner == token sub, fingerprint matches,
   signature verifies). Failure → error frame + close 4401.
3. Registration assigns a monotonic epoch (in-memory; reset on
   service restart). A new hello for the same device_id EVICTS
   the old connection immediately (revoke frame + close) — never
   a silent takeover.

## Liveness and limits
- Heartbeat 5 s, stale after 15 s (sweeper closes + fails pending
  commands). A background sweeper runs every 5 s.
- Max 32 pending commands per device; TTL 30 s default. An
  unanswered command expires as `command_timeout` with outcome
  `uncertain` — never reported as success.
- Close codes: 4401 unauthorized, 4403 revoked, 4408 hello
  timeout, 4409 evicted, 4410 heartbeat timeout, 1009 oversize.
  4401/4403 are non-retryable: the client must not reconnect.
  Other failures reconnect with exponential backoff + jitter,
  capped at 30 s.

## Command dispatch
`POST /api/v1/devices/{id}/commands` checks, in order: the
command kind exists; the control token carries the kind's scope
(observe kinds → desktop.observe, input kinds → desktop.input);
the device belongs to the caller (unknown/other → 404); the
device is Active; the device was granted that capability.
Offline → 409 `device_offline`. Timeout → 504 with outcome
`uncertain`. Revocation evicts the live connection at once
(revoke frame + close 4403); a revoked device cannot reconnect.

## Decisions
- **RelayApp factory.** Program.cs is now a thin wrapper around
  `RelayApp.CreateApp()` so tests can build the same app on a
  real Kestrel socket (TestServer cannot upgrade to WebSocket).
  Config stays `RelayService:DatabasePath` / `RelayService:Urls`.
- **Application parts.** Under a test host the entry assembly is
  the test assembly, so the service's compiled Razor views
  (`_LoginPartial`) were not discovered. `RelayApp` registers
  the service assembly as an application part explicitly.
  (Also: the Kestrel fixture points the content root at the
  service PROJECT dir, like WebApplicationFactory does.)
- **Test client redirects.** A real HttpClient follows the login
  302 to `/` (which 404s — the app has no home page). Kestrel
  test clients use `AllowAutoRedirect = false`, like TestServer.
- **The concurrent-claim loser has two correct phrasings**
  ("already claimed" vs "expired or already used") depending on
  whether it loaded before or after the winner committed; the
  test accepts both (T10 follow-up).
