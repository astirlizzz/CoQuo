# CoQuo — Codex weekly remaining % for Windows

Minimal, Windows 11 notification-area-only monitor (WinForms + .NET 8).

## Behavior

- Shows remaining weekly Codex allowance as a large numeral on a green / amber / red icon.
- Reads account-wide `codex` window of **10080 minutes** (7 days), regardless of whether it is `primary` or `secondary`.
- Tooltip and right-click menu contain percentage, reset time in local timezone, last update/error.
- Changes icon to gray on errors, stale data, or after a scheduled reset until upstream confirms the new figure. Never fabricates 100% when a timer expires.
- Receives `account/rateLimits/updated` from the local Codex app-server; also polls `account/rateLimits/read` every **60 seconds by default** (configurable 1, 2, 5, 10 minutes).
- Backoff on errors up to 15 minutes; manual refresh throttled to 15 seconds. No periodic timer requests beyond the configured interval.
- No browser scraping, API key, OAuth token access, saved credentials, admin rights, or server-side changes.

## Prerequisites

- Windows 11 x64 (Windows 10 may also work).
- Existing **Codex CLI** installed and logged into ChatGPT, accessible as `codex` in PATH (either `codex.exe` or `codex.cmd`). You can override via `COQUO_CODEX_PATH` pointing to the desired executable/shim.
- .NET 8 SDK to compile. The published self-contained exe does not need .NET installed on the target computer.

## Build

From PowerShell in this directory:

```powershell
./build.ps1
```

Runs the dependency-free parser tests, then produces `dist/CoQuo.exe` as a self-contained single-file Windows executable. The binary **is not signed**; do not bypass antivirus warnings blindly. You can also run it from source with `dotnet run --project CoQuo/CoQuo.csproj`.

## UI

- Single-click tray icon: standard Windows behavior; hover for usage.
- Right-click: current weekly remaining percent, reset, last update/error; refresh; polling interval; start with Windows; exit.
- Double-click: refresh (with 15-second minimum between actual requests).
- Startup uses the current user's `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run` key; no UAC. Recommended to enable after publishing, not while using `dotnet run`.
- Refresh settings saved in `%LOCALAPPDATA%\\CoQuo\\settings.json`.

## Security & rate-limit policy

Uses `codex app-server` child process on local JSONL stdio. The app-server owns OpenAI auth and token refresh. CoQuo does not open `~/.codex/auth.json`, never copies browser cookies, and makes no direct OpenAI HTTPS requests.

OpenAI has not published a guaranteed safe polling threshold specifically for this read. A 60-second interval is a conservative application choice **not a guarantee against throttling or enforcement**. Avoid configuring much faster reads; honor server throttling/backoff. Notifications from the local app-server do not necessarily arrive for Codex activity in another process/device; the timer is authoritative fallback. Usage reporting may itself lag upstream.

### Protocol

`codex app-server` starts over stdio, accepts `initialize`, then `initialized`, then `account/rateLimits/read`. `account/rateLimits/updated` can arrive asynchronously. Uses an account-wide bucket identified as `codex`, not an unrelated model-specific bucket.

Official reference: https://learn.chatgpt.com/docs/app-server

## Testing limitations

The project was authored in a Linux sandbox where the .NET SDK and Windows tray UI were **not available**. It has not been compiled or integration-tested on Windows by the author. Run `build.ps1` and inspect the output before relying on it. Parser-only test scenarios are included, but they are likewise not claimed as executed.

## Possible next changes

- Monitor actual session activity to dynamically slow polling when inactive.
- Add proper logging stripped of secrets and the option to export diagnostics.
- Accessibility enhancement: high contrast 16px icon testing at 100–200% DPI.
