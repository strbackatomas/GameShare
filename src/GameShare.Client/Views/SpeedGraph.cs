using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GameShare.Client.ViewModels;

namespace GameShare.Client.Views;

/// <summary>
/// Draws a <see cref="SpeedHistory"/> as a filled line, newest on the right, like Steam's download graph.
/// The graph is always <see cref="SpeedHistory.Capacity"/> seconds wide, so it fills up from the right and then scrolls.
/// The top is the highest speed in view, so a slow transfer still shows its shape; the numbers are next to the graph.
/// </summary>
public sealed class SpeedGraph : Control
{
    public static readonly StyledProperty<SpeedHistory?> HistoryProperty =
        AvaloniaProperty.Register<SpeedGraph, SpeedHistory?>(nameof(History));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Stroke), Brushes.Orange);

    public static readonly StyledProperty<IBrush?> GridProperty =
        AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Grid), Brushes.Gray);

    public SpeedHistory? History { get => GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Grid { get => GetValue(GridProperty); set => SetValue(GridProperty, value); }

    static SpeedGraph() => AffectsRender<SpeedGraph>(HistoryProperty, StrokeProperty, GridProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != HistoryProperty) return;
        if (change.OldValue is SpeedHistory old) old.Changed -= OnHistoryChanged;
        if (change.NewValue is SpeedHistory now && VisualRoot is not null) now.Changed += OnHistoryChanged;
    }

    // Subscribed only while shown, so a graph that left the screen does not keep a download's history holding on to it.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (History is { } h) h.Changed += OnHistoryChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (History is { } h) h.Changed -= OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return;

        // Faint lines at a quarter, half and three quarters of the height, so the eye can read proportions.
        if (Grid is { } grid)
        {
            var pen = new Pen(grid, 1) { DashStyle = new DashStyle([2, 4], 0) };
            for (int i = 1; i < 4; i++)
            {
                double y = Math.Round(size.Height * i / 4) + 0.5;
                context.DrawLine(pen, new Point(0, y), new Point(size.Width, y));
            }
        }

        var values = History?.Values;
        if (values is null || values.Count == 0) return;
        double top = Math.Max(1, values.Max()) * 1.1;
        double step = size.Width / (SpeedHistory.Capacity - 1);
        double x0 = size.Width - (values.Count - 1) * step;
        Point At(int i) => new(x0 + i * step, size.Height - values[i] / top * size.Height);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(At(0), isFilled: false);
            a.BeginFigure(new Point(x0, size.Height), isFilled: true);
            a.LineTo(At(0));
            for (int i = 1; i < values.Count; i++)
            {
                l.LineTo(At(i));
                a.LineTo(At(i));
            }
            a.LineTo(new Point(size.Width, size.Height));
            l.EndFigure(isClosed: false);
            a.EndFigure(isClosed: true);
        }

        var stroke = Stroke ?? Brushes.Orange;
        var fill = stroke is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.25) : stroke;
        context.DrawGeometry(fill, null, area);
        context.DrawGeometry(null, new Pen(stroke, 1.5), line);
    }
}
