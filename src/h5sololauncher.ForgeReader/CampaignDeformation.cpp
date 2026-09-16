#include "DeformationOutput.h"
#include <intrin.h>

using namespace h5runtime;
namespace h5runtime::deformation {
static uint64_t image=0;
static volatile LONG armed=0,draws=0,errors=0;
static SRWLOCK failureLock=SRWLOCK_INIT;
static char failure[512]{};
static PVOID handler=nullptr;
static thread_local bool pipelineChecked=false;
static ID3D12Resource* resource(uint64_t buffer){return value<ID3D12Resource*>(value<uint64_t>(buffer+0x48)+0x18);}
static void flush(uint64_t render,ID3D12GraphicsCommandList* commands){
    auto queue=value<uint64_t>(render+0x20);auto count=value<UINT>(queue);
    require(count<=4096,"The facial deformation barrier queue is invalid.");
    if(count){commands->ResourceBarrier(count,reinterpret_cast<D3D12_RESOURCE_BARRIER*>(queue+8));*reinterpret_cast<UINT*>(queue)=0;}
}
static void submit(uint64_t render,uint64_t input,uint32_t size){
    require(render && validSize(size),"The facial deformation vertex count is invalid.");
    auto address=value<uint64_t>(input+0x20);auto slice=value<Slice>(address);
    // A draw already in progress when the hooks were installed can have the
    // original allocation. Leave that one frame alone; it has no counter room.
    if(slice.length==size)return;
    require(slice.buffer && slice.length==size+counterBytes,"The facial deformation slice is invalid.");
    auto view=outputView(value<uint64_t>(slice.buffer+0x20),value<uint64_t>(slice.buffer+0x78),slice.offset,size);
    require(value<uint32_t>(slice.buffer+0x64)==1,"The facial deformation output is not a native default buffer.");
    const auto desc=value<D3D12_GRAPHICS_PIPELINE_STATE_DESC>(render+0x1600);
    require(desc.VS.pShaderBytecode && desc.GS.pShaderBytecode && desc.StreamOutput.NumEntries==2 &&
        desc.StreamOutput.NumStrides==4 && desc.StreamOutput.RasterizedStream==D3D12_SO_NO_RASTERIZED_STREAM &&
        value<UINT>(reinterpret_cast<uint64_t>(desc.StreamOutput.pBufferStrides))==32,"The native facial deformation pipeline differs.");
    auto commands=value<ID3D12GraphicsCommandList*>(render+0x68);auto output=resource(slice.buffer);
    require(commands && output,"The facial deformation command list is unavailable.");
    if(!pipelineChecked){
        auto probe=desc;probe.PrimitiveTopologyType=D3D12_PRIMITIVE_TOPOLOGY_TYPE_POINT;
        // The descriptor still contains the preceding draw's root signature.
        // Resolve the signature from the selected shaders, as native PSO
        // creation does after processing its dirty shader flags.
        auto root=reinterpret_cast<uint64_t(*)(const D3D12_GRAPHICS_PIPELINE_STATE_DESC*)>(image+0x15e94b0)(&probe);
        require(root!=0,"The facial deformation root signature is unavailable.");
        probe.pRootSignature=value<ID3D12RootSignature*>(root+0x4300);
        ID3D12Device* device=nullptr;require(SUCCEEDED(commands->GetDevice(__uuidof(ID3D12Device),reinterpret_cast<void**>(&device))),"The facial deformation device is unavailable.");
        ID3D12PipelineState* state=nullptr;auto result=device->CreateGraphicsPipelineState(&probe,__uuidof(ID3D12PipelineState),reinterpret_cast<void**>(&state));device->Release();if(state)state->Release();
        if(FAILED(result)){char message[128];_snprintf_s(message,_TRUNCATE,"The native facial deformation pipeline failed validation (0x%08lx).",static_cast<unsigned long>(result));throw std::runtime_error(message);}
        pipelineChecked=true;
    }
    alignas(16) const uint32_t zero[4]{};Slice upload{};
    reinterpret_cast<Slice*(*)(Slice*,uint64_t,uint32_t,const void*)>(image+0x1586320)(&upload,render,sizeof(zero),zero);
    require(upload.buffer && upload.length>=sizeof(zero),"The facial deformation counter upload failed.");
    auto source=resource(upload.buffer);require(source!=nullptr,"The facial deformation upload resource is unavailable.");
    reinterpret_cast<void(*)(uint64_t,uint64_t,uint32_t)>(image+0x15845b0)(render,slice.buffer,D3D12_RESOURCE_STATE_COPY_DEST);
    flush(render,commands);
    resetCounter(commands,output,value<uint64_t>(slice.buffer+0x50)+slice.offset+size,source,value<uint64_t>(upload.buffer+0x50)+upload.offset);
    auto bind=reinterpret_cast<void(*)(uint64_t,uint32_t,const uint64_t*,const uint32_t*,const uint64_t*)>(image+0x1583d40);
    // Native binding registers allocation lifetime, transitions the resource and
    // updates the engine cache. Narrow its pool-wide view to this exact slice.
    bind(render,1,&slice.buffer,&slice.offset,&view.BufferFilledSizeLocation);
    bindOutput(commands,view);
    reinterpret_cast<void(*)(uint64_t,uint32_t)>(image+0x157dc40)(render+0x15e0,D3D12_PRIMITIVE_TOPOLOGY_TYPE_POINT);
    *reinterpret_cast<uint32_t*>(render+0x319a8)=D3D_PRIMITIVE_TOPOLOGY_POINTLIST;
    reinterpret_cast<void(*)(uint64_t,uint32_t,uint32_t,uint32_t,uint32_t)>(image+0x1582410)(render,size/32,1,0,0);
    bind(render,1,nullptr,nullptr,nullptr);
    // Only vertex bytes are exposed to the original consumer. Its native input
    // binding performs the final transition and keeps normal pool ownership.
    reinterpret_cast<Slice*>(address)->length=size;
    InterlockedIncrement(&draws);
}
static LONG CALLBACK trap(EXCEPTION_POINTERS* event){
    if(event->ExceptionRecord->ExceptionCode!=EXCEPTION_BREAKPOINT)return EXCEPTION_CONTINUE_SEARCH;
    auto at=reinterpret_cast<uint64_t>(event->ExceptionRecord->ExceptionAddress);auto context=event->ContextRecord;
    if(at==image+0x15f165c){
        auto size=static_cast<uint32_t>(context->Rdi);context->R8=validSize(size)?size+counterBytes:size;
        context->Rip=image+0x15f165f;return EXCEPTION_CONTINUE_EXECUTION;
    }
    if(at!=image+0x15f167e)return EXCEPTION_CONTINUE_SEARCH;
    if(!errors)try{submit(context->R14,context->Rsi,static_cast<uint32_t>(context->Rdi));}
        catch(const std::exception& error){AcquireSRWLockExclusive(&failureLock);strncpy_s(failure,error.what(),_TRUNCATE);ReleaseSRWLockExclusive(&failureLock);InterlockedIncrement(&errors);}
    // Execute the displaced CALL with the original register/flag context.
    context->Rsp-=8;*reinterpret_cast<uint64_t*>(context->Rsp)=image+0x15f1683;context->Rip=image+0x1529870;
    return EXCEPTION_CONTINUE_EXECUTION;
}
static void patch(uint64_t address,char original){
    auto point=reinterpret_cast<char*>(address);DWORD old=0,unused=0;
    require(VirtualProtect(point,1,PAGE_EXECUTE_READWRITE,&old)!=0,"Could not prepare facial deformation.");
    auto changed=_InterlockedCompareExchange8(point,static_cast<char>(0xcc),original)==original;
    auto flushed=FlushInstructionCache(GetCurrentProcess(),point,1);auto restored=VirtualProtect(point,1,old,&unused);
    require(changed && flushed && restored,"Could not install facial deformation. Restart Forge.");
}
static void arm(uint64_t base){
    require(!handler,"Facial deformation setup was already attempted. Restart Forge.");
    code(base,0x15f12e0,0x420,"EBD497F9F40D0B73786E347447B11DBC64F5D085CAA59AE3AF86C920A2E1C9AD");
    code(base,0x1586320,0x120,"91E1A65A3C6FAA40827230F3904FE958B21723EBAE70AE52A72ACD96D10A39F8");
    code(base,0x1586b00,0x80,"475F02DE8F66326E90C141B9FD108E465E2B87C1F328D72029E64ED8D5956C07");
    code(base,0x15845b0,0x240,"3E4F8A4F3F222C6384E42B8AEF721945B4733C7A58392263C1683671A4733894");
    code(base,0x1583d40,0x2c0,"4CF17A72B31B53EF4D657F65AF111384375A0F418FFCC30C32861EC14562B54A");
    code(base,0x1582410,0xac,"8CA0AD6F0EF6B622A2D86FED1B4F5CEB230695E3502997846157DD7124A8F53F");
    code(base,0x157dc40,0x30,"FA90EFD3BC558A91BF08DEFBAB7272456187E0893CEFF4E7718C6CAD0247266F");
    code(base,0x1529870,0x60,"016B3020F4A7B26137C43240D4301A2DDECBA828EA0CD1ED654AD7A31067A258");
    code(base,0x15e94b0,0x85,"0C39800FC2DFD0E8D92DEA8D3CAFD2DF590F64C6DFC7794560E14D3D2BD10D05");
    image=base;handler=AddVectoredExceptionHandler(1,trap);require(handler!=nullptr,"Could not install the facial deformation handler.");
    patch(base+0x15f165c,0x44);patch(base+0x15f167e,static_cast<char>(0xe8));InterlockedExchange(&armed,1);
}
}
struct DeformationRequest{uint32_t magic,version,operation,status;uint64_t creation;uint32_t phase,draws,errors,reserved;char message[512];};
static_assert(sizeof(DeformationRequest)==552);
static SRWLOCK operationLock=SRWLOCK_INIT;
extern "C" __declspec(dllexport) DWORD WINAPI H5Deformation(DeformationRequest* request){
    if(!request)return ERROR_INVALID_PARAMETER;AcquireSRWLockExclusive(&operationLock);request->status=0;request->message[0]=0;
    try{require(request->magic==0x46443548 && request->version==1 && request->operation<=1,"The facial deformation request is incompatible.");
        auto base=identity(request->creation);if(request->operation==1)deformation::arm(base);
        request->phase=deformation::armed?1:0;request->draws=deformation::draws;request->errors=deformation::errors;
        if(deformation::errors){request->phase=2;request->status=ERROR_INVALID_STATE;AcquireSRWLockShared(&deformation::failureLock);strncpy_s(request->message,deformation::failure,_TRUNCATE);ReleaseSRWLockShared(&deformation::failureLock);}
    }catch(const std::exception& error){request->status=ERROR_INVALID_STATE;strncpy_s(request->message,error.what(),_TRUNCATE);}
    ReleaseSRWLockExclusive(&operationLock);return request->status;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}

