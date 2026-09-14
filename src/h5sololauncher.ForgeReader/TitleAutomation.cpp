#include "RuntimeSupport.h"
#include "MenuState.h"

using namespace h5runtime;
// The worker supplies no native pointers. The helper discovers and rechecks all
// objects in this exact process; queued events copy their arguments immediately.
struct Request {
    uint32_t magic, version, operation, status;
    uint64_t creation;
    uint32_t phase, node, xmlBytes, reserved;
    char message[512];
    unsigned char xml[1024 * 1024];
};
static_assert(offsetof(Request, xml) == 552);
static SRWLOCK operationLock = SRWLOCK_INIT;

struct Session {
    uint64_t creation = 0, base = 0, root = 0, originalXml = 0, replacement = 0, began = 0;
    uint32_t originalBytes = 0, replacementBytes = 0, phase = 0;
    uint64_t stableSince = 0;
    Menu menu{};
    bool failed = false;
};
static Session session;

static uint64_t titleRoot(uint64_t base) {
    auto pool = value<uint64_t>(base + 0x5f20768); require(pool && value<uint64_t>(pool + 0x20) == 88, "The native asset pool is unavailable.");
    auto count = value<uint32_t>(pool + 0x4c); auto storage = value<uint64_t>(pool + 0x58);
    require(count && count <= 0x15400, "The native asset pool has an unsupported size."); auto data = bytes(storage, count * 88ull); uint64_t found = 0;
    for (uint32_t i = 0; i < count; ++i) {
        auto p = data.data() + i * 88; uint32_t gid, handle; uint64_t root;
        memcpy(&handle, p, 4); memcpy(&gid, p + 76, 4); memcpy(&root, p + 40, 8);
        if (gid != 0x277a6 || !root || handle >> 15 != i) continue;
        require(!found && value<uint32_t>(root + 8) == gid && value<uint32_t>(root + 12) == handle && value<uint64_t>(root) == base + 0x36a5548, "The title asset identity changed."); found = root;
    }
    require(found != 0, "The title asset has not loaded yet."); return found;
}
static void queueGuards(uint64_t base) {
    code(base, 0x1e064f0, 200, "637EA8F585C7DFE5DCCF414C4171BA2E5F60F83AC479A2F77DE01E9E3C3C3DEF");
    code(base, 0x1e06420, 194, "0FAF1ACA1D1F8C3BC44DFAD4A0D19E70CDEAB19EBE3EDC04BBFD27734D2BC3AA");
    code(base, 0x1e05680, 163, "96E64EC4207920DE2021D39E02891158CC1D802D55A3EF1FE838EA4CE7DF70FF");
}
static void validateTransitions(const Menu& menu) {
    auto nodes = value<uint64_t>(menu.definition + 8); auto count = value<uint32_t>(menu.definition + 24);
    require(nodes && count && count < 128, "The main-menu graph has an unsupported node array."); unsigned matches = 0;
    for (uint32_t i = 0; i < count; ++i) {
        auto node = nodes + i * 156; if (value<uint32_t>(node) != nameId("any")) continue;
        auto transitions = value<uint64_t>(node + 8); auto number = value<uint32_t>(node + 24);
        require(transitions && number < 256, "The native menu transition array differs.");
        for (uint32_t j = 0; j < number; ++j) {
            auto event = value<uint32_t>(transitions + j * 108), target = value<uint32_t>(transitions + j * 108 + 4);
            if (event == nameId("goto_main_menu_off")) { require(target == nameId("off"), "The title refresh transition differs."); ++matches; }
            if (event == nameId("goto_title_screen")) { require(target == nameId("title_screen"), "The title entry transition differs."); ++matches; }
        }
    }
    require(matches == 2, "The native title transitions are not uniquely identified.");
}
static void post(uint32_t event, uint32_t expected) {
    auto& s = session; auto current = inspectMenu(s.base);
    require(current.receiver == s.menu.receiver && current.definition == s.menu.definition && current.name == expected, "The menu changed before its queued action.");
    queueGuards(s.base); uint32_t arguments[64]{}; arguments[0] = event;
    using Queue = void(__fastcall*)(uint64_t, const void*, uint64_t);
    reinterpret_cast<Queue>(s.base + 0x1e064f0)(current.bus, arguments, current.receiver);
}
static void run(Request& request) {
    require(request.magic == 0x35525448 && request.version == 1 && request.operation <= 2, "The title automation request is incompatible.");
    auto base = identity(request.creation); auto menu = inspectMenu(base); request.node = menu.name; auto& s = session;
    if (request.operation == 0) {
        request.phase = menu.name == nameId("main_menu") && menu.screen ? 3 : s.phase; request.xmlBytes = 0;
        if (menu.name == nameId("title_screen") && menu.screen) {
            auto root = titleRoot(base), pointer = value<uint64_t>(root + 160); auto length = value<uint32_t>(root + 184);
            require(length > 0 && length < sizeof(request.xml), "The native title source exceeds its supported size.");
            auto original = bytes(pointer, length); memcpy(request.xml, original.data(), length); request.xmlBytes = length;
        }
        return;
    }
    require(!s.failed && (!s.creation || s.creation == request.creation), "Title automation was already interrupted. Restart Forge before another attempt.");
    if (request.operation == 1) {
        require(s.phase == 0 && menu.name == nameId("title_screen") && menu.screen, "Automatic Continue requires the loaded title screen.");
        require(request.xmlBytes > 0 && request.xmlBytes < sizeof(request.xml) && request.xml[request.xmlBytes - 1] == 0, "The prepared title source is invalid.");
        queueGuards(base); validateTransitions(menu); auto root = titleRoot(base); auto pointer = value<uint64_t>(root + 160); auto length = value<uint32_t>(root + 184);
        auto original = bytes(pointer, length);
        require(sha(original.data(), original.size()) == "E2855AAF17C971FEAE4A5F1E5EE26A6906F5496A82EDE580C4166B8DB476CA4D", "The title source differs from the supported native title.");
        auto replacement = VirtualAlloc(nullptr, request.xmlBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE); require(replacement != nullptr, "Could not allocate the automatic title source.");
        memcpy(replacement, request.xml, request.xmlBytes);
        s.creation = request.creation; s.base = base; s.root = root; s.originalXml = pointer; s.originalBytes = length; s.replacement = (uint64_t)replacement;
        s.replacementBytes = request.xmlBytes; s.menu = menu; s.began = GetTickCount64(); s.phase = 1;
        // Claim before dispatch. No uncertain event or process-owned allocation is retried or freed.
        post(nameId("goto_main_menu_off"), nameId("title_screen"));
    } else {
        require(s.phase != 0, "Automatic title entry has not started.");
        if (s.phase == 1 && menu.name == nameId("off") && !menu.screen) {
            require(value<uint64_t>(s.root + 160) == s.originalXml && value<uint32_t>(s.root + 184) == s.originalBytes, "The title changed before publication.");
            *reinterpret_cast<uint64_t*>(s.root + 160) = s.replacement; *reinterpret_cast<uint32_t*>(s.root + 184) = s.replacementBytes;
            s.phase = 2; post(nameId("goto_title_screen"), nameId("off"));
        }
        if (s.phase == 2) {
            if (menu.name == nameId("main_menu") && menu.screen) {
                if (!s.stableSince) s.stableSince = GetTickCount64();
                if (GetTickCount64() - s.stableSince >= 1000) {
                    require(value<uint64_t>(s.root + 160) == s.replacement && value<uint32_t>(s.root + 184) == s.replacementBytes, "The automatic title source changed.");
                    *reinterpret_cast<uint64_t*>(s.root + 160) = s.originalXml; *reinterpret_cast<uint32_t*>(s.root + 184) = s.originalBytes;
                    s.phase = 3;
                }
            } else s.stableSince = 0;
        }
        if (s.phase != 3 && GetTickCount64() - s.began > 120000) s.phase = 4;
    }
    request.phase = s.phase;
}
extern "C" __declspec(dllexport) DWORD WINAPI H5Title(Request* request) {
    if (!request) return ERROR_INVALID_PARAMETER;
    AcquireSRWLockExclusive(&operationLock); request->status = 0; request->message[0] = 0;
    try { run(*request); }
    catch (const std::exception& error) { request->status = ERROR_INVALID_STATE; strncpy_s(request->message, error.what(), _TRUNCATE); if (request->operation != 0 && session.phase) session.failed = true; }
    catch (...) { request->status = ERROR_UNHANDLED_EXCEPTION; if (session.phase) session.failed = true; }
    ReleaseSRWLockExclusive(&operationLock); return request->status;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) { if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance); return TRUE; }
