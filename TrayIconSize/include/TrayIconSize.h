#pragma once
// TrayIconSize SDK: ask the shared TrayIconSize host to draw your own notification icons larger.
//
// The application still creates, updates and removes its icons with Shell_NotifyIcon; the SDK
// only attaches a display size to an icon identity (hWnd + uID, or guidItem with NIF_GUID).
// Registrations live as long as the calling process: they are dropped when it exits.
// Sizes are XAML DIPs (16 = standard, up to 32); DPI scaling is applied by the taskbar.
// Ship TrayIconSizeClient.dll, TrayIconSizeHost.exe and TrayIconSizeHook.dll side by side.

#include <windows.h>

#ifdef TRAYICONSIZE_CLIENT_EXPORTS
#define TRAYICONSIZE_API extern "C" __declspec(dllexport) HRESULT WINAPI
#else
#define TRAYICONSIZE_API extern "C" __declspec(dllimport) HRESULT WINAPI
#endif

// guid may be null; when given, hwnd/uid are ignored (NIF_GUID icon).
// E_ACCESSDENIED: hwnd belongs to another process. E_INVALIDARG: size outside 16..32.
// HRESULT_FROM_WIN32(ERROR_OLD_WIN_VERSION): Windows build without the Windows 11 XAML tray.
TRAYICONSIZE_API TrayIconSize_SetIconSize(HWND hwnd, UINT uid, const GUID* guid, UINT sizeDip);
TRAYICONSIZE_API TrayIconSize_RemoveIcon(HWND hwnd, UINT uid, const GUID* guid);

#ifdef __cplusplus
namespace TrayIconSize
{
    inline HRESULT RegisterIcon(const GUID& iconGuid, UINT sizeDip)
    {
        return TrayIconSize_SetIconSize(nullptr, 0, &iconGuid, sizeDip);
    }
    inline HRESULT SetIconSize(const GUID& iconGuid, UINT sizeDip) { return RegisterIcon(iconGuid, sizeDip); }
    inline HRESULT UnregisterIcon(const GUID& iconGuid) { return TrayIconSize_RemoveIcon(nullptr, 0, &iconGuid); }

    inline HRESULT RegisterIcon(HWND hwnd, UINT uid, UINT sizeDip)
    {
        return TrayIconSize_SetIconSize(hwnd, uid, nullptr, sizeDip);
    }
    inline HRESULT SetIconSize(HWND hwnd, UINT uid, UINT sizeDip) { return RegisterIcon(hwnd, uid, sizeDip); }
    inline HRESULT UnregisterIcon(HWND hwnd, UINT uid) { return TrayIconSize_RemoveIcon(hwnd, uid, nullptr); }
}
#endif
