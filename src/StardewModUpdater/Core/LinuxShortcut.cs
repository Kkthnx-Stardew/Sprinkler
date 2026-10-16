using System.Reflection;
using System.Text;

namespace StardewModUpdater.Core;

/// <summary>
/// Puts the app in the Linux application menu (and so on the Steam Deck's Desktop Mode menu) by writing a freedesktop
/// .desktop file and an icon into the user's own XDG data folder. Nothing outside the home folder is touched, and the
/// shortcut points at the program where it is now, so it must be refreshed if the program is moved.
/// </summary>
internal static class LinuxShortcut
{
    internal const string DesktopFileName = "stardew-mod-updater.desktop";
    internal const string IconName = "stardew-mod-updater";

    public static string DataHome() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d && Path.IsPathRooted(d)
            ? d
            : Path.Combine(Platform.HomeDir, ".local", "share");

    /// <summary>Quotes a program path for the Exec key. Quotes, backslashes, backticks and dollar signs are escaped, and a percent sign is doubled.</summary>
    internal static string QuoteExec(string exe) =>
        "\"" + exe.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";

    internal static string DesktopEntryText(string exe)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Desktop Entry]");
        sb.AppendLine("Type=Application");
        sb.AppendLine("Version=1.0");
        sb.AppendLine("Name=Stardew Mod Updater");
        sb.AppendLine("GenericName=Mod updater");
        sb.AppendLine("Comment=Keep your Stardew Valley mods up to date");
        sb.AppendLine($"Exec={QuoteExec(exe)}");
        sb.AppendLine($"Icon={IconName}");
        sb.AppendLine("Terminal=false");
        sb.AppendLine("Categories=Game;Utility;");
        sb.AppendLine("Keywords=Stardew;Valley;SMAPI;Mods;Nexus;Updater;");
        // matches the window class the app window is started with, so the taskbar groups it under this icon
        sb.AppendLine("StartupWMClass=StardewModUpdater");
        return sb.ToString();
    }

    private static string DesktopFilePath(string dataHome) => Path.Combine(dataHome, "applications", DesktopFileName);

    private static string IconPath(string dataHome) => Path.Combine(dataHome, "icons", "hicolor", "256x256", "apps", IconName + ".png");

    public static bool IsInstalled() => File.Exists(DesktopFilePath(DataHome()));

    public static byte[] EmbeddedIcon()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon-256.png")
            ?? throw new InvalidOperationException("The icon is missing from the program.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Writes the shortcut for the running program. Returns a plain sentence about what was done.</summary>
    public static string Install()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The program path is unknown.");
        var dataHome = DataHome();
        Install(dataHome, exe, EmbeddedIcon());
        // best effort: make menus and icon themes notice the change without logging out
        Platform.Capture("update-desktop-database", new[] { Path.Combine(dataHome, "applications") });
        Platform.Capture("gtk-update-icon-cache", new[] { "-f", "-t", Path.Combine(dataHome, "icons", "hicolor") });
        return $"Added Stardew Mod Updater to your application menu. It starts {exe}, so move the program first if you want it elsewhere, then add it again.";
    }

    internal static void Install(string dataHome, string exe, byte[] icon)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DesktopFilePath(dataHome))!);
        Directory.CreateDirectory(Path.GetDirectoryName(IconPath(dataHome))!);
        File.WriteAllBytes(IconPath(dataHome), icon);
        File.WriteAllText(DesktopFilePath(dataHome), DesktopEntryText(exe), new UTF8Encoding(false));
        // the launcher must be readable; some desktops also want the executable bit on a .desktop file
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(DesktopFilePath(dataHome),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    public static void Remove()
    {
        var dataHome = DataHome();
        Remove(dataHome);
        Platform.Capture("update-desktop-database", new[] { Path.Combine(dataHome, "applications") });
    }

    internal static void Remove(string dataHome)
    {
        foreach (var file in new[] { DesktopFilePath(dataHome), IconPath(dataHome) })
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Could not remove {file}: {ex.Message}");
            }
        }
    }
}
