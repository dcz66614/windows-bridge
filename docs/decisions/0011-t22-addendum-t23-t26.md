# ADR 0011 — T22 addendum (file.move / file.mkdir), T23 workflows, T26 update verification

Date: 2026-11-13. Status: implemented; Windows execution NOT_RUN.

## File API additions (T22 addendum)
- `file.move` (`RestrictedFileAccess.Move` + `FileBackend.Move`):
  rename within the authorized root, both paths resolved and
  re-checked, destination must not exist (no silent overwrite),
  same-volume move is atomic on NTFS. No delete in v1.
- `file.mkdir` (`RestrictedFileAccess.Mkdir` + `FileBackend.Mkdir`):
  create a directory within the root, parents included,
  idempotent; fails when the path exists as a file. Added
  because workflow B needs subdirectories and the file API had
  no way to create them.
- Both are `desktop.input` operations (relay scope map,
  `lease_mode` / `consent_scope` enforcement applies), ride the
  command channel, and are in the OpenAPI + Skill rules.
- Tests: DesktopCore 55/55 (was 44; +2 move, +1 mkdir, +8
  update-manifest — see below); the relay routing test now also
  dispatches `file.move`; RelayService 56/56.

## T23 workflows (definitions only, NOT_RUN)
- `workflows/A-notepad.md`: create a file with random Chinese +
  English text via `file.write`, read back + SHA-256, open in
  Notepad via UI input, append a line, save, read back + SHA-256.
- `workflows/B-organize.md`: create 3 fictional texts, `file.mkdir`
  three subdirectories, `file.move` by rule, conflict drill
  (move onto an existing name must fail `already_exists` with
  the original untouched), list + read-back verification.
- Both: 10 consecutive runs, randomized names/content, per-run
  evidence (action count, authorization count, duration, human
  interventions). Failures are kept and root-caused, never
  dropped. Scope fallback per the plan (file-bridge-only /
  observe-only) is recorded as valid delivery, not failure.
- Execution needs real consumer Muse + real Windows; nothing
  here claims a run happened.

## T26 update verification (library + tests; wiring waits for the update origin)
- `src/Bridge.DesktopCore/UpdateManifest.cs`:
  - Manifest {version, package_sha256, package_size,
    download_url, issued_at, signature_b64}; signature is
    ECDSA P-256 + SHA-256 (IEEE P1363, base64) over a FIXED
    field-order canonical JSON. Signature verification and
    package-hash verification are separate methods.
  - Fixed update source: the URL must be HTTPS and its host
    must equal the configured update origin exactly.
  - Anti-downgrade via `InstallLayout.CompareVersions`.
  - `DownloadPackageAsync` follows same-host HTTPS redirects
    only (max 5); cross-host redirects are rejected. CALLER
    REQUIREMENT: the HttpClient must be built with
    AllowAutoRedirect = false, or the handler follows redirects
    before this code can inspect them.
  - The public key is a parameter. The release pipeline (T28)
    generates the keypair offline, ships the public key with the
    installer, and never puts the private key in the repo.
  - No update is applied from an unverified remote script;
    `Installer.Upgrade` already keeps the old version runnable
    on failure.
- Tests: 8 new (valid round-trip, tampered version/hash,
  wrong key, HTTP URL, wrong host, size/hash mismatch,
  downgrade, same-host redirect vs cross-host redirect).
  One test bug found during the run (the stub redirected
  forever on the `?v=2` URL); fixed in the stub, not in the
  production code.

## Verification this round
- DesktopCore 55/55; RelayService 56/56; full solution 0
  warnings 0 errors. win-x64 publish + ZIP refreshed
  (56M, sha256 12682c4f…).
