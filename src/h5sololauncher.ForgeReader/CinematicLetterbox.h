#pragma once
#include "RuntimeSupport.h"
#include <d3d12.h>
#include <cmath>
#include <intrin.h>

namespace h5runtime::letterbox {
// Engine rectangle order: top, left, bottom, right (signed pixels).
struct NativeRect {int16_t top,left,bottom,right;};
inline bool rectangles(const NativeRect (&source)[2],LONG width,LONG height,D3D12_RECT (&out)[2]) {
    if(width<=0 || height<=0)return false;
    for(unsigned i=0;i<2;++i){auto& r=source[i];
        if(r.left<0 || r.top<0 || r.right>width || r.bottom>height || r.left>=r.right || r.top>r.bottom)return false;
        out[i]={r.left,r.top,r.right,r.bottom};
    }
    return source[0].left==source[1].left && source[0].right==source[1].right &&
        source[0].top==0 && source[1].bottom==height && source[0].bottom<=source[1].top;
}
inline uint64_t image=0;
inline volatile LONG fills=0,errors=0;
inline PVOID handler=nullptr;
inline void fill(uint64_t stack,uint64_t cinematic) {
    const auto amount=value<float>(cinematic+0x2c3b0);
    // Zero amount reaches this instruction without initializing the rectangles.
    if(!(amount>0))return;
    require(std::isfinite(amount) && amount<=1,"The cinematic letterbox amount is invalid.");
    auto render=reinterpret_cast<uint64_t(*)()>(image+0x15298d0)();
    require(render!=0,"The cinematic render context is unavailable.");
    // Forge's binding helper passes a four-slot array (including empty slots),
    // not a count of one active output. The visible color is output slot zero.
    const auto slots=value<uint32_t>(render+0x31aa4);
    require(slots==1 || slots==4,"The cinematic has an unexpected render target slot count.");
    float viewport[6];require(read(render+0x31980,viewport,sizeof(viewport)),"The cinematic viewport is unavailable.");
    require(viewport[0]==0 && viewport[1]==0 && std::isfinite(viewport[2]) && std::isfinite(viewport[3]) &&
        viewport[2]>0 && viewport[2]<=32767 && viewport[3]>0 && viewport[3]<=32767,"The cinematic viewport is unsupported.");
    NativeRect source[2];require(read(stack+0x20,source,sizeof(source)),"The cinematic rectangles are unavailable.");
    D3D12_RECT rects[2];require(rectangles(source,static_cast<LONG>(viewport[2]),static_cast<LONG>(viewport[3]),rects),"The cinematic rectangles are invalid.");
    auto target=value<uint64_t>(render+0x31aa8);require(target!=0,"The cinematic render target is unavailable.");
    D3D12_CPU_DESCRIPTOR_HANDLE descriptor{value<SIZE_T>(target+0x40)};
    auto commands=value<ID3D12GraphicsCommandList*>(render+0x68);
    require(commands && descriptor.ptr,"The cinematic command list or target descriptor is unavailable.");
    // Original draws have returned and their resource barriers are flushed.
    // Clear only the authored bars, preserving PSO, viewport and scissor state.
    // The native subtitle and fade passes still run afterwards.
    D3D12_RECT nonempty[2];UINT count=0;
    for(auto& rect:rects)if(rect.bottom>rect.top)nonempty[count++]=rect;
    if(count){const float black[]={0,0,0,1};commands->ClearRenderTargetView(descriptor,black,count,nonempty);InterlockedIncrement(&fills);}
}
inline LONG CALLBACK trap(EXCEPTION_POINTERS* event){
    if(event->ExceptionRecord->ExceptionCode!=EXCEPTION_BREAKPOINT ||
        reinterpret_cast<uint64_t>(event->ExceptionRecord->ExceptionAddress)!=image+0x72925a)return EXCEPTION_CONTINUE_SEARCH;
    auto context=event->ContextRecord;
    try{fill(context->Rsp,context->Rbx);}catch(...){InterlockedIncrement(&errors);}
    context->Rcx=context->Rbx;context->Rip=image+0x72925d; // mov rcx,rbx
    return EXCEPTION_CONTINUE_EXECUTION;
}
inline void guards(uint64_t base){
    code(base,0x729200,125,"1BFFC72C9FC548B83E55DEA95E25291E590F2897C7C1B6F06277D97055D77CC6");
    code(base,0x15298d0,96,"C004B295547CF7DE22E27F5DDF32934FB858E354544999B496E1067DFCCC0513");
    code(base,0x1584130,534,"40F2211BD9445DCF6AF3AAB3AD4691A833E900F42E463BA2B1E4AC2905DAB0E7");
}
inline void arm(uint64_t base){
    image=base;handler=AddVectoredExceptionHandler(1,trap);require(handler!=nullptr,"Could not install cinematic letterboxing.");
    auto point=reinterpret_cast<char*>(base+0x72925a);DWORD old=0,unused=0;
    if(!VirtualProtect(point,1,PAGE_EXECUTE_READWRITE,&old)){RemoveVectoredExceptionHandler(handler);handler=nullptr;throw std::runtime_error("Could not prepare cinematic letterboxing.");}
    const bool changed=_InterlockedCompareExchange8(point,static_cast<char>(0xcc),0x48)==0x48;
    const bool flushed=FlushInstructionCache(GetCurrentProcess(),point,1)!=0;
    const bool restored=VirtualProtect(point,1,old,&unused)!=0;
    require(changed && flushed && restored,"Cinematic letterboxing setup failed. Restart Forge.");
}
}
