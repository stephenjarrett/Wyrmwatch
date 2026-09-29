using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Wyrmwatch.Desktop;

public sealed class Sparkline : Control
{
    private readonly Queue<double> values = new();
    public Color Color { get; set; } = Color.Parse("#A896FF");
    public double Maximum { get; set; } = 100;
    public void Add(double value) { values.Enqueue(value); while (values.Count > 100) values.Dequeue(); InvalidateVisual(); }
    public void Clear() { values.Clear(); InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width; var height = Bounds.Height - 12;
        var grid = new Pen(new SolidColorBrush(Colors.Gray, .16), 1);
        for (var i = 0; i <= 4; i++) context.DrawLine(grid, new(0, 6 + height * i / 4), new(width, 6 + height * i / 4));
        if (values.Count < 2) return;
        var samples = values.ToArray(); var max = Math.Max(Maximum, samples.Max() * 1.15); var pen = new Pen(new SolidColorBrush(Color), 2);
        Point PointAt(int i) => new(width * i / 99, 6 + height - Math.Clamp(samples[i] / max, 0, 1) * height);
        var offset = width * (100 - samples.Length) / 99;
        for (var i = 1; i < samples.Length; i++) { var a = PointAt(i - 1); var b = PointAt(i); context.DrawLine(pen, new(a.X + offset, a.Y), new(b.X + offset, b.Y)); }
    }
}
