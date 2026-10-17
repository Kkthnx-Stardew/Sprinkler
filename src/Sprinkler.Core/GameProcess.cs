using System.Diagnostics;

namespace Sprinkler.Core;

/// <summary>Detects whether the game is running and starts it through SMAPI, on every platform.</summary>
internal static class GameProcess
{
    /// <summary>File names (without folders) that mean the game or SMAPI is running, under native Linux/macOS, Windows or Proton.</summary>
    private static readonly string[] GameNames =
    {
        "Stardew Valley", "Stardew Valley.exe", "StardewValley", "StardewValley.exe",
        "StardewModdingAPI", "StardewModdingAPI.exe", "StardewModdingAPI.dll",
    };

    public static bool IsRunning()
    {
        try
        {
            if (Platform.IsLinux)
                return LinuxProcessIsRunning("/proc", Environment.ProcessId);

            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    var name = SafeName(p);
                    if (name is not null && (GameNames.Contains(name, StringComparer.OrdinalIgnoreCase) || GameNames.Contains(name + ".exe", StringComparer.OrdinalIgnoreCase)))
                        return true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not list processes: " + ex.Message);
        }
        return false;
    }

    private static string? SafeName(Process p)
    {
        try { return p.ProcessName; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>
    /// Linux names a process by a 15 character "comm" value, which cuts "StardewModdingAPI" short, so the full
    /// command line in /proc is read instead. Under Proton the game shows up as a Windows path, which is matched too.
    /// </summary>
    internal static bool LinuxProcessIsRunning(string procRoot, int ownPid)
    {
        foreach (var dir in Directory.EnumerateDirectories(procRoot))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == ownPid)
                continue;
            byte[] raw;
            try
            {
                raw = File.ReadAllBytes(Path.Combine(dir, "cmdline"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (CommandLineMatches(raw))
                return true;
        }
        return false;
    }

    internal static bool CommandLineMatches(byte[] rawCmdline)
    {
        if (rawCmdline.Length == 0)
            return false;
        foreach (var arg in System.Text.Encoding.UTF8.GetString(rawCmdline).Split('\0', StringSplitOptions.RemoveEmptyEntries).Take(4))
        {
            var file = arg[(arg.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
            if (GameNames.Contains(file, StringComparer.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True when this install can be started from here.</summary>
    public static bool CanLaunch(GameInstall game)
    {
        if (Platform.IsWindows)
            return game.HasSmapi;
        return UnixLauncher(game) is not null || Platform.Which("steam") is not null || game.Store.StartsWith("Steam", StringComparison.Ordinal);
    }

    /// <summary>Starts the game through SMAPI. Returns a plain message when it cannot, or null on success.</summary>
    public static string? Launch(GameInstall game)
    {
        if (IsRunning())
            return Loc.T("core.game_running");

        if (Platform.IsWindows)
        {
            var exe = Path.Combine(game.GamePath, "StardewModdingAPI.exe");
            if (!File.Exists(exe))
                return "SMAPI is not installed in this game folder. Get it from smapi.io first.";
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = game.GamePath, UseShellExecute = true })?.Dispose();
            return null;
        }

        var launcher = UnixLauncher(game);
        if (launcher is not null)
        {
            try
            {
                var psi = new ProcessStartInfo(launcher) { WorkingDirectory = game.GamePath, UseShellExecute = false };
                Process.Start(psi)?.Dispose();
                return null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Write("Direct launch failed, trying Steam: " + ex.Message);
            }
        }

        // Steam can start the game with the launch options the player already set up (this also covers Proton)
        if (Platform.Which("steam") is not null || game.Store.StartsWith("Steam", StringComparison.Ordinal))
        {
            Platform.OpenUrl("steam://rungameid/413150");
            return null;
        }
        return "SMAPI is not installed in this game folder. Get it from smapi.io first.";
    }

    /// <summary>The native SMAPI launcher that SMAPI's installer puts in the game folder on Linux and macOS.</summary>
    private static string? UnixLauncher(GameInstall game)
    {
        var launcher = Path.Combine(game.GamePath, "StardewModdingAPI");
        return Platform.IsExecutable(launcher) ? launcher : null;
    }
}
