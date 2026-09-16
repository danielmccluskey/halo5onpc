#pragma once
#include <windows.h>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <string>
#include <intrin.h>
#include "CompletionGuards.h"
#include "CompletionConfiguration.h"
namespace h5runtime::completion {
struct State {
 uint64_t magic;uint32_t version,size;uint64_t base,creation;
 volatile LONG armed,error,installed,phase,command,busy,wonBlocked,continues;
 uint64_t vm,thread,lastPoll,wpfRoot,originalXml;uint32_t originalLength,xmlLength;
 char source[16384],xmlTemplate[16384],xml[32768];
 char mission[256],next[512],cinematic[256],mode[64],duration[64],par[64],difficulty[64],skulls[64],scores[2048],synthetic[128],message[1024];
 uint32_t events,reserved;char reports[64][1024];wchar_t journal[512];
};
static State CompletionState={0x31504d4f433548ULL,1,sizeof(State)};
static PVOID handler=nullptr;static bool errorsArmed=false;
static bool releaseWon=false,advancing=false,dismissed=false;static uint32_t activeMap=0xffffffff;
static bool returningToMenu=false;
static constexpr uint64_t updateRva=0x869460,wonRva=0x1363900,visibleRva=0x1f714a0,loomOptionsRva=0x5e4cf0,loomPathRva=0x5e4d50;
static uint64_t base(){return CompletionState.base;}
static bool swapByte(uint64_t address,unsigned char from,unsigned char to){DWORD old;auto p=(volatile char*)address;
 if(!VirtualProtect((void*)p,1,PAGE_EXECUTE_READWRITE,&old))return false;
 bool ok=_InterlockedCompareExchange8(p,(char)to,(char)from)==(char)from;
 FlushInstructionCache(GetCurrentProcess(),(void*)p,1);DWORD unused;VirtualProtect((void*)p,1,old,&unused);return ok;}
static void record(const char* text){auto&s=CompletionState;auto i=s.events%64;
 strncpy_s(s.reports[i],text,_TRUNCATE);InterlockedIncrement((LONG*)&s.events);

}
static uint64_t game(){auto tls=((uint64_t*)__readgsqword(0x58))[*(uint32_t*)(base()+0x5f1d56c)];return tls?*(uint64_t*)(tls+0x15a0):0;}
static bool campaign(){auto g=game();return g&&*(uint32_t*)(g+8)==1&&*(unsigned char*)(g+0x56748)&&*(unsigned char*)(g+1);}
static const CampaignMapList* mapByPath(const char* path){char normalized[512];unsigned i=0;
 for(;path&&path[i]&&i<511;i++)normalized[i]=path[i]=='\\'?'/':path[i];normalized[i]=0;
 if(!path||path[i])return nullptr;for(auto& map:campaign_maps)if(_stricmp(normalized,map.scenario.c_str())==0)return &map;return nullptr;}
static bool isSuccessor(uint32_t current,const char* path){auto next=mapByPath(path);if(!next)return false;
 for(auto& map:campaign_maps)if(map.id==current)return map.next==next->id;return false;}
static bool availableNext(){auto g=game();return g&&isSuccessor(*(uint32_t*)(g+0x3603c),CompletionState.next);}
static bool matchingPreload(const CampaignMapList* expected,const void* request,bool pathOnly){
 if(!expected||!request)return false;
 // The native scenario-options structure is 0x1178 bytes. Its module list
 // occupies +8..+1007, path +1048..+114b, type +114c and map ID +1150.
 // Check the complete normalized registry list before permitting a preload.
 __try {
  auto bytes=(const unsigned char*)request;char path[261];
  memcpy(path,bytes+(pathOnly?0:0x1048),260);path[260]=0;
  if(!memchr(path,0,260)||mapByPath(path)!=expected)return false;
  if(pathOnly)return true;
  return *(const uint32_t*)(bytes+0x114c)==5&&*(const uint32_t*)(bytes+0x1150)==expected->id
   &&expected->target.size()<=4096&&*(const uint32_t*)bytes==expected->target.size()
   &&memcmp(bytes+8,expected->target.data(),expected->target.size())==0;
 } __except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
static bool successorPreload(uint32_t current,const void* request,bool pathOnly){
 // Authored cinematics prepare their successor before game_won. Retain that
 // native ordering when the exact next scenario and normalized modules are
 // available; actual advancement still waits for the report's Continue action.
 for(auto& map:campaign_maps)if(isSuccessor(current,map.scenario.c_str())&&matchingPreload(&map,request,pathOnly))return true;
 return false;
}
static bool menuPreload(const void* request,bool pathOnly){
 // These are the same two native request layouts used by matchingPreload.
 // Allow only the PC front-end scenario, never arbitrary non-successor maps.
 if(!request)return false;
 __try {
  const char* path=(const char*)request+(pathOnly?0:0x1048);
  const char expected[]="levels/ui/mainmenu/mainmenu";
  for(size_t i=0;i<sizeof(expected);++i){
   char c=path[i];if(c=='\\')c='/';else if(c>='A'&&c<='Z')c+=32;
   if(c!=expected[i])return false;
  }
  return true;
 } __except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
static void resetMissionState(){auto&s=CompletionState;
 // Do not dereference the old VM or UI assets here: teardown may own them.
 activeMap=0xffffffff;advancing=dismissed=releaseWon=false;
 s.phase=0;s.installed=0;s.command=0;s.vm=0;s.lastPoll=0;s.xmlLength=0;
 s.mission[0]=s.next[0]=s.cinematic[0]=s.synthetic[0]=s.scores[0]=0;
}
static void beginMenuReturn(){
 if(returningToMenu)return;
 returningToMenu=true;resetMissionState();record("leave|native main menu preload allowed; completion suspended");
}
static void report(const char* text){auto&s=CompletionState;if(!text)return;
 record(text);auto separator=strchr(text,'|');if(!separator)return;std::string key(text,separator);auto value=separator+1;
 if(key=="begin") {s.phase=1;strncpy_s(s.mission,value,_TRUNCATE);s.next[0]=s.scores[0]=s.synthetic[0]=0;}
 else if(key=="reset") {s.phase=0;s.next[0]=s.synthetic[0]=s.mission[0]=0;}
 else if(key=="cinematic_begin") {s.phase=2;strncpy_s(s.cinematic,value,_TRUNCATE);}
 else if(key=="cinematic_end")s.phase=3;
 else if(key=="next") {s.phase=4;strncpy_s(s.next,value,_TRUNCATE);}
 else if(key=="interlude_next") {strncpy_s(s.next,value,_TRUNCATE);s.phase=availableNext()?7:6;record(s.phase==7?"interlude|validated native handoff":"interlude|next scenario unavailable");}
 else if(key=="mode")strncpy_s(s.mode,value,_TRUNCATE);
 else if(key=="time")strncpy_s(s.duration,value,_TRUNCATE);
 else if(key=="par")strncpy_s(s.par,value,_TRUNCATE);
 else if(key=="difficulty")strncpy_s(s.difficulty,value,_TRUNCATE);
 else if(key=="skulls")strncpy_s(s.skulls,value,_TRUNCATE);
 else if(key=="scores")strncpy_s(s.scores,value,_TRUNCATE);
 else if(key=="synthetic")strncpy_s(s.synthetic,value,_TRUNCATE);
 else if(key=="complete")InterlockedExchange(&s.phase,5);
 else if(key=="failure") {strncpy_s(s.message,value,_TRUNCATE);s.error=30;}
}
static void consumeReports(uint64_t vm){auto&s=CompletionState;auto top=*(uint64_t*)(vm+0x48),bottom=*(uint64_t*)(vm+0x50);
 if(top<bottom+16||(*(uint32_t*)(top-16)&15)!=4){s.error=31;return;}
 auto text=((const char*(*)(uint64_t,void*,void*))(base()+0x7cdec0))(vm,(void*)(top-16),nullptr);
 if(!text||strnlen_s(text,65536)==65536){s.error=32;return;}
 std::string buffer(text);size_t at=0;
 while(at<buffer.size()){auto end=buffer.find('\n',at);auto line=buffer.substr(at,end==std::string::npos?end:end-at);report(line.c_str());if(end==std::string::npos)break;at=end+1;}
}
static std::string escaped(const char* text){std::string out;for(auto p=text;*p;++p){switch(*p){case '&':out+="&amp;";break;case '<':out+="&lt;";break;case '>':out+="&gt;";break;case '"':out+="&quot;";break;case '\'':out+="&apos;";break;default:if((unsigned char)*p>=32)out+=*p;}}return out;}
static void replace(std::string& xml,const char* key,const char* text){auto value=escaped(text);std::string token="{{";token+=key;token+="}}";size_t at=0;while((at=xml.find(token,at))!=std::string::npos){xml.replace(at,token.size(),value);at+=value.size();}}
static bool publishResults(bool nextAvailable){auto&s=CompletionState;
 if(*(uint32_t*)(s.wpfRoot+8)!=0xf79a5480)return false;
 auto pointer=*(uint64_t*)(s.wpfRoot+160);if(pointer!=s.originalXml&&pointer!=(uint64_t)s.xml)return false;
 std::string xml=s.xmlTemplate;
 replace(xml,"MISSION",s.mission);replace(xml,"MODE",s.mode);replace(xml,"TIME",s.duration);replace(xml,"PAR",s.par);replace(xml,"DIFFICULTY",s.difficulty);
 replace(xml,"SKULLS",s.skulls);replace(xml,"SCORES",s.scores);replace(xml,"NEXT",s.next);replace(xml,"SYNTHETIC",s.synthetic);
 replace(xml,"CONTINUETITLE",nextAvailable?"LOADING NEXT MISSION":"NEXT MISSION UNAVAILABLE");
 replace(xml,"CONTINUEMESSAGE",nextAvailable?"Preparing the next scenario...":"This mission has finished. The next scenario has not been imported yet. You can close the game.");
 if(xml.size()+1>sizeof(s.xml)||xml.find("{{")!=std::string::npos)return false;
 memcpy(s.xml,xml.c_str(),xml.size()+1);s.xmlLength=(uint32_t)xml.size()+1;
 *(uint64_t*)(s.wpfRoot+160)=(uint64_t)s.xml;*(uint32_t*)(s.wpfRoot+184)=s.xmlLength;
 return true;
}
static void showResults(){auto b=base();auto old=*(uint32_t*)(b+0x6415d98);uint32_t hide=0xc9c2975a,show=0x39797786;
 // Original ausar_visor_menus events: hide_post_game_exp / show_post_game_exp_campaign.
 auto change=(void(*)(uint64_t,uint32_t,uint32_t,uint32_t*))(b+0x1ddcf30);
 if(old==4)change(b+0x6415d50,old,0,&hide);
 *(uint32_t*)(b+0x6415d98)=4;change(b+0x6415d50,old,4,&show);
 record("results_visible|local campaign report");}
static bool execute(uint64_t vm,const char* source,uint32_t flags,bool drain=false){auto&s=CompletionState;auto owner=base()+0x5982ac0;auto top=*(uint64_t*)(vm+0x48);
 s.message[0]=0;errorsArmed=true;
 if(!swapByte(base()+0x835c4c,0x49,0xcc)){s.error=21;errorsArmed=false;return false;}
 if(!swapByte(base()+0x835dc2,0x49,0xcc)){swapByte(base()+0x835c4c,0xcc,0x49);s.error=21;errorsArmed=false;return false;}
 bool result=false,clean=false;
 __try {result=((bool(*)(uint64_t,const char*,const char*,uint32_t))(base()+0x8353e0))(owner,source,"campaign-completion",flags);if(result&&drain&&!s.message[0])consumeReports(vm);}
 __finally {clean=swapByte(base()+0x835c4c,0xcc,0x49);clean=swapByte(base()+0x835dc2,0xcc,0x49)&&clean;errorsArmed=false;*(uint64_t*)(vm+0x48)=top;}
 if(!clean)s.error=22;
 if(s.message[0]){if(!s.error)s.error=23;record(s.message);}
 return result;}
static void tick(){auto&s=CompletionState;
 if(!campaign()){
  if(returningToMenu){resetMissionState();returningToMenu=false;record("leave|campaign state released");}
  return;
 }
 if(s.error||returningToMenu)return;
 auto currentMap=*(uint32_t*)(game()+0x3603c);
 if(activeMap!=currentMap){activeMap=currentMap;advancing=dismissed=releaseWon=false;s.phase=0;s.installed=0;s.xmlLength=0;s.next[0]=0;record("map|campaign scenario changed");}
 auto owner=base()+0x5982ac0;auto vm=*(uint64_t*)(owner+8);if(!vm||*(uint64_t*)(vm+0x98)!=owner)return;
 auto now=GetTickCount64();if(now-s.lastPoll>=1000){s.lastPoll=now;
  if(s.vm!=vm){s.vm=vm;s.installed=0;s.phase=0;record("vm|new campaign state");}
  if(execute(vm,s.source,0x21,true))s.installed=1;
 }
 if(!s.installed||s.error)return;
 if(s.phase==7&&!advancing){
  if(!availableNext()){s.phase=6;record("advance_refused|next scenario is not in the validated catalogue");return;}
  advancing=true;dismissed=false;s.phase=8;
  // Dismiss our local report before notifying the native lifecycle. Its own
  // campaign PGCR, if requested, is acknowledged once it becomes visible.
  if(s.xmlLength&&*(uint32_t*)(base()+0x6415d98)==4){uint32_t hide=0xc9c2975a;
   ((void(*)(uint64_t,uint32_t,uint32_t,uint32_t*))(base()+0x1ddcf30))(base()+0x6415d50,4,0,&hide);*(uint32_t*)(base()+0x6415d98)=0;}
  record("advance|native game_won released after validated handoff");releaseWon=true;
  ((void(*)())(base()+wonRva))();releaseWon=false;
 }
 if(advancing&&!dismissed&&*(unsigned char*)(game()+0x56750)&&*(uint32_t*)(base()+0x6415d98)==4){
  dismissed=true;record("advance|acknowledge native campaign report");((void(*)())(base()+0x1dde280))();
 }
 if(InterlockedCompareExchange(&s.command,0,1)==1){
  if(s.phase==0)execute(vm,"local s=rawget(_G,'H5CampaignCompletion'); if s then s.TestEnd(); end; return true;",0x22);
  else record("test_refused|completion already started");
 }
 if(s.phase==5&&s.xmlLength==0){if(!publishResults(availableNext())){s.error=24;return;}showResults();}
 if(s.phase==0&&s.xmlLength){s.xmlLength=0;}
}
static LONG CALLBACK trap(EXCEPTION_POINTERS*x){if(x->ExceptionRecord->ExceptionCode!=EXCEPTION_BREAKPOINT)return EXCEPTION_CONTINUE_SEARCH;
 auto&s=CompletionState;auto c=x->ContextRecord;auto at=(uint64_t)x->ExceptionRecord->ExceptionAddress;
 if(errorsArmed&&(at==base()+0x835c4c||at==base()+0x835dc2)){
  auto vm=c->R13;auto v=*(uint64_t*)(vm+0x48)-16;auto str=((const char*(*)(uint64_t,void*,void*))(base()+0x7cdec0))(vm,(void*)v,nullptr);
  if(str)strncpy_s(s.message,str,_TRUNCATE);c->Rcx=*(uint64_t*)(vm+0x48);c->Rip=at+4;return EXCEPTION_CONTINUE_EXECUTION;}
 if(!s.armed)return EXCEPTION_CONTINUE_SEARCH;
 if(at==base()+updateRva){
  if(!s.thread)s.thread=GetCurrentThreadId();
  if(s.thread==GetCurrentThreadId()&&InterlockedCompareExchange(&s.busy,1,0)==0){
   __try{tick();}__except(EXCEPTION_EXECUTE_HANDLER){s.error=(LONG)GetExceptionCode();}
   InterlockedExchange(&s.busy,0);
  }
  // Original two-byte push r14; the native stack allocation follows unchanged.
  c->Rsp-=8;*(uint64_t*)c->Rsp=c->R14;c->Rip=at+2;return EXCEPTION_CONTINUE_EXECUTION;
 }
 if(at==base()+wonRva){
  if(!returningToMenu&&campaign()&&!releaseWon){InterlockedIncrement(&s.wonBlocked);record("game_won_held|awaiting completion handoff");c->Rip=*(uint64_t*)c->Rsp;c->Rsp+=8;}
  else{c->Rsp-=0x38;c->Rip=at+4;}
  return EXCEPTION_CONTINUE_EXECUTION;
 }
 if(at==base()+visibleRva){
  if((s.phase==5||s.phase==6||s.phase==7)&&s.xmlLength){if(s.phase==5&&!*(unsigned char*)c->Rdx){s.phase=availableNext()?7:6;InterlockedIncrement(&s.continues);record(s.phase==7?"continue|validated next scenario":"continue|next scenario unavailable");}c->Rip=*(uint64_t*)c->Rsp;c->Rsp+=8;}
  else c->Rip=base()+(*(unsigned char*)c->Rdx?0x1ddeca0:0x1dde280);
  return EXCEPTION_CONTINUE_EXECUTION;
 }
 if(at==base()+loomOptionsRva||at==base()+loomPathRva){
  // Both native background-scenario request forms converge here. Cinematic
  // tracks can invoke these before their ending script reaches game_won.
  auto g=game();bool owned=returningToMenu||s.phase>0||campaign();
  bool menu=owned&&menuPreload((void*)c->Rcx,at==base()+loomPathRva);
  bool valid=owned&&!returningToMenu&&g&&successorPreload(*(uint32_t*)(g+0x3603c),(void*)c->Rcx,at==base()+loomPathRva);
  if(menu)beginMenuReturn();
  if(owned&&!menu&&!valid){
   record(at==base()+loomPathRva?"preload_held|scenario path":"preload_held|next campaign options");
   c->Rip=*(uint64_t*)c->Rsp;c->Rsp+=8;
  }else{if(valid)record("preload_allowed|validated authored successor");*(uint64_t*)(c->Rsp+8)=c->Rbx;c->Rip=at+5;}
  return EXCEPTION_CONTINUE_EXECUTION;
 }
 return EXCEPTION_CONTINUE_SEARCH;
}

}
