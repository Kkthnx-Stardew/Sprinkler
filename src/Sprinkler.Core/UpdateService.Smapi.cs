namespace Sprinkler.Core;

// Installing and updating SMAPI itself, with SMAPI's own installer. The mods are not part of this, so it does not touch them.
internal sealed partial class UpdateService
{
    private bool _smapiBusy;
    private int _smapiProgress;

    private SmapiSetupInfo SmapiSetup() => new(SmapiInstaller.IsSupported, _smapiBusy, _smapiProgress);

    /// <summary>Looks up the newest SMAPI on GitHub. Returns the release, or a sentence for the person when it cannot be had.</summary>
    public async Task<(SmapiRelease? Release, string? Error)> FindSmapiReleaseAsync()
    {
        try
        {
            return (await SmapiInstaller.FindLatestAsync(_appCts.Token), null);
        }
        catch (SmapiInstallException ex)
        {
            Log.Write("SMAPI lookup failed: " + ex.Message);
            return (null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return (null, Loc.T("smapi.lookup_failed", "cancelled"));
        }
    }

    /// <summary>
    /// Installs the release into the game folder and reads the folder again. Returns a sentence for the person when something is wrong,
    /// or null on success. Refuses to start while a check or an install of mods is running.
    /// </summary>
    public async Task<string?> InstallSmapiAsync(SmapiRelease release)
    {
        GameInstall? game;
        lock (_gate)
        {
            if (_smapiBusy)
                return null;
            game = _game;
        }
        if (game is null)
            return Loc.T("core.no_game");
        if (IsBusy)
            return Loc.T("smapi.busy_other");

        Update(() =>
        {
            _smapiBusy = true;
            _smapiProgress = 0;
        });
        try
        {
            var version = await SmapiInstaller.InstallAsync(game, release, p => Update(() => _smapiProgress = p), _appCts.Token);
            Log.Write($"SMAPI {version} is installed in {game.GamePath}");
        }
        catch (SmapiInstallException ex)
        {
            Log.Write("The SMAPI install failed: " + ex.Message);
            return ex.Message;
        }
        catch (OperationCanceledException)
        {
            return Loc.T("smapi.failed", "cancelled");
        }
        finally
        {
            Update(() => _smapiBusy = false);
        }

        // SMAPI's version and its own bundled mods changed, so read the folder again and ask smapi.io about everything
        if (GameLocator.Inspect(game.GamePath, game.Store) is { } refreshed)
            lock (_gate)
                _game = refreshed;
        await CheckAsync();
        return null;
    }
}
