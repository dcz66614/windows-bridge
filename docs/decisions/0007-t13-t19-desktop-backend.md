# ADR 0007 — T13–T19 desktop backend (code complete, Windows NOT_RUN)

Date: 2026-11-13. Status: code complete; all real-machine gates
NOT_RUN (no Windows run yet — the agent cross-compiles on Linux
and publishes win-x64 self-contained, but screenshot/UIA/
SendInput have never executed on a real desktop).

## Layout
- `Bridge.DesktopCore` (net10.0, pure, tested on Linux 24/24):
  `CoordinateMapper` (image→native floor mapping, native→
  SendInput 0..65535 over the virtual desktop, negative origins),
  `InputValidation` (click/type/key payload rules, the
  contract_lab key allowlist verbatim, envelope time rules,
  PNG artifact rules), `CommandJournal` (SQLite WAL; accepted
  persisted before any side effect, executing, then exactly one
  terminal state; reopen marks interrupted commands uncertain;
  same id + different binding → conflict), `GenerationTracker`
  + `ObservationConstraints` (30 s freshness, generations).
- `Bridge.Win32` (net10.0-windows, P/Invoke only): monitor
  enumeration + DPI, virtual desktop, interactive/locked state,
  BitBlt screenshot → PNG (halved until ≤4 MiB, actual size
  reported), SendInput click (absolute + VIRTUALDESK), Unicode
  typing (KEYEVENTF_UNICODE, chunked), key combos (down in
  order, up in reverse), modifier release.
- `Bridge.DesktopWorker` (net10.0-windows exe): the isolated
  worker process. One MTA thread runs FlaUI.UIA3 (bounded
  snapshot: foreground window tree, depth 4, max 200 nodes,
  truncated strings) and the screenshot; it speaks JSON lines
  over a current-user-only named pipe (WorldSid denied).
- `Bridge.WindowsAgent`: `WindowsDesktopBackend`
  (IDesktopBackend) + `DesktopWorkerHost` + `LocalControlState`
  + tray wiring.

## Behavior (docs/04 §7–§8)
- Probe (T13): monitors/DPI/virtual desktop, interactive
  session (OpenInputDesktop), unlocked (foreground window
  exists), capabilities; degraded when the worker crashed
  repeatedly.
- Observe (T15): worker screenshot + UIA, artifact SHA-256
  verified against the metadata, observation bound to
  lease/epoch/generations, 30 s expiry, one accepted input
  consumes it.
- Input (T16/T17): validation BEFORE the journal (rejections
  do not consume the sequence); journal accept (replay returns
  the stored receipt); local pause/epoch re-checked
  immediately before the side effect; serial input queue;
  mid-input failure → uncertain, never a silent success and
  never an automatic replay.
- Worker (T14/T18): per-request timeout (probe 5 s, observe
  10 s), kill + restart once on crash/hang, max 3 restarts per
  minute, then degraded until the agent restarts.
- Local stop (T19): the tray pause latches in
  `LocalControlState` (bumps the epoch, releases held modifier
  keys at once); only the local toggle resumes it. The backend
  releases only keys IT held — no global emergency-stop claim.

## What is NOT done
- No Windows run: nothing here has executed on a real
  desktop. All T13–T19 gates and the 72 acceptance cases that
  need a desktop stay NOT_RUN until Sam's Windows is online
  and he is present for the local allow step.
- No transport yet: the backend is not connected to the relay
  channel (T20) or the Muse Skill (T21); the tray shows
  "not bound".
- The worker exe must sit next to the agent exe (the publish
  output `artifacts/win-x64/` has both). No installer, no
  signature, no auto-update yet.
