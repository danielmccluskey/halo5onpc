#include "RuntimeSupport.h"
#include "AudioGuards.h"

namespace h5runtime::campaignAudio {
struct Request{uint32_t magic,version,operation,status;uint64_t creation;uint32_t phase,packageId,result,reserved;char message[512];};
static_assert(sizeof(Request)==552);
static uint64_t base=0,creation=0;
static volatile LONG phase=0;
static uint32_t packageId=0,result=0;
static char failure[512]{};
static SRWLOCK operationLock=SRWLOCK_INIT;
static void loadCaught(){try{audioGuards(base);auto io=value<uint64_t>(base+0x5a3c200)+16;require(value<uint64_t>(io)==base+0x33eeb30,"Forge's native audio package loader differs.");
    result=reinterpret_cast<int(*)(void*,const wchar_t*,uint32_t*,int)>(base+0xa3f8f0)(reinterpret_cast<void*>(io),L"h5solo-campaign.pck",&packageId,-1);
    require(result==1,"Forge rejected the generated campaign audio package. Copy the launch details.");InterlockedExchange(&phase,2);
}catch(const std::exception& error){strncpy_s(failure,error.what(),_TRUNCATE);InterlockedExchange(&phase,3);}catch(...){strcpy_s(failure,"Campaign audio package registration failed.");InterlockedExchange(&phase,3);}}
static DWORD WINAPI load(void*){__try{loadCaught();}__except(EXCEPTION_EXECUTE_HANDLER){strcpy_s(failure,"A native fault interrupted campaign audio registration. Restart Forge.");InterlockedExchange(&phase,3);}return 0;}
static void run(Request& request){require(request.magic==0x55413548 && request.version==1 && request.operation<=1,"The campaign audio request is incompatible.");auto currentBase=identity(request.creation);
    require(!creation || creation==request.creation,"Campaign audio belongs to another Forge session.");
    if(request.operation==1){require(phase==0,"Campaign audio registration was already attempted. Restart Forge before retrying.");audioGuards(currentBase);base=currentBase;creation=request.creation;InterlockedExchange(&phase,1);auto thread=CreateThread(nullptr,0,load,nullptr,0,nullptr);require(thread!=nullptr,"Could not start native campaign audio registration.");CloseHandle(thread);}
    request.phase=InterlockedCompareExchange(&phase,0,0);if(request.phase>=2){request.packageId=packageId;request.result=result;}if(request.phase==3){request.status=ERROR_INVALID_STATE;strncpy_s(request.message,failure,_TRUNCATE);}
}
}
extern "C" __declspec(dllexport) DWORD WINAPI H5Audio(h5runtime::campaignAudio::Request* request){using namespace h5runtime::campaignAudio;
    if(!request)return ERROR_INVALID_PARAMETER;AcquireSRWLockExclusive(&operationLock);request->status=0;request->message[0]=0;
    try{run(*request);}catch(const std::exception& error){request->status=ERROR_INVALID_STATE;strncpy_s(request->message,error.what(),_TRUNCATE);}catch(...){request->status=ERROR_UNHANDLED_EXCEPTION;}
    ReleaseSRWLockExclusive(&operationLock);return request->status;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
