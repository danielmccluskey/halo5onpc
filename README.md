# Halo 5 Campaign on PC

[![Join the Discord](https://img.shields.io/badge/Join_the_Discord-5865F2?logo=discord&logoColor=white)](https://discord.com/widget?id=1481242539930419243&theme=dark) [![Sponsor me](https://img.shields.io/badge/Sponsor_me-ea4aaa?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/danielmccluskey)

A small hobby project to get the **Halo 5: Guardians campaign running on PC** through the existing Halo 5: Forge PC build.

**All 15 missions are now fully playable.** A few kinks to iron out, but it's getting there.

I'm mainly making this because I want to speedrun Halo 5 on PC. That's pretty much the goal. No plans for multiplayer, matchmaking or the rest of the Xbox services.

The Discord is for help and support with the tool. **No sharing game files, prepared caches or links to download them. Please don't ask for them either.**

Sponsoring supports me personally as a developer. It doesn't get you game files, private builds, priority support or any other special treatment.

## Outstanding bugs
- Lots! Still a work in progress
- Checkpoint reverts not working.
- Launcher doesn't recognize the game if it is on a different drive
- Lots of crashes!

Find a bug? Please report it on the [GitHub issues page](https://github.com/danielmccluskey/halo5onpc/issues).

## What you need

- A Windows x64 PC and a local NTFS drive for the cache.
- [Halo 5: Forge Bundle from the Microsoft Store](https://apps.microsoft.com/detail/9nblggh4v0fr), including the Halo companion app. Tested with Forge **1.194.6192.2**.
- Your own extracted Halo 5: Guardians game files, tested with **1.1.31695.21**, or a complete compatible launcher cache.

Game files are not provided. English (US) is currently supported.

## Getting started

1. Download the launcher from [Releases](https://github.com/danielmccluskey/halo5onpc/releases) and extract the ZIP.
2. Open `h5sololauncher.exe`, select your extracted game files and a cache destination, then click **Prepare & play**.
3. In **Solo**, choose a mission and play.

**Already have a complete cache?** Select it and click **Play**. You don't need the original game dump to play from a complete cache.

**Missing missions in an older cache?** Select your extracted game files and use **Options and diagnostics > Prepare cache only**. Updating the launcher alone won't add the missing mission files.

## Preview

An older video of the first two missions running:

[![Halo 5 Campaign on PC - First two missions](https://img.youtube.com/vi/g_5H8P4LFNY/maxresdefault.jpg)](https://www.youtube.com/watch?v=g_5H8P4LFNY)

## Fair warning

A lot of this is vibe coded. I'm not going to pretend I understand all of it, and things will probably break.

It works on my PC, though!

## This isn't Halo 5: Reforged

This is a separate hobby project, **not Halo 5: Reforged**. I'm just focused on getting the original campaign working as well as I can.

## Game files

**Halo 5 game files are not included in this repository and will not be distributed by this project.**

You'll need to provide your own copy of the required campaign data. Please don't open issues asking me to upload or provide it.

## Support me

[![Sponsor me on GitHub](https://img.shields.io/badge/Sponsor_me-GitHub-ea4aaa?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/danielmccluskey)

Sponsorship is completely optional and supports me personally as a developer. It isn't payment for Halo 5 or access to any game files.

There are **no sponsor-only builds, early access, priority support or fixes, or any other special treatment**. Everyone gets the same public releases, and sponsors still need to provide their own game files.

## Bugs and contributing

If something breaks, open an issue with what you were doing and the output from **Copy details** in the launcher.

If you know your way around Halo 5, Forge or its file formats, help is always welcome.

[Launcher and build instructions](src/h5sololauncher/README.md) · [Testing notes](docs/playable-implementation.md) · [Campaign preparation](docs/full-campaign-implementation.md)

## Halo Studios

If you're reading this, I'd happily work on an official Halo 5 PC port for free. Give me a shout. Would save me doing it the awkward way.

This is an unofficial, non-commercial fan project, unaffiliated with Microsoft, Xbox, 343 Industries or Halo Studios.