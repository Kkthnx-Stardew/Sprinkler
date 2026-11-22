using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sprinkler.Core;
using Sprinkler.ViewModels;
using Xunit;

namespace Sprinkler.Tests;

/// <summary>Repeated checks and a closed window must not keep memory alive.</summary>
public class LeakTests(ITestOutputHelper output)
{
    [AvaloniaFact]
    public async Task RepeatedChecksDoNotGrowTheHeap()
    {
        using var dir = new TempDir();
        using var web = new FakeWeb();
        Directory.CreateDirectory(dir.Combine("game", "Mods"));
        File.WriteAllText(dir.Combine("game", "Stardew Valley.dll"), "");
        var smapi = new StringBuilder("[");
        for (var i = 0; i < 100; i++)
        {
            dir.Write($"game/Mods/M{i}/manifest.json", Zips.Manifest($"me.M{i}", "1.0.0", $"Mod {i}", $", \"UpdateKeys\": [\"Nexus:{1000 + i}\"]"));
            smapi.Append($"{(i > 0 ? "," : "")}{{\"id\":\"me.M{i}\",\"metadata\":{{\"name\":\"Mod {i}\",\"nexusID\":{1000 + i},\"main\":{{\"version\":\"1.0.0\"}}}},\"errors\":[]}}");
        }
        smapi.Append(']');
        web.Handler = (path, _) => path == "/smapi" ? FakeWeb.Json(smapi.ToString()) : null;
        var settings = new SettingsStore(dir.Combine("settings.json"));
        settings.Data.Welcomed = true;
        using var service = new UpdateService(settings, new HistoryStore(dir.Combine("h.json"), dir.Combine("b")), _ => { });
        await service.SetGameFolderAsync(dir.Combine("game"));
        var vm = new MainViewModel(service) { Ui = new FakeUi(), SuppressTour = true };

        async Task Settle()
        {
            var end = DateTime.UtcNow.AddSeconds(20);
            do { Dispatcher.UIThread.RunJobs(); vm.Refresh(); await Task.Delay(30); }
            while (!(vm.IsReview && vm.Mods.Count == 100) && DateTime.UtcNow < end);
        }

        long Heap() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }

        await Settle();
        for (var i = 0; i < 3; i++) { await service.CheckAsync(); await Settle(); }
        var start = Heap();
        for (var i = 0; i < 20; i++) { await service.CheckAsync(); await Settle(); }
        var grown = Heap() - start;
        output.WriteLine($"after 20 more checks: {grown / 1024.0:F0} KB more than at the start");
        vm.Dispose();
        Assert.True(grown < 2 * 1024 * 1024, $"The heap grew by {grown / 1024} KB over 20 checks.");
    }

    [AvaloniaFact]
    public async Task AClosedWindowStateIsFreed()
    {
        using var dir = new TempDir();
        using var web = new FakeWeb();
        Directory.CreateDirectory(dir.Combine("game", "Mods"));
        File.WriteAllText(dir.Combine("game", "Stardew Valley.dll"), "");
        web.Handler = (path, _) => path == "/smapi" ? FakeWeb.Json("[]") : null;
        using var service = new UpdateService(new SettingsStore(dir.Combine("settings.json")), new HistoryStore(dir.Combine("h.json"), dir.Combine("b")), _ => { });
        await service.SetGameFolderAsync(dir.Combine("game"));

        WeakReference Make()
        {
            var vm = new MainViewModel(service) { Ui = new FakeUi(), SuppressTour = true };
            vm.Refresh();
            vm.Dispose();
            return new WeakReference(vm);
        }
        var weak = Make();
        for (var i = 0; i < 5 && weak.IsAlive; i++) { Dispatcher.UIThread.RunJobs(); GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(50); }
        Assert.False(weak.IsAlive, "The window's view model was still held after it was disposed.");
    }
}
