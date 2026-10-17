# Third-party notices

Sprinkler is MIT licensed (see `LICENSE`). It includes or uses the following.

| Component | Use | License |
| --- | --- | --- |
| [Avalonia](https://avaloniaui.net) (core, Desktop, Fluent theme) | The window and controls. | MIT |
| [Inter](https://rsms.me/inter/) through the Avalonia.Fonts.Inter package | The app font, bundled so the app works offline. | SIL Open Font License 1.1 |
| [SkiaSharp](https://github.com/mono/SkiaSharp) and [HarfBuzzSharp](https://github.com/mono/SkiaSharp) | Drawing and text shaping under Avalonia. | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | View model plumbing. | MIT |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | Reading .7z and .rar archives. | MIT |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) | Encrypting the saved Nexus key for the current Windows user. | MIT |
| [Feather icons](https://feathericons.com) | The line icon shapes, stored as path data in `src/Sprinkler/Controls/IconData.cs`. | MIT |
| [.NET runtime](https://github.com/dotnet) | The self-contained runtime inside the program. | MIT |

## Services

The app reads public data from the [SMAPI update service](https://smapi.io), [Nexus Mods](https://www.nexusmods.com), [CurseForge](https://www.curseforge.com) and [GitHub](https://github.com). Mod pictures are downloaded from the sites that host the mods. The app is not affiliated with or endorsed by any of them.

## Artwork

The Sprinkler logo and banner (`docs/brand`, `src/Sprinkler/Assets`) were supplied by the project owner for this project, in a pixel-art style. The detailed sprinkler image in `docs/brand` is kept only as a source file. If you fork or redistribute this project, replace them with your own images; nothing else depends on them.

Stardew Valley is a trademark of ConcernedApe. This project is a fan-made tool and is not affiliated with or endorsed by ConcernedApe.
