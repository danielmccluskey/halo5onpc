#include "RuntimeSupport.h"
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.ApplicationModel.Core.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.UI.ViewManagement.h>
#include <atomic>
#include <memory>
namespace h5runtime::presentation {
struct Request{uint32_t magic,version,operation,status;uint64_t creation;uint32_t visible,fullscreen;float width,height;char message[512];};
static_assert(sizeof(Request)==552);
static std::atomic<bool> pending{false};
struct Result{
    HANDLE event=CreateEventW(nullptr,TRUE,FALSE,nullptr);HRESULT status=E_PENDING;
    uint32_t visible=0,fullscreen=0;float width=0,height=0;std::atomic<bool> completed{false};std::atomic<int> stage{0};
    ~Result(){if(event)CloseHandle(event);}
    void finish(HRESULT code){if(completed.exchange(true))return;status=code;SetEvent(event);pending.store(false);}
};
static void run(Request& request){
    require(request.magic==0x57563548 && request.version==1 && request.operation<=2,"The game window request is incompatible.");identity(request.creation);
    require(!pending.exchange(true),"A game window request is still pending. Wait before retrying.");bool apartment=false,queued=false;
    try{
        winrt::init_apartment(winrt::apartment_type::multi_threaded);apartment=true;
        auto result=std::make_shared<Result>();if(!result->event)winrt::throw_last_error();
        auto dispatcher=winrt::Windows::ApplicationModel::Core::CoreApplication::MainView().Dispatcher();require(dispatcher!=nullptr,"Forge has not created its game dispatcher yet.");
        bool show=request.operation!=0,windowed=request.operation==2;
        auto action=dispatcher.RunAsync(winrt::Windows::UI::Core::CoreDispatcherPriority::Normal,[result,show,windowed](){
            try{
                result->stage=1;
                auto window=winrt::Windows::UI::Core::CoreWindow::GetForCurrentThread();require(window!=nullptr,"Forge's game view has no window.");
                if(show)window.Activate();
                result->stage=2;
                auto bounds=window.Bounds();result->visible=window.Visible()?1:0;result->width=bounds.Width;result->height=bounds.Height;
                auto view=winrt::Windows::UI::ViewManagement::ApplicationView::GetForCurrentView();
                if(windowed)view.ExitFullScreenMode();
                result->fullscreen=view.IsFullScreenMode()?1:0;
                // This is already the main view; a standalone switch targets a
                // different view and can leave a same-view switch pending forever.
                result->finish(S_OK);
            }catch(winrt::hresult_error const& error){result->finish(error.code());}catch(...){result->finish(E_FAIL);}
        });queued=true;
        action.Completed([result](auto const& operation,auto const& status){if(status!=winrt::Windows::Foundation::AsyncStatus::Completed){try{operation.GetResults();}catch(winrt::hresult_error const& error){result->finish(error.code());}catch(...){result->finish(E_FAIL);}}});
        if(WaitForSingleObject(result->event,9000)!=WAIT_OBJECT_0)throw std::runtime_error("Forge's window did not respond (stage "+std::to_string(result->stage.load())+"). The pending request was kept; close Forge before retrying.");
        request.visible=result->visible;request.fullscreen=result->fullscreen;request.width=result->width;request.height=result->height;
        if(FAILED(result->status))winrt::throw_hresult(result->status);
    }catch(...){if(!queued)pending.store(false);if(apartment)winrt::uninit_apartment();throw;}
    if(apartment)winrt::uninit_apartment();
}
}
extern "C" __declspec(dllexport) DWORD WINAPI H5View(h5runtime::presentation::Request* request){using namespace h5runtime::presentation;if(!request)return ERROR_INVALID_PARAMETER;request->status=0;request->message[0]=0;
    try{run(*request);}catch(winrt::hresult_error const& error){request->status=static_cast<uint32_t>(error.code().value);auto message=winrt::to_string(error.message());strncpy_s(request->message,message.c_str(),_TRUNCATE);}
    catch(const std::exception& error){request->status=ERROR_INVALID_STATE;strncpy_s(request->message,error.what(),_TRUNCATE);}catch(...){request->status=ERROR_UNHANDLED_EXCEPTION;}return 0;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
