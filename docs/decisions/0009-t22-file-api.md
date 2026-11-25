# ADR 0009 — T22 restricted file API + two authorization fixes

Date: 2026-11-13. Status: implemented; Windows execution NOT_RUN.

## File API
- `src/Bridge.DesktopCore/RestrictedFileAccess.cs` — lexical path
  validation with Windows semantics (UNC/ADS/device-path/`..`
  rejection, case-insensitive roots, relative paths resolve
  under the root) + bounded list (500) / read (1 MiB, SHA-256) /
  atomic write (temp file, verify, atomic publish; `create_only`,
  `expected_hash`). 14 new tests, DesktopCore 38/38.
- `src/Bridge.WindowsAgent/Backend/FileBackend.cs` — agent side:
  authorized roots from local config (default: none, closed
  until the user authorizes a directory in the tray); symlinks /
  reparse points resolved to the final target BEFORE the root
  check, so an in-root link pointing outside is rejected.
- `src/Bridge.WindowsAgent/Backend/AgentCommandRouter.cs` — the
  single entry point the future device channel loop calls:
  file.* → FileBackend, everything else → desktop backend.
- Relay: `file.list`/`file.read` → desktop.observe,
  `file.write` → desktop.input (CommandsController.RequiredScope);
  operations ride the existing command channel (no new HTTP
  routes). OpenAPI + Skill operation-rules updated; connector
  JSON no longer lists the file API as unimplemented.

## Two bugs found while wiring T22 (both fixed + regression-tested)
1. **Relay mapped `"input.type"` instead of `"input.type_text"`.**
   The canonical operation (golden vectors + docs/04 §105 +
   agent `OperationNames.TypeText`) is `input.type_text`. Every
   real typing command would have been rejected as
   `unknown_operation`. It survived because the T12 tests only
   ever dispatched `test.echo`. Regression test added
   (`TypeText_And_FileOps_RouteThroughCommandChannel`).
2. **Lease mode was never enforced.** An observe-mode lease
   could send `input.*` / `file.write` whenever the caller's
   token carried desktop.input, and a write lease could be
   acquired under a consent that only approved observation.
   Fixes: CommandsController rejects write operations under a
   non-write lease (403 `lease_mode`); LeaseManager.AcquireAsync
   rejects a mode the consent's scopes don't cover (409
   `consent_scope`). Tests:
   `ObserveLease_CannotInput_OrWriteFiles`,
   `WriteLease_RequiresInputScopeInConsent`.

## Decisions
- v1 file API: one authorized root per device in practice
  (the agent holds a list for the future; relative paths
  resolve under the first root). The tray UI exposes one root
  at a time; adding a second root is a later, explicit change.
- No delete / move / cross-drive operations in v1. "整理" (as
  in QA scenario C) that needs moves is out of scope until
  a move operation with the same guardrails is specified.
- RelayService tests now 56/56; full solution 0 warnings
  0 errors; win-x64 publish refreshed (146M, agent + worker).
