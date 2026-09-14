#pragma once
#include "RuntimeSupport.h"
#include "ContentRouting.h"
#include "MenuGuards.h"
#include <array>
#include <map>
#include <sstream>

namespace h5runtime::menuAssets {
struct Key{uint64_t asset,checksum;};
struct Proof{uint32_t gid;Key key;};
struct Root{uint64_t root,record;uint32_t handle;Proof proof;};
struct Picture{Proof proof;std::string name;};
struct Shared{uint64_t object,control;};
struct ResourceKey{Key key;uint32_t part,padding;};
inline Shared moduleRequest{},requests[5]{};
inline std::vector<Picture> pictures;
inline std::map<uint32_t,Root> find(uint64_t base) {
    auto pool=value<uint64_t>(base+0x5f20768);require(pool && value<uint64_t>(pool+0x20)==88,"Forge's menu asset pool differs.");
    auto count=value<uint32_t>(pool+0x4c);auto storage=value<uint64_t>(pool+0x58);require(count && count<=0x15400,"Forge's menu asset count differs.");
    auto data=bytes(storage,count*88ull);std::map<uint32_t,Root> roots;
    for(unsigned i=0;i<count;++i){auto row=data.data()+i*88;uint32_t handle,gid;uint64_t root;memcpy(&handle,row,4);memcpy(&gid,row+76,4);memcpy(&root,row+40,8);if(!root || gid==0xffffffff || handle>>15!=i)continue;
        Proof proof{gid,{}};memcpy(&proof.key,row+16,16);
        if(value<uint32_t>(root+8)!=gid || value<uint32_t>(root+12)!=handle)continue;
        require(roots.emplace(gid,Root{root,storage+i*88,handle,proof}).second,"Several live menu assets share the same identifier.");
    }
    return roots;
}
inline Root resolve(const std::map<uint32_t,Root>& roots,const Proof& proof) {
    auto found=roots.find(proof.gid);
    if(found==roots.end() || found->second.proof.key.asset!=proof.key.asset || found->second.proof.key.checksum!=proof.key.checksum){std::ostringstream message;message<<"A required menu asset is missing or has a different version: "<<std::hex<<proof.gid;throw std::runtime_error(message.str());}
    return found->second;
}
inline void load(uint64_t base) {
    menuGuards(base);require(content::phase==4 && !content::menuAlias.empty(),"Verify and activate campaign artwork before loading the campaign menu.");
    auto manager=value<uint64_t>(base+0x58fb848);auto backend=value<uint64_t>(manager+0x30);require(value<uint64_t>(backend)==base+0x33f46f0,"Forge's native resource backend differs.");
    unsigned char looming=0;const char* alias="h5solo-menu.module";
    reinterpret_cast<void*(*)(Shared*,uint64_t*,const char*,unsigned char*)>(base+0x2a00d40)(&moduleRequest,&manager,alias,&looming);
    require(moduleRequest.object && moduleRequest.control && value<uint64_t>(moduleRequest.object)==base+0x39cbf00,"Forge did not create its menu module request.");
    auto actual=bytes(moduleRequest.object+0x48,strlen(alias)+1);require(!memcmp(actual.data(),alias,actual.size()),"The menu module request names another file.");
    reinterpret_cast<void(*)(uint64_t,Shared*)>(base+0xa5c940)(backend,&moduleRequest);
    // The native backend asynchronously registers the module. Bound the wait;
    // requests and shared owners stay alive until Forge exits.
    Sleep(1000);
    for(unsigned i=0;i<pictures.size();++i){auto& picture=pictures[i];ResourceKey key{picture.proof.key,0,0};
        reinterpret_cast<void(*)(uint64_t,Key*)>(base+0x2a0d3a0)(manager,&key.key);
        reinterpret_cast<void*(*)(uint64_t,Shared*,ResourceKey*)>(base+0x2a078f0)(manager,&requests[i],&key);
        if(requests[i].object){*reinterpret_cast<uint32_t*>(requests[i].object+0xfc)=0x6269746d;reinterpret_cast<void(*)(uint64_t,Shared*)>(base+0xa5c840)(backend,&requests[i]);}
        auto began=GetTickCount64();
        while(reinterpret_cast<unsigned char(*)(uint64_t,ResourceKey*)>(base+0x2a075c0)(manager,&key)!=2){require(GetTickCount64()-began<30000,"Forge did not finish loading campaign menu artwork.");Sleep(100);}
        uint32_t handle=0xffffffff,gid=picture.proof.gid;
        reinterpret_cast<void*(*)(uint32_t*,uint32_t,const char*,Key*,uint32_t*,bool)>(base+0x29ef310)(&handle,0x6269746d,picture.name.c_str(),&key.key,&gid,false);
        require(handle!=0xffffffff && gid==picture.proof.gid,"Forge did not allocate its campaign picture handle.");
        auto record=reinterpret_cast<uint64_t(*)(uint32_t*)>(base+0x29edcd0)(&handle);auto root=value<uint64_t>(record+0x28);
        require(root && value<uint32_t>(root+8)==gid && value<uint32_t>(root+12)==handle && value<uint64_t>(record+16)==key.key.asset && value<uint64_t>(record+24)==key.key.checksum,"Forge returned a different campaign picture.");
        auto table=value<void**>(base+0x6c0b500);auto index=handle>>15;require(table && index<0x15400,"The native picture root table is unavailable.");
        auto old=InterlockedCompareExchangePointer(table+index,reinterpret_cast<void*>(root),nullptr);require(!old || old==reinterpret_cast<void*>(root),"A campaign picture root is already owned by another asset.");
    }
}
}
