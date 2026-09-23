using EasyEdaAltiumGrabber.Services;
using EasyEdaAltiumGrabber.Models;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Pcb;
using OriginalCircuit.Altium.Models.Sch;
using OriginalCircuit.Eda.Primitives;

if (args.Length > 0 && args[0] == "--inspect-symbol")
{
    foreach (var path in args.Skip(1))
    {
        var library = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(path);
        Console.WriteLine($"LIBRARY {path}");
        Console.WriteLine("HEADER " + string.Join(" ", (library.HeaderParameters ?? new()).Where(kv => kv.Key.Contains("color", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("sheet", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("font", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("size", StringComparison.OrdinalIgnoreCase)).Take(35).Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (var item in library.Components.OfType<SchComponent>().Where(item =>
                     item.Name == "C11702" || item.Name.Contains("Resistor", StringComparison.OrdinalIgnoreCase) ||
                     item.Name.EndsWith(" Diode", StringComparison.OrdinalIgnoreCase) ||
                     path.Contains("Resistors.SchLib", StringComparison.OrdinalIgnoreCase) || path.Contains("Diodes.SchLib", StringComparison.OrdinalIgnoreCase) ||
                     path.Contains("Capacitors.SchLib", StringComparison.OrdinalIgnoreCase) ||
                     path.Contains("Desktop\\Library", StringComparison.OrdinalIgnoreCase)).Take(3))
        {
            Console.WriteLine($"SYMBOL {item.Name}: pins={item.Pins.Count}, polylines={item.Polylines.Count}, models={item.Implementations.Count}");
            foreach (var pin in item.Pins.Take(3))
                Console.WriteLine($"  PIN {pin.Designator} {pin.Name}: ({pin.Location.X.ToMm():F4},{pin.Location.Y.ToMm():F4}) length={pin.Length.ToMm():F4}");
            foreach (var line in item.Polylines.Take(2))
                Console.WriteLine("  LINE " + string.Join(" ", line.Vertices.Take(8).Select(v => $"({v.X.ToMm():F4},{v.Y.ToMm():F4})")));
            Console.WriteLine($"  META comment={item.Comment} prefix={item.DesignatorPrefix} description={item.Description}");
            foreach (var param in item.Parameters.Cast<SchParameter>().Where(p => p.IsVisible || p.Name is "Value" or "Comment" or "Designator").Take(12))
                Console.WriteLine($"  PARAM {param.Name}={param.Value} visible={param.IsVisible} pos=({param.Location.X.ToMm():F4},{param.Location.Y.ToMm():F4}) color={param.Color} font={param.FontId} owner={param.OwnerIndex}");
            foreach (var impl in item.Implementations)
                Console.WriteLine($"  MODEL {impl.ModelType} {impl.ModelName} [{string.Join(",",impl.DataFileKinds)}] entities=[{string.Join(",",((SchImplementation)impl).DataFileEntities)}] desc={impl.Description}");
        }
    }
    return;
}

const string source = """
{"result":{"title":"Synthetic two-unit part","description":"KiCad and Altium exporter test",
"dataStr":{"head":{"x":0,"y":0,"c_para":{"pre":"U?"}},"shape":[
"R~-2~-2~~~4~4~#880000~1~0~none~gge1~0",
"P~show~0~1~-20~0~180~gge2~0^^-20~0^^M -20 0 h 10~#8D2323^^0~-7~3~0~A~start~~~#8D2323"]},
"subparts":[{"dataStr":{"head":{"x":0,"y":0,"c_para":{"pre":"U?"}},"shape":[
"R~-2~-2~~~4~4~#880000~1~0~none~gge3~0",
"P~show~0~2~20~0~0~gge4~0^^20~0^^M 20 0 h -10~#8D2323^^0~7~3~0~B~end~~~#8D2323"]}}],
"packageDetail":{"dataStr":{"head":{"x":100,"y":100},"shape":[
"PAD~OVAL~100~100~10~8~11~~1~2~~0~gge5~5~~Y",
"TRACK~1~3~~90 90 110 90~gge6~0",
"RECT~95~95~10~10~3~gge7~0~1~none",
"CIRCLE~100~100~2~0.5~3~gge8~0",
"SOLIDREGION~99~~M 90 90 L 110 90 L 110 110 L 90 110 Z~solid~gge9~~0"]}}}}
""";

var component = new Parser().Parse("C999999", source);
Check(component.Pads.Count == 1 && Math.Abs(component.Pads[0].HoleMm - 1.016) < .0001,
    "EasyEDA drill radius must become millimetre diameter");
Check(component.SymbolUnits.Count == 2 && component.SymbolPins.Count == 2, "multi-unit symbol parsing");
Check(component.Shapes.Any(shape => shape.Kind == "SOLIDREGION" && shape.PointsMm.Count >= 4),
    "courtyard path parsing");

var output = Path.Combine(AppContext.BaseDirectory, "smoke-output");
var writer = new KiCadLibraryExporter();
await writer.UpsertAsync(component, output);
await writer.UpsertAsync(component, output);
var symbolFile = File.ReadAllText(Path.Combine(output, "youeda.kicad_sym"));
var footprintFile = File.ReadAllText(Path.Combine(output, "youeda.pretty", "C999999.kicad_mod"));
Check(symbolFile.Split("(symbol \"C999999\"", StringSplitOptions.None).Length == 2, "idempotent symbol upsert");
Check(symbolFile.Contains("C999999_2_1"), "second symbol unit");
Check(footprintFile.Contains("(drill oval 1.016 1.27)"), "oval drill diameter and length");
Check(footprintFile.Contains("F.CrtYd"), "courtyard layer mapping");
Check(File.Exists(Path.Combine(output, "sym-lib-table")) && File.Exists(Path.Combine(output, "fp-lib-table")),
    "KiCad project library tables");
var stubStep = Path.Combine(output, "source.step");
await File.WriteAllTextAsync(stubStep, "ISO-10303-21;\nEND-ISO-10303-21;");
var modelInfo = new Eda3dModel("fixture", "fixture", 1, 2, .3, 0, 0, 90, 2, 3);
var model = new Downloaded3dModel(modelInfo, "source.step", stubStep, "", .1, 1);
await writer.UpsertAsync(component, output, model: model);
Check(File.Exists(Path.Combine(output, "youeda.3dshapes", "C999999.step")), "portable STEP copy");
Check(File.ReadAllText(Path.Combine(output, "youeda.pretty", "C999999.kicad_mod"))
    .Contains("${KIPRJMOD}/youeda.3dshapes/C999999.step"), "KiCad project-relative STEP link");

var altiumFile = await new AltiumV2Exporter().UpsertPcbLibAsync(component, output);
var reopened = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(altiumFile);
var altiumComponent = reopened.Components.Single(item => item.Name == component.LcscPartNumber);
var pad = (PcbPad)altiumComponent.Pads.Single();
Check(Math.Abs(pad.HoleSize.ToMm() - 1.016) < .001 && pad.HoleType == PadHoleType.Slot &&
    Math.Abs(Coord.FromRaw(pad.HoleSlotLength).ToMm() - 1.27) < .001, "Altium drilled pad round-trip");
Check(altiumComponent.Tracks.Count >= 8 && altiumComponent.Arcs.Count == 1,
    "Altium rectangle, circle, and courtyard artwork round-trip");
component.Tags.Add("Crystal");
var catalog = Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib");
var resolver = new UserSymbolLibraryResolver();
var match = resolver.Resolve(component, catalog, null);
Check(match is not null, "automatic two-terminal crystal match");
var genericOutput = Path.Combine(AppContext.BaseDirectory, "generic-output");
await writer.UpsertAsync(component, genericOutput, resolver.LoadSelectedComponent(match!));
Check(File.ReadAllText(Path.Combine(genericOutput, "youeda.kicad_sym")).Contains("(xy -2.54 0)"),
    "bundled Altium graphic scale translation");
var resistorSource = new EdaComponent { LcscPartNumber = "C11702", Name = "0402WGF1001TCE", Description = "1KΩ ±1%" };
resistorSource.Properties["Value"] = "1kΩ";
var bundled = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(catalog);
var resistor = (SchComponent)bundled["0724 Resistor"]!;
resistor.Name = resistorSource.LcscPartNumber;
resistor.LibReference = resistorSource.LcscPartNumber;
AltiumSchExporter.PrepareSymbol(resistor, resistorSource);
var schExporter = new AltiumSchExporter();
var schPath = await schExporter.UpsertSymbolAsync(resistor, output);
var writtenSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(schPath);
var writtenResistor = (SchComponent)writtenSch["C11702"]!;
Check(writtenResistor.Implementations.Count == 1 && writtenResistor.Implementations[0].ModelType == "PCBLIB" &&
    writtenResistor.Implementations[0].ModelName == "C11702", "native Altium footprint model link");
Check(writtenResistor.Parameters.OfType<SchParameter>().Any(p => p.Name == "Value" && p.Value == "1kΩ" && !p.IsVisible) &&
    writtenResistor.Parameters.OfType<SchParameter>().Any(p => p.Name == "Comment" && p.Value == "=Value" && p.IsVisible),
    "passive value and visible Altium comment expression");
Check(writtenSch.HeaderParameters?.Any(kv => kv.Key == "AreaColor" && kv.Value == "16317695") == true,
    "light Altium schematic canvas");
await schExporter.UpsertSymbolAsync(writtenResistor, output);
writtenSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(schPath);
Check(((SchComponent)writtenSch["C11702"]!).Implementations.Count == 1, "idempotent native footprint link");
var diodeSource = new EdaComponent { LcscPartNumber = "C2891778", Name = "BZT52C10", Description = "10 V Zener" };
diodeSource.Properties["Manufacturer Part"] = "BZT52C10";
var diode = (SchComponent)bundled.Components.OfType<SchComponent>().First(item => item.Name.EndsWith(" Diode", StringComparison.OrdinalIgnoreCase));
Check(diode.Pins.Any(pin => pin.Designator == "2" && pin.Name == "K" && pin.Location.X.ToMm() > 0) &&
    diode.Pins.Any(pin => pin.Designator == "1" && pin.Name == "A" && pin.Location.X.ToMm() < 0),
    "bundled rectifier pin 2 is the right-side cathode");
var diodePinNumbers = diode.Pins.Select(pin => pin.Designator).ToArray();
diode.Name = diodeSource.LcscPartNumber;
diode.LibReference = diodeSource.LcscPartNumber;
AltiumSchExporter.PrepareSymbol(diode, diodeSource);
await schExporter.UpsertSymbolAsync(diode, output);
writtenSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(schPath);
var writtenDiode = (SchComponent)writtenSch[diodeSource.LcscPartNumber]!;
Check(writtenDiode.Pins.Select(pin => pin.Designator).SequenceEqual(diodePinNumbers), "diode pins preserve template numbering");
Check(writtenDiode.Parameters.OfType<SchParameter>().Any(p => p.Name == "Manufacturer Part" && p.Value == "BZT52C10" && !p.IsVisible) &&
    writtenDiode.Parameters.OfType<SchParameter>().Any(p => p.Name == "Comment" && p.Value == "=Manufacturer Part" && p.IsVisible),
    "diode manufacturer-part comment expression");
Check(writtenDiode.Implementations.Any(model => model.ModelType == "PCBLIB" && model.ModelName == diodeSource.LcscPartNumber),
    "diode native footprint model link");
var capacitorSource = new EdaComponent { LcscPartNumber = "C999998", Name = "CC0402", Description = "10 uF capacitor" };
capacitorSource.Properties["Value"] = "10uF";
var capacitor = (SchComponent)bundled.Components.OfType<SchComponent>().First(item => item.Name.EndsWith(" Capacitor", StringComparison.OrdinalIgnoreCase));
capacitor.Name = capacitorSource.LcscPartNumber;
capacitor.LibReference = capacitorSource.LcscPartNumber;
AltiumSchExporter.PrepareSymbol(capacitor, capacitorSource);
await schExporter.UpsertSymbolAsync(capacitor, output);
writtenSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(schPath);
var writtenCapacitor = (SchComponent)writtenSch[capacitorSource.LcscPartNumber]!;
Check(writtenCapacitor.Parameters.OfType<SchParameter>().Any(p => p.Name == "Value" && p.Value == "10uF" && !p.IsVisible) &&
    writtenCapacitor.Parameters.OfType<SchParameter>().Any(p => p.Name == "Comment" && p.Value == "=Value" && p.IsVisible),
    "capacitor value/comment convention");
Check(writtenCapacitor.Implementations.Any(model => model.ModelType == "PCBLIB" && model.ModelName == capacitorSource.LcscPartNumber),
    "capacitor native footprint model link");
Console.WriteLine($"PASS: parser, KiCad/Altium footprints, resistor/diode/capacitor labels and model links; {output}");

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
}
