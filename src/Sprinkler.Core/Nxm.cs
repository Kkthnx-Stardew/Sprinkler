using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Sprinkler.Core;

/// <summary>A parsed nxm:// link, as sent by the Mod Manager Download button on Nexus.</summary>
internal sealed record NxmLink(string Game, string ModId, string FileId, string? Key, string? Expires)
{
    public static NxmLink? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (!uri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("files", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!parts[1].All(char.IsDigit) || !parts[3].All(char.IsDigit))
            return null;

        string? key = null, expires = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var name = pair[..eq];
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (name.Equals("key", StringComparison.OrdinalIgnoreCase)) key = value;
            else if (name.Equals("expires", StringComparison.OrdinalIgnoreCase)) expires = value;
        }
        return new NxmLink(uri.Host.ToLowerInvariant(), parts[1], parts[3], key, expires);
    }
}

/// <summary>
/// Makes this app the handler for nxm:// links for the current user only, and can hand the link back.
/// Windows uses the registry. Linux uses a .desktop file and xdg-mime. macOS cannot do this without an app bundle.
/// </summary>
internal static class NxmRegistration
{
    private const string RegistryKeyPath = @"Software\Classes\nxm";
    internal const string DesktopFileName = "sprinkler-nxm.desktop";

    private static (bool Ours, bool Other, DateTime At) _status;
    private static readonly object StatusGate = new();

    /// <summary>False where the system gives the app no way to take over nxm links (macOS).</summary>
    public static bool Supported => Platform.IsWindows || Platform.IsLinux;

    /// <summary>State for the UI, cached for a few seconds because the UI asks on every refresh.</summary>
    public static (bool Ours, bool Other) Status()
    {
        lock (StatusGate)
        {
            if (DateTime.UtcNow - _status.At > TimeSpan.FromSeconds(10))
            {
                var ours = IsRegisteredToThisApp();
                _status = (ours, !ours && CurrentHandler() is not null, DateTime.UtcNow);
            }
            return (_status.Ours, _status.Other);
        }
    }

    private static void ForgetStatus()
    {
        lock (StatusGate)
            _status = default;
    }

    public static bool IsRegisteredToThisApp()
    {
        if (OperatingSystem.IsWindows())
            return WindowsIsOurs();
        if (Platform.IsLinux)
            return string.Equals(LinuxDefaultHandler(), DesktopFileName, StringComparison.Ordinal);
        return false;
    }

    /// <summary>The program that handles nxm links now, if it is not this app (for example Vortex).</summary>
    public static string? CurrentHandler()
    {
        if (OperatingSystem.IsWindows())
            return WindowsHandler();
        if (Platform.IsLinux)
        {
            var current = LinuxDefaultHandler();
            return string.IsNullOrEmpty(current) || current == DesktopFileName ? null : current;
        }
        return null;
    }

    /// <summary>Registers this app. Returns the handler that was replaced so it can be restored later.</summary>
    public static string? Register()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException(Loc.T("core.app_path_unknown"));
        string? previous;
        if (OperatingSystem.IsWindows())
            previous = WindowsRegister(exe);
        else if (Platform.IsLinux)
            previous = LinuxRegister(exe);
        else
            throw new InvalidOperationException(Loc.T("core.nxm_unsupported"));
        ForgetStatus();
        return previous;
    }

    /// <summary>Removes this app as the handler, putting back the previous one when there was one.</summary>
    public static void Unregister(string? previousHandler)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                WindowsUnregister(previousHandler);
            else if (Platform.IsLinux)
                LinuxUnregister(previousHandler);
        }
        finally
        {
            ForgetStatus();
        }
    }

    // ------------------------------------------------------------ Windows

    [SupportedOSPlatform("windows")]
    private static bool WindowsIsOurs()
    {
        try
        {
            using var cmd = Registry.CurrentUser.OpenSubKey(RegistryKeyPath + @"\shell\open\command");
            var value = cmd?.GetValue("") as string;
            return value is not null && value.Contains(Environment.ProcessPath ?? "?", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsHandler()
    {
        try
        {
            using var cmd = Registry.CurrentUser.OpenSubKey(RegistryKeyPath + @"\shell\open\command")
                ?? Registry.ClassesRoot.OpenSubKey(@"nxm\shell\open\command");
            return cmd?.GetValue("") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsRegister(string exe)
    {
        var previous = WindowsIsOurs() ? null : WindowsHandler();
        using var root = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
        root.SetValue("", "URL:NXM Protocol");
        root.SetValue("URL Protocol", "");
        using var cmd = root.CreateSubKey(@"shell\open\command");
        cmd.SetValue("", $"\"{exe}\" \"%1\"");
        return previous;
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsUnregister(string? previousCommand)
    {
        if (!WindowsIsOurs())
            return;

        if (string.IsNullOrWhiteSpace(previousCommand))
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistryKeyPath, throwOnMissingSubKey: false);
            return;
        }

        using var cmd = Registry.CurrentUser.CreateSubKey(RegistryKeyPath + @"\shell\open\command");
        cmd.SetValue("", previousCommand);
    }

    // ------------------------------------------------------------ Linux (freedesktop.org: .desktop files and xdg-mime)

    private static string DesktopFilePath =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(Platform.HomeDir, ".local", "share"), "applications", DesktopFileName);

    /// <summary>The text of the .desktop file that makes nxm links start this app.</summary>
    internal static string DesktopFileText(string exe)
    {
        // Exec values are quoted, and a literal percent sign is written twice
        var quoted = LinuxShortcut.QuoteExec(exe);
        var sb = new StringBuilder();
        sb.AppendLine("[Desktop Entry]");
        sb.AppendLine("Type=Application");
        sb.AppendLine("Name=Sprinkler (Nexus links)");
        sb.AppendLine("Comment=Installs mods from the Mod Manager Download button on Nexus Mods");
        sb.AppendLine($"Exec={quoted} %u");
        sb.AppendLine("Terminal=false");
        sb.AppendLine("NoDisplay=true");
        sb.AppendLine("MimeType=x-scheme-handler/nxm;");
        return sb.ToString();
    }

    private static string? LinuxDefaultHandler() =>
        Platform.Capture("xdg-mime", new[] { "query", "default", "x-scheme-handler/nxm" })?.Trim() is { Length: > 0 } s ? s : null;

    private static string? LinuxRegister(string exe)
    {
        if (Platform.Which("xdg-mime") is null)
            throw new InvalidOperationException(Loc.T("core.nxm_xdg"));

        var previous = LinuxDefaultHandler();
        if (previous == DesktopFileName)
            previous = null;

        var file = DesktopFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, DesktopFileText(exe));
        Platform.Capture("update-desktop-database", new[] { Path.GetDirectoryName(file)! });
        if (Platform.Capture("xdg-mime", new[] { "default", DesktopFileName, "x-scheme-handler/nxm" }) is null)
            throw new InvalidOperationException("xdg-mime could not register the handler.");
        return previous;
    }

    private static void LinuxUnregister(string? previous)
    {
        if (LinuxDefaultHandler() == DesktopFileName && !string.IsNullOrWhiteSpace(previous))
            Platform.Capture("xdg-mime", new[] { "default", previous, "x-scheme-handler/nxm" });
        try
        {
            if (File.Exists(DesktopFilePath))
                File.Delete(DesktopFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        Platform.Capture("update-desktop-database", new[] { Path.GetDirectoryName(DesktopFilePath)! });
    }
}
