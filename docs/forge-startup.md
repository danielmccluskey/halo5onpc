# Forge startup: v0.6.0

The launcher now starts Forge as part of **Prepare Forge data** and **Allow Forge
to read cache**. **Start Forge** also works independently of selecting a campaign
or cache. No app opens automatically just because h5sololauncher was opened.

The launcher reuses a matching running Forge process. Otherwise it discovers the
installed Halo companion app, activates it, sends Forge's startup protocol request
from that app, and waits for a matching Forge process to remain alive for roughly
two seconds. A successful Windows activation alone is not reported as a running
game. The game menu may still be loading when the process check succeeds; this
step does not start a campaign or establish gameplay readiness.

## User flow

Open h5sololauncher and press **Start Forge**, or go straight to **Prepare Forge
data** after preparing the source plan. The Halo app may appear briefly during
startup. Progress describes Halo activation, the Forge request, process startup
and the check that it stays running.

If the Halo companion app is missing, the launcher explains the prerequisite
and offers **Find Halo in Store**. Availability depends on the user's Store
account and region; the launcher does not redistribute Microsoft's package or
guarantee it can be acquired. A modified Halo hub is not required by this route.
Both stock UWP and packaged desktop Halo hubs are supported by the bridge design.

**Stop waiting**, **Pause**, and closing the launcher cancel further work. A
request already dispatched to Windows may still finish. The game and Halo app
are not terminated by cancellation. Existing sessions are checked again on retry.
Errors remain copyable and startup outcomes are logged in
`%LOCALAPPDATA%/h5sololauncher/logs/launch.log`, with one rotated previous log.

## Modules

- `ForgeLaunchCoordinator` holds the startup state flow independently of Windows,
  with a replaceable platform interface for failure/recovery tests.
- `WindowsForgeLaunch` discovers the current user's installed packages, activates
  Halo, invokes the bridge, and observes the matching Forge process.
- `PackageProcessSession` shares the bounded native-helper loading and lifetime
  rules used by the existing Forge reader. Targets must match their installed
  package full name, executable path, Windows session and native x64 architecture.
- `h5sololauncher.HubBridge.dll` sends only the fixed Forge protocol request. Its
  entry point refuses any host outside `Microsoft.Tomp_8wekyb3d8bbwe`.

The bridge uses `ms-xbl-multiplayer://launch` with the explicit Forge target family.
Inside the stock UWP Halo app it dispatches through that app's real CoreDispatcher
because UWP URI activation needs the UI/ASTA thread. Packaged desktop hubs may use
the worker thread. The protocol and caller identity follow the existing local
launch investigation; the new implementation has no dependency on those scripts,
custom hub folders, old launch captures or developer paths.

The helper is copied under the installed Halo app's own
`%LOCALAPPDATA%/Packages/Microsoft.Tomp_8wekyb3d8bbwe/LocalState/h5sololauncher/bridge/<sha256>/`.
It remains loaded until Halo exits. No package registration is replaced; no
WindowsApps permissions or game files are changed. The app does not require
Developer Mode merely to use this startup path. Users still need functioning
installed Halo and Forge packages for their account.

## Recovery and bounds

One current-user launch lock prevents concurrent starts. A durable attempt
record is saved before activating Halo. After an uncertain failure or cancellation,
another request is withheld for 90 seconds unless the matching Forge process has
already appeared. The native bridge also refuses a second request while its first
asynchronous request remains pending.

Halo is given up to about 15 seconds to appear and remain alive. Native calls use
the shared 15-second wait and never free a still-running thread's request memory.
The bridge spends at most about three seconds finding Halo's dispatcher and nine
seconds waiting for a launch outcome. Asynchronous callbacks retain private heap
state, never a pointer to the worker's remote request buffer. An uncertain native
session marker requires closing the affected apps before a fresh attempt.

After acceptance, the coordinator observes Forge for up to 180 quarter-second
polls, checking package, path, PID and process creation time. An exit or replacement
during observation is a startup failure. Preparation only continues after the
running-process result. Cache ownership and the saved source plan are validated
before automatic startup is allowed to occur.

## Build and validation

The existing CMake build now also produces `h5sololauncher.HubBridge.dll` and its
protocol/host checks. Publish includes both native helpers and the worker:

```powershell
cmake --build artifacts/forge-reader-build --config Release
ctest --test-dir artifacts/forge-reader-build -C Release --output-on-failure
dotnet test h5sololauncher.slnx -c Release
dotnet publish src/h5sololauncher -c Release -r win-x64 --self-contained true -o artifacts/h5sololauncher-0.6.0
```

Both helpers must be built before .NET build/publish. `ForgeReaderPath` and
`HubBridgePath` can point to custom build outputs. CMake uses the installed Visual
C++ tools and Windows SDK C++/WinRT headers; players need neither build tools nor
scripts.

Read-only preflight is available as:

```text
h5sololauncher.Worker.exe --launch-check <Forge install folder> <package full name>
```

It does not activate or modify any app. Local preflight identified the currently
installed stock Halo executable and packaged helper successfully. Automated tests
cover process reuse, late-start guards, acceptance without a process, process exit
and PID reuse, cancellation, missing prerequisites, UI state gating, IPC, and native
rejection of invalid protocols or hosts. The native bridge's real Halo-to-Forge
launch and WPF interaction require the user's live test; no game or Halo app was
started during automated validation.

Release validation: 120 managed tests and both native CTest suites passed. Eight
worker-related tests also passed with the published worker configured. Published
read-only preflight returned `Available`, and both packaged helper hashes match
their tested native builds. For the live test, leave Forge closed and press
**Start Forge** in v0.6; copy the details if startup needs attention.

Windows API references: [package activation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication),
[URI activation and thread requirements](https://learn.microsoft.com/en-us/uwp/api/windows.system.launcher.launchuriasync),
[Microsoft Store links](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-store-app).
