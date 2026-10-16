using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StardewModUpdater.Core;

/// <summary>Finds Stardew Valley installs for Steam, GOG, Xbox and common manual locations.</summary>
internal static partial class GameLocator
{
    public static List<GameInstall> FindAll()
    {
        var found = new Dictionary<string, GameInstall>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, string store)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            var install = Inspect(path, store);
            if (install is not null)
                found.TryAdd(Path.GetFullPath(install.GamePath).TrimEnd('\\'), install);
        }

        // These locations follow SMAPI's own installer (GameScanner in SMAPI.Toolkit), so a game SMAPI can find is found here too.

        // a path the player set for SMAPI development in %USERPROFILE%\stardewvalley.targets
        Add(CustomTargetsPath(), "Custom");

        // Steam: the game's own uninstall entry, then every library folder
        Add(RegistryValue(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 413150", "InstallLocation"), "Steam");
        foreach (var lib in SteamLibraries())
            Add(Path.Combine(lib, "steamapps", "common", "Stardew Valley"), "Steam");

        // GOG
        Add(RegistryValue(@"SOFTWARE\WOW6432Node\GOG.com\Games\1453375253", "PATH"), "GOG");

        foreach (var programFiles in new[] { @"C:\Program Files", @"C:\Program Files (x86)" })
        {
            Add($@"{programFiles}\GalaxyClient\Games\Stardew Valley", "GOG");
            Add($@"{programFiles}\GOG Galaxy\Games\Stardew Valley", "GOG");
            Add($@"{programFiles}\GOG Games\Stardew Valley", "GOG");
            Add($@"{programFiles}\Steam\steamapps\common\Stardew Valley", "Steam");
        }

        // Xbox app: the install drive can change, the folder on it cannot. This is the mods-enabled path, not the read-only one.
        for (var letter = 'C'; letter <= 'Z'; letter++)
            Add($@"{letter}:\Program Files\ModifiableWindowsApps\Stardew Valley", "Xbox");

        // common manual layouts on any fixed drive
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = drive.RootDirectory.FullName;
            Add(Path.Combine(root, "SteamLibrary", "steamapps", "common", "Stardew Valley"), "Steam");
            Add(Path.Combine(root, "Steam", "steamapps", "common", "Stardew Valley"), "Steam");
            Add(Path.Combine(root, "GOG Games", "Stardew Valley"), "GOG");
            Add(Path.Combine(root, "Games", "Stardew Valley"), "Manual");
        }

        return found.Values.ToList();
    }

    /// <summary>Reads GamePath from the optional stardewvalley.targets file that SMAPI's tools also honor.</summary>
    private static string? CustomTargetsPath()
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "stardewvalley.targets");
            if (!File.Exists(file))
                return null;
            var root = System.Xml.Linq.XElement.Load(file);
            return root.Descendants().FirstOrDefault(e => e.Name.LocalName == "GamePath")?.Value.Trim();
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True for a game older than 1.6, which SMAPI 4 and current mods do not support.</summary>
    public static bool IsLegacyInstall(string folder) =>
        Directory.Exists(folder)
        && !File.Exists(Path.Combine(folder, "Stardew Valley.dll"))
        && File.Exists(Path.Combine(folder, "Stardew Valley.exe"));

    /// <summary>Checks a folder is a game install and reads the game and SMAPI versions.</summary>
    public static GameInstall? Inspect(string gamePath, string store = "Manual")
    {
        try
        {
            if (!Directory.Exists(gamePath))
                return null;

            // SMAPI treats a folder as the game when Stardew Valley.dll exists (game 1.6 and newer)
            var gameDll = Path.Combine(gamePath, "Stardew Valley.dll");
            if (!File.Exists(gameDll))
                return null;
            var gameVersion = TrimVersion(FileVersionInfo.GetVersionInfo(gameDll).FileVersion, 3);

            string? smapiVersion = null;
            var smapi = Path.Combine(gamePath, "StardewModdingAPI.dll");
            if (!File.Exists(smapi))
                smapi = Path.Combine(gamePath, "StardewModdingAPI.exe");
            if (File.Exists(smapi))
            {
                var info = FileVersionInfo.GetVersionInfo(smapi);
                smapiVersion = TrimVersion(info.ProductVersion ?? info.FileVersion, 3, keepPrerelease: true);
            }

            return new GameInstall(gamePath, Path.Combine(gamePath, "Mods"), store, gameVersion, smapiVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True when the game or SMAPI is running, since mods cannot be swapped safely then.</summary>
    public static bool IsGameRunning(bool fresh = false)
    {
        lock (RunningGate)
        {
            if (!fresh && DateTime.UtcNow - _runningCheckedAt < TimeSpan.FromSeconds(2))
                return _running;
            _running = ProbeRunning();
            _runningCheckedAt = DateTime.UtcNow;
            return _running;
        }
    }

    private static readonly object RunningGate = new();
    private static DateTime _runningCheckedAt = DateTime.MinValue;
    private static bool _running;

    private static bool ProbeRunning()
    {
        foreach (var name in new[] { "Stardew Valley", "StardewModdingAPI" })
        {
            var procs = Process.GetProcessesByName(name);
            var running = procs.Length > 0;
            foreach (var p in procs)
                p.Dispose();
            if (running)
                return true;
        }
        return false;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var steam = RegistryValue(@"SOFTWARE\Valve\Steam", "SteamPath", Registry.CurrentUser)
            ?? RegistryValue(@"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
            ?? RegistryValue(@"SOFTWARE\Valve\Steam", "InstallPath");
        if (string.IsNullOrWhiteSpace(steam))
            yield break;

        steam = steam.Replace('/', '\\');
        yield return steam;

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            yield break;

        string text;
        try
        {
            text = File.ReadAllText(vdf);
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (Match m in LibraryPathRegex().Matches(text))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    private static string? RegistryValue(string subKey, string name, RegistryKey? hive = null)
    {
        try
        {
            using var key = (hive ?? Registry.LocalMachine).OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? TrimVersion(string? raw, int parts, bool keepPrerelease = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var pre = "";
        var s = raw.Trim();
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = keepPrerelease ? s[dash..] : "";
            s = s[..dash];
        }
        var nums = s.Split('.').Take(parts);
        return string.Join('.', nums) + pre;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPathRegex();
}
