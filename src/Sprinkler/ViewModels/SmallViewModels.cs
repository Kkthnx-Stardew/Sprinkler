using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprinkler.Core;

namespace Sprinkler.ViewModels;

internal sealed partial class StepViewModel(string text, bool isOn, bool isDone) : ObservableObject
{
    public string Text { get; } = (isDone ? "✓ " : "") + text;
    public bool IsOn { get; } = isOn;
    public bool IsDone { get; } = isDone;
}

internal sealed partial class FilterChipViewModel(MainViewModel main, string key, string label) : ObservableObject
{
    public string Key { get; } = key;

    [ObservableProperty]
    private string _text = label;

    [ObservableProperty]
    private bool _isOn;

    [RelayCommand]
    private void Pick()
    {
        main.SetModFilter(Key);
        // a toggle button flips itself on click; push the real state back
        OnPropertyChanged(nameof(IsOn));
    }
}

internal sealed class ToastViewModel(string text, bool bad)
{
    private readonly bool _bad = bad;

    public string Text { get; } = text;
    public bool IsBad => _bad;
    public string Kind => _bad ? "alert" : "check";
}

internal sealed class CandidateViewModel(MainViewModel main, CandidateInfo info)
{
    public string Text { get; } = $"{info.Store}: {info.Path}" + (info.Smapi is not null ? $"  (SMAPI {info.Smapi})" : "");
    public IRelayCommand Pick { get; } = new AsyncRelayCommand(() => main.SetGameFolderAsync(info.Path));
}

internal sealed class ProblemViewModel(MainViewModel main, Problem problem)
{
    public string Title => problem.Title;
    public string Detail => problem.Detail;
    public bool IsError => problem.Level == "error";
    public bool IsWarn => problem.Level != "error";
    public bool HasLink => !string.IsNullOrEmpty(problem.Url);
    public string LinkText => problem.UrlLabel ?? "Open";
    public IRelayCommand Open { get; } = new RelayCommand(() => main.OpenUrl(problem.Url));
}

internal sealed class SkippedModViewModel(SkippedInfo info)
{
    public string Title { get; } = info.Name + (info.Version is not null ? " " + info.Version : "");
    public string Reason { get; } = "SMAPI says " + info.Reason;
    public string Hint { get; } = "What to do: " + info.Hint;
}

internal sealed class LogErrorViewModel(LogErrorSource source)
{
    public string Text { get; } = $"{source.Source}: {Fmt.Plural(source.Count, "error", "errors")}";
}

// ------------------------------------------------------------------ dialogs

internal abstract partial class DialogViewModel : ObservableObject
{
    internal Action<DialogViewModel>? Closed { get; set; }

    protected void Close() => Closed?.Invoke(this);
}

internal sealed record DialogLine(string Lead, string Text);

internal sealed partial class ConfirmDialogViewModel : DialogViewModel
{
    private readonly TaskCompletionSource<bool> _result = new();

    public ConfirmDialogViewModel(string title, string icon, string yesText, string noText, IEnumerable<DialogLine>? lines = null, IEnumerable<string>? bullets = null)
    {
        Title = title;
        Icon = icon;
        YesText = yesText;
        NoText = noText;
        Lines = new ObservableCollection<DialogLine>(lines ?? Array.Empty<DialogLine>());
        Bullets = new ObservableCollection<string>(bullets ?? Array.Empty<string>());
    }

    public string Title { get; }
    public string Icon { get; }
    public string YesText { get; }
    public string NoText { get; }
    public ObservableCollection<DialogLine> Lines { get; }
    public ObservableCollection<string> Bullets { get; }
    public bool HasBullets => Bullets.Count > 0;
    public Task<bool> Result => _result.Task;

    [RelayCommand]
    private void Yes()
    {
        _result.TrySetResult(true);
        Close();
    }

    [RelayCommand]
    public void No()
    {
        _result.TrySetResult(false);
        Close();
    }
}

internal sealed record TourStep(string Icon, string Title, string Text);

internal sealed partial class TourDialogViewModel : DialogViewModel
{
    private readonly Action _done;

    public TourDialogViewModel(IReadOnlyList<TourStep> steps, int start, Action done)
    {
        Steps = steps;
        _index = start;
        _done = done;
        Dots = new ObservableCollection<bool>(Enumerable.Range(0, steps.Count).Select(i => i == start));
    }

    public IReadOnlyList<TourStep> Steps { get; }
    public ObservableCollection<bool> Dots { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(IsFirst), nameof(IsLast), nameof(NextText), nameof(SkipText))]
    private int _index;

    public TourStep Current => Steps[Index];
    public bool IsFirst => Index == 0;
    public bool IsLast => Index == Steps.Count - 1;
    public string NextText => IsLast ? "Done" : "Next";
    public string SkipText => IsLast ? "Close" : "Skip";

    partial void OnIndexChanged(int value)
    {
        for (var i = 0; i < Dots.Count; i++)
            Dots[i] = i == value;
    }

    [RelayCommand]
    private void Next()
    {
        if (IsLast)
            Finish();
        else
            Index++;
    }

    [RelayCommand]
    private void Back()
    {
        if (Index > 0)
            Index--;
    }

    [RelayCommand]
    public void Finish()
    {
        _done();
        Close();
    }
}

/// <summary>Asks for one line of text, such as a profile name or a note about a mod.</summary>
internal sealed partial class TextPromptDialogViewModel : DialogViewModel
{
    private readonly TaskCompletionSource<string?> _result = new();

    public TextPromptDialogViewModel(string title, string icon, string prompt, string initial, string yesText, int maxLength)
    {
        Title = title;
        Icon = icon;
        Prompt = prompt;
        YesText = yesText;
        MaxLength = maxLength;
        _text = initial;
    }

    public string Title { get; }
    public string Icon { get; }
    public string Prompt { get; }
    public string YesText { get; }
    public int MaxLength { get; }
    public Task<string?> Result => _result.Task;

    [ObservableProperty]
    private string _text;

    [RelayCommand]
    private void Ok()
    {
        _result.TrySetResult(Text);
        Close();
    }

    [RelayCommand]
    public void Cancel()
    {
        _result.TrySetResult(null);
        Close();
    }
}

/// <summary>Shows what a Nexus collection holds before it is added, and asks whether to include its optional mods.</summary>
internal sealed partial class CollectionPreviewDialogViewModel : DialogViewModel
{
    private readonly TaskCompletionSource<bool?> _result = new();

    public CollectionPreviewDialogViewModel(CollectionPreview preview)
    {
        Preview = preview;
    }

    public CollectionPreview Preview { get; }

    public string Title => Preview.Name;
    public string Curator => Preview.Curator is null ? "" : "by " + Preview.Curator;
    public bool HasCurator => Preview.Curator is not null;
    public string? Summary => Preview.Summary;
    public bool HasSummary => !string.IsNullOrEmpty(Preview.Summary);
    public bool HasOptional => Preview.Optional > 0;

    public string Counts
    {
        get
        {
            var line = $"{Fmt.Plural(Preview.Required, "mod", "mods")}, {Preview.RequiredInstalled} already installed.";
            if (Preview.Optional > 0)
                line += $" Plus {Fmt.Plural(Preview.Optional, "optional mod", "optional mods")}.";
            return line;
        }
    }

    public string ToInstallText
    {
        get
        {
            var missing = Preview.Required - Preview.RequiredInstalled + (IncludeOptional ? Preview.Optional - Preview.OptionalInstalled : 0);
            return missing == 0
                ? "Everything in it is already installed. The profile only remembers the list."
                : $"{Fmt.Plural(missing, "mod", "mods")} will appear under Updates as installs. Nothing is changed until you press Update there.";
        }
    }

    public string OptionalText => $"Also add the {Fmt.Plural(Preview.Optional, "optional mod", "optional mods")}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToInstallText))]
    private bool _includeOptional;

    /// <summary>Null when cancelled, otherwise whether to include the optional mods.</summary>
    public Task<bool?> Result => _result.Task;

    [RelayCommand]
    private void Add()
    {
        _result.TrySetResult(IncludeOptional);
        Close();
    }

    [RelayCommand]
    public void Cancel()
    {
        _result.TrySetResult(null);
        Close();
    }
}
