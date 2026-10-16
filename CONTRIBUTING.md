# Contributing

Thanks for helping. Bug reports, ideas, documentation fixes and code are all welcome.

## Reporting a bug

Use the **Report a problem** button in the app (Help tab or footer). It copies the app version, game and SMAPI versions, and the last log lines to your clipboard, then opens a new issue where you can paste them. User folder names are masked and your Nexus key is never included. Please read the text before posting anyway.

## Building

You need the .NET 9 SDK on Windows or Linux. One project builds for both with a single `net9.0` target, and the few system differences live in `Core/Platform.cs` and its neighbours (`GameLocator`, `GameProcess`, `SecretStore`, `Nxm`, `Web/AppWindow`). The analyzer that checks platform-specific calls runs on every build, so a Windows-only call outside a guard fails the build.

```
dotnet test tests/StardewModUpdater.Tests
dotnet run --project src/StardewModUpdater
dotnet publish src/StardewModUpdater -c Release -r win-x64 -p:PublishSingleFile=true -o artifacts/publish   # or -r linux-x64
```

Useful flags while developing:

```
--game <folder>    use another Stardew Valley folder (a folder with an empty "Stardew Valley.dll" is enough)
--browser          open in a normal browser tab so you can use dev tools
--no-browser       do not open any window (the address is written to url.txt in the data folder)
--check            print what is outdated and change nothing
```

Set `SMU_DATA` to a temporary folder to keep settings, history and backups away from your real ones. `SMU_SMAPI_URL`, `SMU_CF_URL`, `SMU_GH_URL`, `SMU_NEXUS_URL` and `SMU_NEXUS_SITE` point the app at a fake server, which is how the integration tests work.

## How the code is laid out

| Path | What it does |
| --- | --- |
| `src/StardewModUpdater/Core` | Everything that is not UI: scanning mods, version rules, the SMAPI, Nexus, CurseForge and GitHub clients, the installer with backup and undo, and `UpdateService`, which ties them together. |
| `src/StardewModUpdater/Web` | The local web server and the single-page UI (`index.html`, plain JavaScript, no build step). |
| `tests/StardewModUpdater.Tests` | Unit tests plus integration tests that run the whole update flow against a fake local server. |

The UI talks to the app through `/api/*` on 127.0.0.1 with a per-run token. The page polls `/api/state`, so a new field only needs adding to `UpdateService.Snapshot()` and reading in `index.html`.

## Ground rules

- Nothing may change the user's mods until they confirm. Every install goes through `Installer`, which backs up first and rolls back on failure.
- Never log or send the Nexus key. It is stored through `SecretStores` (DPAPI on Windows, the desktop keyring or a mode-600 file on Linux).
- Treat every archive as hostile. Paths are validated in `ArchiveFile`, and the manifest ID must match the mod being updated.
- Keep system-specific behavior behind the platform classes, and test path and parsing logic with fake folders so the test runs on every system. CI runs the tests on Windows and Ubuntu. Say in the pull request if you could only try a change on one system.
- Add a test for each bug you fix. The fake-server tests in `ServiceTests.cs` show how.
- Keep the app light. Avoid new dependencies unless they earn their size; the published download is about 32 MB.
- No telemetry, ads or accounts of our own.

## Before a wide release: register with Nexus

Nexus's API policy says personal API keys are tolerated for testing and personal use only, and that a public-facing app must be registered by emailing support@nexusmods.com with a testing build, a name, a short description and a logo. Registration also gives the app a slug for Nexus's single sign-on, which would replace pasting a key. Open source is encouraged and may speed the review. Until that is done, keep the README's wording about personal keys. The policy page is the source: https://help.nexusmods.com/article/114-api-acceptable-use-policy

## Pull requests

Keep each pull request to one change, explain what and why in the description, and make sure `dotnet test` passes. Commit messages should be short plain sentences in the imperative, for example "Keep config.json when a mod is replaced".

## Releases

The version lives only in `src/StardewModUpdater/StardewModUpdater.csproj`. To release, update `CHANGELOG.md`, bump the version, and push a tag named `v<version>`. The release workflow checks the tag matches the project version, builds the Windows zip and the Linux tar.gz and publishes them with checksums. Released versions are never changed.
