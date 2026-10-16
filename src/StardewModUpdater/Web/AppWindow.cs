using System.Diagnostics;
using StardewModUpdater.Core;

namespace StardewModUpdater.Web;

/// <summary>
/// Opens the UI in its own chrome-less window (no tabs, no address bar) using a Chromium browser that is already
/// installed. Each run gets a throwaway private profile, so the window never touches the user's browsing and a window
/// left over from an earlier run cannot take over the new one. Falls back to a normal browser tab.
/// </summary>
internal static class AppWindow
{
    private static string? _profile;

    private static string ProfilesRoot => Path.Combine(AppEnvironment.DataDir, "window-profiles");

    public static string? FindBrowser()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Opens the window. Returns the browser process to watch for the window closing, or null when a plain tab was used.</summary>
    public static Process? Open(string url, bool appWindow)
    {
        if (appWindow && FindBrowser() is { } exe)
        {
            try
            {
                _profile = Path.Combine(ProfilesRoot, $"{Environment.ProcessId}-{DateTime.UtcNow.Ticks}");
                Directory.CreateDirectory(_profile);
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    Arguments = $"--app=\"{url}\" --user-data-dir=\"{_profile}\" --no-first-run --no-default-browser-check --disable-sync --disable-extensions "
                        + "--disable-background-mode --disable-features=Translate,msEdgeShopping,msEdgeSidebar,msImplicitSignin,msEdgeImplicitSignin --window-size=1120,820",
                };
                var process = Process.Start(psi);
                if (process is not null)
                    return process;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                Log.Write("App window failed, using the default browser: " + ex.Message);
            }
        }

        WebHost.OpenInBrowser(url);
        return null;
    }

    /// <summary>Removes this run's profile, and any older ones that a crash left behind.</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(ProfilesRoot))
                return;
            foreach (var dir in Directory.EnumerateDirectories(ProfilesRoot))
            {
                var mine = _profile is not null && string.Equals(Path.GetFullPath(dir), Path.GetFullPath(_profile), StringComparison.OrdinalIgnoreCase);
                var old = Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1);
                if (mine || old)
                {
                    try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
