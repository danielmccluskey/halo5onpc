#include "MoviePlayback.h"
#include "MovieGuards.h"
#include "ContentFiles.h"

namespace h5runtime::movie {
struct Request{uint32_t magic,version,operation,status;uint64_t creation;uint32_t phase,redirects,viewport,clock;char message[512];wchar_t configuration[2048];char sha256[65];unsigned char padding[7];};
static_assert(sizeof(Request)==4720);
using BinkOpen=void*(WINAPI*)(const char*,uint32_t);
using BinkUnary=void(WINAPI*)(void*);
inline BinkOpen nativeOpen=nullptr;inline BinkUnary nativeNext=nullptr,nativeClose=nullptr;
inline decltype(&CreateFile2) nativeFile=nullptr;
static content::File file;
static uint64_t creation=0;
static std::string digest,forgePath;
static std::wstring forgeDirectory;
static volatile LONG phase=0,redirects=0;
static char failure[512]{};
static SRWLOCK failureLock=SRWLOCK_INIT;
static void fail(const char* message){AcquireSRWLockExclusive(&failureLock);if(!failure[0])strncpy_s(failure,message,_TRUNCATE);ReleaseSRWLockExclusive(&failureLock);InterlockedExchange(&phase,3);}
static SRWLOCK operationLock=SRWLOCK_INIT;
static HANDLE WINAPI openFile(LPCWSTR name,DWORD access,DWORD share,DWORD disposition,LPCREATEFILE2_EXTENDED_PARAMETERS parameters){
    auto incoming=GetLastError();bool replace=false;
    if(name){auto leaf=wcsrchr(name,L'\\');leaf=leaf?leaf+1:name;if(_wcsicmp(leaf,L"h5solo-opening.bk2")==0){wchar_t full[32768];auto count=GetFullPathNameW(name,32768,full,nullptr);auto prefix=forgeDirectory+L"\\";replace=count>0 && count<32768 && _wcsnicmp(full,prefix.c_str(),prefix.size())==0;}}
    if(replace && (disposition!=OPEN_EXISTING || (access&(GENERIC_WRITE|DELETE|FILE_WRITE_DATA|FILE_APPEND_DATA)))){SetLastError(ERROR_ACCESS_DENIED);return INVALID_HANDLE_VALUE;}
    SetLastError(incoming);auto handle=nativeFile(replace?file.target.c_str():name,access,share,disposition,parameters);auto error=GetLastError();
    if(replace && handle!=INVALID_HANDLE_VALUE){BY_HANDLE_FILE_INFORMATION info{};if(!GetFileInformationByHandle(handle,&info) || info.dwVolumeSerialNumber!=file.identity.dwVolumeSerialNumber || info.nFileIndexHigh!=file.identity.nFileIndexHigh || info.nFileIndexLow!=file.identity.nFileIndexLow){CloseHandle(handle);handle=INVALID_HANDLE_VALUE;error=ERROR_FILE_INVALID;}}
    SetLastError(error);return handle;
}
static bool requested(const char* path,uint32_t flags){if(!path || (flags&0x0c000000))return false;return _stricmp(path,"bink\\cin_010_halsey_60.bk2")==0 || _stricmp(path,"bink/cin_010_halsey_60.bk2")==0 || _stricmp(path,(forgePath+"\\bink\\cin_010_halsey_60.bk2").c_str())==0;}
static void* WINAPI open(const char* path,uint32_t flags){auto eligible=phase==2 && requested(path,flags);auto handle=nativeOpen(path,flags);auto error=GetLastError();
    if(!handle && eligible){SetLastError(error);handle=nativeOpen("h5solo-opening.bk2",flags);error=GetLastError();if(handle){reset();InterlockedExchange64(reinterpret_cast<volatile LONG64*>(&current),reinterpret_cast<LONG64>(handle));InterlockedIncrement(&redirects);}else fail("Forge's movie decoder could not open the verified campaign opening movie.");}
    SetLastError(error);return handle;
}
static void WINAPI next(void* handle){auto incoming=GetLastError();beforeFrame(handle);SetLastError(incoming);nativeNext(handle);auto error=GetLastError();afterFrame(handle);
    if(current==reinterpret_cast<uint64_t>(handle)){
        if(viewport>1)fail("The opening movie's native display layout changed. Copy details for the movie status.");
        else if(clockResult>1 && clockResult!=10)fail("The opening movie's audio clock could not be aligned. Copy details for the movie status.");
    }
    SetLastError(error);}
static void WINAPI close(void* handle){if(current==reinterpret_cast<uint64_t>(handle))InterlockedExchange64(reinterpret_cast<volatile LONG64*>(&current),0);nativeClose(handle);}
static bool exchange(uint64_t address,void* from,void* to){auto slot=reinterpret_cast<void**>(address);DWORD old=0,unused=0;if(!VirtualProtect(slot,8,PAGE_READWRITE,&old))return false;auto changed=InterlockedCompareExchangePointer(slot,to,from)==from;return VirtualProtect(slot,8,old,&unused) && changed;}
static void activate(){
    movieGuards(base,bink);content::verify(file);require(file.length==355451652,"The campaign opening movie has an unsupported length.");
    LARGE_INTEGER start{};uint32_t header[11]{};DWORD read=0;
    require(SetFilePointerEx(file.locked,start,nullptr,FILE_BEGIN) && ReadFile(file.locked,header,sizeof(header),&read,nullptr) && read==sizeof(header) && header[0]==0x6932424b && header[2]==7966 && header[5]==1920 && header[6]==1080 && header[7]==2997 && header[8]==100,"The campaign opening movie has an unsupported header.");
    nativeOpen=reinterpret_cast<BinkOpen>(GetProcAddress(reinterpret_cast<HMODULE>(bink),"BinkOpen"));nativeNext=reinterpret_cast<BinkUnary>(GetProcAddress(reinterpret_cast<HMODULE>(bink),"BinkNextFrame"));nativeClose=reinterpret_cast<BinkUnary>(GetProcAddress(reinterpret_cast<HMODULE>(bink),"BinkClose"));
    nativeFile=reinterpret_cast<decltype(nativeFile)>(GetProcAddress(GetModuleHandleW(L"kernelbase.dll"),"CreateFile2"));
    require(nativeOpen && nativeNext && nativeClose && nativeFile && value<void*>(base+0x32856e0)==reinterpret_cast<void*>(nativeOpen) && value<void*>(base+0x3285640)==reinterpret_cast<void*>(nativeNext) && value<void*>(base+0x3285630)==reinterpret_cast<void*>(nativeClose) && value<void*>(bink+0x31068)==reinterpret_cast<void*>(nativeFile),"A movie playback function is already modified or has changed.");
    require(exchange(bink+0x31068,reinterpret_cast<void*>(nativeFile),reinterpret_cast<void*>(openFile)),"Could not prepare Unicode campaign movie access. Restart Forge.");
    require(exchange(base+0x32856e0,reinterpret_cast<void*>(nativeOpen),reinterpret_cast<void*>(open)) && exchange(base+0x3285640,reinterpret_cast<void*>(nativeNext),reinterpret_cast<void*>(next)) && exchange(base+0x3285630,reinterpret_cast<void*>(nativeClose),reinterpret_cast<void*>(close)),"Could not install campaign movie playback. Restart Forge.");
    InterlockedExchange(&phase,2);
}
static void activateCaught(){try{activate();}catch(const std::exception& error){fail(error.what());}catch(...){fail("Campaign movie preparation failed.");}}
static DWORD WINAPI background(void*){__try{activateCaught();}__except(EXCEPTION_EXECUTE_HANDLER){fail("A native fault interrupted campaign movie preparation. Restart Forge.");}return 0;}
static void run(Request& request){require(request.magic==0x55563548 && request.version==1 && request.operation<=1,"The campaign movie request is incompatible.");auto currentBase=identity(request.creation);
    require(strnlen_s(request.sha256,65)==64 && wcsnlen_s(request.configuration,2048)<2048,"The campaign movie configuration path or digest is invalid.");if(creation)require(creation==request.creation && digest==request.sha256,"Another campaign movie cache owns this Forge session.");
    if(request.operation==1){require(phase==0,"Campaign movie preparation was already attempted. Restart Forge before retrying.");base=currentBase;bink=reinterpret_cast<uint64_t>(GetModuleHandleW(L"bink2winrt_x64.uni10.dll"));require(bink!=0,"Forge's native movie decoder is not loaded.");
        wchar_t modulePath[32768],imagePath[32768];require(GetModuleFileNameW(nullptr,imagePath,32768)>0 && GetModuleFileNameW(reinterpret_cast<HMODULE>(bink),modulePath,32768)>0,"The native movie decoder path is unavailable.");forgeDirectory=imagePath;forgeDirectory.resize(forgeDirectory.find_last_of(L'\\'));forgePath=utf8(forgeDirectory);require(_wcsicmp(modulePath,(forgeDirectory+L"\\bink2winrt_x64.uni10.dll").c_str())==0,"Forge loaded its movie decoder from an unexpected location.");
        auto input=CreateFileW(request.configuration,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);require(input!=INVALID_HANDLE_VALUE,"Forge cannot read the campaign movie configuration.");LARGE_INTEGER size{};BY_HANDLE_FILE_INFORMATION info{};bool ok=GetFileSizeEx(input,&size) && size.QuadPart>0 && size.QuadPart<16384 && GetFileInformationByHandle(input,&info) && !(info.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT));std::vector<unsigned char> data(ok?static_cast<size_t>(size.QuadPart):0);DWORD read=0;if(ok)ok=ReadFile(input,data.data(),static_cast<DWORD>(data.size()),&read,nullptr) && read==data.size();CloseHandle(input);require(ok && sha(data.data(),data.size())==request.sha256,"The campaign movie configuration changed or is damaged.");
        content::Wire wire(data);require(wire.number<uint32_t>()==0x564d3548 && wire.number<uint32_t>()==1 && wire.string()=="Microsoft.Halo5Forge_1.194.6192.2_x64__8wekyb3d8bbwe","The campaign movie configuration is incompatible.");auto root=content::wide(wire.string());auto relative=wire.string();require(root.size()>3 && root[1]==':' && root[2]=='\\' && relative.rfind("game/movies/",0)==0 && content::suffix(relative,"/cin_010_halsey_60.bk2"),"The campaign movie must belong to its generated cache.");file.target=root+L"\\"+content::relative(relative);file.length=wire.number<uint64_t>();auto hash=wire.blob(32);require(hash.size()==32 && wire.end(),"The campaign movie configuration has an invalid digest or trailing data.");memcpy(file.digest.data(),hash.data(),32);
        creation=request.creation;digest=request.sha256;InterlockedExchange(&phase,1);auto thread=CreateThread(nullptr,0,background,nullptr,0,nullptr);require(thread!=nullptr,"Could not start movie verification.");CloseHandle(thread);
    }
    request.phase=InterlockedCompareExchange(&phase,0,0);request.redirects=redirects;request.viewport=viewport;request.clock=clockResult;if(request.phase==3){request.status=ERROR_INVALID_STATE;AcquireSRWLockShared(&failureLock);strncpy_s(request.message,failure,_TRUNCATE);ReleaseSRWLockShared(&failureLock);}
}
}
extern "C" __declspec(dllexport) DWORD WINAPI H5Movie(h5runtime::movie::Request* request){using namespace h5runtime::movie;if(!request)return ERROR_INVALID_PARAMETER;AcquireSRWLockExclusive(&operationLock);request->status=0;request->message[0]=0;
    try{run(*request);}catch(const std::exception& error){request->status=ERROR_INVALID_STATE;strncpy_s(request->message,error.what(),_TRUNCATE);}catch(...){request->status=ERROR_UNHANDLED_EXCEPTION;}
    ReleaseSRWLockExclusive(&operationLock);return request->status;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
