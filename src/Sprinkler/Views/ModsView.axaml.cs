using Avalonia.Controls;

namespace Sprinkler.Views;

public partial class ModsView : UserControl
{
    // Below this the list would shrink to a row or two, so the page scrolls instead.
    private const double MinBodyHeight = 480;

    public ModsView()
    {
        InitializeComponent();
        Page.SizeChanged += (_, e) =>
            Body.Height = Math.Max(MinBodyHeight, e.NewSize.Height - Body.Margin.Top - Body.Margin.Bottom);
    }
}
