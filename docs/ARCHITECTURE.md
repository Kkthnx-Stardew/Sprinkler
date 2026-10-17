# Architecture and the choices behind it

This page explains how Sprinkler is built, why, and what each alternative cost. Numbers come from builds measured on one Windows 11 machine with the same 36-mod folder. Nothing here was measured on Linux, because no Linux machine was available while writing it. That is stated again at the end.

## The shape

```
 Sprinkler (Avalonia 12, native window)          Sprinkler.Core (.NET 9 library)
 screens, view models, icons, themes   ----->    scan, check, download, install, undo,
 listens for UpdateService.Changed               platform code, pictures, history
```

- **Core** (`src/Sprinkler.Core`): everything that matters, with no UI code. Scanning mods, version rules, the SMAPI, Nexus, CurseForge and GitHub clients, the installer with backup and undo, mod pictures, history. `UpdateService` is the one object the window talks to. It builds a typed `StateSnapshot` under a lock and raises `Changed`.
- **Window** (`src/Sprinkler`): Avalonia screens bound to view models. The main view model takes a snapshot at most ten times a second, updates stable child objects in place (so focus, scroll and open sections survive every refresh), and re-checks only things the service cannot know about (the game being closed) every few seconds.
- **Platform layer**: `Platform`, `GameLocator`, `GameProcess`, `SecretStore`, `Nxm`, `SingleInstance` and `LinuxShortcut` hold every difference between Windows, Linux and macOS. The rest of the code does not check the operating system.
- **Second copies**: a second launch talks to the first through a local pipe, open to the current user only, to bring the window forward or hand over a Nexus link.

## Why a native Avalonia window

Sprinkler first shipped as a local web page shown in a Chromium window. It worked, but a native window won on every measure that mattered, and the person using it never has to wonder about a browser:

| | Local web page in a Chromium window | Avalonia (current) |
| --- | --- | --- |
| Memory with a real mod folder | about 400 MB in total, almost all the browser | about 145 MB private |
| Download | about 32 MB | about 24 MB, trimmed and compressed |
| Needs a browser | Yes (a tab fallback worked, but it was not an app) | No |
| Idle CPU | near zero | near zero |
| Looks the same on every system | Yes | Yes |
| Linux | A Chromium-based browser had to exist | One self-contained file |

An untrimmed self-contained Avalonia file is about 100 MB, because it carries the whole .NET runtime. The 24 MB figure needs trimming and single-file compression (both set in `Sprinkler.csproj`). Third-party summaries that quote "about 20 MB" for Avalonia were not measured on a real app.

The other options were ruled out on evidence:

- **WPF / WinForms**: Windows only.
- **WebView2 host**: Windows only, and about 250 to 275 MB in a prototype.
- **Tauri / Photino (system web view)**: on Linux they use WebKitGTK. Tauri's own documentation has a page of [Linux graphics issues](https://v2.tauri.app/develop/debug/linux-graphics/) (blank windows, flicker and crashes on NVIDIA under Wayland, fixed by environment variables), so shipping it would mean shipping known rendering risks that cannot be tested here.
- **Electron**: a full Chromium per app, the heaviest option.

[Stardrop](https://github.com/blackrosepetals/stardrop), a popular Stardew mod manager, also uses Avalonia on Windows, Linux and macOS, which is a good sign for the Linux case.

## Themes and accessibility

The palette (loam, harvest gold, parchment, sprout, barn red) has a light and a dark variant in `Theme/Palette.axaml`. `ContrastTests` reads that file and checks that every text and background pair used by the app meets WCAG AA (4.5 : 1) in both themes, so a colour change that breaks readability fails the build. Icons are drawn as vector paths and take the colour of the text around them.

## Updating itself

`SelfUpdate` finds a newer release in GitHub's release list (pre-releases only for someone already on one), picks the file for this system, and refuses any download link that is not under this project's own release path. It then reads the small signed SHA-256 list, checks its ECDSA P-256 signature against the public key compiled into the app, finds the archive's hash in the list, and downloads the archive with a hard size limit while hashing it. Only after both checks does it take the single program file out of the zip or tar.gz (top level or one folder down, nothing with `..`), write it beside the running program, check its header, and start it once with `--version` to confirm it reports the expected version. Replacing the running program is the platform-specific part: Windows lets a running program be renamed but not overwritten, so the old one becomes `.old` (put back if the move fails) and is removed at the next start; Linux renames the new file over the old one. The restart passes `--wait-for <pid>` so the new copy does not mistake the closing one for a second instance. Without a signature, a replaceable program, or a build for the system, the pill only opens the release page. The Windows path was run end to end with the published single file against a local server and the real key; the Linux path is covered by tests of the unpacking and the swap but has not been run on a Linux desktop.

## Languages

English is the source. Each language is a flat JSON file of keys and texts in `Sprinkler.Core/Locales`, embedded in the program, so there is nothing to install and no globalization data to ship (the app runs with invariant globalization to stay small). `Loc` picks the language from the setting or, for "same as my system", from the environment variables, the Windows user locale or the .NET culture, and shows English for any key a language lacks. Screens bind to keys through a `{loc:T key}` markup extension that listens to the language, so switching redraws everything without restarting. Counted phrases use CLDR plural categories per language (one and other, one few and many, or other only), and dates use a pattern from the file with numbers instead of month names. `LocaleTests` checks every file for missing keys, unknown keys, placeholders that differ from English, missing plural forms, empty texts and em dashes. [TRANSLATING.md](TRANSLATING.md) explains the format. CJK and Thai text relies on a system font, because the bundled Inter font does not cover those scripts.

## Profiles

A profile is a list of mod IDs. Switching renames mod folders by adding or removing the leading dot, which is the rule SMAPI itself uses to skip a mod. That choice has three effects: nothing is moved out of the Mods folder, nothing needs special permissions (no links, no elevated rights), and switching back is exact. Before a switch it turns on whatever the chosen mods require, and anything a profile lists but is not installed is fed into the same missing-mod flow that the update check already uses, so it appears under Updates as an install. With "keep each profile's settings" on, every mod's `config.json` is saved into the profile when leaving it and written back when entering, and a copy of the current settings is saved first so the first switch cannot lose anything. A shared profile file contains IDs, names, versions and update keys only, is size limited, and every entry is checked when it is read.

## Nexus collections

A collection is read from Nexus's public GraphQL API (its schema is open, and reading a collection needs no key, so a lookup never sends the person's key). The revision gives the name, curator and a list of mods with their Nexus IDs and the file the curator tested. Sprinkler stores that list inside a profile and treats every mod that is not installed as an ordinary new install: the same offers, the same Premium or one-click flow, the same backups. A collection mod's SMAPI ID is not known until its file is opened, so these installs accept the manifests inside the archive, and a file dropped into the Downloads folder is matched to its install by the mod ID Nexus puts in the file name. As each mod arrives it is added to the profile, found through the Nexus update key in its manifest, and the offers stop when nothing is left. Collections are read only: nothing is downloaded or changed until the person presses Update.

## Mod pictures

`ModArtStore` fetches each mod's picture once and keeps it on disk for two weeks (three days when a mod has none). Nexus mod pages expose a `picture_url` through the API the person's own key already uses, GitHub mods use the owner's avatar, and everything else shows a steady coloured initial. Only bytes that start like a PNG, JPEG, GIF or WebP are stored, files over 2 MB are refused, the final address after redirects must still be on an allowed site, and a rate limit pauses fetching for half an hour. Nothing about updating depends on pictures.

## Security of the app

- It opens no network port. Second copies talk through a pipe that only the current user can open.
- Links the app opens are limited to an allow-list of known mod and documentation sites over https.
- The Nexus key is stored with the best place each system offers: DPAPI on Windows, the desktop keyring through `secret-tool` on Linux when available, otherwise a file with mode 600.
- Archives are treated as hostile: paths, duplicate names and sizes are checked, and the manifest ID must match.

## How the Linux paths were chosen

Each Linux location comes from SMAPI's own installer code (`GameScanner` in SMAPI.Toolkit), so a game SMAPI can find, Sprinkler can find: GOG (`~/GOG Games/Stardew Valley/game`), Steam under `~/.steam/steam` and `~/.local/share/Steam`, the Flatpak Steam folder, and any extra library listed in `libraryfolders.vdf` (which covers the Steam Deck SD card).

Other decisions taken from SMAPI's behavior, not guessed:

- A folder is a game install when `Stardew Valley.dll` exists (game 1.6 and later).
- A mod folder whose name starts with a dot is skipped by SMAPI, which is how Sprinkler switches mods off.
- SMAPI finds `manifest.json` exactly, and also other capitalizations when `UseCaseInsensitivePaths` is on, which is its default on Linux. Sprinkler matches that.
- On Linux and macOS the SMAPI installer replaces the game launcher, so launching goes through `StardewModdingAPI` in the game folder, or through Steam (`steam://rungameid/413150`) so the player's own launch options and Proton settings are kept.
- A running game is detected by reading `/proc/<pid>/cmdline`, because Linux cuts process names to 15 characters. Proton shows the game as a Windows path, which is matched as well.
- Downloads go to the XDG downloads folder, read from `user-dirs.dirs`.
- Nexus links (`nxm://`) are registered with a `.desktop` file and `xdg-mime`. This is not available on macOS.

## How it is tested

- Unit and integration tests run the whole update flow against fake local servers for SMAPI, Nexus, CurseForge and GitHub, including the mod picture cache.
- The Linux and macOS path logic takes the home folder and operating system as parameters, so tests run it on any system with fake folders.
- The .NET platform-compatibility analyzer is on, so calling a Windows-only API outside a guard fails the build.
- The window is tested without a screen: Avalonia's headless support (which needs xunit v3) opens the real window on every tab and drives the real view models through the same commands the buttons use, including dialogs, file pickers and profile switching.
- CI runs the tests on Windows and Ubuntu, publishes both builds, and on Ubuntu runs the published binary's `--check` against a fake game folder.

## What is not verified

- The Linux build has compiled, linked (about 24 MB) and passed its tests, but has not been started on a real Linux desktop, a Steam Deck, or under Proton by the author. The first Ubuntu CI run and reports from Linux players are the real test. Avalonia's own Wayland support is described by its authors as ongoing work.
- Memory and speed numbers are from Windows only.
- Trimmed publishing was checked by running the published Windows build (screens, `--check`, mod pictures). The Linux trimmed build has only been compiled.
- macOS compiles but has no release, no tests on a Mac, and no Nexus link support.
