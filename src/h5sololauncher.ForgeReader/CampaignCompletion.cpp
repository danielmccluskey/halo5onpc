#include "CompletionLifecycle.h"
#include "CompletionRecipeGuards.h"
#include "MenuState.h"
namespace h5runtime::completion {
struct Request{uint32_t magic,version,operation,status;uint64_t creation;uint32_t phase,installed,events,map;char message[512];wchar_t configuration[2048];char sha256[65];unsigned char padding[7];};
static_assert(sizeof(Request)==4720);
static HMODULE library=nullptr;
static SRWLOCK operationLock=SRWLOCK_INIT;
static std::string digest;
static std::vector<unsigned char> replacementLua;
static bool attempted=false;
static void resource(unsigned id,char* target,size_t capacity){auto info=FindResourceW(library,MAKEINTRESOURCEW(id),RT_RCDATA);require(info!=nullptr,"The launcher is missing a mission report resource.");auto size=SizeofResource(library,info);auto loaded=LoadResource(library,info);require(size>0 && size<capacity && loaded,"A mission report resource has an invalid size.");auto data=LockResource(loaded);require(data && !memchr(data,0,size),"A mission report resource contains invalid text.");memcpy(target,data,size);target[size]=0;}
static void arm(){auto& state=CompletionState;completionGuards(base());auto menu=inspectMenu(base());require(menu.name==nameId("main_menu") && menu.screen,"Mission completion setup requires the main menu.");
    auto roots=menuAssets::find(base());auto wpf=menuAssets::resolve(roots,reportHosts[0].proof);auto lua=menuAssets::resolve(roots,reportHosts[1].proof);
    for(unsigned i=0;i<2;++i){auto root=i==0?wpf.root:lua.root;auto offset=i==0?160:20;auto pointer=value<uint64_t>(root+offset);auto length=value<uint32_t>(root+offset+24);require(length==reportHosts[i].before.size() && bytes(pointer,length)==reportHosts[i].before,"A native mission report has already changed.");}
    require(sha(reportHosts[1].before.data(),reportHosts[1].before.size())==completionUiBefore && sha(reportHosts[1].after.data(),reportHosts[1].after.size())==completionUiAfter,"The mission report script is not the supported launcher adaptation.");
    state.wpfRoot=wpf.root;state.originalXml=value<uint64_t>(wpf.root+160);state.originalLength=value<uint32_t>(wpf.root+184);resource(101,state.source,sizeof(state.source));resource(102,state.xmlTemplate,sizeof(state.xmlTemplate));
    replacementLua=reportHosts[1].after;handler=AddVectoredExceptionHandler(1,trap);require(handler!=nullptr,"Could not prepare native mission completion.");state.armed=1;
    const uint64_t points[]={updateRva,wonRva,visibleRva,loomOptionsRva,loomPathRva};const unsigned char original[]={0x41,0x48,0x80,0x48,0x48};
    for(unsigned i=0;i<5;++i)if(!swapByte(base()+points[i],original[i],0xcc)){for(unsigned j=i;j>0;--j)swapByte(base()+points[j-1],0xcc,original[j-1]);state.armed=0;state.error=25;throw std::runtime_error("Mission completion setup did not finish. Restart Forge.");}
    *reinterpret_cast<uint64_t*>(lua.root+20)=reinterpret_cast<uint64_t>(replacementLua.data());*reinterpret_cast<uint32_t*>(lua.root+44)=static_cast<uint32_t>(replacementLua.size());MemoryBarrier();record("armed|mission completion ready");
}
static void run(Request& request){require(request.magic==0x52503548 && request.version==1 && request.operation<=2,"The mission completion request is incompatible.");auto image=identity(request.creation);auto& state=CompletionState;
    require(strnlen_s(request.sha256,65)==64 && wcsnlen_s(request.configuration,2048)<2048,"The mission completion path or digest is invalid.");if(state.creation)require(state.creation==request.creation && digest==request.sha256,"Another mission completion configuration owns this Forge session.");
    if(request.operation==1){require(!attempted,"Mission completion setup was already attempted. Restart Forge.");attempted=true;state.base=image;state.creation=request.creation;digest=request.sha256;
        auto file=CreateFileW(request.configuration,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);require(file!=INVALID_HANDLE_VALUE,"Forge cannot read the mission completion configuration.");LARGE_INTEGER size{};BY_HANDLE_FILE_INFORMATION info{};
        bool ok=GetFileSizeEx(file,&size) && size.QuadPart>0 && size.QuadPart<4*1024*1024 && GetFileInformationByHandle(file,&info) && !(info.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT));std::vector<unsigned char> data(ok?static_cast<size_t>(size.QuadPart):0);DWORD read=0;if(ok)ok=ReadFile(file,data.data(),static_cast<DWORD>(data.size()),&read,nullptr) && read==data.size();CloseHandle(file);require(ok && sha(data.data(),data.size())==request.sha256,"The mission completion configuration changed or is damaged.");parseConfiguration(data);arm();
    }
    if(request.operation==2){require(state.armed && state.installed && !state.error && !state.phase && !state.command,"A diagnostic ending requires an active mission with completion support.");InterlockedExchange(&state.command,1);}
    request.phase=state.armed?static_cast<uint32_t>(state.phase)+1:0;request.installed=state.installed;request.events=state.events;request.map=activeMap;
    if(state.error){request.status=ERROR_INVALID_STATE;_snprintf_s(request.message,_TRUNCATE,"Mission completion error %ld: %s",state.error,state.message);}else if(state.events)strncpy_s(request.message,state.reports[(state.events-1)%64],_TRUNCATE);
}
}
extern "C" __declspec(dllexport) DWORD WINAPI H5Completion(h5runtime::completion::Request* request){using namespace h5runtime::completion;if(!request)return ERROR_INVALID_PARAMETER;AcquireSRWLockExclusive(&operationLock);request->status=0;request->message[0]=0;
    try{run(*request);}catch(const std::exception& error){request->status=ERROR_INVALID_STATE;strncpy_s(request->message,error.what(),_TRUNCATE);}catch(...){request->status=ERROR_UNHANDLED_EXCEPTION;}ReleaseSRWLockExclusive(&operationLock);return request->status;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){h5runtime::completion::library=instance;DisableThreadLibraryCalls(instance);}return TRUE;}
