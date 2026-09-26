using EasyEdaAltiumGrabber.Controls;
using System.Globalization;
using EasyEdaAltiumGrabber.Services;
using EasyEdaAltiumGrabber.Models;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Pcb;
using OriginalCircuit.Altium.Models.Sch;
using OriginalCircuit.Eda.Primitives;

if (args is ["--validate-cached-kicad" or "--validate-cached-kicad-families", var kiManifest, var kiCache, var kiOutput])
{
    await CachedKiCadValidation.RunAsync(kiManifest, kiCache, kiOutput, args[0].EndsWith("-families"));
    return;
}

if (args is ["--validate-cached-families", var acceptanceCsv, var acceptanceCache, var acceptanceOutput])
{
    await CachedFamilyValidation.RunAsync(acceptanceCsv, acceptanceCache, acceptanceOutput);
    return;
}
if (args is ["--audit-symbol-quality", var qualityCsv, var qualityCache, var qualityOutput])
{
    await SymbolSelectionQualityTests.AuditAsync(qualityCsv, qualityCache, qualityOutput);
    return;
}
if (args is ["--add-native-switch-catalog", var nativeSwitchPath, var existingCatalogPath, var extendedCatalogPath])
{
    if (File.Exists(extendedCatalogPath)) throw new IOException("New catalog destination required.");
    var nativeSwitchLibrary = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(nativeSwitchPath);
    var target = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(existingCatalogPath);
    var symbol = nativeSwitchLibrary.Components.OfType<SchComponent>().Single(s => s.Name == "SPST-NO 4 PIN");
    symbol.Name = symbol.LibReference = "SPST-NO_4_PIN";
    target.Add(symbol); await target.SaveAsync(extendedCatalogPath);
    var verified = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(extendedCatalogPath);
    if (verified.Components.Count() != 71 || verified["SPST-NO_4_PIN"]?.Pins.Count != 4) throw new Exception("Catalog verification failed.");
    Console.WriteLine("Verified 71 native symbols, including four-pin switch."); return;
}
if (args is ["--inspect-diode-catalog" or "--inspect-transistor-catalog" or "--inspect-topology-catalog", var diodeCatalog, var diodePreviewDirectory])
{
    Directory.CreateDirectory(diodePreviewDirectory);
    foreach (var catalogPath in Directory.Exists(diodeCatalog) ? Directory.GetFiles(diodeCatalog, "*.*", SearchOption.AllDirectories)
                 .Where(p => Path.GetExtension(p).Equals(".SchLib", StringComparison.OrdinalIgnoreCase)) : new[] { diodeCatalog })
    {
    var library = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(catalogPath);
    foreach (var item in library.Components.OfType<SchComponent>().Where(s => args[0] == "--inspect-topology-catalog" ? s.Pins.Count is >= 2 and <= 4 : args[0] == "--inspect-transistor-catalog" ?
        s.Pins.Count == 3 : s.Pins.Count == 2 &&
        (s.Name.Contains("diode", StringComparison.OrdinalIgnoreCase) || s.Name == "LED" ||
         !Path.GetFileName(diodeCatalog).Equals("BundledUserSymbols.SchLib", StringComparison.OrdinalIgnoreCase))))
    {
        Console.WriteLine($"{catalogPath}: {item.Name}: {item.Description}; display modes={item.DisplayModeCount}");
        foreach (var pin in item.Pins) Console.WriteLine($"  {pin.Designator} [{pin.Name}] at {pin.Location}, {pin.Orientation}");
        if (args[0] != "--inspect-diode-catalog")
        {
            foreach (var polygon in item.Polygons) Console.WriteLine("  POLYGON " + string.Join(";", polygon.Vertices));
            foreach (var line in item.Polylines) Console.WriteLine("  LINE " + string.Join(";", line.Vertices) + " width=" + line.LineWidth.ToMils());
            foreach (var line in item.Lines) Console.WriteLine($"  SEGMENT {line.Start};{line.End} width={line.Width.ToMils()}");
        }
        await File.WriteAllBytesAsync(Path.Combine(diodePreviewDirectory, item.Name + ".png"),
            SchematicSymbolPreviewRenderer.RenderPng(item, 800, 400));
    }
    }
    return;
}
await KiCadGeometryTests.RunAsync();
await KiCadReauditTests.RunAsync();
await SymbolSelectionQualityTests.RunAsync();

var pricePage = """
<script type="application/ld+json">{"@type":"Product","sku":"C11702","offers":{"priceCurrency":"USD","price":0.0022,"inventoryLevel":2179700}}</script>
<script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"product":{"productCode":"C11702","productPriceList":[{"ladder":100,"usdPrice":0.0022,"currencySymbol":"$"},{"ladder":1000,"usdPrice":0.0017,"currencySymbol":"$"}]}}}}</script>
""";
var priceSnapshot = JlcPcbPricingService.ParseProductPage(pricePage, "C11702", DateTimeOffset.Parse("2026-09-25T00:00:00Z", CultureInfo.InvariantCulture));
Check(priceSnapshot is { Currency: "USD", UnitPrice: 0.0022m, MinimumQuantity: 100, Stock: 2179700 } &&
      priceSnapshot.PriceTiers.Count == 2 && priceSnapshot.PriceTiers[1].UnitPrice == 0.0017m,
    "public LCSC/JLCPCB price-page parser preserves stock and price tiers");

var classifier = new ComponentFamilyClassifier();
var familyResistor = new EdaComponent { LcscPartNumber = "C_FAMILY_R", Name = "RC0402", Description = "Thick film resistor" };
familyResistor.Properties["pre"] = "R?";
var familyFerrite = new EdaComponent { LcscPartNumber = "C_FAMILY_L", Name = "FB2012", Description = "Ferrite bead" };
familyFerrite.Properties["pre"] = "L?";
var familyMosfet = new EdaComponent { LcscPartNumber = "C_FAMILY_Q", Name = "AO3401A", Description = "P-channel MOSFET", FootprintName = "SOT-23-3" };
familyMosfet.Properties["pre"] = "Q?";
var familyUnknown = new EdaComponent { LcscPartNumber = "C_FAMILY_OTHER", Name = "Unclassified", Description = "Generic device" };
Check(classifier.Classify(familyResistor) is { Family: "Resistors", Confidence: FamilyConfidence.High } &&
      classifier.Classify(familyFerrite).Family == "Inductance" &&
      classifier.Classify(familyMosfet).Family == "Transistors" &&
      classifier.Classify(familyUnknown) is { Family: "Other", Confidence: FamilyConfidence.Low },
    "family classifier uses component metadata and puts uncertain parts in Other");
var familyPlanRoot = Path.Combine(AppContext.BaseDirectory, "family-plan-output");
if (Directory.Exists(familyPlanRoot)) Directory.Delete(familyPlanRoot, recursive: true);
var familyReference = Path.Combine(familyPlanRoot, "read-only-reference");
var familyPlan = FamilyLibraryExportPlan.Create(familyPlanRoot, [familyResistor, familyFerrite, familyMosfet, familyUnknown], familyReference);
await familyPlan.WriteAuditAsync([familyResistor, familyFerrite, familyMosfet, familyUnknown]);
Check(Directory.Exists(familyPlan.RunDirectory) && familyPlan.FamilyDirectory(familyResistor).EndsWith("Resistors") &&
      File.Exists(Path.Combine(familyPlan.RunDirectory, "family-classification.csv")) &&
      File.Exists(Path.Combine(familyPlan.RunDirectory, "import-summary.json")),
    "family export plan creates isolated audit-ready run directories");
var blockedReferenceOutput = false;
try { _ = FamilyLibraryExportPlan.Create(familyReference, [familyResistor], familyReference); }
catch (InvalidOperationException) { blockedReferenceOutput = true; }
Check(blockedReferenceOutput, "family export plan blocks writes inside the read-only reference library");
Check(!FamilyLibraryExportPlan.RequiresOrganizationChoice(5) && FamilyLibraryExportPlan.RequiresOrganizationChoice(6),
    "family organization picker threshold is exactly more than five resolved components");
foreach (var count in new[] { 1, 5, 6, 351 })
{
    var automatic = BatchLibraryOrganizationPicker.Resolve(count, true,
        _ => throw new Exception("Full import must never prompt for organization."));
    Check(automatic == BatchLibraryOrganization.FamilyLibraries,
        $"full import selects family libraries without prompting for {count} parts");
}
Check(BatchLibraryOrganizationPicker.Resolve(5, false,
    _ => throw new Exception("Small manual export must not prompt.")) == BatchLibraryOrganization.Normal,
    "small manual export retains normal-library output without prompting");
foreach (BatchLibraryOrganization? choice in new BatchLibraryOrganization?[]
    { BatchLibraryOrganization.Normal, BatchLibraryOrganization.FamilyLibraries, null })
{
    int calls = 0;
    var manual = BatchLibraryOrganizationPicker.Resolve(351, false, count =>
    {
        Check(count == 351, "manual organization prompt receives resolved component count");
        calls++; return choice;
    });
    Check(calls == 1 && manual == choice, "large manual export preserves normal/family/cancel selection");
}

if (args.Length > 0 && args[0] == "--inspect-symbol")
{
    foreach (var path in args.Skip(1))
    {
        var library = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(path);
        Console.WriteLine($"LIBRARY {path}");
        Console.WriteLine("HEADER " + string.Join(" ", (library.HeaderParameters ?? new()).Where(kv => kv.Key.Contains("color", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("sheet", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("font", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("size", StringComparison.OrdinalIgnoreCase)).Take(35).Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (var item in library.Components.OfType<SchComponent>().Where(item =>
                     item.Name is "C11702" or "C48618269" or "Inductor" || item.Name.Contains("Resistor", StringComparison.OrdinalIgnoreCase) ||
                     item.Name.EndsWith(" Diode", StringComparison.OrdinalIgnoreCase) ||
                     path.Contains("Resistors.SchLib", StringComparison.OrdinalIgnoreCase) || path.Contains("Diodes.SchLib", StringComparison.OrdinalIgnoreCase) ||
                     path.Contains("Capacitors.SchLib", StringComparison.OrdinalIgnoreCase) ||
                     path.Contains("Desktop\\Library", StringComparison.OrdinalIgnoreCase)).Take(3))
        {
            Console.WriteLine($"SYMBOL {item.Name}: pins={item.Pins.Count}, rectangles={item.Rectangles.Count}, polylines={item.Polylines.Count}, models={item.Implementations.Count}");
            Console.WriteLine($"  CURVES arcs={item.Arcs.Count} beziers={item.Beziers.Count} elliptical={item.EllipticalArcs.Count}");
            foreach (var arc in item.Arcs.OfType<SchArc>())
                Console.WriteLine($"  ARC center={arc.Center} radius={arc.Radius.ToMils()} mil width-index={arc.LineWidth}");
            foreach (var pin in item.Pins.Take(3))
                Console.WriteLine($"  PIN {pin.Designator} {pin.Name}: ({pin.Location.X.ToMm():F4},{pin.Location.Y.ToMm():F4}) length={pin.Length.ToMm():F4} orientation={pin.Orientation}");
            foreach (var rect in item.Rectangles.Take(2))
                Console.WriteLine($"  RECT ({rect.Corner1.X.ToMm():F4},{rect.Corner1.Y.ToMm():F4})-({rect.Corner2.X.ToMm():F4},{rect.Corner2.Y.ToMm():F4}) color={rect.Color} width={rect.LineWidth.ToMm():F4}");
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

if (args is ["--write-common-catalog", var sourceCatalog, var destinationCatalog])
{
    var retained = await UserSymbolLibraryResolver.WriteCommonCatalogAsync(sourceCatalog, destinationCatalog);
    Console.WriteLine($"Wrote verified common symbol catalog with {retained} symbols: {destinationCatalog}");
    return;
}

if (args is ["--write-common-catalog-from-directory", var sourceDirectory, var directoryCatalogDestination])
{
    var retained = await UserSymbolLibraryResolver.WriteCommonCatalogFromDirectoryAsync(sourceDirectory, directoryCatalogDestination);
    Console.WriteLine($"Created verified 35-symbol fallback catalog from source directory: {directoryCatalogDestination}");
    return;
}

if (args is ["--index-symbol-directory", var indexSourceDirectory, var indexDestination])
{
    await using var indexWriter = new StreamWriter(indexDestination, false);
    foreach (var path in Directory.EnumerateFiles(indexSourceDirectory, "*.*", SearchOption.AllDirectories)
                 .Where(path => string.Equals(Path.GetExtension(path), ".SchLib", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
    {
        try
        {
            var library = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(path);
            foreach (var symbol in library.Components.OfType<SchComponent>())
                await indexWriter.WriteLineAsync($"{symbol.Pins.Count}\t{symbol.Name}\t{symbol.DesignItemId}\t{symbol.Description}\t{path}");
        }
        catch (Exception exception)
        {
            await indexWriter.WriteLineAsync($"ERROR\t{exception.Message}\t\t\t{path}");
        }
    }
    Console.WriteLine($"Wrote source symbol index: {indexDestination}");
    return;
}

await BatchLookupTests.RunAsync();
await FamilyParallelTests.RunAsync();
await ModelPipelineTests.RunAsync();

const string source = """
{"result":{"title":"Synthetic two-unit part","description":"KiCad and Altium exporter test",
"dataStr":{"head":{"x":0,"y":0,"c_para":{"pre":"U?"}},"shape":[
"R~-2~-2~~~4~4~#880000~1~0~none~gge1~0",
"P~show~0~1~-20~0~180~gge2~0^^-20~0^^M -20 0 h 10~#8D2323^^0~-7~3~0~A~start~~~#8D2323"]},
"subparts":[{"dataStr":{"head":{"x":0,"y":0,"c_para":{"pre":"U?"}},"shape":[
"R~-2~-2~~~4~4~#880000~1~0~none~gge3~0",
"P~show~0~2~20~0~0~gge4~0^^20~0^^M 20 0 h -10~#8D2323^^0~7~3~0~B~end~~~#8D2323"]}}],
"packageDetail":{"title":"SYNTH_TEST_PAD","dataStr":{"head":{"x":100,"y":100},"shape":[
"PAD~OVAL~100~100~10~8~11~~1~2~~0~gge5~5~~Y",
"TRACK~1~3~~90 90 110 90~gge6~0",
"RECT~95~95~10~10~3~gge7~0~1~none",
"CIRCLE~100~100~2~0.5~3~gge8~0",
"ARC~1~3~~M 90 100 A 10 10 0 0 1 100 90~helper~gge10~0",
"SOLIDREGION~99~~M 90 90 L 110 90 L 110 110 L 90 110 Z~solid~gge9~~0"]}}}}
""";

var component = new Parser().Parse("C999999", source);
Check(component.FootprintName == "SYNTH_TEST_PAD" &&
    AltiumFootprintNaming.NameFor(component) == "SYNTH_TEST_PAD", "EasyEDA package title parsing");
Check(component.Pads.Count == 1 && Math.Abs(component.Pads[0].HoleMm - 1.016) < .0001,
    "EasyEDA drill radius must become millimetre diameter");
Check(component.SymbolUnits.Count == 2 && component.SymbolPins.Count == 2, "multi-unit symbol parsing");
Check(component.Shapes.Any(shape => shape.Kind == "SOLIDREGION" && shape.PointsMm.Count >= 4),
    "courtyard path parsing");
var parsedArc = component.Shapes.Single(shape => shape.Kind == "ARC");
Check(Math.Abs(parsedArc.PointsMm[0].X) < .0001 && Math.Abs(parsedArc.PointsMm[0].Y) < .0001 &&
      Math.Abs(parsedArc.RadiusMm - 2.54) < .0001 && Math.Abs(parsedArc.StartAngleDeg - 90) < .0001 &&
      Math.Abs(parsedArc.EndAngleDeg - 180) < .0001,
    "EasyEDA SVG arc parsing preserves the circular corner centre, radius, and flipped Altium angles");

// EasyEDA's BBox, rather than dataStr.head, is the footprint-local coordinate system.
// This mirrors the coordinate transform used by EasyEDALoader when it emits IPCB_Track
// primitives: centre on BBox and invert Y.  The deliberately different head location
// guards against a regression that displaces pads and their six silkscreen segments.
const string bboxOriginSource = """
{"result":{"title":"BBox-origin test","dataStr":{"head":{"x":900,"y":800},"shape":["R~-2~-2~~~4~4~#880000~1~0~none~gge0~0"]},
"packageDetail":{"title":"BBOX_ORIGIN_TEST","dataStr":{"head":{"x":900,"y":800},"BBox":{"x":900,"y":800,"width":200,"height":400},"shape":[
"PAD~RECT~1000~1000~10~8~1~~1~0~~0~gge1~0~~Y",
"TRACK~1~3~~900 800 1100 1200~gge2~0"]}}}}
""";
var bboxOriginComponent = new Parser().Parse("C_BBOX_ORIGIN", bboxOriginSource);
var bboxTrack = bboxOriginComponent.Shapes.Single(shape => shape.Kind == "TRACK");
Check(Math.Abs(bboxOriginComponent.Pads.Single().Xmm) < .0001 && Math.Abs(bboxOriginComponent.Pads.Single().Ymm) < .0001 &&
      Math.Abs(bboxTrack.PointsMm[0].X + 25.4) < .0001 && Math.Abs(bboxTrack.PointsMm[0].Y - 50.8) < .0001 &&
      Math.Abs(bboxTrack.PointsMm[1].X - 25.4) < .0001 && Math.Abs(bboxTrack.PointsMm[1].Y + 50.8) < .0001,
    "EasyEDA BBox centres pads and silkscreen tracks using one shared footprint origin");

const string symbolOnlySource = """
{"success":true,"result":{"title":"Symbol-only test part","description":"Provider has no footprint",
"dataStr":{"head":{"x":0,"y":0,"c_para":{"pre":"U?","Manufacturer":"Test Manufacturer","Manufacturer Part":"TEST-SYM-ONLY"}},"shape":[
"R~-20~-10~~~40~20~#880000~1~0~none~gge1~0",
"P~show~0~1~-30~0~180~gge2~0^^-30~0^^M -30 0 h 10~#8D2323^^0~-7~3~0~IN~~~#8D2323",
"P~show~0~2~30~0~0~gge3~0^^30~0^^M 30 0 h -10~#8D2323^^0~7~3~0~OUT~~~#8D2323"]}}}
""";
var symbolOnly = new Parser().Parse("C_SYMBOL_ONLY", symbolOnlySource);
Check(!symbolOnly.HasFootprintData && symbolOnly.SymbolPins.Count == 2 &&
      symbolOnly.Properties["Manufacturer"] == "Test Manufacturer",
    "symbol-only EasyEDA records retain schematic pins and metadata without a footprint");
var symbolOnlyOutput = Path.Combine(AppContext.BaseDirectory, "symbol-only-output");
if (Directory.Exists(symbolOnlyOutput)) Directory.Delete(symbolOnlyOutput, recursive: true);
await new LibraryExporter().ExportImportPlanAsync(symbolOnly, symbolOnlyOutput, include3d: false,
    format: LibraryExporter.OutputFormat.Altium, includeFootprint: false);
var symbolOnlySch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(Path.Combine(symbolOnlyOutput, "youeda.SchLib"));
var symbolOnlyWritten = (SchComponent)symbolOnlySch[AltiumSchExporter.LibraryComponentName(symbolOnly)]!;
Check(!File.Exists(Path.Combine(symbolOnlyOutput, "youeda.PcbLib")) &&
      !symbolOnlyWritten.Implementations.Any(model => model.ModelType == "PCBLIB") &&
      symbolOnlyWritten.Parameters.OfType<SchParameter>().Any(parameter => parameter.Name == "Manufacturer" && parameter.Value == "Test Manufacturer"),
    "Altium symbol-only export writes the schematic and all metadata without a footprint link");
var symbolOnlyKiCadOutput = Path.Combine(AppContext.BaseDirectory, "symbol-only-kicad-output");
if (Directory.Exists(symbolOnlyKiCadOutput)) Directory.Delete(symbolOnlyKiCadOutput, recursive: true);
await new LibraryExporter().ExportImportPlanAsync(symbolOnly, symbolOnlyKiCadOutput, include3d: false,
    format: LibraryExporter.OutputFormat.KiCad, includeFootprint: false);
Check(File.Exists(Path.Combine(symbolOnlyKiCadOutput, "youeda.kicad_sym")) &&
      !Directory.Exists(Path.Combine(symbolOnlyKiCadOutput, "youeda.pretty")),
    "KiCad symbol-only export writes a symbol library without a footprint directory");

var familyIntegrationOutput = Path.Combine(AppContext.BaseDirectory, "family-library-integration-output");
if (Directory.Exists(familyIntegrationOutput)) Directory.Delete(familyIntegrationOutput, recursive: true);
var familyIntegrationPlan = FamilyLibraryExportPlan.Create(familyIntegrationOutput, [component], Path.Combine(familyIntegrationOutput, "read-only-reference"));
await new LibraryExporter().ExportImportPlanAsync(component, familyIntegrationOutput, include3d: false,
    format: LibraryExporter.OutputFormat.Altium,
    additionalOutputDirectories: [familyIntegrationPlan.FamilyDirectory(component)]);
await familyIntegrationPlan.WriteAuditAsync([component]);
familyIntegrationPlan.FinalizeFamilyFileNames(LibraryExporter.OutputFormat.Altium);
var integrationFamilyDirectory = familyIntegrationPlan.FamilyDirectory(component);
var integrationSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(Path.Combine(integrationFamilyDirectory, "IC.SchLib"));
var integrationPcb = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(Path.Combine(integrationFamilyDirectory, "IC.PcbLib"));
var integrationSymbol = (SchComponent)integrationSch[AltiumSchExporter.LibraryComponentName(component)]!;
Check(integrationPcb.Contains(AltiumFootprintNaming.NameFor(component)) &&
      integrationSymbol.Parameters.OfType<SchParameter>().Any(parameter => parameter.Name == "LCSC Part" && parameter.Value == component.LcscPartNumber) &&
      File.Exists(Path.Combine(familyIntegrationPlan.RunDirectory, "family-classification.csv")),
    "family library export writes a reopenable paired native library and preserves the LCSC reference");

await BatchExportTests.RunAsync(component, symbolOnly);

var output = Path.Combine(AppContext.BaseDirectory, "smoke-output");
var reportedUserSchLib = @"C:\Users\you38\Documents\EasyEdaAltium\youeda.SchLib";
if (File.Exists(reportedUserSchLib))
{
    var reportedLibrary = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(reportedUserSchLib);
    Console.WriteLine($"Reported user SchLib: {reportedLibrary.Count} component(s); diagnostics: {string.Join(" | ", reportedLibrary.Diagnostics.Select(item => item.Message))}");
    Console.WriteLine($"Reported user SchLib names: {string.Join(" | ", reportedLibrary.Components.Select(component => component.Name))}");
    AltiumSchExporter.MigrateUnsafeLibraryNames(reportedLibrary);
    var repairedReportedLibrary = Path.Combine(AppContext.BaseDirectory, "reported-library-repaired.SchLib");
    await reportedLibrary.SaveAsync(repairedReportedLibrary);
    var reopenedReportedLibrary = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(repairedReportedLibrary);
    Check(reopenedReportedLibrary.Count == reportedLibrary.Count && reopenedReportedLibrary.Components.All(component =>
        System.Text.RegularExpressions.Regex.IsMatch(component.Name ?? string.Empty, @"^[A-Za-z0-9_+\-.]{1,120}$")),
        "affected user SchLib migrates to Altium-safe component record names without data loss");
}
var settingsPath = Path.Combine(output, "settings-" + Guid.NewGuid().ToString("N") + ".json");
var settingsStore = new AppSettingsStore(settingsPath);
Check(settingsStore.Load().ExportFormat == "Altium", "Altium is the first-run export-format default");
settingsStore.Save(new AppUserSettings("KiCad", null, @"C:\AltiumOutput", @"C:\KiCadOutput", "Warm Studio"));
var restoredSettings = settingsStore.Load();
Check(restoredSettings.ExportFormat == "KiCad" &&
    restoredSettings.AltiumOutputDirectory == @"C:\AltiumOutput" &&
    restoredSettings.KiCadOutputDirectory == @"C:\KiCadOutput",
    "desktop exporter tabs and their independent settings persist across sessions");
Check(restoredSettings.Theme == "Warm Studio", "selected application skin persists across sessions");
var metadataSource = new EdaComponent
{
    LcscPartNumber = "C_METADATA", Name = "LMR33630", Description = "Synchronous buck regulator",
    FootprintName = "SOT-23-6"
};
metadataSource.Properties["Manufacturer"] = "Texas Instruments";
metadataSource.Properties["Manufacturer Part"] = "LMR33630ADDCR";
metadataSource.Properties["Datasheet"] = "https://example.test/lmr33630";
metadataSource.Properties["Voltage Rating"] = "36V";
metadataSource.Properties["Tolerance"] = "±1%";
metadataSource.Properties["Dielectric"] = "X7R";
metadataSource.Properties["pre"] = "U?";
metadataSource.Tags.AddRange(["Power Management", "Buck Regulator"]);
var metadataSymbol = AltiumSchExporter.CreateEasyEdaSymbol(metadataSource);
AltiumSchExporter.PrepareSymbol(metadataSymbol, metadataSource);
Check(metadataSymbol.DesignItemId == "LMR33630" &&
    metadataSymbol.Description == "Synchronous buck regulator · SOT-23-6 · 36 V · ±1% · X7R · Texas Instruments" &&
    metadataSymbol.Parameters.OfType<SchParameter>().Any(parameter => parameter.Name == "LCSC Part" && parameter.Value == "C_METADATA" && !parameter.IsVisible) &&
    metadataSymbol.Parameters.OfType<SchParameter>().Any(parameter => parameter.Name == "Voltage Rating" && parameter.Value == "36V" && !parameter.IsVisible) &&
    metadataSymbol.Parameters.OfType<SchParameter>().Any(parameter => parameter.Name == "Tags" && parameter.Value.Contains("Power Management") && !parameter.IsVisible),
    "consistent readable identity and all EasyEDA metadata parameters");
var resistorMetadata = new EdaComponent { LcscPartNumber = "C_RES", Name = "RC0402FR-0710KL", Description = "SMT Resistor", FootprintName = "0402" };
resistorMetadata.Properties["pre"] = "R?";
resistorMetadata.Properties["Manufacturer"] = "UNI-ROYAL(??)";
resistorMetadata.Properties["Resistance"] = "10 kΩ";
resistorMetadata.Properties["Voltage Rating"] = "50 V";
resistorMetadata.Properties["Tolerance"] = "±1%";
resistorMetadata.Properties["Technology"] = "Thick Film";
var resistorMetadataSymbol = AltiumSchExporter.CreateEasyEdaSymbol(resistorMetadata);
AltiumSchExporter.PrepareSymbol(resistorMetadataSymbol, resistorMetadata);
Check(resistorMetadataSymbol.DesignItemId == "RC0402FR-0710KL" &&
    resistorMetadataSymbol.Description == "Resistor · 0402 · 10 kΩ · 50 V · ±1% · Thick Film · UNI-ROYAL",
    "all component categories use the same compact description convention");
var screenshotResistor = new EdaComponent { LcscPartNumber = "C_SCREEN_R", Name = "0402WGF2003TCE", Description = "SMT Resistor", FootprintName = "R0402" };
screenshotResistor.Properties["pre"] = "R?";
screenshotResistor.Properties["Manufacturer"] = "UNI-ROYAL(??)";
screenshotResistor.Properties["Resistance"] = "200kΩ";
var screenshotResistorSymbol = AltiumSchExporter.CreateEasyEdaSymbol(screenshotResistor);
AltiumSchExporter.PrepareSymbol(screenshotResistorSymbol, screenshotResistor);
Check(screenshotResistorSymbol.Description == "Resistor · 0402 · 200 kΩ · UNI-ROYAL",
    "resistor descriptions normalize package, engineering unit, and supplier placeholders");
var screenshotCapacitor = new EdaComponent { LcscPartNumber = "C_SCREEN_C", Name = "CL31A226KBHNNNE", Description = "SMT Capacitor", FootprintName = "C1206" };
screenshotCapacitor.Properties["pre"] = "C?";
screenshotCapacitor.Properties["Manufacturer"] = "SAMSUNG(??)";
screenshotCapacitor.Properties["Capacitance"] = "2.2uF";
var screenshotCapacitorSymbol = AltiumSchExporter.CreateEasyEdaSymbol(screenshotCapacitor);
AltiumSchExporter.PrepareSymbol(screenshotCapacitorSymbol, screenshotCapacitor);
Check(screenshotCapacitorSymbol.Description == "Capacitor · 1206 · 2.2 µF · SAMSUNG",
    "capacitor descriptions use the same cleaned display convention");
var exactCapacitor = new EdaComponent { LcscPartNumber = "C_CAP_EXACT", Name = "CC0402JRNPO9BN390", FootprintName = "C0402" };
exactCapacitor.Properties["pre"] = "C?"; exactCapacitor.Properties["Capacitance"] = "39pF"; exactCapacitor.Properties["Voltage Rating"] = "50V";
exactCapacitor.Properties["Tolerance"] = "±5%"; exactCapacitor.Properties["Dielectric"] = "C0G/NP0"; exactCapacitor.Properties["Manufacturer"] = "YAGEO";
var exactCapacitorSymbol = AltiumSchExporter.CreateEasyEdaSymbol(exactCapacitor); AltiumSchExporter.PrepareSymbol(exactCapacitorSymbol, exactCapacitor);
Check(exactCapacitorSymbol.Description == "Capacitor · 0402 · 39 pF · 50 V · ±5% · C0G/NP0 · YAGEO", "accepted capacitor description format");
var exactInductor = new EdaComponent { LcscPartNumber = "C_IND_EXACT", Name = "LQG18HN100M00", FootprintName = "L0603" };
exactInductor.Properties["pre"] = "L?"; exactInductor.Properties["Inductance"] = "10uH"; exactInductor.Properties["Current Rating"] = "1A";
exactInductor.Properties["Tolerance"] = "±20%"; exactInductor.Properties["Material"] = "Ferrite"; exactInductor.Properties["Manufacturer"] = "Murata";
exactInductor.SymbolPins.Add(new EdaSymbolPin("1", "1", 0, 180, "start"));
exactInductor.SymbolPins.Add(new EdaSymbolPin("2", "2", 0, 0, "end"));
var exactInductorSymbol = AltiumSchExporter.CreateEasyEdaSymbol(exactInductor); AltiumSchExporter.PrepareSymbol(exactInductorSymbol, exactInductor);
Check(exactInductorSymbol.Description == "Inductor · 0603 · 10 µH · 1 A · ±20% · Ferrite · Murata", "accepted inductor description format");
Check(exactInductorSymbol.Pins.Count == 2 && exactInductorSymbol.Arcs.Count == 4 && exactInductorSymbol.Lines.Count == 2 && exactInductorSymbol.Rectangles.Count == 0 &&
      exactInductorSymbol.Parameters.OfType<SchParameter>().Any(parameter => parameter.Name == "Designator" && parameter.OwnerPartId == 0) &&
      exactInductorSymbol.Parameters.OfType<SchParameter>().Any(parameter => parameter.Name == "Comment" && parameter.Value == "=Value" && parameter.OwnerPartId == 0) &&
      exactInductorSymbol.Arcs.All(arc => Math.Abs(arc.LineWidth.ToMm() - Coord.FromMils(2).ToMm()) < .001) &&
      exactInductorSymbol.Lines.All(line => Math.Abs(line.Width.ToMm() - Coord.FromMils(2).ToMm()) < .001) &&
      exactInductorSymbol.Pins.All(pin => Math.Abs(pin.Length.ToMm() - 2.54) < .001),
    "inductors use the complete standard two-pin coil, visible labels, and Altium Small-width core bars rather than a bundled-artwork pin block");
await File.WriteAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "standard-inductor.png"),
    SchematicSymbolPreviewRenderer.RenderPng(exactInductorSymbol, 800, 500));
var exactDiode = new EdaComponent { LcscPartNumber = "C_DIODE_EXACT", Name = "B5819W", FootprintName = "SOD-123" };
exactDiode.Properties["pre"] = "D?"; exactDiode.Properties["Voltage Rating"] = "100V"; exactDiode.Properties["Current Rating"] = "1A";
exactDiode.Properties["Tolerance"] = "±5%"; exactDiode.Properties["Technology"] = "Schottky"; exactDiode.Properties["Manufacturer"] = "Diodes Inc.";
var exactDiodeSymbol = AltiumSchExporter.CreateEasyEdaSymbol(exactDiode); AltiumSchExporter.PrepareSymbol(exactDiodeSymbol, exactDiode);
Check(exactDiodeSymbol.Description == "Diode · SOD-123 · 100 V · 1 A · ±5% · Schottky · Diodes Inc.", "accepted diode description format");
var unsafeNameSource = new EdaComponent { LcscPartNumber = "C_SAFE_NAME", Name = "SM04B-SRSS-TB (LF)(SN)", FootprintName = "CONN" };
Check(AltiumSchExporter.LibraryComponentName(unsafeNameSource) == "SM04B-SRSS-TB_LF_SN",
    "Altium library component names preserve the MPN while removing index-breaking characters");
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
    .Contains(Path.GetFullPath(Path.Combine(output, "youeda.3dshapes", "C999999.step")).Replace('\\', '/')), "KiCad installed-library STEP link");
var altiumFile = await new AltiumV2Exporter().UpsertPcbLibAsync(component, output);
var reopened = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(altiumFile);
var altiumComponent = reopened.Components.Single(item => item.Name == component.FootprintName);
Check(!reopened.Contains(component.LcscPartNumber), "Altium PcbLib uses the EasyEDA footprint name");
component.FootprintName = "";
await new AltiumV2Exporter().UpsertPcbLibAsync(component, output);
component.FootprintName = "SYNTH_TEST_PAD";
await new AltiumV2Exporter().UpsertPcbLibAsync(component, output);
reopened = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(altiumFile);
Check(reopened.Components.Count(item => item.Name == component.FootprintName) == 1 &&
    !reopened.Contains(component.LcscPartNumber), "re-import migrates legacy LCSC footprint name");
var pad = (PcbPad)altiumComponent.Pads.Single();
Check(Math.Abs(pad.HoleSize.ToMm() - 1.016) < .001 && pad.HoleType == PadHoleType.Slot &&
    Math.Abs(Coord.FromRaw(pad.HoleSlotLength).ToMm() - 1.27) < .001, "Altium drilled pad round-trip");
Check(altiumComponent.Tracks.Count == 5 && altiumComponent.Arcs.Count == 2 && altiumComponent.Regions.Count == 0,
    $"Altium preserves source track, arc, rectangle, and circle artwork but omits editor-only ComponentShape outlines (tracks={altiumComponent.Tracks.Count}, arcs={altiumComponent.Arcs.Count}, regions={altiumComponent.Regions.Count})");
component.Tags.Add("Crystal");
var catalog = Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib");
var resolver = new UserSymbolLibraryResolver();
var cleanupOutput = Path.Combine(AppContext.BaseDirectory, "cleanup-output");
await new LibraryExporter().ExportImportPlanAsync(component, cleanupOutput, include3d: false, userSymbolDirectory: catalog);
Check(File.Exists(Path.Combine(cleanupOutput, "youeda.PcbLib")) &&
    File.Exists(Path.Combine(cleanupOutput, "youeda.SchLib")) &&
    !Directory.Exists(Path.Combine(cleanupOutput, component.LcscPartNumber)),
    "successful import removes its per-component staging folder");
var match = resolver.Resolve(component, catalog, null);
Check(match is null, "adding a Crystal tag cannot flatten the earlier multi-unit synthetic component into a passive crystal");
var twoTerminalCrystal = new EdaComponent { LcscPartNumber = "C_CRYSTAL_TEST", Name = "PassiveQuartz" };
twoTerminalCrystal.Tags.Add("Crystals");
twoTerminalCrystal.SymbolPins.Add(new("1", "1", 0, 0, ""));
twoTerminalCrystal.SymbolPins.Add(new("2", "2", 0, 180, ""));
match = resolver.Resolve(twoTerminalCrystal, catalog, null);
Check(match?.CommonPins?.Kind == "two-terminal crystal", "automatic two-terminal crystal match uses a real single-unit passive fixture");
var commonBundledSymbols = resolver.ListMasterSymbols(catalog);
Check(commonBundledSymbols.Count == 71, "bundled picker contains 71 common symbols including four-pin switch");
Check(commonBundledSymbols.All(item =>
        resolver.LoadPreviewComponent(item).Pins.Count <= UserSymbolLibraryResolver.CommonSymbolMaximumPins),
    "bundled picker lists only common six-pin-or-smaller symbols");
var personalSymbolRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Library");
if (Directory.Exists(personalSymbolRoot))
{
    var nativeInductor = new EdaComponent { LcscPartNumber = "C436585", Name = "PSPMAQ0605H-470M-ANP", FootprintName = "IND-SMD_L7.1-W6.6" };
    nativeInductor.Properties["Manufacturer Part"] = "PSPMAQ0605H-470M-ANP";
    nativeInductor.SymbolPins.Add(new("1", "1", 0, 0, ""));
    nativeInductor.SymbolPins.Add(new("2", "2", 0, 180, ""));
    var nativeMatch = resolver.Resolve(nativeInductor, string.Join(Path.PathSeparator, personalSymbolRoot, catalog), null);
    Check(nativeMatch?.MatchKind == "native source exact match" && nativeMatch.ComponentName == nativeInductor.Name,
        "exact personal native symbol wins before the common fallback catalog");
}
var minimalCapacitor = new EdaComponent { LcscPartNumber = "C_MINIMAL_CAP", Name = "MLCC", FootprintName = "C0402" };
minimalCapacitor.Properties["pre"] = "C?";
var minimalResistor = new EdaComponent { LcscPartNumber = "C_MINIMAL_RES", Name = "CHIP", FootprintName = "R0402" };
minimalResistor.Properties["pre"] = "R?";
Check(resolver.Resolve(minimalCapacitor, catalog, null) is null && resolver.Resolve(minimalResistor, catalog, null) is null,
    "missing schematic and pad terminals do not authorize an unverified generic symbol");
minimalCapacitor.Pads.Add(new EdaPad("1", -.5, 0, .5, .7, 0, "1", true, 0));
minimalCapacitor.Pads.Add(new EdaPad("2", .5, 0, .5, .7, 0, "1", true, 0));
minimalResistor.Pads.Add(new EdaPad("1", -.5, 0, .5, .7, 0, "1", true, 0));
minimalResistor.Pads.Add(new EdaPad("2", .5, 0, .5, .7, 0, "1", true, 0));
Check(resolver.Resolve(minimalCapacitor, catalog, null)?.ComponentName == "Capacitor" &&
    resolver.Resolve(minimalResistor, catalog, null)?.ComponentName == "Resistor",
    "sparse passive records reuse native artwork when footprint terminal numbers prove compatibility");
var sparsePassiveOutput = Path.Combine(output, "sparse-passive-output");
await new LibraryExporter().ExportImportPlanAsync(minimalCapacitor, sparsePassiveOutput, include3d: false, userSymbolDirectory: catalog);
await new LibraryExporter().ExportImportPlanAsync(minimalResistor, sparsePassiveOutput, include3d: false, userSymbolDirectory: catalog);
var sparsePassiveLibrary = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(Path.Combine(sparsePassiveOutput, "youeda.SchLib"));
Check(sparsePassiveLibrary.Contains(AltiumSchExporter.LibraryComponentName(minimalCapacitor)) &&
    sparsePassiveLibrary.Contains(AltiumSchExporter.LibraryComponentName(minimalResistor)),
    "sparse capacitor and resistor payloads are both written instead of being skipped");
var cleanedCatalog = Path.Combine(output, "BundledUserSymbols-cleaned.SchLib");
Check(await UserSymbolLibraryResolver.WriteCommonCatalogAsync(catalog, cleanedCatalog) == 71,
    "physically cleaned bundled catalog reopens with exactly 71 symbols");
var previewMatch = commonBundledSymbols.First(item => item.ComponentName == "Resistor");
var bundledPreview = resolver.LoadPreviewComponent(previewMatch);
var fallbackPreview = AltiumSchExporter.CreateEasyEdaSymbol(component);
var bundledPng = SchematicSymbolPreviewRenderer.RenderPng(bundledPreview);
var fallbackPng = SchematicSymbolPreviewRenderer.RenderPng(fallbackPreview);
Check(bundledPreview.Pins.Count == 2 && IsPng(bundledPng),
    "selected bundled symbol preview renders as PNG");
Check(fallbackPreview.Pins.Count == component.SymbolPins.Count &&
    IsPng(fallbackPng),
    "EasyEDA fallback symbol preview renders as PNG");
File.WriteAllBytes(Path.Combine(output, "bundled-symbol-preview.png"), bundledPng);
File.WriteAllBytes(Path.Combine(output, "easyeda-fallback-preview.png"), fallbackPng);
var inductorMatch = commonBundledSymbols.Single(item => item.ComponentName == "Inductor");
var inductorPreview = resolver.LoadPreviewComponent(inductorMatch);
var inductorPng = SchematicSymbolPreviewRenderer.RenderPng(inductorPreview);
Check(inductorPreview.Pins.Count == 2 && inductorPreview.EllipticalArcs.Count == 4 && inductorPreview.Arcs.Count == 0 && IsPng(inductorPng), "bundled inductor preview retains the four native elliptical coils");
File.WriteAllBytes(Path.Combine(output, "bundled-inductor-preview.png"), inductorPng);
var genericOutput = Path.Combine(AppContext.BaseDirectory, "generic-output");
await writer.UpsertAsync(component, genericOutput, resolver.LoadSelectedComponent(match!));
Check(File.ReadAllText(Path.Combine(genericOutput, "youeda.kicad_sym")).Contains("(xy -2.54 0)"),
    "bundled Altium graphic scale translation");
// Native EasyEDA C11702 / 0402WGF1001TCE geometry, verified against the public
// EasyEDA library: pad centres ±1.704 (0.432816 mm), pads 2.227 × 2.126 (0.565658 × 0.540004 mm).
var resistorSource = new EdaComponent { LcscPartNumber = "C11702", Name = "0402WGF1001TCE", Description = "1KΩ ±1%", FootprintName = "R0402_EASYEDA" };
resistorSource.Properties["Value"] = "1kΩ";
resistorSource.Properties["pre"] = "R?";
resistorSource.Pads.Add(new EdaPad("1", -0.432816, 0, 0.565658, 0.540004, 0, "1", true, 0));
resistorSource.Pads.Add(new EdaPad("2", 0.432816, 0, 0.565658, 0.540004, 0, "1", true, 0));
resistorSource.Shapes.Add(new EdaShape("TRACK", "3", [(-.226212, -.498602), (-.944262, -.498602), (-.944262, .498602), (-.226212, .498602)], .1524));
resistorSource.Shapes.Add(new EdaShape("SOLIDREGION", "5", [(.135001, -.229997), (.135001, .229997), (.72263, .269999), (.72263, -.269999), (.135001, -.229997)], 0));
resistorSource.Shapes.Add(new EdaShape("CIRCLE", "101", [(-.499872, -.249936), (.029972, 0)], .060005));
var bundled = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(catalog);
var resistor = (SchComponent)bundled["Resistor"]!;
resistor.Name = resistorSource.LcscPartNumber;
resistor.LibReference = resistorSource.LcscPartNumber;
AltiumSchExporter.PrepareSymbol(resistor, resistorSource);
var schExporter = new AltiumSchExporter();
var schPath = await schExporter.UpsertSymbolAsync(resistor, output, legacyLcscName: resistorSource.LcscPartNumber);
var writtenSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(schPath);
var resistorLibraryName = AltiumSchExporter.LibraryComponentName(resistorSource);
var writtenResistor = (SchComponent)writtenSch[resistorLibraryName]!;
Check(!writtenSch.Contains(resistorSource.LcscPartNumber), "re-export migrates the old LCSC-named schematic record");
Check(writtenResistor.Implementations.Count == 1 && writtenResistor.Implementations[0].ModelType == "PCBLIB" &&
    writtenResistor.Implementations[0].ModelName == resistorSource.FootprintName, "native Altium footprint model link");
await new AltiumV2Exporter().UpsertPcbLibAsync(resistorSource, output);
var writtenPcb = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(Path.Combine(output, "youeda.PcbLib"));
var written0402 = writtenPcb.Components.OfType<PcbComponent>().Single(item => item.Name == resistorSource.FootprintName);
var resistorPads = written0402.Pads.OfType<PcbPad>().OrderBy(pad => pad.Designator).ToArray();
Check(resistorPads.Length == 2 && Math.Abs(resistorPads[0].Location.X.ToMm() + .432816) < .001 &&
      Math.Abs(resistorPads[1].Location.X.ToMm() - .432816) < .001 &&
      resistorPads.All(pad => Math.Abs(pad.SizeTop.X.ToMm() - .565658) < .001 && Math.Abs(pad.SizeTop.Y.ToMm() - .540004) < .001) &&
      written0402.Tracks.Count == 3 && written0402.Regions.Count == 0 && written0402.Arcs.Count == 1,
    "0402 resistors preserve EasyEDA pads, silkscreen tracks, and marking artwork without filled editor regions");
Check(writtenResistor.Parameters.OfType<SchParameter>().Any(p => p.Name == "Value" && p.Value == "1kΩ" && !p.IsVisible) &&
    writtenResistor.Parameters.OfType<SchParameter>().Any(p => p.Name == "Comment" && p.Value == "=Value" && p.IsVisible),
    "passive value and visible Altium comment expression");
Check(writtenSch.HeaderParameters?.Any(kv => kv.Key == "AreaColor" && kv.Value == "16317695") == true,
    "light Altium schematic canvas");
await schExporter.UpsertSymbolAsync(writtenResistor, output);
writtenSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(schPath);
Check(((SchComponent)writtenSch[resistorLibraryName]!).Implementations.Count == 1, "idempotent native footprint link");
var icSource = new EdaComponent { LcscPartNumber = "C48618269", Name = "TPA132A2Q-SO1R-S", FootprintName = "SOP-8_TEST" };
var leftNames = new[] { "IN-", "GND", "VREF2", "NC" };
var rightNames = new[] { "OUT", "VS", "VREF1", "IN+" };
for (var index = 0; index < 4; index++)
{
    icSource.SymbolPins.Add(new EdaSymbolPin((index + 1).ToString(), leftNames[index], 0, 180, "start"));
    icSource.SymbolPins.Add(new EdaSymbolPin((index + 5).ToString(), rightNames[index], 0, 0, "end"));
}
Check(resolver.MustGenerateFromEasyEda(icSource) && resolver.Resolve(icSource, catalog, null) is null,
    "seven-or-more-pin components bypass bundled matching and use EasyEDA fallback");
await schExporter.UpsertEasyEdaSymbolAsync(icSource, output);
writtenSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(schPath);
var writtenIc = (SchComponent)writtenSch[AltiumSchExporter.LibraryComponentName(icSource)]!;
File.WriteAllBytes(Path.Combine(output, "ic-fallback-preview.png"),
    SchematicSymbolPreviewRenderer.RenderPng(writtenIc));
var icBody = writtenIc.Rectangles.OfType<SchRectangle>().Single();
Check(icBody.Color == 128 && Math.Abs(icBody.LineWidth.ToMils() - 1) < 0.001 &&
    icBody.IsFilled && icBody.IsTransparent && icBody.FillColor == 11862015 &&
    icBody.OwnerPartId == 1 && icBody.IsNotAccessible &&
    Math.Abs(icBody.Corner1.X.ToMm() + 12.7) < 0.001 &&
    Math.Abs(icBody.Corner2.X.ToMm() - 12.7) < 0.001,
    "EasyEDA fallback IC has a compact, visible filled body");
Check(writtenIc.Pins.OfType<SchPin>().Where(pin => int.Parse(pin.Designator!) <= 4)
        .All(pin => pin.Orientation == OriginalCircuit.Eda.Enums.PinOrientation.Left &&
                    Math.Abs(pin.Location.X.ToMm() + 12.7) < 0.001) &&
    writtenIc.Pins.OfType<SchPin>().Where(pin => int.Parse(pin.Designator!) >= 5)
        .All(pin => pin.Orientation == OriginalCircuit.Eda.Enums.PinOrientation.Right &&
                    Math.Abs(pin.Location.X.ToMm() - 12.7) < 0.001),
    "EasyEDA fallback pins point outward from the body");
Check(writtenIc.Parameters.OfType<SchParameter>().Any(parameter =>
        parameter.Name == "Designator" && parameter.Location.Y.ToMm() > icBody.Corner2.Y.ToMm()) &&
    writtenIc.Parameters.OfType<SchParameter>().Any(parameter =>
        parameter.Name == "Comment" && parameter.Location.Y.ToMm() < icBody.Corner1.Y.ToMm()),
    "EasyEDA fallback labels sit outside the body");
var illustratedSource = new EdaComponent { LcscPartNumber = "C74192", Name = "XL1509-ADJE1" };
illustratedSource.SymbolPins.Add(new EdaSymbolPin("1", "VIN", 0, 180, "start") { Xmm = -12.7, Ymm = 0, LengthMm = 2.54 });
illustratedSource.SymbolPins.Add(new EdaSymbolPin("5", "GND", 0, 0, "end") { Xmm = 12.7, Ymm = 0, LengthMm = 2.54 });
var illustratedUnit = new EdaSymbolUnit();
illustratedUnit.Graphics.Add(new EdaSymbolGraphic("RECT", [(-10.16, -6.858), (10.16, 6.858)], .254, false));
illustratedUnit.Graphics.Add(new EdaSymbolGraphic("ELLIPSE", [(-8.89, 5.588), (.381, .381)], .254, true));
illustratedSource.SymbolUnits.Add(illustratedUnit);
var illustratedFallback = AltiumSchExporter.CreateEasyEdaSymbol(illustratedSource);
var illustratedBody = illustratedFallback.Rectangles.OfType<SchRectangle>().Single();
Check(Math.Abs(illustratedBody.Corner1.X.ToMm() + 12.7) < .001 &&
    Math.Abs(illustratedBody.Corner2.Y.ToMm() - 3.81) < .001 &&
    Math.Abs(illustratedBody.LineWidth.ToMils() - 1) < .001 &&
    illustratedBody.IsFilled && illustratedBody.IsTransparent && illustratedBody.FillColor == 11862015 &&
    illustratedFallback.Ellipses.Count == 0,
    "EasyEDA fallback uses a readable filled body even when source artwork is present");
Check(illustratedFallback.Pins.OfType<SchPin>().Any(pin => pin.Designator == "1" &&
        Math.Abs(pin.Location.X.ToMm() + 12.7) < .001 && pin.Orientation == OriginalCircuit.Eda.Enums.PinOrientation.Left) &&
    illustratedFallback.Pins.OfType<SchPin>().Any(pin => pin.Designator == "5" &&
        Math.Abs(pin.Location.X.ToMm() - 12.7) < .001 && pin.Orientation == OriginalCircuit.Eda.Enums.PinOrientation.Right),
    "EasyEDA fallback preserves source pin sides and identifiers");
var diodeSource = new EdaComponent { LcscPartNumber = "C2891778", Name = "BZT52C10", Description = "10 V Zener", FootprintName = "SOD-123_EASYEDA" };
diodeSource.Properties["Manufacturer Part"] = "BZT52C10";
var diode = (SchComponent)bundled["Diode"]!;
File.WriteAllBytes(Path.Combine(output, "diode-symbol-preview.png"), SchematicSymbolPreviewRenderer.RenderPng(diode));
Check(diode.Pins.Count == 2 && diode.Pins.Select(pin => pin.Designator).OrderBy(value => value)
        .SequenceEqual(new[] { "1", "2" }),
    "source rectifier symbol preserves its two Altium pin designators");
var diodePinNumbers = diode.Pins.Select(pin => pin.Designator).ToArray();
diode.Name = diodeSource.LcscPartNumber;
diode.LibReference = diodeSource.LcscPartNumber;
AltiumSchExporter.PrepareSymbol(diode, diodeSource);
await schExporter.UpsertSymbolAsync(diode, output);
writtenSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(schPath);
var writtenDiode = (SchComponent)writtenSch[AltiumSchExporter.LibraryComponentName(diodeSource)]!;
Check(writtenDiode.Pins.Select(pin => pin.Designator).SequenceEqual(diodePinNumbers), "diode pins preserve template numbering");
Check(writtenDiode.Parameters.OfType<SchParameter>().Any(p => p.Name == "Manufacturer Part" && p.Value == "BZT52C10" && !p.IsVisible) &&
    writtenDiode.Parameters.OfType<SchParameter>().Any(p => p.Name == "Comment" && p.Value == "=Manufacturer Part" && p.IsVisible),
    "diode manufacturer-part comment expression");
Check(writtenDiode.Implementations.Any(model => model.ModelType == "PCBLIB" && model.ModelName == diodeSource.FootprintName),
    "diode native footprint model link");
var capacitorSource = new EdaComponent { LcscPartNumber = "C999998", Name = "CC0402", Description = "10 uF capacitor", FootprintName = "C0402_EASYEDA" };
capacitorSource.Properties["Value"] = "10uF";
capacitorSource.Properties["pre"] = "C?";
capacitorSource.Pads.Add(new EdaPad("1", -.4, 0, .5, .5, 0, "1", true, 0));
capacitorSource.Pads.Add(new EdaPad("2", .4, 0, .5, .5, 0, "1", true, 0));
var capacitor = (SchComponent)bundled["Capacitor"]!;
File.WriteAllBytes(Path.Combine(output, "capacitor-symbol-preview.png"), SchematicSymbolPreviewRenderer.RenderPng(capacitor));
capacitor.Name = capacitorSource.LcscPartNumber;
capacitor.LibReference = capacitorSource.LcscPartNumber;
AltiumSchExporter.PrepareSymbol(capacitor, capacitorSource);
await schExporter.UpsertSymbolAsync(capacitor, output);
writtenSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(schPath);
var capacitorLibraryName = AltiumSchExporter.LibraryComponentName(capacitorSource);
var writtenCapacitor = (SchComponent)writtenSch[capacitorLibraryName]!;
Check(writtenCapacitor.Parameters.OfType<SchParameter>().Any(p => p.Name == "Value" && p.Value == "10uF" && !p.IsVisible) &&
    writtenCapacitor.Parameters.OfType<SchParameter>().Any(p => p.Name == "Comment" && p.Value == "=Value" && p.IsVisible),
    "capacitor value/comment convention");
Check(writtenCapacitor.Implementations.Any(model => model.ModelType == "PCBLIB" && model.ModelName == AltiumFootprintNaming.NameFor(capacitorSource)),
    "capacitor native footprint model link");
await new AltiumV2Exporter().UpsertPcbLibAsync(capacitorSource, output);
writtenPcb = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(Path.Combine(output, "youeda.PcbLib"));
var writtenCap0402 = writtenPcb.Components.OfType<PcbComponent>().Single(item => item.Name == capacitorSource.FootprintName);
var capacitorPads = writtenCap0402.Pads.OfType<PcbPad>().OrderBy(pad => pad.Designator).ToArray();
Check(capacitorPads.Length == 2 && Math.Abs(capacitorPads[0].Location.X.ToMm() + .4) < .001 &&
      Math.Abs(capacitorPads[1].Location.X.ToMm() - .4) < .001 &&
      capacitorPads.All(pad => Math.Abs(pad.SizeTop.X.ToMm() - .5) < .001 && Math.Abs(pad.SizeTop.Y.ToMm() - .5) < .001) &&
      writtenCap0402.Tracks.Count == 0,
    "0402 capacitors preserve the EasyEDA-provided pad geometry without normalizing it");
Console.WriteLine($"PASS: parser, KiCad/Altium footprints, resistor/diode/capacitor labels and model links; {output}; common bundled symbols: {commonBundledSymbols.Count}");


static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
}

static bool IsPng(byte[] bytes) => bytes.Length > 1000 &&
    bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71;
