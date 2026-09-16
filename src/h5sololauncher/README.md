# h5sololauncher

A small WPF app that prepares all 15 missions through Guardians from an extracted Halo 5:
Guardians dump and starts the campaign in Halo 5: Forge. The modular implementation
is under live validation; see [current evidence](../../docs/playable-implementation.md).
Version 0.12.0 adds full campaign preparation. Osiris and Blue Team are user-verified;
see [Expansion preparation record](../../docs/meridian-station-implementation.md) for this expansion.

## Use

1. Open `h5sololauncher.exe`. It checks the installed Forge package.
2. Choose an existing complete cache. No dump is needed for playback. To build
   or repair instead, choose an extracted campaign folder and a cache destination.
   Both choices are remembered. New caches use a `h5sololauncher-cache` subfolder.
3. Click **Play**, or **Prepare & play** for a new cache. Preparation, cache
   permissions, Forge startup, title entry and campaign setup run automatically.
   In Solo, choose an available mission, your difficulty and **Start New Mission**.

There is no timed Continue or Solo click during setup. **Pause** keeps completed
cache stages. **Stop monitoring** leaves Forge running; closing the launcher also
stops monitoring without closing the game. Options and technical reports are
under **Options and diagnostics**.

This version targets extracted Guardians 1.1.31695.21, Forge 1.194.6192.2 x64 and
English (US). The installed Halo companion app is required for Forge startup.
Other builds are rejected with details. Archives need to be extracted first.
No game assets, Xbox SDK, Python interpreter or developer scripts are included
or required by the published app. Game assets are read from the selected dump
and the installed Forge package.

## Cache and errors

To add the three missions after Glassed to an existing cache, select the extracted dump and
use **Options and diagnostics > Prepare cache only**. Existing complete caches
remain playable without the dump. Updating the launcher alone does not add assets.

Use a writable local NTFS drive with room for generated game modules, source
packs, native conversion controls and metadata. The source dump and Forge
installation are kept separate from the app-owned cache. Preparation grants the
Forge package read access to that cache, verifies files inside Forge and retains
read handles for the game session. It does not change WindowsApps ownership.

A portable playback manifest binds the generated files to their hashes, language,
Forge version and runtime configuration. Select a copied or moved cache and click
**Play**; the launcher regenerates machine-specific paths and grants Forge access.
A dump is only needed to build or repair files. Complete 0.8 caches are upgraded
from their cached manifests and variants without opening the original dump.
Index-only or incomplete caches explain that they require a dump to finish.

Cache selection checks small metadata/configuration hashes and game file sizes.
Forge verifies game payload hashes before activation. A sealed cache does not
need the source index, conversion packs or old source-plan files for playback.
Copy the whole cache for now; manual pruning is not part of the user workflow.
See [portable cache format](../../docs/portable-cache.md).

**Copy details** includes the operation, file, error code and native component
status. Local startup and play logs are under `%LOCALAPPDATA%/h5sololauncher/logs`;
the cache keeps its indexing logs and manifests. Logs rotate and stay local.
After a helper fails or changes version, close Forge before retrying. The app
never blindly retries an uncertain native operation in the same process.

Field of view is stored in Forge's own LocalState `h5sololauncher/display.ini`.
The settings menu provides the original frame rates plus 144 and 180 FPS options.
Explicit cinematic timing is preserved. Mission reports and the available
routes through Guardians use the original campaign lifecycle. The full campaign expansion awaits bulk gameplay testing.

## Build

Development requires Windows x64, .NET 10 SDK, CMake and Visual C++ build tools.
Players use the self-contained published folder.

```powershell
cmake -S src/h5sololauncher.ForgeReader -B artifacts/forge-reader-build -A x64
cmake --build artifacts/forge-reader-build --config Release
ctest --test-dir artifacts/forge-reader-build -C Release --output-on-failure
dotnet test h5sololauncher.slnx -c Release
dotnet publish src/h5sololauncher -c Release -r win-x64 --self-contained true -o artifacts/h5sololauncher-0.12.0
```

Keep the complete published folder together, including the worker, native DLLs,
.NET runtime and SQLite. No personal drive or username is an application default.

## Modules

- **WPF app:** package discovery, folder selection, settings, one preparation/play
  action, progress, cancellation and copyable diagnostics.
- **Core:** bounded dump readers, transactional index, dependency/layer selection,
  verified conversion packs, module/audio/shader writers and immutable manifests.
- **Worker:** typed private pipe, preparation coordinator, process identity checks,
  native setup and ongoing runtime monitoring.
- **Native components:** Halo launch bridge, file reader, campaign registry and
  routing, menus, rendering, UI retention, audio, movies, controls, display settings
  and mission completion. Helpers are bundled binaries with guarded native calls.
- **Tests:** generated fixtures for readers, conversion, lifecycle, cancellation,
  worker recovery and native hook behavior; no game files are test dependencies.

Legacy research captures are development controls only. They are not launcher
inputs or distributable game data.
