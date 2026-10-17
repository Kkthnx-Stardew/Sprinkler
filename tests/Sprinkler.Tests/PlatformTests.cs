using System.Text;
using Sprinkler.Core;

using Xunit;

namespace Sprinkler.Tests;

public class LinuxAndMacLocationTests
{
    private static string Under(string home, params string[] parts) => Path.Combine(new[] { home }.Concat(parts).ToArray());

    private static string FakeHome() => Path.Combine(Path.GetTempPath(), "nohome-" + Guid.NewGuid().ToString("N")[..6]);

    [Fact]
    public void LinuxCandidatesMatchSmapisOwnList()
    {
        var home = FakeHome();
        var paths = GameLocator.UnixCandidates(OsKind.Linux, home).Select(c => c.Path).ToList();
        Assert.Contains(Under(home, "GOG Games", "Stardew Valley", "game"), paths);
        Assert.Contains(Under(home, ".steam", "steam", "steamapps", "common", "Stardew Valley"), paths);
        Assert.Contains(Under(home, ".local", "share", "Steam", "steamapps", "common", "Stardew Valley"), paths);
        Assert.Contains(Under(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam", "steamapps", "common", "Stardew Valley"), paths);
    }

    [Fact]
    public void MacCandidatesPointInsideTheAppBundle()
    {
        var home = FakeHome();
        var paths = GameLocator.UnixCandidates(OsKind.Mac, home).Select(c => c.Path).ToList();
        Assert.Contains(paths, p => p.Replace('\\', '/') == "/Applications/Stardew Valley.app/Contents/MacOS");
        Assert.Contains(Under(home, "Library", "Application Support", "Steam", "steamapps", "common", "Stardew Valley", "Contents", "MacOS"), paths);
    }

    [Fact]
    public void FindsAGameOnASecondSteamLibraryLikeTheSteamDecksSdCard()
    {
        using var d = new TempDir();
        var home = d.Combine("home");
        var sd = d.Combine("sdcard");
        Directory.CreateDirectory(Under(home, ".steam", "steam", "steamapps"));
        File.WriteAllText(Under(home, ".steam", "steam", "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n  \"0\"\n  {\n    \"path\"\t\t\"" + sd.Replace("\\", "\\\\") + "\"\n  }\n}\n");
        var game = Path.Combine(sd, "steamapps", "common", "Stardew Valley");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Stardew Valley.dll"), "");

        var found = GameLocator.FindAll(OsKind.Linux, home);
        var install = Assert.Single(found);
        Assert.Equal(Path.GetFullPath(game), Path.GetFullPath(install.GamePath));
        Assert.Equal(Path.Combine(game, "Mods"), install.ModsPath);
    }

    [Fact]
    public void ReadsTheGamePathFromStardewvalleyTargets()
    {
        using var d = new TempDir();
        var game = d.Combine("custom game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Stardew Valley.dll"), "");
        File.WriteAllText(d.Combine("stardewvalley.targets"),
            $"<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup><GamePath>{game}</GamePath></PropertyGroup></Project>");
        Assert.Equal(game, GameLocator.CustomTargetsPath(d.Path));
        Assert.Single(GameLocator.FindAll(OsKind.Linux, d.Path));
    }

    [Fact]
    public void AFolderWithoutTheGameDllIsNotAnInstall()
    {
        using var d = new TempDir();
        d.Write("game/StardewValley.exe", "");
        Assert.Null(GameLocator.Inspect(d.Combine("game")));
        Assert.True(GameLocator.IsLegacyInstall(d.Combine("game")));
    }
}

public class LinuxProcessTests
{
    private static byte[] Cmd(params string[] args) => Encoding.UTF8.GetBytes(string.Join('\0', args) + '\0');

    [Theory]
    [InlineData("/home/deck/.local/share/Steam/steamapps/common/Stardew Valley/StardewModdingAPI")]
    [InlineData("/home/user/stardew/StardewValley")]
    [InlineData("Z:\\home\\deck\\steamapps\\common\\Stardew Valley\\StardewModdingAPI.exe")]
    public void RecognizesTheGameAndSmapiUnderLinuxAndProton(string executable) =>
        Assert.True(GameProcess.CommandLineMatches(Cmd(executable)));

    [Fact]
    public void RecognizesSmapiLaunchedThroughDotnet() =>
        Assert.True(GameProcess.CommandLineMatches(Cmd("dotnet", "/games/Stardew Valley/StardewModdingAPI.dll")));

    [Theory]
    [InlineData("/usr/bin/firefox")]
    [InlineData("/opt/StardewModUpdater/StardewModUpdater")]
    [InlineData("/usr/bin/nano")]
    public void IgnoresOtherPrograms(string executable) =>
        Assert.False(GameProcess.CommandLineMatches(Cmd(executable)));

    [Theory]
    [InlineData("code", "/home/deck/.local/share/Steam/steamapps/common/Stardew Valley")]
    [InlineData("dolphin", "/home/deck/.local/share/Steam/steamapps/common/Stardew Valley")]
    [InlineData("/usr/bin/nano", "/games/Stardew Valley")]
    public void AnEditorOrFileManagerOpenOnTheGameFolderIsNotTheGame(string program, string folder) =>
        Assert.False(GameProcess.CommandLineMatches(Cmd(program, folder)));

    [Fact]
    public void ProtonStartingTheGameExecutableStillCounts() =>
        Assert.True(GameProcess.CommandLineMatches(Cmd("/steam/proton", "waitforexitandrun", "Z:\\games\\Stardew Valley\\Stardew Valley.exe")));

    [Fact]
    public void ScansAProcFolderAndSkipsItself()
    {
        using var d = new TempDir();
        Directory.CreateDirectory(d.Combine("1234"));
        File.WriteAllBytes(d.Combine("1234", "cmdline"), Cmd("/games/StardewModdingAPI"));
        Directory.CreateDirectory(d.Combine("self"));
        Directory.CreateDirectory(d.Combine("99"));

        Assert.True(GameProcess.LinuxProcessIsRunning(d.Path, ownPid: 1));
        // the entry for this very process is ignored, and a process folder without a readable cmdline is skipped
        Assert.False(GameProcess.LinuxProcessIsRunning(d.Path, ownPid: 1234));
    }
}

public class PlatformHelperTests
{
    [Fact]
    public void ReadsTheDownloadsFolderFromXdgUserDirs()
    {
        using var d = new TempDir();
        d.Write("config/user-dirs.dirs", "# comment\nXDG_DESKTOP_DIR=\"$HOME/Desktop\"\nXDG_DOWNLOAD_DIR=\"$HOME/Telechargements\"\n");
        var home = d.Combine("home");
        var folder = Platform.ReadXdgDownloads(home, d.Combine("config"));
        Assert.Equal(home + "/Telechargements", folder);
        Assert.Null(Platform.ReadXdgDownloads(home, d.Combine("none")));
    }

    [Fact]
    public void TheNxmDesktopFileQuotesThePathAndAsksForTheLink()
    {
        var text = NxmRegistration.DesktopFileText("/home/me/My Apps/StardewModUpdater");
        Assert.Contains("Exec=\"/home/me/My Apps/StardewModUpdater\" %u", text);
        Assert.Contains("MimeType=x-scheme-handler/nxm;", text);
        Assert.Contains("NoDisplay=true", text);
        Assert.Contains("100%%", NxmRegistration.DesktopFileText("/opt/100%/app"));
    }

    [Fact]
    public void AnOldDataFolderIsCarriedOverOnce()
    {
        using var d = new TempDir();
        d.Write("old/settings.json", "{\"nexusKeyProtected\":\"abc\"}");
        d.Write("old/history.json", "[]");
        d.Write("old/backups/a.zip", "zip");
        d.Write("old/window-profiles/x/junk", "junk");
        using var held = new FileStream(d.Combine("old", "instance.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None); // a running older copy

        Platform.MigrateLegacyDataDir(d.Combine("old"), d.Combine("new"));

        Assert.Equal("{\"nexusKeyProtected\":\"abc\"}", File.ReadAllText(d.Combine("new", "settings.json")));
        Assert.True(File.Exists(d.Combine("new", "backups", "a.zip")));
        // Only Windows refuses to move a folder that holds an open file, which is what makes the copy path (and its skipping
        // of run-time files) run. Linux simply moves the whole folder.
        if (OperatingSystem.IsWindows())
        {
            Assert.False(File.Exists(d.Combine("new", "instance.lock")));
            Assert.False(Directory.Exists(d.Combine("new", "window-profiles")));
        }
    }

    [Fact]
    public void AnExistingNewDataFolderIsNeverOverwritten()
    {
        using var d = new TempDir();
        d.Write("old/settings.json", "old");
        d.Write("new/settings.json", "new");
        Platform.MigrateLegacyDataDir(d.Combine("old"), d.Combine("new"));
        Assert.Equal("new", File.ReadAllText(d.Combine("new", "settings.json")));
    }

    [Fact]
    public void SmapiPlatformNameIsOneSmapiAccepts() =>
        Assert.Contains(Platform.SmapiName, new[] { "Windows", "Linux", "Mac" });

    [Fact]
    public void OnlyOneCopyOfTheAppRunsPerDataFolder()
    {
        using var first = new SingleInstance();
        using var second = new SingleInstance();
        Assert.True(first.IsFirst);
        Assert.False(second.IsFirst);
    }

    [Fact]
    public void TheFileSecretStoreRoundTripsAndDeletes()
    {
        using var d = new TempDir();
        var store = new FileSecretStore(d.Combine("sub", "nexus.key"));
        Assert.Null(store.Read());
        store.Write("  abc123  ");
        Assert.Equal("abc123", store.Read());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(d.Combine("sub", "nexus.key")));
        store.Write(null);
        Assert.Null(store.Read());
        Assert.False(File.Exists(d.Combine("sub", "nexus.key")));
    }
}
