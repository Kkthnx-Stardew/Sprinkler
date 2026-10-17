using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Sprinkler.Controls;

/// <summary>A line icon that takes the colour of the text around it, so one icon works in light and dark themes and on any button.</summary>
public sealed class IconView : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<IconView, string?>(nameof(Kind));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<IconView, double>(nameof(Size), 20);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<IconView>();

    static IconView()
    {
        AffectsRender<IconView>(KindProperty, SizeProperty, ForegroundProperty);
        AffectsMeasure<IconView>(SizeProperty);
    }

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var kind = Kind;
        var geometry = IconData.Get(kind);
        if (geometry is null || kind is null)
            return;

        var brush = Foreground ?? Brushes.Gray;
        var pen = new Pen(brush, Size >= 36 ? 1.6 : 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var scale = Size / 24.0;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(IconData.IsFilled(kind) ? brush : null, pen, geometry);
    }
}
