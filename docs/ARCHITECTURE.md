# Architecture and the choices behind it

This page explains how the app is built, why, and what each alternative would have cost. Numbers come from prototypes built and measured on the same Windows 11 machine with the same 36-mod folder. Nothing here was measured on Linux, because no Linux machine was available while writing it. That is stated again at the end.

## The shape

```
 your browser-based window  <--HTTP, 127.0.0.1, per-run token-->  C# core (.NET 9, Kestrel)
 (one HTML file, plain JS)                                          scan, check, download, install, undo
```

- **Core** (`src/StardewModUpdater/Core`): everything that matters, with no UI code. Scanning mods, version rules, the SMAPI, Nexus, CurseForge and GitHub clients, the installer, history.
- **UI** (`src/StardewModUpdater/Web/index.html`): one embedded file, no build step, no framework, no network fonts. It polls `/api/state`.
- **Window**: the UI opens in its own chrome-less window using a Chromium-family browser that is already installed (`--app` mode, private throwaway profile). Without one, it opens a normal browser tab. Closing the window ends the app.
- **Platform layer**: `Platform`, `GameLocator`, `GameProcess`, `SecretStore`, `Nxm`, `SingleInstance` and `AppWindow` hold every difference between Windows, Linux and macOS. The rest of the code does not check the operating system.

## Why a local web UI

The requirement that settled it: the app must run natively on Linux, because that is where Stardew Valley runs natively, and it must stay small, readable and easy for others to contribute to.

| Option | Memory with a real mod folder | Linux story | Verdict |
| --- | --- | --- | --- |
| WPF / WinForms | about 110 MB | None. Windows only. | Ruled out by the Linux requirement. |
| Avalonia (C# UI) | about 116 MB | Good. Stardrop, a popular Stardew mod manager, uses it. | Works, but a rewrite, a heavier native dependency chain, and a custom-drawn UI that is harder for accessibility and for contributors than HTML. |
| WebView2 host | about 250 to 275 MB | None. WebView2 is Windows only. | Ruled out. |
| Tauri / Photino (system web view) | not measured | Uses WebKitGTK on Linux, whose version and graphics behavior differ between distributions and the Steam Deck. | Too much risk of "works on my machine" rendering bugs that cannot be tested here. |
| **Local server plus Chromium `--app` window (chosen)** | about 400 MB in total, almost all of it the browser | Same code and same rendering on every system, as long as a Chromium-based browser exists. | Chosen. Costs the most memory. |

Honest trade-off: the chosen design uses the most memory of the options that run on Linux. The reasons it still wins are that the interface looks and behaves identically everywhere, accessibility comes from the browser for free, the UI is a single file anyone can edit, the core is fully testable without a screen, and there is no per-system UI toolkit to build and ship. If a lighter window matters more later, only `AppWindow` and the HTML would change; the core stays.

If the machine has no Chromium-based browser, the app opens a normal tab in the default browser. That works too, but the app no longer controls when the "window" closes, so a heartbeat from the page ends the app a short time after the tab is closed.

## Security of the local server

- Binds to 127.0.0.1 on a random port. Nothing is reachable from the network.
- Every API call needs a random per-run token in a header. The `Host` header is checked, and a strict Content-Security-Policy is sent.
- Links the UI asks the app to open are limited to an allow-list of known mod and documentation sites over https.
- The Nexus key is stored with the best place each system offers: DPAPI on Windows, the desktop keyring through `secret-tool` on Linux when available, otherwise a file with mode 600.

## How the Linux paths were chosen

Each Linux location comes from SMAPI's own installer code (`GameScanner` in SMAPI.Toolkit), so a game SMAPI can find, this app can find: GOG (`~/GOG Games/Stardew Valley/game`), Steam under `~/.steam/steam` and `~/.local/share/Steam`, the Flatpak Steam folder, and any extra library listed in `libraryfolders.vdf` (which covers the Steam Deck SD card).

Other decisions taken from SMAPI's behavior, not guessed:

- A folder is a game install when `Stardew Valley.dll` exists (game 1.6 and later).
- A mod folder whose name starts with a dot is skipped by SMAPI, which is how the app switches mods off.
- On Linux and macOS the SMAPI installer replaces the game launcher with its own, so launching goes through `StardewModdingAPI` in the game folder, or through Steam (`steam://rungameid/413150`) so the player's own launch options and Proton settings are kept.
- A running game is detected by reading `/proc/<pid>/cmdline`, because Linux cuts process names to 15 characters ("StardewModdingA"). Proton shows the game as a Windows path, which is matched as well.
- Downloads go to the XDG downloads folder, read from `user-dirs.dirs`.
- Nexus links (`nxm://`) are registered with a `.desktop` file and `xdg-mime`, the freedesktop way. This is not available on macOS.

## How it is tested

- Unit and integration tests run the whole update flow against fake local servers for SMAPI, Nexus, CurseForge and GitHub.
- The Linux and macOS path logic takes the home folder and operating system as parameters, so tests run it on any system with fake folders: candidate paths, the second Steam library, `stardewvalley.targets`, `/proc` scanning, the XDG downloads file, the `.desktop` text, the file secret store and its permissions.
- The .NET platform-compatibility analyzer is on, so calling a Windows-only API outside a guard fails the build.
- CI runs the tests on Windows and Ubuntu, publishes both builds, and on Ubuntu starts the published binary and calls its API.

## What is not verified

- The Linux build has compiled and linked (a 33 MB single file), and the platform logic is covered by the tests above. It has not been started on a real Linux desktop, a Steam Deck, or under Proton by the author. The first Ubuntu CI run and reports from Linux players are the real test.
- Trimmed single-file publishing relies on reflection-based JSON; the options for that are set in the project file and exercised by the CI smoke test.
- macOS compiles but has no release, no tests on a Mac, and no Nexus link support.
