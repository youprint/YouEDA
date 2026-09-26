using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Sch;
using EasyEdaAltiumGrabber.Controls;

internal static class NativeCommonSymbolTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("FAILED: " + message); Console.WriteLine("PASS: " + message); }

    private static EdaComponent Part(string code, string name, string category, string[] numbers, string[] labels)
    {
        var p = new EdaComponent { LcscPartNumber = code, Name = name };
        p.Properties["Manufacturer Part"] = name; p.Tags.Add(category);
        for (int i = 0; i < numbers.Length; i++) p.SymbolPins.Add(new(numbers[i], labels[i], 0, 0, ""));
        return p;
    }
    private static string Geometry(SchComponent s) => string.Join("|", s.Polylines.SelectMany(l => l.Vertices)
        .Concat(s.Polygons.SelectMany(p => p.Vertices)).Select(p => p.ToString()));

    public static async Task RunAsync(string root, string catalog)
    {
        var resolver = new UserSymbolLibraryResolver();
        var parts = new[] {
            Part("C109227", "LTV-817S-TA1-C", "Optocouplers - Phototransistor Output", ["1","2","3","4"], ["1","2","3","4"]),
            Part("C115450", "LTV-217-B-G", "Optocouplers - Phototransistor Output", ["1","2","3","4"], ["AN","CAT","EM","COL"]),
            Part("C16133", "TAJB107K006RNJ", "Tantalum Capacitors", ["1","2"], ["1","2"]),
            Part("C7171", "TAJA106K016RNJ", "Tantalum Capacitors", ["2","1"], ["2","1"]),
            Part("C13738", "X322516MLB4SI", "Crystals", ["4","2","1","3"], ["GND","GND","OSC1","OSC2"]),
            Part("C9002", "X322512MSB4SI", "Crystals", ["3","1","2","4"], ["OSC2","OSC1","GND","GND"]),
            Part("C9006", "X322525MOB4SI", "Crystals", ["4","2","1","3"], ["GND","GND","OSC1","OSC2"]),
            Part("C999", "PassiveCrystal", "Crystals", ["7","9"], ["7","9"]),
            Part("C998", "ReorderedCrystal", "Crystals", ["1","2","3","4"], ["GND","OSC2","GND","OSC1"])
        };
        foreach (var p in parts)
        {
            var match = resolver.Resolve(p, catalog, null);
            Check(match?.CommonPins is not null, p.LcscPartNumber + " automatically selects native common artwork");
            var symbol = resolver.LoadSelectedComponent(match!);
            var template = resolver.LoadPreviewComponent(new(catalog, "test", match!.ComponentName));
            Check(Geometry(symbol) == Geometry(template) && UserSymbolLibraryResolver.CheckPinCompatibility(p, symbol, true).Compatible,
                p.LcscPartNumber + " keeps native geometry and verified terminal functions");
            Check(symbol.Arcs.Count == template.Arcs.Count && symbol.EllipticalArcs.Count == template.EllipticalArcs.Count,
                p.LcscPartNumber + " retains native curved artwork");
            Check(symbol.Polylines.All(l => l.LineWidth.ToMils() == 2), p.LcscPartNumber + " preserves Small native strokes");
            foreach (var pin in symbol.Pins)
            {
                if (match.CommonPins!.Kind == "optoisolator")
                    Check(pin.Name == new[] { "A", "K", "E", "C" }[int.Parse(pin.Designator!) - 1], "opto LED/transistor roles match reviewed pinout");
                else if (match.CommonPins.Kind == "polarised capacitor")
                    Check(pin.Name == (pin.Designator == "1" ? "+" : "-") && pin.Location.X.ToMils() == (pin.Designator == "1" ? -100 : 100),
                        "tantalum positive pin 1 stays on the native plus-marked plate; pin 2 is negative");
                else if (p.SymbolPins.Count == 4)
                    Check((pin.Name == "GND") == (pin.Location.Y.ToMils() == -200), "crystal grounds are on ground terminals, not crystal electrodes");
            }
            var library = (SchLibrary)AltiumLibrary.CreateSchLib(); library.Add(symbol);
            var file = Path.Combine(root, p.LcscPartNumber + "-common.SchLib"); await library.SaveAsync(file);
            var reopened = ((SchLibrary)await AltiumLibrary.OpenSchLibAsync(file)).Components.OfType<SchComponent>().Single();
            Check(Geometry(reopened) == Geometry(template) && UserSymbolLibraryResolver.CheckPinCompatibility(p, reopened, true).Compatible,
                p.LcscPartNumber + " geometry and mapped terminals survive save/reopen");
            await File.WriteAllBytesAsync(Path.Combine(root, p.LcscPartNumber + "-common.png"), SchematicSymbolPreviewRenderer.RenderPng(reopened, 800, 400));
        }
        var opto = parts[1]; opto.SymbolPins[2] = opto.SymbolPins[2] with { Name = "COL" };
        Check(resolver.Resolve(opto, catalog, null) is null, "conflicting optocoupler emitter/collector never silently binds");
        opto.SymbolPins[2] = opto.SymbolPins[2] with { Name = "EM" }; opto.Properties["Manufacturer Part"] = "UNREVIEWED";
        Check(resolver.Resolve(opto, catalog, null) is null, "changed optocoupler identity requires review");
        foreach (var cap in parts.Where(p => p.LcscPartNumber is "C16133" or "C7171"))
        {
            var index = cap.SymbolPins.FindIndex(p => p.Number == "1");
            cap.SymbolPins[index] = cap.SymbolPins[index] with { Name = "-" };
            Check(resolver.Resolve(cap, catalog, null) is null, "conflicting tantalum polarity is never silently accepted");
            cap.SymbolPins[index] = cap.SymbolPins[index] with { Name = "1" };
            cap.Properties["Manufacturer Part"] = "UNKNOWN";
            Check(resolver.Resolve(cap, catalog, null) is null, "changed tantalum identity cannot inherit numeric pin polarity");
        }
        foreach (var category in new[] { "Crystal Oscillators", "TCXO Crystals", "Optotriac", "Optocouplers - Phototransistor Output" })
        {
            var unknown = Part("C777", "Unknown", category, ["1","2","3","4"], ["1","2","3","4"]);
            Check(resolver.Resolve(unknown, catalog, null) is null, "four pins do not guess " + category);
        }
        var powered = Part("C778", "ActiveCrystal", "Crystals", ["1","2","3","4"], ["EN","GND","OUT","VCC"]);
        Check(resolver.Resolve(powered, catalog, null) is null, "powered oscillator pins cannot use passive crystal artwork");
        Check(resolver.LoadPreviewComponent(new(catalog, "test", "Optoisolator")).Pins.All(p => p.Name == p.Designator),
            "binding does not mutate the cached optoisolator source");
    }
}
