# Source publication boundary

Commit launcher source, tests, build definitions and documentation. Do not upload
campaign dumps, generated playable caches, installed game files, player saves,
diagnostic captures or local published binaries. Build and validation artifacts
remain ignored. Ignore rules do not protect files added with `git add -f`.

## Cache generation recipes

`completion-ui-recipe.json` and `display-ui-recipe.json` are included in the
source and embedded in release builds. They provide the script edits needed for
mission completion and display settings during cache generation. Their inclusion
has been approved by the project owner.

Both recipes are required build inputs. Tests check that they are embedded, and
the release workflow checks that neither file is missing. Source and release
builds support preparing a cache from the player's extracted dump as well as
playing an existing complete cache without a dump.

A playable cache contains game-derived assets and is not part of the source release.

## Other embedded data and dependencies

- `Bundles.json` describes supported relative campaign paths.
- `layout-profiles.json` stores numeric layouts and schema identities.
- `texture-addresses.json.gz` stores numeric address calculations, not textures.
- `audio-profiles.json` stores bounded conversion edits and hashes, not audio
  media. The developer derivation tool documents the input-driven process.
- The vendored DXIL checksum implementation retains its upstream source reference
  and license in `src/h5sololauncher.ForgeReader/third_party/dxilhash`.

These descriptions are a content inventory, not a blanket rights clearance.
No project-wide license has been chosen on the owner's behalf. Decide the license
for original project code before presenting this as a licensed open-source release.

Before staging, inspect `git status --short --untracked-files=all` and
`git diff --cached --stat`. Only source publication has been reviewed here; do not
assume a local release folder or cache is suitable for redistribution.

## Automated builds

Pushes to `main` run `.github/workflows/release.yml` on Windows, build the native
components, run native and managed tests, and publish a self-contained x64 ZIP
with a SHA-256 checksum as a GitHub prerelease. The workflow can also be started
manually on `main`. Each run/attempt gets a unique tag tied to its exact commit.

The workflow includes both cache generation recipes. It uses GitHub's automatic
token with release write permission; no personal access token or game installation
is needed. Nothing is released if build or tests fail.
