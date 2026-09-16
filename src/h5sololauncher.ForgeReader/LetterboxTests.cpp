#ifdef NDEBUG
#undef NDEBUG
#endif
#pragma warning(disable:4505)
#include "CinematicLetterbox.h"
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <cassert>
#include <cstdio>
using Microsoft::WRL::ComPtr;
using namespace h5runtime::letterbox;
static void check(HRESULT result){assert(SUCCEEDED(result));}
int main(){
    ComPtr<IDXGIFactory4> factory;check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
    ComPtr<IDXGIAdapter> adapter;check(factory->EnumWarpAdapter(IID_PPV_ARGS(&adapter)));
    ComPtr<ID3D12Device> device;check(D3D12CreateDevice(adapter.Get(),D3D_FEATURE_LEVEL_11_0,IID_PPV_ARGS(&device)));
    D3D12_COMMAND_QUEUE_DESC queueDesc{};ComPtr<ID3D12CommandQueue> queue;check(device->CreateCommandQueue(&queueDesc,IID_PPV_ARGS(&queue)));
    ComPtr<ID3D12CommandAllocator> allocator;check(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,IID_PPV_ARGS(&allocator)));
    ComPtr<ID3D12GraphicsCommandList> list;check(device->CreateCommandList(0,D3D12_COMMAND_LIST_TYPE_DIRECT,allocator.Get(),nullptr,IID_PPV_ARGS(&list)));check(list->Close());
    D3D12_DESCRIPTOR_HEAP_DESC heapDesc{};heapDesc.Type=D3D12_DESCRIPTOR_HEAP_TYPE_RTV;heapDesc.NumDescriptors=1;
    ComPtr<ID3D12DescriptorHeap> heap;check(device->CreateDescriptorHeap(&heapDesc,IID_PPV_ARGS(&heap)));auto rtv=heap->GetCPUDescriptorHandleForHeapStart();
    ComPtr<ID3D12Fence> fence;check(device->CreateFence(0,D3D12_FENCE_FLAG_NONE,IID_PPV_ARGS(&fence)));
    HANDLE done=CreateEventW(nullptr,FALSE,FALSE,nullptr);assert(done);
    std::vector<unsigned char> render(0x31b00),cinematic(0x2c400),target(0x60),stack(0x40);
    auto memory=static_cast<unsigned char*>(VirtualAlloc(nullptr,0x152a000,MEM_RESERVE,PAGE_READWRITE));assert(memory);
    auto function=memory+0x15298d0;assert(VirtualAlloc(memory+0x1529000,4096,MEM_COMMIT,PAGE_READWRITE));
    // Test substitute for the native thread-local render-context getter.
    unsigned char getter[]={0x48,0xb8,0,0,0,0,0,0,0,0,0xc3};auto renderPointer=reinterpret_cast<uint64_t>(render.data());memcpy(getter+2,&renderPointer,8);memcpy(function,getter,sizeof(getter));
    DWORD old;assert(VirtualProtect(memory+0x1529000,4096,PAGE_EXECUTE_READ,&old));assert(FlushInstructionCache(GetCurrentProcess(),function,sizeof(getter)));image=reinterpret_cast<uint64_t>(memory);
    *reinterpret_cast<ID3D12GraphicsCommandList**>(render.data()+0x68)=list.Get();
    *reinterpret_cast<uint32_t*>(render.data()+0x31aa4)=1;*reinterpret_cast<void**>(render.data()+0x31aa8)=target.data();*reinterpret_cast<SIZE_T*>(target.data()+0x40)=rtv.ptr;
    uint64_t sequence=0;
    for(UINT slots:{1u,4u})for(auto dimensions:{std::pair<LONG,LONG>{1920,1080},{3440,1440}})for(LONG bar:{0,1,131}){
        *reinterpret_cast<uint32_t*>(render.data()+0x31aa4)=slots;
        auto [width,height]=dimensions;
        D3D12_RESOURCE_DESC desc{};desc.Dimension=D3D12_RESOURCE_DIMENSION_TEXTURE2D;desc.Width=width;desc.Height=height;desc.DepthOrArraySize=1;desc.MipLevels=1;desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.SampleDesc.Count=1;desc.Flags=D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        D3D12_HEAP_PROPERTIES props{};props.Type=D3D12_HEAP_TYPE_DEFAULT;
        ComPtr<ID3D12Resource> texture;check(device->CreateCommittedResource(&props,D3D12_HEAP_FLAG_NONE,&desc,D3D12_RESOURCE_STATE_RENDER_TARGET,nullptr,IID_PPV_ARGS(&texture)));device->CreateRenderTargetView(texture.Get(),nullptr,rtv);
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};UINT64 bytes;device->GetCopyableFootprints(&desc,0,1,0,&footprint,nullptr,nullptr,&bytes);
        D3D12_RESOURCE_DESC buffer{};buffer.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER;buffer.Width=bytes;buffer.Height=1;buffer.DepthOrArraySize=1;buffer.MipLevels=1;buffer.SampleDesc.Count=1;buffer.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR;props.Type=D3D12_HEAP_TYPE_READBACK;
        ComPtr<ID3D12Resource> readback;check(device->CreateCommittedResource(&props,D3D12_HEAP_FLAG_NONE,&buffer,D3D12_RESOURCE_STATE_COPY_DEST,nullptr,IID_PPV_ARGS(&readback)));
        check(allocator->Reset());check(list->Reset(allocator.Get(),nullptr));const float red[]={1,0,0,1};list->ClearRenderTargetView(rtv,red,0,nullptr);
        float viewport[]={0,0,static_cast<float>(width),static_cast<float>(height),0,1};memcpy(render.data()+0x31980,viewport,sizeof(viewport));
        NativeRect rectangles[]={{0,0,static_cast<int16_t>(bar),static_cast<int16_t>(width)},{static_cast<int16_t>(height-bar),0,static_cast<int16_t>(height),static_cast<int16_t>(width)}};memcpy(stack.data()+0x20,rectangles,sizeof(rectangles));
        *reinterpret_cast<float*>(cinematic.data()+0x2c3b0)=bar?1.0f:0.0f;
        // The live Glassed capture retains a scissor cropped to the cinematic
        // image. Letterbox clears must still reach the pixels outside it.
        D3D12_RECT scissor{0,bar,width,height-bar};list->RSSetScissorRects(1,&scissor);
        fill(reinterpret_cast<uint64_t>(stack.data()),reinterpret_cast<uint64_t>(cinematic.data()));
        D3D12_RESOURCE_BARRIER barrier{};barrier.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;barrier.Transition={texture.Get(),D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,D3D12_RESOURCE_STATE_RENDER_TARGET,D3D12_RESOURCE_STATE_COPY_SOURCE};list->ResourceBarrier(1,&barrier);
        D3D12_TEXTURE_COPY_LOCATION source{};source.pResource=texture.Get();source.Type=D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        D3D12_TEXTURE_COPY_LOCATION destination{};destination.pResource=readback.Get();destination.Type=D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;destination.PlacedFootprint=footprint;
        list->CopyTextureRegion(&destination,0,0,0,&source,nullptr);check(list->Close());ID3D12CommandList* submit[]={list.Get()};queue->ExecuteCommandLists(1,submit);check(queue->Signal(fence.Get(),++sequence));check(fence->SetEventOnCompletion(sequence,done));assert(WaitForSingleObject(done,30000)==WAIT_OBJECT_0);
        unsigned char* pixels;D3D12_RANGE range{0,static_cast<SIZE_T>(bytes)};check(readback->Map(0,&range,reinterpret_cast<void**>(&pixels)));
        for(LONG y=0;y<height;++y)for(LONG x=0;x<width;++x){auto pixel=pixels+footprint.Offset+y*footprint.Footprint.RowPitch+x*4;bool black=y<bar || y>=height-bar;assert(pixel[0]==(black?0:255) && pixel[1]==0 && pixel[2]==0 && pixel[3]==255);}
        D3D12_RANGE none{0,0};readback->Unmap(0,&none);
    }
    assert(fills==8 && errors==0);
    NativeRect invalid[]={{0,0,600,1920},{500,0,1080,1920}};D3D12_RECT output[2];assert(!rectangles(invalid,1920,1080,output));
    invalid[0].bottom=131;invalid[1].top=949;assert(rectangles(invalid,1920,1080,output));invalid[0].left=-1;assert(!rectangles(invalid,1920,1080,output));
    *reinterpret_cast<float*>(cinematic.data()+0x2c3b0)=0;
    EXCEPTION_RECORD record{};CONTEXT context{};EXCEPTION_POINTERS event{&record,&context};record.ExceptionCode=EXCEPTION_BREAKPOINT;record.ExceptionAddress=reinterpret_cast<void*>(image+0x72925a);context.Rbx=reinterpret_cast<uint64_t>(cinematic.data());context.Rsp=1;
    assert(trap(&event)==EXCEPTION_CONTINUE_EXECUTION);assert(context.Rcx==context.Rbx && context.Rip==image+0x72925d && errors==0);
    record.ExceptionAddress=nullptr;assert(trap(&event)==EXCEPTION_CONTINUE_SEARCH);
    CloseHandle(done);assert(VirtualFree(memory,0,MEM_RELEASE));puts("PASS: D3D12 letterbox pixels at 1080p and ultrawide, animated edges, unchanged center/gameplay, rectangle bounds and native register continuation.");
}
