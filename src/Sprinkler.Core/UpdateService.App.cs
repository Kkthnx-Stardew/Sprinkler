using System.Text.Json;

namespace Sprinkler.Core;

// Keeping the program itself up to date: finding a newer release, and (where it is safe) installing it.
internal sealed partial class UpdateService
{
    private AppRelease? _appRelease;
    private bool _appCanInstall;
    private bool _appBusy;
    private int _appProgress;
    private string? _restartPath;

    /// <summary>For tests: the program to replace, the public key to trust, and the check that the new program starts.</summary>
    internal string? SelfPathOverride { get; set; }

    internal byte[]? SelfKeyOverride { get; set; }

    internal Func<string, string, Task<bool>>? SelfCheckOverride { get; set; }

    internal Func<string, bool>? RelaunchOverride { get; set; }

    private UpdateNotice? AppUpdateNotice() =>
        _appRelease is null ? null : new UpdateNotice(_appRelease.Version, _appRelease.PageUrl, _appCanInstall, _appBusy, _appProgress);

    /// <summary>Looks for a newer release of this app on GitHub. Silent when switched off, offline, or when nothing newer exists.</summary>
    public async Task CheckAppUpdateAsync()
    {
        if (!Settings.Data.CheckAppUpdates)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{AppEnvironment.GitHubApiUrl}/repos/{AppEnvironment.RepoSlug}/releases?per_page=20");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await AppEnvironment.Http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return;
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (ModVersion.Parse(AppEnvironment.Version) is not { } current)
                return;
            var release = SelfUpdate.Pick(doc.RootElement, current);
            if (release is null)
                return;
            var canInstall = release.CanDownload && (SelfPathOverride ?? SelfUpdate.ReplaceablePath()) is not null;
            Update(() =>
            {
                _appRelease = release;
                _appCanInstall = canInstall;
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
        }
    }

    /// <summary>Turns the start-up look for a newer version of this app on or off.</summary>
    public void SetCheckAppUpdates(bool on)
    {
        lock (_gate)
        {
            Settings.Data.CheckAppUpdates = on;
            Settings.Save();
            if (!on)
                _appRelease = null;
        }
        Update(() => { });
        if (on)
            _ = CheckAppUpdateAsync();
    }

    /// <summary>
    /// Downloads and checks the newer version and puts it in place of this program, without restarting. Returns a sentence for the
    /// person if anything is wrong (nothing is changed then), or null when <see cref="RestartIntoUpdate"/> can be called.
    /// </summary>
    public async Task<string?> InstallAppUpdateAsync()
    {
        AppRelease? release;
        lock (_gate)
        {
            if (_appBusy)
                return null;
            release = _appRelease;
        }
        if (release is null || !release.CanDownload)
            return Loc.T("upd.no_files");
        var path = SelfPathOverride ?? SelfUpdate.ReplaceablePath();
        if (path is null)
            return Loc.T("upd.cannot_replace");

        Update(() =>
        {
            _appBusy = true;
            _appProgress = 0;
        });
        try
        {
            var staged = await SelfUpdate.FetchAsync(release, SelfKeyOverride ?? SelfUpdate.DefaultKey, path, p => Update(() => _appProgress = p), _cts.Token);
            var starts = await (SelfCheckOverride ?? SelfUpdate.ReportsVersionAsync)(staged, release.Version);
            if (!starts)
            {
                SelfUpdate.Discard(staged);
                return Loc.T("upd.wont_start");
            }
            SelfUpdate.Swap(staged, path);
            lock (_gate)
                _restartPath = path;
            Log.Write($"Updated the program from {AppEnvironment.Version} to {release.Version}");
            return null;
        }
        catch (AppUpdateException ex)
        {
            Log.Write("The app update failed: " + ex.Message);
            return ex.Message;
        }
        catch (OperationCanceledException)
        {
            return Loc.T("upd.network", "cancelled");
        }
        finally
        {
            Update(() => _appBusy = false);
        }
    }

    /// <summary>Starts the updated program. The caller then closes this one.</summary>
    public bool RestartIntoUpdate()
    {
        string? path;
        lock (_gate)
            path = _restartPath;
        return path is not null && (RelaunchOverride ?? SelfUpdate.Relaunch)(path);
    }
}
