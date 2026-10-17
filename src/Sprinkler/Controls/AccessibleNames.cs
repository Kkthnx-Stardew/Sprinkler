using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace Sprinkler.Controls;

/// <summary>
/// A button that holds an icon and a label is read by a screen reader as the type of its content ("StackPanel") unless it is
/// given a name. This gives every such button the text of its own label, and follows the label when the language changes.
/// Buttons that already have a name, or whose content is plain text, are left alone.
/// </summary>
internal static class AccessibleNames
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        Control.LoadedEvent.AddClassHandler<Button>(OnLoaded);
        Control.LoadedEvent.AddClassHandler<ListBoxItem>(OnLoaded);
    }

    private static void OnLoaded(ContentControl item, RoutedEventArgs e)
    {
        if (item.Content is string || !string.IsNullOrEmpty(AutomationProperties.GetName(item)))
            return;
        var labels = item.GetLogicalDescendants().OfType<TextBlock>().ToList();
        if (labels.Count == 0)
            return;
        if (item.Classes.Contains("tile"))
        {
            // a statistic tile is a number and what it counts; read both
            void Join() => AutomationProperties.SetName(item, string.Join(' ', labels.Select(l => l.Text)));
            foreach (var l in labels)
                l.PropertyChanged += (_, args) =>
                {
                    if (args.Property == TextBlock.TextProperty)
                        Join();
                };
            Join();
            return;
        }
        item.Bind(AutomationProperties.NameProperty, labels[0].GetObservable(TextBlock.TextProperty));
    }
}
