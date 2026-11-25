# ADR 0004 — T10 device binding (pairing protocol + identity stores)

Date: 2026-11-13. Status: accepted (T10 PASS, 26/26 RelayService tests,
5/5 DeviceIdentity tests).

## Protocol (docs/04 §4.1)
1. The device generates a random P-256 keypair BEFORE login
   (`Bridge.DeviceIdentity.DeviceKeyPair`, ECDSA via BCL — no Ed25519
   in the BCL, so P-256 is the v1 choice) and a stable machine
   fingerprint (hash of machine GUID + OS, never a MAC alone).
2. `POST /api/v1/devices/pair/init` (device.connect token) creates a
   pairing: 8-char user code from an unambiguous alphabet
   (no 0/O/1/I/L), 5-minute expiry, a 32-byte initiator secret
   (SHA-256 hash stored; the secret itself is shown once, never
   stored/logged/returned again). The user code alone never
   completes a pairing.
3. The user claims it at `/devices/pair?code=…` (login required):
   the page shows account, device alias, requested capabilities,
   code, key/machine fingerprint prefixes, expiry — before any
   confirm. Confirm is an atomic conditional UPDATE
   (`WHERE Status='Pending'`): two simultaneous claims, exactly
   one wins.
4. `POST /api/v1/devices/pair/complete` (device.connect token +
   initiator secret) creates the device in one transaction: the
   device row insert + the Claimed→Completed flip are atomic, so
   a failed or duplicate complete leaves no half-bound device.
   The response returns the owner id, so a wrong-account claim is
   visible on the device side immediately.
5. Revocation is `DELETE /api/v1/devices/{id}` (owner + devices.read
   only). It marks the device Revoked; the WSS handshake (T11) is
   the enforcement point.

## Decisions
- **No EF rowversion for concurrency.** SQLite's `[Timestamp]`
  rowversion did not protect the concurrent-claim test (both
  commits succeeded). Claim/complete transitions are atomic
  conditional UPDATEs via `ExecuteSqlAsync` instead — and that
  call needs a SINGLE interpolated string: concatenation produces
  a plain `string`, not a `FormattableString` (compile error
  CS1503, found the hard way when stale binaries kept running).
- **Clone protection:** same key fingerprint on a different machine
  fingerprint while the old device is Active → rejected
  (`key_in_use_on_another_machine`). A full disk clone that also
  copies the machine GUID is indistinguishable server-side — the
  T11 single-connection rule (second connection evicts the first)
  is the backstop; documented, not solved.
- **Rate limits are per (client, machine fingerprint)** for init
  (5/hour), per pairing record for confirm/complete (10/10 min).
  A fleet of distinct devices is not throttled by each other.
- **Refused at init:** `files.workspace.write` and
  `developer.process` (need separate enablement); bad alias,
  bad fingerprint, unparsable public key.
- **Identity storage:** Windows = DPAPI CurrentUser file in
  %LOCALAPPDATA%\Bridge (`WindowsDeviceIdentityStore`, in the
  Windows agent project; compile-checked only, NOT_RUN here).
  Linux/dev = 0600 file store (`FileDeviceIdentityStore`) for
  protocol tests only — never the Windows path. No key material
  in config, logs, CLI args, registry plaintext, or chat.
- **SQLite quirk:** `ORDER BY DateTimeOffset` is unsupported —
  the device list sorts in memory.
- **v1 limit, stated plainly:** revoking a device does NOT revoke
  its live 10-minute access token; the handshake gate (T11) is
  the enforcement point, and the token dies within 10 minutes.
