#include "RuntimeSupport.h"
#include "MenuState.h"
#include "UpdateJob.h"
#include "RegistryGuards.h"
#include "RegistryShared.h"
#include <array>

using namespace h5runtime;
struct MapInput { wchar_t path[2048]; char sha256[65]; unsigned char padding[3]; };
struct MapOutput { uint32_t id, mission, sublevel, bytes; unsigned char modules[4096]; };
struct Request {
    uint32_t magic, version, operation, status;
    uint64_t creation;
    uint32_t phase, maps, missions, resolved;
    char message[512];
    MapInput inputs[32];
    MapOutput outputs[32];
};
static_assert(sizeof(MapInput) == 4164 && sizeof(MapOutput) == 4112 && offsetof(Request, inputs) == 552);
static SRWLOCK requestLock = SRWLOCK_INIT;
static uint64_t image = 0, creation = 0;
static Menu observed{};
static std::array<uint64_t,3> original{}, candidate{};
static std::array<MapOutput,32> outputs{};
static uint32_t inputCount = 0, missionCount = 0, resolvedCount = 0;
static unsigned char oldReady = 0;
static bool active = false, failed = false;
static char failure[512]{};
static std::array<std::wstring,32> paths;
static std::array<std::string,32> digests;
static std::array<HANDLE,32> lockedInputs{};
static constexpr uint32_t globals[] = {0x5eb0c88,0x5eb0c90,0x5eb0c98};
void h5runtime::requireOwnedRegistry(uint64_t expectedCreation) {
    require(creation == expectedCreation && updateJob::phase == 3 && !updateJob::fault && !failed && active, "Complete campaign metadata registration in this Forge session first.");
    for (unsigned i = 0; i < 3; ++i) require(value<uint64_t>(image + globals[i]) == candidate[i], "The owned campaign registry was replaced by another component.");
}
template<class T> static T at(uint32_t rva) { return reinterpret_cast<T>(image + rva); }

// The native file-reference API accepts a narrow, 256-byte path. Keep that
// engine identity short; the actual cache location stays Unicode end to end.
static decltype(&CreateFile2) originalOpen = nullptr;
static volatile LONG redirected = 0;
static DWORD importThread = 0;
static unsigned currentInput = 0;
static HANDLE WINAPI openMetadata(LPCWSTR name, DWORD access, DWORD share, DWORD disposition, LPCREATEFILE2_EXTENDED_PARAMETERS parameters) {
    if (redirected && GetCurrentThreadId() == importThread && name && disposition == OPEN_EXISTING && !(access & (GENERIC_WRITE | DELETE | FILE_WRITE_DATA | FILE_APPEND_DATA))) {
        const wchar_t* leaf = wcsrchr(name, L'\\'); leaf = leaf ? leaf + 1 : name;
        if (_wcsicmp(leaf, L"h5solo-metadata.mapinfo") == 0) return originalOpen(paths[currentInput].c_str(), access, share, disposition, parameters);
    }
    return originalOpen(name, access, share, disposition, parameters);
}
static bool swapOpen(void* from, void* to) {
    auto slot = at<void* volatile*>(0x3284aa8); DWORD prior = 0, ignored = 0;
    if (!VirtualProtect(const_cast<void**>(slot), sizeof(void*), PAGE_READWRITE, &prior)) return false;
    bool success = InterlockedCompareExchangePointer(slot, to, from) == from;
    if (!VirtualProtect(const_cast<void**>(slot), sizeof(void*), prior, &ignored)) success = false;
    return success;
}
static void switchArrays(bool useCandidate) {
    at<void(__fastcall*)(void*)>(0x683a00)(at<void*>(0x5eb0bc0));
    for (unsigned i = 0; i < 3; ++i) InterlockedExchangePointer(at<void* volatile*>(globals[i]), reinterpret_cast<void*>(useCandidate ? candidate[i] : original[i]));
    *at<unsigned char*>(0x5eb257e) = oldReady;
    at<void(__fastcall*)(void*)>(0x683a20)(at<void*>(0x5eb0bc0)); active = useCandidate;
}
static void importMaps() {
    const char* names[] = {"campaign missions", "campaign levels", "campaign insertions"};
    const unsigned capacities[] = {15,64,19}, sizes[] = {0x234,0x13c0,0x1514};
    auto now = inspectMenu(image);
    require(now.receiver == observed.receiver && now.node == observed.node && now.definition == observed.definition, "Forge's menu changed before campaign registration.");
    for (unsigned i = 0; i < 3; ++i) {
        require(value<uint64_t>(image + globals[i]) == original[i], "A native registry changed before import.");
        auto allocator = value<void*>(original[i] + 0x40);
        auto array = at<void*(__fastcall*)(const char*,unsigned,unsigned,uint64_t,unsigned,void*,unsigned char)>(0x655440)(names[i],0x10,capacities[i],sizes[i],0,allocator,2);
        require(array != nullptr, "Forge could not allocate a campaign registry."); candidate[i] = reinterpret_cast<uint64_t>(array);
        at<void(__fastcall*)(void*)>(0x655430)(array);
    }
    switchArrays(true); importThread = GetCurrentThreadId();
    require(swapOpen(reinterpret_cast<void*>(originalOpen), reinterpret_cast<void*>(openMetadata)), "Forge's file reader changed before metadata import.");
    InterlockedExchange(&redirected, 1);
    for (unsigned i = 0; i < inputCount; ++i) {
        currentInput = i; alignas(16) unsigned char reference[0x120]{}, info[16]{};
        at<void*(__fastcall*)(void*,const char*,bool)>(0x668120)(reference,"h5solo-metadata.mapinfo",true);
        at<void*(__fastcall*)(void*)>(0x94d8d0)(info);
        auto parsed = at<bool(__fastcall*)(void*,void*)>(0x94e4c0)(info,reference);
        uint64_t row = 0;
        if (parsed) {
            uint32_t flags = 0;
            if (*at<uint32_t*(__fastcall*)(void*,uint32_t*)>(0x94e1e0)(info,&flags) & 0x20)
                row = reinterpret_cast<uint64_t>(at<unsigned char*(__fastcall*)(void*,bool,bool)>(0xad6200)(info,false,false));
        }
        at<void(__fastcall*)(void*)>(0x94ddb0)(info);
        require(row != 0, "Forge could not parse a source campaign mapinfo file.");
        auto& output = outputs[i]; output.id = value<uint32_t>(row + 4); output.bytes = value<uint32_t>(row + 0x148);
        require(output.bytes > 0 && output.bytes <= sizeof(output.modules), "The source map's module list exceeds its limit.");
        require(read(row + 0x150, output.modules, output.bytes), "The imported module list became unreadable.");
        output.mission = output.sublevel = 0xffffffff;
    }
    InterlockedExchange(&redirected, 0);
    require(swapOpen(reinterpret_cast<void*>(openMetadata), reinterpret_cast<void*>(originalOpen)), "The metadata file reader could not be restored.");
    *at<unsigned char*>(0x5eb257e) = 0; at<void(__fastcall*)()>(0xad56b0)();
    missionCount = value<uint32_t>(candidate[0] + 0x50); auto data = value<uint64_t>(candidate[0] + 0x58);
    require(missionCount == 15 && value<uint32_t>(candidate[1] + 0x50) == inputCount, "The imported campaign registry is incomplete.");
    for (unsigned i = 0; i < missionCount; ++i) {
        auto row = data + i * 0x234;
        require(value<uint32_t>(row + 4) == i, "The campaign mission order is inconsistent.");
        if (value<unsigned char>(row + 0x14)) ++resolvedCount;
        for (unsigned j = 0; j < 3; ++j) {
            auto id = value<uint32_t>(row + 8 + j * 4); if (id == 0xfffffffd) continue;
            unsigned matches = 0;
            for (unsigned k = 0; k < inputCount; ++k) if (outputs[k].id == id) {
                require(outputs[k].mission == 0xffffffff, "A campaign map belongs to several mission slots.");
                outputs[k].mission = i; outputs[k].sublevel = j; ++matches;
            }
            require(matches == 1, "A campaign mission refers to missing or duplicate map metadata.");
        }
    }
    require(resolvedCount == 15, "Forge did not resolve every imported mission.");
    for (unsigned i = 0; i < inputCount; ++i) require(outputs[i].mission != 0xffffffff, "An imported map has no campaign mission.");
}
static void importCaught() {
    try { importMaps(); }
    catch (const std::exception& error) { failed = true; strncpy_s(failure, error.what(), _TRUNCATE); }
    catch (...) { failed = true; strcpy_s(failure, "An unexpected error interrupted campaign registration."); }
}
static void importChecked() {
    __try { importCaught(); }
    __except(EXCEPTION_EXECUTE_HANDLER) { failed = true; strcpy_s(failure,"A native fault interrupted campaign registration. Restart Forge before retrying."); }
    if (failed) {
        if (redirected) { InterlockedExchange(&redirected,0); swapOpen(reinterpret_cast<void*>(openMetadata),reinterpret_cast<void*>(originalOpen)); }
        if (active) switchArrays(false);
    }
    for (auto file : lockedInputs) if (file && file != INVALID_HANDLE_VALUE) CloseHandle(file);
    lockedInputs.fill(nullptr);
}
static void run(Request& request) {
    require(request.magic == 0x35524748 && request.version == 1 && request.operation <= 1, "The campaign registry request is incompatible.");
    auto base = identity(request.creation);
    if (!creation && request.operation == 0) {
        auto current = inspectMenu(base);
        request.resolved = current.receiver && current.screen && (current.name == nameId("title_screen") || current.name == nameId("main_menu")) && value<unsigned char>(base + 0x5eb257e) == 1 ? 15 : 0;
    }
    if (creation) {
        require(creation == request.creation && request.maps == inputCount, "The campaign catalogue belongs to another startup request.");
        for (unsigned i = 0; i < inputCount; ++i)
            require(strnlen_s(request.inputs[i].sha256,65) == 64 && digests[i] == request.inputs[i].sha256 &&
                wcsnlen_s(request.inputs[i].path,2048) < 2048 && paths[i] == request.inputs[i].path, "Forge already imported metadata from a different cache. Restart it to switch folders.");
    }
    if (request.operation == 1) {
        require(!creation && updateJob::phase == 0 && request.maps == 19, "Campaign metadata has already been attempted or its file count is unsupported.");
        image = base; observed = inspectMenu(image);
        require(observed.receiver && observed.screen && (observed.name == nameId("title_screen") || observed.name == nameId("main_menu")), "Wait for Forge's title or main menu before preparing missions.");
        registryGuards(image);
        oldReady = value<unsigned char>(image + 0x5eb257e); require(oldReady == 1, "Forge's native campaign registry is not ready.");
        const unsigned sizes[] = {0x234,0x13c0,0x1514};
        for (unsigned i = 0; i < 3; ++i) {
            original[i] = value<uint64_t>(image + globals[i]);
            require(value<uint64_t>(original[i] + 0x20) == sizes[i] && value<uint32_t>(original[i] + 0x50) == (i ? 0u : 15u), "Forge already contains a different campaign registry. Restart it before preparing this cache.");
        }
        inputCount = request.maps;
        for (unsigned i = 0; i < inputCount; ++i) {
            auto& input = request.inputs[i]; require(wcsnlen_s(input.path,2048) < 2048 && strnlen_s(input.sha256,65) == 64, "A metadata input has an invalid path or digest.");
            paths[i] = input.path; require(paths[i].size() > 4 && paths[i][1] == L':' && paths[i][2] == L'\\' && paths[i].find(L":",2) == std::wstring::npos, "Metadata must be in a local cache folder.");
            digests[i] = input.sha256;
            auto file = CreateFileW(input.path,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
            require(file != INVALID_HANDLE_VALUE, "Forge cannot read a prepared metadata file.");
            BY_HANDLE_FILE_INFORMATION information{}; LARGE_INTEGER size{};
            bool ok = GetFileInformationByHandle(file,&information) && !(information.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) && GetFileSizeEx(file,&size) && size.QuadPart > 0 && size.QuadPart <= 1024*1024;
            std::vector<unsigned char> content(ok ? static_cast<size_t>(size.QuadPart) : 0); DWORD count = 0;
            if (ok) ok = ReadFile(file,content.data(),static_cast<DWORD>(content.size()),&count,nullptr) && count == content.size();
            lockedInputs[i] = file;
            require(ok && sha(content.data(),content.size()) == input.sha256, "A prepared metadata file changed or cannot be read by Forge.");
        }
        originalOpen = reinterpret_cast<decltype(originalOpen)>(GetProcAddress(GetModuleHandleW(L"kernelbase.dll"),"CreateFile2"));
        require(originalOpen && value<uint64_t>(image + 0x3284aa8) == reinterpret_cast<uint64_t>(originalOpen), "Forge's file reader is already modified.");
        creation = request.creation; updateJob::arm(image, importChecked);
    }
    request.phase = static_cast<uint32_t>(InterlockedCompareExchange(&updateJob::phase,0,0));
    if (request.phase >= 3) {
        require(!failed && !updateJob::fault, failed ? failure : "The campaign update operation failed or timed out. Restart Forge before retrying.");
        request.maps = inputCount; request.missions = missionCount; request.resolved = resolvedCount;
        memcpy(request.outputs,outputs.data(),sizeof(request.outputs));
    }
}
extern "C" __declspec(dllexport) DWORD WINAPI H5Registry(Request* request) {
    if (!request) return ERROR_INVALID_PARAMETER;
    AcquireSRWLockExclusive(&requestLock); request->status = 0; request->message[0] = 0;
    try { run(*request); }
    catch (const std::exception& error) { request->status = ERROR_INVALID_STATE; strncpy_s(request->message,error.what(),_TRUNCATE); }
    catch (...) { request->status = ERROR_UNHANDLED_EXCEPTION; }
    if (request->status && updateJob::phase == 0) {
        for (auto file : lockedInputs) if (file && file != INVALID_HANDLE_VALUE) CloseHandle(file);
        lockedInputs.fill(nullptr);
    }
    ReleaseSRWLockExclusive(&requestLock); return request->status;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) { if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance); return TRUE; }
