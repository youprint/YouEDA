using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using EasyEdaAltiumGrabber.Models;
using OriginalCircuit.Altium.Models.Sch;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Writes native KiCad libraries with installed-location model paths.</summary>
public sealed class KiCadLibraryExporter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public async Task<string> UpsertAsync(EdaComponent source, string outputDirectory,
        SchComponent? matchedUserSymbol = null, Downloaded3dModel? model = null, CancellationToken ct = default,
        bool includeFootprint = true)
    {
        Directory.CreateDirectory(outputDirectory);
        var footprintDirectory = Path.Combine(outputDirectory, "youeda.pretty");
        var modelDirectory = Path.Combine(outputDirectory, "youeda.3dshapes");
        if (includeFootprint) Directory.CreateDirectory(footprintDirectory);
        var modelFile = includeFootprint && model is not null ? source.LcscPartNumber + ".step" : null;
        var footprintPath = Path.Combine(footprintDirectory, source.LcscPartNumber + ".kicad_mod");
        var symbolPath = Path.Combine(outputDirectory, "youeda.kicad_sym");
        var footprintText = includeFootprint ? BuildFootprint(source, model,
            modelFile is null ? null : Path.GetFullPath(Path.Combine(modelDirectory, modelFile)).Replace('\\', '/')) : null;
        var newSymbol = BuildSymbol(source, matchedUserSymbol, includeFootprint);
        var existing = File.Exists(symbolPath) ? await File.ReadAllTextAsync(symbolPath, ct) :
            "(kicad_symbol_lib (version 20211014) (generator kicad_symbol_editor)\n)\n";
        var updatedSymbolLibrary = UpsertSymbol(existing, source.LcscPartNumber, newSymbol);
        if (modelFile is not null)
        {
            Directory.CreateDirectory(modelDirectory);
            File.Copy(model!.Path, Path.Combine(modelDirectory, modelFile), overwrite: true);
        }
        if (footprintText is not null) await AtomicWriteAsync(footprintPath, footprintText, ct);
        await AtomicWriteAsync(symbolPath, updatedSymbolLibrary, ct);
        await WriteTableIfMissingAsync(Path.Combine(outputDirectory, "sym-lib-table"),
            $"(sym_lib_table\n  (lib (name \"youeda\")(type \"KiCad\")(uri \"{Escape(Path.GetFullPath(symbolPath).Replace('\\', '/'))}\")(options \"\")(descr \"YouEDA symbols\"))\n)\n", ct);
        if (includeFootprint)
            await WriteTableIfMissingAsync(Path.Combine(outputDirectory, "fp-lib-table"),
                $"(fp_lib_table\n  (lib (name \"youeda\")(type \"KiCad\")(uri \"{Escape(Path.GetFullPath(footprintDirectory).Replace('\\', '/'))}\")(options \"\")(descr \"YouEDA footprints\"))\n)\n", ct);
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
        var bounds = FootprintBounds(source);
        var labelX = (bounds.MinX + bounds.MaxX) / 2;
        builder.AppendLine($"  (fp_text reference \"REF**\" (at {N(labelX)} {N(bounds.MinY - 1)}) (layer \"F.SilkS\") (effects (font (size 1 1) (thickness 0.15))))");
        builder.AppendLine($"  (fp_text value \"{name}\" (at {N(labelX)} {N(bounds.MaxY + 1)}) (layer \"F.Fab\") (effects (font (size 1 1) (thickness 0.15))))");
        foreach (var pad in source.Pads)
        {
            var type = pad.HoleMm > 0 ? pad.Plated ? "thru_hole" : "np_thru_hole" : "smd";
            var layer = pad.Layer is "2" or "BottomLayer" ? "B" : "F";
            var layers = type == "smd" ? $"\"{layer}.Cu\" \"{layer}.Paste\" \"{layer}.Mask\"" : "\"*.Cu\" \"*.Mask\"";
            var shape = pad.Shape switch { "OVAL" or "ELLIPSE" => "oval", "CIRCLE" => "circle", _ => "rect" };
            if (pad.Shape == "POLYGON" && pad.PolygonPointsMm.Count > 2 && type == "smd") shape = "custom";
            // Both EasyEDA and KiCad store pad orientation counter-clockwise;
            // reflecting the position's Y does not negate this angle convention.
            builder.Append($"  (pad \"{Escape(pad.Number)}\" {type} {shape} (at {N(pad.Xmm)} {N(-pad.Ymm)} {N(shape == "custom" ? 0 : pad.RotationDeg)})");
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
        foreach (var arc in source.Shapes.Where(item => item.Kind == "ARC" && item.PointsMm.Count > 0 && item.RadiusMm > 0))
        {
            var sweep = (arc.EndAngleDeg - arc.StartAngleDeg + 360) % 360;
            if (sweep == 0) sweep = 360;
            (double X, double Y) At(double angle) =>
                (arc.PointsMm[0].X + arc.RadiusMm * Math.Cos(angle * Math.PI / 180),
                 -(arc.PointsMm[0].Y + arc.RadiusMm * Math.Sin(angle * Math.PI / 180)));
            // Split full circles to avoid ambiguous coincident start/end points.
            var segments = sweep >= 359.999 ? 2 : 1;
            for (var i = 0; i < segments; i++)
            {
                var a = At(arc.StartAngleDeg + sweep * i / segments);
                var m = At(arc.StartAngleDeg + sweep * (i + .5) / segments);
                var z = At(arc.StartAngleDeg + sweep * (i + 1) / segments);
                builder.AppendLine($"  (fp_arc (start {N(a.X)} {N(a.Y)}) (mid {N(m.X)} {N(m.Y)}) (end {N(z.X)} {N(z.Y)}) (stroke (width {N(Math.Max(.01, arc.StrokeMm))}) (type solid)) (layer \"{MapLayer(arc.Layer)}\"))");
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
                    builder.AppendLine($"  (fp_line (start {N(a.X)} {N(-a.Y)}) (end {N(z.X)} {N(-z.Y)}) (stroke (width 0.05) (type solid)) (layer \"F.Fab\"))");
                }
            }
            else if (region.Layer is "3" or "4" or "13" or "14")
            {
                builder.Append("  (fp_poly (pts");
                foreach (var point in region.PointsMm) builder.Append($" (xy {N(point.X)} {N(-point.Y)})");
                builder.AppendLine($") (stroke (width 0) (type solid)) (fill solid) (layer \"{layer}\"))");
            }
        }
        // EasyEDA layer 99 is the physical body outline, not a placement courtyard.
        // Conservative envelope includes rotated/custom pads, holes and all body geometry.
        double Down(double v) => Math.Floor((v - .25) / .05) * .05;
        double Up(double v) => Math.Ceiling((v + .25) / .05) * .05;
        builder.AppendLine($"  (fp_rect (start {N(Down(bounds.MinX))} {N(Down(bounds.MinY))}) (end {N(Up(bounds.MaxX))} {N(Up(bounds.MaxY))}) (stroke (width 0.05) (type solid)) (fill none) (layer \"F.CrtYd\"))");
        if (model is not null && modelFile is not null)
        {
            // Models belong to the installed library, not the consuming project's directory.
            // Regenerate/relink after moving the library to another location.
            // 3D offsets use right-handed Y-up coordinates (unlike PCB's 2D Y-down).
            // KiCad applies negative stored Euler rotations: negate EasyEDA's angles.
            builder.AppendLine($"  (model \"{Escape(modelFile)}\" (offset (xyz {N(model.Source.Xmm)} {N(model.Source.Ymm)} {N(model.Source.Zmm + model.ZOffsetMm)})) (scale (xyz 1 1 1)) (rotate (xyz {N(-model.Source.RotationXDeg)} {N(-model.Source.RotationYDeg)} {N(-model.Source.RotationZDeg)})))");
        }
        builder.AppendLine(")");
        return builder.ToString();
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) FootprintBounds(EdaComponent source)
    {
        var points = new List<(double X, double Y)>();
        void Box(double x, double y, double rx, double ry)
        { points.Add((x - rx, -y - ry)); points.Add((x + rx, -y + ry)); }
        foreach (var pad in source.Pads)
        {
            if (pad.Shape == "POLYGON" && pad.PolygonPointsMm.Count > 2)
                points.AddRange(pad.PolygonPointsMm.Select(p => (p.X, -p.Y)));
            else
            {
                var a = pad.RotationDeg * Math.PI / 180;
                Box(pad.Xmm, pad.Ymm, (Math.Abs(Math.Cos(a)) * pad.WidthMm + Math.Abs(Math.Sin(a)) * pad.HeightMm) / 2,
                    (Math.Abs(Math.Sin(a)) * pad.WidthMm + Math.Abs(Math.Cos(a)) * pad.HeightMm) / 2);
            }
        }
        foreach (var shape in source.Shapes.Where(s => s.PointsMm.Count > 0 && s.Layer is not "12" and not "15" and not "101"))
        {
            if (shape.Kind is "CIRCLE" or "HOLE" && shape.PointsMm.Count > 1)
                Box(shape.PointsMm[0].X, shape.PointsMm[0].Y, Math.Abs(shape.PointsMm[1].X) + shape.StrokeMm / 2,
                    Math.Abs(shape.PointsMm[1].X) + shape.StrokeMm / 2);
            else if (shape.Kind == "ARC")
                Box(shape.PointsMm[0].X, shape.PointsMm[0].Y, shape.RadiusMm + shape.StrokeMm / 2, shape.RadiusMm + shape.StrokeMm / 2);
            else foreach (var p in shape.PointsMm) Box(p.X, p.Y, shape.StrokeMm / 2, shape.StrokeMm / 2);
        }
        if (points.Count == 0) return (-1, -1, 1, 1);
        return (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }

    private static string BuildSymbol(EdaComponent source, SchComponent? matched, bool includeFootprint)
    {
        var b = new StringBuilder();
        var name = Escape(source.LcscPartNumber);
        var sourcePrefix = source.Properties.GetValueOrDefault("pre");
        var reference = (string.IsNullOrWhiteSpace(sourcePrefix) ? matched?.DesignatorPrefix ?? "U" : sourcePrefix).TrimEnd('?');
        if (matched is not null && matched.Pins.Count > 0)
            AppendAltiumUnits(b, name, matched,
                DiscreteNetworkSymbolPolicy.Resolve(source)?.Kind is "bridge" or "asymmetric TVS array");
        else if (source.SymbolUnits.Count > 0)
        {
            for (var i = 0; i < source.SymbolUnits.Count; i++)
            {
                var unit = source.SymbolUnits[i];
                var shift = GridTranslation(unit.Pins.Select(p => (p.Xmm, p.Ymm)), source.LcscPartNumber, i + 1);
                b.AppendLine($"    (symbol \"{name}_{i + 1}_1\"");
                foreach (var graphic in unit.Graphics)
                    AppendGraphic(b, graphic with { PointsMm = graphic.PointsMm.Select((p, index) =>
                        graphic.Kind == "ELLIPSE" && index > 0 ? p : (p.X + shift.X, p.Y + shift.Y)).ToArray() });
                foreach (var pin in unit.Pins)
                    AppendPin(b, pin.Number, pin.Name, MapEasyEdaElectrical(pin.ElectricalType),
                        pin.Xmm + shift.X, pin.Ymm + shift.Y, (180 + pin.RotationDeg + 360) % 360, pin.LengthMm);
                b.AppendLine("    )");
            }
        }
        else throw new InvalidDataException($"{source.LcscPartNumber} has no schematic symbol data.");
        // Calculate label extents after conversion, including wire tips and every unit.
        var coordinates = Regex.Matches(b.ToString(), @"\((?:xy|start|end|center|at) (-?[\d.]+) (-?[\d.]+)")
            .Select(m => (X: double.Parse(m.Groups[1].Value, Invariant), Y: double.Parse(m.Groups[2].Value, Invariant))).ToArray();
        var top = coordinates.Length == 0 ? 2.54 : coordinates.Max(p => p.Y);
        var bottom = coordinates.Length == 0 ? -2.54 : coordinates.Min(p => p.Y);
        var header = new StringBuilder();
        header.AppendLine($"  (symbol \"{name}\" (in_bom yes) (on_board yes)");
        // Native discrete templates deliberately hide names such as A/K or OSC1/OSC2.
        // Keep those electrical names in the file, without drawing them over the artwork.
        var hidePinNames = matched is not null && matched.Pins.Count > 0
            ? matched.Pins.All(p => p is SchPin { ShowName: false })
            : source.SymbolUnits.SelectMany(u => u.Pins).Any() && source.SymbolUnits.SelectMany(u => u.Pins).All(p => !p.ShowName);
        if (hidePinNames)
            header.AppendLine("    (pin_names hide)");
        Property(header, "Reference", reference, 0, top + 2.54);
        Property(header, "Value", source.Name, 0, bottom - 2.54);
        Property(header, "Footprint", includeFootprint ? "youeda:" + source.LcscPartNumber : "", 0, 0, hidden: true);
        Property(header, "Datasheet", source.Properties.FirstOrDefault(p => p.Key.Equals("Datasheet", StringComparison.OrdinalIgnoreCase)).Value ?? "", 0, 0, hidden: true);
        Property(header, "Description", source.Description, 0, 0, hidden: true);
        Property(header, "LCSC", source.LcscPartNumber, 0, 0, hidden: true);
        var reserved = new HashSet<string>(["Reference", "Value", "Footprint", "Datasheet", "Description", "LCSC"], StringComparer.OrdinalIgnoreCase);
        if (source.Properties.ContainsKey("Value")) reserved.Add("Component Value");
        foreach (var property in source.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (!reserved.Contains(property.Key)) Property(header, property.Key, property.Value, 0, 0, hidden: true);
        // Preserve the electrical value alongside the original MPN used as the KiCad Value.
        if (source.Properties.TryGetValue("Value", out var electricalValue))
            Property(header, "Component Value", electricalValue, 0, 0, hidden: true);
        b.Insert(0, header.ToString());
        b.AppendLine("  )");
        return b.ToString();
    }

    private static void AppendAltiumUnits(StringBuilder b, string name, SchComponent symbol, bool includeNetworkLabels)
    {
        var shift = GridTranslation(symbol.Pins.Select(pin =>
        {
            var angle = (int)pin.Orientation * Math.PI / 2;
            return (pin.Location.X.ToMm() + pin.Length.ToMm() * Math.Cos(angle),
                    pin.Location.Y.ToMm() + pin.Length.ToMm() * Math.Sin(angle));
        }), name, 1);
        // Native coordinates are decoded in physical units by the SchLib reader.
        const double graphicScale = 1;
        (double X, double Y) Point(OriginalCircuit.Eda.Primitives.CoordPoint point) =>
            (point.X.ToMm() * graphicScale + shift.X, point.Y.ToMm() * graphicScale + shift.Y);
        b.AppendLine($"    (symbol \"{name}_1_1\"");
        foreach (var rect in symbol.Rectangles)
            AppendGraphic(b, new EdaSymbolGraphic("RECT", [Point(rect.Corner1), Point(rect.Corner2)],
                rect.LineWidth.ToMm(), rect.IsFilled));
        foreach (var line in symbol.Lines)
            AppendGraphic(b, new EdaSymbolGraphic("POLYLINE", [Point(line.Start), Point(line.End)], line.Width.ToMm(), false));
        foreach (var line in symbol.Polylines.Where(line => line.Vertices.Count > 1))
            AppendGraphic(b, new EdaSymbolGraphic("POLYLINE", line.Vertices.Select(Point).ToArray(), line.LineWidth.ToMm(), false));
        foreach (var polygon in symbol.Polygons.Where(item => item.Vertices.Count > 2))
            AppendGraphic(b, new EdaSymbolGraphic("POLYGON", polygon.Vertices.Select(Point).ToArray(), polygon.LineWidth.ToMm(), polygon.IsFilled));
        foreach (var ellipse in symbol.Ellipses)
            AppendGraphic(b, new EdaSymbolGraphic("ELLIPSE", [Point(ellipse.Center),
                (ellipse.RadiusX.ToMm() * graphicScale, ellipse.RadiusY.ToMm() * graphicScale)], NativeWidth(ellipse is SchEllipse e ? e.LineWidth : 1), ellipse.IsFilled));
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
            AppendGraphic(b, new EdaSymbolGraphic("POLYLINE", points, arc.LineWidth.ToMm(), false));
        }
        foreach (var arc in symbol.EllipticalArcs)
        {
            var center = Point(arc.Center);
            var sweep = arc.EndAngle - arc.StartAngle;
            if (sweep <= 0) sweep += 360;
            var steps = Math.Max(4, (int)Math.Ceiling(sweep / 5));
            var points = Enumerable.Range(0, steps + 1).Select(i =>
            {
                var radians = (arc.StartAngle + sweep * i / steps) * Math.PI / 180;
                return (center.X + arc.PrimaryRadius.ToMm() * Math.Cos(radians),
                    center.Y + arc.SecondaryRadius.ToMm() * Math.Sin(radians));
            }).ToArray();
            AppendGraphic(b, new EdaSymbolGraphic("POLYLINE", points, arc.LineWidth.ToMm(), false));
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
            AppendGraphic(b, new EdaSymbolGraphic("POLYLINE", points, curve.LineWidth.ToMm(), false));
        }
        foreach (var rounded in symbol.RoundedRectangles)
            AppendGraphic(b, new EdaSymbolGraphic("RECT", [Point(rounded.Corner1), Point(rounded.Corner2)], NativeWidth(rounded.LineWidth), rounded.IsFilled));
        // These reviewed compositions carry polarity and voltage-branch annotations.
        // Keep pin names hidden and render the separate labels clear of the diode artwork.
        if (includeNetworkLabels)
            foreach (var label in symbol.Labels.Where(label => !string.IsNullOrWhiteSpace(label.Text)))
            {
                var at = Point(label.Location);
                b.AppendLine($"      (text \"{Escape(label.Text)}\" (at {N(at.X)} {N(at.Y)} 0) (effects (font (size 1.27 1.27)) (justify left bottom)))");
            }
        foreach (var pin in symbol.Pins)
        {
            // Altium stores the body root and an outward orientation; KiCad stores
            // the wire tip and an inward orientation. Preserve the entire segment.
            var outward = (int)pin.Orientation * 90;
            var radians = outward * Math.PI / 180;
            AppendPin(b, pin.Designator ?? "", pin.Name ?? "", MapAltiumElectrical(pin.ElectricalType.ToString()),
                pin.Location.X.ToMm() + pin.Length.ToMm() * Math.Cos(radians) + shift.X,
                pin.Location.Y.ToMm() + pin.Length.ToMm() * Math.Sin(radians) + shift.Y,
                (outward + 180) % 360, pin.Length.ToMm());
        }
        b.AppendLine("    )");
    }

    // A rigid translation only: never snap individual pins, stretch artwork, or mutate sources.
    private static (double X, double Y) GridTranslation(IEnumerable<(double X, double Y)> points, string part, int unit)
    {
        const double grid = 1.27, tolerance = .00001;
        var pins = points.ToArray();
        if (pins.Distinct().Count() < 2) return (0, 0);
        double Delta(double value) => Math.Round(value / grid) * grid - value;
        var dx = Delta(pins[0].X); var dy = Delta(pins[0].Y);
        if (pins.Any(p => Math.Abs(Delta(p.X + dx)) > tolerance || Math.Abs(Delta(p.Y + dy)) > tolerance))
        {
            ImportDiagnostics.Record("symbol.grid_review", new { part, unit, reason = "Pin spacing is not compatible with a rigid 50-mil grid translation" });
            return (0, 0);
        }
        if (Math.Abs(dx) < tolerance && Math.Abs(dy) < tolerance) return (0, 0);
        ImportDiagnostics.Record("symbol.grid_translation", new { part, unit, dx, dy });
        return (dx, dy);
    }

    private static void AppendGraphic(StringBuilder b, EdaSymbolGraphic graphic)
    {
        if (graphic.PointsMm.Count < 2) return;
        var p = graphic.PointsMm;
        var fill = graphic.Filled ? (graphic.Kind == "RECT" ? "background" : "outline") : "none";
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
        // EasyEDA's numeric types are not Altium's electrical-type enum.
        1 => "input", 2 => "output", 3 => "bidirectional", 4 => "power_in", _ => "unspecified"
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
        // Library/editor markings are documentation, not printed silkscreen.
        // In particular, layer-101 dots occur on non-polar resistor pad 1 too.
        "13" or "99" or "100" or "101" => "F.Fab", "14" => "B.Fab",
        "12" => "Cmts.User", "15" => "Dwgs.User", _ => "Cmts.User"
    };
    private static double NativeWidth(int style) => style switch { 0 => .0254, 1 => .0508, 2 => .1016, 3 => .1524, _ => .0508 };
    private static string N(double value) => (Math.Abs(value) < .0000005 ? 0 : Math.Round(value, 6)).ToString("0.######", Invariant);
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
