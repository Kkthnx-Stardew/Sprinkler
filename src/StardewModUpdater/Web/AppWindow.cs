using System.Diagnostics;
using StardewModUpdater.Core;

namespace StardewModUpdater.Web;

/// <summary>
/// Opens the UI in its own chrome-less window (no tabs, no address bar) using a Chromium browser that is already
/// installed, with a private profile so it never touches the user's browsing. Falls back to a normal browser tab.
/// </summary>
internal static class AppWindow
{
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
                var profile = Path.Combine(AppEnvironment.DataDir, "window-profile");
                Directory.CreateDirectory(profile);
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    Arguments = $"--app=\"{url}\" --user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --disable-sync --disable-extensions "
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
}
