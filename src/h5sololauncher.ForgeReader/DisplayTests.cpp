#ifdef NDEBUG
#undef NDEBUG
#endif
#pragma warning(disable:4505)
#include "DisplaySettings.h"
#include <cassert>
#include <cstdio>
using namespace h5runtime::display;
int main(){
    auto allocation=static_cast<unsigned char*>(VirtualAlloc(nullptr,0x6143000,MEM_RESERVE,PAGE_READWRITE));assert(allocation);
    assert(VirtualAlloc(allocation+0x6141000,4096,MEM_COMMIT,PAGE_READWRITE));image=reinterpret_cast<uint64_t>(allocation);
    EXCEPTION_RECORD exception{};CONTEXT context{};EXCEPTION_POINTERS event{&exception,&context};exception.ExceptionCode=EXCEPTION_BREAKPOINT;
    auto call=[&](uint64_t rva){exception.ExceptionAddress=reinterpret_cast<void*>(image+rva);return trap(&event);};
    unsigned char profile[0xc0]{};*reinterpret_cast<int32_t*>(profile+8)=-123;context.Rcx=reinterpret_cast<uint64_t>(profile);
    for(unsigned option=0;option<5;++option){context.R14=option;float period=0.25f;memcpy(&context.Xmm10.Low,&period,4);
        assert(call(0x1551ecf)==EXCEPTION_CONTINUE_EXECUTION);memcpy(&period,&context.Xmm10.Low,4);
        assert(context.Rdi==static_cast<uint64_t>(-123ll));assert(context.Rip==image+0x1551ed3);
        assert(fabsf(period-(option==3?1.0f/144:option==4?1.0f/180:0.25f))<0.000001f);
        context.Rbx=option;assert(call(0x152b28e)==EXCEPTION_CONTINUE_EXECUTION);assert(context.Rdx==(option>=3?1:option));
        *reinterpret_cast<int*>(profile+0xac)=option;allocation[0x614168a]=1;assert(call(0x155f483)==EXCEPTION_CONTINUE_EXECUTION);
        assert(context.Rip==image+(option>=3?0x155f4f1:option>1?0x155f4ea:0x155f48c));assert(allocation[0x614168a]==(option>=3?0:1));
        *reinterpret_cast<int*>(allocation+0x614194c)=option;context.Rdi=16666;
        assert(call(0xad5217)==EXCEPTION_CONTINUE_EXECUTION);assert(context.Rdi==(option==3?6944:option==4?5556:16666));
        context.Rdi=33333;assert(call(0xad5217)==EXCEPTION_CONTINUE_EXECUTION);assert(context.Rdi==33333); // Explicit cinematic timing is preserved.
        context.Rcx=reinterpret_cast<uint64_t>(profile);
    }
    unsigned char output[16];memset(output,0xa5,sizeof(output));DisplayState.fov=96;
    assert(GetCampaignFov(nullptr,output)==output && *reinterpret_cast<float*>(output)==96 && *reinterpret_cast<uint32_t*>(output+8)==0xffff0003);
    for(unsigned i=12;i<16;++i)assert(output[i]==0xa5);
    assert(call(123)==EXCEPTION_CONTINUE_SEARCH);assert(VirtualFree(allocation,0,MEM_RELEASE));
    puts("PASS: native and extended frame rates, cinematic timing, presentation interval, signed register emulation and FOV value layout.");
}
