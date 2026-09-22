using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EasyEdaAltiumGrabber.Models;

namespace EasyEdaAltiumGrabber.Controls;

public sealed class FootprintPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty ComponentProperty = DependencyProperty.Register(
        nameof(Component), typeof(EdaComponent), typeof(FootprintPreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public EdaComponent? Component { get => (EdaComponent?)GetValue(ComponentProperty); set => SetValue(ComponentProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 18)), null, new Rect(RenderSize));
        var component = Component;
        if (component is null || component.Pads.Count == 0)
        {
            DrawMessage(drawing, "Footprint preview", RenderSize.Width / 2, RenderSize.Height / 2);
            return;
        }

        var minX = component.Pads.Min(pad => pad.Xmm - pad.WidthMm / 2);
        var maxX = component.Pads.Max(pad => pad.Xmm + pad.WidthMm / 2);
        var minY = component.Pads.Min(pad => pad.Ymm - pad.HeightMm / 2);
        var maxY = component.Pads.Max(pad => pad.Ymm + pad.HeightMm / 2);
        var width = Math.Max(.1, maxX - minX);
        var height = Math.Max(.1, maxY - minY);
        var scale = Math.Min((RenderSize.Width - 42) / width, (RenderSize.Height - 42) / height);
        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;
        Point Transform(double x, double y) => new(RenderSize.Width / 2 + (x - centerX) * scale, RenderSize.Height / 2 - (y - centerY) * scale);

        var outline = new Pen(new SolidColorBrush(Color.FromRgb(255, 190, 0)), 1.4);
        var body = new Rect(Transform(minX, maxY), Transform(maxX, minY));
        body.Inflate(10, 10);
        drawing.DrawRectangle(null, outline, body);

        foreach (var pad in component.Pads)
        {
            var center = Transform(pad.Xmm, pad.Ymm);
            var rect = new Rect(center.X - pad.WidthMm * scale / 2, center.Y - pad.HeightMm * scale / 2,
                Math.Max(2, pad.WidthMm * scale), Math.Max(2, pad.HeightMm * scale));
            drawing.DrawRectangle(Brushes.Red, new Pen(new SolidColorBrush(Color.FromRgb(255, 95, 95)), 1), rect);
            DrawLabel(drawing, pad.Number, center.X, center.Y);
        }
    }

    private static void DrawLabel(DrawingContext drawing, string label, double x, double y) =>
        drawing.DrawText(new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 15, Brushes.White, 1), new Point(x - 4, y - 10));

    private static void DrawMessage(DrawingContext drawing, string text, double x, double y) =>
        drawing.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, new SolidColorBrush(Color.FromRgb(165, 165, 165)), 1), new Point(x - 55, y - 8));
}

public sealed class SymbolPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty ComponentProperty = DependencyProperty.Register(
        nameof(Component), typeof(EdaComponent), typeof(SymbolPreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public EdaComponent? Component { get => (EdaComponent?)GetValue(ComponentProperty); set => SetValue(ComponentProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        var pinCount = Math.Max(2, Component?.Pads.Count ?? 2);
        var body = new Rect(RenderSize.Width * .28, 18, RenderSize.Width * .44, Math.Max(40, RenderSize.Height - 36));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(145, 45, 45)), 2);
        drawing.DrawRectangle(null, pen, body);
        for (var index = 0; index < pinCount; index++)
        {
            var left = index % 2 == 0;
            var y = body.Top + 18 + (index / 2) * Math.Min(25, (body.Height - 36) / Math.Max(1, (pinCount + 1) / 2));
            var x1 = left ? body.Left - 19 : body.Right;
            var x2 = left ? body.Left : body.Right + 19;
            drawing.DrawLine(pen, new Point(x1, y), new Point(x2, y));
            DrawSymbolLabel(drawing, (index + 1).ToString(), left ? x1 - 14 : x2 + 4, y - 8);
        }
        DrawSymbolLabel(drawing, Component?.LcscPartNumber ?? "PART", body.Left + 7, body.Top + body.Height / 2 - 8);
    }

    private static void DrawSymbolLabel(DrawingContext drawing, string label, double x, double y) =>
        drawing.DrawText(new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, new SolidColorBrush(Color.FromRgb(80, 45, 45)), 1), new Point(x, y));
}
