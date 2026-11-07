using Sprinkler.Core;
using Xunit;

namespace Sprinkler.Tests;

/// <summary>
/// Steam is used only when it is open, and for a modded start only when SMAPI is already Steam's launch option for the
/// game. Every other case starts the game from its folder, and Steam is never started just to play.
/// </summary>
public class SteamLaunchTests
{
    // mode, steamInstall, steamRunning, optionStartsSmapi, hasSmapi, canStartDirectly -> plan
    [Theory]
    [InlineData("WithMods", true, true, true, true, true, "Steam")] // open, set up: through Steam
    [InlineData("WithMods", true, true, false, true, true, "DirectSmapi")] // open but not set up: Steam would start the plain game
    [InlineData("WithMods", true, false, true, true, true, "DirectSmapi")] // not open: never start Steam
    [InlineData("WithMods", false, false, false, true, true, "DirectSmapi")] // GOG or a plain folder
    [InlineData("WithMods", true, true, true, false, true, "Unavailable")] // no SMAPI at all
    [InlineData("Vanilla", true, true, false, true, true, "Steam")] // open and Steam really starts the plain game
    [InlineData("Vanilla", true, true, true, true, true, "DirectGame")] // Steam would add SMAPI, so start the game itself
    [InlineData("Vanilla", true, false, false, true, true, "DirectGame")] // Steam closed
    [InlineData("Vanilla", false, false, false, false, true, "DirectGame")] // works before SMAPI is installed
    [InlineData("Vanilla", true, true, true, true, false, "Unavailable")] // nothing to start directly, Steam would add mods
    public void EveryCaseChoosesTheRightWayToStart(string mode, bool steamInstall, bool steamRunning, bool optionSmapi, bool hasSmapi, bool direct, string expected) =>
        Assert.Equal(Enum.Parse<LaunchPlan>(expected), SteamLaunch.Plan(Enum.Parse<LaunchMode>(mode), steamInstall, steamRunning, optionSmapi, hasSmapi, direct));

    private const string Vdf = """
        "UserLocalConfigStore"
        {
            "Software"
            {
                "Valve"
                {
                    "Steam"
                    {
                        "apps"
                        {
                            "250900"
                            {
                                "LaunchOptions"		"-windowed"
                            }
                            "413150"
                            {
                                "cloud"
                                {
                                    "last_sync_state"		"synchronized"
                                }
                                "LaunchOptions"		"\"C:\\Games\\Stardew Valley\\StardewModdingAPI.exe\" %command%"
                                "Playtime"		"5220"
                            }
                        }
                    }
                }
            }
        }
        """;

    [Fact]
    public void TheLaunchOptionIsReadFromTheGamesOwnBlockEvenWithNestedBlocksAndEscapes()
    {
        var options = SteamLaunch.ReadLaunchOptions(Vdf);

        Assert.Equal("\"C:\\Games\\Stardew Valley\\StardewModdingAPI.exe\" %command%", options);
        Assert.True(SteamLaunch.OptionsStartSmapi(options));
    }

    [Fact]
    public void AnotherGamesOptionsAreNotMistakenForStardewValleys()
    {
        var other = Vdf.Replace("\"413150\"", "\"999999\"");

        Assert.Null(SteamLaunch.ReadLaunchOptions(other));
    }

    [Fact]
    public void AGameWithoutOptionsAndAFileWithoutTheGameBothGiveNull()
    {
        Assert.Null(SteamLaunch.ReadLaunchOptions("\"apps\" { \"413150\" { \"Playtime\" \"5\" } }"));
        Assert.Null(SteamLaunch.ReadLaunchOptions(""));
        Assert.Null(SteamLaunch.ReadLaunchOptions("\"Software\" { \"Number\" \"413150\" }")); // the number as a value is not a block
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("-windowed", false)]
    [InlineData("\"D:\\x\\stardewmoddingapi.exe\" %command%", true)]
    [InlineData("\"/home/me/Stardew Valley/StardewModdingAPI\" %command%", true)]
    public void OnlyAnOptionThatStartsSmapiCounts(string? options, bool expected) =>
        Assert.Equal(expected, SteamLaunch.OptionsStartSmapi(options));

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley", true)]
    [InlineData(@"c:\program files (x86)\steam\steamapps\common\Stardew Valley", true)]
    [InlineData("/home/me/.local/share/Steam/steamapps/common/Stardew Valley", true)]
    [InlineData(@"D:\Games\GOG\Stardew Valley", false)]
    [InlineData(@"C:\Games\Stardew Valley", false)]
    public void ASteamLibraryFolderIsRecognisedEvenWhenTheSettingsReopenedItAsManual(string path, bool expected) =>
        Assert.Equal(expected, SteamLaunch.IsSteamFolder(path));

    [Fact]
    public void TheOptionToPasteNamesTheRealSmapiProgramOfThisInstall()
    {
        using var d = new TempDir();
        var game = new GameInstall(d.Combine("Stardew Valley"), d.Combine("Stardew Valley", "Mods"), "Steam", "1.6.15", "4.5.2");

        var text = GameProcess.SteamLaunchOptionText(game);

        Assert.EndsWith(" %command%", text);
        Assert.StartsWith("\"" + game.GamePath, text);
        Assert.Contains("StardewModdingAPI", text);
    }
}
