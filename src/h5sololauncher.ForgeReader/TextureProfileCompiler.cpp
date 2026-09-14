// Development-only numeric layout verifier. Not shipped with the launcher.
// The reference library path is an explicit command-line input; it is never loaded in Forge.
#include <windows.h>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <filesystem>
#include <vector>
#include <stdexcept>
#include <algorithm>
#include <string>

struct Description2D { uint32_t w,h,mips,array,format,samples,quality,usage,bind,cpu,misc,esramOffset,esramSize,tile,pitch; };
struct Description3D { uint32_t w,h,depth,mips,format,usage,bind,cpu,misc,esramOffset,esramSize,tile,pitch; };
struct Shape { uint32_t w,h,depth,kind,format,mips,tile,dxgi,bpe,block; };
using Offset = uint64_t(*)(void*,uint32_t,uint32_t,uint64_t,uint32_t,uint32_t,uint32_t);
using Release = uint32_t(*)(void*);
struct Computer {
    void* object{}; Offset offset{}; Release release{};
    Computer(HMODULE library, const Shape& s) {
        HRESULT result;
        if (s.kind == 1) {
            Description3D desc{s.w,s.h,s.depth,s.mips,s.dxgi,0,8,0,0,0,0,s.tile,0};
            auto create = reinterpret_cast<HRESULT(*)(const Description3D*,void**)>(GetProcAddress(library,"XGCreateTexture3DComputer"));
            if (!create) throw std::runtime_error("Missing 3D reference export");
            result = create(&desc,&object);
        } else {
            Description2D desc{s.w,s.h,s.mips,s.kind==2 ? 6u:s.depth,s.dxgi,1,0,0,8,0,0,0,0,s.tile,0};
            auto create = reinterpret_cast<HRESULT(*)(const Description2D*,void**)>(GetProcAddress(library,"XGCreateTexture2DComputer"));
            if (!create) throw std::runtime_error("Missing 2D reference export");
            result = create(&desc,&object);
        }
        if (FAILED(result) || !object) throw std::runtime_error("Reference rejected texture descriptor");
        auto table = *reinterpret_cast<void***>(object);
        release = reinterpret_cast<Release>(table[1]); offset = reinterpret_cast<Offset>(table[6]);
    }
    ~Computer() { if(object && release) release(object); }
    uint64_t get(uint32_t mip,uint32_t x,uint32_t y,uint32_t z) const { return offset(object,0,mip,x,y,z,0); }
};
static unsigned bits(uint32_t count) { unsigned n=0; while((uint64_t(1)<<n)<count) ++n; return n; }
static uint64_t fold(uint32_t coordinate,const std::vector<uint64_t>& values) {
    uint64_t result=0; for(unsigned bit=0;bit<values.size();++bit) if(coordinate&(1u<<bit)) result^=values[bit]; return result;
}
static void array(std::ostream& out,const std::vector<uint64_t>& data) {
    out << '['; for(size_t i=0;i<data.size();++i) { if(i) out << ','; out << data[i]; } out << ']';
}
int wmain(int argc,wchar_t** argv) {
    if(argc!=4) { std::cerr<<"Expected <reference library> <shape list> <output json>\n"; return 2; }
    const auto library=LoadLibraryExW(argv[1],nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if(!library) { std::cerr<<"Reference library load failed: "<<GetLastError()<<'\n';return 3; }
    try {
        std::ifstream input{std::filesystem::path(argv[2])}; std::ofstream out{std::filesystem::path(argv[3])};
        if(!input || !out) throw std::runtime_error("Cannot open descriptor input or profile output");
        out<<"{\"Format\":1,\"Rules\":\"verified-texture-addresses-1\",\"Profiles\":[";
        Shape s{}; size_t shapes=0; uint64_t allElements=0;
        while(input>>s.w>>s.h>>s.depth>>s.kind>>s.format>>s.mips>>s.tile>>s.dxgi>>s.bpe>>s.block) {
            if(!s.w||s.w>8192||!s.h||s.h>8192||!s.depth||s.depth>128||s.kind>3||!s.mips||s.mips>16||
                (s.tile!=13&&s.tile!=14)||(s.bpe!=1&&s.bpe!=2&&s.bpe!=4&&s.bpe!=8&&s.bpe!=16)||(s.block!=1&&s.block!=4))
                throw std::runtime_error("Descriptor is outside compiler limits");
            Computer computer(library,s); if(shapes++)out<<',';
            const auto key=std::to_string(s.w)+"-"+std::to_string(s.h)+"-"+std::to_string(s.depth)+"-"+std::to_string(s.kind)+"-"+std::to_string(s.format)+"-"+std::to_string(s.mips)+"-"+std::to_string(s.tile);
            out<<"{\"Key\":\""<<key<<"\",\"Mips\":[";
            std::vector<bool> seen; uint64_t elements=0,maximum=0;
            for(uint32_t mip=0;mip<s.mips;++mip) {
                const auto w=(std::max(1u,s.w>>mip)+s.block-1)/s.block,h=(std::max(1u,s.h>>mip)+s.block-1)/s.block;
                const auto depth=s.kind==1 ? std::max(1u,s.depth>>mip):s.kind==2 ? 6u:s.depth;
                const auto origin=computer.get(mip,0,0,0); std::vector<uint64_t> xs,ys,zs;
                for(unsigned b=0;b<bits(w);++b)xs.push_back(computer.get(mip,1u<<b,0,0)^origin);
                for(unsigned b=0;b<bits(h);++b)ys.push_back(computer.get(mip,0,1u<<b,0)^origin);
                for(unsigned b=0;b<bits(depth);++b)zs.push_back(computer.get(mip,0,0,1u<<b)^origin);
                bool affine=true;
                for(uint32_t z=0;z<depth;++z)for(uint32_t y=0;y<h;++y)for(uint32_t x=0;x<w;++x) {
                    const auto expected=computer.get(mip,x,y,z);
                    if(expected%s.bpe || expected>512ull*1024*1024-s.bpe)throw std::runtime_error("Reference address exceeds bounded resource size");
                    const auto slot=static_cast<size_t>(expected/s.bpe);
                    if(slot>=seen.size())seen.resize(std::max(slot+1,seen.size()*2),false);
                    if(seen[slot])throw std::runtime_error("Reference mapping aliases texture elements");
                    seen[slot]=true;++elements;maximum=std::max(maximum,expected+s.bpe);
                    if((origin^fold(x,xs)^fold(y,ys)^fold(z,zs))!=expected)affine=false;
                }
                std::vector<uint64_t> tiles;
                if(!affine) {
                    // Exact 8x8 microtile origins handle non-power-of-two macro pitches.
                    xs.resize(std::min(size_t(3),xs.size())); ys.resize(std::min(size_t(3),ys.size())); zs.clear();
                    const auto tw=(w+7)/8,th=(h+7)/8;
                    for(uint32_t z=0;z<depth;++z)for(uint32_t y=0;y<th;++y)for(uint32_t x=0;x<tw;++x)tiles.push_back(computer.get(mip,x*8,y*8,z));
                    for(uint32_t z=0;z<depth;++z)for(uint32_t y=0;y<h;++y)for(uint32_t x=0;x<w;++x) {
                        const auto value=tiles[(z*th+y/8)*tw+x/8]^fold(x%8,xs)^fold(y%8,ys);
                        if(value!=computer.get(mip,x,y,z))throw std::runtime_error("Microtile rule differs from reference: "+key);
                    }
                }
                if(mip)out<<',';
                out<<"{\"Width\":"<<w<<",\"Height\":"<<h<<",\"Slices\":"<<depth<<",\"Origin\":"<<origin<<",\"X\":";array(out,xs);
                out<<",\"Y\":";array(out,ys);out<<",\"Z\":";array(out,zs);out<<",\"Tiles\":";array(out,tiles);out<<'}';
            }
            out<<"],\"ElementsChecked\":"<<elements<<",\"MaximumEnd\":"<<maximum<<",\"Exhaustive\":true}";
            allElements+=elements; std::cout<<key<<" verified "<<elements<<" elements\n";
        }
        if(!input.eof()||!shapes)throw std::runtime_error("Malformed or empty descriptor list");
        out<<"]}";out.flush();if(!out)throw std::runtime_error("Profile write failed");
        std::cout<<"Verified "<<shapes<<" shapes and "<<allElements<<" addresses\n";
    } catch(const std::exception& e) { std::cerr<<e.what()<<'\n';FreeLibrary(library);return 1; }
    FreeLibrary(library);return 0;
}
