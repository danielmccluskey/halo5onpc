#pragma once
#include "RuntimeSupport.h"
#include <set>
namespace h5runtime {
struct Menu { uint64_t bus, receiver, definition, node, screen; uint32_t name; };
inline Menu inspectMenu(uint64_t base) {
    auto context = value<uint64_t>(base + 0x590e758);
    if (!context || value<uint32_t>(context + 0x600) != 2) return {};
    auto bus = context + 0x2c0; auto head = value<uint64_t>(bus + 8), count = value<uint64_t>(bus + 16);
    if (!head && !count) return {};
    require(head && count < 3000, "The native event bus has an unsupported shape.");
    std::vector<uint64_t> pending{ value<uint64_t>(head + 8) }; std::set<uint64_t> visited; std::vector<Menu> matches;
    while (!pending.empty()) {
        auto at = pending.back(); pending.pop_back(); if (at == head) continue;
        require(visited.size() < 3000 && visited.insert(at).second, "The native event tree changed during observation.");
        auto row = bytes(at, 56); require(row[25] == 0, "The native event tree contains an unexpected node.");
        uint64_t left, right, listHead, size; uint32_t event;
        memcpy(&left, row.data(), 8); memcpy(&right, row.data() + 16, 8); memcpy(&event, row.data() + 32, 4);
        pending.push_back(left); pending.push_back(right);
        if (event != nameId("goto_title_screen")) continue;
        memcpy(&listHead, row.data() + 40, 8); memcpy(&size, row.data() + 48, 8); if (!size) continue;
        require(size < 256, "Too many native title subscribers.");
        auto entry = value<uint64_t>(listHead), previous = listHead; std::set<uint64_t> entries;
        while (entry != listHead) {
            require(entries.size() < size && entries.insert(entry).second, "The native title subscriber list changed.");
            auto data = bytes(entry, 40); uint64_t next, prev, receiver; uint32_t flags;
            memcpy(&next, data.data(), 8); memcpy(&prev, data.data() + 8, 8); memcpy(&receiver, data.data() + 16, 8); memcpy(&flags, data.data() + 24, 4);
            require(prev == previous, "The native title subscriber list changed.");
            if (receiver && value<uint64_t>(receiver) == base + 0x36e95b0 && data[36] == 0 && flags == 1) {
                auto definition = value<uint64_t>(receiver + 0x28);
                if (definition && value<uint32_t>(definition) == nameId("main_menu")) {
                    auto node = value<uint64_t>(receiver + 0x30), screen = value<uint64_t>(receiver + 0x40);
                    matches.push_back({ bus, receiver, definition, node, screen, node ? value<uint32_t>(node) : 0 });
                }
            }
            previous = entry; entry = next;
        }
        require(entries.size() == size, "The native title subscriber count changed.");
    }
    require(value<uint64_t>(bus + 8) == head && value<uint64_t>(bus + 16) == count && matches.size() <= 1, "The native menu changed during observation.");
    return matches.empty() ? Menu{} : matches[0];
}
}
