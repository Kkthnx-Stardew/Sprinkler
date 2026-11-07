using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Sprinkler.Core;

internal enum LaunchMode { WithMods, Vanilla }

/// <summary>How a launch will actually start the game.</summary>
internal enum LaunchPlan { Unavailable, Steam, DirectSmapi, DirectGame }

internal sealed record LaunchResult(string? Error, LaunchPlan Plan, bool SteamOptionMissing)
{
    public static LaunchResult Fail(string error) => new(error, LaunchPlan.Unavailable, false);
}

/// <summary>
/// Decides between starting the game through Steam and starting it from its folder.
/// Steam is used only when it is already open (it is never started just to play), and for a modded start only when the
/// player has set SMAPI as the game's Steam launch option, because without that Steam would start the plain game.
/// </summary>
internal static class SteamLaunch
{
    public const string AppId = "413150";

    private static readonly object Gate = new();
    private static DateTime _runningAt = DateTime.MinValue;
    private static bool _running;
    private static DateTime _optionsAt = DateTime.MinValue;
    private static string? _options;

    /// <summary>
    /// True for a game folder inside a Steam library (".../steamapps/common/Stardew Valley"). The store name is not enough:
    /// a folder saved in the settings is reopened as "Manual", but it is still the Steam copy.
    /// </summary>
    public static bool IsSteamFolder(string gamePath) =>
        gamePath.Replace('\\', '/').Contains("/steamapps/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pure decision, so every case can be tested without Steam.</summary>
    public static LaunchPlan Plan(LaunchMode mode, bool steamInstall, bool steamRunning, bool optionStartsSmapi, bool hasSmapi, bool canStartGameDirectly)
    {
        if (mode == LaunchMode.WithMods)
        {
            if (!hasSmapi)
                return LaunchPlan.Unavailable;
            return steamInstall && steamRunning && optionStartsSmapi ? LaunchPlan.Steam : LaunchPlan.DirectSmapi;
        }

        // the plain game: through Steam when that really starts the plain game, otherwise from the folder
        if (steamInstall && steamRunning && !optionStartsSmapi)
            return LaunchPlan.Steam;
        return canStartGameDirectly ? LaunchPlan.DirectGame : LaunchPlan.Unavailable;
    }

    /// <summary>True when Steam is running. Cached for a few seconds, because the screen asks several times a second.</summary>
    public static bool IsSteamRunning()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _runningAt < TimeSpan.FromSeconds(3))
                return _running;
            _runningAt = DateTime.UtcNow;
            try
            {
                var found = false;
                foreach (var name in new[] { "steam", "steam_osx" })
                {
                    var list = Process.GetProcessesByName(name);
                    found |= list.Length > 0;
                    foreach (var p in list)
                        p.Dispose();
                }
                return _running = found;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                return _running = false;
            }
        }
    }

    /// <summary>The launch options saved for Stardew Valley in Steam, or null when none can be read. Cached for half a minute.</summary>
    public static string? ReadSavedLaunchOptions()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _optionsAt < TimeSpan.FromSeconds(30))
                return _options;
            _optionsAt = DateTime.UtcNow;
            try
            {
                foreach (var root in SteamRoots())
                {
                    var userdata = Path.Combine(root, "userdata");
                    if (!Directory.Exists(userdata))
                        continue;
                    // the player who used Steam last is the one whose file changed last
                    var files = Directory.EnumerateDirectories(userdata)
                        .Select(d => Path.Combine(d, "config", "localconfig.vdf"))
                        .Where(File.Exists)
                        .OrderByDescending(File.GetLastWriteTimeUtc);
                    foreach (var file in files)
                    {
                        var text = ReadSharedly(file);
                        if (text is not null && ReadLaunchOptions(text) is { } options)
                            return _options = options;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write("Could not read Steam's launch options: " + ex.Message);
            }
            return _options = null;
        }
    }

    // Steam keeps this file open while it runs, so it has to be read without asking for exclusive access.
    private static string? ReadSharedly(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SteamRoots()
    {
        if (Platform.IsWindows && OperatingSystem.IsWindows())
        {
            string? path = null;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
                path = key?.GetValue("SteamPath") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
            if (!string.IsNullOrWhiteSpace(path))
                yield return path.Replace('/', '\\');
            yield break;
        }

        var home = Platform.HomeDir;
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam");
        yield return Path.Combine(home, "Library", "Application Support", "Steam");
    }

    /// <summary>
    /// Reads the LaunchOptions saved for Stardew Valley out of Steam's localconfig.vdf text, or null when the game has none.
    /// The file is a nested "key" "value" format, so the game's block is found by matching braces.
    /// </summary>
    internal static string? ReadLaunchOptions(string vdf)
    {
        var at = 0;
        while (true)
        {
            var key = vdf.IndexOf("\"" + AppId + "\"", at, StringComparison.Ordinal);
            if (key < 0)
                return null;
            var open = vdf.IndexOf('{', key);
            var between = open < 0 ? "x" : vdf[(key + AppId.Length + 2)..open];
            if (open < 0 || !string.IsNullOrWhiteSpace(between))
            {
                at = key + 1; // the number appears as a value somewhere, not as a block name
                continue;
            }

            var depth = 0;
            var end = open;
            for (; end < vdf.Length; end++)
            {
                if (vdf[end] == '{') depth++;
                else if (vdf[end] == '}' && --depth == 0) break;
            }
            var block = vdf[open..Math.Min(end + 1, vdf.Length)];
            var m = Regex.Match(block, "\"LaunchOptions\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (m.Success)
                return m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
            at = key + 1;
        }
    }

    /// <summary>True when the launch options start SMAPI, which is what lets Steam start the game with mods.</summary>
    public static bool OptionsStartSmapi(string? options) =>
        !string.IsNullOrWhiteSpace(options) && options.Contains("StardewModdingAPI", StringComparison.OrdinalIgnoreCase);
}
