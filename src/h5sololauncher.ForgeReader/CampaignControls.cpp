#include "MenuState.h"
#include "ControlsGuards.h"
#include <intrin.h>
namespace h5runtime::controls {
struct Request{uint32_t magic,version,operation,status;uint64_t creation;uint32_t phase,actions,skulls,reserved;char message[512];};
static_assert(sizeof(Request)==552);
static uint64_t base=0,creation=0;
static volatile LONG phase=0,actions=0,skulls=0;
static PVOID handler=nullptr;
static SRWLOCK lock=SRWLOCK_INIT;
static bool eligible(void*,void**){return reinterpret_cast<bool(*)()>(base+0x21483b0)();}
static void action(void* object,void** argument){reinterpret_cast<void(*)(void*,void**)>(base+0x17bb140)(object,argument);InterlockedIncrement(&actions);}
static LONG CALLBACK trap(EXCEPTION_POINTERS* event){
    if(event->ExceptionRecord->ExceptionCode!=EXCEPTION_BREAKPOINT || reinterpret_cast<uint64_t>(event->ExceptionRecord->ExceptionAddress)!=base+0x17b982b)return EXCEPTION_CONTINUE_SEARCH;
    auto context=event->ContextRecord;auto object=reinterpret_cast<unsigned char*>(context->Rbx);auto available=static_cast<unsigned char>(context->Rax);
    // Enable the native selection control; collection and save bits stay native.
    if(*reinterpret_cast<uint64_t*>(object)==base+0x3714810 && *reinterpret_cast<uint32_t*>(object+0x88)<13){available=1;InterlockedIncrement(&skulls);}
    object[0xa8]=available;context->Rip=base+0x17b982b+6;return EXCEPTION_CONTINUE_EXECUTION;
}
static bool exchange(uint64_t address,void* before,void* after){DWORD old=0,unused=0;auto slot=reinterpret_cast<void**>(address);if(!VirtualProtect(slot,8,PAGE_READWRITE,&old))return false;auto ok=InterlockedCompareExchangePointer(slot,after,before)==before;return VirtualProtect(slot,8,old,&unused) && ok;}
static void run(Request& request){require(request.magic==0x52433548 && request.version==1 && request.operation<=1,"The campaign controls request is incompatible.");auto image=identity(request.creation);require(!creation || creation==request.creation,"Campaign controls belong to another Forge session.");
    if(request.operation==1){require(phase==0,"Campaign controls were already attempted. Restart Forge.");auto menu=inspectMenu(image);require(menu.name==nameId("main_menu") && menu.screen,"Campaign control setup requires the main menu.");controlsGuards(image);base=image;creation=request.creation;phase=2;
        auto select=base+0x37149f0+0x68;auto execute=select+8;require(value<uint64_t>(select)==base+0x171cc50 && value<uint64_t>(execute)==base+0x171d030,"The native campaign mouse controls are already changed.");
        handler=AddVectoredExceptionHandler(1,trap);require(handler!=nullptr,"Could not prepare campaign skull controls.");
        require(exchange(select,reinterpret_cast<void*>(base+0x171cc50),reinterpret_cast<void*>(eligible)),"Could not prepare campaign selection. Restart Forge.");
        require(exchange(execute,reinterpret_cast<void*>(base+0x171d030),reinterpret_cast<void*>(action)),"Could not prepare campaign selection action. Restart Forge.");
        auto site=reinterpret_cast<char*>(base+0x17b982b);DWORD old=0,unused=0;require(VirtualProtect(site,1,PAGE_EXECUTE_READWRITE,&old)!=0,"Could not prepare skull availability. Restart Forge.");
        auto changed=_InterlockedCompareExchange8(site,static_cast<char>(0xcc),static_cast<char>(0x88))==static_cast<char>(0x88);FlushInstructionCache(GetCurrentProcess(),site,1);auto restored=VirtualProtect(site,1,old,&unused)!=0;require(changed && restored,"Could not prepare skull availability. Restart Forge.");InterlockedExchange(&phase,1);
    }
    request.phase=phase;request.actions=actions;request.skulls=skulls;
}
}
extern "C" __declspec(dllexport) DWORD WINAPI H5Controls(h5runtime::controls::Request* request){using namespace h5runtime::controls;if(!request)return ERROR_INVALID_PARAMETER;AcquireSRWLockExclusive(&lock);request->status=0;request->message[0]=0;
    try{run(*request);}catch(const std::exception& error){request->status=ERROR_INVALID_STATE;strncpy_s(request->message,error.what(),_TRUNCATE);}catch(...){request->status=ERROR_UNHANDLED_EXCEPTION;}ReleaseSRWLockExclusive(&lock);return request->status;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
