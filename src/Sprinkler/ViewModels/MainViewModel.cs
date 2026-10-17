using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprinkler.Core;

namespace Sprinkler.ViewModels;

internal enum AppTab
{
    Updates,
    Mods,
    History,
    Settings,
    Help,
}

/// <summary>What the window needs from the operating system and the window itself.</summary>
internal interface IUiServices
{
    Task CopyTextAsync(string text);

    Task<string?> PickFolderAsync(string title);
}

/// <summary>
/// The whole app state as the window sees it. The service holds the truth; this listens for its Changed event, takes a
/// snapshot a few times a second at most, and updates stable child objects in place, so focus, scroll position and open
/// sections survive every refresh.
/// </summary>
internal sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly UpdateService _svc;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _poll;
    private readonly Dictionary<string, ItemViewModel> _itemMap = new();
    private readonly Dictionary<string, ModRowViewModel> _rowMap = new();
    private readonly Dictionary<string, HistoryViewModel> _historyMap = new();
    private readonly Dictionary<string, FoundViewModel> _foundMap = new();
    private Phase? _previousPhase;
    private bool _tourChecked;
    private bool _themeApplied;
    private string _problemSignature = "";
    private string _logSignature = "";
    private string _candidateSignature = "";
    private bool _disposed;

    public MainViewModel(UpdateService service)
    {
        _svc = service;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Refresh();
        };
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _poll.Tick += (_, _) => PollForOutsideChanges();

        ModChips = new ObservableCollection<FilterChipViewModel>
        {
            new(this, "all", "All"), new(this, "update", "Updates"), new(this, "ok", "Up to date"),
            new(this, "needs", "Missing requirements"), new(this, "nocheck", "Cannot check"), new(this, "off", "Off or skipped"),
        };
        ModChips[0].IsOn = true;

        _svc.Changed += OnServiceChanged;
        _svc.Art.Changed += () =>
        {
            _artDirty = true;
            OnServiceChanged();
        };
        _poll.Start();
    }

    public IUiServices? Ui { get; set; }

    internal Core.ModArtStore Art => _svc.Art;

    internal string? ArtKeyOf(string uniqueId) => _svc.ArtKeyOf(uniqueId);
    private bool _artDirty;
    private long _lastVersion = -1;
    private bool _lastRunning;

    /// <summary>For screenshots and tests: show this theme and skip the first-run tour without saving anything.</summary>
    public string? ThemeOverride { get; init; }

    public bool SuppressTour { get; init; }

    /// <summary>Starts the first scan. Called once the window exists.</summary>
    public void Start()
    {
        Refresh();
        _ = Run(_svc.InitializeAsync);
        _ = Run(_svc.CheckAppUpdateAsync);
    }

    /// <summary>The service tells us about its own changes. Things it cannot know, like the game being closed, are noticed here, and only then is anything rebuilt.</summary>
    private void PollForOutsideChanges()
    {
        var running = GameLocator.IsGameRunning();
        if (_svc.Version != _lastVersion || running != _lastRunning)
            Refresh();
    }

    private void OnServiceChanged()
    {
        if (_disposed)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_debounce.IsEnabled)
                _debounce.Start();
        });
    }

    // ------------------------------------------------------------------ state

    [ObservableProperty]
    private StateSnapshot? _snap;

    [ObservableProperty]
    private AppTab _tab = AppTab.Updates;

    public ObservableCollection<ItemViewModel> Items { get; } = new();
    public ObservableCollection<ItemViewModel> VisibleItems { get; } = new();
    public ObservableCollection<ModRowViewModel> Mods { get; } = new();
    public ObservableCollection<FilterChipViewModel> ModChips { get; }
    public ObservableCollection<HistoryViewModel> History { get; } = new();
    public ObservableCollection<FoundViewModel> Found { get; } = new();
    public ObservableCollection<ProblemViewModel> Problems { get; } = new();
    public ObservableCollection<SkippedModViewModel> SkippedMods { get; } = new();
    public ObservableCollection<LogErrorViewModel> LogErrors { get; } = new();
    public ObservableCollection<CandidateViewModel> Candidates { get; } = new();
    public ObservableCollection<StepViewModel> Steps { get; } = new();
    public ObservableCollection<ToastViewModel> Toasts { get; } = new();

    [ObservableProperty]
    private DialogViewModel? _dialog;

    public bool HasDialog => Dialog is not null;

    partial void OnDialogChanged(DialogViewModel? value) => OnPropertyChanged(nameof(HasDialog));

    public int TabIndex
    {
        get => (int)Tab;
        set
        {
            if (value >= 0 && value <= (int)AppTab.Help)
                Tab = (AppTab)value;
        }
    }

    partial void OnTabChanged(AppTab value)
    {
        OnPropertyChanged(nameof(TabIndex));
        OnPropertyChanged(nameof(ShowUpdatesTab));
        OnPropertyChanged(nameof(ShowModsTab));
        OnPropertyChanged(nameof(ShowHistoryTab));
        OnPropertyChanged(nameof(ShowSettingsTab));
        OnPropertyChanged(nameof(ShowHelpTab));
    }

    public bool ShowUpdatesTab => Tab == AppTab.Updates;
    public bool ShowModsTab => Tab == AppTab.Mods;
    public bool ShowHistoryTab => Tab == AppTab.History;
    public bool ShowSettingsTab => Tab == AppTab.Settings;
    public bool ShowHelpTab => Tab == AppTab.Help;

    public int UpdateCount => Snap?.Items.Count(i => i.Status == ItemStatus.Ready) ?? 0;
    public bool HasUpdateCount => UpdateCount > 0;

    // ------------------------------------------------------------------ refresh

    public void Refresh()
    {
        if (_disposed)
            return;
        var s = _svc.Snapshot();
        Snap = s;
        _lastVersion = s.Version;
        _lastRunning = s.Game?.Running == true;

        if (!_themeApplied)
        {
            _themeApplied = true;
            App.ApplyTheme(ThemeOverride ?? s.Settings.Theme);
        }

        SyncItems(s);
        SyncMods(s);
        if (_artDirty)
        {
            _artDirty = false;
            foreach (var row in _rowMap.Values)
                row.NotifyArt();
            foreach (var item in _itemMap.Values)
                item.NotifyArt();
        }
        SyncHistory(s);
        SyncFound(s);
        SyncProblems(s);
        SyncLog(s);
        SyncCandidates(s);
        SyncSteps(s);
        SyncSettingsFields(s);

        if (_previousPhase is { } before && before != s.Phase && s.Phase == Phase.Finished)
            Toast("Finished: " + (s.Summary ?? ""), s.Summary?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true);
        _previousPhase = s.Phase;

        MaybeShowTour(s);
        OnPropertyChanged(string.Empty);
    }

    private void SyncItems(StateSnapshot s)
    {
        var order = new List<ItemViewModel>();
        foreach (var info in s.Items)
        {
            if (_itemMap.TryGetValue(info.Id, out var vm))
                vm.Update(info);
            else
                _itemMap[info.Id] = vm = new ItemViewModel(this, info);
            order.Add(vm);
        }
        foreach (var gone in _itemMap.Keys.Where(k => s.Items.All(i => i.Id != k)).ToList())
            _itemMap.Remove(gone);

        SyncList(Items, order);

        var applying = s.Phase is Phase.Applying or Phase.Finished;
        var shown = s.Phase is Phase.Review or Phase.Applying or Phase.Finished
            ? order.Where(i => !applying || !i.IsReady).ToList()
            : new List<ItemViewModel>();
        SyncList(VisibleItems, shown);
    }

    private void SyncMods(StateSnapshot s)
    {
        foreach (var row in s.Rows)
        {
            var key = RowKey(row);
            if (_rowMap.TryGetValue(key, out var vm))
            {
                if (RowSig(vm.Row) != RowSig(row))
                    vm.Update(row);
            }
            else
            {
                _rowMap[key] = new ModRowViewModel(this, row);
            }
        }
        var live = s.Rows.Select(RowKey).ToHashSet();
        foreach (var gone in _rowMap.Keys.Where(k => !live.Contains(k)).ToList())
            _rowMap.Remove(gone);

        RebuildMods();
    }

    private static string RowKey(ModRow r) => r.Folder + "|" + r.UniqueId;

    private static string RowSig(ModRow r) =>
        string.Join('|', r.State, r.Version, r.Note, r.Author, r.Unofficial, r.SkippedVersion, r.Url, r.Needs is null ? "" : string.Join(',', r.Needs));

    private void SyncHistory(StateSnapshot s)
    {
        var order = new List<HistoryViewModel>();
        foreach (var h in s.History)
        {
            if (_historyMap.TryGetValue(h.Id, out var vm))
                vm.Update(h);
            else
                _historyMap[h.Id] = vm = new HistoryViewModel(this, h);
            order.Add(vm);
        }
        foreach (var gone in _historyMap.Keys.Where(k => s.History.All(h => h.Id != k)).ToList())
            _historyMap.Remove(gone);
        SyncList(History, order);
    }

    private void SyncFound(StateSnapshot s)
    {
        var order = new List<FoundViewModel>();
        foreach (var f in s.Found)
        {
            if (_foundMap.TryGetValue(f.Id, out var vm))
                vm.Update(f);
            else
                _foundMap[f.Id] = vm = new FoundViewModel(this, f);
            order.Add(vm);
        }
        foreach (var gone in _foundMap.Keys.Where(k => s.Found.All(f => f.Id != k)).ToList())
            _foundMap.Remove(gone);
        SyncList(Found, order);
    }

    private void SyncProblems(StateSnapshot s)
    {
        var sig = string.Join('\n', s.Problems.Select(p => p.Level + p.Title + p.Detail + p.Url));
        if (sig == _problemSignature)
            return;
        _problemSignature = sig;
        Problems.Clear();
        foreach (var p in s.Problems)
            Problems.Add(new ProblemViewModel(this, p));
    }

    private void SyncLog(StateSnapshot s)
    {
        var g = s.GameLog;
        var sig = g is null ? "" : g.WrittenAt.Ticks + "|" + g.Skipped.Count + "|" + g.Errors.Count;
        if (sig == _logSignature)
            return;
        _logSignature = sig;
        SkippedMods.Clear();
        LogErrors.Clear();
        if (g is null)
            return;
        foreach (var m in g.Skipped)
            SkippedMods.Add(new SkippedModViewModel(m));
        foreach (var e in g.Errors)
            LogErrors.Add(new LogErrorViewModel(e));
    }

    private void SyncCandidates(StateSnapshot s)
    {
        var sig = string.Join('\n', s.Candidates.Select(c => c.Path + c.Smapi));
        if (sig == _candidateSignature)
            return;
        _candidateSignature = sig;
        Candidates.Clear();
        foreach (var c in s.Candidates)
            Candidates.Add(new CandidateViewModel(this, c));
    }

    private void SyncSteps(StateSnapshot s)
    {
        var names = new[] { "Find your game", "Check mods", "Choose", "Update" };
        var active = s.Phase switch
        {
            Phase.Starting or Phase.ChooseGame => 0,
            Phase.Scanning or Phase.Checking => 1,
            Phase.Review => 2,
            Phase.Applying => 3,
            _ => -1,
        };
        var wanted = active < 0 ? new List<StepViewModel>() : names.Select((n, i) => new StepViewModel($"{(i < active ? "" : (i + 1) + ". ")}{n}", i == active, i < active)).ToList();
        if (Steps.Select(x => x.Text + x.IsOn).SequenceEqual(wanted.Select(x => x.Text + x.IsOn)))
            return;
        Steps.Clear();
        foreach (var w in wanted)
            Steps.Add(w);
    }

    private static void SyncList<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        if (target.SequenceEqual(wanted))
            return;
        for (var i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i]))
                target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i]))
                continue;
            var at = target.IndexOf(wanted[i]);
            if (at >= 0)
                target.Move(at, i);
            else
                target.Insert(i, wanted[i]);
        }
    }

    // ------------------------------------------------------------------ messages and dialogs

    public void Toast(string text, bool bad = false)
    {
        var t = new ToastViewModel(text, bad);
        Toasts.Add(t);
        _ = Task.Delay(5200).ContinueWith(_ => Dispatcher.UIThread.Post(() => Toasts.Remove(t)), TaskScheduler.Default);
    }

    public void ShowDialog(DialogViewModel dialog)
    {
        if (Dialog is ConfirmDialogViewModel previous)
            previous.No();
        dialog.Closed = d =>
        {
            if (ReferenceEquals(Dialog, d))
                Dialog = null;
        };
        Dialog = dialog;
    }

    public Task<bool> AskAsync(string title, string text, string yes, string icon = "help")
    {
        var d = new ConfirmDialogViewModel(title, icon, yes, "Cancel", new[] { new DialogLine("", text) });
        ShowDialog(d);
        return d.Result;
    }

    public void ShowTour(int start = 0)
    {
        ShowDialog(new TourDialogViewModel(Content.Tour, start, () => _svc.SetPrefs(null, true)));
    }

    private void MaybeShowTour(StateSnapshot s)
    {
        if (_tourChecked || SuppressTour || s.Phase != Phase.Review || !s.Welcomed)
            return;
        _tourChecked = true;
        if (!s.Settings.TourSeen)
            ShowTour();
    }

    /// <summary>Runs service work in the background and turns an unexpected failure into a message instead of a crash.</summary>
    private async Task Run(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Write("Unexpected failure: " + ex);
            Dispatcher.UIThread.Post(() => Toast("Something went wrong: " + ex.Message, true));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Stop();
        _poll.Stop();
        _svc.Changed -= OnServiceChanged;
    }
}
