#include "RenderBankLifecycle.h"
#include "RendererGuards.h"
#include "MenuState.h"
#include "CinematicLetterbox.h"

using namespace h5runtime;
struct RendererRequest{uint32_t magic,version,operation,status;uint64_t creation;uint32_t phase,callbacks,batches,blocked,published,fault;char message[512];};
static_assert(sizeof(RendererRequest)==560);
static SRWLOCK operationLock=SRWLOCK_INIT;
static uint64_t session=0;
static bool attempted=false;
static void arm(uint64_t base){using namespace renderer;
    require(!attempted,"Renderer setup was already attempted. Restart Forge before retrying.");auto menu=inspectMenu(base);require(menu.screen && menu.name==nameId("main_menu"),"Prepare the renderer at Forge's main menu.");rendererGuards(base);
    letterbox::guards(base);
    renderer::base=base;pool=value<uint64_t>(base+0x5f20768);currentPool();auto manager=value<uint64_t>(base+0x58fbd80+3*0x1d40+0xc8);
    require(manager && value<unsigned char>(manager+0x80)==0,"Forge's frame ownership state differs.");critical=manager+0x40;
    nativeTry=reinterpret_cast<decltype(nativeTry)>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"TryEnterCriticalSection"));
    require(nativeTry && value<void*>(base+0x3284d00)==reinterpret_cast<void*>(nativeTry),"Forge's frame synchronization function is already modified.");
    for(unsigned i=0;i<3;++i)require(value<uint64_t>(base+callbackSlots[i])==base+callbackFunctions[i],"A native shader callback is already modified.");
    require(value<unsigned char>(base+0x15c7569)==0x63,"The native shader iterator has already changed.");attempted=true;
    // Correct the iterator's back edge while preserving the complete native
    // function, validation filters and native pipeline creation paths.
    auto point=reinterpret_cast<volatile char*>(base+0x15c7569);DWORD old=0,unused=0;require(VirtualProtect(const_cast<char*>(point),1,PAGE_EXECUTE_READWRITE,&old)!=0,"Could not prepare the native shader iterator.");
    auto changed=_InterlockedCompareExchange8(point,0x2f,0x63)==0x63;auto flushed=FlushInstructionCache(GetCurrentProcess(),const_cast<char*>(point),1);auto restored=VirtualProtect(const_cast<char*>(point),1,old,&unused);
    require(changed && flushed && restored,"The native shader iterator could not be prepared. Restart Forge.");
    require(exchange(0x3284d00,reinterpret_cast<void*>(nativeTry),reinterpret_cast<void*>(tryFrame)),"Could not install frame synchronization. Restart Forge.");
    for(unsigned i=0;i<3;++i)require(exchange(callbackSlots[i],reinterpret_cast<void*>(base+callbackFunctions[i]),callbackHooks[i]),"Could not install shader bank synchronization. Restart Forge.");
    letterbox::arm(base);InterlockedExchange(&armed,1);
}
extern "C" __declspec(dllexport) DWORD WINAPI H5Renderer(RendererRequest* request){
    if(!request)return ERROR_INVALID_PARAMETER;AcquireSRWLockExclusive(&operationLock);request->status=0;request->message[0]=0;
    try{require(request->magic==0x44523548 && request->version==1 && request->operation<=1,"The campaign renderer request is incompatible.");auto base=identity(request->creation);require(!session || session==request->creation,"The campaign renderer belongs to another session.");
        if(request->operation==1){session=request->creation;arm(base);}request->phase=renderer::armed?1:attempted?2:0;
        request->callbacks=renderer::callbacks;request->batches=renderer::batches;request->blocked=renderer::blocked;request->published=renderer::published;request->fault=renderer::fault;
        _snprintf_s(request->message,_TRUNCATE,"Cinematic letterbox fills: %ld; errors: %ld.",letterbox::fills,letterbox::errors);
        if(letterbox::errors){request->phase=2;request->fault=1;request->status=ERROR_INVALID_STATE;}
        if(renderer::fault){request->phase=2;AcquireSRWLockShared(&renderer::failureLock);_snprintf_s(request->message,_TRUNCATE,"%s Close Forge and copy the launch details. (callbacks %lu, batches %lu, blocked frames %lu)",renderer::failure,request->callbacks,request->batches,request->blocked);ReleaseSRWLockShared(&renderer::failureLock);request->status=ERROR_INVALID_STATE;}
    }catch(const std::exception& error){request->status=ERROR_INVALID_STATE;strncpy_s(request->message,error.what(),_TRUNCATE);}catch(...){request->status=ERROR_UNHANDLED_EXCEPTION;}
    ReleaseSRWLockExclusive(&operationLock);return request->status;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
