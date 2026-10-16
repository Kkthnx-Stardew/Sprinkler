using System.Diagnostics;
using StardewModUpdater.Core;

namespace StardewModUpdater.Web;

/// <summary>
/// Opens the UI in its own chrome-less window (no tabs, no address bar) using a Chromium-based browser that is already
/// installed: Edge, Chrome or Brave on Windows, Chrome, Chromium, Brave or Edge on Linux and macOS. Each run gets a
/// throwaway private profile, so the window never touches the user's browsing and a window left over from an earlier
/// run cannot take over the new one. Without such a browser (Firefox only, for example) a normal browser tab is used.
/// </summary>
internal static class AppWindow
{
    private static string? _profile;

    private static string ProfilesRoot(string? browser)
    {
        // A snap-packaged browser cannot see hidden folders such as ~/.local, but it can use its own folder under ~/snap
        if (browser is not null && browser.Contains("/snap/", StringComparison.Ordinal))
        {
            var name = Path.GetFileName(browser);
            return Path.Combine(Platform.HomeDir, "snap", name, "common", "stardew-mod-updater");
        }
        return Path.Combine(AppEnvironment.DataDir, "window-profiles");
    }

    /// <summary>Browsers that support the --app window mode, in the order they are preferred.</summary>
    internal static IEnumerable<string> BrowserCandidates()
    {
        if (Platform.IsWindows)
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe");
            yield return Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe");
            yield return Path.Combine(pf, @"Google\Chrome\Application\chrome.exe");
            yield return Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe");
            yield return Path.Combine(local, @"Google\Chrome\Application\chrome.exe");
            yield return Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe");
        }
        else if (Platform.IsMac)
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
            yield return "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
        }
        else
        {
            foreach (var name in new[] { "google-chrome-stable", "google-chrome", "chromium", "chromium-browser", "brave-browser", "brave", "microsoft-edge-stable", "microsoft-edge", "vivaldi" })
            {
                if (Platform.Which(name) is { } found)
                    yield return found;
            }
            yield return "/snap/bin/chromium";
        }
    }

    public static string? FindBrowser() => BrowserCandidates().FirstOrDefault(File.Exists);

    /// <summary>The command line that opens the app window. Pure, so it can be tested.</summary>
    internal static string BuildArguments(string url, string profile)
    {
        var args = $"--app=\"{url}\" --user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --disable-sync --disable-extensions "
            + "--disable-background-mode --disable-features=Translate,msEdgeShopping,msEdgeSidebar,msImplicitSignin,msEdgeImplicitSignin --window-size=1120,820";
        if (Platform.IsLinux)
            args += " --class=StardewModUpdater";
        return args;
    }

    /// <summary>Opens the window. Returns the browser process to watch for the window closing, or null when a plain tab was used.</summary>
    public static Process? Open(string url, bool appWindow)
    {
        if (appWindow && FindBrowser() is { } exe)
        {
            try
            {
                _profile = Path.Combine(ProfilesRoot(exe), $"{Environment.ProcessId}-{DateTime.UtcNow.Ticks}");
                Directory.CreateDirectory(_profile);
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, Arguments = BuildArguments(url, _profile) };
                var process = Process.Start(psi);
                if (process is not null)
                    return process;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Log.Write("App window failed, using the default browser: " + ex.Message);
            }
        }

        try
        {
            WebHost.OpenInBrowser(url);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Write("Could not open a browser: " + ex.Message);
            Console.Error.WriteLine("Open this address in a browser: " + url);
        }
        return null;
    }

    /// <summary>Removes this run's profile, and any older ones that a crash left behind.</summary>
    public static void Cleanup()
    {
        foreach (var root in new[] { ProfilesRoot(null) }.Concat(SnapRoots()).Distinct())
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (var dir in Directory.EnumerateDirectories(root))
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

    private static IEnumerable<string> SnapRoots()
    {
        if (!Platform.IsLinux)
            yield break;
        var snapHome = Path.Combine(Platform.HomeDir, "snap");
        if (!Directory.Exists(snapHome))
            yield break;
        foreach (var app in Directory.EnumerateDirectories(snapHome))
            yield return Path.Combine(app, "common", "stardew-mod-updater");
    }
}
