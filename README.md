# CoQuo — Codex / Claude Code remaining limits for Windows

Minimal, Windows 11 notification-area-only monitor (WinForms + .NET 8).

## Behavior

- Two independent tray icons. For each, the menu (*Codex ▸* / *Claude ▸*) picks the **5-hour limit**, the **weekly limit**, or **Hide** (the last visible icon cannot be hidden). Defaults: Codex weekly, Claude 5-hour.
  - **Codex** — dark icon. Reads the account-wide `codex` bucket and recognizes windows by duration (**300** / **10080** minutes), regardless of whether they are `primary` or `secondary`.
  - **Claude Code** — terracotta icon; `five_hour` and `seven_day` windows.
- Every reading keeps both windows, so switching between them redraws without a new request. A window the last response did not contain (e.g. no 5-hour limit on the Codex plan) is grayed out in the menu.
- Both show a large numeral (a check mark at 100%); the outline is green / amber / red by remaining percent.
- With an enlarged icon size the icon is 25% wider and has a **time bar** on the right: the share of the limit window still ahead of the reset (full before the window has started). Its color is the pace (experimental): green while the remaining allowance is no more than 10 points below the remaining time, amber up to 25 points, red beyond that — at this rate the allowance runs out before the reset.
- Tooltip and right-click menu contain percentage, reset time in local timezone, last update/error.
- Changes icon to gray on errors, stale data, or after a scheduled reset until upstream confirms the new figure. Never fabricates 100% when a timer expires.
- Codex: receives `account/rateLimits/updated` from the local Codex app-server; also polls `account/rateLimits/read` every **60 seconds by default** (configurable 1, 2, 5, 10 minutes). Hiding the Codex icon stops the app-server child process.
- Claude: polls the Claude Code usage endpoint at the same interval but **never more often than every 2 minutes**.
- Backoff on errors up to 15 minutes; manual refresh throttled to 15 seconds. No periodic timer requests beyond the configured interval.
- No browser scraping, API key, saved credentials, admin rights, or server-side changes. The only token access is the read-only Claude Code token described below.

## Prerequisites

- Windows 11 x64 (Windows 10 may also work).
- For Codex: existing **Codex CLI** installed and logged into ChatGPT, accessible as `codex` in PATH (either `codex.exe` or `codex.cmd`). You can override via `COQUO_CODEX_PATH` pointing to the desired executable/shim.
- For Claude: **Claude Code** logged in with a Claude subscription (Pro/Max/Team), so `~/.claude/.credentials.json` exists. `CLAUDE_CONFIG_DIR` is honored.
- .NET 8 SDK to compile. The published self-contained exe does not need .NET installed on the target computer.

## Build

From PowerShell in this directory:

```powershell
./build.ps1
```

Builds the native TrayIconSize component (needs Visual Studio with C++ tools and the Windows SDK), runs the dependency-free parser tests, then produces `dist/CoQuo.exe` as a self-contained single-file Windows executable plus the three TrayIconSize binaries; copy all four files when installing. The binary **is not signed**; do not bypass antivirus warnings blindly. You can also run it from source with `dotnet run --project CoQuo/CoQuo.csproj`.

## UI

- Single-click tray icon: standard Windows behavior; hover for usage.
- Right-click (either icon): remaining percent of each shown limit, reset time, last update/error; refresh; polling interval; limit to show per service; icon size; start with Windows; exit.
- **Icon size** (Standard / 20 / 24 / 28 / 32 px high, 25% wider for the time bar; Standard has no bar): draws CoQuo's icons larger than the Windows 16 px through the bundled [TrayIconSize](TrayIconSize/README.md) component, which modifies Explorer's taskbar XAML. Standard does not load anything into Explorer. Needs `TrayIconSizeClient.dll`, `TrayIconSizeHost.exe` and `TrayIconSizeHook.dll` next to `CoQuo.exe`.
- Double-click an icon: refresh that source (with 15-second minimum between actual requests).
- Startup uses the current user's `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run` key; no UAC. Recommended to enable after publishing, not while using `dotnet run`.
- Refresh interval, shown limit (`CodexDisplay`, `ClaudeDisplay`: `FiveHour` / `Weekly` / `Hidden`; the older `ShowCodex`/`ShowClaude` flags are migrated) and `IconSize` saved in `%LOCALAPPDATA%\\CoQuo\\settings.json`.

## Security & rate-limit policy

Uses `codex app-server` child process on local JSONL stdio. The app-server owns OpenAI auth and token refresh. CoQuo does not open `~/.codex/auth.json`, never copies browser cookies, and makes no direct OpenAI HTTPS requests.

### Claude Code

Claude Code has no local server equivalent to `codex app-server`, so CoQuo calls `GET https://api.anthropic.com/api/oauth/usage` (header `anthropic-beta: oauth-2025-04-20`) and reads `five_hour.utilization` / `five_hour.resets_at`. This endpoint is **undocumented** and may change.

Token handling:

- `~/.claude/.credentials.json` is re-read before every request; `claudeAiOauth.accessToken` lives only in a local variable for the duration of that request and is sent only to `api.anthropic.com`.
- The token is never written, logged, shown in the UI, or included in error text. The refresh token is never used and CoQuo never refreshes the login — Claude Code owns it.
- If the token has expired (Claude Code refreshes it while running) or is rejected, the icon turns gray with "open Claude Code"; no request is sent for an expired token.

### Rate limits

OpenAI has not published a guaranteed safe polling threshold specifically for this read. A 60-second interval is a conservative application choice **not a guarantee against throttling or enforcement**. Avoid configuring much faster reads; honor server throttling/backoff. Notifications from the local app-server do not necessarily arrive for Codex activity in another process/device; the timer is authoritative fallback. Usage reporting may itself lag upstream.

Anthropic has not published a polling budget for the Claude usage endpoint either, and it answers 429 to aggressive polling; CoQuo therefore polls it at most every 2 minutes and applies the same backoff and 15-minute 429 pause.

### Protocol

`codex app-server` starts over stdio, accepts `initialize`, then `initialized`, then `account/rateLimits/read`. `account/rateLimits/updated` can arrive asynchronously. Uses an account-wide bucket identified as `codex`, not an unrelated model-specific bucket.

Official reference: https://learn.chatgpt.com/docs/app-server

## Testing limitations

The project was authored in a Linux sandbox where the .NET SDK and Windows tray UI were **not available**. It has not been compiled or integration-tested on Windows by the author. Run `build.ps1` and inspect the output before relying on it. Parser-only test scenarios are included, but they are likewise not claimed as executed.

## Possible next changes

- Claude 7-day windows (`seven_day`, `seven_day_opus`) as optional extra indicators.
- Monitor actual session activity to dynamically slow polling when inactive.
- Add proper logging stripped of secrets and the option to export diagnostics.
- Accessibility enhancement: high contrast 16px icon testing at 100–200% DPI.
