#include "HubBridge.cpp"
int main() {
    LaunchRequest request{};
    if (H5LaunchForge(&request) || request.status != E_INVALIDARG) return 1;
    request.magic = 0x48354C48; request.version = 1;
    if (H5LaunchForge(&request) || request.status != E_ACCESSDENIED || request.accepted || request.stage) return 2;
    return 0;
}
