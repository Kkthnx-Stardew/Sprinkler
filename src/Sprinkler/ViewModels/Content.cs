using Sprinkler.Core;

namespace Sprinkler.ViewModels;

internal sealed record QA(string Question, string Answer);

/// <summary>The words the app uses to explain itself: the tour, the FAQ and the troubleshooting list.</summary>
internal static class Content
{
    public const string Tagline = "Every mod. Current. One click.";

    public static readonly IReadOnlyList<TourStep> Tour = new[]
    {
        new TourStep("sprinkler", "Welcome to Sprinkler", "It checks your mods against SMAPI's update list, shows what is out of date, and installs updates with a backup. This takes 30 seconds."),
        new TourStep("package", "1. It checks for you", "On launch the app reads your Mods folder and asks smapi.io which mods have newer versions. Nothing is changed during the check."),
        new TourStep("arrowup", "2. You choose", "Every update has a card. Tick the ones you want. The source buttons show where the file comes from, and \"Automatic\" ones need nothing from you."),
        new TourStep("download", "3. Press Update", "Mods that need a click open on Nexus a few at a time. When the file lands in Downloads, the app installs it. Every mod is backed up first."),
        new TourStep("layers", "Profiles for different games", "Save which mods are switched on as a profile, for example solo, co-op or testing, and switch in one click. Share one with a friend as a small file."),
        new TourStep("shield", "Undo any time", "History has an Undo button for every change, even removing a mod. When you are done, press Play to start the game through SMAPI."),
        new TourStep("key", "Optional: connect Nexus", "A free Nexus key lets the app pick the exact file and show what changed. With Premium it downloads by itself. You will find it under Nexus and settings."),
    };

    public static IReadOnlyList<QA> Faq(string dataDir) => new[]
    {
        new QA("Is it safe to use?", "Nothing changes until you press Update. Before a mod is replaced, its folder is saved as a zip, and every change can be undone from History. The app only talks to smapi.io, Nexus Mods, CurseForge and GitHub, and it never touches your save games."),
        new QA("Why do I have to click on Nexus Mods?", "Nexus only gives files to logged-in users, and a free account has to press the download button itself. The app opens the exact file page for you, then installs the file the moment it lands in your Downloads folder. With Nexus Premium and a saved key it is fully automatic."),
        new QA("What does \"Cannot check\" mean?", "The mod author did not put update information in the mod's manifest, so there is nothing to look up. You can still update such a mod: download it yourself and drop the .zip onto this window."),
        new QA("What is a \"missing required mod\"?", "Some mods need a framework mod, such as Content Patcher or SpaceCore. If it is not installed, SMAPI skips the mod without a clear message. The app finds these and offers to install them."),
        new QA("Where are my backups?", $"In the backups folder inside {dataDir}. The History tab lists them with an Undo button, and the app keeps the newest five per mod."),
        new QA("Will it change my settings or saves?", "No. Your config.json files are kept when a mod is updated, and save games are never touched. The one exception is a profile with \"keeps its own mod settings\" turned on, which puts back the settings it saved for that profile when you switch to it."),
        new QA("What is a profile?", "A saved list of which mods are switched on, for example one for solo play and one for co-op. Switching only adds or removes the leading dot on a mod's folder name, which is how SMAPI skips a mod, so nothing is moved or deleted. A shared profile lists mods, names and where to get them, never your settings or folders."),
        new QA("Can I use a Nexus collection?", "Yes. On the Profiles tab, paste the collection's address from nexusmods.com. Sprinkler reads its mod list, saves it as a profile, and offers the mods you do not have under Updates as installs. It never switches your mods by itself, and the optional mods are only added if you tick the box. With a free Nexus account you click once per mod; with Premium it is automatic."),
        new QA("Can I get a removed mod back?", "Yes. Removing a mod saves a backup of its folder first, and the removal appears in History with an Undo button that restores it with its settings."),
        new QA("How do I remove the app?", $"Delete the program and the folder {dataDir}. If you turned on \"Let this app handle Nexus links\" or added the app to your menu, turn those off first under Nexus and settings."),
    };

    public static readonly IReadOnlyList<QA> Fixes = new[]
    {
        new QA("SMAPI not found", "Mods only load through SMAPI. Install it from smapi.io, then press Check again."),
        new QA("The app says to close the game", "Stardew Valley locks the files of the mods it loaded. Close the game and SMAPI, then try again."),
        new QA("Nexus did not accept my key", "Copy the \"Personal API Key\" again from nexusmods.com/settings/api-keys and paste it without spaces."),
        new QA("An update failed", "The app puts the old version back when anything goes wrong, so nothing is left half-installed. The card says why. Try again, or pick another source on the card."),
        new QA("The window looks wrong or will not open", "Start the app from a terminal and send what it prints with a problem report. Under Linux, mention your desktop and whether it runs on Wayland or X11."),
    };

    public static string FindGameHelp(string os)
    {
        const string steam = "Steam: right-click Stardew Valley in your library, choose Manage, then Browse local files, and copy the address at the top. ";
        return os switch
        {
            "Linux" => steam + "On a Steam Deck, a game on the SD card lives under /run/media/. GOG: it is usually in ~/GOG Games/Stardew Valley/game. The folder must contain \"Stardew Valley.dll\".",
            "Mac" => steam + "The game folder is inside the app: Stardew Valley.app/Contents/MacOS.",
            _ => steam + "GOG: it is usually under GOG Games. Xbox app: C:\\Program Files\\ModifiableWindowsApps\\Stardew Valley, which is the copy that allows mods.",
        };
    }

    public static string GamePathExample(string os) => os switch
    {
        "Linux" => "/home/you/.local/share/Steam/steamapps/common/Stardew Valley",
        "Mac" => "/Users/you/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS",
        _ => "D:\\SteamLibrary\\steamapps\\common\\Stardew Valley",
    };
}
