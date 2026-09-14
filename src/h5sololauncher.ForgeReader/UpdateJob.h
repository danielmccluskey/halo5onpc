#pragma once
#include "RuntimeSupport.h"
#include <intrin.h>

// One bounded operation on the normal game update thread. The original entry
// instruction is restored before work starts. A request never runs twice.
#ifndef H5_UPDATE_JOB_NAMESPACE
#define H5_UPDATE_JOB_NAMESPACE updateJob
#endif
namespace h5runtime::H5_UPDATE_JOB_NAMESPACE {
inline volatile LONG phase = 0;
inline uint64_t point = 0, deadline = 0, finished = 0;
inline DWORD thread = 0, fault = 0;
inline PVOID handler = nullptr;
inline void (*operation)() = nullptr;
inline constexpr unsigned char expected[] = {0x41,0x56,0x48,0x83,0xec,0x40};
inline bool exchange(unsigned char from, unsigned char to) {
    auto address = reinterpret_cast<char*>(point); DWORD prior = 0, unused = 0;
    if (!VirtualProtect(address, 1, PAGE_EXECUTE_READWRITE, &prior)) return false;
    bool changed = _InterlockedCompareExchange8(address, static_cast<char>(to), static_cast<char>(from)) == static_cast<char>(from);
    if (!FlushInstructionCache(GetCurrentProcess(), address, 1)) changed = false;
    if (!VirtualProtect(address, 1, prior, &unused)) changed = false;
    return changed;
}
inline void finish(DWORD error) {
    fault = error; finished = GetTickCount64(); InterlockedExchange(&phase, error ? 4 : 3);
}
inline void invoke() {
    __try { operation(); }
    __except(EXCEPTION_EXECUTE_HANDLER) { fault = GetExceptionCode(); }
}
inline LONG CALLBACK trap(EXCEPTION_POINTERS* exception) {
    if (exception->ExceptionRecord->ExceptionCode != EXCEPTION_BREAKPOINT ||
        reinterpret_cast<uint64_t>(exception->ExceptionRecord->ExceptionAddress) != point) return EXCEPTION_CONTINUE_SEARCH;
    if (InterlockedCompareExchange(&phase, 2, 1) == 1) {
        if (!exchange(0xcc, expected[0])) { finish(ERROR_INVALID_DATA); return EXCEPTION_CONTINUE_SEARCH; }
        thread = GetCurrentThreadId(); invoke(); finish(fault);
    } else {
        while (InterlockedCompareExchange(&phase, 0, 0) == 2) YieldProcessor();
        if (!finished || GetTickCount64() > finished + 1000 || memcmp(reinterpret_cast<void*>(point), expected, sizeof(expected))) return EXCEPTION_CONTINUE_SEARCH;
    }
    exception->ContextRecord->Rip = point; return EXCEPTION_CONTINUE_EXECUTION;
}
inline DWORD WINAPI watch(void*) {
    while (InterlockedCompareExchange(&phase, 0, 0) == 1) {
        if (GetTickCount64() >= deadline && InterlockedCompareExchange(&phase, 2, 1) == 1) {
            finish(exchange(0xcc, expected[0]) ? WAIT_TIMEOUT : ERROR_INVALID_DATA); break;
        }
        Sleep(20);
    }
    return 0;
}
inline void arm(uint64_t base, void (*work)()) {
    require(phase == 0 && !handler, "The update operation has already been attempted. Restart Forge before retrying.");
    point = base + 0x869460;
    auto entry = bytes(point, sizeof(expected)); require(!memcmp(entry.data(), expected, sizeof(expected)), "Forge's update entry changed.");
    operation = work; handler = AddVectoredExceptionHandler(1, trap); require(handler != nullptr, "The game update handler could not be installed.");
    deadline = GetTickCount64() + 30000; InterlockedExchange(&phase, 1);
    if (!exchange(expected[0], 0xcc)) { finish(ERROR_INVALID_DATA); throw std::runtime_error("The game update entry could not be queued."); }
    auto watcher = CreateThread(nullptr, 0, watch, nullptr, 0, nullptr);
    if (!watcher) {
        if (InterlockedCompareExchange(&phase, 2, 1) == 1) finish(exchange(0xcc, expected[0]) ? ERROR_NOT_ENOUGH_MEMORY : ERROR_INVALID_DATA);
        throw std::runtime_error("The game update timeout could not be started.");
    }
    CloseHandle(watcher);
}
}
