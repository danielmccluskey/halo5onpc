#include <windows.h>
#include <appmodel.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.ApplicationModel.Core.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.System.h>
#include <atomic>
#include <memory>

struct LaunchRequest { uint32_t magic, version; int32_t status; uint32_t accepted, stage; };
static_assert(sizeof(LaunchRequest) == 20);
static std::atomic<bool> pending{ false };

struct LaunchState {
    HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HRESULT status = E_PENDING;
    bool accepted = false;
    std::atomic<bool> completed{ false };
    ~LaunchState() { if (event) CloseHandle(event); }
    void finish(HRESULT result, bool launched) {
        if (completed.exchange(true)) return;
        status = result; accepted = launched;
        SetEvent(event); pending.store(false);
    }
};

static void launch(const std::shared_ptr<LaunchState>& state) {
    try {
        winrt::Windows::System::LauncherOptions options;
        options.TargetApplicationPackageFamilyName(L"Microsoft.Halo5Forge_8wekyb3d8bbwe");
        auto operation = winrt::Windows::System::Launcher::LaunchUriAsync(
            winrt::Windows::Foundation::Uri(L"ms-xbl-multiplayer://launch"), options);
        operation.Completed([state](auto const& result, auto const&) {
            try { state->finish(S_OK, result.GetResults()); }
            catch (winrt::hresult_error const& error) { state->finish(error.code(), false); }
            catch (...) { state->finish(E_FAIL, false); }
        });
    }
    catch (winrt::hresult_error const& error) { state->finish(error.code(), false); }
    catch (...) { state->finish(E_FAIL, false); }
}

extern "C" __declspec(dllexport) DWORD WINAPI H5LaunchForge(LaunchRequest* request) {
    if (!request) return ERROR_INVALID_PARAMETER;
    request->accepted = 0; request->stage = 0; request->status = E_INVALIDARG;
    if (request->magic != 0x48354C48 || request->version != 1) return 0;
    wchar_t family[256]; UINT32 length = 256;
    if (GetCurrentPackageFamilyName(&length, family) != ERROR_SUCCESS || wcscmp(family, L"Microsoft.Tomp_8wekyb3d8bbwe")) {
        request->status = E_ACCESSDENIED; return 0;
    }
    request->stage = 1;
    if (pending.exchange(true)) { request->status = HRESULT_FROM_WIN32(ERROR_BUSY); return 0; }
    bool apartment = false, queued = false;
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded); apartment = true;
        auto state = std::make_shared<LaunchState>();
        if (!state->event) winrt::throw_last_error();
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) winrt::throw_last_error();
        DWORD container = 0, size = 0;
        const BOOL queried = GetTokenInformation(token, TokenIsAppContainer, &container, sizeof(container), &size);
        const auto tokenError = GetLastError(); CloseHandle(token);
        if (!queried) winrt::throw_hresult(HRESULT_FROM_WIN32(tokenError));
        if (container) {
            // UWP LaunchUriAsync must run on the app's actual ASTA/UI thread.
            winrt::Windows::UI::Core::CoreDispatcher dispatcher{ nullptr };
            for (int i = 0; i < 30 && !dispatcher; ++i) {
                try {
                    auto view = winrt::Windows::ApplicationModel::Core::CoreApplication::MainView();
                    if (view && view.CoreWindow()) dispatcher = view.CoreWindow().Dispatcher();
                }
                catch (winrt::hresult_error const&) { }
                if (!dispatcher) Sleep(100);
            }
            if (!dispatcher) winrt::throw_hresult(RPC_E_WRONG_THREAD);
            auto scheduled = dispatcher.RunAsync(winrt::Windows::UI::Core::CoreDispatcherPriority::Normal,
                [state]() { launch(state); });
            queued = true; request->stage = 2;
            // Failure to dispatch is terminal; successful dispatch is not launch completion.
            scheduled.Completed([state](auto const& action, auto const& status) {
                if (status != winrt::Windows::Foundation::AsyncStatus::Completed) {
                    try { action.GetResults(); }
                    catch (winrt::hresult_error const& error) { state->finish(error.code(), false); }
                }
            });
        } else { queued = true; request->stage = 2; launch(state); } // Packaged desktop hubs may call from this MTA thread.
        if (WaitForSingleObject(state->event, 9000) == WAIT_OBJECT_0) {
            request->stage = 3; request->status = state->status; request->accepted = state->accepted ? 1u : 0u;
        } else request->status = HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        // Asynchronous callbacks own state, never the request buffer. A late launch
        // remains pending and cannot access memory the worker may now release.
    }
    catch (winrt::hresult_error const& error) { request->status = error.code(); }
    catch (...) { request->status = E_FAIL; }
    if (!queued) pending.store(false);
    if (apartment) winrt::uninit_apartment();
    return 0;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
