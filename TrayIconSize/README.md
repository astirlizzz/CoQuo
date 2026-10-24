# TrayIconSize

Lets applications display **their own** Windows 11 notification-area icons larger than the standard 16×16 DIP (up to 32 high and 48 wide), without touching other icons. One shared host and one hook in Explorer serve every application.

```
App A ─┐                              ┌─ explorer.exe
App B ─┼─ TrayIconSizeClient.dll ──►  TrayIconSizeHost.exe ──► TrayIconSizeHook.dll
App C ─┘   (named pipe per session)   (shared memory rules)    (XAML watcher)
```

## API

```cpp
#include "TrayIconSize.h"   // link TrayIconSizeClient.lib, ship the three binaries side by side

TrayIconSize::RegisterIcon(hwnd, uid, 24);   // NOTIFYICONDATA hWnd + uID
TrayIconSize::RegisterIcon(iconGuid, 24);    // or guidItem (NIF_GUID)
TrayIconSize::SetIconSize(hwnd, uid, 28);
TrayIconSize::SetIconSize(hwnd, uid, 30, 24); // width × height: the square icon is stretched to fill
TrayIconSize::UnregisterIcon(hwnd, uid);
```

C ABI (for P/Invoke): `TrayIconSize_SetIconSizeEx(HWND, UINT uid, const GUID*, UINT widthDip, UINT heightDip)`, `TrayIconSize_SetIconSize(HWND, UINT uid, const GUID*, UINT sizeDip)` (square), `TrayIconSize_RemoveIcon(HWND, UINT uid, const GUID*)`. Returns `E_ACCESSDENIED` for a window of another process, `E_INVALIDARG` for a height outside 16..32 or a width outside 16..48, `ERROR_OLD_WIN_VERSION` before Windows 11.

A notification icon is always square. For a non-square size, draw the picture at the target aspect ratio squeezed into the square `HICON` (e.g. scale x by height/width); Explorer stretches it back.

The application keeps creating and updating its icon with `Shell_NotifyIcon` and re-adds it after `TaskbarCreated` as usual. Registrations live as long as the calling process.

## How it works

- **Identification.** Explorer does not expose the shell identity of `SystemTray.NotifyIconView` (its `DataContext`, `SystemTray.IconViewModel`, has only an undocumented interface). Instead the hook asks the shell for each registered icon's screen rectangle with the public `Shell_NotifyIconGetRect` and picks the `NotifyIconView` laid out at exactly that rectangle (±1 px). Two candidates, no candidate, or an icon in the overflow flyout → nothing is changed.
- **Resizing.** `SystemTray.ImageIconContent > Grid#ContainerGrid > Image` gets `Width`/`Height` = size, and `Stretch=Fill` when width ≠ height; the cell grows by itself (`MinWidth=32`). Explorer builds the bitmap from the application's `HICON`, so a large icon stays sharp. Original values (including `Stretch`) are restored when the rule goes away, unless someone else changed them meanwhile. A container recycled for another icon (`DataContextChanged`) is restored immediately.
- **Hook.** `TrayIconSizeHook.dll` is a XAML Diagnostics TAP loaded with `InitializeXamlDiagnosticsEx` (endpoint `VisualDiagConnection1`) and watches the tree via `IVisualTreeService3::AdviseVisualTreeChange`. A worker thread re-matches on rule changes, on tray tree changes and every 2 s; XAML is only touched on the taskbar's UI thread. It creates `Local\TrayIconSize.v2.Hook.<pid>` so it is never injected twice.
- **Host.** Single instance per session (`Local\TrayIconSize.v2.Host`). Verifies that an `hWnd` belongs to the calling process (`GetNamedPipeClientProcessId`), drops a client's rules when its pipe closes, publishes rules in `Local\TrayIconSize.v2.Rules`, injects only when there is at least one rule, re-injects after `TaskbarCreated`, and exits 10 s after the last client. The hook ignores rules of a host that is no longer running.
- **Client.** Starts `TrayIconSizeHost.exe` from its own directory when the pipe is missing; if the host dies it reconnects within 2 s and replays its registrations.
- Explorer keeps a loaded hook locked, so the host injects a copy from `%LOCALAPPDATA%\TrayIconSize` and deletes unused copies. Diagnostic log: `%LOCALAPPDATA%\TrayIconSize\hook.log` (attach / match / release only, capped at 1 MB).

- **Protocol versions.** All kernel object names carry the protocol version (v2 added width × height). A v1 hook left in Explorer by an older build sees its host gone, restores its icons and idles; the v2 host injects its own hook next to it (verified: both load through `VisualDiagConnection1`), so upgrading needs no Explorer restart. Stop the old applications before starting new ones, so the old hook has restored the icons before the new one records their original size.

## Build

`./build.ps1` (Visual Studio with C++ and the Windows 10/11 SDK; uses VS-bundled CMake). Output: `build/Release`.

## Verified (Windows 11 24H2, build 26100, 100% DPI, single monitor)

Two icons of one app enlarged to 24 and to 30×24, others untouched; v2 hook injected next to a v1 one; foreign `hWnd` rejected; overflow icon left alone; host killed → icons restored, client restarted the host, icons enlarged again; Explorer restarted → re-injected and re-matched; app exited → rules dropped, host exited; app restarted → no second injection.

## Limits

- Relies on Explorer internals (XAML class/element names, XAML Diagnostics in explorer.exe) that are not a stable Windows API; any mismatch fails open.
- Not yet tested at other DPI scales, with several monitors, or alongside Windhawk / other XAML Diagnostics consumers.
- Icons in the overflow flyout keep the standard size.
- A loaded hook stays in Explorer until it restarts (it idles without rules).
