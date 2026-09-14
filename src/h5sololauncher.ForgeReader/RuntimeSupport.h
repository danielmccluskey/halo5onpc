#pragma once
#include <windows.h>
#include <appmodel.h>
#include <bcrypt.h>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#include <stdexcept>

namespace h5runtime {
inline void require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
inline std::string utf8(const std::wstring& text){auto length=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),nullptr,0,nullptr,nullptr);require(length>=0,"A diagnostic path cannot be encoded.");std::string output(length,0);if(length)require(WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),output.data(),length,nullptr,nullptr)==length,"A diagnostic path cannot be encoded.");return output;}
inline bool read(uint64_t address, void* bytes, size_t count) {
    SIZE_T copied = 0;
    return address >= 0x10000 && address < 0x0000800000000000ull && count <= 16 * 1024 * 1024 &&
        ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<void*>(address), bytes, count, &copied) && copied == count;
}
template<class T> T value(uint64_t address) { T result{}; require(read(address, &result, sizeof(result)), "A required native object is no longer readable."); return result; }
inline std::vector<unsigned char> bytes(uint64_t address, size_t count) {
    require(count <= 16 * 1024 * 1024, "Native data exceeds its supported limit.");
    std::vector<unsigned char> result(count); require(read(address, result.data(), count), "A required native buffer is no longer readable."); return result;
}
inline std::string sha(const void* data, size_t count) {
    require(count <= 0xffffffffu, "Hash input exceeds its limit.");
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
    require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0, "SHA-256 provider is unavailable.");
    unsigned char digest[32]; auto status = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0);
    if (status >= 0) status = BCryptHashData(hash, (PUCHAR)data, static_cast<ULONG>(count), 0);
    if (status >= 0) status = BCryptFinishHash(hash, digest, sizeof(digest), 0);
    if (hash) BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0);
    require(status >= 0, "SHA-256 calculation failed.");
    const char* hex = "0123456789ABCDEF"; std::string result;
    for (auto b : digest) { result += hex[b >> 4]; result += hex[b & 15]; } return result;
}
inline uint32_t nameId(const char* name) {
    const auto length = strlen(name); uint32_t h = 0;
    auto rotate = [](uint32_t n, int bits) { return (n << bits) | (n >> (32 - bits)); };
    for (size_t i = 0; i < length; i += 4) {
        uint32_t k = 0; const auto count = (length - i < 4) ? length - i : 4;
        memcpy(&k, name + i, count); k *= 0xcc9e2d51; k = rotate(k, 15) * 0x1b873593; h ^= k;
        if (count == 4) h = rotate(h, 13) * 5 + 0xe6546b64;
    }
    h ^= static_cast<uint32_t>(length); h ^= h >> 16; h *= 0x85ebca6b; h ^= h >> 13; h *= 0xc2b2ae35; h ^= h >> 16; return h;
}
inline uint64_t identity(uint64_t creation) {
    wchar_t package[256]; UINT32 length = 256;
    require(GetCurrentPackageFullName(&length, package) == ERROR_SUCCESS &&
        wcscmp(package, L"Microsoft.Halo5Forge_1.194.6192.2_x64__8wekyb3d8bbwe") == 0, "The native runtime does not support this Forge package.");
    FILETIME created, exited, kernel, user;
    require(GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user) && value<uint64_t>((uint64_t)&created) == creation, "The runtime request belongs to a different Forge session.");
    const auto base = reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr)); auto dos = value<IMAGE_DOS_HEADER>(base);
    require(dos.e_magic == IMAGE_DOS_SIGNATURE && dos.e_lfanew > 0 && dos.e_lfanew < 4096, "Forge has an invalid executable header.");
    auto nt = value<IMAGE_NT_HEADERS64>(base + dos.e_lfanew);
    require(nt.Signature == IMAGE_NT_SIGNATURE && nt.FileHeader.Machine == IMAGE_FILE_MACHINE_AMD64 && nt.FileHeader.TimeDateStamp == 1520325666, "Forge's executable identity is unsupported."); return base;
}
inline void code(uint64_t base, uint32_t rva, size_t count, const char* expected) {
    auto current = bytes(base + rva, count); require(sha(current.data(), current.size()) == expected, "A required native function differs from the supported Forge build.");
}
}
