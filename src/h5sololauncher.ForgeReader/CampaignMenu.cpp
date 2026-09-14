#include "MenuAssets.h"
#include "MenuState.h"
#include "SoloEntrySource.h"

namespace h5runtime::campaignMenu {
struct Request{uint32_t magic,version,operation,status;uint64_t creation;uint32_t phase,completed,reserved[2];char message[512];wchar_t configuration[2048];char sha256[65];unsigned char padding[7];};
static_assert(sizeof(Request)==4720);
struct Fix{int32_t offset,target,length;};
struct Ref{int32_t block,offset,nameLength,optional;menuAssets::Proof proof;};
struct Host{uint32_t role;menuAssets::Proof proof;uint32_t rootSize;std::vector<unsigned char> expected;std::vector<std::vector<unsigned char>> blocks;std::vector<Fix> fixes;std::vector<Ref> refs;};
struct Change{uint64_t address;std::vector<unsigned char> before,after;};
static std::vector<Host> hosts;
static std::vector<Change> changes;
static std::vector<void*> allocations;
static uint64_t base=0,creation=0,began=0,stable=0;
static std::string configDigest;
static Menu menu{};
static volatile LONG phase=0;
static unsigned soloPhase=0;
static uint64_t soloRoot=0,soloOriginal=0,soloReplacement=0,soloStable=0;
static uint32_t soloOriginalSize=0,soloReplacementSize=0;
static char failure[512]{};
static SRWLOCK operationLock=SRWLOCK_INIT;
static menuAssets::Proof proof(content::Wire& wire){return{wire.number<uint32_t>(),{wire.number<uint64_t>(),wire.number<uint64_t>()}};}
static void parse(const std::vector<unsigned char>& bytes) {
    content::Wire wire(bytes);require(wire.number<uint32_t>()==0x4e4d3548 && wire.number<uint32_t>()==1,"The campaign menu configuration format differs.");
    require(wire.string()=="Microsoft.Halo5Forge_1.194.6192.2_x64__8wekyb3d8bbwe" && wire.string()=="deploy/pc/levels/h5solo-menu.module","The menu configuration belongs to another package or module.");
    require(wire.number<uint32_t>()==5,"The campaign menu picture set is incomplete.");std::set<uint32_t> seen;const std::set<uint32_t> expected{0xd9489f7b,0xf87213af,0x2574a4ba,0xacd901b4,0x37c51ddd};
    for(unsigned i=0;i<5;++i){auto identity=proof(wire);auto name=wire.string();require(expected.count(identity.gid) && seen.insert(identity.gid).second && name.size()<260,"The menu contains an unsupported picture.");menuAssets::pictures.push_back({identity,name});}
    require(wire.number<uint32_t>()==8,"The campaign menu component set is incomplete.");
    const uint32_t gids[]={0x24a4f,0x24a4e,0xb719da90,0x4435a884,0xd0942c43,0xcc77d593,0xbca20cd8,0xa5832ad9};
    for(unsigned i=0;i<8;++i){Host host;host.role=wire.number<uint32_t>();host.proof=proof(wire);host.rootSize=wire.number<uint32_t>();
        require(host.role==i && host.proof.gid==gids[i] && host.rootSize>=48 && host.rootSize<=4096,"A campaign menu host has an unsupported identity or layout.");
        host.expected=wire.blob(4*1024*1024);auto count=wire.number<uint32_t>();require(count<=32,"A campaign menu host has too many blocks.");
        for(unsigned j=0;j<count;++j)host.blocks.push_back(wire.blob(4*1024*1024));
        require(i==5?host.blocks.empty():!host.blocks.empty() && host.blocks[0].size()==host.rootSize,"A campaign menu root layout differs.");
        count=wire.number<uint32_t>();require(count<=32,"A campaign menu host has too many pointers.");
        for(unsigned j=0;j<count;++j){Fix fix{wire.number<int32_t>(),wire.number<int32_t>(),wire.number<int32_t>()};require(fix.offset>=16 && fix.offset+28<=static_cast<int>(host.rootSize) && fix.target>=-1 && fix.target<static_cast<int>(host.blocks.size()) && (fix.length==-1 || fix.length==fix.offset+24),"A menu pointer lies outside its root.");host.fixes.push_back(fix);}
        count=wire.number<uint32_t>();require(count<=128,"A campaign menu host has too many references.");
        for(unsigned j=0;j<count;++j){Ref ref{wire.number<int32_t>(),wire.number<int32_t>(),wire.number<int32_t>(),wire.number<int32_t>(),proof(wire)};require(ref.block>=0 && ref.block<static_cast<int>(host.blocks.size()) && ref.offset>=0 && ref.offset+32<=static_cast<int>(host.blocks[ref.block].size()) && ref.nameLength>=0 && ref.nameLength<2048 && (ref.optional==0 || ref.optional==1),"A menu reference lies outside its block.");host.refs.push_back(ref);}
        hosts.push_back(std::move(host));
    }
    require(wire.end(),"The campaign menu configuration contains trailing data.");
}
static uint64_t allocate(size_t size){require(size>0 && size<=8*1024*1024,"A campaign menu allocation exceeds its limit.");auto block=VirtualAlloc(nullptr,size,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);require(block!=nullptr,"Could not allocate the campaign menu.");allocations.push_back(block);return reinterpret_cast<uint64_t>(block);}
template<class T>static void put(std::vector<unsigned char>& bytes,size_t at,T value){require(at<=bytes.size() && sizeof(T)<=bytes.size()-at,"A campaign menu field exceeds its block.");memcpy(bytes.data()+at,&value,sizeof(value));}
static void prepare() {
    auto roots=menuAssets::find(base);changes.clear();
    for(auto& host:hosts){auto live=menuAssets::resolve(roots,host.proof);if(host.role==5)continue;
        auto old=bytes(live.root,host.rootSize);auto blocks=host.blocks;std::vector<uint64_t> addresses{live.root};
        for(unsigned i=1;i<blocks.size();++i)addresses.push_back(blocks[i].empty()?0:allocate(blocks[i].size()));
        if(host.role<=1){auto offset=host.role==0?20u:160u;auto pointer=value<uint64_t>(live.root+offset);auto length=value<uint32_t>(live.root+offset+24);require(length==host.expected.size() && bytes(pointer,length)==host.expected,"The native main menu source has already changed.");blocks[0]=old;}
        else memcpy(blocks[0].data(),old.data(),host.role==4?16:20);
        for(auto fix:host.fixes){put<uint64_t>(blocks[0],fix.offset,fix.target<0?0:addresses[fix.target]);memcpy(blocks[0].data()+fix.offset+8,old.data()+fix.offset+8,8);if(fix.length>=0)put<uint32_t>(blocks[0],fix.length,fix.target<0?0:static_cast<uint32_t>(blocks[fix.target].size()));}
        for(auto ref:host.refs){auto& block=blocks[ref.block];put<uint64_t>(block,ref.offset,base+0x3308818);put<uint32_t>(block,ref.offset+8,ref.nameLength);
            if(ref.optional)put<uint32_t>(block,ref.offset+28,0xffffffff);
            else{auto target=menuAssets::resolve(roots,ref.proof);put<uint64_t>(block,ref.offset+16,target.proof.key.asset);put<uint32_t>(block,ref.offset+28,target.handle);}
        }
        for(unsigned i=1;i<blocks.size();++i)if(!blocks[i].empty())memcpy(reinterpret_cast<void*>(addresses[i]),blocks[i].data(),blocks[i].size());
        changes.push_back({live.root,std::move(old),std::move(blocks[0])});
    }
    auto nodes=value<uint64_t>(menu.definition+8);auto count=value<uint32_t>(menu.definition+24);require(count>0 && count<128,"The native menu node list differs.");unsigned matched=0;
    for(unsigned i=0;i<count;++i){auto node=nodes+i*156;if(value<uint32_t>(node)!=nameId("any"))continue;auto transitions=value<uint64_t>(node+8);auto number=value<uint32_t>(node+24);require(number<256,"The native menu event list differs.");
        for(unsigned j=0;j<number;++j){auto at=transitions+j*108;if(value<uint32_t>(at)!=nameId("goto_theater"))continue;require(value<uint32_t>(at+4)==nameId("theater_lobby"),"The native campaign host transition differs.");auto original=bytes(at,4);auto replacement=original;put<uint32_t>(replacement,0,nameId("goto_campaign_solo"));changes.push_back({at,original,replacement});++matched;}}
    require(matched==1,"The native campaign menu event is missing or ambiguous.");
}
static void post(const char* event,uint32_t expected) {
    menuGuards(base);auto now=inspectMenu(base);require(now.receiver==menu.receiver && now.definition==menu.definition && now.name==expected,"Forge's menu changed during campaign menu setup.");
    uint32_t arguments[64]{};arguments[0]=nameId(event);reinterpret_cast<void(__fastcall*)(uint64_t,const void*,uint64_t)>(base+0x1e064f0)(now.bus,arguments,now.receiver);
}
static void backgroundCaught(){try{menuAssets::load(base);prepare();InterlockedExchange(&phase,2);}catch(const std::exception& error){strncpy_s(failure,error.what(),_TRUNCATE);InterlockedExchange(&phase,6);}catch(...){strcpy_s(failure,"Campaign menu preparation failed. Restart Forge.");InterlockedExchange(&phase,6);}}
static DWORD WINAPI background(void*){__try{backgroundCaught();}__except(EXCEPTION_EXECUTE_HANDLER){strcpy_s(failure,"A native fault interrupted campaign menu preparation. Restart Forge.");InterlockedExchange(&phase,6);}return 0;}
static void run(Request& request) {
    require(request.magic==0x554d3548 && request.version==1 && request.operation<=3,"The campaign menu request is incompatible.");auto currentBase=identity(request.creation);
    require(strnlen_s(request.sha256,65)==64,"The menu configuration digest is invalid.");
    if(creation)require(creation==request.creation && configDigest==request.sha256,"Another campaign menu has already been prepared in this Forge session.");
    if(request.operation==1){require(phase==0 && content::phase==4,"Activate campaign content before preparing its menu.");
        require(wcsnlen_s(request.configuration,2048)<2048,"The menu configuration path is too long.");auto file=CreateFileW(request.configuration,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);require(file!=INVALID_HANDLE_VALUE,"Forge cannot open the campaign menu configuration.");
        LARGE_INTEGER size{};BY_HANDLE_FILE_INFORMATION info{};bool ok=GetFileSizeEx(file,&size) && size.QuadPart>0 && size.QuadPart<16*1024*1024 && GetFileInformationByHandle(file,&info) && !(info.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT));std::vector<unsigned char> data(ok?static_cast<size_t>(size.QuadPart):0);DWORD read=0;if(ok)ok=ReadFile(file,data.data(),static_cast<DWORD>(data.size()),&read,nullptr) && read==data.size();CloseHandle(file);require(ok && sha(data.data(),data.size())==request.sha256,"The campaign menu configuration changed or is damaged.");
        menu=inspectMenu(currentBase);require(menu.name==nameId("main_menu") && menu.screen,"Automatic campaign menu setup requires the main menu.");
        creation=request.creation;base=currentBase;configDigest=request.sha256;InterlockedExchange(&phase,1);parse(data);menuGuards(base);began=GetTickCount64();
        auto thread=CreateThread(nullptr,0,background,nullptr,0,nullptr);require(thread!=nullptr,"Could not start campaign menu preparation.");CloseHandle(thread);
    }
    if(request.operation==2){auto current=inspectMenu(base);
        if(phase==2){post("goto_main_menu_off",nameId("main_menu"));InterlockedExchange(&phase,3);began=GetTickCount64();}
        else if(phase==3 && current.name==nameId("off") && !current.screen){
            require(current.receiver==menu.receiver && current.definition==menu.definition,"The menu owner changed before publication.");
            for(const auto& change:changes)require(bytes(change.address,change.before.size())==change.before,"A campaign menu host changed before publication.");
            for(const auto& change:changes)memcpy(reinterpret_cast<void*>(change.address),change.after.data(),change.after.size());
            InterlockedExchange(&phase,4);post("goto_main_menu",nameId("off"));
        }else if(phase==4 && current.name==nameId("main_menu") && current.screen){if(!stable)stable=GetTickCount64();if(GetTickCount64()-stable>=1000)InterlockedExchange(&phase,5);}
        else stable=0;
        if(phase>=2 && phase<5 && GetTickCount64()-began>60000)throw std::runtime_error("Forge did not finish refreshing its campaign menu. Restart Forge.");
    }
    if(request.operation==3){require(phase==5,"Finish campaign menu preparation before opening Solo.");auto current=inspectMenu(base);
        if(soloPhase==0){
            require(current.name==nameId("main_menu") && current.screen,"Automatic Solo entry requires the main menu.");
            auto roots=menuAssets::find(base);soloRoot=menuAssets::resolve(roots,hosts[1].proof).root;
            soloOriginal=value<uint64_t>(soloRoot+160);soloOriginalSize=value<uint32_t>(soloRoot+184);
            auto original=bytes(soloOriginal,soloOriginalSize);auto prepared=soloEntrySource(original);
            soloReplacement=allocate(prepared.size());soloReplacementSize=static_cast<uint32_t>(prepared.size());
            memcpy(reinterpret_cast<void*>(soloReplacement),prepared.data(),prepared.size());
            soloPhase=1;began=GetTickCount64();post("goto_main_menu_off",nameId("main_menu"));
        }else if(soloPhase==1 && current.name==nameId("off") && !current.screen){
            require(current.receiver==menu.receiver && current.definition==menu.definition && value<uint64_t>(soloRoot+160)==soloOriginal && value<uint32_t>(soloRoot+184)==soloOriginalSize,"The main menu changed before automatic Solo entry.");
            *reinterpret_cast<uint64_t*>(soloRoot+160)=soloReplacement;*reinterpret_cast<uint32_t*>(soloRoot+184)=soloReplacementSize;
            soloPhase=2;post("goto_main_menu",nameId("off"));
        }else if(soloPhase==2 && current.name==nameId("theater_lobby") && current.screen){
            if(!soloStable)soloStable=GetTickCount64();
            if(GetTickCount64()-soloStable>=1500){
                require(value<uint64_t>(soloRoot+160)==soloReplacement && value<uint32_t>(soloRoot+184)==soloReplacementSize,"The automatic Solo source changed before restoration.");
                *reinterpret_cast<uint64_t*>(soloRoot+160)=soloOriginal;*reinterpret_cast<uint32_t*>(soloRoot+184)=soloOriginalSize;soloPhase=3;
            }
        }else soloStable=0;
        if(soloPhase==3)request.reserved[0]=1;
        else require(GetTickCount64()-began<60000,"Forge did not finish opening Solo. Restart Forge.");
    }
    request.phase=static_cast<uint32_t>(InterlockedCompareExchange(&phase,0,0));request.completed=request.phase==5?8:0;
    if(request.operation==0)request.reserved[0]=soloPhase==3?1:0;
    if(request.phase==6){request.status=ERROR_INVALID_STATE;strncpy_s(request.message,failure,_TRUNCATE);}
}
}
extern "C" __declspec(dllexport) DWORD WINAPI H5Menu(h5runtime::campaignMenu::Request* request){
    using namespace h5runtime::campaignMenu;if(!request)return ERROR_INVALID_PARAMETER;AcquireSRWLockExclusive(&operationLock);request->status=0;request->message[0]=0;
    try{run(*request);}catch(const std::exception& error){request->status=ERROR_INVALID_STATE;strncpy_s(request->message,error.what(),_TRUNCATE);if(creation){strncpy_s(failure,error.what(),_TRUNCATE);InterlockedExchange(&phase,6);}}catch(...){request->status=ERROR_UNHANDLED_EXCEPTION;InterlockedExchange(&phase,6);}
    ReleaseSRWLockExclusive(&operationLock);return request->status;
}
