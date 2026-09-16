# Halo 5 Campaign on PC

A small hobby project to get the **Halo 5: Guardians campaign running on PC** using the existing Halo 5: Forge PC build.

## What you need

- A Windows x64 PC and a local NTFS drive for the cache.
- [Halo 5: Forge Bundle from the Microsoft Store](https://apps.microsoft.com/detail/9nblggh4v0fr), including the Halo companion app. Tested with Forge **1.194.6192.2**.
- Your own extracted Halo 5: Guardians game files, tested with **1.1.31695.21**, or a complete compatible launcher cache. Game files are not provided.

## Getting started

1. Download a build from this repository's **Releases** and extract the ZIP.
2. Open `h5sololauncher.exe`. Choose your extracted dump and a cache destination, then click **Prepare & play**. If you already have a complete cache, select it and click **Play**.
3. In Solo, choose an available mission and start your mission. New caches include all fifteen missions through Guardians.

A complete cache does not need the original dump to play. English (US) is currently supported.

To add the remaining campaign missions to an existing cache, select the extracted dump and use **Options and diagnostics > Prepare cache only**. Updating the launcher alone does not add mission assets. See the [Full-campaign preparation record](docs/full-campaign-implementation.md).

## Preview and status

[![Halo 5 Campaign on PC - First two missions](https://img.youtube.com/vi/g_5H8P4LFNY/maxresdefault.jpg)](https://www.youtube.com/watch?v=g_5H8P4LFNY)

The first two campaign missions are verified in h5sololauncher:

* **Osiris**
* **Blue Team**

There is still a lot to work out, but they're properly running and playable, which is further than I expected to get when I started messing with this. Version 0.12.0 prepares the campaign through Guardians; the additional missions are awaiting bulk gameplay testing.

## Fair warning

Just being honest here, a lot of the code in this project is vibe coded! I'm nowhere near smart enough, nor am I pretending to understand half of it!

So if you try this and it breaks, don't be surprised. But it works on my PC!

## Why?

I'm mainly making this because I want to **speedrun Halo 5 on PC**.

That's pretty much the goal.

Halo 5 is the awkward one if you want to play or speedrun the Halo campaigns on PC, and I wanted to see if I could get the original campaign running here.

I'm not particularly interested in bringing across multiplayer, matchmaking or the rest of the Xbox services. For now I just want to get the campaign working as well as I can.

## This isn't Halo 5: Reforged

This project is **not Halo 5: Reforged** and isn't connected to that project in any way.

This is a completely separate hobby project that I started because I wanted to see if I could get the original Halo 5 campaign running on PC.

## Game files

**Halo 5 game files are not included in this repository and will not be distributed by this project.**

You'll need to provide your own copy of the required campaign data. Please don't open issues asking me to upload or provide the game files.

## Contributing

It's still very experimental, so I'm not really sure what contributing will look like yet.

If you know something useful about Halo 5, Halo 5: Forge, its file formats or the PC build, feel free to open an issue.

For bug reports, use **Copy details** in the launcher and include what you were doing when the problem happened.

[Launcher and build instructions](src/h5sololauncher/README.md) · [Testing status](docs/playable-implementation.md)

This is an unofficial, non-commercial fan project, unaffiliated with Microsoft, Xbox, 343 Industries or Halo Studios.
