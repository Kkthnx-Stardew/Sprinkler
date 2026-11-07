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
        var args = System.Text.Encoding.UTF8.GetString(rawCmdline).Split('\0', StringSplitOptions.RemoveEmptyEntries).Take(4).ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            var file = args[i][(args[i].LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
            if (!GameNames.Contains(file, StringComparer.OrdinalIgnoreCase))
                continue;
            // The program itself is the first word. After that only a program file counts (dotnet X.dll, proton X.exe), so a
            // file manager or editor that was opened on the "Stardew Valley" folder is not taken for the game.
            if (i == 0 || file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The program that starts the plain game from its folder, or null when there is none to start directly.</summary>
    internal static string? VanillaExecutable(GameInstall game)
    {
        if (Platform.IsWindows)
        {
            foreach (var name in new[] { "Stardew Valley.exe", "StardewValley.exe" })
            {
                var exe = Path.Combine(game.GamePath, name);
                if (File.Exists(exe))
                    return exe;
            }
            return null;
        }
        var native = Path.Combine(game.GamePath, "StardewValley");
        return Platform.IsExecutable(native) ? native : null;
    }

    /// <summary>True when this install can be started from here, with or without mods.</summary>
    public static bool CanLaunch(GameInstall game)
    {
        if (VanillaExecutable(game) is not null)
            return true;
        if (Platform.IsWindows)
            return game.HasSmapi;
        return UnixLauncher(game) is not null || Platform.Which("steam") is not null || game.Store.StartsWith("Steam", StringComparison.Ordinal);
    }

    private static bool IsSteamInstall(GameInstall game) => game.Store.StartsWith("Steam", StringComparison.Ordinal) || SteamLaunch.IsSteamFolder(game.GamePath);

    /// <summary>What a launch would do right now, for the menu to say so before anyone clicks.</summary>
    public static (LaunchPlan WithMods, LaunchPlan Vanilla, bool SteamNeedsSetup) Describe(GameInstall game)
    {
        var steamInstall = IsSteamInstall(game);
        var running = steamInstall && SteamLaunch.IsSteamRunning();
        var optionSmapi = steamInstall && SteamLaunch.OptionsStartSmapi(SteamLaunch.ReadSavedLaunchOptions());
        var hasSmapi = game.HasSmapi || UnixLauncher(game) is not null;
        var direct = VanillaExecutable(game) is not null;
        var mods = SteamLaunch.Plan(LaunchMode.WithMods, steamInstall, running, optionSmapi, hasSmapi, direct);
        // Linux under Proton has no SMAPI program in the folder: Steam's own launch options are the only way
        if (mods == LaunchPlan.Unavailable && !Platform.IsWindows && (Platform.Which("steam") is not null || steamInstall))
            mods = LaunchPlan.Steam;
        var vanilla = SteamLaunch.Plan(LaunchMode.Vanilla, steamInstall, running, optionSmapi, hasSmapi, direct);
        return (mods, vanilla, steamInstall && game.HasSmapi && !optionSmapi);
    }

    /// <summary>The text to paste into the game's Steam launch options so Steam can start SMAPI.</summary>
    public static string SteamLaunchOptionText(GameInstall game)
    {
        var program = Platform.IsWindows ? "StardewModdingAPI.exe" : "StardewModdingAPI";
        return "\"" + Path.Combine(game.GamePath, program) + "\" %command%";
    }

    /// <summary>Starts the game, with or without mods. Returns what happened, or a plain message when it cannot start.</summary>
    public static LaunchResult Launch(GameInstall game, LaunchMode mode = LaunchMode.WithMods)
    {
        if (IsRunning())
            return LaunchResult.Fail(Loc.T("core.game_running"));

        var (mods, vanilla, _) = Describe(game);
        var plan = mode == LaunchMode.WithMods ? mods : vanilla;
        try
        {
            switch (plan)
            {
                case LaunchPlan.Steam:
                    Platform.OpenUrl("steam://rungameid/" + SteamLaunch.AppId);
                    return new LaunchResult(null, plan, false);

                case LaunchPlan.DirectGame:
                    {
                        var exe = VanillaExecutable(game)!;
                        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = game.GamePath, UseShellExecute = Platform.IsWindows })?.Dispose();
                        return new LaunchResult(null, plan, false);
                    }

                case LaunchPlan.DirectSmapi:
                    {
                        var exe = Platform.IsWindows ? Path.Combine(game.GamePath, "StardewModdingAPI.exe") : UnixLauncher(game);
                        if (exe is null || !File.Exists(exe))
                            return LaunchResult.Fail(Loc.T("core.launch_no_smapi"));
                        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = game.GamePath, UseShellExecute = Platform.IsWindows })?.Dispose();
                        // Steam is open and this is a Steam game, but SMAPI is not set up as its launch option: the mods load,
                        // Steam's overlay, playtime and achievements do not, and the person is told how to change that
                        var steamOpen = IsSteamInstall(game) && SteamLaunch.IsSteamRunning();
                        return new LaunchResult(null, plan, steamOpen && !SteamLaunch.OptionsStartSmapi(SteamLaunch.ReadSavedLaunchOptions()));
                    }

                default:
                    return LaunchResult.Fail(Loc.T(mode == LaunchMode.WithMods ? "core.launch_no_smapi" : "core.launch_unavailable"));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Write($"Could not start the game ({plan}): {ex.Message}");
            return LaunchResult.Fail(Loc.T("core.launch_failed", ex.Message));
        }
    }

    /// <summary>The native SMAPI launcher that SMAPI's installer puts in the game folder on Linux and macOS.</summary>
    private static string? UnixLauncher(GameInstall game)
    {
        var launcher = Path.Combine(game.GamePath, "StardewModdingAPI");
        return Platform.IsExecutable(launcher) ? launcher : null;
    }
}
