# Source dependency planning: v0.4.0

This stage turns the source index into a reproducible preparation plan. It reads
the supplied dump directly and has no runtime dependency on old converted caches,
native capture directories, research scripts or installed development tools.

## Using it

Open the existing indexed cache, choose a voice language under **Campaign
preparation**, and press **Check dependencies**. The built-in versioned content
recipe covers Osiris (`w3_halsey`), the connecting scene (`cin_030`) and Blue Team
(`w4_station`). The recipe contains relative content paths, not installation paths.

The app shows counts for checked tags, unmatched references, patch choices and
unresolved resources. **Show plan file** selects the full report in Explorer.
**Copy details** includes the plan identity, location and any operation error.
The existing **Pause** button applies to this operation too. Closing requests a
pause; rerunning validates the inputs and reuses completed dependency analyses.

The result is a source preparation plan. It does not claim conversion readiness
or a verified playable campaign. Forge compatibility remains an explicit step.

## Selection and reading rules

1. Require a complete v0.3 index, its matching cache ID and source package version.
   Acquire the same exclusive lock used by indexing. Verify the inventory and
   hashes of all indexed metadata tables before trusting candidate lookups.
2. Include every stored tag in the selected scenario modules across `any` and
   `x1`, retaining physical source paths, item indices and patch versions.
3. Decode each selected tag through bounded raw/zlib readers. Validate physical
   ranges, decompressed lengths and contiguous logical block coverage. Tags are
   capped at 64 MiB; resource payload conversion is deferred.
4. Parse the bounded `ucsh` dependency table and follow exact group/tag-ID/asset-ID
   references. Search the bundle's modules and globals first; if absent, query
   the complete index. Cycles terminate through visited physical entries.
5. Retain all matching candidates within that scope. Different checksums produce
   an explicit patch-selection issue. Filename order is used for deterministic
   traversal only; no filename ranking is asserted to be runtime load order.
6. Report missing exact identities and zero-size resource references. Do not adopt
   a same-name or same-tag-ID asset with another asset ID, invent platform aliases,
   or assume that Forge already supplies a dependency.
7. Preserve source-relative locations and resource links. Include the selected
   language and SFX package candidates, plus indexed movie candidates. Required
   audio banks and authored cinematic routes are not resolved by this stage.

This deliberately retains more versions than the eventual effective runtime
dependency set. The missing-reference count concerns exact source matches under
these rules; it does not prove missing game data. Native availability, generated
platform identities and original patch order need further evidence.

## Repeatability and storage

The original `catalog.sqlite` remains schema 2 and is read-only during planning.
The cache gains:

```text
analysis.sqlite          dependency analysis checkpoints, independent format 1
plans/<sha256>.json       immutable source plan with relative paths
plan-summary.json        small cache-owned summary of the last published plan
```

Every selected tag is reread and hashed before analysis reuse. Checkpoints use
the actual decoded payload SHA-256 and dependency-reader version; timestamps,
tag IDs and declared checksums alone are insufficient. No decoded tag payloads
are saved as loose files. Bulk output and temporary plan files remain in the
selected cache. Shared logs are bounded and contain no game payloads.

The plan ID is SHA-256 of deterministic JSON containing recipe/rule versions,
source metadata fingerprints, physical inputs, decoded tag hashes, dependencies
and issues. Absolute roots, cache IDs, timestamps and reuse counters do not affect
the ID. A plan is flushed and renamed before the small summary is replaced;
pause or failure preserves the previous published plan. Opening the app reads
the small summary, checking its source fingerprint and rule/recipe versions.
It does not rehash the entire plan or all payloads at startup.

An unchanged replan verifies an existing plan's hash before reusing its file.
Changed source metadata requires reindexing. A changed tag payload with the same
size and timestamp is read again and changes the plan ID. Future conversion must
still verify the bytes it consumes; this plan does not certify unread resources.

Old plans and analyses are retained. Automatic history cleanup, garbage collection,
effective runtime selection and converted-generation publication are future work.

## Local validation

The fresh supplied `1.1.31695.21` dump produced the following English plan:

| Result | Count |
| --- | ---: |
| Root campaign modules, including retained patch layers | 114 |
| Modules from which stored tags were read | 57 |
| Stored tags decoded and hashed | 42,829 |
| Dependency references, deduplicated within each tag | 196,433 |
| Resource links retained | 60,452 |
| Stored tag bytes read | 228,277,363 |
| Logical tag bytes processed, one tag at a time | 1,062,703,757 |
| Exact references without a stored source match | 6,369 |
| Identities requiring patch selection | 2,567 |
| Required zero-size resource records | 80 |
| Tag format/decompression failures | 0 |
| Plan JSON size | 39,817,023 bytes |
| Dependency checkpoint database size | 22,384,640 bytes |

The debug run and published self-contained worker produced the same plan ID:
`4EC5DB835A7C2F683727E85B0A1B0AAB56126BFE453951E8F795DFDB52026C97`.
The published rerun reused all 42,829 dependency analyses. It still read and hashed
the selected source payloads, as required for safe reuse.

79 automated tests passed, including the published worker's index and plan pipe
operations, pause/resume, worker recovery, cyclic references, missing identities,
patch alternatives, resource diagnostics, deterministic output across relocated
fixtures, same-timestamp source changes and view-model operation gating. The
WPF window and live game behavior remain for the user's manual testing.

## Next gate

Acquire Forge compatibility metadata from the user's actual installation through
a reproducible adapter. Use that evidence to resolve native availability, platform
identities and effective patch order, then implement and round-trip one supported
conversion. Verify that Forge can read the chosen cache location before integrating
campaign launch. The planner's remaining-step list keeps these requirements visible.
