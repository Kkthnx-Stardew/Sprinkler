# Changelog

All notable changes are listed here. The project follows [Semantic Versioning](https://semver.org).

## Unreleased

### Added

- "Switch all on" and "Switch all off" on the All mods tab. They act on exactly the mods the list shows, so type in the search box or pick a filter first (for example "Animated", or Cannot check). One confirmation says how many mods change and names any switched-on mod outside the group that needs one of them and would stop loading. A folder that cannot be renamed is skipped and reported, and the rest still go through. Switching back is the other button.

### Fixed

- When smapi.io could not be reached (no internet, or the service is down), the All mods tab said "No mods found yet" and nothing could be switched on or off. The mods found on disk now stay in the list, each marked as not checked, and the check message still explains why.
- A download server that accepted the connection and then never answered could hold a download for ten minutes per try. Waiting for an answer now gives up after a minute, the same as a download that stalls midway, and the same guard covers the download of a new Sprinkler version.
- The profile and app update chips in the header were cut off with no ellipsis when a translation was long. They now end in an ellipsis, and the profile chip shows its full text on hover.
- The four count tiles on Updates could end up at different heights when a translation wrapped onto more lines. They now stay level.
- The search box turned almost black while it had focus. It now keeps one tone whether idle, hovered or focused.
- In a short window the All mods list shrank to one or two rows. The page now scrolls instead, so the list keeps room to show several mods.

- In a narrow window the last tab, Help, was cut off behind a scroll bar. Below 940 pixels wide the tabs drop their icons and tighten up, so all six fit.
- In a short window the step chips, the picture in the summary banner and the "About this list" fold-out are hidden, so the cards and the mod list get the room.
- The text under an update card started at a position that matched neither the picture nor the title, and moved out of line on cards whose tick box was hidden. It now lines up with the picture in every state.
- A card with a "What changed" fold-out had a taller row and a larger gap above "Skip this version" than a card without one. They now match, and the fold-out arrow lines up with the others.
- On Nexus and settings, two cards had no icon beside their title and the Browse and Save buttons of the two folder rows ended at different places. Every card now has an icon, and both folder rows share their button widths. The Help title icon lines up with the rest.

### Improved

- The program files for Windows and Linux are now precompiled, so the window opens in about half the time (0.6 seconds instead of 1.3 on the test PC) and uses half the start-up processor time. The download is about 15 MB larger.
- Notices at the top take at most a third of a short window, so the tab below keeps its room.

## 1.0.2 (2026-10-22)

### Fixed

- A downloaded file that was not installed because the author never raised the version in the mod's manifest (the page says 1.2.1, the file inside still says 1.2.0) was refused with no way forward. The file now appears as a card with an Install button, which installs it anyway with a backup and an Undo as usual.
- A file whose name does not match what it really is (a 7z saved as `.zip`, a zip saved as `.rar`) was called damaged. The first bytes of the file now decide how it is read, and a download is renamed to match.
- A zip with a second zip inside it, which some authors upload, was reported as holding no mod. The inner archive is now opened, and its mods are installed like any other.
- Two updates that both bring the same required mod could install it twice, the second time as "Mod (2)", because the second install was planned before the first finished. The Mods folder is now read again inside the install lock.
- A file whose version number cannot be compared (such as `1.2a`) was ignored when it turned up in Downloads. It is now offered whenever its version differs from the installed one.
- A file that holds the same mod twice (a main and an alternative folder) wrote both to the same place. The newer copy is used and a note says which copy was left out.
- A GitHub release without an attached file hid a later release that has one. A release tagged `latest` is now judged by the version in its file name.
- Installing from a solid `.7z` or `.rar` with many small files could take a minute or more, because the archive reader decoded the whole block again for every file. The reader library is now a version that does not (1,500 files in a quarter of a second), and it also has no known security advisory.
- Backups of changes you have undone, and of history entries that fell off the end of the list, were never deleted and used disk space for good. They are now removed.

### Improved

- Downloads are tried again after a dropped connection, a server error or a stall, and a download that ends early is caught by comparing it with the size the server promised. Nexus Premium downloads fall back to the next Nexus server when one fails. A web page sent in place of a file is refused.
- The Downloads folder is read again every 15 seconds while an update waits for a file, in case the file system watcher missed it. A big file saved straight under its final name is now waited for up to an hour instead of two minutes.
- The log now says what happened to every download and install: what was seen in Downloads, what was in it, and why it was installed, offered or ignored.

## 1.0.1 (2026-10-17)

### Fixed

- A mod whose Nexus page was handed to a new owner, who gave it a new mod ID, could never be updated. The download was silently ignored. Sprinkler now recognises the same Nexus page under a new ID and updates the old folder in place, with a backup and an Undo as usual. This works both for an update you started and for a file that simply turns up in Downloads, and every file the app skips is written to the log with the reason.
- Dropping a damaged file, or a text file named like an archive (`.7z` or `.rar`), onto the window could close the program. It now says the file cannot be read. Unexpected errors are written to the log, and one that slips through is shown as a message instead of ending the program.
- Pressing the source button that is already chosen on an update card (for example "Nexus Mods") did nothing. It now opens that site's page for the file.
- On Linux, an editor or file manager that was open on the Stardew Valley folder made Sprinkler think the game was running and refuse to update.
- An older GitHub or CurseForge release could be offered as a newer one when its title mentioned a higher number, such as "for SMAPI 4.1.0". A release is now judged by its own version.
- A file downloaded twice (named like "Mod-123-1-0-0-1752000000 (1).zip") was not matched to its Nexus mod.
- The "One click" and "Automatic" tags on a chosen source button blended into the button in the dark theme. They now have an outline and sit centred in both themes.
- Hovering a fold-out row, such as a question in Help, turned it black with a boxed arrow. It now gets a soft tint and keeps its link colour.
- Turning on Nexus links on a PC that refuses the registry change now says so instead of failing.

### Improved

- All mods: a mod with content packs shows a "4 content packs" button that folds and unfolds them. The packs have a thin rail beside them, a smaller icon and tighter rows. A mod with more than ten packs starts folded, and what you fold or unfold is remembered. Searching always shows every match.
- The long "update key points nowhere" note is no longer repeated on every grouped pack, and the full note under any mod's name appears in a tooltip when it is cut off.
- The arrows of fold-out sections now sit in one column at the right edge, and the Mod column of All mods gets more of the width.
- Copies of dropped files and downloads that were never installed are removed: when their card is dismissed, and at the next start for anything older than six hours. They used to stay in the data folder for good.
- Every Nexus link the app receives is written to the log, which makes a click that seems to do nothing easy to trace.
- A manifest larger than 1 MB inside an archive is ignored instead of being read into memory.
- A settings or history file that cannot be read is kept as `.unreadable` beside the new one, so the saved Nexus key and notes are not lost to the next save.
- The Xbox app folder is only looked for on drives that are connected, so a disconnected network drive can no longer slow the search for the game.
- On Linux the Nexus key is read from the keyring once, not for every request, and the window no longer waits on a scan of running programs while other work is going on.
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
