// TrayIconSizeClient: the SDK applications load. Talks to the per-session host over a
// named pipe, starting TrayIconSizeHost.exe from this DLL's directory when needed, and
// re-sends this process's registrations if the host is restarted.

#define TRAYICONSIZE_CLIENT_EXPORTS
#include "TrayIconSize.h"
#include "Protocol.h"

#include <algorithm>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

using namespace TrayIconSize;

namespace
{
    std::mutex g_lock;
    HANDLE g_pipe = INVALID_HANDLE_VALUE;
    std::vector<Request> g_registered; // SetSize requests to replay after a host restart
    bool g_monitorRunning = false;

    std::wstring HostPath()
    {
        HMODULE self = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&HostPath), &self);
        wchar_t path[MAX_PATH];
        DWORD n = GetModuleFileNameW(self, path, MAX_PATH);
        if (n == 0 || n >= MAX_PATH) return {};
        std::wstring result(path, n);
        return result.substr(0, result.find_last_of(L'\\')) + L"\\TrayIconSizeHost.exe";
    }

    bool StartHost()
    {
        std::wstring path = HostPath();
        if (path.empty()) return false;
        STARTUPINFOW startup{ sizeof(startup) };
        PROCESS_INFORMATION process{};
        std::wstring command = L"\"" + path + L"\"";
        if (!CreateProcessW(path.c_str(), command.data(), nullptr, nullptr, FALSE,
                CREATE_NO_WINDOW | DETACHED_PROCESS, nullptr, nullptr, &startup, &process))
            return false;
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return true;
    }

    // Caller holds g_lock.
    void Disconnect()
    {
        if (g_pipe != INVALID_HANDLE_VALUE) CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
    }

    // Caller holds g_lock.
    HRESULT Connect()
    {
        if (g_pipe != INVALID_HANDLE_VALUE) return S_OK;
        std::wstring name = PipeName();
        bool started = false;
        for (int attempt = 0; attempt < 50; ++attempt)
        {
            HANDLE pipe = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
            if (pipe != INVALID_HANDLE_VALUE)
            {
                DWORD mode = PIPE_READMODE_MESSAGE;
                if (!SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr))
                {
                    CloseHandle(pipe);
                    return HRESULT_FROM_WIN32(GetLastError());
                }
                g_pipe = pipe;
                return S_OK;
            }
            DWORD error = GetLastError();
            if (error == ERROR_PIPE_BUSY) WaitNamedPipeW(name.c_str(), 1000);
            else if (error == ERROR_FILE_NOT_FOUND)
            {
                if (!started && !(started = StartHost())) return HRESULT_FROM_WIN32(GetLastError());
                Sleep(100);
            }
            else return HRESULT_FROM_WIN32(error);
        }
        return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    }

    // Caller holds g_lock.
    HRESULT Transact(const Request& request)
    {
        for (int attempt = 0; attempt < 2; ++attempt)
        {
            if (HRESULT hr = Connect(); FAILED(hr)) return hr;
            Reply reply{};
            DWORD read = 0;
            if (TransactNamedPipe(g_pipe, const_cast<Request*>(&request), sizeof(request), &reply, sizeof(reply),
                    &read, nullptr) && read == sizeof(reply))
                return reply.result;
            Disconnect(); // host went away; reconnect (and restart it) once
        }
        return HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE);
    }

    // Caller holds g_lock.
    void Replay()
    {
        for (const auto& request : g_registered) Transact(request);
    }

    void MonitorThread()
    {
        for (;;)
        {
            Sleep(2000);
            std::lock_guard lock(g_lock);
            if (g_registered.empty())
            {
                g_monitorRunning = false;
                return;
            }
            DWORD available = 0;
            if (g_pipe == INVALID_HANDLE_VALUE ||
                !PeekNamedPipe(g_pipe, nullptr, 0, nullptr, &available, nullptr))
            {
                Disconnect();
                if (SUCCEEDED(Connect())) Replay();
            }
        }
    }

    IconId MakeId(HWND hwnd, UINT uid, const GUID* guid)
    {
        IconId id{};
        if (guid)
        {
            id.hasGuid = 1;
            id.guid = *guid;
        }
        else
        {
            id.hwnd = reinterpret_cast<uint64_t>(hwnd);
            id.uid = uid;
        }
        return id;
    }
}

TRAYICONSIZE_API TrayIconSize_SetIconSize(HWND hwnd, UINT uid, const GUID* guid, UINT sizeDip)
{
    return TrayIconSize_SetIconSizeEx(hwnd, uid, guid, sizeDip, sizeDip);
}

TRAYICONSIZE_API TrayIconSize_SetIconSizeEx(HWND hwnd, UINT uid, const GUID* guid, UINT widthDip, UINT heightDip)
{
    if (!guid && !hwnd) return E_INVALIDARG;
    Request request{ ProtocolVersion, Op::SetSize, MakeId(hwnd, uid, guid), widthDip, heightDip };
    std::lock_guard lock(g_lock);
    HRESULT hr = Transact(request);
    if (FAILED(hr)) return hr;
    auto existing = std::find_if(g_registered.begin(), g_registered.end(),
        [&](const Request& r) { return r.icon == request.icon; });
    if (existing != g_registered.end()) *existing = request;
    else g_registered.push_back(request);
    if (!g_monitorRunning)
    {
        g_monitorRunning = true;
        std::thread(MonitorThread).detach();
    }
    return S_OK;
}

TRAYICONSIZE_API TrayIconSize_RemoveIcon(HWND hwnd, UINT uid, const GUID* guid)
{
    if (!guid && !hwnd) return E_INVALIDARG;
    Request request{ ProtocolVersion, Op::Remove, MakeId(hwnd, uid, guid), 0, 0 };
    std::lock_guard lock(g_lock);
    auto existing = std::find_if(g_registered.begin(), g_registered.end(),
        [&](const Request& r) { return r.icon == request.icon; });
    if (existing == g_registered.end()) return S_OK;
    g_registered.erase(existing);
    // Without a connection the host holds nothing for us anyway.
    if (g_pipe == INVALID_HANDLE_VALUE) return S_OK;
    HRESULT hr = Transact(request);
    // Dropping the last registration releases the host, which then exits on its own.
    if (g_registered.empty()) Disconnect();
    return hr;
}
