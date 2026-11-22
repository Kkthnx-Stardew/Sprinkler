using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Sprinkler.Core;

/// <summary>
/// Starts the app quietly when the person signs in, so it can check the mods and only open when updates are waiting.
/// Windows uses the per-user Run key, Linux a freedesktop autostart file in the user's config folder. Both belong to the
/// user alone, need no extra rights, and point at the program where it is now, so they are written again at every start.
/// </summary>
internal static class Autostart
{
    /// <summary>The command line flag the sign-in start uses.</summary>
    public const string Argument = "--remind";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Sprinkler";
    internal const string DesktopFileName = "sprinkler-remind.desktop";

    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>The folder for autostart files, from XDG_CONFIG_HOME or ~/.config.</summary>
    internal static string AutostartDir(string home, string? configHome) =>
        Path.Combine(configHome is { Length: > 0 } c && Path.IsPathRooted(c) ? c : Path.Combine(home, ".config"), "autostart");

    internal static string DesktopEntryText(string exe)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Desktop Entry]");
        sb.AppendLine("Type=Application");
        sb.AppendLine("Version=1.0");
        sb.AppendLine("Name=Sprinkler");
        sb.AppendLine("Comment=Check Stardew Valley mods for updates after signing in");
        sb.AppendLine($"Exec={LinuxShortcut.QuoteExec(exe)} {Argument}");
        sb.AppendLine($"Icon={LinuxShortcut.IconName}");
        sb.AppendLine("Terminal=false");
        sb.AppendLine("NoDisplay=true");
        return sb.ToString();
    }

    /// <summary>The Run key value: the quoted program path and the flag, as Windows expects it.</summary>
    internal static string RunCommand(string exe) => $"\"{exe}\" {Argument}";

    /// <summary>Writes the sign-in start for the running program. Returns null when it worked, or what went wrong.</summary>
    public static string? Enable()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
            return Loc.T("core.app_path_unknown");
        try
        {
            if (OperatingSystem.IsWindows())
                WriteRunKey(exe);
            else if (OperatingSystem.IsLinux())
                WriteDesktopFile(AutostartDir(Platform.HomeDir, Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")), exe);
            else
                return Loc.T("core.remind_unsupported");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write("Could not set up the sign-in check: " + ex.Message);
            return ex.Message;
        }
    }

    /// <summary>Removes the sign-in start. Missing entries are fine.</summary>
    public static void Disable()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                DeleteRunKey();
            else if (OperatingSystem.IsLinux())
                Cleanup.TryDeleteFile(Path.Combine(AutostartDir(Platform.HomeDir, Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")), DesktopFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write("Could not remove the sign-in check: " + ex.Message);
        }
    }

    internal static void WriteDesktopFile(string autostartDir, string exe)
    {
        Directory.CreateDirectory(autostartDir);
        File.WriteAllText(Path.Combine(autostartDir, DesktopFileName), DesktopEntryText(exe), new UTF8Encoding(false));
    }

    [SupportedOSPlatform("windows")]
    private static void WriteRunKey(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(RunValueName, RunCommand(exe), RegistryValueKind.String);
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}
