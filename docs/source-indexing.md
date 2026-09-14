# Source indexing: v0.3.0

This is the first cache implementation for h5sololauncher. Players select an
extracted campaign dump and a cache destination, then index the source with a
single packaged worker. The output is a persistent metadata catalog for future
conversion stages. It does not yet produce playable game modules.

## Storage contract

```text
<chosen destination>/h5sololauncher-cache/
  cache.json          ownership marker, format 1, generated cache ID
  catalog.sqlite      SQLite application ID 0x48354958, schema 2
  index.lock          exclusive writer lease while indexing
  logs/
    index.log
    index.previous.log
```

SQLite may create WAL/SHM sidecars here while open. All bulk work stays under the
selected cache. The launcher requires local NTFS, checks ownership and write/read/
rename access, rejects linked ancestors and source/package overlap, and checks
available space before initialization and each file checkpoint. Those checks are
metadata work budgets, not a prediction of future converted cache size.

The catalog stores:

| Table | Contents |
| --- | --- |
| `Meta` | Cache ownership ID, package identity/version key and current summary |
| `Files` | Source-relative path, kind, length, timestamp, metadata SHA-256, raw header and inventory generation |
| `Entries` | Module item names, exact ID/checksum fields, parent/resource/block links, sizes, offsets, payload state and original 88-byte records |
| `Blocks` | Stored/logical ranges, compression flags and original block records |
| `Resources` | Module resource table links |
| `AudioEntries` | Bank/media/external IDs, language IDs and source ranges |

The raw audio header includes its language directory. CMS files are small raw
metadata records; semantic campaign route parsing is future work. Movie records
identify their source files without copying or hashing movie payloads.

Each source file is replaced in one SQLite transaction. Reuse requires matching
kind, file length and SHA-256 of the metadata bytes. Timestamps alone never allow
module/audio metadata reuse. A final inventory and package identity check must
pass before the index is marked complete; only then are absent files removed.
Unchanged reruns still read metadata tables to validate their digests, but skip
the much larger row insertion work. They do not hash the full game dump.

Cancellation rolls back the active file transaction. Completed transactions stay
available after pause, process termination or an error. Resume creates a new
inventory generation and validates prior records before reusing them. Unknown
database schemas and mismatched package versions require another cache folder;
the first release has no database migration or managed cache relocation UI.

## Format and integrity boundaries

Readers cover revision-23 module tables and the revision-27 layout. The full
supplied campaign corpus is revision 23; revision 27 has synthetic fixture
coverage. Audio support covers the observed AKPK version-1 package directories.
Reads are bounded to 128 MiB of metadata per file and do not decompress payloads.

Zero aggregate resource sizes can coexist with section sizes and block tables.
Those raw fields are retained. If a zero-size record points beyond the local
payload area, its `PayloadState` is `Unresolved`. Other entries are `Unverified`:
passing range checks is not payload verification. Invalid table bounds, links,
logical ranges and nonzero payload declarations outside the file stop indexing
with the source path and a copyable error. Uncompressed blocks use their logical
size for physical range checks.

The observed unresolved references are not proof of a damaged export or proof
that a matching platform payload exists. A future resolver must establish the
correct input by identity and dependency rules. Conversion must never read an
unresolved range or treat an indexed record as a verified game artifact.

## Validation on 13 September 2026

The indexer was exercised directly against the supplied extracted package
`Halo5-Guardians_1.1.31695.21_x64_1_8wekyb3d8bbwe/Mount`.

| Full index result | Observed |
| --- | ---: |
| Source files indexed | 1,956 |
| Modules | 1,824 |
| Module item records | 370,066 |
| Compression blocks | 742,775 |
| Audio records | 135,675 |
| Metadata bytes read/indexed | 93,312,826 (about 89 MiB) |
| Source bytes represented | 105,755,751,548 |
| SQLite file after indexing | 360,919,040 bytes (about 344 MiB) |
| Unresolved payload records | 88 across 37 `any` modules |

The initial index took approximately 38 seconds on the development machine,
with previously read source metadata in the OS cache. This is not a cold-disk
benchmark or a user-facing time estimate. A subsequent run reused all 1,956 file
checkpoints and kept the same record counts. No game module, audio or movie
payload was copied into the index directory.

Automated tests cover damaged/truncated tables, revision variants, same-size and
same-timestamp metadata changes, source changes during a run, file rollback,
cache locking/space failures, ownership/path rejection, root discovery, settings
migration, view-model job gating, real named-pipe indexing, pause/resume and
recovery after forcibly stopping a worker. The published self-contained worker
is exercised through the same integration tests. WPF interaction and game launch
have not been tested automatically.

## Next implementation

The source dependency planner is implemented in v0.4; see
[dependency planning](dependency-planning.md). Bring across conversion stages
behind Core/worker interfaces next. Resolve the zero-size/platform records
explicitly. Verify Forge input acquisition and
Forge's ability to read the chosen cache location before promising custom-folder
campaign launch. These remain separate acceptance gates in the cache build plan.
