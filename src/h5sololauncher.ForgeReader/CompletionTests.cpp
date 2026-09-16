#ifdef NDEBUG
#undef NDEBUG
#endif
#pragma warning(disable:4505) // This test uses the pure lifecycle functions only.
#include "CompletionLifecycle.h"
using namespace h5runtime::completion;
#include <cassert>
int main(){
 campaign_maps={{158432u,1453154147u,"levels/campaignworld030/w3_halsey/w3_halsey",{'a',0,0}},
 {1453154147u,2362033u,"levels/cinematics/cin_030/cin_030",{'b',0,0}},
 {2362033u,3323466730u,"levels/campaignworld040/w4_station/w4_station",{'c',0,0}},
 {3323466730u,4198816u,"levels/cinematics/cin_060/cin_060",{'d',0,0}},
 {4198816u,4309647u,"levels/campaignworld010/w1_meridian/w1_meridian",{'e',0,0}},
 {4309647u,4199692u,"levels/campaignworld010/w1_miningtown/w1_miningtown",{'f',0,0}},
 {4199692u,193574u,"levels/campaignworld010/w1_unconfirmed_reports/w1_unconfirmed_reports",{'g',0,0}},
 {193574u,1496944548u,"levels/campaignworld010/w1_evacuation/w1_evacuation",{'h',0,0}},
 {1496944548u,158874u,"levels/cinematics/cin_110/cin_110",{'i',0,0}},
 {158874u,1601191485u,"levels/campaignworld030/w3_builder/w3_builder",{'j',0,0}},
 {1601191485u,558531u,"levels/cinematics/cin_120/cin_120",{'k',0,0}},
 {558531u,4143105u,"levels/campaignworld020/w2_grotto/w2_grotto",{'l',0,0}},
 {4143105u,4142841u,"levels/campaignworld020/w2_campsite/w2_campsite",{'m',0,0}},
 {4142841u,4316546u,"levels/campaignworld020/w2_plateau/w2_plateau",{'n',0,0}},
 {4316546u,69764u,"levels/campaignworld020/w2_campsite_return/w2_campsite_return",{'o',0,0}},
 {69764u,3762540u,"levels/campaignworld020/w2_tsunami/w2_tsunami",{'p',0,0}},
 {3762540u,157705u,"levels/campaignworld030/w3_arrival/w3_arrival",{'q',0,0}},
 {157705u,3771230u,"levels/campaignworld030/w3_citadel/w3_citadel",{'r',0,0}},
 {3771230u,4294967295u,"levels/campaignworld030/w3_innerworld/w3_innerworld",{'s',0,0}}};
 auto routes=campaign_maps;
 for(uint32_t count:{3u,4u,5u,6u,7u,8u,18u,19u}){
  std::vector<unsigned char> config;
  auto number=[&](auto value){auto p=(const unsigned char*)&value;config.insert(config.end(),p,p+sizeof(value));};
  auto blob=[&](const std::vector<unsigned char>& value){number((uint32_t)value.size());config.insert(config.end(),value.begin(),value.end());};
  auto text=[&](const std::string& value){blob({value.begin(),value.end()});};
  number(0x50433548u);number(1u);text("Microsoft.Halo5Forge_1.194.6192.2_x64__8wekyb3d8bbwe");
  for(uint32_t i=0;i<2;++i){number(i==0?0xf79a5480u:0x5887e62fu);number(uint64_t{1});number(uint64_t{2});blob({1});blob(i==0?std::vector<unsigned char>{}:std::vector<unsigned char>{2});}
  number(count);for(uint32_t i=0;i<count;++i){auto& map=routes[i];number(map.id);number(map.next);text(map.scenario);blob(map.target);}
  campaign_maps.clear();bool accepted=true;try{parseConfiguration(config);}catch(const std::exception&){accepted=false;}
  assert(accepted==(count==3 || count==5 || count==6 || count==8 || count==19));if(accepted)assert(campaign_maps.size()==count);
 }
 campaign_maps=routes;
 assert(isSuccessor(2362033,"levels/cinematics/cin_060/cin_060"));
 assert(isSuccessor(3323466730u,"levels/campaignworld010/w1_meridian/w1_meridian"));
 assert(isSuccessor(4198816,"levels/campaignworld010/w1_miningtown/w1_miningtown"));
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
 // Native Leave Game can request the menu while campaign flags are still set.
 alignas(8) unsigned char menuOptions[0x1178]={};
 strcpy_s((char*)menuOptions+0x1048,260,"levels/ui/mainmenu/mainmenu");
 assert(menuPreload(menuOptions,false));
 assert(menuPreload(menuOptions+0x1048,true));
 for(auto& map:campaign_maps){assert(!successorPreload(map.id,menuOptions,false));assert(!successorPreload(map.id,menuOptions+0x1048,true));}
 strcpy_s((char*)menuOptions+0x1048,260,"LEVELS\\UI\\MAINMENU\\MAINMENU");
 assert(menuPreload(menuOptions,false));
 for(const char* path:{"levels/ui/mainmenu/mainmenu_extra","levels/ui/mainmenu/mainmenu/other","levels/campaignworld030/w3_halsey/w3_halsey","","levels/ui/mainmenu"}){
  strcpy_s((char*)menuOptions+0x1048,260,path);assert(!menuPreload(menuOptions,false));
 }
 assert(!menuPreload(nullptr,true));assert(!menuPreload((void*)1,true));
 memset(menuOptions+0x1048,'x',260);assert(!menuPreload(menuOptions,false));
 auto& leaving=CompletionState;leaving.phase=5;leaving.installed=1;leaving.command=1;
 leaving.vm=123;leaving.lastPoll=456;leaving.xmlLength=10;strcpy_s(leaving.next,"stale successor");
 activeMap=158432;advancing=dismissed=releaseWon=true;
 auto leaveEvents=leaving.events;beginMenuReturn();
 assert(returningToMenu&&activeMap==0xffffffff&&!advancing&&!dismissed&&!releaseWon);
 assert(!leaving.phase&&!leaving.installed&&!leaving.command&&!leaving.vm&&!leaving.lastPoll&&!leaving.xmlLength&&!leaving.next[0]);
 assert(leaving.events==leaveEvents+1);beginMenuReturn();assert(leaving.events==leaveEvents+1);
 returningToMenu=false;
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
 puts("PASS: campaign routing, menu destination validation, leave-state reset, XML publication and guards");
}
