#pragma once
#include "RenderFrameGate.h"

namespace h5runtime::renderer {
using NativeCallback=uint64_t(WINAPI*)(uint64_t);
inline constexpr uint32_t callbackSlots[]={0x6cb5848,0x6cb5850,0x6cb5858};
inline constexpr uint32_t callbackFunctions[]={0x15c8270,0x15c7c10,0x15c7760};
struct BankKey{uint64_t asset,checksum;};
inline void insert(const std::vector<BankKey>& keys){auto lock=reinterpret_cast<LPCRITICAL_SECTION>(base+0x61b0340);EnterCriticalSection(lock);bool valid=true;
    __try{for(const auto& key:keys){uint64_t result[2]{};reinterpret_cast<void*(__fastcall*)(void*,void*,bool,const BankKey*,bool)>(base+0x15c6f50)(reinterpret_cast<void*>(base+0x61b0330),result,false,&key,false);
        BankKey actual{};if(!result[0] || !read(result[0]+0x20,&actual,sizeof(actual)) || memcmp(&actual,&key,sizeof(key))){valid=false;break;}
    }}__finally{LeaveCriticalSection(lock);}require(valid,"Forge rejected a replacement shader bank dependency.");
}
inline void closure(){auto state=currentPool();auto entries=bytes(state.storage,state.count*88ull);std::vector<BankKey> keys;
    for(unsigned i=0;i<state.count;++i){auto row=entries.data()+i*88;uint32_t handle;uint64_t alternate,root;memcpy(&handle,row,4);memcpy(&alternate,row+8,8);memcpy(&root,row+40,8);
        if(!alternate || handle==0xffffffff || handle>>15!=i)continue;auto replacement=bytes(alternate,88);if(!root)memcpy(&root,replacement.data()+40,8);if(!root || value<uint64_t>(root)!=base+0x3624758)continue;
        require(value<uint32_t>(root+12)==handle && value<uint32_t>(state.storage+i*88)==handle && value<uint64_t>(state.storage+i*88+8)==alternate && keys.size()<256,"A replacement shader bank identity changed.");
        BankKey key{};memcpy(&key,replacement.data()+16,16);require(key.asset && key.asset!=UINT64_MAX && key.checksum,"A replacement shader bank has no valid identity.");keys.push_back(key);
    }
    insert(keys);
}
inline void prepareClosure(){try{closure();}catch(const std::exception& error){failed(error.what());}catch(...){failed();}}
inline uint64_t call(unsigned which,uint64_t argument) {
    auto incoming=GetLastError();InterlockedIncrement(&callbacks);if(which==0)prepareClosure();
    auto protect=which!=0;bool acquired=false;uint64_t result=0;DWORD returned=incoming;
    if(protect){if(!reinterpret_cast<bool(WINAPI*)(uint32_t)>(base+0x6de780)(3)){reinterpret_cast<uint64_t(WINAPI*)(uint32_t)>(base+0x6de730)(3);acquired=true;}if(which==1)begin();}
    __try{SetLastError(incoming);result=reinterpret_cast<NativeCallback>(base+callbackFunctions[which])(argument);returned=GetLastError();}
    __finally{if(protect){if(which==1)end();if(acquired)reinterpret_cast<void(WINAPI*)(uint32_t)>(base+0x6de9c0)(3);}}
    SetLastError(returned);return result;
}
inline uint64_t WINAPI pre(uint64_t argument){return call(0,argument);}
inline uint64_t WINAPI post(uint64_t argument){return call(1,argument);}
inline uint64_t WINAPI release(uint64_t argument){return call(2,argument);}
inline void* callbackHooks[]={reinterpret_cast<void*>(pre),reinterpret_cast<void*>(post),reinterpret_cast<void*>(release)};
}
