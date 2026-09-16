// Development-only exhaustive address-table compiler. No SDK code is shipped.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <vector>
#include <string>
#include <stdexcept>
#include <algorithm>
struct Desc {uint32_t w,h,mips,array,format,samples,quality,usage,bind,cpu,misc,esramOffset,esramUsage,tile,pitch;};
using Create=HRESULT(__cdecl*)(Desc*,void**);
using Release=ULONG(__cdecl*)(void*);
using Offset=uint64_t(__cdecl*)(void*,uint32_t,uint32_t,uint64_t,uint32_t,uint32_t,uint32_t);
static void require(bool value){if(!value)throw std::runtime_error("Address verification failed");}
static uint64_t fold(uint32_t n,const std::vector<uint64_t>& bits){uint64_t v=0;for(size_t i=0;n;n>>=1,i++)if(n&1)v^=bits.at(i);return v;}
static void array(std::ostream& o,const std::vector<uint64_t>& v){o<<'[';for(size_t i=0;i<v.size();i++){if(i)o<<',';o<<v[i];}o<<']';}
int wmain(int argc,wchar_t** argv){
 try{
  require(argc==4);HMODULE dll=LoadLibraryW(argv[1]);require(dll!=nullptr);
  auto create=(Create)GetProcAddress(dll,"XGCreateTexture2DComputer");require(create!=nullptr);
  std::ifstream in(argv[2]);std::ofstream out(argv[3]);require(bool(in)&&bool(out));out<<'[';bool first=true;
  uint32_t w,h,depth,kind,fmt,mips,tile,dxgi,bpe,block;
  while(in>>w>>h>>depth>>kind>>fmt>>mips>>tile>>dxgi>>bpe>>block){
   require(w>0&&h>0&&w<=8192&&h<=8192&&depth>0&&depth<=128&&kind!=1&&kind<=3&&mips>0&&mips<=16&&bpe>0&&block>0);
   uint32_t slices=kind==2?6:depth;Desc d={w,h,mips,slices,dxgi,1,0,0,8,0,0,0,0,tile,0};void* obj=nullptr;
   require(SUCCEEDED(create(&d,&obj))&&obj);auto vt=*(void***)obj;auto release=(Release)vt[1];auto offset=(Offset)vt[6];
   if(!first)out<<',';first=false;std::string key=std::to_string(w)+"-"+std::to_string(h)+"-"+std::to_string(depth)+"-"+std::to_string(kind)+"-"+std::to_string(fmt)+"-"+std::to_string(mips)+"-"+std::to_string(tile);
   out<<"{\"Key\":\""<<key<<"\",\"Mips\":[";uint64_t count=0,maximum=0;std::vector<uint8_t> seen;
   for(uint32_t mip=0;mip<mips;mip++){
    uint32_t ew=(std::max(1u,w>>mip)+block-1)/block,eh=(std::max(1u,h>>mip)+block-1)/block;
    auto at=[&](uint32_t x,uint32_t y,uint32_t z){return offset(obj,0,mip,x,y,z,0);};
    uint64_t origin=at(0,0,0);std::vector<uint64_t> xs,ys,tiles;
    for(uint32_t x=1;x<std::min(8u,ew);x<<=1)xs.push_back(origin^at(x,0,0));
    for(uint32_t y=1;y<std::min(8u,eh);y<<=1)ys.push_back(origin^at(0,y,0));
    auto tw=(ew+7)/8,th=(eh+7)/8;
    for(uint32_t z=0;z<slices;z++)for(uint32_t y=0;y<eh;y+=8)for(uint32_t x=0;x<ew;x+=8)tiles.push_back(at(x,y,z));
    for(uint32_t z=0;z<slices;z++)for(uint32_t y=0;y<eh;y++)for(uint32_t x=0;x<ew;x++){
     auto native=at(x,y,z);auto derived=tiles[(z*th+y/8)*tw+x/8]^fold(x&7,xs)^fold(y&7,ys);
     require(native==derived&&native%bpe==0&&native+bpe<=512ull*1024*1024);
     auto bit=native/bpe;auto byte=bit/8;if(seen.size()<=byte)seen.resize(byte+1);require(!(seen[byte]&(1u<<(bit&7))));seen[byte]|=1u<<(bit&7);
     maximum=std::max(maximum,native+bpe);count++;
    }
    if(mip)out<<',';out<<"{\"Width\":"<<ew<<",\"Height\":"<<eh<<",\"Slices\":"<<slices<<",\"Origin\":0,\"X\":";array(out,xs);out<<",\"Y\":";array(out,ys);out<<",\"Z\":[],\"Tiles\":";array(out,tiles);out<<'}';
   }
   release(obj);out<<"],\"ElementsChecked\":"<<count<<",\"MaximumEnd\":"<<maximum<<",\"Exhaustive\":true}";out.flush();std::cout<<key<<" verified "<<count<<" addresses"<<std::endl;
  }
  out<<']';require(bool(out));FreeLibrary(dll);return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<std::endl;return 1;}
}
