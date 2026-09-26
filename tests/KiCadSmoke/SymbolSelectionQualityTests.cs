using System.Text.Json;
using System.Text.RegularExpressions;
using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Sch;
using OriginalCircuit.Eda.Primitives;
using EasyEdaAltiumGrabber.Controls;

internal static class SymbolSelectionQualityTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("FAILED: " + message); Console.WriteLine("PASS: " + message); }
    private static EdaComponent Part(string name, params (string Number, string Name)[] pins)
    {
        var part = new EdaComponent { LcscPartNumber = "C123", Name = name };
        foreach (var pin in pins) part.SymbolPins.Add(new(pin.Number, pin.Name, 0, 0, ""));
        return part;
    }
    private static SchComponent Symbol(string name, params (string Number, string Name)[] pins)
    {
        var symbol = new SchComponent { Name = name, LibReference = name, DesignItemId = name };
        foreach (var pin in pins) symbol.AddPin(SchPin.Create(pin.Number).WithName(pin.Name).Build());
        return symbol;
    }
    private static async Task WriteAsync(string path, params SchComponent[] symbols)
    {
        var library = (SchLibrary)AltiumLibrary.CreateSchLib();
        foreach (var symbol in symbols) library.Add(symbol);
        await library.SaveAsync(path);
    }
    public static async Task RunAsync()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "symbol-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var resolver = new UserSymbolLibraryResolver();
        var part = Part("TARGET", ("1", "1"), ("2", "2"));
        var grouped = Path.Combine(root, "TARGET.SchLib");
        await WriteAsync(grouped, Symbol("WRONG_FIRST", ("1", "1"), ("2", "2")), Symbol("TARGET", ("1", "1"), ("2", "2")));
        Check(resolver.Resolve(part, grouped, null)?.ComponentName == "TARGET", "filename match selects the identified record, not the first library symbol");
        var ambiguous = Path.Combine(root, "MULTI.SchLib");
        await WriteAsync(ambiguous, Symbol("FIRST", ("1", "1"), ("2", "2")), Symbol("SECOND", ("1", "1"), ("2", "2")));
        Check(resolver.Resolve(Part("MULTI", ("1", "1"), ("2", "2")), ambiguous, null) is null,
            "multi-record filename alone cannot select an arbitrary symbol");
        var package = Path.Combine(root, "SOT23.SchLib");
        await WriteAsync(package, Symbol("WRONG_DEVICE", ("1", "1"), ("2", "2")));
        part.Properties["package"] = "SOT23";
        Check(resolver.Resolve(part, package, null) is null, "package match is never an exact component-identity match");
        var incompatible = Path.Combine(root, "MISMATCH.SchLib");
        await WriteAsync(incompatible, Symbol("MISMATCH", ("2", "2"), ("3", "3")));
        Check(resolver.Resolve(Part("MISMATCH", ("1", "1"), ("2", "2")), incompatible, null) is null,
            "same name and pin count cannot hide different numbered terminals");
        var diode = Part("DIODE_TEST", ("1", "K"), ("2", "A"));
        Check(!UserSymbolLibraryResolver.CheckPinCompatibility(diode, Symbol("reversed", ("1", "A"), ("2", "K"))).Compatible,
            "opposite diode polarity on the same pin numbers is rejected");
        Check(UserSymbolLibraryResolver.CheckPinCompatibility(diode, Symbol("matched", ("2", "Anode"), ("1", "Cathode")), true).Compatible,
            "pin order is irrelevant and recognised diode function aliases match");
        Check(!UserSymbolLibraryResolver.CheckPinCompatibility(diode, Symbol("unknown", ("1", "1"), ("2", "2")), true).Compatible,
            "unknown generic polarity is not treated as proven compatibility");
        Check(!UserSymbolLibraryResolver.CheckPinCompatibility(Part("FET", ("1", "G"), ("2", "S"), ("3", "D")),
            Symbol("wrongFET", ("1", "GATE"), ("2", "DRAIN"), ("3", "SOURCE"))).Compatible,
            "MOSFET source/drain swap is rejected");
        var catalog = Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib");
        Check(resolver.ListMasterSymbols(string.Join(Path.PathSeparator, root, catalog, catalog)).Count == 71,
            "combined directory/catalog search path exposes all 71 choices exactly once");
        await TransistorSymbolTests.RunAsync(root, catalog);
        await DiscreteNetworkSymbolTests.RunAsync(root, catalog);
        await EasyEdaSymbolPreferenceTests.RunAsync(root, catalog);
        await NativeCommonSymbolTests.RunAsync(root, catalog);
        foreach (var (description, template) in new[] { ("Switching Diode", "Diode"), ("LED", "LED"), ("Zener Diode", "Diode_Zener") })
        foreach (var reversed in new[] { false, true })
        {
            var testDiode = Part("DIODE_QUALITY", ("1", reversed ? "A" : "K"), ("2", reversed ? "K" : "A"));
            testDiode.Tags.Add(description);
            var match = resolver.Resolve(testDiode, catalog, null);
            Check(match?.ComponentName == template && match.DiodePins is not null, $"{description} uses its native drawing for both pin polarities ({reversed})");
            var drawing = resolver.LoadSelectedComponent(match!);
            Check(UserSymbolLibraryResolver.CheckPinCompatibility(testDiode, drawing, true).Compatible && drawing.Rectangles.Count == 0,
                "diode drawing is not a rectangular pin block and every numbered function is preserved");
            Check(drawing.Pins.Single(p => p.Name == "A").Location.X.ToMils() == -40 &&
                drawing.Pins.Single(p => p.Name == "K").Location.X.ToMils() == 40,
                "source pin numbers bind to the verified native anode/cathode artwork ends");
            var savedDiode = Path.Combine(root, template + reversed + ".SchLib");
            await WriteAsync(savedDiode, drawing);
            var writtenDiode = ((SchLibrary)await AltiumLibrary.OpenSchLibAsync(savedDiode)).Components.OfType<SchComponent>().Single();
            Check(UserSymbolLibraryResolver.CheckPinCompatibility(testDiode, writtenDiode, true).Compatible,
                "diode polarity binding survives native write/read");
        }
        var untouchedDiode = resolver.LoadPreviewComponent(new(catalog, "test", "Diode"));
        Check(untouchedDiode.Pins.All(p => p.Name == p.Designator), "diode binding does not mutate cached native templates");
        var unknownDiode = Part("UNKNOWN_DIODE", ("1", "1"), ("2", "2")); unknownDiode.Tags.Add("Diodes");
        Check(resolver.Resolve(unknownDiode, catalog, null) is null, "numeric source pins alone never establish diode polarity");
        var multiDiode = Part("MULTI_DIODE", ("1", "K"), ("2", "A"), ("3", "A")); multiDiode.Tags.Add("Diode array");
        Check(resolver.Resolve(multiDiode, catalog, null) is null, "multi-terminal diode array is never flattened to two terminals");
        var signDiode = Part("SIGN_DIODE", ("1", "-"), ("2", "+")); signDiode.Tags.Add("Switching Diodes");
        Check(resolver.Resolve(signDiode, catalog, null)?.DiodePins is { SourceAnode: "2", SourceCathode: "1" },
            "explicit plus/minus diode labels bind in diode context only");
        signDiode.SymbolPins[0] = signDiode.SymbolPins[0] with { Name = "C" };
        signDiode.SymbolPins[1] = signDiode.SymbolPins[1] with { Name = "A" };
        Check(resolver.Resolve(signDiode, catalog, null)?.DiodePins is { SourceAnode: "2", SourceCathode: "1" },
            "C means cathode for verified two-terminal diode metadata (US1MG/SM4007PL regression)");
        Check(!UserSymbolLibraryResolver.CheckPinCompatibility(Part("BJT", ("1", "B"), ("2", "C"), ("3", "E")),
            Symbol("WRONG", ("1", "B"), ("2", "K"), ("3", "E"))).Compatible, "C remains collector outside diode context");
        var schottkyFile = Path.Combine(root, "Schottky.SchLib");
        var donor = Symbol("SS34_C52023881", ("1", "1"), ("2", "2"));
        donor.AddParameter(new SchParameter { Name = "Donor Rating", Value = "Must not leak" });
        await WriteAsync(schottkyFile, donor);
        var schottky = Part("SS54_TEST", ("1", "A"), ("2", "K")); schottky.Tags.Add("Schottky Barrier Diodes (SBD)");
        var schottkyMatch = resolver.Resolve(schottky, string.Join(Path.PathSeparator, schottkyFile, catalog), null)!;
        var schottkyCopy = resolver.LoadSelectedComponent(schottkyMatch);
        Check(schottkyMatch.DiodePins is { TemplateAnode: "2", TemplateCathode: "1", SourceAnode: "1", SourceCathode: "2" } &&
            UserSymbolLibraryResolver.CheckPinCompatibility(schottky, schottkyCopy, true).Compatible,
            "reversed-numbering Schottky uses native SS34 artwork with source electrical mapping");
        Check(!schottkyCopy.Parameters.Any(p => p.Name == "Donor Rating") &&
            resolver.LoadPreviewComponent(new(schottkyFile, "test", "SS34_C52023881")).Parameters.Any(p => p.Name == "Donor Rating"),
            "artwork reuse discards donor-specific metadata only on the independent output copy");
        var inductor = Part("QUALITY_INDUCTOR", ("1", "1"), ("2", "2"));
        inductor.Description = "Inductor"; inductor.Properties["pre"] = "L?";
        var nativeMatch = resolver.Resolve(inductor, catalog, null)!;
        var native = resolver.LoadSelectedComponent(nativeMatch);
        Check(native.EllipticalArcs.Count == 4 && native.Arcs.Count == 0 &&
            native.EllipticalArcs.All(a => Math.Abs(a.LineWidth.ToMils() - 2) < .001),
            "native inductor elliptical coils are retained with Small width, not replaced");
        Check(native.Polylines.Count == 2 && native.Polylines.All(p =>
            Math.Abs(p.Vertices[0].X.ToMils() - 100) < .001 && Math.Abs(p.Vertices[1].X.ToMils() + 100) < .001),
            "native inductor core bars decode at 200 mil span, not 100 times smaller");
        var nativePath = Path.Combine(root, "native-inductor.SchLib");
        await WriteAsync(nativePath, native);
        var reopened = ((SchLibrary)await AltiumLibrary.OpenSchLibAsync(nativePath)).Components.OfType<SchComponent>().Single();
        Check(reopened.EllipticalArcs.Count == 4 && reopened.EllipticalArcs.All(a => Math.Abs(a.LineWidth.ToMils() - 2) < .001) &&
            reopened.Polylines.SelectMany(p => p.Vertices).SequenceEqual(native.Polylines.SelectMany(p => p.Vertices)),
            "native inductor curve width and core coordinates survive saving and reopening");
        await File.WriteAllBytesAsync(Path.Combine(root, "native-inductor.png"), SchematicSymbolPreviewRenderer.RenderPng(reopened, 800, 500));
        var kiCadPath = await new KiCadLibraryExporter().UpsertAsync(inductor, Path.Combine(root, "kicad"), native, null,
            CancellationToken.None, includeFootprint: false);
        var kiCadText = await File.ReadAllTextAsync(kiCadPath);
        Check(Regex.Matches(kiCadText, @"\(polyline ").Count == 6 && kiCadText.Contains("(width 0.0508)"),
            "KiCad native inductor keeps four coil curves and two Small-width core bars");
        var fallback = AltiumSchExporter.CreateEasyEdaSymbol(inductor);
        Check(fallback.Pins.Select(p => p.Location.X.ToMils()).Order().SequenceEqual(new[] { -200d, 200d }) &&
            fallback.Arcs.All(a => Math.Abs(a.LineWidth.ToMils() - 2) < .001),
            "generated coil meets pin body ends with Small width");
        inductor.SymbolPins.Add(new("3", "3", 0, 0, ""));
        inductor.SymbolPins.Add(new("4", "4", 0, 0, ""));
        Check(!AltiumSchExporter.UsesStandardInductorSymbol(inductor) &&
            AltiumSchExporter.CreateEasyEdaSymbol(inductor).Pins.Count == 4,
            "multi-terminal inductors retain every pin instead of becoming a two-pin coil");
        var fractional = Symbol("FRACTIONAL", ("1", "1"));
        fractional.AddPolyline(SchPolyline.Create().From(Coord.FromMils(.5), Coord.FromMils(-.25))
            .To(Coord.FromMils(100.125), Coord.FromMils(0)).LineWidth(1).Build());
        var fractionalPath = Path.Combine(root, "fractional.SchLib");
        await WriteAsync(fractionalPath, fractional);
        var fractionalRead = ((SchLibrary)await AltiumLibrary.OpenSchLibAsync(fractionalPath)).Components.OfType<SchComponent>().Single();
        Check(fractional.Polylines[0].Vertices.SequenceEqual(fractionalRead.Polylines[0].Vertices),
            "native vertices preserve fractional-only, negative and omitted zero coordinates");
        var resistor = Part("GENERIC_PART", ("1", "1"), ("2", "2")); resistor.Properties["pre"] = "R?";
        Check(resolver.Resolve(resistor, catalog, null)?.ComponentName == "Resistor", "compatible ordinary resistor retains native artwork");
        resistor.SymbolPins.Add(new("3", "3", 0, 0, ""));
        Check(resolver.Resolve(resistor, catalog, null) is null, "a third terminal prevents automatic two-pin resistor substitution");
        foreach (var (description, prefix) in new[] { ("Polarized electrolytic capacitor", "C?"), ("Thermistor", "R?"),
            ("Resistor network", "R?"), ("Coupled inductor", "L?"), ("Variable capacitor", "C?") })
        {
            var special = Part("SPECIAL", ("1", "1"), ("2", "2")); special.Description = description; special.Properties["pre"] = prefix;
            Check(resolver.Resolve(special, catalog, null) is null, $"{description} is not flattened into an ordinary passive by its prefix");
        }
        var bead = Part("FERRITE_TEST", ("1", "1"), ("2", "2")); bead.Description = "Ferrite bead";
        bead.Properties["pre"] = "L?";
        Check(resolver.Resolve(bead, catalog, null)?.ComponentName == "Ferrite_Chip" &&
            !AltiumSchExporter.UsesStandardInductorSymbol(bead), "ferrite bead uses its native ferrite symbol, not an inductor coil");
        var beadSymbol = resolver.LoadSelectedComponent(resolver.Resolve(bead, catalog, null)!);
        AltiumSchExporter.PrepareSymbol(beadSymbol, bead, false);
        Check(beadSymbol.Description?.StartsWith("Ferrite bead", StringComparison.Ordinal) == true,
            "ferrite bead description overrides misleading L-prefix Inductor label");
        bool rejected = false;
        try
        {
            await new LibraryExporter().ExportImportPlanAsync(Part("MISMATCH", ("1", "1"), ("2", "2")),
                Path.Combine(root, "rejected-export"), include3d: false,
                selectedSymbol: new(incompatible, "test explicit choice", "MISMATCH"), includeFootprint: false);
        }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(root, "rejected-export", "youeda.SchLib")),
            "explicit incompatible native symbol fails before writing a library");
    }

    public static async Task AuditAsync(string csv, string cache, string output)
    {
        if (File.Exists(output)) throw new IOException("Quality report output must be a new file.");
        var codes = (await File.ReadAllLinesAsync(csv)).Select(s => s.Trim()).Where(s => Regex.IsMatch(s, "^C[0-9]+$"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Length != 351) throw new InvalidDataException($"Expected 351 acceptance components, found {codes.Length}.");
        var roots = string.Join(Path.PathSeparator, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Library"),
            Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib"));
        var rows = new List<object>();
        var counts = new Dictionary<string, int>();
        var review = (SchLibrary)AltiumLibrary.CreateSchLib();
        var reviewFolder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(reviewFolder);
        int terminalReviews = 0;
        foreach (var code in codes)
        {
            var component = new Parser().Parse(code, await File.ReadAllTextAsync(Path.Combine(cache, code + ".json")));
            var resolver = new UserSymbolLibraryResolver();
            var match = resolver.Resolve(component, roots, null);
            var symbol = match is null ? null : resolver.LoadPreviewComponent(match);
            var pinCheck = symbol is null ? null : UserSymbolLibraryResolver.CheckPinCompatibility(component, symbol);
            if (pinCheck is { Compatible: false }) throw new Exception("Unsafe automatic symbol match: " + code);
            var decision = match?.MatchKind ?? (resolver.MustGenerateFromEasyEda(component) ? "EasyEDA fallback" : "deferred symbol review");
            counts[decision] = counts.GetValueOrDefault(decision) + 1;
            var pins = component.SymbolPins.Select(p => p.Number.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var pads = component.Pads.Select(p => p.Number.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var unmatchedPins = pins.Except(pads).ToArray(); var unmatchedPads = pads.Except(pins).ToArray();
            var terminalReview = component.HasFootprintData && (unmatchedPins.Length > 0 || unmatchedPads.Length > 0);
            if (terminalReview) terminalReviews++;
            if (match is not null && (match.DiodePins is not null || match.TransistorPins is not null || match.Network is not null || match.CommonPins is not null || match.ComponentName is "Ferrite_Chip" or "Inductor"))
            {
                var copy = resolver.LoadSelectedComponent(match);
                AltiumSchExporter.PrepareSymbol(copy, component, false);
                new AltiumSchExporter().Upsert(review, copy, false);
                await File.WriteAllBytesAsync(Path.Combine(reviewFolder, code + ".png"), SchematicSymbolPreviewRenderer.RenderPng(copy, 800, 400));
            }
            rows.Add(new { part = code, component.Name, component.Description, component.FootprintName, component.Tags, decision,
                symbol = match?.ComponentName, source = match?.Path, pinCheck,
                easyEdaPreference = EasyEdaSymbolPreference.Reason(component),
                diodeBinding = match?.DiodePins,
                transistorBinding = match?.TransistorPins,
                networkBinding = match?.Network,
                commonBinding = match?.CommonPins,
                sourcePins = component.SymbolPins.Select(p => new { p.Number, p.Name }), numberedPads = pads,
                unmatchedPins, unmatchedPads, terminalReview,
                note = "Unmatched terminals need review: NC, mechanical pads and repeated terminals may be legitimate. Matching numbers alone does not certify electrical correctness." });
        }
        if (review.Components.Any())
        {
            var reviewPath = Path.Combine(reviewFolder, "Symbol-Quality-Review.SchLib");
            if (File.Exists(reviewPath)) throw new IOException("Review library output must be a new file.");
            await review.SaveAsync(reviewPath);
            var reopened = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(reviewPath);
            if (reopened.Components.Count() != review.Components.Count()) throw new Exception("Review library lost symbols on reopen.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { schema = "youeda-symbol-quality-audit/v1",
            total = codes.Length, counts, terminalReviews, offline = true, libraryFilesModified = false, components = rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { total = codes.Length, counts, terminalReviews, output }));
    }
}
