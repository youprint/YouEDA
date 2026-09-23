using System.Globalization;
using System.IO;
using System.Text;
using EasyEdaAltiumGrabber.Models;
using OriginalCircuit.Altium.Models.Sch;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Writes portable, native KiCad symbol, footprint, and 3D libraries.</summary>
public sealed class KiCadLibraryExporter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public async Task<string> UpsertAsync(EdaComponent source, string outputDirectory,
        SchComponent? matchedUserSymbol = null, Downloaded3dModel? model = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var footprintDirectory = Path.Combine(outputDirectory, "youeda.pretty");
        var modelDirectory = Path.Combine(outputDirectory, "youeda.3dshapes");
        Directory.CreateDirectory(footprintDirectory);
        var modelFile = model is null ? null : source.LcscPartNumber + ".step";
        var footprintPath = Path.Combine(footprintDirectory, source.LcscPartNumber + ".kicad_mod");
        var symbolPath = Path.Combine(outputDirectory, "youeda.kicad_sym");
        var footprintText = BuildFootprint(source, model, modelFile);
        var newSymbol = BuildSymbol(source, matchedUserSymbol);
        var existing = File.Exists(symbolPath) ? await File.ReadAllTextAsync(symbolPath, ct) :
            "(kicad_symbol_lib (version 20211014) (generator kicad_symbol_editor)\n)\n";
        var updatedSymbolLibrary = UpsertSymbol(existing, source.LcscPartNumber, newSymbol);
        if (modelFile is not null)
        {
            Directory.CreateDirectory(modelDirectory);
            File.Copy(model!.Path, Path.Combine(modelDirectory, modelFile), overwrite: true);
        }
        await AtomicWriteAsync(footprintPath, footprintText, ct);
        await AtomicWriteAsync(symbolPath, updatedSymbolLibrary, ct);
        await WriteTableIfMissingAsync(Path.Combine(outputDirectory, "sym-lib-table"),
            "(sym_lib_table\n  (lib (name \"youeda\")(type \"KiCad\")(uri \"${KIPRJMOD}/youeda.kicad_sym\")(options \"\")(descr \"YouEDA symbols\"))\n)\n", ct);
        await WriteTableIfMissingAsync(Path.Combine(outputDirectory, "fp-lib-table"),
            "(fp_lib_table\n  (lib (name \"youeda\")(type \"KiCad\")(uri \"${KIPRJMOD}/youeda.pretty\")(options \"\")(descr \"YouEDA footprints\"))\n)\n", ct);
        return symbolPath;
    }

    private static async Task WriteTableIfMissingAsync(string path, string content, CancellationToken ct)
    {
        if (!File.Exists(path)) await AtomicWriteAsync(path, content, ct);
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string BuildFootprint(EdaComponent source, Downloaded3dModel? model, string? modelFile)
    {
        var name = Escape(source.LcscPartNumber);
        var builder = new StringBuilder();
        builder.AppendLine($"(footprint \"{name}\" (version 20240108) (generator \"YouEDA\") (layer \"F.Cu\")");
        builder.AppendLine($"  (descr \"{Escape(source.Description)}\")");
        builder.AppendLine(source.Pads.Any(pad => pad.HoleMm > 0) || source.Shapes.Any(shape => shape.Kind == "HOLE")
            ? "  (attr through_hole)" : "  (attr smd)");
        builder.AppendLine("  (fp_text reference \"REF**\" (at 0 -2) (layer \"F.SilkS\") (effects (font (size 1 1) (thickness 0.15))))");
        builder.AppendLine($"  (fp_text value \"{name}\" (at 0 2) (layer \"F.Fab\") (effects (font (size 1 1) (thickness 0.15))))");
        foreach (var pad in source.Pads)
        {
            var type = pad.HoleMm > 0 ? pad.Plated ? "thru_hole" : "np_thru_hole" : "smd";
            var layer = pad.Layer is "2" or "BottomLayer" ? "B" : "F";
            var layers = type == "smd" ? $"\"{layer}.Cu\" \"{layer}.Paste\" \"{layer}.Mask\"" : "\"*.Cu\" \"*.Mask\"";
            var shape = pad.Shape switch { "OVAL" or "ELLIPSE" => "oval", "CIRCLE" => "circle", _ => "rect" };
            if (pad.Shape == "POLYGON" && pad.PolygonPointsMm.Count > 2 && type == "smd") shape = "custom";
            builder.Append($"  (pad \"{Escape(pad.Number)}\" {type} {shape} (at {N(pad.Xmm)} {N(-pad.Ymm)} {N(shape == "custom" ? 0 : -pad.RotationDeg)})");
            builder.Append(shape == "custom" ? " (size 0.01 0.01)" :
                $" (size {N(Math.Max(0.01, pad.WidthMm))} {N(Math.Max(0.01, pad.HeightMm))})");
            builder.Append($" (layers {layers})");
            if (pad.HoleMm > 0)
                builder.Append(pad.SlotLengthMm > 0 ?
                    $" (drill oval {N(pad.HoleMm)} {N(pad.SlotLengthMm)})" :
                    $" (drill {N(pad.HoleMm)})");
            if (shape == "custom")
            {
                builder.Append(" (options (clearance outline) (anchor rect)) (primitives (gr_poly (pts");
                foreach (var point in pad.PolygonPointsMm)
                    builder.Append($" (xy {N(point.X - pad.Xmm)} {N(-point.Y + pad.Ymm)})");
                builder.Append(" ) (width 0) (fill yes)))");
            }
            builder.AppendLine(")");
        }
        foreach (var track in source.Shapes.Where(item => item.Kind == "TRACK" && item.PointsMm.Count > 1))
        {
            var layer = MapLayer(track.Layer);
            for (var i = 1; i < track.PointsMm.Count; i++)
            {
                var a = track.PointsMm[i - 1]; var b = track.PointsMm[i];
                builder.AppendLine($"  (fp_line (start {N(a.X)} {N(-a.Y)}) (end {N(b.X)} {N(-b.Y)}) (stroke (width {N(Math.Max(.01, track.StrokeMm))}) (type solid)) (layer \"{layer}\"))");
            }
        }
        foreach (var circle in source.Shapes.Where(item => item.Kind == "CIRCLE" && item.PointsMm.Count > 1))
        {
            var center = circle.PointsMm[0]; var radius = circle.PointsMm[1].X;
            builder.AppendLine($"  (fp_circle (center {N(center.X)} {N(-center.Y)}) (end {N(center.X + radius)} {N(-center.Y)}) (stroke (width {N(Math.Max(.01, circle.StrokeMm))}) (type solid)) (fill none) (layer \"{MapLayer(circle.Layer)}\"))");
        }
        foreach (var rect in source.Shapes.Where(item => item.Kind == "RECT" && item.PointsMm.Count > 1))
        {
            var a = rect.PointsMm[0]; var z = rect.PointsMm[1];
            builder.AppendLine($"  (fp_rect (start {N(a.X)} {N(-a.Y)}) (end {N(z.X)} {N(-z.Y)}) (stroke (width {N(Math.Max(.01, rect.StrokeMm))}) (type solid)) (fill none) (layer \"{MapLayer(rect.Layer)}\"))");
        }
        foreach (var hole in source.Shapes.Where(item => item.Kind == "HOLE" && item.PointsMm.Count > 1))
        {
            var center = hole.PointsMm[0]; var diameter = 2 * hole.PointsMm[1].X;
            builder.AppendLine($"  (pad \"\" np_thru_hole circle (at {N(center.X)} {N(-center.Y)}) (size {N(diameter)} {N(diameter)}) (drill {N(diameter)}) (layers \"*.Cu\" \"*.Mask\"))");
        }
        foreach (var region in source.Shapes.Where(item => item.Kind == "SOLIDREGION" && item.PointsMm.Count >= 3 && item.Layer is not "100" and not "101"))
        {
            var layer = MapLayer(region.Layer);
            if (region.Layer == "99")
            {
                for (var i = 1; i < region.PointsMm.Count; i++)
                {
                    var a = region.PointsMm[i - 1]; var z = region.PointsMm[i];
                    builder.AppendLine($"  (fp_line (start {N(a.X)} {N(-a.Y)}) (end {N(z.X)} {N(-z.Y)}) (stroke (width 0.05) (type solid)) (layer \"F.CrtYd\"))");
                }
            }
            else if (region.Layer is "3" or "4" or "13" or "14")
            {
                builder.Append("  (fp_poly (pts");
                foreach (var point in region.PointsMm) builder.Append($" (xy {N(point.X)} {N(-point.Y)})");
                builder.AppendLine($") (stroke (width 0) (type solid)) (fill solid) (layer \"{layer}\"))");
            }
        }
        if (model is not null && modelFile is not null)
        {
            // The ${KIPRJMOD} reference is portable when the generated folder is the project folder.
            builder.AppendLine($"  (model \"${{KIPRJMOD}}/youeda.3dshapes/{modelFile}\" (offset (xyz {N(model.Source.Xmm)} {N(-model.Source.Ymm)} {N(model.Source.Zmm + model.ZOffsetMm)})) (scale (xyz 1 1 1)) (rotate (xyz {N(model.Source.RotationXDeg)} {N(model.Source.RotationYDeg)} {N(model.Source.RotationZDeg)})))");
        }
        builder.AppendLine(")");
        return builder.ToString();
    }

    private static string BuildSymbol(EdaComponent source, SchComponent? matched)
    {
        var b = new StringBuilder();
        var name = Escape(source.LcscPartNumber);
        var reference = Escape((matched?.DesignatorPrefix ?? source.Properties.GetValueOrDefault("pre") ?? "U").TrimEnd('?'));
        b.AppendLine($"  (symbol \"{name}\" (in_bom yes) (on_board yes)");
        Property(b, "Reference", reference, 0, 5.08);
        Property(b, "Value", source.Name, 0, -5.08);
        Property(b, "Footprint", "youeda:" + source.LcscPartNumber, 0, 0, hidden: true);
        Property(b, "Datasheet", source.Properties.GetValueOrDefault("Datasheet") ?? "", 0, 0, hidden: true);
        Property(b, "Description", source.Description, 0, 0, hidden: true);
        Property(b, "LCSC", source.LcscPartNumber, 0, 0, hidden: true);

        if (matched is not null && matched.Pins.Count > 0)
            AppendAltiumUnits(b, name, matched);
        else if (source.SymbolUnits.Count > 0)
        {
            for (var i = 0; i < source.SymbolUnits.Count; i++)
            {
                var unit = source.SymbolUnits[i];
                b.AppendLine($"    (symbol \"{name}_{i + 1}_1\"");
                foreach (var graphic in unit.Graphics) AppendGraphic(b, graphic);
                foreach (var pin in unit.Pins)
                    AppendPin(b, pin.Number, pin.Name, MapEasyEdaElectrical(pin.ElectricalType),
                        pin.Xmm, pin.Ymm, (180 - pin.RotationDeg + 360) % 360, pin.LengthMm);
                b.AppendLine("    )");
            }
        }
        else throw new InvalidDataException($"{source.LcscPartNumber} has no schematic symbol data.");
        b.AppendLine("  )");
        return b.ToString();
    }

    private static void AppendAltiumUnits(StringBuilder b, string name, SchComponent symbol)
    {
        var graphicExtent = symbol.Polylines.SelectMany(line => line.Vertices)
            .Concat(symbol.Polygons.SelectMany(polygon => polygon.Vertices))
            .SelectMany(point => new[] { Math.Abs(point.X.ToMm()), Math.Abs(point.Y.ToMm()) })
            .Concat(symbol.Rectangles.SelectMany(rect => new[] { Math.Abs(rect.Corner1.X.ToMm()), Math.Abs(rect.Corner1.Y.ToMm()),
                Math.Abs(rect.Corner2.X.ToMm()), Math.Abs(rect.Corner2.Y.ToMm()) }))
            .Concat(symbol.Arcs.SelectMany(arc => new[] { Math.Abs(arc.Center.X.ToMm()) + arc.Radius.ToMm(),
                Math.Abs(arc.Center.Y.ToMm()) + arc.Radius.ToMm() }))
            .Concat(symbol.Beziers.SelectMany(curve => curve.ControlPoints).SelectMany(point =>
                new[] { Math.Abs(point.X.ToMm()), Math.Abs(point.Y.ToMm()) }))
            .DefaultIfEmpty(0).Max();
        var pinExtent = symbol.Pins.SelectMany(pin => new[] { Math.Abs(pin.Location.X.ToMm()), Math.Abs(pin.Location.Y.ToMm()) })
            .DefaultIfEmpty(0).Max();
        // Some legacy Altium catalogs encode graphic vertices in coarse 100-mil steps
        // while their binary pin positions are already millimetre-scaled by AltiumSharp.
        // Detect that mixed scale instead of emitting a nearly invisible body.
        var graphicScale = graphicExtent > 0 && graphicExtent < .15 && pinExtent > .5 && pinExtent / graphicExtent > 20 ? 100.0 : 1.0;
        (double X, double Y) Point(OriginalCircuit.Eda.Primitives.CoordPoint point) =>
            (point.X.ToMm() * graphicScale, point.Y.ToMm() * graphicScale);
        b.AppendLine($"    (symbol \"{name}_1_1\"");
        foreach (var rect in symbol.Rectangles)
            AppendGraphic(b, new EdaSymbolGraphic("RECT", [Point(rect.Corner1), Point(rect.Corner2)],
                Math.Max(.1524, rect.LineWidth.ToMm()), rect.IsFilled));
        foreach (var line in symbol.Lines)
            AppendGraphic(b, new EdaSymbolGraphic("POLYLINE", [Point(line.Start), Point(line.End)], Math.Max(.1524, line.Width.ToMm()), false));
        foreach (var line in symbol.Polylines.Where(line => line.Vertices.Count > 1))
            AppendGraphic(b, new EdaSymbolGraphic("POLYLINE", line.Vertices.Select(Point).ToArray(), .1524, false));
        foreach (var polygon in symbol.Polygons.Where(item => item.Vertices.Count > 2))
            AppendGraphic(b, new EdaSymbolGraphic("POLYGON", polygon.Vertices.Select(Point).ToArray(), .1524, polygon.IsFilled));
        foreach (var ellipse in symbol.Ellipses)
            AppendGraphic(b, new EdaSymbolGraphic("ELLIPSE", [Point(ellipse.Center),
                (ellipse.RadiusX.ToMm() * graphicScale, ellipse.RadiusY.ToMm() * graphicScale)], .1524, ellipse.IsFilled));
        foreach (var arc in symbol.Arcs)
        {
            var center = Point(arc.Center);
            var radius = arc.Radius.ToMm() * graphicScale;
            var sweep = arc.EndAngle - arc.StartAngle;
            if (sweep <= 0) sweep += 360;
            var steps = Math.Max(4, (int)Math.Ceiling(sweep / 10));
            var points = Enumerable.Range(0, steps + 1).Select(i =>
            {
                var radians = (arc.StartAngle + sweep * i / steps) * Math.PI / 180;
                return (center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
            }).ToArray();
            AppendGraphic(b, new EdaSymbolGraphic("POLYLINE", points, .1524, false));
        }
        foreach (var curve in symbol.Beziers.Where(curve => curve.ControlPoints.Count >= 2))
        {
            var controls = curve.ControlPoints.Select(Point).ToArray();
            IReadOnlyList<(double X, double Y)> points = controls;
            if (controls.Length == 4)
                points = Enumerable.Range(0, 17).Select(i =>
                {
                    var t = i / 16.0; var u = 1 - t;
                    return (u * u * u * controls[0].X + 3 * u * u * t * controls[1].X +
                            3 * u * t * t * controls[2].X + t * t * t * controls[3].X,
                            u * u * u * controls[0].Y + 3 * u * u * t * controls[1].Y +
                            3 * u * t * t * controls[2].Y + t * t * t * controls[3].Y);
                }).ToArray();
            AppendGraphic(b, new EdaSymbolGraphic("POLYLINE", points, .1524, false));
        }
        foreach (var rounded in symbol.RoundedRectangles)
            AppendGraphic(b, new EdaSymbolGraphic("RECT", [Point(rounded.Corner1), Point(rounded.Corner2)], .1524, rounded.IsFilled));
        foreach (var pin in symbol.Pins)
            AppendPin(b, pin.Designator ?? "", pin.Name ?? "", MapAltiumElectrical(pin.ElectricalType.ToString()),
                pin.Location.X.ToMm(), pin.Location.Y.ToMm(), (int)pin.Orientation * 90, pin.Length.ToMm());
        b.AppendLine("    )");
    }

    private static void AppendGraphic(StringBuilder b, EdaSymbolGraphic graphic)
    {
        if (graphic.PointsMm.Count < 2) return;
        var p = graphic.PointsMm;
        var fill = graphic.Filled ? "background" : "none";
        var stroke = $"(stroke (width {N(Math.Max(.01, graphic.StrokeMm))}) (type default)) (fill (type {fill}))";
        if (graphic.Kind == "RECT")
            b.AppendLine($"      (rectangle (start {N(p[0].X)} {N(p[0].Y)}) (end {N(p[1].X)} {N(p[1].Y)}) {stroke})");
        else if (graphic.Kind == "ELLIPSE" && Math.Abs(p[1].X - p[1].Y) < .001)
            b.AppendLine($"      (circle (center {N(p[0].X)} {N(p[0].Y)}) (radius {N(Math.Abs(p[1].X))}) {stroke})");
        else if (graphic.Kind == "ELLIPSE")
        {
            var points = Enumerable.Range(0, 25).Select(i => (p[0].X + p[1].X * Math.Cos(i * Math.PI / 12),
                p[0].Y + p[1].Y * Math.Sin(i * Math.PI / 12))).ToArray();
            AppendGraphic(b, new EdaSymbolGraphic("POLYGON", points, graphic.StrokeMm, graphic.Filled));
        }
        else
        {
            b.Append("      (polyline (pts");
            foreach (var point in p) b.Append($" (xy {N(point.X)} {N(point.Y)})");
            if (graphic.Kind == "POLYGON" && p[0] != p[^1]) b.Append($" (xy {N(p[0].X)} {N(p[0].Y)})");
            b.AppendLine($") {stroke})");
        }
    }

    private static void AppendPin(StringBuilder b, string number, string name, string electrical,
        double x, double y, int angle, double length)
    {
        if (string.Equals(name, number, StringComparison.Ordinal)) name = "";
        b.AppendLine($"      (pin {electrical} line (at {N(x)} {N(y)} {angle}) (length {N(Math.Max(.01, length))})");
        b.AppendLine($"        (name \"{Escape(name)}\" (effects (font (size 1.27 1.27))))");
        b.AppendLine($"        (number \"{Escape(number)}\" (effects (font (size 1.27 1.27)))))");
    }

    private static void Property(StringBuilder b, string key, string value, double x, double y, bool hidden = false) =>
        b.AppendLine($"    (property \"{Escape(key)}\" \"{Escape(value)}\" (at {N(x)} {N(y)} 0) (effects (font (size 1.27 1.27)){(hidden ? " (hide yes)" : "")}))");

    private static string MapEasyEdaElectrical(int value) => value switch
    {
        1 => "bidirectional", 2 => "output", 3 => "power_in", 4 => "input", _ => "passive"
    };
    private static string MapAltiumElectrical(string value) => value switch
    {
        "Input" => "input", "Output" => "output", "InputOutput" => "bidirectional",
        "Power" or "PowerInput" => "power_in", "OpenCollector" => "open_collector",
        "OpenEmitter" => "open_emitter", "HiZ" => "tri_state", _ => "passive"
    };
    private static string MapLayer(string value) => value switch
    {
        "1" or "TopLayer" => "F.Cu", "2" or "BottomLayer" => "B.Cu",
        "3" or "TopSilkLayer" => "F.SilkS", "4" or "BottomSilkLayer" => "B.SilkS",
        "5" => "F.Paste", "6" => "B.Paste", "7" => "F.Mask", "8" => "B.Mask",
        "13" => "F.Fab", "14" => "B.Fab", "99" => "F.CrtYd", _ => "F.SilkS"
    };
    private static string N(double value) => Math.Round(value, 6).ToString("0.######", Invariant);
    private static string Escape(string? value) => (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

    // Replace only a direct child of the library root; parentheses inside quoted properties
    // do not affect the scan. Other users' symbols are kept byte-for-byte.
    private static string UpsertSymbol(string library, string name, string replacement)
    {
        var depth = 0; var quoted = false; var escaped = false; var start = -1; var foundStart = -1; var foundEnd = -1;
        var rootEnd = -1;
        for (var i = 0; i < library.Length; i++)
        {
            var c = library[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') { quoted = true; continue; }
            if (c == '(')
            {
                if (depth == 1) start = i;
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth < 0) throw new InvalidDataException("Existing KiCad symbol library is malformed.");
                if (depth == 1 && start >= 0)
                {
                    var header = library.AsSpan(start, Math.Min(i - start + 1, name.Length + 20));
                    if (header.StartsWith($"(symbol \"{Escape(name)}\"", StringComparison.Ordinal))
                    { foundStart = start; foundEnd = i + 1; }
                    start = -1;
                }
                if (depth == 0) rootEnd = i;
            }
        }
        if (depth != 0 || quoted || rootEnd < 0) throw new InvalidDataException("Existing KiCad symbol library is malformed.");
        return foundStart >= 0
            ? library[..foundStart] + replacement + library[foundEnd..]
            : library.Insert(rootEnd, replacement);
    }
}
