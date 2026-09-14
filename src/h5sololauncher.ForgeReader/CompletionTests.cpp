#ifdef NDEBUG
#undef NDEBUG
#endif
#pragma warning(disable:4505) // This test uses the pure lifecycle functions only.
#include "CompletionLifecycle.h"
using namespace h5runtime::completion;
#include <cassert>
int main(){
 campaign_maps={{158432,1453154147,"levels/campaignworld030/w3_halsey/w3_halsey",{'a',0,0}},
 {1453154147,2362033,"levels/cinematics/cin_030/cin_030",{'b',0,0}},
 {2362033,3323466730u,"levels/campaignworld040/w4_station/w4_station",{'c',0,0}}};
 auto previousEvents=CompletionState.events;record(std::string(8192,'x').c_str());
 assert(CompletionState.events==previousEvents+1);
 assert(strlen(CompletionState.reports[previousEvents%64])==1023);
 for(auto& current:campaign_maps)for(auto& next:campaign_maps){
  const bool expected=current.next==next.id;
  assert(isSuccessor(current.id,next.scenario.c_str())==expected);
  std::string normalized=next.scenario;for(char& c:normalized){if(c=='/')c='\\';else if(c>='a'&&c<='z')c-=32;}
  assert(isSuccessor(current.id,normalized.c_str())==expected);
 }
 assert(!mapByPath(nullptr));assert(!mapByPath(std::string(700,'a').c_str()));
 assert(!isSuccessor(0xffffffff,campaign_maps[0].scenario.c_str()));
 assert(!isSuccessor(campaign_maps[0].id,"levels/future/not_imported"));
 for(auto& map:campaign_maps){
  alignas(8) unsigned char options[0x1178]={};
  *(uint32_t*)options=static_cast<uint32_t>(map.target.size());memcpy(options+8,map.target.data(),map.target.size());
  strcpy_s((char*)options+0x1048,260,map.scenario.c_str());
  *(uint32_t*)(options+0x114c)=5;*(uint32_t*)(options+0x1150)=map.id;
  assert(matchingPreload(&map,options,false));
  assert(matchingPreload(&map,options+0x1048,true));
  for(auto& current:campaign_maps){
   assert(successorPreload(current.id,options,false)==(current.next==map.id));
   assert(successorPreload(current.id,options+0x1048,true)==(current.next==map.id));
  }
  assert(!successorPreload(0xffffffff,options,false));
  options[8]^=1;assert(!matchingPreload(&map,options,false));options[8]^=1;
  (*(uint32_t*)options)++;assert(!matchingPreload(&map,options,false));(*(uint32_t*)options)--;
  (*(uint32_t*)(options+0x1150))++;assert(!matchingPreload(&map,options,false));(*(uint32_t*)(options+0x1150))--;
  *(uint32_t*)(options+0x114c)=0;assert(!matchingPreload(&map,options,false));
  memset(options+0x1048,'x',260);assert(!matchingPreload(&map,options+0x1048,true));
 }
 assert(!matchingPreload(nullptr,nullptr,false));
 assert(!matchingPreload(&campaign_maps[0],(void*)1,false));
 assert(escaped("<&>\"'\n") == "&lt;&amp;&gt;&quot;&apos;");
 auto&s=CompletionState;alignas(8) unsigned char root[224]={};
 s.wpfRoot=(uint64_t)root;s.originalXml=0x12345678;s.originalLength=12;
 *(uint32_t*)(root+8)=0xf79a5480;*(uint64_t*)(root+160)=s.originalXml;
 strcpy_s(s.xmlTemplate,"<r mission=\"{{MISSION}}\" next=\"{{NEXT}}\">{{SCORES}}</r>");
 strcpy_s(s.mission,"future<&\"");strcpy_s(s.next,"levels/future/next");strcpy_s(s.scores,"Player 1: 12345");
 assert(publishResults(false));assert(strstr(s.xml,"future&lt;&amp;&quot;"));
 assert(*(uint64_t*)(root+160)==(uint64_t)s.xml && *(uint32_t*)(root+184)==strlen(s.xml)+1);
 strcpy_s(s.mission,"another mission");assert(publishResults(false));assert(strstr(s.xml,"another mission"));
 strcpy_s(s.xmlTemplate,"{{CONTINUETITLE}} {{CONTINUEMESSAGE}}");
 assert(publishResults(true)&&strstr(s.xml,"LOADING NEXT MISSION"));
 assert(publishResults(false)&&strstr(s.xml,"NEXT MISSION UNAVAILABLE"));
 *(uint32_t*)(root+8)=0;assert(!publishResults(false));*(uint32_t*)(root+8)=0xf79a5480;
 *(uint64_t*)(root+160)=0x87654321;assert(!publishResults(false));*(uint64_t*)(root+160)=s.originalXml;
 strcpy_s(s.xmlTemplate,"{{UNRESOLVED}}");assert(!publishResults(false));
 strcpy_s(s.scores,"&&&&&&&&&&&&&&&&");std::string large;for(int n=0;n<1500;n++)large+="{{SCORES}}";strcpy_s(s.xmlTemplate,large.c_str());assert(!publishResults(false));
 puts("PASS: XML escaping, result publication, repeated report data, asset identity, pointer guard, unresolved values, expanded-size limit");
}
