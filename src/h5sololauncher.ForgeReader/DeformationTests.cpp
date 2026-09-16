#ifdef NDEBUG
#undef NDEBUG
#endif
#include "DeformationOutput.h"
#include <dxgi1_4.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <cassert>
#include <cstdio>
using Microsoft::WRL::ComPtr;
using namespace h5runtime::deformation;
static void check(HRESULT hr){assert(SUCCEEDED(hr));}
int main(){
    ComPtr<IDXGIFactory4> factory;check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
    ComPtr<IDXGIAdapter> adapter;check(factory->EnumWarpAdapter(IID_PPV_ARGS(&adapter)));
    ComPtr<ID3D12Device> device;check(D3D12CreateDevice(adapter.Get(),D3D_FEATURE_LEVEL_11_0,IID_PPV_ARGS(&device)));
    D3D12_COMMAND_QUEUE_DESC q{};ComPtr<ID3D12CommandQueue> queue;check(device->CreateCommandQueue(&q,IID_PPV_ARGS(&queue)));
    ComPtr<ID3D12CommandAllocator> allocator;check(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,IID_PPV_ARGS(&allocator)));
    ComPtr<ID3D12GraphicsCommandList> list;check(device->CreateCommandList(0,D3D12_COMMAND_LIST_TYPE_DIRECT,allocator.Get(),nullptr,IID_PPV_ARGS(&list)));
    D3D12_ROOT_PARAMETER parameter{};parameter.ParameterType=D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;parameter.Constants.Num32BitValues=1;parameter.ShaderVisibility=D3D12_SHADER_VISIBILITY_VERTEX;
    D3D12_ROOT_SIGNATURE_DESC rootDesc{};rootDesc.NumParameters=1;rootDesc.pParameters=&parameter;rootDesc.Flags=D3D12_ROOT_SIGNATURE_FLAG_ALLOW_STREAM_OUTPUT;
    ComPtr<ID3DBlob> serialized,error;check(D3D12SerializeRootSignature(&rootDesc,D3D_ROOT_SIGNATURE_VERSION_1,&serialized,&error));
    ComPtr<ID3D12RootSignature> root;check(device->CreateRootSignature(0,serialized->GetBufferPointer(),serialized->GetBufferSize(),IID_PPV_ARGS(&root)));
    const char* shader="cbuffer Weights:register(b0){float weight;} struct V{float4 position:POSITION;float4 normal:NORMAL;}; V main(uint id:SV_VertexID){V v;v.position=float4(id+weight,2*weight,3,1);v.normal=float4(0,0,1,0);return v;}";
    ComPtr<ID3DBlob> vs;check(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"main","vs_5_0",0,0,&vs,&error));
    D3D12_SO_DECLARATION_ENTRY entries[]={{0,"POSITION",0,0,4,0},{0,"NORMAL",0,0,4,0}};UINT stride=32;
    D3D12_GRAPHICS_PIPELINE_STATE_DESC psoDesc{};psoDesc.pRootSignature=root.Get();psoDesc.VS={vs->GetBufferPointer(),vs->GetBufferSize()};psoDesc.StreamOutput={entries,2,&stride,1,D3D12_SO_NO_RASTERIZED_STREAM};
    psoDesc.RasterizerState.FillMode=D3D12_FILL_MODE_SOLID;psoDesc.RasterizerState.CullMode=D3D12_CULL_MODE_NONE;psoDesc.SampleMask=UINT_MAX;psoDesc.SampleDesc.Count=1;psoDesc.PrimitiveTopologyType=D3D12_PRIMITIVE_TOPOLOGY_TYPE_POINT;
    ComPtr<ID3D12PipelineState> pso;check(device->CreateGraphicsPipelineState(&psoDesc,IID_PPV_ARGS(&pso)));
    auto buffer=[&](D3D12_HEAP_TYPE type,D3D12_RESOURCE_STATES state){
        D3D12_HEAP_PROPERTIES props{};props.Type=type;D3D12_RESOURCE_DESC desc{};desc.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER;desc.Width=4096;desc.Height=1;desc.DepthOrArraySize=1;desc.MipLevels=1;desc.SampleDesc.Count=1;desc.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ComPtr<ID3D12Resource> result;check(device->CreateCommittedResource(&props,D3D12_HEAP_FLAG_NONE,&desc,state,nullptr,IID_PPV_ARGS(&result)));return result;
    };
    auto output=buffer(D3D12_HEAP_TYPE_DEFAULT,D3D12_RESOURCE_STATE_COPY_DEST),upload=buffer(D3D12_HEAP_TYPE_UPLOAD,D3D12_RESOURCE_STATE_GENERIC_READ),readback=buffer(D3D12_HEAP_TYPE_READBACK,D3D12_RESOURCE_STATE_COPY_DEST);
    void* mapped;D3D12_RANGE none{0,0};check(upload->Map(0,&none,&mapped));memset(mapped,0xa5,4096);memset(static_cast<char*>(mapped)+2048,0,16);upload->Unmap(0,nullptr);
    list->CopyBufferRegion(output.Get(),0,upload.Get(),0,4096);
    auto transition=[&](D3D12_RESOURCE_STATES before,D3D12_RESOURCE_STATES after){D3D12_RESOURCE_BARRIER b{};b.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;b.Transition={output.Get(),D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,before,after};list->ResourceBarrier(1,&b);};
    // Multiple faces in one pool, nonzero offsets, and reuse of a dirty counter.
    // A missing reset causes the second draw to append or overflow; sentinel
    // checks catch an output view that can overwrite its neighbor/counter.
    for(unsigned pass=0;pass<3;++pass){
        UINT offset=pass==1?512:128;constexpr UINT count=5,size=count*32;
        resetCounter(list.Get(),output.Get(),offset+size,upload.Get(),2048);
        transition(D3D12_RESOURCE_STATE_COPY_DEST,D3D12_RESOURCE_STATE_STREAM_OUT);
        auto view=outputView(output->GetGPUVirtualAddress(),4096,offset,size);bindOutput(list.Get(),view);
        list->SetPipelineState(pso.Get());list->SetGraphicsRootSignature(root.Get());float weight=static_cast<float>(pass+1);UINT bits;memcpy(&bits,&weight,4);list->SetGraphicsRoot32BitConstant(0,bits,0);
        list->DrawInstanced(count,1,0,0);list->SOSetTargets(0,0,nullptr);
        transition(D3D12_RESOURCE_STATE_STREAM_OUT,D3D12_RESOURCE_STATE_COPY_DEST);
    }
    transition(D3D12_RESOURCE_STATE_COPY_DEST,D3D12_RESOURCE_STATE_COPY_SOURCE);list->CopyBufferRegion(readback.Get(),0,output.Get(),0,4096);check(list->Close());
    ID3D12CommandList* submit[]={list.Get()};queue->ExecuteCommandLists(1,submit);ComPtr<ID3D12Fence> fence;check(device->CreateFence(0,D3D12_FENCE_FLAG_NONE,IID_PPV_ARGS(&fence)));check(queue->Signal(fence.Get(),1));HANDLE done=CreateEventW(nullptr,FALSE,FALSE,nullptr);assert(done);check(fence->SetEventOnCompletion(1,done));assert(WaitForSingleObject(done,30000)==WAIT_OBJECT_0);CloseHandle(done);
    D3D12_RANGE range{0,4096};check(readback->Map(0,&range,&mapped));auto data=static_cast<unsigned char*>(mapped);
    for(UINT offset:{128u,512u}){float weight=offset==128?3.0f:2.0f;for(UINT i=0;i<5;++i){auto v=reinterpret_cast<float*>(data+offset+i*32);assert(v[0]==i+weight && v[1]==2*weight && v[2]==3 && v[3]==1 && v[4]==0 && v[5]==0 && v[6]==1 && v[7]==0);}assert(*reinterpret_cast<UINT*>(data+offset+160)==160);}
    for(UINT i=0;i<4096;++i)if(!(i>=128 && i<292) && !(i>=512 && i<676))assert(data[i]==(i>=2048 && i<2064?0:0xa5));
    readback->Unmap(0,&none);assert(!validSize(0) && !validSize(33) && !validSize(UINT_MAX));
    bool rejected=false;try{outputView(4096,160,0,160);}catch(const std::exception&){rejected=true;}assert(rejected);
    puts("PASS: GPU deformation output, animated weights, shared pool slices, counter reset/reuse and untouched neighboring bytes.");
}
