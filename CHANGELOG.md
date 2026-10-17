# Changelog

All notable changes are listed here. The project follows [Semantic Versioning](https://semver.org).

## 0.1.0-alpha.1 (not released)

First public build.

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
- On Linux, adds itself to the application menu with an icon (`--install-shortcut` or a button in settings), and finds mods whose manifest is spelled `Manifest.json`, as SMAPI does there.
- Every text and background colour pair meets the WCAG AA contrast ratio of 4.5 to 1 in both themes, and a test keeps it that way.
- About 24 MB to download and about 145 MB of memory in use.
- Renamed from Stardew Mod Updater. An older data folder is carried over on first start.
