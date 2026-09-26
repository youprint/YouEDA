using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;
using EasyEdaAltiumGrabber.Controls;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Sch;

internal static class TransistorSymbolTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("FAILED: " + message); Console.WriteLine("PASS: " + message); }

    public static async Task RunAsync(string output, string catalog)
    {
        var resolver = new UserSymbolLibraryResolver();
        foreach (var (code, mpn, kind, labels) in new[]
        {
            ("C10487", "SI2301CDS-T1-GE3", "PMOS", "GSD"),
            ("C15127", "AO3401A", "PMOS", "GSD"),
            ("C20917", "AO3400A", "NMOS", "GSD"),
            ("C8545", "2N7002", "NMOS", "GSD"),
            ("C105432", "S8550(RANGE:120-200)", "PNP", "123"),
            ("C9634", "D882(RANGE:160-320)", "NPN", "BCE"),
            ("C8326", "MMBT5401(RANGE:200-300)", "PNP", "BEC"),
            ("C8542", "SS8550 Y2(RANGE:200-350)", "PNP", "BEC"),
            ("C8543", "S9012 2T1(RANGE:200-350)", "PNP", "BEC")
        })
        {
            var part = new EdaComponent { LcscPartNumber = code, Name = mpn };
            part.Properties["Manufacturer Part"] = mpn;
            part.Tags.Add(kind is "NMOS" or "PMOS" ? "MOSFETs" : "Bipolar Transistors - BJT");
            for (int i = 0; i < 3; i++) part.SymbolPins.Add(new((i + 1).ToString(), labels[i].ToString(), 0, 0, ""));
            var match = resolver.Resolve(part, catalog, null);
            Check(match?.TransistorPins?.Kind == kind, $"{code}: verified {kind}, not a rectangular fallback or opposite polarity");
            var symbol = resolver.LoadSelectedComponent(match!);
            Check(symbol.Rectangles.Count == 0 && symbol.Lines.Count + symbol.Polylines.Count > 0 && symbol.Polygons.Count > 0 &&
                UserSymbolLibraryResolver.CheckPinCompatibility(part, symbol, true).Compatible,
                $"{code}: transistor artwork and all source terminal functions preserved");
            Check(symbol.Lines.All(l => Math.Abs(l.Width.ToMils() - 2) < .001) &&
                symbol.Polylines.All(l => Math.Abs(l.LineWidth.ToMils() - 2) < .001) &&
                symbol.Arcs.All(l => Math.Abs(l.LineWidth.ToMils() - 2) < .001), $"{code}: Small line width");
            Check(symbol.Pins.Single(p => p.Name == (kind is "NMOS" or "PMOS" ? "G" : "B")).Designator == "1",
                $"{code}: control pin matches datasheet");
            if (kind is "PNP" or "NPN")
            {
                var tip = symbol.Polygons.Single().Vertices[0];
                Check(tip.X.ToMils() == (kind == "PNP" ? 0 : 100), $"{code}: emitter arrow points {(kind == "PNP" ? "inward" : "outward")}");
                Check(symbol.Pins.Single(p => p.Name == "E").Designator == (code == "C9634" ? "3" : "2"),
                    $"{code}: emitter numbering is not assumed from SOT package");
            }
            else
            {
                var channel = symbol.Polygons[0].Vertices;
                var bodyDiode = symbol.Polygons[1].Vertices;
                Check(channel[0].X.ToMils() == (kind == "NMOS" ? 20 : 80) &&
                    bodyDiode[2].Y.ToMils() == (kind == "NMOS" ? 40 : -40), $"{code}: native channel arrow and body diode match polarity");
            }
            var library = (SchLibrary)AltiumLibrary.CreateSchLib(); library.Add(symbol);
            var path = Path.Combine(output, code + "-transistor.SchLib"); await library.SaveAsync(path);
            var reopened = ((SchLibrary)await AltiumLibrary.OpenSchLibAsync(path)).Components.OfType<SchComponent>().Single();
            Check(UserSymbolLibraryResolver.CheckPinCompatibility(part, reopened, true).Compatible &&
                reopened.Polygons.SelectMany(p => p.Vertices).SequenceEqual(symbol.Polygons.SelectMany(p => p.Vertices)) &&
                reopened.Lines.All(l => Math.Abs(l.Width.ToMils() - 2) < .001), $"{code}: mapping, arrows and Small width survive SchLib roundtrip");
            await File.WriteAllBytesAsync(Path.Combine(output, code + "-transistor.png"), SchematicSymbolPreviewRenderer.RenderPng(reopened, 600, 400));
            var kicad = await new KiCadLibraryExporter().UpsertAsync(part, Path.Combine(output, "transistor-kicad"), reopened, null,
                CancellationToken.None, includeFootprint: false);
            Check((await File.ReadAllTextAsync(kicad)).Contains("(width 0.0508)"), $"{code}: KiCad keeps Small stroke");
            // Known electrical conflicts cannot be hidden by a recognised part name.
            part.SymbolPins[0] = part.SymbolPins[0] with { Name = "E" };
            Check(resolver.Resolve(part, catalog, null) is null, $"{code}: contradictory source function rejects automatic mapping");
        }
        var template = resolver.LoadPreviewComponent(new(catalog, "test", "DIODES_INC_NPN_SOT-23-3"));
        Check(template.Polygons.Single().Vertices[0].X.ToMils() == 100 && template.Pins.Single(p => p.Name == "E").Designator == "2",
            "PNP/renumbered BJT outputs never mutate cached native NPN");
        var generic = new EdaComponent { LcscPartNumber = "C987654321", Name = "Unreviewed transistor", Description = "PNP transistor" };
        generic.SymbolPins.Add(new("A", "Base", 0, 0, "")); generic.SymbolPins.Add(new("B", "Collector", 0, 0, "")); generic.SymbolPins.Add(new("C", "Emitter", 0, 0, ""));
        Check(resolver.Resolve(generic, catalog, null)?.TransistorPins is { Kind: "PNP", Control: "A", Output: "B", Common: "C" },
            "explicit polarity plus functional terminals allows arbitrary pin numbers");
        generic.Description = "Bipolar transistor";
        Check(resolver.Resolve(generic, catalog, null) is null, "unknown BJT polarity cannot fall through to an NPN template");
        generic.Description = "NPN PNP transistor";
        Check(resolver.Resolve(generic, catalog, null) is null, "conflicting polarity rejected");
        generic.Description = "NPN Darlington transistor";
        Check(resolver.Resolve(generic, catalog, null) is null, "Darlington not flattened into single BJT");
        generic.Description = "PNP transistor"; generic.SymbolPins[0] = generic.SymbolPins[0] with { Name = "1" };
        Check(resolver.Resolve(generic, catalog, null) is null, "numeric unknown transistor does not borrow S8550's mapping");
        var impostor = new EdaComponent { LcscPartNumber = "C105432", Name = "S8550A-UNVERIFIED", Description = "PNP transistor" };
        impostor.Properties["Manufacturer Part"] = "S8550A-UNVERIFIED";
        foreach (var pin in generic.SymbolPins) impostor.SymbolPins.Add(pin);
        Check(resolver.Resolve(impostor, catalog, null) is null, "similar MPN does not match exact supplier profile");
        var donorFolder = Path.Combine(output, "transistor-donor"); Directory.CreateDirectory(donorFolder);
        var donorPath = Path.Combine(donorFolder, "BundledUserSymbols.SchLib");
        var donor = resolver.LoadSelectedComponent(new(catalog, "test", "DIODES_INC_NPN_SOT-23-3"));
        donor.AddParameter(new SchParameter { Name = "Donor Rating", Value = "Must not survive write/read" });
        var donorLib = (SchLibrary)AltiumLibrary.CreateSchLib(); donorLib.Add(donor); await donorLib.SaveAsync(donorPath);
        var npn = new EdaComponent { LcscPartNumber = "C777777", Name = "Native NPN test", Description = "NPN transistor" };
        npn.SymbolPins.Add(new("1", "B", 0, 0, "")); npn.SymbolPins.Add(new("2", "E", 0, 0, "")); npn.SymbolPins.Add(new("3", "C", 0, 0, ""));
        var cleaned = resolver.LoadSelectedComponent(resolver.Resolve(npn, donorPath, null)!);
        var cleanLib = (SchLibrary)AltiumLibrary.CreateSchLib(); cleanLib.Add(cleaned);
        var cleanedPath = Path.Combine(output, "clean-transistor.SchLib"); await cleanLib.SaveAsync(cleanedPath);
        Check(!((SchLibrary)await AltiumLibrary.OpenSchLibAsync(cleanedPath)).Components.OfType<SchComponent>().Single().Parameters.Any(p => p.Name == "Donor Rating"),
            "removed donor parameters cannot reappear from native read-order records during save");
    }
}
