using Microsoft.Win32;

namespace StardewModUpdater.Core;

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

/// <summary>Makes this app the handler for nxm:// links for the current Windows user only, and can hand the link back.</summary>
internal static class NxmRegistration
{
    private const string KeyPath = @"Software\Classes\nxm";

    public static bool IsRegisteredToThisApp()
    {
        try
        {
            using var cmd = Registry.CurrentUser.OpenSubKey(KeyPath + @"\shell\open\command");
            var value = cmd?.GetValue("") as string;
            return value is not null && value.Contains(Environment.ProcessPath ?? "?", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The current handler's command line, if some other program (for example Vortex) owns the link.</summary>
    public static string? CurrentHandler()
    {
        try
        {
            using var cmd = Registry.CurrentUser.OpenSubKey(KeyPath + @"\shell\open\command")
                ?? Registry.ClassesRoot.OpenSubKey(@"nxm\shell\open\command");
            return cmd?.GetValue("") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Registers this app. Returns the command that was replaced so it can be restored later.</summary>
    public static string? Register()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The app path is unknown.");
        var previous = IsRegisteredToThisApp() ? null : CurrentHandler();

        using var root = Registry.CurrentUser.CreateSubKey(KeyPath);
        root.SetValue("", "URL:NXM Protocol");
        root.SetValue("URL Protocol", "");
        using var cmd = root.CreateSubKey(@"shell\open\command");
        cmd.SetValue("", $"\"{exe}\" \"%1\"");
        return previous;
    }

    /// <summary>Removes this app as the handler, putting back the previous command when there was one.</summary>
    public static void Unregister(string? previousCommand)
    {
        if (!IsRegisteredToThisApp())
            return;

        if (string.IsNullOrWhiteSpace(previousCommand))
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
            return;
        }

        using var cmd = Registry.CurrentUser.CreateSubKey(KeyPath + @"\shell\open\command");
        cmd.SetValue("", previousCommand);
    }
}
