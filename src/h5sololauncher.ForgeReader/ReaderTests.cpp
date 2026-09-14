#include "Reader.cpp"
#include <memory>

int main() {
    if (!globalPath(L"deploy/any/levels/globals-rtx-1.module") ||
        !globalPath(L"deploy/pc/levels/globals-rtx-20-1.module") ||
        !globalPath(L"deploy/pc/levels/globals.module")) return 1;
    for (const auto path : { L"deploy/pc/levels/globals-.module", L"deploy/pc/levels/globals--1.module",
        L"deploy/pc/levels/globals.module:stream", L"deploy/pc/levels/globals/../../secret.module",
        L"deploy/x1/levels/globals-rtx-1.module", L"C:/deploy/pc/levels/globals.module" })
        if (globalPath(path)) return 2;
    if (!probePath(L"D:\\cache folder\\forge\\probe-0123456789abcdef0123456789abcdef.bin")) return 3;
    for (const auto path : { L"D:cache\\forge\\probe-0123456789abcdef0123456789abcdef.bin",
        L"D:\\cache\\..\\forge\\probe-0123456789abcdef0123456789abcdef.bin",
        L"D:\\cache\\forge\\probe-0123456789abcdef0123456789abcdef.bin:secret",
        L"D:\\cache\\forge\\probe-not-a-nonce.bin" })
        if (probePath(path)) return 4;
    auto r = std::make_unique<Request>();
    if (H5Read(r.get()) != ERROR_INVALID_PARAMETER) return 5;
    r->magic = 0x48354652; r->version = 1; r->operation = 1; r->requested = 48;
    if (H5Read(r.get()) != ERROR_ACCESS_DENIED) return 6; // Never read files outside a Forge package process.
    r->requested = sizeof(r->bytes) + 1;
    if (H5Read(r.get()) != ERROR_INVALID_PARAMETER) return 7;
    if (!audioPath(L"sound/win/SFX/soundbank.pck") || !audioPath(L"sound/win/English(US)/soundvoice.pck")) return 8;
    for (const auto path : { L"sound/win/../soundvoice.pck", L"sound/win/SFX/other.pck", L"sound/win/English(US)/soundvoice.pck:secret" })
        if (audioPath(path)) return 9;
    return 0;
}
