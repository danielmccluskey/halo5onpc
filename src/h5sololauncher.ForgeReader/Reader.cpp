#include <windows.h>
#include <appmodel.h>
#include <cstdint>
#include <cstddef>
#include <cwchar>
#include <string>

// Fixed, versioned ABI. No remote pointers; the worker owns this entire buffer.
struct Request {
    uint32_t magic, version, operation, status, requested, returned;
    uint64_t offset, length, modified;
    wchar_t path[32768];
    unsigned char bytes[1024 * 1024];
};
static_assert(offsetof(Request, path) == 48 && offsetof(Request, bytes) == 65584);
static_assert(sizeof(Request) == 1114160);

static bool globalPath(const std::wstring& path) {
    const std::wstring any = L"deploy/any/levels/globals", pc = L"deploy/pc/levels/globals";
    const auto prefix = path.find(any) == 0 ? any.size() : path.find(pc) == 0 ? pc.size() : 0u;
    if (!prefix || path.size() < prefix + 7 || path.substr(path.size() - 7) != L".module") return false;
    const auto middle = path.substr(prefix, path.size() - prefix - 7);
    if (middle.empty()) return true;
    bool needsLetter = true;
    if (middle[0] != L'-') return false;
    for (size_t i = 1; i < middle.size(); ++i) {
        const wchar_t c = middle[i];
        if (c == L'-') { if (needsLetter) return false; needsLetter = true; }
        else if ((c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || (c >= L'0' && c <= L'9')) needsLetter = false;
        else return false;
    }
    return !needsLetter;
}
static bool probePath(const std::wstring& path) {
    if (path.size() < 3 || path[1] != L':' || path[2] != L'\\' || path.find(L"..") != std::wstring::npos) return false;
    const std::wstring marker = L"\\forge\\probe-";
    const auto at = path.rfind(marker);
    if (at == std::wstring::npos || path.size() != at + marker.size() + 32 + 4 || path.substr(path.size() - 4) != L".bin") return false;
    for (size_t i = at + marker.size(); i < at + marker.size() + 32; ++i)
        if (!((path[i] >= L'0' && path[i] <= L'9') || (path[i] >= L'a' && path[i] <= L'f'))) return false;
    return true;
}
static bool audioPath(const std::wstring& path) {
    if (path == L"sound/win/SFX/soundbank.pck" || path == L"sound/win/SFX/soundstream.pck") return true;
    const std::wstring prefix = L"sound/win/", suffix = L"/soundvoice.pck";
    if (path.find(prefix) != 0 || path.size() <= prefix.size() + suffix.size() || path.substr(path.size() - suffix.size()) != suffix) return false;
    const auto language = path.substr(prefix.size(), path.size() - prefix.size() - suffix.size());
    if (language.size() > 40) return false;
    for (wchar_t c : language) if (!((c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z') || c == L'(' || c == L')' || c == L'-')) return false;
    return true;
}
static uint32_t word(const unsigned char* b, int at) { uint32_t value; memcpy(&value, b + at, 4); return value; }
static DWORD readRequest(Request& r) {
    if (r.magic != 0x48354652 || r.version != 1 || r.operation < 1 || r.operation > 4 ||
        !r.requested || r.requested > sizeof(r.bytes) || wcsnlen_s(r.path, 32768) == 32768) return ERROR_INVALID_PARAMETER;
    wchar_t family[256]; UINT32 familyLength = 256;
    if (GetCurrentPackageFamilyName(&familyLength, family) != ERROR_SUCCESS || wcscmp(family, L"Microsoft.Halo5Forge_8wekyb3d8bbwe") != 0) return ERROR_ACCESS_DENIED;
    wchar_t image[32768]; const auto imageLength = GetModuleFileNameW(nullptr, image, 32768);
    if (!imageLength || imageLength >= 32768) return ERROR_BAD_PATHNAME;
    auto name = wcsrchr(image, L'\\');
    if (!name || _wcsicmp(name + 1, L"halo5forge.exe") != 0) return ERROR_ACCESS_DENIED;
    std::wstring path(r.path);
    if (r.operation == 1 || r.operation == 3 || r.operation == 4) {
        if (!(r.operation == 4 ? audioPath(path) : globalPath(path))) return ERROR_BAD_PATHNAME;
        *name = 0; path = std::wstring(image) + L"\\" + path;
    } else if (!probePath(path) || r.offset != 0 || r.requested != 32) return ERROR_BAD_PATHNAME;
    const auto file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError();
    DWORD error = ERROR_SUCCESS;
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(file, &info)) error = GetLastError();
    else if (info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) error = ERROR_ACCESS_DENIED;
    else {
        r.length = (uint64_t(info.nFileSizeHigh) << 32) | info.nFileSizeLow;
        r.modified = (uint64_t(info.ftLastWriteTime.dwHighDateTime) << 32) | info.ftLastWriteTime.dwLowDateTime;
        uint64_t limit = r.length;
        if (r.operation == 1 || r.operation == 3) {
            unsigned char header[56]; DWORD count = 0;
            if (!ReadFile(file, header, 56, &count, nullptr)) error = GetLastError();
            else if (count != 56 || memcmp(header, "mohd", 4) || word(header, 4) != 27) error = ERROR_INVALID_DATA;
            else {
                limit = 56ull + word(header, 16) * 88ull + word(header, 28) + word(header, 32) * 4ull + word(header, 36) * 32ull;
                if (limit > r.length || limit > 128ull * 1024 * 1024) error = ERROR_INVALID_DATA;
                if (r.operation == 3) limit = r.length; // Explicit native payload operation, still restricted to installed globals.
            }
        } else if (r.operation == 4) {
            unsigned char header[28]; DWORD count = 0;
            if (!ReadFile(file, header, 28, &count, nullptr)) error = GetLastError();
            else if (count != 28 || memcmp(header, "AKPK", 4) || word(header, 8) != 1) error = ERROR_INVALID_DATA;
            else {
                limit = word(header, 4) + 8ull;
                if (limit != 28ull + word(header, 12) + word(header, 16) + word(header, 20) + word(header, 24) || limit > r.length || limit > 128ull * 1024 * 1024) error = ERROR_INVALID_DATA;
            }
        } else if (r.length != 32) error = ERROR_INVALID_DATA;
        if (!error && (r.offset > limit || r.requested > limit - r.offset)) error = ERROR_INVALID_PARAMETER;
        if (!error) {
            LARGE_INTEGER position{}; position.QuadPart = static_cast<LONGLONG>(r.offset);
            if (!SetFilePointerEx(file, position, nullptr, FILE_BEGIN)) error = GetLastError();
            else if (!ReadFile(file, r.bytes, r.requested, reinterpret_cast<DWORD*>(&r.returned), nullptr)) error = GetLastError();
            else if (r.returned != r.requested) error = ERROR_HANDLE_EOF;
        }
    }
    CloseHandle(file); return error;
}
extern "C" __declspec(dllexport) DWORD WINAPI H5Read(Request* request) {
    if (!request) return ERROR_INVALID_PARAMETER;
    request->returned = 0;
    try { request->status = readRequest(*request); }
    catch (...) { request->status = ERROR_UNHANDLED_EXCEPTION; }
    return request->status;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
