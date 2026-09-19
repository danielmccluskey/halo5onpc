#pragma once
#include "RuntimeSupport.h"
#include <array>
#include <set>
#include <sstream>

namespace h5runtime::content {
struct File { std::wstring original, target; uint64_t length; std::array<unsigned char,32> digest; HANDLE locked = nullptr; BY_HANDLE_FILE_INFORMATION identity{}; };
struct Route { uint32_t id, mission, next; std::string scenario; std::vector<unsigned char> source, target; };
inline std::vector<File> files;
inline std::vector<Route> routes;
inline std::string configDigest;
inline uint64_t base = 0, creation = 0;
inline volatile LONG phase = 0, checkedFiles = 0, stop = 0, fileErrors = 0, mapsOpened = 0;
inline char failure[512]{};
inline SRWLOCK errorLock = SRWLOCK_INIT;
inline void report(const char* message) {
    AcquireSRWLockExclusive(&errorLock); strncpy_s(failure,message,_TRUNCATE); ReleaseSRWLockExclusive(&errorLock);
}
inline std::wstring wide(const std::string& value) {
    auto count = MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),nullptr,0);
    require(count > 0 && count < 30000,"A runtime path has an invalid UTF-8 encoding or size.");
    std::wstring result(count,0); require(MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),result.data(),count) == count,"A runtime path could not be decoded."); return result;
}
inline std::wstring relative(std::string path) {
    require(!path.empty() && path.size()<2048 && path[0]!='/' && path.find(':')==std::string::npos && path.find('\\')==std::string::npos && path.find('\0')==std::string::npos,"A runtime path is not relative.");
    size_t offset=0;
    while (offset<path.size()) {
        auto end=path.find('/',offset); if(end==std::string::npos)end=path.size(); auto part=path.substr(offset,end-offset);
        require(!part.empty() && part!="." && part!=".." && part.back()!='.' && part.back()!=' ',"A runtime path contains an unsafe segment."); offset=end+1;
    }
    for(auto& c:path)if(c=='/')c='\\'; return wide(path);
}
class Wire {
    const std::vector<unsigned char>& data; size_t at=0;
public:
    explicit Wire(const std::vector<unsigned char>& value):data(value){}
    template<class T>T number(){require(at<=data.size() && sizeof(T)<=data.size()-at,"The runtime configuration is truncated.");T value;memcpy(&value,data.data()+at,sizeof(T));at+=sizeof(T);return value;}
    std::vector<unsigned char> blob(size_t maximum){auto size=number<uint32_t>();require(size<=maximum && size<=data.size()-at,"A runtime configuration field exceeds its limit.");std::vector<unsigned char> value(data.begin()+at,data.begin()+at+size);at+=size;return value;}
    std::string string(){auto value=blob(16384);return std::string(value.begin(),value.end());}
    bool end()const{return at==data.size();}
};
inline bool suffix(const std::string& value,const char* ending) {auto length=strlen(ending);return value.size()>=length && value.compare(value.size()-length,length,ending)==0;}
inline void parse(const std::vector<unsigned char>& bytes) {
    std::vector<File> parsedFiles;std::vector<Route> parsedRoutes;
    Wire input(bytes); require(input.number<uint32_t>()==0x54433548 && input.number<uint32_t>()==1,"The content configuration format is unsupported.");
    require(input.string()=="Microsoft.Halo5Forge_1.194.6192.2_x64__8wekyb3d8bbwe","The content configuration belongs to another Forge package.");
    auto cache=wide(input.string()); require(cache.size()>3 && cache[1]==L':' && cache[2]==L'\\' && cache.find(L':',2)==std::wstring::npos && cache.find(L"..") == std::wstring::npos,"The content cache root is invalid.");
    while(cache.back()==L'\\')cache.pop_back();
    wchar_t imagePath[32768];auto length=GetModuleFileNameW(nullptr,imagePath,32768);require(length>0 && length<32768,"Forge's installation path is unavailable.");
    std::wstring root=imagePath;root.resize(root.find_last_of(L'\\'));
    require(_wcsnicmp(root.c_str(),cache.c_str(),root.size())!=0,"Generated content must be outside the Forge installation.");
    auto count=input.number<uint32_t>();require(count>0 && count<=1024,"The runtime file count is unsupported.");
    std::set<std::wstring> names;
    for(unsigned i=0;i<count;++i) {
        auto name=input.string(),target=input.string();
        bool module=name.rfind("deploy/any/levels/",0)==0 || name.rfind("deploy/pc/levels/",0)==0;
        require((module && suffix(name,".module")) || name=="sound/win/h5solo-campaign.pck" || name=="__cms__/campaign/campaignnormal.bin" || name=="__cms__/campaign/campaignarcade.bin","A file mapping is outside campaign content.");
        require(target.rfind("game/",0)==0,"A runtime file must belong to the generated game cache.");
        File file;file.original=root+L"\\"+relative(name);file.target=cache+L"\\"+relative(target);file.length=input.number<uint64_t>();
        auto digest=input.blob(32);require(digest.size()==32 && file.length>0 && file.length<32ull*1024*1024*1024,"A runtime file has an invalid digest or size.");memcpy(file.digest.data(),digest.data(),32);
        auto key=file.original;for(auto& c:key)c=towlower(c);require(names.insert(key).second,"Several runtime files replace the same game file."); parsedFiles.push_back(std::move(file));
    }
    count=input.number<uint32_t>();require(count>0 && count<=64,"The available campaign route count is unsupported.");std::set<uint32_t> ids;
    for(unsigned i=0;i<count;++i) {
        Route route;route.id=input.number<uint32_t>();route.mission=input.number<uint32_t>();route.next=input.number<uint32_t>();route.scenario=input.string();route.source=input.blob(4096);route.target=input.blob(4096);
        require(route.mission<15 && ids.insert(route.id).second && !route.source.empty() && !route.target.empty() && route.source.back()==0 && route.target.back()==0,"A campaign route is invalid or duplicated.");parsedRoutes.push_back(std::move(route));
    }
    require(input.end(),"The runtime configuration contains trailing data.");
    files=std::move(parsedFiles);routes=std::move(parsedRoutes);
}
inline void closeFiles(){for(auto& file:files)if(file.locked && file.locked!=INVALID_HANDLE_VALUE){CloseHandle(file.locked);file.locked=nullptr;}}
inline void verify(File& file) {
    file.locked=CreateFileW(file.target.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_SEQUENTIAL_SCAN,nullptr);
    require(file.locked!=INVALID_HANDLE_VALUE,"Forge cannot open a generated game file. Check the cache access details.");
    LARGE_INTEGER length{};BY_HANDLE_FILE_INFORMATION info{};
    require(GetFileInformationByHandle(file.locked,&info) && !(info.dwFileAttributes&(FILE_ATTRIBUTE_REPARSE_POINT|FILE_ATTRIBUTE_DIRECTORY)) && GetFileSizeEx(file.locked,&length) && static_cast<uint64_t>(length.QuadPart)==file.length,"A generated game file changed or has an unsupported type.");
    // AppContainer path normalization asks for ancestor access outside the cache
    // permission grant. The worker rejects links before configuration; retain
    // this verified file and compare its volume/file identity on every open.
    file.identity=info;
    // Outputs are fully hashed and read back before their sealed manifest is
    // published. Routine Play verifies stable file identity, type and length,
    // then retains this handle so later redirected opens must match the same
    // NTFS object. Full multi-gigabyte hashing belongs to explicit repair.
}
inline DWORD WINAPI verifyAll(void*) {
    try { for(auto& file:files){verify(file);InterlockedIncrement(&checkedFiles);}InterlockedExchange(&phase,2); }
    catch(const std::exception& error){report(error.what());closeFiles();InterlockedExchange(&phase,5);}
    catch(...){report("Generated content verification failed.");closeFiles();InterlockedExchange(&phase,5);}
    return 0;
}
}
