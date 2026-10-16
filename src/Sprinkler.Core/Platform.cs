using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sprinkler.Core;

/// <summary>Everything that differs between Windows, Linux and macOS lives behind this class and its neighbours.</summary>
internal static class Platform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsMac => OperatingSystem.IsMacOS();

    /// <summary>The platform name SMAPI's web API expects: Windows, Linux or Mac.</summary>
    public static string SmapiName => IsWindows ? "Windows" : IsMac ? "Mac" : "Linux";

    public static string Description => RuntimeInformation.OSDescription;

    public static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Where settings, backups and logs live. Windows: LocalAppData. Linux: XDG data home. macOS: Application Support.</summary>
    public static string DefaultDataDir()
    {
        var root = IsMac
            ? Path.Combine(HomeDir, "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(root, "Sprinkler");
        MigrateLegacyDataDir(Path.Combine(root, "StardewModUpdater"), dir);
        return dir;
    }

    /// <summary>
    /// The app was called Stardew Mod Updater before it was renamed. Settings (including the encrypted Nexus key),
    /// history and backups are carried over once, by moving the folder, or by copying it when a running older copy
    /// holds files open. The old folder is left in place after a copy so nothing is lost.
    /// </summary>
    internal static void MigrateLegacyDataDir(string legacy, string target)
    {
        try
        {
            if (!Directory.Exists(legacy) || Directory.Exists(target))
                return;
            try
            {
                Directory.Move(legacy, target);
                return;
            }
            catch (IOException)
            {
            }
            CopyDataTree(legacy, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void CopyDataTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("instance.", StringComparison.OrdinalIgnoreCase) || name == "url.txt")
                continue; // run-time files of a running copy
            File.Copy(file, Path.Combine(to, name), overwrite: false);
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            var name = Path.GetFileName(dir);
            if (name is "window-profiles" or "staging")
                continue; // throwaway
            CopyDataTree(dir, Path.Combine(to, name));
        }
    }

    /// <summary>The user's Downloads folder, honoring XDG user directories on Linux.</summary>
    public static string DownloadsFolder()
    {
        if (OperatingSystem.IsWindows())
            return WindowsDownloads();

        if (IsLinux)
        {
            var fromXdg = ReadXdgDownloads(HomeDir, Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
            if (fromXdg is not null)
                return fromXdg;
        }
        return Path.Combine(HomeDir, "Downloads");
    }

    /// <summary>Reads XDG_DOWNLOAD_DIR from user-dirs.dirs. Public so it can be tested with a fake home.</summary>
    internal static string? ReadXdgDownloads(string home, string? configHome)
    {
        try
        {
            var file = Path.Combine(string.IsNullOrWhiteSpace(configHome) ? Path.Combine(home, ".config") : configHome, "user-dirs.dirs");
            if (!File.Exists(file))
                return null;
            foreach (var line in File.ReadLines(file))
            {
                var t = line.Trim();
                if (!t.StartsWith("XDG_DOWNLOAD_DIR", StringComparison.Ordinal))
                    continue;
                var eq = t.IndexOf('=');
                if (eq < 0)
                    continue;
                var value = t[(eq + 1)..].Trim().Trim('"');
                value = value.Replace("$HOME", home).Replace("${HOME}", home);
                if (value.Length > 0 && Path.IsPathRooted(value))
                    return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>Opens a web address in the user's default browser.</summary>
    public static void OpenUrl(string url)
    {
        if (IsWindows)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        else
            Run(IsMac ? "open" : "xdg-open", url);
    }

    /// <summary>Shows a folder in the system file manager.</summary>
    public static void OpenFolder(string path)
    {
        if (IsWindows)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        else
            Run(IsMac ? "open" : "xdg-open", path);
    }

    /// <summary>Starts a helper program without a shell and without waiting for it.</summary>
    public static void Run(string program, params string[] arguments)
    {
        var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start {program}: {ex.Message}", ex);
        }
    }

    /// <summary>Runs a helper program, waits briefly, and returns its output, or null when it failed or is not installed.</summary>
    public static string? Capture(string program, string[] arguments, string? stdin = null, int timeoutMs = 4000)
    {
        try
        {
            var psi = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
            };
            foreach (var a in arguments)
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null)
                return null;
            if (stdin is not null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            var output = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return null;
            }
            return p.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Finds a program on PATH, or null.</summary>
    public static string? Which(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
                if (IsWindows && File.Exists(candidate + ".exe"))
                    return candidate + ".exe";
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    /// <summary>True when a file has an executable bit for the current user (always true on Windows).</summary>
    public static bool IsExecutable(string file)
    {
        if (!File.Exists(file))
            return false;
        if (OperatingSystem.IsWindows())
            return true;
        try
        {
            return (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string WindowsDownloads()
    {
        try
        {
            var guid = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            var hr = SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out var path);
            if (hr == 0 && !string.IsNullOrEmpty(path))
                return path;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return Path.Combine(HomeDir, "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [SupportedOSPlatform("windows")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, [MarshalAs(UnmanagedType.LPWStr)] out string path);
}
