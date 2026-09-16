#pragma once
#include "RuntimeSupport.h"
#include <d3d12.h>

namespace h5runtime::deformation {
struct Slice { uint64_t buffer; uint32_t offset,length; };
static_assert(sizeof(Slice)==16);
constexpr uint32_t counterBytes=4;
inline bool validSize(uint64_t size){return size>0 && size<=16*1024*1024-counterBytes && size%32==0;}
inline D3D12_STREAM_OUTPUT_BUFFER_VIEW outputView(uint64_t gpu,uint64_t capacity,uint32_t offset,uint32_t size){
    require(gpu && validSize(size) && offset%4==0 && static_cast<uint64_t>(offset)+size+counterBytes<=capacity,"The facial deformation output exceeds its allocation.");
    return {gpu+offset,size,gpu+offset+size};
}
// The counter belongs to the same allocation as the vertices. The caller uses
// native resource tracking to order COPY_DEST -> STREAM_OUT -> vertex input.
inline void resetCounter(ID3D12GraphicsCommandList* commands,ID3D12Resource* output,uint64_t outputOffset,ID3D12Resource* upload,uint64_t uploadOffset){
    commands->CopyBufferRegion(output,outputOffset,upload,uploadOffset,counterBytes);
}
inline void bindOutput(ID3D12GraphicsCommandList* commands,const D3D12_STREAM_OUTPUT_BUFFER_VIEW& view){
    commands->SOSetTargets(0,1,&view);
    commands->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_POINTLIST);
}
}
