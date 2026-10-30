using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Sprinkler.ViewModels;

namespace Sprinkler.Views;

public partial class MainWindow : Window, IUiServices
{
    private const double NarrowWidth = 940;
    private const double ShortHeight = 700;

    private int _dragDepth;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        KeyDown += OnKeyDown;
        Opened += (_, _) => ScrollSelectedTabIntoView();
        // notices may take a third of a short window at most, so the tab below keeps room
        SizeChanged += (_, e) =>
        {
            NoticeScroller.MaxHeight = Math.Clamp(e.NewSize.Height * 0.3, 90, 300);
            // the styles key off these two: a narrow window gets compact tabs, a short one loses its decoration
            Classes.Set("narrow", e.NewSize.Width < NarrowWidth);
            Classes.Set("short", e.NewSize.Height < ShortHeight);
        };
    }

    internal MainViewModel? ViewModel => DataContext as MainViewModel;

    // The tab strip scrolls when the window is narrow; keep the chosen tab in view.
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e) => ScrollSelectedTabIntoView();

    private void ScrollSelectedTabIntoView()
    {
        if (TabStrip.SelectedItem is not Control tab)
            return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // the list box swallows BringIntoView, so move the outer scroller by hand
            var at = new Avalonia.Point(tab.Bounds.X + TabStrip.Padding.Left, 0);
            var left = TabScroller.Offset.X;
            var view = TabScroller.Viewport.Width;
            if (at.X < left)
                left = at.X;
            else if (at.X + tab.Bounds.Width > left + view)
                left = at.X + tab.Bounds.Width - view;
            TabScroller.Offset = new Avalonia.Vector(Math.Max(0, left), 0);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    internal void Attach(MainViewModel vm)
    {
        vm.Ui = this;
        DataContext = vm;
    }

    // ---- IUiServices
    public void Exit() => Close();

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickOpenFileAsync(string title, string typeName, IReadOnlyList<string> patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(typeName) { Patterns = patterns } },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string typeName, IReadOnlyList<string> patterns)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = patterns } },
        });
        return file?.TryGetLocalPath();
    }

    // ---- keyboard: Escape closes a dialog
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        switch (ViewModel?.Dialog)
        {
            case ConfirmDialogViewModel confirm:
                confirm.No();
                e.Handled = true;
                break;
            case TourDialogViewModel tour:
                tour.Finish();
                e.Handled = true;
                break;
            case CollectionPreviewDialogViewModel preview:
                preview.Cancel();
                e.Handled = true;
                break;
            case TextPromptDialogViewModel prompt:
                prompt.Cancel();
                e.Handled = true;
                break;
        }
    }

    // ---- drag and drop of mod archives
    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (!HasFiles(e))
            return;
        _dragDepth++;
        DropOverlay.IsVisible = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _dragDepth = Math.Max(0, _dragDepth - 1);
        if (_dragDepth == 0)
            DropOverlay.IsVisible = false;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        _dragDepth = 0;
        DropOverlay.IsVisible = false;
        var paths = (e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>())
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .ToList();
        if (paths.Count == 0 || ViewModel is not { } vm)
            return;
        try
        {
            await vm.HandleDroppedAsync(paths);
        }
        catch (Exception ex)
        {
            // an async void handler that throws would end the whole program
            Sprinkler.Core.Log.Write("Handling a drop failed: " + ex);
        }
    }
}
