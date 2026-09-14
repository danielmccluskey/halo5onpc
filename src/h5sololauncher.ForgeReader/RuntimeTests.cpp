#ifdef NDEBUG
#undef NDEBUG
#endif
#include "RenderFrameGate.h"
#include "SoloEntrySource.h"
#include <cassert>
#include <cstdio>
using namespace h5runtime;
static LONG forwards=0;
static BOOL WINAPI fakeTry(LPCRITICAL_SECTION){assert(GetLastError()==111);++forwards;SetLastError(222);return 7;}
static void put(void* to,uint64_t value,unsigned count=8){memcpy(to,&value,count);}
static void soloTest(){
    const std::string action="<halo:DoLuaAction LuaLine=\"OnListBoxSelectionChange()\"/>";
    auto input=std::string("<r>")+action+"</r>";std::vector<unsigned char> bytes(input.begin(),input.end());bytes.push_back(0);
    auto result=soloEntrySource(bytes);std::string text(reinterpret_cast<char*>(result.data()));
    assert(text.find("RequestGotoLobbyActivity(Hui.Import(Anubis.Lobby.eLobbyMenuActivity).k_OfflineCampaign, true)")!=std::string::npos);
    assert(text.find(action)!=std::string::npos && result.back()==0);
    for(auto invalid:std::vector<std::string>{"<r/>","<r>"+action+action+"</r>"}){
        std::vector<unsigned char> bad(invalid.begin(),invalid.end());bad.push_back(0);bool rejected=false;
        try{soloEntrySource(bad);}catch(const std::exception&){rejected=true;}assert(rejected);
    }
    bytes.pop_back();bool rejected=false;try{soloEntrySource(bytes);}catch(const std::exception&){rejected=true;}assert(rejected);
}
static void frameTest(){using namespace renderer;
    unsigned char header[96]{},entries[176]{},alternates[176]{},roots[112]{};uint64_t accelerated[2]{};
    auto image=static_cast<unsigned char*>(VirtualAlloc(nullptr,0x6c0c000,MEM_RESERVE,PAGE_READWRITE));assert(image);
    assert(VirtualAlloc(image+0x6c0b000,4096,MEM_COMMIT,PAGE_READWRITE));base=reinterpret_cast<uint64_t>(image);put(image+0x6c0b500,reinterpret_cast<uint64_t>(accelerated));
    pool=reinterpret_cast<uint64_t>(header);put(header+0x20,88);put(header+0x4c,2,4);put(header+0x58,reinterpret_cast<uint64_t>(entries));
    for(unsigned i=0;i<2;++i){auto handle=0x201+(i<<15);put(entries+i*88,handle,4);put(entries+i*88+8,reinterpret_cast<uint64_t>(alternates+i*88));put(alternates+i*88+40,reinterpret_cast<uint64_t>(roots+i*56));put(roots+i*56+8,0x42a37,4);put(roots+i*56+12,handle,4);}
    critical=0x1234;nativeTry=fakeTry;armed=1;
    auto attempt=[&](uint64_t section=0x1234,uint64_t caller=0){SetLastError(111);return tryAt(reinterpret_cast<LPCRITICAL_SECTION>(section),caller?caller:base+0x683a3a);};
    assert(attempt()==7 && GetLastError()==222);begin();assert(expectedCount==2 && !fault && active==1);
    assert(!attempt() && GetLastError()==111);assert(attempt(0x9999)==7);assert(attempt(0x1234,0x9999)==7);
    for(unsigned i=0;i<2;++i){put(entries+i*88+40,reinterpret_cast<uint64_t>(roots+i*56));accelerated[i]=reinterpret_cast<uint64_t>(roots+i*56);}
    assert(!attempt());end();accelerated[1]=0;assert(!attempt());accelerated[1]=reinterpret_cast<uint64_t>(roots+56);
    put(entries+88,0x8202,4);assert(!attempt());put(entries+88,0x8201,4);
    assert(attempt()==7 && !pending && published==1 && expectedCount==0);
    // A stale root must not be mistaken for a published current generation.
    begin();put(entries+88+40,reinterpret_cast<uint64_t>(roots));end();assert(!attempt());
    put(entries+88+40,reinterpret_cast<uint64_t>(roots+56));assert(attempt()==7);
    assert(forwards==5);put(image+0x6c0b500,0);begin();assert(!attempt());end();assert(attempt()==7 && !fault);
    pending=1;pendingSince=100;checkDeadline(120100);assert(!fault);checkDeadline(120101);assert(fault && pending && strstr(failure,"two minutes"));
    armed=0;assert(VirtualFree(image,0,MEM_RELEASE));
}
int main(){soloTest();frameTest();puts("PASS: original Solo setup, malformed menu rejection, bank publication, generations, callback ordering, native return values and LastError.");}
