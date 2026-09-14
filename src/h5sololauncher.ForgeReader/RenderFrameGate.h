#pragma once
#include "RuntimeSupport.h"
#include <intrin.h>
#include <array>

namespace h5runtime::renderer {
inline uint64_t base=0,pool=0,critical=0;
inline volatile LONG armed=0,fault=0,pending=0,active=0,blocked=0,batches=0,published=0,callbacks=0;
inline uint64_t pendingSince=0;
inline SRWLOCK frameLock=SRWLOCK_INIT;
inline decltype(&TryEnterCriticalSection) nativeTry=nullptr;
struct ExpectedBank{uint32_t handle;uint64_t root;};
inline std::array<ExpectedBank,256> expected{};
inline unsigned expectedCount=0;
inline SRWLOCK failureLock=SRWLOCK_INIT;
inline char failure[384]{};
inline void failed(const char* message="A native renderer callback failed."){AcquireSRWLockExclusive(&failureLock);if(!fault)strncpy_s(failure,message,_TRUNCATE);InterlockedExchange(&fault,1);InterlockedExchange(&pending,1);ReleaseSRWLockExclusive(&failureLock);}
struct Pool{uint64_t storage;uint32_t count;};
inline Pool currentPool(){require(pool && value<uint64_t>(pool+0x20)==88,"The renderer asset pool changed.");auto count=value<uint32_t>(pool+0x4c);auto storage=value<uint64_t>(pool+0x58);require(storage && count>0 && count<=0x15400,"The renderer asset pool is unavailable.");return{storage,count};}
inline void capture() {
    auto poolData=currentPool();auto entries=bytes(poolData.storage,poolData.count*88ull);
    for(unsigned i=0;i<poolData.count;++i){auto row=entries.data()+i*88;uint32_t handle;uint64_t alternate;memcpy(&handle,row,4);memcpy(&alternate,row+8,8);if(!alternate || handle==0xffffffff || handle>>15!=i)continue;
        auto root=value<uint64_t>(alternate+0x28);if(!root || value<uint32_t>(root+8)!=0x42a37)continue;
        require(value<uint32_t>(root+12)==handle,"The replacement surface bank handle changed.");unsigned n=0;for(;n<expectedCount;++n)if(expected[n].handle==handle)break;
        require(n<expected.size(),"Too many replacement shader banks.");if(n==expectedCount)++expectedCount;expected[n]={handle,root};
    }
}
inline void begin(){AcquireSRWLockExclusive(&frameLock);++active;
    try{capture();}catch(const std::exception& error){failed(error.what());}catch(...){failed();}
    pending=expectedCount || fault;if(pending && !pendingSince)pendingSince=GetTickCount64();++batches;ReleaseSRWLockExclusive(&frameLock);
}
inline void end(){AcquireSRWLockExclusive(&frameLock);if(active>0)--active;else failed();ReleaseSRWLockExclusive(&frameLock);}
inline bool ready() {
    if(fault || active)return false;auto state=currentPool();auto accelerated=value<uint64_t>(base+0x6c0b500);
    for(unsigned i=0;i<expectedCount;++i){auto entry=expected[i];auto index=entry.handle>>15;if(index>=state.count)return false;auto row=state.storage+index*88;
        if(value<uint32_t>(row)!=entry.handle || value<uint64_t>(row+0x28)!=entry.root)return false;
        // Forge can use its ordinary asset pool without the optional accelerated
        // table. When the table exists, both publication paths must agree.
        if(accelerated && value<uint64_t>(accelerated+index*8)!=entry.root)return false;
    }
    return true;
}
inline void checkDeadline(uint64_t now){if(pending && pendingSince && now-pendingSince>120000 && !fault)failed("Campaign shader publication did not finish within two minutes. Close Forge and use Play again.");}
inline BOOL tryAt(LPCRITICAL_SECTION section,uint64_t caller) {
    auto incoming=GetLastError();bool block=false;
    if(armed && reinterpret_cast<uint64_t>(section)==critical && caller==base+0x683a3a){AcquireSRWLockExclusive(&frameLock);
        if(pending){try{if(ready()){pending=0;expectedCount=0;pendingSince=0;++published;}else {checkDeadline(GetTickCount64());block=true;}}catch(const std::exception& error){failed(error.what());block=true;}catch(...){failed();block=true;}if(block)++blocked;}
        ReleaseSRWLockExclusive(&frameLock);
    }
    SetLastError(incoming);return block?FALSE:nativeTry(section);
}
inline BOOL WINAPI tryFrame(LPCRITICAL_SECTION section){return tryAt(section,reinterpret_cast<uint64_t>(_ReturnAddress()));}
inline bool exchange(uint32_t rva,void* from,void* to){auto slot=reinterpret_cast<void**>(base+rva);DWORD old=0,unused=0;if(!VirtualProtect(slot,8,PAGE_READWRITE,&old))return false;auto changed=InterlockedCompareExchangePointer(slot,to,from)==from;return VirtualProtect(slot,8,old,&unused) && changed;}
}
