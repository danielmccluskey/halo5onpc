# Portable playback caches (0.9)

Choose an existing complete cache and click **Play**. The campaign dump is
optional for playback. Building, repairing or changing the supported game
version still requires the extracted dump. The destination PC needs the
compatible installed Forge and Halo companion app, Windows x64, and a writable
local NTFS cache folder. Copy the entire cache folder for now.

## Implementation

`PlayableCache` owns import, publication, validation, cached mission registration
and runtime path resolution. `playable.json` points to a SHA-256-addressed
`inputs/playable-manifests/<id>.json`. That manifest contains the generated file
list, mission metadata identities, source/target module lists, movie description,
language, Forge package identity and path-independent configuration IDs. All
runtime file references are relative to the selected cache.

Cache selection validates configuration and mission metadata hashes plus payload
existence and sizes. The existing native runtime verifies full payload hashes
inside Forge before activation. The cache remains tied to a compatible Forge
version and launcher runtime format; copying does not make it universal across
arbitrary game builds.

Playback creates content and movie configurations using the current cache root,
grants the Forge package read access, imports cached mapinfo files, checks Forge's
returned metadata against the manifest, then runs the existing startup sequence.
Neither Play nor monitoring opens the dump or source index. The movie's previous
absolute path is discarded during legacy import.

Generation now stages both campaign variants before publishing the playable
manifest, including when choosing **Prepare cache only**. A complete 0.8 cache
can be imported from its existing hashed preparation manifests and cached files.
Legacy variant selection requires exactly one hash-valid candidate for each
variant; missing or ambiguous variants require rebuilding. Publication and import
use the cache lock and atomic manifest writes.

Once sealed, playback does not need the old preparation manifests, source plan,
SQLite index, or conversion packs. These are retained to support repairs and
development; this release does not expose a cache-pruning operation.

The WPF model separates Forge/cache readiness from source readiness. A complete
cache enables Play with an empty source setting. Preparing, indexing and language
changes require a recognized dump. Invalid caches retain an actionable message
and diagnostic code; they cannot silently trigger preparation without a dump.

## Validation

- 188 managed tests passed, including relocation with no dump/index/legacy
  manifests; missing payload, changed metadata and configuration; path rejection;
  Forge mismatch; native registry result mismatch; legacy import; variant repair;
  source-independent WPF selection, restore and Play; and prepare-only sealing.
- The published 0.9 worker imported and checked the existing real cache without
  a source argument: 119 runtime file entries, English (US), `DumpRequired=false`.
  Its regenerated content configuration matched the previously tested runtime
  configuration hash. Evidence: `artifacts/live-validation/portable-cache-0.9.json`.
- No game session or saves were changed during these portability checks. This
  does not add gameplay validation beyond the [0.8 evidence](playable-implementation.md):
  controls, Blue Team and onward transition still need live verification.

Developer preflight: `h5sololauncher.Worker.exe --check-cache <cache> <forge-root>
<package-full-name>`. It imports if necessary and resolves paths without starting
Forge. `--play-cache` accepts the same arguments and starts playback. These are
diagnostic entry points; normal users use the WPF app.
