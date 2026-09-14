#include "MenuAssets.h"
#include "UiGuards.h"
#include <intrin.h>

namespace h5runtime::campaignUi {
struct Request{uint32_t magic,version,operation,status;uint64_t creation;uint32_t phase,retained,rebound,rejected;char message[512];wchar_t configuration[2048];char sha256[65];unsigned char padding[7];};
static_assert(sizeof(Request)==4720);
static std::vector<menuAssets::Root> entries;
static std::vector<menuAssets::Proof> versions;
static uint64_t base=0,creation=0;
static uint64_t unicodeType=0;
static std::string digest;
static volatile LONG armed=0,rebound=0,rejected=0,hits=0,fault=0;
static bool attempted=false;
static PVOID handler=nullptr;
static SRWLOCK operationLock=SRWLOCK_INIT;
static bool version(uint32_t gid,uint64_t asset,uint64_t checksum){for(const auto& item:versions)if(item.gid==gid && item.key.asset==asset && item.key.checksum==checksum)return true;return false;}
static bool valid(menuAssets::Root& entry){
    auto record=reinterpret_cast<unsigned char*>(entry.record);if(*reinterpret_cast<uint32_t*>(record)!=entry.handle || *reinterpret_cast<uint64_t*>(record+8)!=0 || *reinterpret_cast<uint64_t*>(record+16)!=entry.proof.key.asset || *reinterpret_cast<uint32_t*>(record+76)!=entry.proof.gid)return false;
    auto root=*reinterpret_cast<uint64_t*>(record+40);auto checksum=*reinterpret_cast<uint64_t*>(record+24);if(root<0x10000 || *reinterpret_cast<uint32_t*>(root+8)!=entry.proof.gid || *reinterpret_cast<uint32_t*>(root+12)!=entry.handle)return false;
    if(root!=entry.root || checksum!=entry.proof.key.checksum){if(*reinterpret_cast<uint64_t*>(root)!=unicodeType || !version(entry.proof.gid,entry.proof.key.asset,entry.proof.key.checksum) || !version(entry.proof.gid,entry.proof.key.asset,checksum))return false;entry.root=root;entry.proof.key.checksum=checksum;InterlockedIncrement(&rebound);}
    return true;
}
static LONG CALLBACK trap(EXCEPTION_POINTERS* exception){
    if(exception->ExceptionRecord->ExceptionCode!=EXCEPTION_BREAKPOINT || reinterpret_cast<uint64_t>(exception->ExceptionRecord->ExceptionAddress)!=base+0x29eecbc)return EXCEPTION_CONTINUE_SEARCH;
    if(armed){InterlockedIncrement(&hits);
        __try{for(auto& entry:entries){if(!valid(entry)){InterlockedIncrement(&rejected);continue;}auto flags=reinterpret_cast<uint16_t*>(entry.record+80);if((*flags&0xa000)==0xa000)*flags&=0x7fff;}}
        __except(EXCEPTION_EXECUTE_HANDLER){InterlockedExchange(&fault,1);}
    }
    // Native cache marking has just completed. Preserve the original epilogue.
    exception->ContextRecord->Rbx=*reinterpret_cast<uint64_t*>(exception->ContextRecord->Rsp+0x58);exception->ContextRecord->Rip=base+0x29eecbc+5;return EXCEPTION_CONTINUE_EXECUTION;
}
static void load(const Request& request){
    auto file=CreateFileW(request.configuration,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);require(file!=INVALID_HANDLE_VALUE,"Forge cannot read the native menu retention configuration.");
    LARGE_INTEGER size{};BY_HANDLE_FILE_INFORMATION info{};bool ok=GetFileSizeEx(file,&size) && size.QuadPart>0 && size.QuadPart<512*1024 && GetFileInformationByHandle(file,&info) && !(info.dwFileAttributes&(FILE_ATTRIBUTE_REPARSE_POINT|FILE_ATTRIBUTE_DIRECTORY));
    std::vector<unsigned char> data(ok?static_cast<size_t>(size.QuadPart):0);DWORD read=0;if(ok)ok=ReadFile(file,data.data(),static_cast<DWORD>(data.size()),&read,nullptr) && read==data.size();CloseHandle(file);require(ok && sha(data.data(),data.size())==request.sha256,"The native menu retention configuration changed or is damaged.");
    content::Wire wire(data);require(wire.number<uint32_t>()==0x49553548 && wire.number<uint32_t>()==1 && wire.string()=="Microsoft.Halo5Forge_1.194.6192.2_x64__8wekyb3d8bbwe","The native menu retention configuration is incompatible.");
    auto count=wire.number<uint32_t>();require(count>0 && count<=4096,"The native menu retention set exceeds its supported size.");auto roots=menuAssets::find(base);std::set<uint32_t> seen;
    auto proof=[&](){return menuAssets::Proof{wire.number<uint32_t>(),{wire.number<uint64_t>(),wire.number<uint64_t>()}};};
    for(unsigned i=0;i<count;++i){auto identity=proof();require(seen.insert(identity.gid).second,"A native menu asset is listed twice.");auto entry=menuAssets::resolve(roots,identity);require(value<uint64_t>(entry.record+8)==0,"A native menu asset is being replaced. Restart Forge before preparing it.");entries.push_back(entry);}
    for(auto required:{0x43ecf1e4u,0xbb680c37u,0x407c17u,0x40c2c9u,0x5da21966u,0x357f0fecu,0x4435a884u,0xb719da90u})require(seen.count(required)!=0,"The prepared native menu closure is missing a required control.");
    count=wire.number<uint32_t>();require(count<=8192,"The native string version catalogue exceeds its supported size.");
    require(roots.count(0xd0942c43)!=0,"The campaign localization root is missing.");unicodeType=value<uint64_t>(roots.at(0xd0942c43).root);
    for(unsigned i=0;i<count;++i){auto item=proof();require(seen.count(item.gid)!=0 && item.gid!=0xd0942c43 && value<uint64_t>(roots.at(item.gid).root)==unicodeType,"A string version does not belong to an immutable retained dictionary.");versions.push_back(item);}
    require(wire.end(),"The native menu retention configuration contains trailing data.");
}
static void run(Request& request){
    require(request.magic==0x52553548 && request.version==1 && request.operation<=1,"The native UI request is incompatible.");auto currentBase=identity(request.creation);
    require(strnlen_s(request.sha256,65)==64 && wcsnlen_s(request.configuration,2048)<2048,"The native UI configuration path or digest is invalid.");
    if(creation)require(creation==request.creation && digest==request.sha256,"Another native menu closure already owns this Forge session.");
    if(request.operation==1){require(!attempted,"Native UI preparation was already attempted. Restart Forge before retrying.");base=currentBase;uiGuards(base);creation=request.creation;digest=request.sha256;attempted=true;load(request);
        handler=AddVectoredExceptionHandler(1,trap);require(handler!=nullptr,"Could not prepare native menu retention.");auto point=reinterpret_cast<volatile char*>(base+0x29eecbc);DWORD old=0,unused=0;
        require(VirtualProtect(const_cast<char*>(point),1,PAGE_EXECUTE_READWRITE,&old)!=0,"Could not prepare the native UI cache callback.");
        InterlockedExchange(&armed,1);auto changed=_InterlockedCompareExchange8(point,static_cast<char>(0xcc),0x48)==0x48;auto flushed=FlushInstructionCache(GetCurrentProcess(),const_cast<char*>(point),1);auto restored=VirtualProtect(const_cast<char*>(point),1,old,&unused);
        require(changed && flushed && restored,"Native UI cache retention could not be installed. Restart Forge.");
    }
    request.phase=armed?1:attempted?2:0;request.retained=static_cast<uint32_t>(entries.size());request.rebound=rebound;request.rejected=rejected;
    if(fault){request.status=ERROR_INVALID_STATE;strcpy_s(request.message,"A native menu asset changed during campaign loading. Copy the launch details.");}
}
}
extern "C" __declspec(dllexport) DWORD WINAPI H5Ui(h5runtime::campaignUi::Request* request){using namespace h5runtime::campaignUi;
    if(!request)return ERROR_INVALID_PARAMETER;AcquireSRWLockExclusive(&operationLock);request->status=0;request->message[0]=0;
    try{run(*request);}catch(const std::exception& error){request->status=ERROR_INVALID_STATE;strncpy_s(request->message,error.what(),_TRUNCATE);}catch(...){request->status=ERROR_UNHANDLED_EXCEPTION;}
    ReleaseSRWLockExclusive(&operationLock);return request->status;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
