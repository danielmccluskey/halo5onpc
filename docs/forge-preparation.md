# Forge preparation: v0.5.1

In v0.7, the next step after this report is **Build conversion inputs / resume**.
It uses the saved report and dump without starting Forge; see
[conversion inputs](conversion-inputs.md). The user's v0.6 test confirmed both
integrated Forge startup and a passed read probe in the selected cache.

This step compares the saved campaign dependency plan with the installed Forge
global module tables. It also asks the running Forge process to read a fresh
32-byte file in the selected cache. It does not convert or launch the campaign.

## Try it

1. Open the launcher and use the existing dump, cache and completed dependency
   plan. Existing v0.3 indexes and v0.4 plans work directly.
2. In v0.6 and later, the launcher starts Forge for you when preparing data. You
   can also use **Start Forge** independently; see [Forge startup](forge-startup.md).
3. Click **Prepare Forge data** under **Forge compatibility**. Use **Pause** to
   stop after a bounded read or database checkpoint; the same button resumes.
4. Read the native candidate counts and cache access result. **Show compatibility
   report** selects the JSON file. **Copy details** includes errors and provenance.
5. If cache access is denied, click **Allow Forge to read cache**. The worker adds
   a read/traverse rule for Forge's package identity to this owned cache folder
   and repeats preparation and the live read check. Existing indexes and plans
   do not need rebuilding.

If Forge is closed, v0.6 starts it through the integrated Halo bridge before
reading protected module files. It never starts the old research tools.
It does not change installation permissions, ownership or game module files.

**Passed** means Forge read the probe file during that run. It is not a permanent
permission grant or proof that future generated modules can load. **Denied** means
this cache location is not ready for game output. **NotTested** means tables could
be read directly, but Forge was not running to perform the access check. No cache
ACLs are changed by ordinary preparation. The separate **Allow Forge to read
cache** action is the only operation that changes cache permissions.

That action uses Windows' package SID derived from the stable Forge family name,
not a developer account or a group covering every packaged app. It adds
`ReadAndExecute` (including directory traversal) with inheritance for existing
and future contents. It does not grant write/delete access, replace unrelated
rules, remove deny entries, change ownership, disable protection, or change the
dump, parent folders or Forge installation. Linked cache contents are refused
before changing inheritance. The rule remains after the operation and retries
are idempotent. If Windows refuses the change, the app reports
`CACHE_PERMISSION_DENIED`; it does not elevate or take ownership automatically.
The live probe must still pass: a protected child folder, an explicit deny or
another ancestor restriction may require choosing another cache location.

Microsoft documents [package-specific access rules](https://learn.microsoft.com/en-us/windows/apps/develop/communication/sharing-named-objects)
and [AppContainer access checks](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer).

## Components and storage

- Core: bounded table acquisition, native identity catalog, exact reference
  comparison, immutable reports and cache ownership checks.
- Worker: Windows package/process verification, direct reads, and a native reader
  fallback for protected files. The WPF app uses the existing typed pipe protocol.
- ForgeReader: a packaged, approximately 122 KiB x64 DLL. It uses ordinary Windows
  file reads in Forge's process; it does not hook engine functions or use fixed
  game addresses. Players need no compiler, Python or shell scripts.

All tables, checkpoints, reports and probe files stay in the selected cache:

```text
forge.sqlite                independent format 1; module tables and native entries
forge/<sha256>.json          immutable comparison report
forge-summary.json          last complete report pointer, owned by this cache
logs/index.log              existing bounded diagnostic log
```

The DLL is staged under Forge's own
`%LOCALAPPDATA%/Packages/Microsoft.Halo5Forge_8wekyb3d8bbwe/LocalState/h5sololauncher/bridge/<dll-sha256>/`.
This small support file inherits the package data folder's permissions. Bulk
game data never moves there. The DLL remains loaded until Forge exits.

Each module checkpoint is one SQLite transaction, keyed by exact package full
name, relative filename, original length and SHA-256 of the tables. Native
entries retain group, tag ID, asset ID, checksum, item index and source-relative
path. Raw tables preserve resource/block links for subsequent readers. All
tables are reread before reuse and the full snapshot is verified again before
publication. No executable or large module payload is copied.

Package versions coexist as separate checkpoint keys. Removed files cannot leak
into a new report because its snapshot explicitly lists the current inventory.
Catalog identity binds that sorted inventory, lengths, table hashes and rules
version to the exact package. A failed or cancelled run preserves the last
complete report. Old package checkpoints remain in the cache; automatic pruning
is deferred. The source catalog and source plan are unchanged.

The source plan's SHA-256, format, rules, bundle revision and indexed input
fingerprint are verified before comparison. This compares that saved source
plan; it does not revalidate every campaign payload again. Conversion must
revalidate the actual inputs it consumes.

## Reader boundaries and recovery

The worker only selects a process in its own Windows session whose full image
path and package full name match the discovered installation, and whose machine
type is native x64. Multiple matches are refused. Windows must permit opening
that process. The DLL additionally checks its host executable and package family.

The fixed protocol has no embedded pointers, a maximum 1 MiB result and a 128 MiB
module table cap. Global paths are restricted to `deploy/any/levels/globals*.module`
and `deploy/pc/levels/globals*.module`, with a narrow filename grammar. The native
reader independently calculates the table boundary and will not return payloads.
The separate probe operation accepts only a 32-byte nonce-named file in a `forge`
subfolder; the worker verifies it is inside this owned cache and has no linked
ancestors. Native operations perform no file writes.

Windows loader addresses are rebased from the exact corresponding system module,
and the reader export is rebased from the matching staged DLL. No target address
is hardcoded. A successful loader thread exit is followed by actual module
enumeration; a truncated thread exit code is never treated as an x64 DLL address.

Each remote call has a 15-second wait. Pause takes effect between calls; a call's
buffer is freed only after its thread has ended. A timeout does not terminate a
game thread or free memory it may still use. A process-creation-time session
marker is written before attachment and retained on crash/timeout or worker loss.
Close Forge completely and reopen it before retrying an uncertain session. The
launcher only terminates its own unresponsive worker. Old small markers remain
as diagnostic evidence and do not block a new Forge process creation time.

## What the report means

Missing references are compared by exact **group + tag ID + asset ID**. Every
matching native checksum/patch alternative is retained. The UI counts distinct
missing identities, which can differ from the source plan's issue count.

Version 0.5.1 separately retains native entries with the same group/tag ID but a
different asset ID. These are diagnostic alternatives; exact match counts and
unresolved counts are unchanged. The report preserves their filenames, item
indices, names, native asset IDs and checksums. Rules version 2 includes this
diagnostic pass. Version 0.5 reports remain readable; preparing again adds the
new diagnostics while reusing their table checkpoints.

Being present in a global table does not establish effective runtime load order,
native schema compatibility, generated platform aliases or a usable converted
resource. Those remain explicit gates, followed by module construction and a
real Forge load check. No missing source issue is silently removed.

## Build and test

Developers need the .NET 10 SDK, CMake and Visual C++ x64 build tools with a Windows
SDK. From the repository root, configure with a generator supported by the local
CMake/Visual Studio installation, for example:

```powershell
cmake -S src/h5sololauncher.ForgeReader -B artifacts/forge-reader-build -G "Visual Studio 16 2019" -A x64
cmake --build artifacts/forge-reader-build --config Release
ctest --test-dir artifacts/forge-reader-build -C Release --output-on-failure
dotnet test h5sololauncher.slnx
dotnet publish src/h5sololauncher -c Release -r win-x64 --self-contained true -o artifacts/h5sololauncher-0.5.1
```

`ForgeReaderPath` can override the DLL build output path. A missing native DLL
fails the .NET build with an actionable message. Keep the entire published folder
together; the WPF executable, worker, native helper, SQLite and .NET runtime are
all included. Source builds and players do not depend on a developer's drive or
installation version.

Automated validation covers generated module fixtures, exact candidate matching,
patch alternatives, immutable plan/report verification, version isolation,
interrupted snapshot recovery, probe result handling, cache locking, WPF state
gating, worker IPC, native protocol/path rejection and refusal outside Forge.
The installed package/real saved-plan smoke check returned `FORGE_NOT_RUNNING`
with Forge closed. The user's live v0.5 test then collected six modules, 75,411
native tags and 35,188,401 table bytes through the Forge process reader, confirming
protected module acquisition. The external cache probe returned access denied.
Read-only inspection found 4,269 of 6,369 unmatched identities sharing group/tag
IDs with native entries but differing in asset ID; none matched exactly. This
motivated the scoped permission action and separate identity diagnostics.

The v0.5.1 permission tests use disposable test caches and cover existing-file
inheritance, future-file inheritance, package specificity, idempotence, preserved
ownership/denies and unchanged surrounding folders. The real cache permission
action and WPF interaction remain for the user's test; no game was launched or
controlled by automation.

Version 0.5.1 validation passed: 108 managed tests and six worker tests rerun
against the self-contained published worker. The native helper is byte-identical
to the user-tested v0.5 build; its CTest protocol/path suite passed in that release.

The Windows thread lifetime rules used here are documented in Microsoft's
[CreateRemoteThread reference](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createremotethread).
