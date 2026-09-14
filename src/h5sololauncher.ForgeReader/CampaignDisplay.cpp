#include "DisplaySettings.h"
#include "DisplayGuards.h"
#include "MenuAssets.h"
#include "MenuState.h"
#include "UpdateJob.h"
namespace h5runtime::display {
struct Request{uint32_t magic,version,operation,status;uint64_t creation;uint32_t phase;float fov;uint32_t frameOption,saveError;char message[512];wchar_t configuration[2048];char sha256[65];unsigned char padding[7];};
static_assert(sizeof(Request)==4720);
static HMODULE library=nullptr;
static uint64_t creation=0,scriptRoot=0,originalPointer=0;
static uint32_t originalLength=0;
static std::string digest;
static std::vector<unsigned char> script;
static volatile LONG attempted=0,error=0;
static char failure[512]{};
static SRWLOCK lock=SRWLOCK_INIT;
static PVOID handler=nullptr;
static bool swapByte(uint32_t rva,unsigned char before,unsigned char after){
    auto site=reinterpret_cast<char*>(image+rva);DWORD old=0,unused=0;if(!VirtualProtect(site,1,PAGE_EXECUTE_READWRITE,&old))return false;
    bool ok=_InterlockedCompareExchange8(site,static_cast<char>(after),static_cast<char>(before))==static_cast<char>(before);
    ok=FlushInstructionCache(GetCurrentProcess(),site,1)!=0 && ok;return VirtualProtect(site,1,old,&unused)!=0 && ok;
}
static void initializeCaught(){
    try{
        require(value<uint64_t>(scriptRoot+20)==originalPointer && value<uint32_t>(scriptRoot+44)==originalLength,"The audio and video settings script changed before setup.");
        DisplayState.initThread=GetCurrentThreadId();auto result=initialize();require(result==0,"Forge could not register campaign field of view.");
        *reinterpret_cast<uint64_t*>(scriptRoot+20)=reinterpret_cast<uint64_t>(script.data());
        *reinterpret_cast<uint32_t*>(scriptRoot+44)=static_cast<uint32_t>(script.size());MemoryBarrier();
    }catch(const std::exception& exception){strncpy_s(failure,exception.what(),_TRUNCATE);InterlockedExchange(&error,1);}
    catch(...){strcpy_s(failure,"Display settings initialization failed.");InterlockedExchange(&error,1);}
}
static void loadSettingsPath(){
    wchar_t path[32768];auto count=GetModuleFileNameW(library,path,32768);require(count>0 && count<32768,"The display helper path is unavailable.");
    std::wstring folder(path);for(unsigned i=0;i<3;++i){auto slash=folder.find_last_of(L'\\');require(slash!=std::wstring::npos,"The display helper is outside its launcher folder.");folder.resize(slash);}
    require(folder.size()>15 && folder.substr(folder.size()-15)==L"\\h5sololauncher","The display helper is outside its launcher folder.");
    auto file=folder+L"\\display.ini";
    auto input=CreateFileW(file.c_str(),GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ,nullptr,OPEN_ALWAYS,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
    require(input!=INVALID_HANDLE_VALUE,"Forge cannot open its display settings file.");BY_HANDLE_FILE_INFORMATION info{};
    bool valid=GetFileInformationByHandle(input,&info) && !(info.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)) && !info.nFileSizeHigh && info.nFileSizeLow<=16384;
    CloseHandle(input);require(valid,"Forge's display settings file is not a regular settings file.");
    wcscpy_s(DisplayState.configPath,file.c_str());auto fov=GetPrivateProfileIntW(L"Campaign",L"FieldOfView",78,file.c_str());
    DisplayState.fov=static_cast<float>(fov>=60 && fov<=120?fov:78);
}
static void run(Request& request){
    require(request.magic==0x44523548 && request.version==1 && request.operation<=1,"The display settings request is incompatible.");auto current=identity(request.creation);
    require(strnlen_s(request.sha256,65)==64 && wcsnlen_s(request.configuration,2048)<2048,"The display settings path or digest is invalid.");
    if(creation)require(creation==request.creation && digest==request.sha256,"Another display configuration owns this Forge session.");
    if(request.operation==1){
        require(!attempted,"Display settings setup was already attempted. Restart Forge.");attempted=1;image=current;creation=request.creation;digest=request.sha256;
        auto menu=inspectMenu(image);require(menu.name==nameId("main_menu") && menu.screen,"Display setup requires the main menu.");guards(image);
        auto input=CreateFileW(request.configuration,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
        require(input!=INVALID_HANDLE_VALUE,"Forge cannot read the display configuration.");LARGE_INTEGER size{};BY_HANDLE_FILE_INFORMATION info{};
        bool ok=GetFileSizeEx(input,&size) && size.QuadPart>0 && size.QuadPart<1024*1024 && GetFileInformationByHandle(input,&info) && !(info.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT));
        std::vector<unsigned char> data(ok?static_cast<size_t>(size.QuadPart):0);DWORD read=0;if(ok)ok=ReadFile(input,data.data(),static_cast<DWORD>(data.size()),&read,nullptr) && read==data.size();CloseHandle(input);
        require(ok && sha(data.data(),data.size())==request.sha256,"The display configuration changed or is damaged.");
        content::Wire wire(data);require(wire.number<uint32_t>()==0x53443548 && wire.number<uint32_t>()==1 && wire.string()=="Microsoft.Halo5Forge_1.194.6192.2_x64__8wekyb3d8bbwe","The display configuration is incompatible.");
        menuAssets::Proof proof{wire.number<uint32_t>(),{wire.number<uint64_t>(),wire.number<uint64_t>()}};auto before=wire.blob(256*1024);script=wire.blob(256*1024);
        require(wire.end() && proof.gid==0x003d87f1 && sha(before.data(),before.size())=="2CA444108AA6EB342AF33D089A23BB767B370D614DE6EAFDAFABF5F960291384" && sha(script.data(),script.size())=="F24E51F97633CD96B2931B263F4B2FB12F9BDAA9691DE98C82D77424DD1A5505","The display script is not the supported launcher adaptation.");
        auto root=menuAssets::resolve(menuAssets::find(image),proof);scriptRoot=root.root;originalPointer=value<uint64_t>(scriptRoot+20);originalLength=value<uint32_t>(scriptRoot+44);
        require(originalLength==before.size() && bytes(originalPointer,originalLength)==before,"The installed display script was already modified.");loadSettingsPath();
        handler=AddVectoredExceptionHandler(1,trap);require(handler!=nullptr,"Could not prepare native display settings.");
        const uint32_t points[]={0x1551ecf,0x152b28e,0x155f483,0xad5217};const unsigned char original[]={0x48,0x8b,0x83,0x65};
        for(unsigned i=0;i<4;++i)if(!swapByte(points[i],original[i],0xcc)){for(unsigned j=i;j>0;--j)swapByte(points[j-1],0xcc,original[j-1]);throw std::runtime_error("Display setup did not finish. Restart Forge.");}
        updateJob::arm(image,initializeCaught);
    }
    request.phase=!attempted?0:updateJob::phase==3 && !error?2:1;request.fov=DisplayState.fov;request.frameOption=DisplayState.lastEnum;request.saveError=DisplayState.saveError;
    if(error || updateJob::fault){request.status=ERROR_INVALID_STATE;strncpy_s(request.message,failure[0]?failure:"Forge could not finish display settings setup. Restart Forge.",_TRUNCATE);}
}
}
extern "C" __declspec(dllexport) DWORD WINAPI H5Display(h5runtime::display::Request* request){using namespace h5runtime::display;if(!request)return ERROR_INVALID_PARAMETER;AcquireSRWLockExclusive(&lock);request->status=0;request->message[0]=0;
    try{run(*request);}catch(const std::exception& exception){request->status=ERROR_INVALID_STATE;strncpy_s(request->message,exception.what(),_TRUNCATE);}catch(...){request->status=ERROR_UNHANDLED_EXCEPTION;}ReleaseSRWLockExclusive(&lock);return request->status;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){h5runtime::display::library=instance;DisableThreadLibraryCalls(instance);}return TRUE;}
