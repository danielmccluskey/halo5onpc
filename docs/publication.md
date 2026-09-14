# Source publication boundary

Commit launcher source, tests, build definitions and documentation. Do not upload
campaign dumps, generated playable caches, installed game files, player saves,
diagnostic captures or local published binaries. Build and validation artifacts
remain ignored. Ignore rules do not protect files added with `git add -f`.

## UI recipe restriction

`completion-ui-recipe.json` and `display-ui-recipe.json` contain literal script
byte edits whose redistribution provenance has not been established. They remain
local and are excluded from Git. Do not attach the current local binaries to a
GitHub release: those binaries embed these recipes.

The project conditionally embeds these files when present. A fresh source clone
builds without them and can use a complete compatible playable cache. Preparing
a new cache reaches a clear `UI_RECIPE_NOT_INCLUDED` error. Full source-only
preparation remains pending replacement of these recipes with independently
documented transformations or resolution of their provenance. This is a known
distribution limitation, not a completed full-feature release.

Existing local builds and recipes are retained. A playable cache itself contains
game-derived assets and is not part of the source release.

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

The workflow refuses to publish if either excluded UI recipe is present. Its
ZIP and release notes explicitly describe the existing-cache-only limitation.
It uses GitHub's automatic token with release write permission; no personal
access token or game installation is needed. Nothing is released if build or
tests fail. These CI-built packages are distinct from local builds that embed
the excluded recipes.
