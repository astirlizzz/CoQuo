// TrayIconSizeHost: one instance per user session.
//
// Accepts icon-size registrations from applications over a named pipe, publishes them
// to shared memory for TrayIconSizeHook.dll, and makes sure the taskbar's explorer.exe
// carries the hook exactly once (again after Explorer restarts). Registrations are
// owned by their pipe connection: when an application exits, its rules disappear.
// The host exits by itself a few seconds after the last client disconnects.

#include <windows.h>
#include <xamlOM.h>

#include <algorithm>
#include <map>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "Protocol.h"

using namespace TrayIconSize;

namespace
{
    constexpr UINT WM_ENSURE_HOOK = WM_APP + 1;
    constexpr UINT WM_CLIENTS_CHANGED = WM_APP + 2;
    constexpr UINT_PTR RetryTimer = 1;
    constexpr UINT_PTR ExitTimer = 2;
    constexpr int MaxInjectAttempts = 30;

    HWND g_window = nullptr;
    UINT g_taskbarCreated = 0;
    bool g_supported = false;

    HANDLE g_rulesLock = nullptr;
    HANDLE g_rulesChanged = nullptr;
    SharedRules* g_shared = nullptr;

    std::mutex g_clientsLock;
    std::map<uint64_t, std::vector<SharedRule>> g_clients; // by connection id
    uint64_t g_nextClient = 1;

    DWORD g_hookedPid = 0;
    int g_injectAttempts = 0;
    bool g_retrying = false;

    std::wstring ModuleDirectory()
    {
        wchar_t path[MAX_PATH];
        DWORD n = GetModuleFileNameW(nullptr, path, MAX_PATH);
        if (n == 0 || n >= MAX_PATH) return {};
        std::wstring result(path, n);
        return result.substr(0, result.find_last_of(L'\\'));
    }

    std::wstring StagingDirectory()
    {
        wchar_t base[MAX_PATH];
        DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", base, MAX_PATH);
        if (n == 0 || n >= MAX_PATH) return {};
        std::wstring dir = std::wstring(base) + L"\\TrayIconSize";
        CreateDirectoryW(dir.c_str(), nullptr);
        return dir;
    }

    // explorer.exe keeps a loaded hook locked until it restarts, so inject a per-build copy
    // and keep the installed file replaceable. Copies no longer loaded anywhere are removed.
    std::wstring StageHook()
    {
        std::wstring dir = StagingDirectory();
        std::wstring source = ModuleDirectory() + L"\\TrayIconSizeHook.dll";
        WIN32_FILE_ATTRIBUTE_DATA info{};
        if (dir.empty() || !GetFileAttributesExW(source.c_str(), GetFileExInfoStandard, &info)) return {};
        wchar_t name[64];
        swprintf_s(name, L"\\TrayIconSizeHook-%08lX%08lX.dll",
            info.ftLastWriteTime.dwHighDateTime, info.ftLastWriteTime.dwLowDateTime);
        std::wstring target = dir + name;

        WIN32_FIND_DATAW found;
        HANDLE search = FindFirstFileW((dir + L"\\TrayIconSizeHook-*.dll").c_str(), &found);
        if (search != INVALID_HANDLE_VALUE)
        {
            do
                if (dir + L"\\" + found.cFileName != target) DeleteFileW((dir + L"\\" + found.cFileName).c_str());
            while (FindNextFileW(search, &found));
            FindClose(search);
        }
        if (GetFileAttributesW(target.c_str()) == INVALID_FILE_ATTRIBUTES &&
            !CopyFileW(source.c_str(), target.c_str(), TRUE))
            return {};
        return target;
    }

    bool IsWindows11Tray()
    {
        using RtlGetVersionFn = LONG(WINAPI*)(OSVERSIONINFOW*);
        auto rtlGetVersion = reinterpret_cast<RtlGetVersionFn>(
            GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"));
        OSVERSIONINFOW version{ sizeof(version) };
        return rtlGetVersion && rtlGetVersion(&version) == 0 && version.dwMajorVersion == 10 &&
               version.dwBuildNumber >= 22000;
    }

    size_t RuleCount()
    {
        size_t count = 0;
        for (const auto& [id, rules] : g_clients) count += rules.size();
        return count;
    }

    // Caller holds g_clientsLock.
    void Publish()
    {
        DWORD wait = WaitForSingleObject(g_rulesLock, 5000);
        if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) return;
        uint32_t count = 0;
        for (const auto& [id, rules] : g_clients)
            for (const auto& rule : rules)
                if (count < MaxRules) g_shared->rules[count++] = rule;
        g_shared->count = count;
        g_shared->generation++;
        ReleaseMutex(g_rulesLock);
        SetEvent(g_rulesChanged);
    }

    HRESULT Handle(uint64_t client, DWORD clientPid, const Request& request)
    {
        if (request.version != ProtocolVersion) return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
        std::lock_guard lock(g_clientsLock);
        auto& rules = g_clients[client];
        auto existing = std::find_if(rules.begin(), rules.end(),
            [&](const SharedRule& r) { return r.icon == request.icon; });
        switch (request.op)
        {
        case Op::SetSize:
        {
            if (!g_supported) return HRESULT_FROM_WIN32(ERROR_OLD_WIN_VERSION);
            if (!ValidSize(request.widthDip, request.heightDip)) return E_INVALIDARG;
            if (!request.icon.hasGuid)
            {
                // An application may only resize icons of its own windows.
                DWORD owner = 0;
                HWND hwnd = reinterpret_cast<HWND>(request.icon.hwnd);
                if (!IsWindow(hwnd) || !GetWindowThreadProcessId(hwnd, &owner) || owner != clientPid)
                    return E_ACCESSDENIED;
            }
            if (existing != rules.end())
            {
                existing->widthDip = request.widthDip;
                existing->heightDip = request.heightDip;
            }
            else if (RuleCount() >= MaxRules) return E_OUTOFMEMORY;
            else rules.push_back({ request.icon, request.widthDip, request.heightDip });
            break;
        }
        case Op::Remove:
            if (existing != rules.end()) rules.erase(existing);
            break;
        default:
            return E_INVALIDARG;
        }
        Publish();
        PostMessageW(g_window, WM_ENSURE_HOOK, 0, 0);
        return S_OK;
    }

    void ClientThread(HANDLE pipe)
    {
        DWORD clientPid = 0;
        GetNamedPipeClientProcessId(pipe, &clientPid);
        uint64_t client;
        {
            std::lock_guard lock(g_clientsLock);
            client = g_nextClient++;
            g_clients[client];
        }
        PostMessageW(g_window, WM_CLIENTS_CHANGED, 0, 0);

        Request request;
        DWORD read = 0;
        while (ReadFile(pipe, &request, sizeof(request), &read, nullptr) && read == sizeof(request))
        {
            Reply reply{ ProtocolVersion, Handle(client, clientPid, request) };
            DWORD written = 0;
            if (!WriteFile(pipe, &reply, sizeof(reply), &written, nullptr)) break;
        }

        // Connection gone (exit, crash or protocol error): drop everything it registered.
        {
            std::lock_guard lock(g_clientsLock);
            g_clients.erase(client);
            Publish();
        }
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
        PostMessageW(g_window, WM_CLIENTS_CHANGED, 0, 0);
    }

    void PipeServerThread()
    {
        std::wstring name = PipeName();
        DWORD flags = PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE;
        for (;;)
        {
            HANDLE pipe = CreateNamedPipeW(name.c_str(), flags,
                PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                PIPE_UNLIMITED_INSTANCES, sizeof(Reply), sizeof(Request), 0, nullptr);
            if (pipe == INVALID_HANDLE_VALUE)
            {
                Sleep(1000);
                continue;
            }
            flags &= ~FILE_FLAG_FIRST_PIPE_INSTANCE;
            if (ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED)
                std::thread(ClientThread, pipe).detach();
            else
                CloseHandle(pipe);
        }
    }

    // Returns true when the current taskbar process carries the hook (or nothing is needed).
    bool EnsureHook()
    {
        {
            std::lock_guard lock(g_clientsLock);
            if (!g_supported || RuleCount() == 0) return true;
        }
        HWND taskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
        DWORD pid = 0;
        if (!taskbar || !GetWindowThreadProcessId(taskbar, &pid) || !pid) return false;
        if (pid == g_hookedPid) return true;

        // Never inject twice: another host instance may already have done it.
        wchar_t marker[96];
        swprintf_s(marker, HookMarkerFormat, pid);
        if (HANDLE existing = OpenEventW(SYNCHRONIZE, FALSE, marker))
        {
            CloseHandle(existing);
            g_hookedPid = pid;
            return true;
        }

        std::wstring hook = StageHook();
        HMODULE xaml = LoadLibraryExW(L"Windows.UI.Xaml.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        auto initialize = xaml ? reinterpret_cast<decltype(&InitializeXamlDiagnosticsEx)>(
            GetProcAddress(xaml, "InitializeXamlDiagnosticsEx")) : nullptr;
        if (hook.empty() || !initialize) return false;

        wchar_t endpoint[64];
        swprintf_s(endpoint, DiagnosticsEndpointFormat, 1);
        // ERROR_NOT_FOUND until the restarted taskbar has created its XAML; the caller retries.
        if (FAILED(initialize(endpoint, pid, L"", hook.c_str(), TapClsid, nullptr))) return false;
        g_hookedPid = pid;
        return true;
    }

    void ScheduleEnsureHook()
    {
        g_retrying = false;
        KillTimer(g_window, RetryTimer);
        if (EnsureHook()) g_injectAttempts = 0;
        else if (++g_injectAttempts <= MaxInjectAttempts)
        {
            g_retrying = true;
            SetTimer(g_window, RetryTimer, 1000, nullptr);
        }
    }

    LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (message == g_taskbarCreated && g_taskbarCreated)
        {
            // Explorer restarted: the old hook died with it.
            g_hookedPid = 0;
            g_injectAttempts = 0;
            ScheduleEnsureHook();
            return 0;
        }
        switch (message)
        {
        case WM_ENSURE_HOOK:
            // A pending retry already covers new registrations.
            if (!g_retrying)
            {
                g_injectAttempts = 0;
                ScheduleEnsureHook();
            }
            return 0;
        case WM_CLIENTS_CHANGED:
        {
            std::lock_guard lock(g_clientsLock);
            if (g_clients.empty()) SetTimer(hwnd, ExitTimer, 10000, nullptr);
            else KillTimer(hwnd, ExitTimer);
            return 0;
        }
        case WM_TIMER:
            if (wParam == RetryTimer) ScheduleEnsureHook();
            else if (wParam == ExitTimer)
            {
                std::lock_guard lock(g_clientsLock);
                if (g_clients.empty()) PostQuitMessage(0);
            }
            return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, LPWSTR, int)
{
    HANDLE single = CreateMutexW(nullptr, TRUE, HostMutexName);
    if (!single || GetLastError() == ERROR_ALREADY_EXISTS) return 0;
    g_supported = IsWindows11Tray();

    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(SharedRules),
        RulesMappingName);
    g_rulesLock = CreateMutexW(nullptr, FALSE, RulesLockName);
    g_rulesChanged = CreateEventW(nullptr, FALSE, FALSE, RulesChangedName);
    if (!mapping || !g_rulesLock || !g_rulesChanged) return 1;
    g_shared = static_cast<SharedRules*>(MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, sizeof(SharedRules)));
    if (!g_shared) return 1;
    {
        // The mapping may survive a previous host because the hook keeps it open.
        DWORD wait = WaitForSingleObject(g_rulesLock, 5000);
        if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED)
        {
            g_shared->version = ProtocolVersion;
            g_shared->hostPid = GetCurrentProcessId();
            g_shared->count = 0;
            g_shared->generation++;
            ReleaseMutex(g_rulesLock);
        }
        SetEvent(g_rulesChanged);
    }

    // TaskbarCreated is broadcast to top-level windows only, so this is a hidden popup.
    WNDCLASSW cls{};
    cls.lpfnWndProc = WindowProc;
    cls.hInstance = instance;
    cls.lpszClassName = L"TrayIconSizeHost";
    RegisterClassW(&cls);
    g_taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");
    g_window = CreateWindowExW(WS_EX_TOOLWINDOW, cls.lpszClassName, L"TrayIconSizeHost", WS_POPUP,
        0, 0, 0, 0, nullptr, nullptr, instance, nullptr);
    if (!g_window) return 1;
    // Allow the broadcast through UIPI even if Explorer runs at a higher integrity level.
    ChangeWindowMessageFilterEx(g_window, g_taskbarCreated, MSGFLT_ALLOW, nullptr);

    std::thread(PipeServerThread).detach();
    // Exit if nobody connects (e.g. a client that started us crashed right away).
    SetTimer(g_window, ExitTimer, 10000, nullptr);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    // Leave no stale rules behind; the hook restores icons when it sees the host gone too.
    {
        std::lock_guard lock(g_clientsLock);
        g_clients.clear();
        Publish();
    }
    return 0;
}
