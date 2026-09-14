#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstring>
#pragma warning(push, 0)
#include "third_party/dxilhash/DxilHash.cpp"
#pragma warning(pop)

using Microsoft::WRL::ComPtr;
struct Binding { uint32_t type, reg, count, flags, ret, dimension, samples, constantBytes; };
struct Validation { uint32_t version, bindings, constantBuffers, featureLevel; };
static bool container(const uint8_t* data, uint32_t size) noexcept {
    if (!data || size < 32 || size > 16 * 1024 * 1024 || memcmp(data, "DXBC", 4)) return false;
    uint32_t length = 0; memcpy(&length, data + 24, 4); return length == size;
}
extern "C" __declspec(dllexport) HRESULT __stdcall H5ShaderHash(const uint8_t* data, uint32_t size, uint8_t* hash) noexcept {
    if (!container(data, size) || !hash) return E_INVALIDARG;
    ComputeHashRetail(data + 20, size - 20, hash); return S_OK;
}
extern "C" __declspec(dllexport) HRESULT __stdcall H5ShaderOpen(ID3D11Device** device) noexcept {
    if(!device)return E_INVALIDARG;*device=nullptr;
    return D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,device,nullptr,nullptr);
}
extern "C" __declspec(dllexport) void __stdcall H5ShaderClose(ID3D11Device* device) noexcept {if(device)device->Release();}
extern "C" __declspec(dllexport) HRESULT __stdcall H5ShaderValidateSession(ID3D11Device* device,const uint8_t* data, uint32_t size,
    uint32_t expectedVersion, const Binding* expected, uint32_t count, Validation* result) noexcept {
    if (!device || !container(data, size) || count > 4096 || (count && !expected) || !result) return E_INVALIDARG;
    *result = {};
    uint8_t hash[16] = {}; ComputeHashRetail(data + 20, size - 20, hash);
    if (memcmp(hash, data + 4, 16)) return HRESULT_FROM_WIN32(ERROR_CRC);
    ComPtr<ID3D11ShaderReflection> reflection;
    auto hr = D3DReflect(data, size, IID_PPV_ARGS(&reflection)); if (FAILED(hr)) return hr;
    D3D11_SHADER_DESC desc = {}; hr = reflection->GetDesc(&desc); if (FAILED(hr)) return hr;
    if (desc.Version != expectedVersion || desc.BoundResources != count) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    uint32_t cbs = 0;
    for (uint32_t i = 0; i < count; ++i) {
        D3D11_SHADER_INPUT_BIND_DESC found = {}; hr = reflection->GetResourceBindingDesc(i, &found); if (FAILED(hr)) return hr;
        const auto& x = expected[i];
        if (static_cast<uint32_t>(found.Type) != x.type || found.BindPoint != x.reg || found.BindCount != x.count || found.uFlags != x.flags ||
            static_cast<uint32_t>(found.ReturnType) != x.ret || static_cast<uint32_t>(found.Dimension) != x.dimension || found.NumSamples != x.samples)
            return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        if (x.type == 0) {
            ++cbs; D3D11_SHADER_BUFFER_DESC buffer = {};
            auto cb = reflection->GetConstantBufferByName(found.Name);
            if (!cb || FAILED(cb->GetDesc(&buffer)) || buffer.Size != x.constantBytes) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        }
    }
    if (desc.ConstantBuffers != cbs) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    ComPtr<ID3DBlob> disassembly; hr = D3DDisassemble(data, size, 0, nullptr, &disassembly); if (FAILED(hr)) return hr;
    auto level=device->GetFeatureLevel();
    // Creation checks the translated bytecode against the user's driver. No shader is dispatched.
    switch (expectedVersion >> 16) {
    case 0: { ComPtr<ID3D11PixelShader> shader; hr = device->CreatePixelShader(data, size, nullptr, &shader); break; }
    case 1: { ComPtr<ID3D11VertexShader> shader; hr = device->CreateVertexShader(data, size, nullptr, &shader); break; }
    case 2: { ComPtr<ID3D11GeometryShader> shader; hr = device->CreateGeometryShader(data, size, nullptr, &shader); break; }
    case 3: { ComPtr<ID3D11HullShader> shader; hr = device->CreateHullShader(data, size, nullptr, &shader); break; }
    case 4: { ComPtr<ID3D11DomainShader> shader; hr = device->CreateDomainShader(data, size, nullptr, &shader); break; }
    case 5: { ComPtr<ID3D11ComputeShader> shader; hr = device->CreateComputeShader(data, size, nullptr, &shader); break; }
    default: return E_INVALIDARG;
    }
    if (FAILED(hr)) return hr;
    *result = { desc.Version, desc.BoundResources, desc.ConstantBuffers, static_cast<uint32_t>(level) }; return S_OK;
}
