Current update: **0.9 adds dump-independent cache playback**. Choose a complete
cache and Play; the source is optional for building or repairing. See
[portable cache implementation and validation](portable-cache.md). The gameplay
validation limitations below still apply; portability tests do not establish
additional mission playability.

# h5sololauncher 0.8 implementation and validation

The WPF app now prepares the campaign from an extracted dump and the installed
Forge package, then starts Forge and opens Solo automatically. It contains the
runtime components needed by the old launcher. This is a preview: full playable
parity has not yet been established for this implementation.

## User flow

1. Select the extracted campaign folder and a cache destination. Both are remembered.
2. Use **Prepare & play**. The app indexes the dump, resolves source layers, reads
   native conversion controls, converts payloads, writes modules/audio, prepares
   the menus and starts Forge. Completed cache stages survive interruption.
3. On later runs use **Play**. The launcher checks the prepared cache, starts Forge
   through the Halo app, invokes the original title action, registers campaign
   content, installs the bundled runtime components and opens the offline Solo
   lobby. There is no timed Continue/Solo click during setup.
4. Choose Osiris or Blue Team, difficulty and Start New Mission in the native menu.

The WPF app has one primary action. Individual engineering stages are no longer
required user actions. Options, re-preparation and reports are collapsed. Pause
stops between safe operations. Stop monitoring and closing the app leave Forge
running. Reopening Play can attach monitoring to a compatible prepared session.

## Supported inputs and architecture

- Extracted Guardians 1.1.31695.21; Forge 1.194.6192.2 x64; English (US).
- Installed Halo companion app for the Forge activation request.
- A user-selected writable cache, separate from the source dump and installed game.
- WPF UI, Core readers/converters/stores, private-pipe Worker, and bundled native
  DLLs. The published folder includes .NET and SQLite. Users need no scripts,
  Python, compiler, Xbox SDK or archived research files.
- Source data and native controls come from the chosen dump and installed Forge.
  Research captures were used to establish development controls only. No game
  files are shipped with the app.
- Native calls verify the package, executable, process creation time, supported
  instruction bytes and relevant object identities. Helper changes require a fresh
  game process. Uncertain native operations are never blindly retried.
- No WindowsApps ownership or package registration changes. Cache read permission
  is granted to the Forge package and checked inside the game process.
- Immutable manifests bind source, language, Forge version and conversion rules.
  Files are hashed and decoded before publication. Small damaged/missing runtime
  configurations invalidate readiness so the primary button offers preparation.

## Completed conversion evidence

Actual-dump conversion and repeated preparation succeeded:

| Component | Evidence |
| --- | --- |
| Native inputs | Six installed modules, about 11.6 GB, payload and table verification |
| Schemas | 2,090 portable definitions; eight structural converters with 2,002 exact controls |
| Effective source | 15,716 physical tags; ten unresolved identities remain explicit |
| Textures | 402 supported shapes; 155,318,960 addresses checked against development controls |
| Converted assets | 15,716 tags and 12,866 resource entries in sealed packs |
| Shaders | 41 definitions, five banks, 604 programs; reflection, disassembly and hardware creation passed |
| Modules | 115 revision-27 modules, 8,759,485,397 bytes; every output payload decoded again |
| Audio | 109 banks, 21,069 media records, 1,268,226,746 bytes; 222 exact controls |
| Registry | 19 original mapinfo records; three available routes derived from source and output manifests |
| Runtime files | 119 files verified inside Forge and retained with read-only handles |
| Menu artwork | 46 images, five tags; visible campaign menu artwork checked |

The ten unresolved identities are three cube bitmaps and seven lightmap tags.
They remain documented in the effective plan; they are not fabricated matches.
Their remaining gameplay implications are unverified.

Latest development cache identifiers:

- Assets: F3787880BA0F12ACE4ECDAFA06239849E85C25AAAEE3B295DE13EF3586BB7655
- Shaders: 0C0C8DCFF482D1548C38143C6E130945B35F03E8A9394A33305DD0BC54C149C9
- Modules: C8A0452A14DAE33B2E19407B09DC615D0271DC9D9C77478BD7746B2F2B609086
- Audio: 0981CCAC70F2821ABAC393B6D1DC4257AF65CD9E4714303A36AB161566E4A5C8
- Prepared: FEF35C917BD7CC10C090A734ED78967A1BB9B5E71A1DDC04FD967425639099D0

## Runtime implementation

- Original Halo URI activation and native title callback, preserving sign-in checks.
- Native offline lobby setup before Solo presentation, avoiding the online lobby
  state produced by a bare campaign graph event.
- Campaign registry, verified module/resource routing and mission availability.
- Shader bank ownership and frame publication, including native optional lookup
  tables and a bounded publication timeout with copyable failure output.
- Retained/rebound pause-menu assets and required localization versions.
- Campaign audio registration and Unicode opening-movie routing from the dump.
  Opening movie viewport and audio-clock alignment have explicit failure states.
- Native campaign mouse selection and skull availability controls.
- Display settings adaptation derived from the installed settings script: field
  of view stored in package LocalState, plus 144/180 FPS options while preserving
  explicit cinematic timing. Native initialization and isolated tests passed;
  changing these settings in the actual game UI remains untested.
- Campaign ending adapter, local mission report and guarded native successor
  handoff. Osiris -> cin_030 -> Blue Team is the available route; later missions
  are not declared available.
- Runtime monitoring for content, renderer, UI, audio, movie, controls, display
  and completion failures. Unexpected process exits retain the last status.
- Rotating local startup/play logs, copyable diagnostics and an unexpected
  launcher error log. These logs are not uploaded.

## Live validation (14 September 2026)

Verified in this implementation:

- Fresh Forge startup and automatic title entry, repeatedly.
- Complete WPF Prepare & play from remembered folder choices through automatic
  Solo entry and ongoing monitoring. A reused-cache run took about five minutes
  on the current H-drive setup. First conversion is substantially longer.
- Stop monitoring and app close detach without closing Forge.
- A rendered first-person Osiris starting scene with geometry, materials, weapon,
  HUD and moving scene effects. Opening movie routing, viewport and clock
  alignment previously reported success in that session.
- Native display initialization returned ready, FOV 78, no settings write error.
- Windows game-view API confirmed an active visible view; presentation is requested
  at the end of Play and when resuming an existing session.
- The self-contained 0.8.0 worker completed startup into Solo, then a second Play
  returned the same process as an already prepared session. All 13 published
  native DLLs matched the tested build by SHA-256. The published WPF app loaded
  remembered paths and reported the prepared cache as ready.

Not verified:

- Responsive player movement, firing and pause navigation.
- Fresh Blue Team gameplay, including Score Attack.
- Osiris report Continue through cin_030 into Blue Team.
- A natural complete mission playthrough.
- Live changes to FOV/frame-rate settings and optional opening-movie skipping.

The computer-use tool stopped exposing a separately targetable Forge window.
It associated a cropped game render with the Halo app frame. Refreshing discovery,
restarting both apps, resetting the tool session and native view presentation did
not resolve that binding. Consequently the rendered scene is not claimed as a
successful input/playability test. The old launcher's documented Blue Team and
transition successes are reference evidence, not tests of this new build.

A diagnostic ending request in the earlier Osiris session reached the original
ending cinematic and then the local report state. That request was artificial;
the report's visible controls and onward transition were not verified. The
production flow uses the original mission lifecycle. An unused arbitrary Lua
probe was removed from the bundled runtime.

## Automated checks and release gate

Run the commands in src/h5sololauncher/README.md. Tests use generated fixtures,
not game files. Relevant coverage includes malformed source data, conversion,
module integrity/reuse, lifecycle guards, frame publication, display timing,
worker recovery, cancellation and the one-action prepare/play/watch flow.

Do not describe the preview as fully playable until the unverified live checks
above pass. The current Forge save state must be backed up before tests and
restored afterward; archived old saves are not a replacement for that backup.

Latest checks: 174 managed tests and all five native CTest targets passed. No
personal environment paths or runtime script invocations were found in the
production source scan; the published folder contains no game modules, movies,
audio packages or developer scripts.

After stopping both test apps, the 19 files from the pre-test current-profile
backup were restored and SHA-256 verified (8,487,661 bytes). The entire test save
state was moved into a separate local validation archive. Restoration evidence
is in artifacts/live-validation/profile-restoration.json. The source dump and
cache remain on H:.
