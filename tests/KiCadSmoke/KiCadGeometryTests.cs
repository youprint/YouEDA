using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;
using OriginalCircuit.Altium.Models.Sch;
using OriginalCircuit.Eda.Enums;
using OriginalCircuit.Eda.Primitives;

internal static class KiCadGeometryTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }

    public static async Task RunAsync()
    {
        var output = Path.Combine(Path.GetTempPath(), "YouEDA-KiCad-geometry-" + Guid.NewGuid().ToString("N"));
        var source = new EdaComponent { LcscPartNumber = "C_PIN_TEST", Name = "Four directions" };
        var native = new SchComponent();
        foreach (var orientation in Enum.GetValues<PinOrientation>())
            native.AddPin(new SchPin { Designator = ((int)orientation + 1).ToString(),
                Name = "P", Location = new CoordPoint(Coord.FromMm(0), Coord.FromMm(0)),
                Length = Coord.FromMm(2.54), Orientation = orientation, ShowName = false });
        var writer = new KiCadLibraryExporter();
        await writer.UpsertAsync(source, output, native, includeFootprint: false);
        var text = await File.ReadAllTextAsync(Path.Combine(output, "youeda.kicad_sym"));
        foreach (var geometry in new[] { "(at 2.54 0 180)", "(at 0 2.54 270)", "(at -2.54 0 0)", "(at 0 -2.54 90)" })
            Check(text.Contains(geometry), "native wire tip and inward direction: " + geometry);
        Check(native.Pins.All(p => p.Location.X.ToMm() == 0 && p.Location.Y.ToMm() == 0), "export does not mutate native pin roots");
        Check(text.Contains("(pin_names hide)") && text.Contains("(name \"P\""), "native hidden pin names remain electrically present without overlapping artwork");
        var unit = new EdaSymbolUnit(); source.SymbolUnits.Add(unit);
        foreach (var rotation in new[] { 0, 90, 180, 270 })
            unit.Pins.Add(new EdaSymbolPin(rotation.ToString(), "P", 0, rotation, "") { Xmm = 10, Ymm = 20, LengthMm = 2.54 });
        await writer.UpsertAsync(source, output, includeFootprint: false);
        text = await File.ReadAllTextAsync(Path.Combine(output, "youeda.kicad_sym"));
        foreach (var angle in new[] { 0, 90, 180, 270 })
            Check(text.Contains($"(at 10 20 {angle})"), "EasyEDA preserves wire anchor and converts direction " + angle);
        Check(text.IndexOf("(at 10 20 270)") < text.IndexOf("(number \"90\""), "EasyEDA 90-degree pin points down into body");
        unit.Pins.Clear();
        foreach (var type in new[] { 0, 1, 2, 3, 4 })
            unit.Pins.Add(new EdaSymbolPin(type.ToString(), "P", type, 0, "") { Xmm = type, Ymm = 20, LengthMm = 2.54 });
        await writer.UpsertAsync(source, output, includeFootprint: false);
        text = await File.ReadAllTextAsync(Path.Combine(output, "youeda.kicad_sym"));
        var types = new[] { "unspecified", "input", "output", "bidirectional", "power_in" };
        for (var type = 0; type < types.Length; type++)
            Check(text.Contains($"(pin {types[type]} line (at {type} 20 180)"), "EasyEDA electrical type " + type + " maps to " + types[type]);
        source.Pads.Add(new EdaPad("1", 0, 0, 2, 4, 90, "1", true, 0));
        source.Shapes.Add(new EdaShape("ARC", "3", [(0, 0)], .15) { RadiusMm = 1, StartAngleDeg = 0, EndAngleDeg = 90 });
        source.Shapes.Add(new EdaShape("SOLIDREGION", "99", [(-.5,-.5),(.5,-.5),(.5,.5),(-.5,.5),(-.5,-.5)], 0));
        source.Properties["JLCPCB Unit Price"] = "0.0022 USD";
        source.Properties["JLCPCB Price Quantity"] = "1";
        source.Properties["Manufacturer"] = "Test \"Maker\"";
        source.Properties["Datasheet"] = "https://example.test/datasheet.pdf";
        source.Properties["Value"] = "10k";
        await writer.UpsertAsync(source, output);
        text = await File.ReadAllTextAsync(Path.Combine(output, "youeda.kicad_sym"));
        var footprint = await File.ReadAllTextAsync(Path.Combine(output, "youeda.pretty", "C_PIN_TEST.kicad_mod"));
        Check(footprint.Contains("(fp_arc (start 1 0) (mid 0.707107 -0.707107) (end 0 -1)"), "arc start/mid/end preserve source sweep after Y conversion");
        Check(footprint.Contains("(fp_rect (start -2.25 -1.35) (end 2.25 1.35)"), "courtyard encloses rotated pad and stroked arc with clearance");
        Check(text.Contains("(property \"JLCPCB Unit Price\" \"0.0022 USD\"") && text.Contains("(property \"Component Value\" \"10k\""), "price and electrical value survive KiCad export");
        Check(text.Contains("(property \"Datasheet\" \"https://example.test/datasheet.pdf\"") && text.Contains("Test \\\"Maker\\\""), "datasheet and escaped metadata survive");
        Check(text.Contains("(property \"Reference\" \"U\" (at 0 22.54"), "labels are outside converted geometry bounds");
        source.Pads.Add(new EdaPad("2", 0, 0, 3, 1, 45, "1", true, 0));
        source.Shapes.Add(new EdaShape("CIRCLE", "12", [(50, 50), (.1, 0)], .05));
        source.Shapes.Add(new EdaShape("CIRCLE", "101", [(0, 0), (.1, 0)], .05));
        var step = Path.Combine(output, "fixture.step");
        await File.WriteAllTextAsync(step, "ISO-10303-21;\nEND-ISO-10303-21;");
        var pose = new Eda3dModel("test", "test", 1, 2, .3, 10, 20, 90, 2, 3);
        await writer.UpsertAsync(source, output, model: new Downloaded3dModel(pose, "fixture.step", step, "", .1, 1));
        footprint = await File.ReadAllTextAsync(Path.Combine(output, "youeda.pretty", "C_PIN_TEST.kicad_mod"));
        Check(footprint.Contains("(offset (xyz 1 2 0.4))") && footprint.Contains("(rotate (xyz -10 -20 -90))"), "3D uses Y-up offsets and KiCad inverse Euler convention");
        Check(footprint.Contains("(pad \"2\" smd rect (at 0 0 45)"), "non-cardinal pad rotation is preserved, not mirrored");
        var circles = footprint.Split('\n').Where(l => l.Contains("(fp_circle")).ToArray();
        Check(circles.Any(l => l.Contains("Cmts.User")) && circles.Any(l => l.Contains("F.Fab")) && !circles.Any(l => l.Contains("F.SilkS")), "editor and pin-one markers are not manufactured as silkscreen");
        Check(!footprint.Split('\n').Single(l => l.Contains("F.CrtYd")).Contains("50"), "documentation markers do not enlarge physical courtyard");
        var hidden = new Parser().Parse("C_HIDDEN", """
            {"result":{"title":"Hidden pin fixture","dataStr":{"head":{"x":0,"y":0,"c_para":{"pre":"Q?"}},"shape":["P~show~4~1~0~0~0~id~0^^0~0^^M 0 0 h -10~#000000^^0~-10~0~0~VCC~end~~~#000000"]}}}
            """);
        await writer.UpsertAsync(hidden, output, includeFootprint: false);
        text = await File.ReadAllTextAsync(Path.Combine(output, "youeda.kicad_sym"));
        var entry = text[text.IndexOf("(symbol \"C_HIDDEN\"")..];
        Check(!hidden.SymbolPins[0].ShowName && entry.Contains("(pin_names hide)") && entry.Contains("(name \"VCC\""), "EasyEDA hidden name is parsed and kept electrically available");
        Check(entry.Contains("(property \"Reference\" \"Q\""), "source designator prefix is preserved");
    }
}
