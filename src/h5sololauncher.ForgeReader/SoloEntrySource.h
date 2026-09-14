#pragma once
#include "RuntimeSupport.h"
namespace h5runtime {
inline std::vector<unsigned char> soloEntrySource(const std::vector<unsigned char>& original) {
    require(!original.empty() && original.size()<4*1024*1024 && original.back()==0,"The campaign main menu source is malformed.");
    std::string text(reinterpret_cast<const char*>(original.data()),original.size()-1);
    require(text.find('\0')==std::string::npos,"The campaign main menu source contains embedded zeroes.");
    const std::string anchor="<halo:DoLuaAction LuaLine=\"OnListBoxSelectionChange()\"/>";
    const auto at=text.find(anchor);
    require(at!=std::string::npos && text.find(anchor,at+anchor.size())==std::string::npos,"The campaign main menu load action is missing or ambiguous.");
    // Invoke the original Solo callback, including local-session setup. A graph
    // event alone opens the campaign screen with the previous lobby activity.
    text.insert(at+anchor.size(),"<halo:DoLuaAction LuaLine=\"Anubis.Lobby.RequestGotoLobbyActivity(Hui.Import(Anubis.Lobby.eLobbyMenuActivity).k_OfflineCampaign, true)\"/>");
    std::vector<unsigned char> output(text.begin(),text.end());output.push_back(0);return output;
}
}
