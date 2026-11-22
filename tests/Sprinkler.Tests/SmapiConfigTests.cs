using System.Text.Json;
using Sprinkler.Core;
using Xunit;

namespace Sprinkler.Tests;

/// <summary>SMAPI's own list of mods it does not check for updates, read the way SMAPI reads it.</summary>
public class SmapiConfigTests
{
    [Fact]
    public void ReadsAllThreeFilesWithCommentsAndJoinsTheirLists()
    {
        using var d = new TempDir();
        d.Write("game/smapi-internal/config.json", """
            {
              /** The mod IDs SMAPI should ignore when performing update checks. */
              "SuppressUpdateChecks": [ "SMAPI.ConsoleCommands", "SMAPI.SaveBackup", ],
              "DeveloperMode": false
            }
            """);
        d.Write("game/smapi-internal/config.user.json", """{ "suppressUpdateChecks": [ "me.Pinned" ] }""");
        d.Write("game/Mods/SMAPI-config.json", """{ "SuppressUpdateChecks": [ "ME.GROUPED", "" ] }""");

        var ids = SmapiConfig.SuppressedUpdateChecks(d.Combine("game"), d.Combine("game", "Mods"));

        Assert.Equal(4, ids.Count);
        Assert.Contains("smapi.savebackup", ids);
        Assert.Contains("me.pinned", ids);
        Assert.Contains("me.Grouped", ids);
    }

    [Fact]
    public void MissingOrBrokenFilesAddNothing()
    {
        using var d = new TempDir();
        d.Write("game/smapi-internal/config.json", "{ this is not json");
        Assert.Empty(SmapiConfig.SuppressedUpdateChecks(d.Combine("game"), d.Combine("game", "Mods")));
    }
}

public class SmapiSkipServiceTests
{
    [Fact]
    public async Task AModSmapiSkipsIsNeitherCheckedNorOffered()
    {
        using var dir = new TempDir();
        using var web = new FakeWeb();
        Directory.CreateDirectory(dir.Combine("game", "Mods"));
        File.WriteAllText(dir.Combine("game", "Stardew Valley.dll"), "");
        dir.Write("game/Mods/Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0", "Thing", ", \"UpdateKeys\": [\"Nexus:5\"]"));
        dir.Write("game/Mods/Other/manifest.json", Zips.Manifest("me.Other", "1.0.0", "Other", ", \"UpdateKeys\": [\"Nexus:6\"]"));
        dir.Write("game/smapi-internal/config.user.json", """{ "SuppressUpdateChecks": [ "me.Thing" ] }""");

        string? body = null;
        web.Handler = (path, ctx) =>
        {
            if (path != "/smapi")
                return null;
            body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
            return FakeWeb.Json("""[{"id":"me.Thing","suggestedUpdate":{"version":"2.0.0","url":"x"},"metadata":{"nexusID":5,"main":{"version":"2.0.0"}},"errors":[]},{"id":"me.Other","metadata":{"nexusID":6,"main":{"version":"1.0.0"}},"errors":[]}]""");
        };

        using var service = new UpdateService(new SettingsStore(dir.Combine("settings.json")), new HistoryStore(dir.Combine("h.json"), dir.Combine("b")), _ => { });
        await service.SetGameFolderAsync(dir.Combine("game"));
        var end = DateTime.UtcNow.AddSeconds(20);
        while (service.Snapshot().Phase != Phase.Review && DateTime.UtcNow < end)
            await Task.Delay(50);

        var snap = service.Snapshot();
        Assert.Empty(snap.Items);
        var row = snap.Rows.Single(r => r.UniqueId == "me.Thing");
        Assert.Equal(RowState.Ignored, row.State);
        Assert.True(row.SkippedBySmapi);
        var ids = JsonDocument.Parse(body!).RootElement.GetProperty("mods").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
        Assert.DoesNotContain("me.Thing", ids);
        Assert.Contains("me.Other", ids);
    }
}

/// <summary>The quiet check at sign-in. Only the parts that touch no real system setting are run here.</summary>
public class AutostartTests
{
    [Fact]
    public void TheLinuxEntryStartsThisProgramQuietly()
    {
        using var d = new TempDir();
        var folder = Autostart.AutostartDir(d.Path, null);
        Assert.Equal(Path.Combine(d.Path, ".config", "autostart"), folder);
        Assert.Equal(Path.Combine(d.Combine("cfg"), "autostart"), Autostart.AutostartDir(d.Path, d.Combine("cfg")));

        Autostart.WriteDesktopFile(folder, "/home/me/My Apps/Sprinkler");
        var text = File.ReadAllText(Path.Combine(folder, Autostart.DesktopFileName));
        Assert.Contains("Exec=\"/home/me/My Apps/Sprinkler\" --remind", text);
        Assert.Contains("NoDisplay=true", text);
    }

    [Fact]
    public void TheWindowsCommandQuotesThePath() =>
        Assert.Equal("\"C:\\Program Files\\Sprinkler\\Sprinkler.exe\" --remind", Autostart.RunCommand(@"C:\Program Files\Sprinkler\Sprinkler.exe"));

    [Fact]
    public void TheFlagIsRead() => Assert.True(Options.Parse(new[] { Autostart.Argument }).Remind);
}
