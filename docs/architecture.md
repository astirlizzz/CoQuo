# CoQuo design — 2026-10-23

## Goal

Show the **remaining account-wide weekly Codex allowance** as a readable numeral in the Windows notification area, with update latency on the order of one minute, without handling OpenAI credentials or abusing undocumented API endpoints.

## Considered approaches

1. **Official Codex App Server over stdio (selected).** Supported JSON-RPC read and notifications; Codex handles authentication. Requires Codex CLI installed and logged in. Child process adds some memory overhead.
2. Direct `GET /backend-api/wham/usage`. Fewer processes, but undocumented endpoint; would require reading OAuth access tokens from Codex credential storage, implementing refresh or failing when stale. Rejected as too intrusive.
3. Scrape UI or parse CLI terminal `/status` output. Fragile and operationally complex; rejected.

## Architecture

- `TrayApplicationContext` (WinForms message loop): single icon/menu, timer, in-memory last accepted reading, stale state, refresh coordination.
- `CodexAppServerClient`: owns one child `codex app-server` process, implements newline-delimited JSON-RPC request correlation and notification handling; stdout/stderr drained, no credential access.
- `UsageParser`: selects `rateLimitsByLimitId.codex` (or legacy `rateLimits`) and finds the **10080-minute** window by duration, not primary/secondary position. Ignores other buckets. Returns no number when weekly data missing.
- `Settings`: persist only refresh interval. Startup registration optional via current-user Run key.
- `TrayIconPainter`: paint two- or three-digit remaining percent, traffic-light color, gray for stale.

## Networking and lifecycle

- At launch, start the child process, perform `initialize` -> `initialized` handshake, call `account/rateLimits/read`.
- Handle optional `account/rateLimits/updated` events when they contain a seven-day Codex window. These notifications are **local process events**, not a promise of remote cross-device push.
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
