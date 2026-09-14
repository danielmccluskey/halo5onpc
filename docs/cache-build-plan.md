# h5sololauncher: dump processing and cache plan

Planning baseline: 13 September 2026. The first indexing milestone is implemented in v0.3.0; see [source indexing](source-indexing.md) for its shipped scope and validation. Conversion and runtime sections below remain a plan.

The player chooses their campaign dump and a cache folder, presses **Build cache**, and returns to a reusable, verified campaign installation. The launcher owns the workflow through callable modules and one worker process. Players need no Python, compiler, SDK, research folders, or previously converted game files.

## 1. What the fresh dump tells us

The inspected package has the following relative folder structure:

```text
Halo5-Guardians_1.1.31695.21_x64_1_8wekyb3d8bbwe/Mount
```

The player chooses the parent location; there is no default drive or personal folder path. The package name is one directory.

| Observed input | Result |
| --- | --- |
| Manifest | `Halo5-Guardians`, package `1.1.31695.21` |
| Content version | `97230.18.02.27.2300-1` |
| `deploy/any` modules | 912; approximately 26.301 GiB |
| `deploy/x1` modules | 912; approximately 65.367 GiB |
| Module headers | All 1,824 surveyed modules identify as `mohd`, revision 23 |
| Header-declared item records | 370,066 across both platforms; includes resources and patch versions, not unique campaign assets |
| Header-declared compression blocks | 742,775 |
| Audio | 48 `.pck` files; approximately 5.706 GiB |
| Movies | 6 `.bk2` files; approximately 1.119 GiB |
| Map metadata | 54 `.mapinfo` files across the dump; this is not a count of supported missions |

These observations came from directory metadata, the manifest/version file, and module headers. Payloads were not exhaustively decompressed or verified. Input size does not establish output size or conversion support.

The existing `CampaignDumpValidator` recognizes the selected root or its `deploy` directory and performs structural checks. It has no persistent index or build pipeline. Extend root selection to accept `Mount`, a package directory containing `Mount`, or an extraction directory with a single recognizable package. Bound that search; show a choice when more than one package qualifies.

## 2. The player experience

Keep the plain WPF window and familiar controls. The setup flow becomes:

1. **Forge** — show installation status and version.
2. **Campaign files** — choose the extracted folder; show its detected version.
3. **Cache folder** — choose a destination; show available space and the build estimate when known.
4. **Build cache** — show the current stage, completed work, and Pause/Cancel controls.
5. **Ready** — show the supported content available in this cache, then offer launch once runtime integration is implemented and validated.

Default content is the currently supported campaign bundle. Initially target Osiris, its connecting cinematic, and Blue Team for conversion parity; this is a development target, not a promise that the new builder already supports them. Keep the supported-content list as versioned data. Index the whole dump so later missions can be added without a new importer or another full extraction. Offer available audio languages without silently dropping required voice banks.

Progress wording should describe real work: “Reading module tables — 410 / 1,824”, “Preparing textures — 208 / 630”, or “Checking generated files”. Discovery can be indeterminate until totals are known. Do not present a fabricated overall percentage or completion time.

On later opens, read the small cache manifest and status records. Do not rescan or hash the entire dump just to show the home screen. **Resume**, **Verify/repair**, **Change cache folder**, and **Open logs** are ordinary actions. A missing cache drive is “Cache folder unavailable”, not “Start again”.

## 3. Cache location and ownership

The chosen destination holds **all bulk data**, including the index, intermediate payloads, temporary work, generated modules, audio, movies and build logs. `%LOCALAPPDATA%/h5sololauncher` holds only small preferences and discovery information. There must be no hidden multi-gigabyte fallback to the system drive.

Create an application-owned child directory in the selected folder, or reopen a cache with a valid ownership marker. Remember its path and cache ID. Never adopt or recursively clean an unrelated directory. Reject nesting the cache inside the dump, the dump inside the cache, or either path inside the installed Forge package. Resolve linked ancestors before accepting locations or deleting owned work.

Initial support should cover user-writable local NTFS locations, including another drive. Validate creation, reading, renaming, free space and cache ownership before starting. Explain unsupported filesystems/network locations in the picker result. SSDs can improve preparation time, but an HDD must remain usable. Removable drives need disconnect/reconnect recovery.

Moving the cache is a managed copy-and-verify operation: stop builds and active game use, copy the owned cache, verify its manifests and artifacts, then change the saved location. Preserve the original until the new location is verified. Bulk records use relative paths so a different drive letter or username does not invalidate content identities.

### Forge access is a separate requirement

Desktop write access does not prove that Forge can read the same directory. The old implementation staged files under Forge's per-user `LocalState` and depended on inherited package access.

Build a `GameCacheAccess` adapter and test it early: a controlled Forge-side reader must successfully open a small probe in the selected cache location. The preferred result is direct, read-only runtime access to the chosen cache through narrowly scoped access for the actual package identity. Do not change the WindowsApps directory's ownership or permissions. If direct access cannot be made reliable, investigate a broker or explicit runtime staging; any required duplicate storage must be shown to the player and included in the estimate. Do not silently copy everything back to the system drive.

Custom cache storage can ship with indexing first. Custom-location campaign launch is a separate acceptance gate.

## 4. A small modular implementation

| Component | Responsibility |
| --- | --- |
| Existing WPF project | Views, view models, folder pickers, settings and copyable errors |
| `h5sololauncher.Core` | Domain records, build graph, compatibility decisions, cache contracts and validation rules; no WPF dependency |
| `h5sololauncher.Worker` | One packaged background executable hosting readers, cache storage, converters and Windows adapters |
| Native library, as needed | Ported format/shader and Forge integration routines behind versioned interfaces; built when distributing the app |
| Tests | Synthetic format fixtures, conversion vectors, cache lifecycle tests and integration harnesses |

Use namespaces and interfaces inside these projects before adding more assemblies. Constructor injection and a fixed converter registry are sufficient. No third-party plugin system or script orchestrator is needed.

The worker exposes typed operations such as `InspectSource`, `InspectDestination`, `IndexSource`, `PlanBuild`, `Build`, `Pause`, `Resume` and `Verify`. Events carry an operation ID, sequence number, stage, counts, bytes, recoverability and a structured error. WPF never infers state from log wording.

Use a versioned, current-user-restricted named pipe between the UI and worker; .NET provides [named-pipe IPC](https://learn.microsoft.com/en-us/dotnet/standard/io/pipe-operations). Authenticate the expected worker/session and reject protocol mismatches. The worker owns the job and cache lock. Closing the window defaults to cooperative pause at a checkpoint; “Keep preparing in the background” is an explicit option. Reopening the app reconnects to the existing job.

## 5. How the cache is built

| Stage | Work and durable result |
| --- | --- |
| Inspect | Resolve roots, versions, ownership, location capabilities and initial compatibility; save a source descriptor |
| Index | Read each module's header and tables once; index assets, resource links, stored ranges and patch layers; index map metadata and audio package directories |
| Acquire Forge compatibility data | Read accessible installed content, or collect required portable metadata through the controlled Forge adapter; record its provenance and exact compatibility identity |
| Resolve content | Select supported scenarios and language; compute required assets/resources, native reuse and unresolved references; save an explicit build plan |
| Convert | Stream selected source payloads into typed tag, texture, shader and audio converters; save verified reusable results |
| Assemble | Build shared banks and native-format modules, audio supplements, selected movies, catalogue and relative runtime mappings |
| Verify | Re-read outputs; validate payloads, identities, resource links, block integrity, source-layer coverage and declared dependencies |
| Publish | Commit a complete immutable generation, retaining the last usable generation until publication succeeds |

### Index once; extract only what is needed

Implement a `ModuleReader` for revision 23 input and the proven revision 27 format used by generated/native modules. Parse headers, 88-byte item records, string/resource tables and the revision-specific block tables. Use checked 64-bit arithmetic and explicit bounds throughout. Table records identify byte ranges; do not load whole modules into memory.

Decompress selected payloads in bounded buffers. Validate stored ranges, decompressed lengths and logical block coverage, rejecting overlaps and gaps. Port the existing zlib/raw-block behavior with independent fixtures. Treat unrecognized flags, schemas or oversized unsupported records as named failures, not guessed layouts.

Store the index in SQLite, including at least:

- `Sources` / `SourceFiles`: role, relative path, package/build identity, size, table digest and content-verification coverage.
- `Modules` / `Entries` / `Blocks` / `ResourceLinks`: platform, original layer, physical location and exact identities.
- `Dependencies` / `Scenarios` / `AudioEntries`: references, mission ordering, language and native availability.
- `Artifacts` / `BuildTasks` / `Generations`: recipe keys, verified outputs, checkpoints and publication state.

Physical entries are identified by source, relative module path and entry index. Logical asset keys retain group, tag ID, asset ID and checksum, together with platform/layer context. Names alone are not identities. Preserve all versions needed for original module reconstruction, even when dependency resolution selects an effective override.

Determine load order from validated metadata/format rules. The historical planner uses a numeric filename rank; treat that as behavior to verify, not a universal ordering rule. Resolve missing references by querying the full index rather than rescanning every module for each mission. Asset reference graphs may contain cycles; use visited sets and explicit cycle handling rather than assuming they are a task DAG. Permit platform aliases only through tested rules.

### Conversion rules

Reuse the proven algorithms by extracting them into typed modules:

- **Tags and resources:** validate native schemas, convert supported layouts, preserve unknown optional data only where a verified rule permits it, and fail on unknown required layouts.
- **Textures and geometry:** convert platform resource layouts using shared format rules, preserving resource ownership, mip/block layout and declared sizes.
- **Shaders:** translate only supported instruction forms, preserve native programs and reject conflicting bytes under the same bank/program identity. Construct shared banks from the effective dependency closure of the whole supported bundle.
- **Audio:** index the user's packages directly, account for required native banks and initialization dependencies, convert only the selected language's missing banks, and assemble a supplement from those inputs. An earlier supplement must be optional reuse, never a required seed.
- **Modules:** emit the expected game format and integrity blocks; preserve original layer order, resource links, stripped entries and load manifests. Validate section/block counts and logical payload equality after packing.
- **Catalogue and movies:** derive routes and successors from validated metadata; publish only fully accounted-for scenarios. Copy required movie bytes once where conversion is unnecessary.

Shader translation outputs and GPU validation are different artifacts. Key device validation by adapter/driver/runtime identity; a driver change should not invalidate unrelated tag, texture or audio conversion. The latest runtime report also identifies native bank request/lifetime behavior as necessary for direct starts. Correctly packed caches alone do not replace that runtime repair.

## 6. Reproducibility must replace the old hidden inputs

Read-only inspection confirmed these migration dependencies:

| Old implementation | Replacement required |
| --- | --- |
| `Plan-MissionImport.py` reads captured native global tables and a converted baseline | Forge content/metadata provider plus exact-identity lookup; empty conversion caches are valid inputs |
| `Convert-MissionTags.py` loads research schema/profile files and texture controls | Versioned, tested converter rules; acquire game-derived schemas from the selected inputs or controlled Forge collection |
| `Build-MissionShaders.py` loads native banks and compiler/device tools from research directories | Packaged converter/verification modules and reproducible native-bank acquisition |
| `Import-MissionAudio.py` requires an existing supplement and prebuilt package indices | Fresh package readers and a reproducible initial supplement |
| `Build-CampaignCatalogue.py` reads captured registry arrays and emits path-bearing C++ headers | Validated metadata reader/provider and relative runtime configuration consumed by prebuilt helpers |
| `ModuleIO.py` imports an earlier capture script | Standalone format reader with no dated-directory dependency |

During this inspection, listing a Forge global module succeeded but opening it for reading failed with access denied in the current standard-user session. Therefore, ordinary desktop reads of installed content cannot be assumed to work. Prefer them when available, but plan for a clearly explained, one-time **Preparing Forge data** step through a compatible, controlled Forge process. This may require the existing Continue interaction until a tested replacement exists.

Collected data must contain portable identities, schemas and bytes needed for conversion, never reusable process addresses or stale pointer tables. Key it to the relevant Forge version/content and collector version. The custom launch integration needed to start and collect from Forge is itself a portability gate; an installed Forge package alone does not establish that integration.

Compatibility rules and executable code can ship with the app. Game-derived content is generated from the player's supplied dump and installation. Historical artifacts remain development evidence and comparison fixtures. A release gate must demonstrate a build with those research artifacts unavailable.

## 7. Storage and repeatability

Proposed owned directory:

```text
h5sololauncher-cache/
  cache.json                       # ownership ID and format version
  catalog.sqlite                   # index, tasks and committed artifact records
  objects/<prefix>/<hash>.pack      # bundles of small intermediate payloads
  objects/<prefix>/<hash>.module    # large native-format outputs
  objects/<prefix>/<hash>.<type>    # audio/movie/other large outputs
  generations/<id>/manifest.json   # immutable relative mappings and provenance
  current.json                     # selected committed generation
  work/<build-id>/                 # bounded, resumable temporary work
  logs/                            # bounded structured and readable logs
```

Avoid a file for every extracted tag or shader. Bundle small intermediates into sealed packs with per-record hashes and offsets; begin benchmarking around a 256 MiB pack target. Keep final game modules as ordinary native-format files. Do not wrap files Forge expects to read in a new archive format. SQLite stores locations and metadata rather than all game bytes.

For each artifact, hash canonical inputs: actual required source bytes, table/layout identity, converter and schema versions, relevant Forge baseline, build options/language and dependency artifact identities. Normalize serialization and ordering. Absolute paths, job IDs, timestamps and enumeration order must not affect output identity. Distinguish an input/recipe key from the output content hash. Native container integrity checks and outer SHA-256 serve different purposes.

Version the catalogue schema, artifact format, conversion recipes and native interface independently so updates invalidate only affected results. Metadata migrations preserve a recoverable copy and the committed generations until the new reader is validated. Unknown newer formats produce an upgrade explanation; they do not trigger automatic cache deletion.

Full hashing of the unused 91.7 GiB module payload set is not a prerequisite for indexing. Hash required byte ranges while consuming them and record exactly what was verified. Header/table digests and timestamps do not prove all payload bytes are unchanged. On resume or rebuild, verify the required input ranges before trusting reused work; file size and modification time are discovery hints only. Reuse decoded data while hashing rather than repeatedly reading the same bytes in separate stages.

A published generation must declare all launch-time dependencies. The intended result is that launching supported cached content needs Forge and the cache, while the original dump is needed only to build, extend or repair it. Prove this with the dump disconnected before advertising that behavior. If an artifact still references the dump, the generation must report that dependency instead of claiming independence.

Normal startup uses a quick health check, not a claim that every byte was just rehashed. Full **Verify/repair** rehashes artifacts and repairs only affected work. Runtime readers must validate consumed data according to their integrity contract; define that contract before promising detection of unchanged-size corruption during a fast launch.

## 8. Efficient execution, pause and recovery

Start with one I/O lane per physical disk and a small, bounded CPU conversion queue. Drive letters can share a physical disk. If topology is unknown, use conservative concurrency. Group requests by module and byte offset; batch reads and output writes to reduce seeks. Increase independent SSD/device concurrency only after measurements. Do not control unrelated user processes as part of normal cache building.

Stream large resources, pool buffers, and cap in-flight decoded data. Start with a configurable intermediate-buffer budget around 512 MiB and measure actual peak working set. Stream index inserts in batches rather than materializing every entry as a large object graph or committing each row separately.

Use one SQLite writer and short transactions. WAL can support status readers while the worker writes, but it requires local shared-memory coordination and is unsuitable for network filesystems. Keep checkpoints bounded and use durable settings for committed work; pin and review the actual SQLite provider/version during implementation. See [SQLite WAL behavior](https://www.sqlite.org/wal.html) and [commit durability](https://www.sqlite.org/atomiccommit.html).

SQLite transactions do not atomically commit separate payload files. Use an explicit sequence: write an owned temporary file, flush it, verify it, rename it to its immutable destination on the same volume, then commit the referencing database records. A crash can leave an unreferenced object, which recovery can validate or remove. It must never leave a published manifest referencing an incomplete object. Reconcile the generation record and `current.json` after a crash; keep the previous committed generation usable.

Checkpoint at bounded packs or completed conversion tasks. Pause stops issuing work and lets current bounded work reach a checkpoint. Cancel and power loss preserve committed artifacts; incomplete tails are discarded or rebuilt. Store checkpoints during work, not only at the end of an entire multi-gigabyte stage. Implement cooperative pause in the worker rather than suspending Windows processes.

An exclusive cache build lock prevents competing writers. A running game leases its generation so cleanup cannot remove files in use. Retention removes only unreferenced objects owned by this cache. Keep normal logs bounded instead of producing a new research tree for every run.

Space planning counts additional unique intermediate data, generated runtime files, peak scratch, retained-generation growth and a reserve. Existing allocations are not counted twice. Report estimates as ranges until format-specific sizes are known, then refine them. Check free space during writes and pause before it is exhausted. Include any runtime staging or later pack compaction in peak requirements. The old approximately 7.35 GiB of mapped modules is evidence for a limited build, not a universal cache-size promise.

## 9. Errors the player can act on

Every error should contain a stable code, stage, short explanation, recovery action and expandable technical details. Useful cases include `DUMP_CHANGED`, `MODULE_INVALID`, `DEPENDENCY_MISSING`, `SCHEMA_UNSUPPORTED`, `SHADER_UNSUPPORTED`, `AUDIO_LANGUAGE_MISSING`, `FORGE_DATA_UNAVAILABLE`, `CACHE_NOT_WRITABLE`, `CACHE_NEEDS_SPACE`, `CACHE_OFFLINE`, `CACHE_IN_USE`, and `GAME_CACHE_ACCESS_FAILED`.

Example: “The cache drive is full. Free space on the selected drive, then press Resume. Completed work has been kept.”

Provide **Copy error**, **Open logs** and **Export bug report**. A report includes versions, operation/stage, input identities, relative failing asset references, timings and bounded logs. Redact user-specific paths by default and omit game payloads, saves and unrelated files. A cache error must never be reduced to a generic failed-script exit code.

## 10. Implementation order and proof

| Milestone | Deliverable | Required proof |
| --- | --- | --- |
| A — Cache location and indexing | WPF cache picker, owned-cache marker, settings migration, Core/Worker boundary and persistent module/audio/metadata index | Fresh dump indexes without old caches; selection survives restart; a second drive works; interrupted indexing resumes; no bulk output appears elsewhere |
| B — Compatibility and clean-input proof | Forge data acquisition, custom-location game read probe, dependency resolver, one representative conversion and pack round trip | Every input has reproducible provenance; standard-user failure modes are clear; no native capture directory or prior supplement is required |
| C — First complete campaign bundle | Shared typed converters, audio, shared banks, modules and catalogue for the initial supported scenarios | Empty-cache build from the fresh dump and Forge; no unresolved required references; output and bank ownership validation passes |
| D — Repeatability and repair | Deterministic keys, incremental builds, pause/reconnect, generation publication, relocation and bounded diagnostics | Two clean builds produce identical declared artifacts under the pinned toolchain; unchanged work is reused; corruption, interruption, disk-full and changed-source cases recover correctly |
| E — Runtime integration | Launch using the selected cache, with device validation and preserved native lifecycle rules | Direct starts, transitions and checkpoint recovery work together; required output is readable at another cache location; input dump can be unavailable when the manifest claims independence |

Milestone A shipped in v0.3. The conservative source dependency planner in v0.4 implements part of B; see [dependency planning](dependency-planning.md). Forge data acquisition, runtime cache access, validated effective-layer/platform selection and conversion are still required. The app keeps the distinction visible: **Indexed — conversion not built**, followed by a saved source plan.

Measure cold/warm indexing, source bytes read, cache hits, peak memory, file count and stage duration on the same HDD, on an SSD, and across separate devices. Use synthetic tests for parser failures and cache lifecycle; use locally supplied content for integration comparisons without committing game data. User testing covers the WPF UI and live game behavior; no computer-use automation is part of this plan.

## Evidence used

- Current app: `src/h5sololauncher/Services/CampaignDumpValidator.cs`, `Services/LauncherSettingsStore.cs`, `ViewModels/CampaignDumpViewModel.cs` and `MainWindow.xaml`.
- Fresh dump manifest, `version.txt`, directory inventory and all 1,824 module headers.
- Legacy conversion code: `haloexport/12_forge_re/20260912-next-mission/{Plan-MissionImport,Convert-MissionTags,Build-MissionShaders,Pack-Mission,Import-MissionAudio,Build-CampaignCatalogue,Build-SharedCampaignBanks}.py` and `IMPORT-WORKFLOW.md`.
- Legacy reader: `Osiris-Launcher/template/ModuleIO.py`.
- Later runtime evidence: `Osiris-Launcher/Desktop/NEXT-MISSION-REPORT.md`. This supersedes the older workflow's unresolved direct-start notes, while still limiting gameplay claims to the documented tests.
- Legacy research files are archived outside the repository. They are development references, never production dependencies.
