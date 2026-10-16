using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Sprinkler.Core;

internal enum OsKind
{
    Windows,
    Linux,
    Mac,
}

/// <summary>
/// Finds Stardew Valley installs. The locations follow SMAPI's own installer (GameScanner in SMAPI.Toolkit),
/// so a game SMAPI can find is found here too, on Windows, Linux (including Steam Deck and Flatpak Steam) and macOS.
/// </summary>
internal static partial class GameLocator
{
    public static OsKind CurrentOs => Platform.IsWindows ? OsKind.Windows : Platform.IsMac ? OsKind.Mac : OsKind.Linux;

    public static List<GameInstall> FindAll() => FindAll(CurrentOs, Platform.HomeDir);

    internal static List<GameInstall> FindAll(OsKind os, string home)
    {
        var found = new Dictionary<string, GameInstall>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, store) in Candidates(os, home))
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var install = Inspect(path, store);
            if (install is not null)
                found.TryAdd(Path.GetFullPath(install.GamePath).TrimEnd('\\', '/'), install);
        }
        return found.Values.ToList();
    }

    /// <summary>Every place a game might be, best guesses first. Nothing here touches the game, it only builds paths.</summary>
    internal static IEnumerable<(string Path, string Store)> Candidates(OsKind os, string home)
    {
        // a path the player set for SMAPI development in stardewvalley.targets
        yield return (CustomTargetsPath(home) ?? "", "Custom");

        if (os == OsKind.Windows)
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var c in WindowsCandidates())
                    yield return c;
            }
            yield break;
        }

        foreach (var c in UnixCandidates(os, home))
            yield return c;
    }

    /// <summary>Linux and macOS locations, from SMAPI's GameScanner plus Steam library folders read from libraryfolders.vdf.</summary>
    internal static IEnumerable<(string Path, string Store)> UnixCandidates(OsKind os, string home)
    {
        var macSuffix = os == OsKind.Mac ? Path.Combine("Contents", "MacOS") : "";

        if (os == OsKind.Linux)
        {
            yield return (Path.Combine(home, "GOG Games", "Stardew Valley", "game"), "GOG");
            yield return (Path.Combine(home, ".steam", "steam", "steamapps", "common", "Stardew Valley"), "Steam");
            yield return (Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Stardew Valley"), "Steam");
            yield return (Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam", "steamapps", "common", "Stardew Valley"), "Steam (Flatpak)");
        }
        else
        {
            yield return ("/Applications/Stardew Valley.app/Contents/MacOS", "Manual");
            yield return (Path.Combine(home, "Library", "Application Support", "Steam", "steamapps", "common", "Stardew Valley", "Contents", "MacOS"), "Steam");
        }

        // other Steam libraries (a second drive, or the Steam Deck's SD card)
        foreach (var steamRoot in UnixSteamRoots(os, home))
            foreach (var library in LibraryPaths(steamRoot))
                yield return (Path.Combine(library, "steamapps", "common", "Stardew Valley", macSuffix), "Steam");
    }

    private static IEnumerable<string> UnixSteamRoots(OsKind os, string home)
    {
        if (os == OsKind.Linux)
        {
            yield return Path.Combine(home, ".steam", "steam");
            yield return Path.Combine(home, ".local", "share", "Steam");
            yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam");
        }
        else
        {
            yield return Path.Combine(home, "Library", "Application Support", "Steam");
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(string Path, string Store)> WindowsCandidates()
    {
        // Steam: the game's own uninstall entry, then every library folder
        yield return (RegistryValue(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 413150", "InstallLocation") ?? "", "Steam");

        var steam = RegistryValue(@"SOFTWARE\Valve\Steam", "SteamPath", Registry.CurrentUser)
            ?? RegistryValue(@"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
            ?? RegistryValue(@"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(steam))
        {
            steam = steam.Replace('/', '\\');
            yield return (Path.Combine(steam, "steamapps", "common", "Stardew Valley"), "Steam");
            foreach (var library in LibraryPaths(steam))
                yield return (Path.Combine(library, "steamapps", "common", "Stardew Valley"), "Steam");
        }

        // GOG
        yield return (RegistryValue(@"SOFTWARE\WOW6432Node\GOG.com\Games\1453375253", "PATH") ?? "", "GOG");
        foreach (var programFiles in new[] { @"C:\Program Files", @"C:\Program Files (x86)" })
        {
            yield return ($@"{programFiles}\GalaxyClient\Games\Stardew Valley", "GOG");
            yield return ($@"{programFiles}\GOG Galaxy\Games\Stardew Valley", "GOG");
            yield return ($@"{programFiles}\GOG Games\Stardew Valley", "GOG");
            yield return ($@"{programFiles}\Steam\steamapps\common\Stardew Valley", "Steam");
        }

        // Xbox app: the install drive can change, the folder on it cannot. This is the mods-enabled path, not the read-only one.
        for (var letter = 'C'; letter <= 'Z'; letter++)
            yield return ($@"{letter}:\Program Files\ModifiableWindowsApps\Stardew Valley", "Xbox");

        // common manual layouts on any fixed drive
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = drive.RootDirectory.FullName;
            yield return (Path.Combine(root, "SteamLibrary", "steamapps", "common", "Stardew Valley"), "Steam");
            yield return (Path.Combine(root, "Steam", "steamapps", "common", "Stardew Valley"), "Steam");
            yield return (Path.Combine(root, "GOG Games", "Stardew Valley"), "GOG");
            yield return (Path.Combine(root, "Games", "Stardew Valley"), "Manual");
        }
    }

    /// <summary>Reads GamePath from the optional stardewvalley.targets file that SMAPI's tools also honor.</summary>
    internal static string? CustomTargetsPath(string home)
    {
        try
        {
            var file = Path.Combine(home, "stardewvalley.targets");
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
        && (File.Exists(Path.Combine(folder, "Stardew Valley.exe")) || File.Exists(Path.Combine(folder, "StardewValley.exe")));

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
            var gameVersion = TrimVersion(AssemblyVersion(gameDll), 3);

            string? smapiVersion = null;
            var smapi = Path.Combine(gamePath, "StardewModdingAPI.dll");
            if (!File.Exists(smapi))
                smapi = Path.Combine(gamePath, "StardewModdingAPI.exe");
            if (File.Exists(smapi))
                smapiVersion = TrimVersion(ProductVersion(smapi), 3, keepPrerelease: true);

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
            _running = GameProcess.IsRunning();
            _runningCheckedAt = DateTime.UtcNow;
            return _running;
        }
    }

    private static readonly object RunningGate = new();
    private static DateTime _runningCheckedAt = DateTime.MinValue;
    private static bool _running;

    /// <summary>Folders Steam knows about, from libraryfolders.vdf. Missing or unreadable files give an empty list.</summary>
    internal static IEnumerable<string> LibraryPaths(string steamRoot)
    {
        var results = new List<string> { steamRoot };
        foreach (var vdf in new[] { Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"), Path.Combine(steamRoot, "config", "libraryfolders.vdf") })
        {
            string text;
            try
            {
                if (!File.Exists(vdf))
                    continue;
                text = File.ReadAllText(vdf);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (Match m in LibraryPathRegex().Matches(text))
                results.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        return results.Distinct(StringComparer.Ordinal);
    }

    /// <summary>The assembly version, read without running the file. Works on every platform.</summary>
    private static string? AssemblyVersion(string path)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path).Version?.ToString();
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>SMAPI's release version, which can carry a prerelease tag. Windows keeps it in the file's version resource.</summary>
    private static string? ProductVersion(string path)
    {
        if (Platform.IsWindows)
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var v = info.ProductVersion ?? info.FileVersion;
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }
        return AssemblyVersion(path);
    }

    [SupportedOSPlatform("windows")]
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
