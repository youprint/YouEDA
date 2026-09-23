using System;
using System.IO;
using System.Linq;
using OriginalCircuit.Altium.Models.Sch;
using OriginalCircuit.Altium.Rendering.Raster;
using OriginalCircuit.Eda.Primitives;
using OriginalCircuit.Eda.Rendering;

namespace EasyEdaAltiumGrabber.Controls;

/// <summary>Renders the same native schematic primitives that YouEDA exports.</summary>
public static class SchematicSymbolPreviewRenderer
{
    public static byte[] RenderPng(SchComponent symbol, int width = 480, int height = 320)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        using var output = new MemoryStream();
        new RasterRenderer().RenderAsync(NormalizeLegacyArtwork(symbol), output, new RenderOptions
        {
            Width = width,
            Height = height,
            AutoZoom = true
        }).AsTask().GetAwaiter().GetResult();
        return output.ToArray();
    }

    // AltiumSharp currently decodes graphic vertex coordinates in some imported user SchLibs
    // 100x smaller than pin coordinates. Normalize only that recognizable case for the preview;
    // never mutate the cached catalog component or the data written to the output SchLib.
    private static SchComponent NormalizeLegacyArtwork(SchComponent source)
    {
        var pinPoints = source.Pins.Select(pin => pin.Location).ToArray();
        var artPoints = source.Polylines.SelectMany(line => line.Vertices)
            .Concat(source.Polygons.SelectMany(shape => shape.Vertices))
            .Concat(source.Lines.SelectMany(line => new[] { line.Start, line.End }))
            .Concat(source.Rectangles.SelectMany(rect => new[] { rect.Corner1, rect.Corner2 }))
            .ToArray();
        if (pinPoints.Length == 0 || artPoints.Length < 2) return source;
        static double Span(CoordPoint[] points) => Math.Max(
            points.Max(point => point.X.ToMm()) - points.Min(point => point.X.ToMm()),
            points.Max(point => point.Y.ToMm()) - points.Min(point => point.Y.ToMm()));
        var pinSpan = Math.Max(Span(pinPoints), source.Pins.Max(pin => pin.Length.ToMm()));
        var artSpan = Span(artPoints);
        if (pinSpan < 1 || artSpan <= 0 || artSpan >= pinSpan / 20) return source;

        const double scale = 100;
        static Coord Scaled(Coord value) => Coord.FromMm(value.ToMm() * scale);
        static CoordPoint ScaledPoint(CoordPoint point) => new(Scaled(point.X), Scaled(point.Y));
        var preview = new SchComponent
        {
            Name = source.Name, PartCount = source.PartCount, CurrentPartId = source.CurrentPartId,
            DisplayMode = source.DisplayMode, Fonts = source.Fonts
        };
        foreach (var pin in source.Pins.OfType<SchPin>()) preview.AddPin(pin);
        foreach (var parameter in source.Parameters.OfType<SchParameter>()) preview.AddParameter(parameter);
        foreach (var label in source.Labels.OfType<SchLabel>()) preview.AddLabel(label);
        foreach (var line in source.Lines.OfType<SchLine>())
            preview.AddLine(new SchLine { Start = ScaledPoint(line.Start), End = ScaledPoint(line.End),
                Width = line.Width, Color = line.Color, LineStyle = line.LineStyle });
        foreach (var line in source.Polylines.OfType<SchPolyline>())
        {
            var copy = SchPolyline.Create();
            foreach (var point in line.Vertices) copy.AddVertex(Scaled(point.X), Scaled(point.Y));
            var built = copy.Build();
            built.LineWidth = line.LineWidth;
            built.Color = line.Color;
            built.LineStyle = line.LineStyle;
            built.OwnerPartId = line.OwnerPartId;
            preview.AddPolyline(built);
        }
        foreach (var polygon in source.Polygons.OfType<SchPolygon>())
        {
            var copy = SchPolygon.Create();
            foreach (var point in polygon.Vertices) copy.AddVertex(Scaled(point.X), Scaled(point.Y));
            var built = copy.Build();
            built.LineWidth = polygon.LineWidth;
            built.Color = polygon.Color;
            built.FillColor = polygon.FillColor;
            built.IsFilled = polygon.IsFilled;
            built.IsTransparent = polygon.IsTransparent;
            built.OwnerPartId = polygon.OwnerPartId;
            preview.AddPolygon(built);
        }
        foreach (var rect in source.Rectangles.OfType<SchRectangle>())
            preview.AddRectangle(new SchRectangle { Corner1 = ScaledPoint(rect.Corner1),
                Corner2 = ScaledPoint(rect.Corner2), LineWidth = rect.LineWidth,
                Color = rect.Color, FillColor = rect.FillColor, IsFilled = rect.IsFilled });
        foreach (var rect in source.RoundedRectangles.OfType<SchRoundedRectangle>())
            preview.AddRoundedRectangle(new SchRoundedRectangle { Corner1 = ScaledPoint(rect.Corner1),
                Corner2 = ScaledPoint(rect.Corner2), CornerRadiusX = Scaled(rect.CornerRadiusX),
                CornerRadiusY = Scaled(rect.CornerRadiusY), LineWidth = rect.LineWidth,
                Color = rect.Color, FillColor = rect.FillColor, IsFilled = rect.IsFilled });
        foreach (var arc in source.Arcs.OfType<SchArc>())
            preview.AddArc(new SchArc { Center = ScaledPoint(arc.Center), Radius = Scaled(arc.Radius),
                StartAngle = arc.StartAngle, EndAngle = arc.EndAngle, LineWidth = arc.LineWidth, Color = arc.Color });
        foreach (var ellipse in source.Ellipses.OfType<SchEllipse>())
            preview.AddEllipse(new SchEllipse { Center = ScaledPoint(ellipse.Center),
                RadiusX = Scaled(ellipse.RadiusX), RadiusY = Scaled(ellipse.RadiusY),
                LineWidth = ellipse.LineWidth, Color = ellipse.Color, FillColor = ellipse.FillColor,
                IsFilled = ellipse.IsFilled });
        foreach (var bezier in source.Beziers.OfType<SchBezier>())
        {
            var copy = SchBezier.Create();
            foreach (var point in bezier.ControlPoints) copy.AddPoint(Scaled(point.X), Scaled(point.Y));
            var built = copy.Build();
            built.LineWidth = bezier.LineWidth;
            built.Color = bezier.Color;
            preview.AddBezier(built);
        }
        return preview;
    }
}
