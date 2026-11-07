# Project context

Working notes for whoever picks the project up next (a person or a coding assistant). It records what is not obvious from the code: where things stand, decisions and the reasons, what was and was not verified, and what to do next. How the program is built is in [ARCHITECTURE.md](ARCHITECTURE.md); how to build and contribute is in [../CONTRIBUTING.md](../CONTRIBUTING.md). Keep this page short and update it at the end of each milestone. No secrets, no personal paths.

Last updated: 2026-11-07, at version 1.4.0 (see "State").

## Purpose and audience

Sprinkler checks a Stardew Valley Mods folder for outdated mods, installs updates with a backup and an Undo, and manages profiles, so a player who is not a modder can keep every mod current without reading forum threads. Most users are players who found mods through Nexus or YouTube. They are not expected to know what a manifest, an update key or a SMAPI log is, so every message is plain language and every change is reversible.

Primary tasks, in order of frequency: see what is outdated and update it; find out why the game will not start; switch between mod setups (solo, co-op, testing); install a mod from a download or a collection; remove a mod safely.

## Platform and constraints

- .NET 9, Avalonia 12, native window. Windows and Linux are released builds; macOS compiles but is not released or tested.
- Version lives only in `Directory.Build.props`. SemVer. A released version is never edited.
- Projects: `src/Sprinkler.Core` (all logic, no UI), `src/Sprinkler` (screens), `tests/Sprinkler.Tests` (xunit v3, includes headless UI tests that open the real window).
- Nothing changes the user's mods until they confirm. Every install goes through `Installer` (backup first, rollback on failure). The Nexus key is never logged or sent anywhere except Nexus.
- Every sentence the person reads is a key in `Sprinkler.Core/Locales/en.json` plus the 16 other language files. `LocaleTests` fails if a file lacks a key or a placeholder differs. No em dashes in texts.
- Commit messages: plain sentences, imperative, what and why, no attribution lines. Do not push unless asked.

## Design thesis

A calm, warm helper that explains itself: brown and gold palette taken from the sprinkler icon, plain sentences instead of jargon, one obvious next action per screen, and a visible way back from every change. The tagline is "Every mod. Current. One click." This is a description of the existing design, not a new brief; change it only on purpose.

Rules of thumb used when changing screens: put the thing the person came for first (the profile list before the profile forms), keep notices to one or two lines, show problems that can stop the game near the top, and give an empty state a next step.

## Commands

```
dotnet test Sprinkler.sln                      # about 45 seconds, 486 tests at last count
dotnet run --project src/Sprinkler
dotnet publish src/Sprinkler -c Release -r win-x64 -p:PublishSingleFile=true -o artifacts/publish
```

To look at the real window without touching a real game, make a temporary folder with a `game/Stardew Valley.dll` (empty file) and `game/Mods/<mod>/manifest.json` files, put a `settings.json` (game path, mods path, `welcomed` and `tourSeen` true) in a data folder, and start the app with `SPRINKLER_DATA` pointing at it. Real mod IDs at old versions make the update cards show up. The window can be driven with Windows UI Automation (invoke buttons by their accessible name) and captured with a screen copy of its rectangle.

## Decisions worth remembering

- Profiles switch by renaming folders (leading dot), SMAPI's own rule. Nothing is moved.
- The list on screen follows the Mods folder in three layers: (1) `RescanQuietlyAsync` reads the folder after every change the app makes and `ReconcileWithMods` fixes rows and update cards at once, before the slow online check; (2) `RequestRecheck` queues a new check if one is already running, so a stale result is never left on screen; (3) `UpdateService.Sync.cs` watches the Mods folder and rescans when the window is activated, so changes made in Explorer are seen. A change only counts if the folder really differs from what is shown, so the app's own writes cause no loop.
- Removing a mod takes it out of the active profile (so it is not offered again as "part of your profile") and records that in History, so Undo puts it back. A mod that disappears outside the app stays listed in the profile, marked "not installed here", with a "Forget the missing mods" action.
- A mod ID in two switched-on folders is reported as an error problem, shown above the update rows. Rows for the two copies still look alike in All mods.
- Updates tab design (chosen 2026-11-07 from three concepts in [REDESIGN_EXPLORATION.md](REDESIGN_EXPLORATION.md)): one-line rows that open in place, a compact top block, problems above the rows. A tab-per-job model stays; a merged list with an inspector and a pre-flight home screen were considered and not built.
- Confirm only for actions that are hard to reverse; removals and updates rely on Undo in History.

## State

- Released: 1.4.0 (2026-11-07), built and signed by the release workflow. It adds the Play menu (with or without mods, through Steam only when Steam is open and SMAPI is already its launch option), the unreadable-manifest warning, highlighted update buttons and the fixes from a hands-on test of 1.3.0. Before it, 1.3.0 (2026-11-07) shipped queued rechecks, the immediate list reconcile, the Mods-folder watcher, profiles that follow removals, Forget missing mods, the duplicate-mod warning, Switch and play and the Updates redesign (one-line rows, compact top block, problems above the rows), and 1.2.0 (2026-11-05) added self-update and SMAPI install from the app. Details are in the changelog.
- The Updates redesign result and what was not verified are at the end of [REDESIGN_EXPLORATION.md](REDESIGN_EXPLORATION.md).
- Verified before release: 516 tests pass, and CI passed on Windows and Ubuntu on the commit before the version bump (one unrelated collection test failed once in a full run and passed on three reruns, cause not found). Checked by hand in the real window on Windows with demo data: every tab at about 1100 and 780 pixels wide, deleting a folder in Explorer, removing a mod from the menu with a profile active, Forget the missing mods, the duplicate banner. Idle cost measured once: about 0.4 percent of one core and 216 MB.
- A hands-on test of 1.3.0 on Windows (real 100-mod setup, a 321-mod stress folder with broken manifests and Unicode names, seven languages by two widths by six tabs, seven window sizes, offline, install and undo, second instance) is the source of most 1.4.0 fixes. Scripts for it were throwaway and are not in the repo.
- Not tested: a real game launch from the Play menu, Steam's overlay and playtime when started through Steam, the Play menu and the folder watcher on Linux and macOS, the self-update swap on Linux, per-monitor DPI changes and text scaling, keyboard-only use of the new controls (the Play menu included), screen readers, dragging the scroll bar, touch, a clean install and uninstall, and very large mod folders for the folder watcher.
- The texts added in this milestone are in all 17 languages but were written by the assistant, not by native speakers.

## Known issues and risks

- SMAPI install and update were tested against a stand-in installer; the real installer has not been run on every system (a test exists behind `SPRINKLER_REAL_SMAPI_GAME`).
- "Log in with Nexus" needs the app registered with Nexus first; until then people paste their own key.
- CurseForge has no API key here, so its mods show a coloured initial instead of a picture.
- From earlier notes, not re-checked this session: repo name and licence assumptions, artwork origin note, Steam Deck game-mode notes, a FAQ entry for self-update.

## Next steps

1. Run the "not tested" list above, starting with a real game folder with SMAPI and a Linux machine.
2. Mark duplicate mods in the All mods rows themselves, and decide whether per-profile mod notes are worth adding (Stardrop ties notes to profiles; here notes are global).
3. Have native speakers review the translations.
