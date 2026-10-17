using Avalonia.Controls;
using Avalonia.Threading;

namespace Sprinkler.Views;

public partial class TextPromptDialogView : UserControl
{
    public TextPromptDialogView()
    {
        InitializeComponent();
        // put the cursor in the box as soon as the dialog appears
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Input.Focus();
            Input.SelectAll();
        });
    }
}
