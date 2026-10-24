# CoQuo design — 2026-10-23

## Goal

Show the **remaining account-wide weekly Codex allowance** as a readable numeral in the Windows notification area, with update latency on the order of one minute, without handling OpenAI credentials or abusing undocumented API endpoints.

## Considered approaches

1. **Official Codex App Server over stdio (selected).** Supported JSON-RPC read and notifications; Codex handles authentication. Requires Codex CLI installed and logged in. Child process adds some memory overhead.
2. Direct `GET /backend-api/wham/usage`. Fewer processes, but undocumented endpoint; would require reading OAuth access tokens from Codex credential storage, implementing refresh or failing when stale. Rejected as too intrusive.
3. Scrape UI or parse CLI terminal `/status` output. Fragile and operationally complex; rejected.

## Claude Code 5-hour indicator (added 2026-10-23)

Second, independently toggleable icon with the remaining **5-hour** Claude Code allowance. Claude Code has no headless RPC like `codex app-server` (no `usage` subcommand; `/usage` is interactive only), so the considered sources were:

1. **`GET api.anthropic.com/api/oauth/usage` with Claude Code's stored OAuth token (selected).** Accurate, works while Claude Code is closed as long as the token is valid. Undocumented; requires reading the access token (read-only, per request, never refreshed).
2. Status-line bridge (Claude Code passes `rate_limits` to a status-line script, which writes a file). No token access, but data only flows during an active session and occupies the user's `statusLine` slot. Rejected.

## Larger tray icons (added 2026-10-24)

Windows draws every notification icon at 16 DIP and `Shell_NotifyIcon` has no size parameter, so the optional *Icon size* setting goes through the separate [TrayIconSize](../TrayIconSize/README.md) component (C++: SDK DLL, per-session host, XAML Diagnostics hook in explorer.exe). CoQuo reads its WinForms `NotifyIcon` identity (`_window` handle, `_id`) by reflection and calls the SDK on a background queue; Standard size never starts the host.

## Architecture

- `TrayApplicationContext` (WinForms message loop): shared menu, timer, settings, the Codex child process; owns two `QuotaIndicator`s.
- `QuotaIndicator`: one tray icon and its menu rows for one source — in-memory last accepted reading, stale state, polling schedule, backoff, manual-refresh throttling. `SourceUnavailableException` (nothing sent, e.g. expired token) retries at the normal pace without escalating backoff.
- `CodexAppServerClient`: owns one child `codex app-server` process, implements newline-delimited JSON-RPC request correlation and notification handling; stdout/stderr drained, no credential access. Stopped while the Codex icon is hidden.
- `UsageParser`: selects `rateLimitsByLimitId.codex` (or legacy `rateLimits`) and finds the **300-minute** and **10080-minute** windows by duration, not primary/secondary position, into one `QuotaReading`. Ignores other buckets and durations. Notifications are merged over the previous reading, since they may carry one window only.
- Displayed window (added 2026-10-24): each indicator shows one window of its last `QuotaReading` (`CodexDisplay` / `ClaudeDisplay` settings, or hidden). Switching only redraws; a window absent from the last reading shows as "not reported" and is grayed in the menu.
- `ClaudeUsageClient`: re-reads `claudeAiOauth.accessToken` from `.credentials.json` (`CLAUDE_CONFIG_DIR` honored) for each request, refuses locally when `expiresAt` has passed, reports only HTTP status codes in errors.
- `ClaudeUsageParser`: `five_hour` and `seven_day` `utilization` (percent used; >100 clamps to 0 remaining) and optional ISO `resets_at`.
- `Settings`: persist refresh interval and per-icon visibility (at least one stays visible). Startup registration optional via current-user Run key.
- `TrayIconPainter`: same silhouette for both, traffic-light outline, gray for stale; the fill identifies the source (dark = Codex, terracotta with a dark gap ring = Claude). Digits in Bahnschrift SemiBold Condensed (Segoe UI Bold fallback) with a dark halo.

## Networking and lifecycle

- At launch, start the child process, perform `initialize` -> `initialized` handshake, call `account/rateLimits/read`.
- Handle optional `account/rateLimits/updated` events when they contain a seven-day Codex window. These notifications are **local process events**, not a promise of remote cross-device push.
- Claude: poll the usage endpoint at the configured interval, but never more often than every 2 minutes.
- Poll at most once per configured 1/2/5/10 minutes (default 1 minute), avoid simultaneous requests and manual bursts (<15 seconds), exponential backoff on errors, >=15 minutes for recognizable 429 responses.
- A reset timestamp is informational: do **not** optimistically set 100% after it passes. Show stale until the authoritative reading confirms a new value.
- Graceful shutdown hides icon and terminates only the owned app-server process.

## Error handling

- Codex CLI absent: gray '?' icon, menu error; retry later.
- API key mode / logout / missing weekly window: do not invent data; keep last reading as stale.
- Auth/account changes, server restarts: regular read refreshes the figure after reconnection.
- 429 / network failures: backoff and fail visibly, never retry in a rapid loop.

## Test strategy

- Parser tests: weekly in primary/secondary, bucket selection, missing week, malformed percentages, optional reset, notification-only message.
- Windows integration checks: .exe launch, tray painting at 100/150/200% DPI, reading an actual logged-in Codex, cmd shim vs native binary, sleep/wake, manual refresh throttling, network loss, app-server crash, reset boundary and account switch, startup registry path, exit cleanup.

## Boundaries

A compiled Windows binary and live integration proof require a Windows build/test environment with the Codex CLI installed. Source package alone does not prove operation. This project intentionally avoids browser-cookie inspection, direct bearer-token access and API-key probes.
