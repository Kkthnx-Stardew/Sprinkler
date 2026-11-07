using Avalonia;
using Avalonia.Controls;

namespace Sprinkler.Views;

/// <summary>The Play button. It opens a menu to start the game with or without mods instead of starting it at once.</summary>
public partial class PlayMenuButton : UserControl
{
    public static readonly StyledProperty<string?> CaptionProperty = AvaloniaProperty.Register<PlayMenuButton, string?>(nameof(Caption));
    public static readonly StyledProperty<bool> BigProperty = AvaloniaProperty.Register<PlayMenuButton, bool>(nameof(Big));

    public PlayMenuButton() => InitializeComponent();

    /// <summary>The words on the button, for example "Play" or "Play Stardew Valley".</summary>
    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>Uses the larger button style, for the places where Play is the main action of the page.</summary>
    public bool Big
    {
        get => GetValue(BigProperty);
        set => SetValue(BigProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BigProperty)
            PlayButton.Classes.Set("big", Big);
    }
}
