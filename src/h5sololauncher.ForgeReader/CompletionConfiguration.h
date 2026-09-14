#pragma once
#include "MenuAssets.h"
namespace h5runtime::completion {
struct CampaignMapList {uint32_t id,next;std::string scenario;std::vector<unsigned char> target;};
struct ReportHost {menuAssets::Proof proof;std::vector<unsigned char> before,after;};
inline std::vector<CampaignMapList> campaign_maps;
inline ReportHost reportHosts[2];
inline void parseConfiguration(const std::vector<unsigned char>& data){
    content::Wire wire(data);require(wire.number<uint32_t>()==0x50433548 && wire.number<uint32_t>()==1 && wire.string()=="Microsoft.Halo5Forge_1.194.6192.2_x64__8wekyb3d8bbwe","The mission completion configuration is incompatible.");
    for(unsigned i=0;i<2;++i){auto& host=reportHosts[i];host.proof={wire.number<uint32_t>(),{wire.number<uint64_t>(),wire.number<uint64_t>()}};host.before=wire.blob(1024*1024);host.after=wire.blob(1024*1024);require(host.proof.gid==(i==0?0xf79a5480u:0x5887e62fu) && !host.before.empty() && (i==0?host.after.empty():!host.after.empty()),"The campaign report has an unsupported host.");}
    auto count=wire.number<uint32_t>();require(count==3,"The supported campaign successor set is incomplete.");std::set<uint32_t> seen;
    for(unsigned i=0;i<count;++i){CampaignMapList map{wire.number<uint32_t>(),wire.number<uint32_t>(),wire.string(),wire.blob(4096)};
        require(seen.insert(map.id).second && map.scenario.size()<260 && map.scenario.rfind("levels/",0)==0 && map.scenario.find("..") == std::string::npos && !map.target.empty() && !map.target.back(),"A campaign successor route is invalid.");campaign_maps.push_back(std::move(map));}
    require(wire.end(),"The mission completion configuration contains trailing data.");
}
}
