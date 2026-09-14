# Conversion inputs: v0.7.0

This stage creates the persistent source-tag cache that subsequent converters can
read. It uses the selected extracted dump, its existing dependency plan and the
saved Forge comparison. It needs no old converted files, research folders,
scripts, running game or new Forge collection.

## Using it

After **Check dependencies** and **Prepare Forge data**, press **Build conversion
inputs / resume**. Keep the existing cache selected; v0.3 indexes, v0.4 plans and
v0.5/v0.6 Forge reports remain usable. The new section shows progress and has its
own **Pause** button. Closing also requests a pause and waits for the worker.

The app shows a conservative tag-data estimate before starting. Everything is
written inside the chosen cache. **Show input manifest** opens the location of
the complete manifest; **Copy details** includes the manifest ID, counts, input
identities, operation errors and relevant paths.

**Tag inputs cached** means verified, decoded source tag bytes are available for
conversion. It does not mean game modules have been produced or the campaign is
ready to launch. Resource payloads, textures, geometry, shaders and audio still
need their converters. Keep the original dump for those stages.

## Content and boundaries

The builder verifies the immutable plan, matching source index and saved Forge
report, then checks the dump inventory and every indexed metadata digest. It
reads the planned physical tags grouped by module, verifies identity, checksum,
name, decoded length and actual SHA-256 against the plan, including on resume.
Changing bytes without changing a timestamp cannot silently reuse a stale plan.

Identical tag bytes share one stored payload. All physical tags and patch
versions remain represented by the original plan, which maps each physical
module/item to its payload hash. This deduplication never combines resource
ownership or selects a runtime patch order. Resource links remain in the plan
and source catalog; their bytes have not been cached by this stage.

The bounded `ucsh` reader preserves named dependencies, the source schema hash
and root structure GUIDs. Header/table/string and aggregate payload lengths are
checked, including strict UTF-8 decoding and bounded names. This is source
header evidence, not validation of full schemas or Forge compatibility.

The manifest includes evidence for every previously missing identity. Potential
generated platform equivalents require the same group/tag ID already recorded
in the Forge report, a known generated-name prefix on the native candidate and
agreement between normalized dependency names. Name conflicts and missing names
remain explicit. Normalization removes only the documented research prefixes
`__chore/{pc,x1,gen}__/`, known platform markers, and the file extension.
**PotentialPlatformMatch never rewrites an asset ID or resolves an issue.** The
existing 6,369 unmatched identities, patch choices and unresolved resources are
not subtracted from the original plan. Native schema and runtime behavior still
need evidence before any replacement can be enabled.

The format interpretation was checked against the earlier `TagMeta.py` and
`Plan-MissionImport.py` readers in the archived research. Those files are
development references only. All runtime logic is typed C# in Core, invoked
through the packaged worker's `inputs` pipe operation.

## Storage and reader contract

```text
inputs/
  packs/<sha256>.pack        decoded source tag bytes, with per-record hashes
  batches/<sha256>.json      payload offsets, lengths, names and schema headers
  checkpoints/<key>.json     resumable pointer to a completed batch
  manifests/<sha256>.json    portable input manifest bound to plan/Forge catalog
  summary.json              cache-owned pointer to the last complete manifest
  work/<guid>.tmp            bounded unpublished work
```

Pack format 1 begins with eight bytes: `H5IP`, then little-endian version 1.
Each record contains a little-endian signed 32-bit length, 32 raw SHA-256 bytes,
then exactly that many uncompressed payload bytes. The matching batch stores
the payload offset, length, hash and parsed metadata. A pack normally targets
32 MiB; a single larger tag can occupy its own pack, bounded by the existing
64 MiB tag limit. No file is created for every individual tag.

`InputCacheStore.ReadManifest` and `ReadBatch` verify the metadata's content
hashes and version/binding fields. `InputPack.Read` is the converter-facing
reader: it checks the pack header, physical range, stored record identity and
the full SHA-256 of the bytes returned. A converter must use those verified
bytes and retain the physical source/resource mapping from the plan.

The plan's sorted first occurrences determine pack membership. Pack checkpoints
are keyed by rules, ordered payload hashes and lengths. Content identities omit
absolute paths, cache IDs, timestamps and reuse counters. The final manifest
binds its source fingerprint, plan ID, Forge package/catalog, bundle, language,
batches and source header evidence. An unchanged run or relocated equivalent
inputs produce the same identities.

The builder uses the shared exclusive cache lease and one I/O lane. Only the
current tag, its compressed block and bounded verification buffers are needed
for payload processing; the existing plan and parsed metadata also occupy
memory. It does not queue the full dump or cache every resource in memory.

## Recovery

Before committing a pack, the worker flushes it, rereads every record and checks
its payload hash and whole-pack hash, then renames it to its content address.
It publishes the batch metadata before replacing its small checkpoint pointer.
The complete manifest and summary are published last. Failed or paused builds
leave the previous complete summary in place.

Resume verifies existing pack contents and metadata before reuse. Missing or
damaged packs/checkpoints are rebuilt from verified source bytes; damaged source
bytes instead stop with `DUMP_CHANGED`. Reusing a pack avoids output writes and
metadata parsing. Build/resume still decodes and hashes every planned physical
source payload to validate the saved plan. Future converters can read the packs
without repeating extraction.

A normal pause discards only the active temporary pack. After a worker crash,
the next cache lease removes only GUID-named `.tmp` files directly inside this
stage's owned work directory. Unrelated files are preserved. Sealed orphaned
objects and older manifests are retained; general history pruning is deferred.

The worker checks estimated additional payload space before writing and keeps a
256 MiB reserve, with further checks during writes. Errors identify the stage,
file and recovery action; logs share the existing bounded `logs/index.log`.
Opening the app verifies the manifest but does not rehash all pack bytes. Pack
integrity is checked when building/resuming or consuming a payload.

## Local validation

The supplied extracted `1.1.31695.21` dump and the user's v0.6 Forge comparison
produced:

| Result | Count |
| --- | ---: |
| Planned physical tags verified | 42,829 |
| Unique tag payloads | 42,397 |
| Decoded payload bytes cached | 1,000,522,696 (954.2 MiB) |
| Pack files | 31 |
| Source payload bytes read | 228,277,363 |
| Distinct group/root/schema headers | 141 |
| Tags lacking a single root | 0 |
| Missing identities with recovered dependency names | 6,369 |
| Potential generated Forge equivalents supported by names | 4,269 |
| Patch choices retained | 2,567 |
| Unresolved resource records retained | 80 |

Manifest: `3F11FBDE4074A4CD58AB4A6041F47927B473DB988C5C1A3E8B9D64667E85AB7C`.

The self-contained v0.7 worker repeated the real build, verified and reused all
31 packs, reread every planned source tag, and produced that same manifest ID.
The full managed suite passed 137 tests; 10 worker-related tests also passed
with the published worker configured. Both native DLL hashes match the
user-tested v0.6 release; this stage did not change native startup or collection.

Tests cover payload round trips, deduplication, retained patch versions, bounded
packs, deterministic relocated fixtures, corruption repair, source changes with
unchanged timestamps, cancellation before and after a sealed pack, crash work
cleanup, required report/plan validation, named-reference ambiguity, worker IPC,
and WPF operation gating/close behavior. Live WPF testing remains with the user.
This stage performs no game startup, injection or computer-use automation.
