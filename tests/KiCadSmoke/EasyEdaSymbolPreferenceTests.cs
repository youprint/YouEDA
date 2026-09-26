using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Sch;

internal static class EasyEdaSymbolPreferenceTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAILED: " + message); Console.WriteLine("PASS: " + message); }

    public static async Task RunAsync(string root, string catalog)
    {
        var resolver = new UserSymbolLibraryResolver();
        var profiles = new (string Code, string[] Names, string[] Numbers)[]
        {
            ("C14289", ["GND", "Vin", "Vout"], ["1", "2", "3"]),
            ("C5446", ["GND", "Vin", "Vout"], ["1", "2", "3"]),
            ("C58069", ["IN", "GND", "OUT"], ["1", "4", "3"]),
            ("C6186", ["GND", "VOUT", "VIN", "VOUT"], ["1", "2", "3", "4"]),
            ("C6187", ["GND", "VOUT", "VIN", "VOUT"], ["1", "2", "3", "4"]),
            ("C71136", ["IN", "GND", "OUT"], ["3", "2", "1"]),
            ("C3113", ["K", "R", "A"], ["1", "2", "3"])
        };
        foreach (var profile in profiles)
        {
            var part = new EdaComponent { LcscPartNumber = profile.Code, Name = "PREFERRED_" + profile.Code };
            for (int i = 0; i < profile.Names.Length; i++)
                part.SymbolPins.Add(new(profile.Numbers[i], profile.Names[i], 0, 0, ""));
            var symbol = AltiumSchExporter.CreateEasyEdaSymbol(part);
            var library = (SchLibrary)AltiumLibrary.CreateSchLib();
            library.Add(symbol);
            var path = Path.Combine(root, part.Name + ".SchLib");
            await library.SaveAsync(path);
            Check(resolver.Resolve(part, string.Join(Path.PathSeparator, path, catalog), null) is null &&
                resolver.MustGenerateFromEasyEda(part), profile.Code + " skips native lookup and review even with sparse metadata and exact native match");
            Check(resolver.Resolve(part, catalog, path)?.MatchKind == "selected override", profile.Code + " still honours explicit override");
            var reopened = ((SchLibrary)await AltiumLibrary.OpenSchLibAsync(path)).Components.OfType<SchComponent>().Single();
            Check(reopened.Pins.Select(p => (Designator: p.Designator ?? "", Name: p.Name ?? "")).OrderBy(p => p.Designator)
                .SequenceEqual(part.SymbolPins.Select(p => (p.Number, p.Name)).OrderBy(p => p.Number)),
                profile.Code + " EasyEDA pin names/numbers including tabs survive save/reopen");
            Check(reopened.Parameters.Any(p => p.Name == "LCSC Part" && p.Value == profile.Code),
                profile.Code + " retains supplier identity");
        }
        foreach (var category in new[] { "Linear Voltage Regulators (LDO)", "LDO", "Voltage Regulators", "Linear Regulators", "Low-dropout regulators" })
        {
            var part = new EdaComponent { LcscPartNumber = "C987654321", Name = "Unknown regulator" };
            part.Tags.Add(category);
            Check(resolver.MustGenerateFromEasyEda(part) && resolver.Resolve(part, catalog, null) is null,
                "new part in " + category + " automatically uses EasyEDA");
            part.Tags.Clear(); part.Properties["JLCPCB Category"] = category;
            Check(resolver.MustGenerateFromEasyEda(part), "category property also recognises " + category);
            part.Properties.Clear(); part.Description = category + " 3.3V";
            Check(resolver.MustGenerateFromEasyEda(part), "type-leading description also recognises " + category);
        }
        foreach (var code in new[] { "C109227", "C115450", "C13738", "C16133", "C7171", "C9002", "C9006", "C999999" })
        {
            var other = new EdaComponent { LcscPartNumber = code, Name = "LDO_LOOKALIKE", FootprintName = "LDO-SOT23",
                Description = "Other device with integrated LDO regulator" };
            other.Tags.Add("Voltage References"); other.Properties["URL"] = "https://example.org/voltage-regulators";
            other.SymbolPins.Add(new("1", "LDO", 0, 0, ""));
            Check(!resolver.MustGenerateFromEasyEda(other) && EasyEdaSymbolPreference.Reason(other) is null,
                code + " unrelated category/name/package/pin/URL does not gain regulator preference");
        }
    }
}
