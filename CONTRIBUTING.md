# Contributing

Thanks for helping. Bug reports, ideas, documentation fixes and code are all welcome.

## Reporting a bug

Use the **Report a problem** button in the app (Help tab). It copies the app version, game and SMAPI versions, and the last log lines to your clipboard, then opens a new issue where you can paste them. User folder names are masked and your Nexus key is never included. Please read the text before posting anyway.

## Building

You need the .NET 9 SDK on Windows or Linux. One set of projects builds for both.

```
dotnet test tests/Sprinkler.Tests
dotnet run --project src/Sprinkler
dotnet publish src/Sprinkler -c Release -r win-x64 -p:PublishSingleFile=true -o artifacts/publish   # or -r linux-x64
```

Useful options while developing:

```
--game <folder>    use another Stardew Valley folder (a folder with an empty "Stardew Valley.dll" is enough)
--check            print what is outdated and change nothing (no window, so it works over SSH and in CI)
--tab <name>       open on a tab: updates, mods, history, settings, help
--theme <name>     show light, dark or auto without saving it
--no-tour          skip the first-run tour
--filter <key>     start the All mods list filtered: update, ok, needs, nocheck, off
--search <text>    start the All mods list with a search
```

Set `SPRINKLER_DATA` to a temporary folder to keep settings, history, backups and pictures away from your real ones. `SPRINKLER_SMAPI_URL`, `SPRINKLER_CF_URL`, `SPRINKLER_GH_URL`, `SPRINKLER_GH_SITE`, `SPRINKLER_NEXUS_URL` and `SPRINKLER_NEXUS_SITE` point the app at a fake server, which is how the integration tests work. `SPRINKLER_ALLOW_LOCAL=1` lets tests use local addresses.

## How the code is laid out

| Path | What it does |
| --- | --- |
| `src/Sprinkler.Core` | Everything that is not UI: scanning mods, version rules, the SMAPI, Nexus, CurseForge and GitHub clients, the installer with backup and undo, mod pictures, and `UpdateService`, which ties them together and produces a typed `StateSnapshot`. |
| `src/Sprinkler` | The Avalonia app. `ViewModels` turn snapshots into what the screens show, `Views` are the screens, `Theme` holds the palette and control styles, `Controls` has the icon control. |
| `tests/Sprinkler.Tests` | Unit tests, app logic tests, a contrast check of the palette, and integration tests that run the whole update flow against a fake local server. |

A new value on screen needs three small steps: add it to `UpdateService.Snapshot()` (and `Snapshot.cs`), expose it on the right view model, and bind it in the XAML. Compiled bindings are on, so a typo fails the build instead of showing a blank.

## Ground rules

- Nothing may change the user's mods until they confirm. Every install goes through `Installer`, which backs up first and rolls back on failure.
- Never log or send the Nexus key. It is stored through `SecretStores` (DPAPI on Windows, the desktop keyring or a mode-600 file on Linux).
- Treat every archive as hostile. Paths are validated in `ArchiveFile`, and the manifest ID must match the mod being updated.
- Keep system-specific behavior behind the platform classes, and test path and parsing logic with fake folders so the test runs on every system. CI runs the tests on Windows and Ubuntu. Say in the pull request if you could only try a change on one system.
- New colours go in `Theme/Palette.axaml` with a literal value. The contrast test reads that file, so a pair that is hard to read fails the build.
- Add a test for each bug you fix. The fake-server tests in `ServiceTests.cs` and `ModArtTests.cs` show how.
- Keep the app light. Avoid new dependencies unless they earn their size; the published download is about 24 MB.
- No telemetry, ads or accounts of our own.

## Before a wide release: register with Nexus

Nexus's API policy says personal API keys are tolerated for testing and personal use only, and that a public-facing app must be registered by emailing support@nexusmods.com with a testing build, a name, a short description and a logo. Registration also gives the app a slug for Nexus's single sign-on, which would replace pasting a key. Open source is encouraged and may speed the review. Until that is done, keep the README's wording about personal keys. The policy page is the source: https://help.nexusmods.com/article/114-api-acceptable-use-policy

## Pull requests

Keep each pull request to one change, explain what and why in the description, and make sure `dotnet test` passes. Commit messages should be short plain sentences in the imperative, for example "Keep config.json when a mod is replaced".

## Releases

The version lives only in `Directory.Build.props`. To release, update `CHANGELOG.md`, bump the version, and push a tag named `v<version>`. The release workflow checks the tag matches the project version, builds the Windows zip and the Linux tar.gz and publishes them with checksums. Released versions are never changed.
