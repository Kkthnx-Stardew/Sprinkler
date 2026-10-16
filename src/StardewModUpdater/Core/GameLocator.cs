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

        foreach (var lib in SteamLibraries())
            Add(Path.Combine(lib, "steamapps", "common", "Stardew Valley"), "Steam");

        Add(RegistryValue(@"SOFTWARE\WOW6432Node\GOG.com\Games\1453375253", "path"), "GOG");
        Add(RegistryValue(@"SOFTWARE\GOG.com\Games\1453375253", "path"), "GOG");

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = drive.RootDirectory.FullName;
            Add(Path.Combine(root, "XboxGames", "Stardew Valley", "Content"), "Xbox");
            Add(Path.Combine(root, "SteamLibrary", "steamapps", "common", "Stardew Valley"), "Steam");
            Add(Path.Combine(root, "Steam", "steamapps", "common", "Stardew Valley"), "Steam");
            Add(Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common", "Stardew Valley"), "Steam");
            Add(Path.Combine(root, "Program Files", "Steam", "steamapps", "common", "Stardew Valley"), "Steam");
            Add(Path.Combine(root, "GOG Games", "Stardew Valley"), "GOG");
            Add(Path.Combine(root, "Games", "Stardew Valley"), "Manual");
        }

        return found.Values.ToList();
    }

    /// <summary>Checks a folder is a game install and reads the game and SMAPI versions.</summary>
    public static GameInstall? Inspect(string gamePath, string store = "Manual")
    {
        try
        {
            if (!Directory.Exists(gamePath))
                return null;

            var hasGame = File.Exists(Path.Combine(gamePath, "Stardew Valley.dll"))
                || File.Exists(Path.Combine(gamePath, "Stardew Valley.exe"));
            if (!hasGame)
                return null;

            string? gameVersion = null;
            foreach (var name in new[] { "Stardew Valley.dll", "Stardew Valley.exe" })
            {
                var p = Path.Combine(gamePath, name);
                if (File.Exists(p))
                {
                    gameVersion = TrimVersion(FileVersionInfo.GetVersionInfo(p).FileVersion, 3);
                    break;
                }
            }

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
