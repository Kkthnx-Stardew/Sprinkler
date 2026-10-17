# Changelog

All notable changes are listed here. The project follows [Semantic Versioning](https://semver.org).

## Unreleased

- Fixed: a mod whose Nexus page was handed to a new owner, who gave it a new mod ID, could never be updated. The download was silently ignored. Sprinkler now recognises the same Nexus page under a new ID and updates the old folder in place, with a backup and an Undo as usual.
- Fixed: a file downloaded twice (named like "Mod-123-1-0-0-1752000000 (1).zip") was not matched to its Nexus mod.
- Failed Nexus link downloads are now written to the log.
- Fixed: the "One click" and "Automatic" tags on a chosen source button blended into the button in the dark theme. They now have an outline and sit centred in both themes.
- The question rows in Help and the other fold-out sections now keep their arrows in one column at the right edge, and the Mod column of All mods gets more of the width so names and notes are cut off less.
- Fixed: hovering a fold-out row (such as the questions in Help) turned it black with a boxed arrow. It now gets a soft tint and keeps its link colour.
- Fixed: dropping a damaged file, or a text file named like an archive (`.7z` or `.rar`), onto the window could close the program. It now says the file cannot be read. Unexpected errors are also written to the log, and one that slips through is shown as a message instead of ending the program.
- Fixed: on Linux, an editor or file manager that was open on the Stardew Valley folder made Sprinkler think the game was running and refuse to update.
- Fixed: an older GitHub or CurseForge release could be offered as a newer one when its title mentioned a higher number, such as "for SMAPI 4.1.0". A release is now judged by its own version.
- A manifest larger than 1 MB inside an archive is ignored instead of being read into memory.
- A settings or history file that cannot be read is kept as `.unreadable` beside the new one, so the saved Nexus key and notes are not lost to the next save.
- The Xbox app folder is only looked for on drives that are connected, so a disconnected network drive can no longer slow the search for the game.
- On Linux the Nexus key is read from the keyring once, not for every request, and the window no longer waits on a scan of running programs while other work is going on.
- Fixed: pressing the source button that is already chosen on an update card (for example "Nexus Mods") did nothing. It now opens that site's page for the file.
- Copies of dropped files and downloads that were never installed are now removed: when their card is dismissed, and at the next start for anything older than six hours. They used to stay in the data folder for good.
- Turning on Nexus links on a PC that refuses the registry change now says so instead of failing. Every Nexus link the app receives is written to the log, which makes a click that seems to do nothing easy to trace.
- Mod pictures that a newer picture replaces are deleted from the cache, and two pictures finishing at the same moment can no longer collide while saving the index.

## 1.0.0 (2026-10-17)

First public release.

- Checks every mod in your Mods folder against the SMAPI update service. No account or key needed.
- A native desktop app (Avalonia) for Windows and Linux, including Steam Deck, Flatpak Steam and extra Steam libraries. No browser is needed. The Linux build is new and has had less testing.
- Downloads from Nexus Mods first, with CurseForge and GitHub as fallbacks when they have the same version.
- Nexus support for Premium accounts (fully automatic), free accounts (one click per mod through the Mod Manager Download button), and no key (the app installs the file when it lands in Downloads).
- Mod pictures and short summaries on the update cards and the All mods list, from Nexus (through your own key) and GitHub, cached for two weeks. Mods without a picture get a coloured initial.
- Finds required mods that are missing, which SMAPI otherwise skips without a clear message.
- Backs up each mod before replacing it, keeps `config.json`, rolls back on any failure, and offers Undo for every change.
- Installs from `.zip`, `.7z` and `.rar` files, including files dropped onto the window.
- First-run tour, built-in help and FAQ, light and dark looks that follow your system, and a Report a problem button that copies details for an issue.
- Reads SMAPI's log from your last game session and lists the mods it skipped, with SMAPI's reason and what to do about it.
- Switch a mod off or on from the All mods tab. A warning lists any mods that need it.
- Nexus collections: paste a collection's address to add it as a profile, see how much of it you have, and install the rest through the normal update flow.
- Profiles: save which mods are switched on, switch between profiles in a click, share a profile as a small file, and optionally keep each profile's own mod settings. Mods a shared profile lists but you do not have appear under Updates as installs.
- Remove a mod with a backup and an Undo button in History. Keep private notes on mods, open a mod's settings file, and endorse mods on Nexus.
- Content packs are shown under the mod they belong to, and a Has settings filter finds mods with a config.json.
- Updates itself: a header pill offers a newer release, and with your yes the app downloads it, checks its signature (ECDSA P-256, key built in) and SHA-256, starts it once with `--version`, replaces itself (the old program is kept as `.old` on Windows until the next start) and restarts. Builds without a signature, from source or in a read-only folder only link to the release page. Pre-releases are offered only to people on a pre-release. The start-up look for updates can be turned off.
- Screen readers now read the real label of buttons and tabs that hold an icon and text, in every language, instead of the control type.
- 17 languages: English plus Spanish, French, German, Brazilian Portuguese, Italian, Dutch, Russian, Ukrainian, Polish, Turkish, Hungarian, Bulgarian, Simplified Chinese, Japanese, Korean and Thai. The app follows the system language and can be switched under Nexus and settings, with every screen, message and counted phrase redrawn at once. The 16 translations were written with machine help and have not been read by native speakers yet. Tests keep every file complete and its placeholders in step with English. See docs/TRANSLATING.md.
- On Linux, adds itself to the application menu with an icon (`--install-shortcut` or a button in settings), and finds mods whose manifest is spelled `Manifest.json`, as SMAPI does there.
- Every text and background colour pair meets the WCAG AA contrast ratio of 4.5 to 1 in both themes, and a test keeps it that way.
- A Support panel on the Help tab links to Ko-fi, Buy Me a Coffee and PayPal. Nothing is locked behind it.
- Found in the final review and fixed: a new check could cancel the download of an app update, the mod scanner could follow a folder link in a loop on Linux, and a few messages were still English in other languages. The header, tabs and statistic tiles now line up on wide windows and wrap or scroll on narrow ones.
- About 24 MB to download and about 165 MB of memory in use.
- Renamed from Stardew Mod Updater. An older data folder is carried over on first start.
