using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;
using EasyEdaAltiumGrabber.Controls;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Sch;
using OriginalCircuit.Eda.Primitives;
using System.Globalization;
using System.Text.RegularExpressions;

internal static class DiscreteNetworkSymbolTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("FAILED: " + message); Console.WriteLine("PASS: " + message); }

    public static async Task RunAsync(string output, string catalog)
    {
        var resolver = new UserSymbolLibraryResolver();
        foreach (var (code, mpn, names, kind, triangles) in new[]
        {
            ("C2488", "MB10S-50MIL", "+,-,C2,C1", "bridge", 4),
            ("C2500", "BAV99,215", "1,2,3", "series diodes", 2),
            ("C68978", "BAV70", "1,2,3", "common-cathode diodes", 2),
            ("C78395", "P6SMB6.8CA/TR13", "1,2", "bidirectional TVS", 2),
            ("C7420377", "SMBJ6.5CA", "1,2", "bidirectional TVS", 2),
            ("C32677", "PSM712-LF-T7", "A1,A1,K", "asymmetric TVS array", 4),
            ("C318884", "TS-1187A-B-A-B", "A,B,C,D", "four-pin normally-open switch", 0)
        })
        {
            var part = new EdaComponent { LcscPartNumber = code, Name = mpn };
            part.Properties["Manufacturer Part"] = mpn;
            foreach (var (name, i) in names.Split(',').Select((name, i) => (name, i)))
                part.SymbolPins.Add(new((i + 1).ToString(), name, 0, 0, ""));
            var match = resolver.Resolve(part, catalog, null);
            Check(match?.Network?.Kind == kind, $"{code}: exact verified network, not rectangular fallback");
            var symbol = resolver.LoadSelectedComponent(match!);
            Check(symbol.Rectangles.Count == 0 && symbol.Polygons.Count == triangles &&
                symbol.Pins.Count == part.SymbolPins.Count && UserSymbolLibraryResolver.CheckPinCompatibility(part, symbol, true).Compatible,
                $"{code}: correct diode count and all numbered roles preserved");
            Check(symbol.Polylines.All(l => Math.Abs(l.LineWidth.ToMils() - 2) < .001) &&
                symbol.Pins.OfType<SchPin>().All(p => p.ElectricalType == PinElectricalType.Passive && p.SymbolOuterEdge == 0),
                $"{code}: Small strokes and passive pins without misleading I/O triangles");
            CheckTopology(code, symbol);
            if (code is "C2488" or "C32677")
            {
                Check(symbol.Pins.All(p => Math.Abs(p.Location.X.ToMils() / 50 - Math.Round(p.Location.X.ToMils() / 50)) < .00001 &&
                    Math.Abs(p.Location.Y.ToMils() / 50 - Math.Round(p.Location.Y.ToMils() / 50)) < .00001 && p.Length.ToMils() == 100),
                    $"{code}: authored pin roots and 100-mil tips are on the 50-mil grid");
                Check(symbol.Pins.OrderBy(p => p.Designator).Select(p => p.Name).SequenceEqual(
                    code == "C2488" ? new[] { "+", "-", "AC2", "AC1" } : new[] { "IO1", "IO2", "GND" }),
                    $"{code}: verified functional names retained, no raw A1/A1/K labels");
            }
            AltiumSchExporter.PrepareSymbol(symbol, part, false);
            Check(symbol.Parameters.Single(p => p.Name == "Designator").IsVisible &&
                symbol.Parameters.Single(p => p.Name == "Comment").Location.Y < symbol.Pins.Min(p => p.Location.Y),
                $"{code}: prepared symbol keeps designator and comment clear of circuit artwork");
            var lib = (SchLibrary)AltiumLibrary.CreateSchLib(); lib.Add(symbol);
            var path = Path.Combine(output, code + "-network.SchLib"); await lib.SaveAsync(path);
            var reopened = ((SchLibrary)await AltiumLibrary.OpenSchLibAsync(path)).Components.OfType<SchComponent>().Single();
            Check(UserSymbolLibraryResolver.CheckPinCompatibility(part, reopened, true).Compatible &&
                reopened.Polygons.SelectMany(p => p.Vertices).SequenceEqual(symbol.Polygons.SelectMany(p => p.Vertices)) &&
                reopened.Polylines.SelectMany(p => p.Vertices).SequenceEqual(symbol.Polylines.SelectMany(p => p.Vertices)) &&
                reopened.Polylines.All(l => Math.Abs(l.LineWidth.ToMils() - 2) < .001), $"{code}: geometry, roles and Small style survive SchLib roundtrip");
            CheckTopology(code, reopened);
            await File.WriteAllBytesAsync(Path.Combine(output, code + "-network.png"), SchematicSymbolPreviewRenderer.RenderPng(reopened, 800, 500));
            var kicad = await new KiCadLibraryExporter().UpsertAsync(part, Path.Combine(output, "network-kicad"), reopened, null,
                CancellationToken.None, includeFootprint: false);
            Check((await File.ReadAllTextAsync(kicad)).Contains("(width 0.0508)"), $"{code}: KiCad retains Small artwork");
            if (code is "C2488" or "C32677")
            {
                var isolated = await new KiCadLibraryExporter().UpsertAsync(part, Path.Combine(output, code + "-grid"), reopened, null,
                    CancellationToken.None, includeFootprint: false);
                var pinMatches = Regex.Matches(await File.ReadAllTextAsync(isolated),
                    @"\(pin passive line\s+\(at ([-\d.]+) ([-\d.]+) ([-\d.]+)\)");
                Check(pinMatches.Count == part.SymbolPins.Count && pinMatches.All(m => new[] { m.Groups[1].Value, m.Groups[2].Value }
                    .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).All(v => Math.Abs(v / 1.27 - Math.Round(v / 1.27)) < .00001)),
                    $"{code}: serialized KiCad passive pin tips remain on grid after SchLib roundtrip");
                var exported = await File.ReadAllTextAsync(isolated);
                foreach (var label in code == "C2488" ? new[] { "+", "-", "~" } : new[] { "12V", "7V" })
                    Check(exported.Contains($"(text \"{label}\""), $"{code}: KiCad preserves visible {label} annotation");
            }
            part.Properties["Manufacturer Part"] = mpn + "-UNREVIEWED";
            Check(resolver.Resolve(part, catalog, null) is null, $"{code}: similar part name cannot borrow the profile");
            part.Properties["Manufacturer Part"] = mpn;
            var pin = part.SymbolPins[0]; part.SymbolPins[0] = pin with { Name = "CONFLICT" };
            Check(resolver.Resolve(part, catalog, null) is null, $"{code}: contradictory terminal name requires review");
            part.SymbolPins[0] = pin with { Number = "2" };
            Check(resolver.Resolve(part, catalog, null) is null, $"{code}: duplicated/missing terminal rejected");
        }
        var switchTemplate = resolver.LoadPreviewComponent(new(catalog, "test", "SPST-NO_4_PIN"));
        Check(switchTemplate.Pins.All(p => p.Location.Y.ToMils() == 0), "switch output separation never mutates cached native source");
        var template = resolver.LoadPreviewComponent(new(catalog, "test", "Diode"));
        Check(template.Pins.Count == 2 && template.Polygons.Count == 1 && template.Polylines.Count == 1,
            "network composition never mutates native single diode");
    }

    private static void CheckTopology(string code, SchComponent s)
    {
        var segments = s.Polylines.SelectMany(l => l.Vertices.Zip(l.Vertices.Skip(1), (a, b) => (a, b))).ToArray();
        static bool On(CoordPoint p, CoordPoint a, CoordPoint b)
        {
            var x = p.X.ToMils(); var y = p.Y.ToMils(); var ax = a.X.ToMils(); var ay = a.Y.ToMils();
            var bx = b.X.ToMils(); var by = b.Y.ToMils();
            return Math.Abs((x - ax) * (by - ay) - (y - ay) * (bx - ax)) < .01 &&
                x >= Math.Min(ax, bx) - .001 && x <= Math.Max(ax, bx) + .001 && y >= Math.Min(ay, by) - .001 && y <= Math.Max(ay, by) + .001;
        }
        bool Connected(CoordPoint start, CoordPoint end)
        {
            var points = segments.SelectMany(l => new[] { l.a, l.b }).Append(start).Append(end).Distinct().ToArray();
            var visited = new HashSet<CoordPoint> { start }; var queue = new Queue<CoordPoint>(); queue.Enqueue(start);
            while (queue.TryDequeue(out var point))
                foreach (var segment in segments.Where(l => On(point, l.a, l.b)))
                    foreach (var next in points.Where(p => On(p, segment.a, segment.b)))
                        if (visited.Add(next)) queue.Enqueue(next);
            return visited.Contains(end);
        }
        string Net(CoordPoint point) => string.Join(',', s.Pins.Where(p => Connected(point, p.Location)).Select(p => p.Designator).Order());
        CoordPoint Anode(int i) => new((s.Polygons[i].Vertices[1].X + s.Polygons[i].Vertices[2].X) / 2,
            (s.Polygons[i].Vertices[1].Y + s.Polygons[i].Vertices[2].Y) / 2);
        var edges = s.Polygons.Select((p, i) => Net(Anode(i)) + ">" + Net(p.Vertices[0])).Order().ToArray();
        if (code == "C2488") Check(edges.SequenceEqual(new[] { "2>3", "2>4", "3>1", "4>1" }), "bridge diode directions and plus/minus/AC connections verified");
        if (code == "C2500") Check(edges.SequenceEqual(new[] { "1>3", "3>2" }), "BAV99 series junction is pin 3, not common cathode");
        if (code == "C32677")
        {
            Check(edges.SequenceEqual(new[] { ">1", ">2", ">3", ">3" }) && Connected(Anode(0), Anode(1)) &&
                Connected(Anode(2), Anode(3)) && !Connected(Anode(0), Anode(2)), "PSM712 has two independent anti-series pairs returning to ground 3");
            Check(s.Labels.Select(l => l.Text).Order().SequenceEqual(new[] { "12V", "12V", "7V", "7V" }), "PSM712 asymmetric 12V/7V branches retained");
        }
        if (code == "C318884")
        {
            var p = s.Pins.ToDictionary(p => p.Designator!, p => p.Location);
            Check(Connected(p["1"], p["2"]) && Connected(p["3"], p["4"]) && !Connected(p["1"], p["3"]) && p.Values.Distinct().Count() == 4,
                "switch has separate visible pins, permanent A-B/C-D pairs and normally-open contacts");
        }
        if (code == "C68978") Check(s.Polygons.All(p => p.Vertices[0].X.ToMils() == 20) &&
            Net(s.Polygons[0].Vertices[0]) == "3" && Net(s.Polygons[1].Vertices[0]) == "3", "BAV70 both native cathodes connect to pin 3");
        if (code is "C78395" or "C7420377") Check(s.Polygons[0].Vertices[1].X.ToMils() == -80 &&
            s.Polygons[1].Vertices[1].X.ToMils() == 80, "bidirectional TVS retains opposed native diodes");
    }
}
