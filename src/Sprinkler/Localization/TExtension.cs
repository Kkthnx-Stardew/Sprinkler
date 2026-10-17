using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Sprinkler.Core;

namespace Sprinkler.Localization;

/// <summary>
/// Text from the language files for use in XAML: Text="{loc:T ui.some_key}". It is a live binding to the language, so the screen
/// changes the moment the person picks another language, with no restart.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
