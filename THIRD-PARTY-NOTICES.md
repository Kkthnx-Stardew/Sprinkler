# Third-party notices

Stardew Mod Updater is MIT licensed (see `LICENSE`). It includes or uses the following.

| Component | Use | License |
| --- | --- | --- |
| [Inter](https://rsms.me/inter/) (variable font, latin subset) | The page font, bundled so the app works offline. License text: `src/StardewModUpdater/Web/Inter-LICENSE.txt`. | SIL Open Font License 1.1 |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | Reading .7z and .rar archives. | MIT |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) | Encrypting the saved Nexus key for the current Windows user. | MIT |
| [.NET runtime and ASP.NET Core](https://github.com/dotnet) | The self-contained runtime inside the exe. | MIT |

## Services

The app reads public data from the [SMAPI update service](https://smapi.io), [Nexus Mods](https://www.nexusmods.com), [CurseForge](https://www.curseforge.com) and [GitHub](https://github.com). It is not affiliated with or endorsed by any of them.

## Game art

`src/StardewModUpdater/Web/chicken.png` is the white chicken sprite from Stardew Valley, which is the work of ConcernedApe. It is used as a fan logo and is not covered by this project's MIT license. If you fork or redistribute this project and are not comfortable with that, replace the file with your own image; nothing else depends on it.

Stardew Valley is a trademark of ConcernedApe. This project is not affiliated with or endorsed by ConcernedApe.
