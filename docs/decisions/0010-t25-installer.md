# ADR 0010 — T25 low-friction installer (self-installing agent)

Date: 2026-11-13. Status: implemented; Windows execution NOT_RUN.

## Design
The published agent exe IS the installer (the kit's "self-contained
directory + mature installer" v1, no MSI yet — a WiX/MSI build
needs a Windows build machine and is a later option, not a v1
blocker):
- Run from anywhere (e.g. Downloads after extracting the ZIP),
  the exe detects it is NOT inside the install directory and
  shows the setup dialog: what gets installed, where
  (%LOCALAPPDATA%\Bridge), two explicit choices — autostart
  (default on, visible) and the file-access root (default: none,
  file access closed).
- Install copies the whole payload directory into
  `app/<version>/`, writes a hash manifest (`install.json`),
  sets `app/current.txt`, writes `config.json`, creates a Start
  Menu shortcut (IShellLinkW, no COM reference) and, if chosen,
  a HKCU Run key. No admin, no LocalSystem service, no console.
- Upgrade requires a newer version string (same-version
  reinstall and downgrades are refused). The new version is
  launched and must write a healthy marker within the wait;
  on failure the shortcut/Run targets roll back to the previous
  version, the new dir is removed, and the old exe relaunches.
  The previous version dir is kept as rollback material.
- Uninstall (tray menu): removes Run key + shortcut, then a
  detached PowerShell waits for this process to exit and deletes
  the install directory.
- `src/Bridge.DesktopCore/InstallLayout.cs` holds the OS-neutral
  layout logic (paths, manifest, hash verify, residue check,
  version compare) — 6 new tests, DesktopCore 44/44. The
  Windows-only parts (registry, shortcut, process launch) live
  in `src/Bridge.WindowsAgent/Install/Installer.cs`.
- `AgentConfig` (`config.json`): version, autostart, file_root,
  relay_origin (for later), installed_at. Corrupt config falls
  back to defaults instead of crashing startup.
- Tray gains "文件访问…" (authorize/clear the file root —
  FileBackend roots swap live) and "开机自动启动" toggle and
  "卸载 Bridge…". Status dialog now shows version, file root,
  autostart state.
- `Program.Router` is the seam the device channel loop will call
  in T20/T23. The backend's data dir moved from
  `%LOCALAPPDATA%\Bridge` to `%LOCALAPPDATA%\Bridge\data`
  (pre-release, no installed base; journal path moves with it).

## Verification
- Full solution 0 warnings 0 errors; win-x64 publish refreshed;
  distribution ZIP `artifacts/Bridge-Windows-0.1.0-win-x64.zip`
  (293 files, 56M, sha256 11659ac9…).
- Full suite: 144 green (contracts 23, relay 16,
  deviceidentity 5, relayservice 56, desktopcore 44). One
  transient RelayService failure in the first full-suite run
  (55/56, test name not captured); the same suite passed 56/56
  on the immediate re-run and again with --no-build. Recorded
  here, not hidden; not reproducible, not chased further.
- The installer flow itself (setup dialog, shortcut, Run key,
  upgrade health check, uninstall) has NEVER run on Windows —
  the Windows-only code paths are compile-checked only.
  T29 (five-person install trial) is the real gate for this.

## Known limits
- No code signing yet (T28): executables and the ZIP must be
  signed in the release pipeline; SmartScreen reputation is not
  guaranteed by signing alone, and the installer never asks the
  user to disable system protection.
- No MSI/WiX installer yet; the self-installing exe is v1.
- Upgrade health check waits on a marker file the tray writes
  at startup; a tray that starts but immediately crashes after
  writing the marker would pass the check (accepted for v1;
  the marker is best-effort).
