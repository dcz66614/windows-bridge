# ADR 0008 — T20 connector definition + T21 operator Skill (drafts)

Date: 2026-11-13. Status: definitions complete; registration
BLOCKED on production deployment (no relay origin yet).

## What was written
- `connector/openapi.yaml`: the relay's public API exactly as
  implemented (T09–T12). Verified against the controllers:
  healthz, devices list/revoke, leases (create/acquire/renew/
  get/release), consent decide, commands (full 11-field
  envelope), command status query, pairing endpoints, OAuth
  endpoints. Endpoints from the architecture doc that are NOT
  implemented (device events, observations endpoint, artifact
  download, file API) are listed as planned, not as operations.
- `connector/bridge-connector.json`: the Custom Connector
  definition (DRAFT). `muse-control` public client, Auth Code +
  PKCE, scopes devices.read/desktop.observe/desktop.input. The
  device credential (`windows-agent`) is separate and must never
  go into the connector. The origin is the placeholder
  `{{RELAY_ORIGIN}}` until Sam sets the production deployment.
- `skills/operate-windows-bridge/SKILL.md`: the operator Skill,
  landed from the kit template and updated with the real API:
  device selection, lease flow, observe → act → verify loop,
  release, error handling, content-is-data rules.
- `skills/.../references/operation-rules.md`: the detailed
  rules (identity, leases, envelope, observation/coordinates,
  idempotency/uncertainty, local priority, file API note).
- `skills/.../references/qa-scenarios.md`: the kit's three QA
  records (A: uncertain timeout, B: malicious page + paused,
  C: ambiguous device/directory) as regression scenarios with
  the exact next steps and prohibitions.

## Decisions
- The OpenAPI lists ONLY implemented operations. Advertising
  an endpoint that does not exist would let a consumer Muse
  plan around a capability it cannot call.
- The connector stays DRAFT and unregistered until the relay
  is deployed over HTTPS. Registering it against a placeholder
  origin would be a false gate pass.
- The Skill's rules are written to match the implementation
  (not the architecture doc's aspirations): e.g. observations
  go through commands (there is no separate observations
  endpoint yet), PNGs return in command results (no artifact
  download route yet).
