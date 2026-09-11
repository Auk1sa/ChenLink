# Changelog

All notable changes to ChenLink are documented in this file.

## V1.1.0 - 2026-09-11

### Added

- System tray menu with show/exit actions and double-click restore.
- Configurable close behavior: ask every time, minimize to tray, or exit.

### Changed

- Replaced the WinForms tray component with H.NotifyIcon to reduce the single-file release from about 124 MB to about 90 MB.
- Updated application version to 1.1.0.

### Fixed

- Fixed a crash when restoring or exiting from the tray by dispatching tray callbacks to the UI thread.

## V1 - 2026-09-10

### Added

- First public release.
- Room-based peer-to-peer multiplayer connection.
- LAN direct connection, TCP hole punching, and relay fallback.
- Optional EasyTier virtual network mode for users without a public IP address.
- Single-file, self-contained Windows x64 release package.
- GitHub Pages project showcase.
