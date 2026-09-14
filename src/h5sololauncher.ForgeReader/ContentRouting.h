#pragma once
#include "ContentFiles.h"

namespace h5runtime::content {
inline decltype(&CreateFile2) nativeOpen=nullptr;
inline decltype(&GetFileAttributesExW) nativeAttributes=nullptr;
using MapCall=bool(__fastcall*)(void*,void*);
using ResourceCall=bool(__fastcall*)(void*,void*,const char*);
inline MapCall nativeMap=nullptr;
inline ResourceCall nativeResource=nullptr;
inline std::array<void*,4> originals{},hooks{};
inline constexpr uint32_t slots[]={0x3284aa8,0x3284a80,0x38f4700+0xb8,0x46c0360+8};
inline std::wstring audioAlias, normalAlias, arcadeAlias, menuAlias, forgeDirectory;
inline const wchar_t* mapped(const wchar_t* path) {
    if(!path)return path;
    for(const auto& file:files)if(_wcsicmp(path,file.original.c_str())==0)return file.target.c_str();
    // These two short names are internal fallbacks for the native resource API,
    // whose path argument is narrow. The real target remains Unicode.
    if(_wcsicmp(path,L"h5solo-campaignnormal.bin")==0 && !normalAlias.empty())return normalAlias.c_str();
    if(_wcsicmp(path,L"h5solo-campaignarcade.bin")==0 && !arcadeAlias.empty())return arcadeAlias.c_str();
    auto alias=[&](const wchar_t* name){
        auto leaf=wcsrchr(path,L'\\');leaf=leaf?leaf+1:path;if(_wcsicmp(leaf,name)!=0)return false;
        wchar_t full[32768];auto count=GetFullPathNameW(path,32768,full,nullptr);if(!count || count>=32768)return false;
        auto prefix=forgeDirectory+L"\\";return _wcsnicmp(full,prefix.c_str(),prefix.size())==0;
    };
    if(alias(L"h5solo-campaignnormal.bin") && !normalAlias.empty())return normalAlias.c_str();
    if(alias(L"h5solo-campaignarcade.bin") && !arcadeAlias.empty())return arcadeAlias.c_str();
    if(alias(L"h5solo-menu.module") && !menuAlias.empty())return menuAlias.c_str();
    if(alias(L"h5solo-campaign.pck") && !audioAlias.empty())return audioAlias.c_str();
    return path;
}
inline HANDLE WINAPI open(LPCWSTR path,DWORD access,DWORD share,DWORD disposition,LPCREATEFILE2_EXTENDED_PARAMETERS parameters) {
    auto actual=mapped(path);
    if(actual!=path && (disposition!=OPEN_EXISTING || (access&(GENERIC_WRITE|DELETE|FILE_WRITE_DATA|FILE_APPEND_DATA)))) {SetLastError(ERROR_ACCESS_DENIED);return INVALID_HANDLE_VALUE;}
    auto result=nativeOpen(actual,access,share,disposition,parameters);auto error=GetLastError();
    if(actual!=path && result!=INVALID_HANDLE_VALUE){
        const File* verified=nullptr;for(const auto& file:files)if(_wcsicmp(actual,file.target.c_str())==0){verified=&file;break;}
        BY_HANDLE_FILE_INFORMATION current{};
        if(!verified || !GetFileInformationByHandle(result,&current) || current.dwVolumeSerialNumber!=verified->identity.dwVolumeSerialNumber || current.nFileIndexHigh!=verified->identity.nFileIndexHigh || current.nFileIndexLow!=verified->identity.nFileIndexLow){CloseHandle(result);result=INVALID_HANDLE_VALUE;error=ERROR_FILE_INVALID;}
    }
    if(actual!=path && result==INVALID_HANDLE_VALUE){InterlockedIncrement(&fileErrors);report("Forge could not open a verified campaign cache file.");}
    SetLastError(error);return result;
}
inline BOOL WINAPI attributes(LPCWSTR path,GET_FILEEX_INFO_LEVELS level,void* data) {return nativeAttributes(mapped(path),level,data);}
inline bool __fastcall map(void* object,void* input) {
    unsigned char candidate[0x1288];
    if(!read(reinterpret_cast<uint64_t>(input),candidate,sizeof(candidate))){report("Forge supplied an unreadable campaign launch request.");return false;}
    uint32_t type,mission,id,length;memcpy(&type,candidate,4);memcpy(&mission,candidate+4,4);memcpy(&id,candidate+8,4);memcpy(&length,candidate+12,4);
    // This vtable belongs to campaign loading; reject unprepared campaign maps.
    for(const auto& route:routes)if(type==1 && route.id==id && route.mission==mission) {
        bool valid=(length==route.source.size() && !memcmp(candidate+20,route.source.data(),length)) || (length==route.target.size() && !memcmp(candidate+20,route.target.data(),length));
        if(!valid)break;
        memset(candidate+20,0,4096);memcpy(candidate+20,route.target.data(),route.target.size());length=static_cast<uint32_t>(route.target.size());memcpy(candidate+12,&length,4);
        auto result=nativeMap(object,candidate);if(result)InterlockedIncrement(&mapsOpened);else report("Forge rejected the prepared campaign launch request.");return result;
    }
    report("The requested mission is unavailable or its module list changed.");return false;
}
inline bool __fastcall resource(void* object,void* output,const char* name) {
    auto result=nativeResource(object,output,name);
    if(!result && name) {
        if(strcmp(name,"__cms__/campaign/campaignnormal.bin")==0 && !normalAlias.empty())return nativeResource(object,output,"h5solo-campaignnormal.bin");
        if(strcmp(name,"__cms__/campaign/campaignarcade.bin")==0 && !arcadeAlias.empty())return nativeResource(object,output,"h5solo-campaignarcade.bin");
    }
    return result;
}
inline bool exchange(unsigned index,void* from,void* to) {
    auto slot=reinterpret_cast<void**>(base+slots[index]);DWORD old=0,unused=0;
    if(!VirtualProtect(slot,8,PAGE_READWRITE,&old))return false;
    auto result=InterlockedCompareExchangePointer(slot,to,from)==from;
    if(!VirtualProtect(slot,8,old,&unused))result=false;return result;
}
inline void restore() {for(unsigned i=0;i<4;++i)if(value<void*>(base+slots[i])==hooks[i])exchange(i,hooks[i],originals[i]);}
inline void installRoutes() {
    wchar_t imagePath[32768];auto length=GetModuleFileNameW(nullptr,imagePath,32768);require(length>0 && length<32768,"Forge's path is unavailable.");forgeDirectory=imagePath;forgeDirectory.resize(forgeDirectory.find_last_of(L'\\'));
    nativeOpen=reinterpret_cast<decltype(nativeOpen)>(GetProcAddress(GetModuleHandleW(L"kernelbase.dll"),"CreateFile2"));
    nativeAttributes=reinterpret_cast<decltype(nativeAttributes)>(GetProcAddress(GetModuleHandleW(L"kernelbase.dll"),"GetFileAttributesExW"));
    nativeMap=reinterpret_cast<MapCall>(base+0x25101b0);nativeResource=reinterpret_cast<ResourceCall>(base+0x1104ca0);
    originals={reinterpret_cast<void*>(nativeOpen),reinterpret_cast<void*>(nativeAttributes),reinterpret_cast<void*>(nativeMap),reinterpret_cast<void*>(nativeResource)};
    hooks={reinterpret_cast<void*>(open),reinterpret_cast<void*>(attributes),reinterpret_cast<void*>(map),reinterpret_cast<void*>(resource)};
    for(unsigned i=0;i<4;++i)require(originals[i] && value<void*>(base+slots[i])==originals[i],"A required campaign file or loading function is already modified.");
    for(const auto& file:files) {
        if(file.original.find(L"\\campaignnormal.bin")!=std::wstring::npos)normalAlias=file.target;
        if(file.original.find(L"\\campaignarcade.bin")!=std::wstring::npos)arcadeAlias=file.target;
        if(file.original.find(L"\\h5solo-campaign.pck")!=std::wstring::npos)audioAlias=file.target;
        if(file.original.find(L"\\h5solo-menu.module")!=std::wstring::npos)menuAlias=file.target;
    }
    require(!normalAlias.empty() && !arcadeAlias.empty() && !audioAlias.empty(),"Campaign variants or campaign audio are missing from this runtime cache.");
    for(unsigned i=0;i<4;++i)if(!exchange(i,originals[i],hooks[i])){restore();throw std::runtime_error("A campaign loading hook could not be installed. Restart Forge before retrying.");}
}
}
