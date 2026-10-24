#pragma once
// Internal protocol shared by TrayIconSizeClient.dll, TrayIconSizeHost.exe and TrayIconSizeHook.dll.

#include <windows.h>
#include <cstdint>
#include <string>

namespace TrayIconSize
{
    // v2: separate width and height. Kernel object names carry the version, so a v1 stack
    // (host and the hook it left in explorer.exe) never reads v2 data and vice versa.
    inline constexpr uint32_t ProtocolVersion = 2;
    inline constexpr uint32_t MaxRules = 256;
    inline constexpr uint32_t MinSizeDip = 16;
    inline constexpr uint32_t MaxHeightDip = 32;
    // Wider than tall leaves room for extra content next to the icon (e.g. a gauge).
    inline constexpr uint32_t MaxWidthDip = 48;

    inline bool ValidSize(uint32_t widthDip, uint32_t heightDip)
    {
        return widthDip >= MinSizeDip && widthDip <= MaxWidthDip &&
               heightDip >= MinSizeDip && heightDip <= MaxHeightDip;
    }

    // CLSID of the TAP object XAML Diagnostics creates inside explorer.exe.
    // {EA4891FD-1374-4F41-B972-FFE9070FF971}
    inline constexpr CLSID TapClsid =
        { 0xea4891fd, 0x1374, 0x4f41, { 0xb9, 0x72, 0xff, 0xe9, 0x07, 0x0f, 0xf9, 0x71 } };

    // XAML only answers endpoints named VisualDiagConnection<N>.
    inline constexpr wchar_t DiagnosticsEndpointFormat[] = L"VisualDiagConnection%d";

    // Kernel object names. Local\ is per session; pipes are machine-wide, so they carry the session id.
    inline constexpr wchar_t HostMutexName[] = L"Local\\TrayIconSize.v2.Host";
    inline constexpr wchar_t RulesMappingName[] = L"Local\\TrayIconSize.v2.Rules";
    inline constexpr wchar_t RulesLockName[] = L"Local\\TrayIconSize.v2.RulesLock";
    inline constexpr wchar_t RulesChangedName[] = L"Local\\TrayIconSize.v2.RulesChanged";
    // Created by the hook inside explorer.exe: "Local\TrayIconSize.v2.Hook.<explorer pid>".
    inline constexpr wchar_t HookMarkerFormat[] = L"Local\\TrayIconSize.v2.Hook.%lu";

    inline std::wstring PipeName()
    {
        DWORD session = 0;
        ProcessIdToSessionId(GetCurrentProcessId(), &session);
        return L"\\\\.\\pipe\\TrayIconSize.v2." + std::to_wstring(session);
    }

    // Identifies a notification icon exactly like NOTIFYICONIDENTIFIER: hWnd + uID, or guidItem.
    struct IconId
    {
        uint64_t hwnd;
        uint32_t uid;
        uint32_t hasGuid;
        GUID guid;

        bool operator==(const IconId& other) const
        {
            return hasGuid == other.hasGuid &&
                (hasGuid ? IsEqualGUID(guid, other.guid) : hwnd == other.hwnd && uid == other.uid);
        }
    };

    // Client -> host request; the host answers every request with a Reply.
    enum class Op : uint32_t { SetSize = 1, Remove = 2 };

    struct Request
    {
        uint32_t version;
        Op op;
        IconId icon;
        uint32_t widthDip;
        uint32_t heightDip;
    };

    struct Reply
    {
        uint32_t version;
        HRESULT result;
    };

    struct SharedRule
    {
        IconId icon;
        uint32_t widthDip;
        uint32_t heightDip;
    };

    // Rules published by the host for the hook; guarded by the RulesLock mutex.
    struct SharedRules
    {
        uint32_t version;
        uint32_t hostPid;
        uint32_t generation;
        uint32_t count;
        SharedRule rules[MaxRules];
    };
}
