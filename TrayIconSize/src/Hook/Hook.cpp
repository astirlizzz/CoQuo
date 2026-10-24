// TrayIconSizeHook: TAP object loaded into explorer.exe by XAML Diagnostics.
//
// Watches the taskbar's notification-area XAML and enlarges only the icons the
// TrayIconSize host publishes rules for. An icon is identified by asking the shell
// for its screen rectangle (Shell_NotifyIconGetRect, public API) and finding the
// SystemTray.NotifyIconView laid out at exactly that rectangle. Everything fails
// open: when in doubt, the icon is left as Explorer drew it.

#include <unknwn.h>
#include <windows.h>
#include <ocidl.h>
#include <shellapi.h>
#include <xamlOM.h>
#undef GetCurrentTime // clashes with Storyboard::GetCurrentTime in C++/WinRT

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Media.h>

#include <atomic>
#include <cmath>
#include <cstdio>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <unordered_set>
#include <vector>

#include "Protocol.h"

namespace
{
    namespace wux = winrt::Windows::UI::Xaml;
    using namespace TrayIconSize;

    std::mutex g_logLock;

    // Small diagnostic log of attach/match events; restarted when it exceeds 1 MB.
    void Log(const wchar_t* format, ...)
    {
        wchar_t line[1024];
        va_list args;
        va_start(args, format);
        _vsnwprintf_s(line, _TRUNCATE, format, args);
        va_end(args);

        std::lock_guard lock(g_logLock);
        static const std::wstring path = []
        {
            wchar_t base[MAX_PATH];
            DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", base, MAX_PATH);
            if (n == 0 || n >= MAX_PATH) return std::wstring();
            std::wstring dir = std::wstring(base) + L"\\TrayIconSize";
            CreateDirectoryW(dir.c_str(), nullptr);
            return dir + L"\\hook.log";
        }();
        if (path.empty()) return;
        WIN32_FILE_ATTRIBUTE_DATA info{};
        bool large = GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &info) &&
            (info.nFileSizeHigh != 0 || info.nFileSizeLow > 1024 * 1024);
        FILE* file = nullptr;
        if (_wfopen_s(&file, path.c_str(), large ? L"w, ccs=UTF-8" : L"a, ccs=UTF-8") != 0 || !file) return;
        SYSTEMTIME now;
        GetLocalTime(&now);
        fwprintf(file, L"%04u-%02u-%02u %02u:%02u:%02u [%lu] %s\n", now.wYear, now.wMonth, now.wDay,
            now.wHour, now.wMinute, now.wSecond, GetCurrentProcessId(), line);
        fclose(file);
    }

    const wchar_t* Str(BSTR value) { return value ? value : L""; }

    bool StartsWith(const wchar_t* text, const wchar_t* prefix)
    {
        return text && wcsncmp(text, prefix, wcslen(prefix)) == 0;
    }

    struct ScreenRect { LONG left, top, right, bottom; };

    // Layout rounding can differ by a pixel between XAML and the shell's answer.
    bool Near(const ScreenRect& a, const ScreenRect& b)
    {
        return std::abs(a.left - b.left) <= 1 && std::abs(a.top - b.top) <= 1 &&
               std::abs(a.right - b.right) <= 1 && std::abs(a.bottom - b.bottom) <= 1;
    }

    struct RuleRect
    {
        IconId icon;
        uint32_t widthDip;
        uint32_t heightDip;
        ScreenRect rect;
    };

    // A rule currently applied to a live XAML Image.
    struct Binding
    {
        IconId icon;
        uint32_t widthDip;
        uint32_t heightDip;
        winrt::weak_ref<wux::Controls::Image> image;
        double originalWidth;
        double originalHeight;
        wux::Media::Stretch originalStretch;
        wux::FrameworkElement::DataContextChanged_revoker dataContextChanged;
    };

    struct Tap : winrt::implements<Tap, IObjectWithSite, IVisualTreeServiceCallback2>
    {
        ~Tap()
        {
            if (m_wake) CloseHandle(m_wake);
        }

        HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override try
        {
            if (!site)
            {
                m_stop = true;
                if (m_wake) SetEvent(m_wake);
                m_site = nullptr;
                return S_OK;
            }
            m_site.copy_from(site);
            m_diagnostics = m_site.as<IXamlDiagnostics>();
            m_tree = m_site.as<IVisualTreeService3>();
            m_wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);

            // Lets the host see that this explorer.exe already carries the hook.
            wchar_t marker[96];
            swprintf_s(marker, HookMarkerFormat, GetCurrentProcessId());
            CreateEventW(nullptr, TRUE, TRUE, marker); // intentionally kept for the process lifetime
            Log(L"attached, protocol %u", ProtocolVersion);

            // AdviseVisualTreeChange replays the existing tree synchronously on the UI
            // thread; calling it from the thread delivering SetSite would deadlock.
            std::thread([self = get_strong()]
            {
                HRESULT hr = self->m_tree->AdviseVisualTreeChange(self.get());
                if (FAILED(hr)) { Log(L"AdviseVisualTreeChange failed: 0x%08X", hr); return; }
                self->RulesLoop();
            }).detach();
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }

        HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** site) override
        {
            *site = nullptr;
            return m_site ? m_site->QueryInterface(riid, site) : E_FAIL;
        }

        HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation relation, VisualElement element,
            VisualMutationType mutation) override
        {
            // Any exception escaping into explorer.exe would take the taskbar down.
            try { Observe(relation, element, mutation); }
            catch (...) { Log(L"OnVisualTreeChange failed: 0x%08X", winrt::to_hresult()); }
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) override
        {
            return S_OK;
        }

    private:
        // ---- UI thread -------------------------------------------------------------------------

        void Observe(const ParentChildRelation& relation, const VisualElement& element, VisualMutationType mutation)
        {
            // The overflow flyout may live on another UI thread.
            std::lock_guard lock(m_uiLock);
            if (mutation == Remove)
            {
                if (m_trayHandles.erase(element.Handle)) SetEvent(m_wake);
                return;
            }
            // Only the notification area is tracked; its subtree is small.
            if (!StartsWith(element.Type, L"SystemTray.") && !m_trayHandles.contains(relation.Parent)) return;
            m_trayHandles.insert(element.Handle);

            bool frame = wcscmp(Str(element.Type), L"SystemTray.SystemTrayFrame") == 0;
            bool view = wcscmp(Str(element.Type), L"SystemTray.NotifyIconView") == 0;
            if (frame || view)
            {
                winrt::com_ptr<::IInspectable> object;
                if (SUCCEEDED(m_diagnostics->GetIInspectableFromHandle(element.Handle, object.put())) && object)
                    if (auto fe = object.try_as<wux::FrameworkElement>())
                    {
                        if (frame)
                        {
                            std::lock_guard frameLock(m_frameLock);
                            m_frame = winrt::make_weak(fe);
                            m_dispatcher = fe.Dispatcher();
                        }
                        else m_views.push_back(winrt::make_weak(fe));
                    }
            }
            SetEvent(m_wake);
        }

        // Physical screen rectangle of a taskbar notification icon view.
        static std::optional<ScreenRect> ScreenBounds(const wux::FrameworkElement& view, const RECT& bridge)
        {
            auto root = view.XamlRoot();
            if (!root) return std::nullopt;
            double scale = root.RasterizationScale();
            auto origin = view.TransformToVisual(nullptr).TransformPoint({ 0, 0 });
            double left = origin.X * scale, top = origin.Y * scale;
            return ScreenRect{
                bridge.left + std::lround(left), bridge.top + std::lround(top),
                bridge.left + std::lround(left + view.ActualWidth() * scale),
                bridge.top + std::lround(top + view.ActualHeight() * scale) };
        }

        // NotifyIconView > ... > SystemTray.ImageIconContent > Grid#ContainerGrid > Image
        static wux::Controls::Image FindTrayImage(const wux::DependencyObject& node, bool insideContent = false)
        {
            using wux::Media::VisualTreeHelper;
            for (int i = 0, n = VisualTreeHelper::GetChildrenCount(node); i < n; ++i)
            {
                auto child = VisualTreeHelper::GetChild(node, i);
                bool content = insideContent || winrt::get_class_name(child) == L"SystemTray.ImageIconContent";
                if (content)
                    if (auto image = child.try_as<wux::Controls::Image>()) return image;
                if (auto found = FindTrayImage(child, content)) return found;
            }
            return nullptr;
        }

        static void Restore(Binding& binding)
        {
            binding.dataContextChanged.revoke();
            // Leave the value alone if someone else changed it after us.
            if (auto image = binding.image.get();
                image && image.Width() == binding.widthDip && image.Height() == binding.heightDip)
            {
                image.Width(binding.originalWidth);
                image.Height(binding.originalHeight);
                image.Stretch(binding.originalStretch);
            }
        }

        void ApplyPass(const std::vector<RuleRect>& rules)
        {
            wux::FrameworkElement frame{ nullptr };
            {
                std::lock_guard lock(m_frameLock);
                frame = m_frame.get();
            }
            HWND taskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
            HWND bridge = FindWindowExW(taskbar, nullptr, L"Windows.UI.Composition.DesktopWindowContentBridge", nullptr);
            RECT bridgeRect{};
            bool usable = frame && frame.XamlRoot() && bridge && GetWindowRect(bridge, &bridgeRect);

            // Live views of the taskbar itself; the overflow flyout has its own XamlRoot and is skipped.
            std::vector<std::pair<wux::FrameworkElement, ScreenRect>> views;
            if (usable)
            {
                auto root = frame.XamlRoot();
                std::vector<wux::FrameworkElement> candidates;
                {
                    std::lock_guard lock(m_uiLock);
                    std::erase_if(m_views, [](auto& weak) { return !weak.get(); });
                    for (auto& weak : m_views)
                        if (auto view = weak.get()) candidates.push_back(view);
                }
                for (const auto& view : candidates)
                {
                    if (view.XamlRoot() != root) continue;
                    if (auto bounds = ScreenBounds(view, bridgeRect)) views.emplace_back(view, *bounds);
                }
            }

            std::vector<Binding> next;
            for (const auto& rule : rules)
            {
                const wux::FrameworkElement* match = nullptr;
                for (const auto& [view, bounds] : views)
                    if (Near(bounds, rule.rect))
                    {
                        // Two candidates means the layout is ambiguous right now: touch nothing.
                        if (match) { match = nullptr; break; }
                        match = &view;
                    }
                if (!match) continue;
                auto image = FindTrayImage(*match);
                if (!image) continue;

                auto previous = std::find_if(m_bindings.begin(), m_bindings.end(),
                    [&](const Binding& b) { return b.icon == rule.icon && b.image.get() == image; });
                Binding binding;
                if (previous != m_bindings.end())
                {
                    binding = std::move(*previous);
                    m_bindings.erase(previous);
                }
                else
                {
                    binding.icon = rule.icon;
                    binding.image = winrt::make_weak(image);
                    binding.originalWidth = image.Width();
                    binding.originalHeight = image.Height();
                    binding.originalStretch = image.Stretch();
                    // A recycled container now shows another icon: undo at once, re-match later.
                    binding.dataContextChanged = match->DataContextChanged(winrt::auto_revoke,
                        [self = get_weak(), weakImage = winrt::make_weak(image)](auto&&, auto&&)
                        {
                            if (auto tap = self.get()) tap->OnDataContextChanged(weakImage);
                        });
                    Log(L"matched %llx:%u guid=%u -> %ux%u dip", rule.icon.hwnd, rule.icon.uid, rule.icon.hasGuid,
                        rule.widthDip, rule.heightDip);
                }
                binding.widthDip = rule.widthDip;
                binding.heightDip = rule.heightDip;
                if (image.Width() != rule.widthDip || image.Height() != rule.heightDip)
                {
                    image.Width(rule.widthDip);
                    image.Height(rule.heightDip);
                }
                // The square icon bitmap must fill a non-square box instead of being letterboxed.
                auto stretch = rule.widthDip == rule.heightDip ? binding.originalStretch : wux::Media::Stretch::Fill;
                if (image.Stretch() != stretch) image.Stretch(stretch);
                next.push_back(std::move(binding));
            }
            for (auto& stale : m_bindings)
            {
                Restore(stale);
                Log(L"released %llx:%u", stale.icon.hwnd, stale.icon.uid);
            }
            m_bindings = std::move(next);
            m_bindingCount = m_bindings.size();
        }

        void OnDataContextChanged(const winrt::weak_ref<wux::Controls::Image>& image)
        {
            auto it = std::find_if(m_bindings.begin(), m_bindings.end(),
                [&](const Binding& b) { return b.image.get() == image.get(); });
            if (it == m_bindings.end()) return;
            Restore(*it);
            m_bindings.erase(it);
            m_bindingCount = m_bindings.size();
            SetEvent(m_wake);
        }

        // ---- rules thread ----------------------------------------------------------------------

        bool OpenShared()
        {
            if (m_view) return true;
            m_mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, RulesMappingName);
            m_lock = OpenMutexW(SYNCHRONIZE, FALSE, RulesLockName);
            m_changed = OpenEventW(SYNCHRONIZE, FALSE, RulesChangedName);
            if (m_mapping && m_lock && m_changed)
                m_view = static_cast<const SharedRules*>(MapViewOfFile(m_mapping, FILE_MAP_READ, 0, 0, sizeof(SharedRules)));
            if (m_view) return true;
            for (HANDLE* h : { &m_mapping, &m_lock, &m_changed })
                if (*h) { CloseHandle(*h); *h = nullptr; }
            return false;
        }

        // Copies the published rules; empty when the host is gone or speaks another protocol.
        std::vector<SharedRule> ReadRules()
        {
            std::vector<SharedRule> rules;
            DWORD wait = WaitForSingleObject(m_lock, 1000);
            if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) return rules;
            uint32_t hostPid = m_view->hostPid;
            if (m_view->version == ProtocolVersion)
                rules.assign(m_view->rules, m_view->rules + std::min(m_view->count, MaxRules));
            ReleaseMutex(m_lock);

            if (hostPid != m_hostPid)
            {
                if (m_host) CloseHandle(m_host);
                m_host = hostPid ? OpenProcess(SYNCHRONIZE, FALSE, hostPid) : nullptr;
                m_hostPid = hostPid;
            }
            if (m_host && WaitForSingleObject(m_host, 0) != WAIT_TIMEOUT)
            {
                // Keep the dead pid so it is not reopened; a new host publishes its own.
                CloseHandle(m_host);
                m_host = nullptr;
            }
            // Rules are tied to a live host: a crashed host must not leave icons enlarged.
            if (!m_host) rules.clear();
            return rules;
        }

        void RulesLoop()
        {
            while (!m_stop)
            {
                HANDLE handles[3] = { m_wake, m_changed, m_host };
                DWORD count = m_changed ? (m_host ? 3 : 2) : 1;
                DWORD signaled = WaitForMultipleObjects(count, handles, FALSE, 2000);
                if (m_stop) break;
                if (signaled == WAIT_OBJECT_0) Sleep(250); // let a burst of tree changes settle

                if (!OpenShared()) continue;
                auto shared = ReadRules();
                if (shared.empty() && m_bindingCount == 0) continue;

                std::vector<RuleRect> rules;
                for (const auto& rule : shared)
                {
                    NOTIFYICONIDENTIFIER id{ sizeof(id) };
                    id.hWnd = reinterpret_cast<HWND>(rule.icon.hwnd);
                    id.uID = rule.icon.uid;
                    if (rule.icon.hasGuid) id.guidItem = rule.icon.guid;
                    RECT rect{};
                    // Hidden (overflow) or unknown icons fail here and stay untouched.
                    if (SUCCEEDED(Shell_NotifyIconGetRect(&id, &rect)))
                        rules.push_back({ rule.icon, rule.widthDip, rule.heightDip, { rect.left, rect.top, rect.right, rect.bottom } });
                }
                Dispatch(std::move(rules));
            }
        }

        void Dispatch(std::vector<RuleRect> rules)
        {
            winrt::Windows::UI::Core::CoreDispatcher dispatcher{ nullptr };
            {
                std::lock_guard lock(m_frameLock);
                dispatcher = m_dispatcher;
            }
            if (!dispatcher || m_passPending.exchange(true)) return;
            try
            {
                dispatcher.RunAsync(winrt::Windows::UI::Core::CoreDispatcherPriority::Low,
                    [self = get_strong(), rules = std::move(rules)]
                    {
                        self->m_passPending = false;
                        try { self->ApplyPass(rules); }
                        catch (...) { Log(L"ApplyPass failed: 0x%08X", winrt::to_hresult()); }
                    });
            }
            catch (...) { m_passPending = false; }
        }

        winrt::com_ptr<IUnknown> m_site;
        winrt::com_ptr<IXamlDiagnostics> m_diagnostics;
        winrt::com_ptr<IVisualTreeService3> m_tree;
        std::atomic<bool> m_stop = false;
        std::atomic<bool> m_passPending = false;
        std::atomic<size_t> m_bindingCount = 0;
        HANDLE m_wake = nullptr;

        // UI thread state; tray handles and views are guarded by m_uiLock.
        std::mutex m_uiLock;
        std::unordered_set<InstanceHandle> m_trayHandles;
        std::vector<winrt::weak_ref<wux::FrameworkElement>> m_views;
        std::vector<Binding> m_bindings;

        // Shared between threads.
        std::mutex m_frameLock;
        winrt::weak_ref<wux::FrameworkElement> m_frame;
        winrt::Windows::UI::Core::CoreDispatcher m_dispatcher{ nullptr };

        // Rules thread state.
        HANDLE m_mapping = nullptr;
        HANDLE m_lock = nullptr;
        HANDLE m_changed = nullptr;
        HANDLE m_host = nullptr;
        uint32_t m_hostPid = 0;
        const SharedRules* m_view = nullptr;
    };

    struct TapFactory : winrt::implements<TapFactory, IClassFactory>
    {
        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** result) override try
        {
            *result = nullptr;
            if (outer) return CLASS_E_NOAGGREGATION;
            return winrt::make_self<Tap>()->QueryInterface(riid, result);
        }
        catch (...) { return winrt::to_hresult(); }

        HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }
    };
}

_Use_decl_annotations_ STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* result) try
{
    *result = nullptr;
    if (clsid != TrayIconSize::TapClsid) return CLASS_E_CLASSNOTAVAILABLE;
    return winrt::make_self<TapFactory>()->QueryInterface(riid, result);
}
catch (...) { return winrt::to_hresult(); }

// Code may still run on explorer threads; never unload.
__control_entrypoint(DllExport) STDAPI DllCanUnloadNow() { return S_FALSE; }
