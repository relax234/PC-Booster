# PC Booster Helper — Rust engine

Windows 10/11 x64 local companion for PC Booster AI.

## Current capabilities
- Reads CPU, RAM, Windows version and system drive usage.
- Binds only to 127.0.0.1:47831.
- Uses a random one-time pairing token printed at startup.
- Allows only a fixed allowlist of current-user registry tweaks.
- Saves original registry values in %LOCALAPPDATA%\\PCBooster\\rollback-journal.json.
- Supports rollback for every tweak applied by this helper.
- Does not accept PowerShell, CMD, shell commands, arbitrary registry paths, or scripts from the website.

## Allowed reversible tweaks
- Show file extensions.
- Show hidden files.
- Disable Windows transparency.
- Prefer performance over visual effects.
- Disable background Game DVR capture for the current user.

Changes are applied only after the user presses the corresponding action in the website. Review each description first. Some visual settings require restarting Explorer or signing out to fully refresh.

## Build
```powershell
cargo build --release --target x86_64-pc-windows-msvc
```
