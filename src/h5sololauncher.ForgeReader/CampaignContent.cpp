#include "ContentRouting.h"
#include "MenuState.h"
#include "RegistryShared.h"
#include "ContentGuards.h"
#define H5_UPDATE_JOB_NAMESPACE contentUpdate
#include "UpdateJob.h"

using namespace h5runtime;
namespace campaignContent {
struct Request {
    uint32_t magic,version,operation,status;
    uint64_t creation;
    uint32_t phase,files,checked,opened;
    char message[512];
    wchar_t configuration[2048];
    char sha256[65]; unsigned char padding[7];
};
static_assert(sizeof(Request)==4720);
static SRWLOCK lock=SRWLOCK_INIT;
static Menu menu{};
static uint64_t oldMembership=0,newMembership=0;
struct RowBackup{uint64_t address;std::vector<unsigned char> bytes;uint32_t length;};
static std::vector<RowBackup> backups;
static bool membershipActive=false,workFailed=false;
template<class T>T at(uint32_t rva){return reinterpret_cast<T>(content::base+rva);}
static void restoreMembership() {
    if(membershipActive){at<void(__fastcall*)(void*)>(0x683a00)(at<void*>(0x5eb0bc0));InterlockedCompareExchangePointer(at<void*volatile*>(0x5eb0c80),reinterpret_cast<void*>(oldMembership),reinterpret_cast<void*>(newMembership));at<void(__fastcall*)(void*)>(0x683a20)(at<void*>(0x5eb0bc0));membershipActive=false;}
    for(auto& backup:backups){memcpy(reinterpret_cast<void*>(backup.address+0x150),backup.bytes.data(),backup.bytes.size());*reinterpret_cast<uint32_t*>(backup.address+0x148)=backup.length;}
}
static void prepareMembership() {
    auto base=content::base;requireOwnedRegistry(content::creation);auto now=inspectMenu(base);
    require(now.receiver==menu.receiver && now.definition==menu.definition && now.node==menu.node,"The menu changed before campaign content became active.");
    auto levels=value<uint64_t>(base+0x5eb0c90);auto count=value<uint32_t>(levels+0x50);auto data=value<uint64_t>(levels+0x58);
    require(count==19 && value<uint64_t>(levels+0x20)==0x13c0,"The prepared native map array changed.");
    oldMembership=value<uint64_t>(base+0x5eb0c80);
    require(value<uint64_t>(oldMembership+0x20)==0x2c8 && value<uint32_t>(oldMembership+0x50)==1,"The native campaign availability array changed.");
    auto oldRow=value<uint64_t>(oldMembership+0x58);auto original=bytes(oldRow,0x2c8);
    for(size_t i=0x188;i<0x288;++i)require(original[i]==0xff,"Campaign availability has already been modified.");
    for(size_t i=0x288;i<0x2c8;++i)require(original[i]==0,"Campaign availability has already been modified.");
    for(const auto& route:content::routes) {
        unsigned matches=0;
        for(unsigned i=0;i<count;++i){auto row=data+i*0x13c0;if(value<uint32_t>(row+4)!=route.id)continue;++matches;
            require(value<uint32_t>(row+0x148)==route.source.size() && bytes(row+0x150,route.source.size())==route.source,"The registered campaign source list changed.");
            backups.push_back({row,bytes(row+0x150,4096),value<uint32_t>(row+0x148)});
        }
        require(matches==1,"An available campaign map is missing from the registered catalogue.");
    }
    auto array=at<void*(__fastcall*)(const char*,unsigned,unsigned,uint64_t,unsigned,void*,unsigned char)>(0x655440)("campaigns",0x10,4,0x2c8,0,value<void*>(oldMembership+0x40),2);
    require(array!=nullptr,"Forge could not allocate campaign availability.");newMembership=reinterpret_cast<uint64_t>(array);at<void(__fastcall*)(void*)>(0x655430)(array);
    auto index=at<uint32_t(__fastcall*)(void*)>(0x655880)(array);require(index!=0xffffffff && (index&0xffff)<4,"Forge could not allocate its campaign row.");
    auto row=value<uint64_t>(newMembership+0x58)+(index&0xffff)*0x2c8;
    memcpy(reinterpret_cast<void*>(row+2),original.data()+2,0x2c6);memset(reinterpret_cast<void*>(row+0x188),0xff,0x100);memset(reinterpret_cast<void*>(row+0x288),0,64);
    for(unsigned i=0;i<content::routes.size();++i) {
        const auto& route=content::routes[i];auto destination=backups[i].address;
        memset(reinterpret_cast<void*>(destination+0x150),0,4096);memcpy(reinterpret_cast<void*>(destination+0x150),route.target.data(),route.target.size());
        *reinterpret_cast<uint32_t*>(destination+0x148)=static_cast<uint32_t>(route.target.size());
        require(at<bool(__fastcall*)(void*)>(0x1101d50)(reinterpret_cast<void*>(destination+0x148)),"Forge rejected a normalized campaign module list.");
        *reinterpret_cast<uint32_t*>(row+0x188+i*4)=route.id;*reinterpret_cast<unsigned char*>(row+0x288+i)=1;
    }
    content::installRoutes();
    at<void(__fastcall*)(void*)>(0x683a00)(at<void*>(0x5eb0bc0));
    auto swapped=InterlockedCompareExchangePointer(at<void*volatile*>(0x5eb0c80),reinterpret_cast<void*>(newMembership),reinterpret_cast<void*>(oldMembership))==reinterpret_cast<void*>(oldMembership);
    at<void(__fastcall*)(void*)>(0x683a20)(at<void*>(0x5eb0bc0));membershipActive=swapped;require(swapped,"Campaign availability changed before publication.");
}
static void workCaught(){try{prepareMembership();}catch(const std::exception& error){content::report(error.what());workFailed=true;}catch(...){content::report("Campaign runtime setup failed.");workFailed=true;}}
static void work(){
    __try{workCaught();}__except(EXCEPTION_EXECUTE_HANDLER){content::report("A native fault interrupted campaign content setup. Restart Forge.");workFailed=true;}
    if(workFailed){content::restore();restoreMembership();InterlockedExchange(&content::phase,5);}else InterlockedExchange(&content::phase,4);
}
static void run(Request& request) {
    require(request.magic==0x35435448 && request.version==1 && request.operation<=4,"The campaign content request is incompatible.");auto base=identity(request.creation);
    if(request.operation!=4)require(strnlen_s(request.sha256,65)==64,"The campaign configuration digest is invalid.");
    if(content::creation)require(content::creation==request.creation && (request.operation==4 || content::configDigest==request.sha256),"Forge is already preparing content from another cache.");
    if(request.operation==1) {
        require(content::phase==0,"The content preparation was already attempted. Restart Forge to retry.");requireOwnedRegistry(request.creation);contentGuards(base);
        require(wcsnlen_s(request.configuration,2048)<2048,"The runtime configuration path is too long.");
        auto file=CreateFileW(request.configuration,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
        require(file!=INVALID_HANDLE_VALUE,"Forge cannot read its runtime configuration.");LARGE_INTEGER size{};BY_HANDLE_FILE_INFORMATION info{};
        bool ok=GetFileSizeEx(file,&size) && size.QuadPart>0 && size.QuadPart<4*1024*1024 && GetFileInformationByHandle(file,&info) && !(info.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT));
        std::vector<unsigned char> bytes(ok?static_cast<size_t>(size.QuadPart):0);DWORD read=0;if(ok)ok=ReadFile(file,bytes.data(),static_cast<DWORD>(bytes.size()),&read,nullptr) && read==bytes.size();CloseHandle(file);
        require(ok && sha(bytes.data(),bytes.size())==request.sha256,"The runtime configuration is damaged or changed.");
        content::parse(bytes);content::base=base;content::creation=request.creation;content::configDigest=request.sha256;
        InterlockedExchange(&content::phase,1);auto thread=CreateThread(nullptr,0,content::verifyAll,nullptr,0,nullptr);
        if(!thread){InterlockedExchange(&content::phase,5);throw std::runtime_error("The campaign file verifier could not be started.");}CloseHandle(thread);
    }
    if(request.operation==2) {
        require(content::phase==2 && !content::stop,"Verify generated game files before activating campaign content.");requireOwnedRegistry(request.creation);contentGuards(base);menu=inspectMenu(base);
        require(menu.receiver && menu.screen && (menu.name==nameId("title_screen") || menu.name==nameId("main_menu")),"Wait for Forge's title or main menu before activating campaign content.");
        InterlockedExchange(&content::phase,3);contentUpdate::arm(base,work);
    }
    if(request.operation==3){require(content::phase<=2,"Campaign setup is already active in Forge. Close Forge to end this session.");InterlockedExchange(&content::stop,1);}
    request.phase=static_cast<uint32_t>(InterlockedCompareExchange(&content::phase,0,0));request.files=static_cast<uint32_t>(content::files.size());request.checked=static_cast<uint32_t>(InterlockedCompareExchange(&content::checkedFiles,0,0));request.opened=static_cast<uint32_t>(InterlockedCompareExchange(&content::mapsOpened,0,0));
    if(request.phase==3 && contentUpdate::phase==4){InterlockedExchange(&content::phase,5);request.phase=5;content::report("Forge's campaign update did not finish. Restart Forge before retrying.");}
    AcquireSRWLockShared(&content::errorLock);
    if(request.phase==5 || content::failure[0]){strncpy_s(request.message,content::failure,_TRUNCATE);request.status=ERROR_INVALID_STATE;}
    ReleaseSRWLockShared(&content::errorLock);
}
}
extern "C" __declspec(dllexport) DWORD WINAPI H5Content(campaignContent::Request* request) {
    if(!request)return ERROR_INVALID_PARAMETER;AcquireSRWLockExclusive(&campaignContent::lock);request->status=0;request->message[0]=0;
    try{campaignContent::run(*request);}catch(const std::exception& error){request->status=ERROR_INVALID_STATE;strncpy_s(request->message,error.what(),_TRUNCATE);}catch(...){request->status=ERROR_UNHANDLED_EXCEPTION;}
    ReleaseSRWLockExclusive(&campaignContent::lock);return request->status;
}
