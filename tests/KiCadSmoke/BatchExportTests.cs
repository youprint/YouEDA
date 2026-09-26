using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Sch;
using OriginalCircuit.Altium.Models.Pcb;

internal static class BatchExportTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + message);
        Console.WriteLine("PASS: " + message);
    }
    public static async Task RunAsync(EdaComponent component, EdaComponent symbolOnly)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "batch-export-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "batch");
        var baseline = Path.Combine(root, "baseline");
        var exporter = new LibraryExporter();
        var batch = new AltiumExportBatch();
        var family = FamilyLibraryExportPlan.Create(directory, [component], Path.Combine(root, "reference"));
        await exporter.ExportImportPlanAsync(symbolOnly, directory, include3d: false, includeFootprint: false, batch: batch);
        Check(!File.Exists(Path.Combine(directory, "youeda.SchLib")), "batch conversion defers full file saving to checkpoint");
        await batch.CheckpointAsync();
        Check(!File.Exists(Path.Combine(directory, "youeda.PcbLib")), "symbol-only batch does not create an empty PCB library");
        await exporter.ExportImportPlanAsync(component, directory, include3d: false, batch: batch,
            additionalOutputDirectories: [family.FamilyDirectory(component)]);
        await exporter.ExportImportPlanAsync(component, baseline, include3d: false);
        await batch.CheckpointAsync();
        await exporter.ExportImportPlanAsync(component, directory, include3d: false, batch: batch);
        await batch.CheckpointAsync();
        await family.WriteAuditAsync([component]);
        family.FinalizeFamilyFileNames(LibraryExporter.OutputFormat.Altium);
        var sch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(Path.Combine(directory, "youeda.SchLib"));
        var symbol = (SchComponent)sch[AltiumSchExporter.LibraryComponentName(component)]!;
        var noFootprint = (SchComponent)sch[AltiumSchExporter.LibraryComponentName(symbolOnly)]!;
        Check(sch.Components.Count() == 2 && !noFootprint.Implementations.Any(i => i.ModelType == "PCBLIB") &&
              symbol.Implementations.Any(i => i.ModelType == "PCBLIB") &&
              symbol.Parameters.Any(p => p.Name == "LCSC Part" && p.Value == component.LcscPartNumber),
            "repeated checkpoints retain prior components, symbol-only choices, footprint links, and LCSC IDs");
        var pcb = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(Path.Combine(directory, "youeda.PcbLib"));
        var expectedPcb = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(Path.Combine(baseline, "youeda.PcbLib"));
        var actual = pcb.Components.Single();
        var expected = expectedPcb.Components.Single();
        Check(actual.Pads.Count == expected.Pads.Count &&
            actual.Tracks.Cast<PcbTrack>().Select(t => (t.Start, t.End, t.Width, t.Layer))
                .SequenceEqual(expected.Tracks.Cast<PcbTrack>().Select(t => (t.Start, t.End, t.Width, t.Layer))) &&
            actual.Arcs.Cast<PcbArc>().Select(a => (a.Center, a.Radius, a.StartAngle, a.EndAngle, a.Width, a.Layer))
                .SequenceEqual(expected.Arcs.Cast<PcbArc>().Select(a => (a.Center, a.Radius, a.StartAngle, a.EndAngle, a.Width, a.Layer))),
            "batch output matches regular footprint track/arc dimensions and layers");
        var familyName = family.ClassificationFor(component).Family;
        var familySch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(Path.Combine(family.FamilyDirectory(component), familyName + ".SchLib"));
        Check(familySch.Components.Count() == 1 && familySch.Contains(symbol.Name!), "family batch files are independent and reopen after final naming");

        var catalog = Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib");
        var resolver = new UserSymbolLibraryResolver();
        var inductor = new EdaComponent { LcscPartNumber = "C_TEST_L_BATCH", Name = "BATCH_INDUCTOR", Description = "Inductor" };
        inductor.Properties["pre"] = "L?";
        inductor.SymbolPins.Add(new("1", "1", 0, 0, ""));
        inductor.SymbolPins.Add(new("2", "2", 0, 180, ""));
        var match = resolver.Resolve(inductor, catalog, null) ?? throw new Exception("inductor fixture not found");
        var native = resolver.LoadSelectedComponent(match);
        await exporter.ExportImportPlanAsync(inductor, directory, include3d: false, selectedSymbol: match,
            includeFootprint: false, batch: batch);
        await batch.CheckpointAsync();
        var nativeOutput = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(Path.Combine(directory, "youeda.SchLib"));
        var written = (SchComponent)nativeOutput[AltiumSchExporter.LibraryComponentName(inductor)]!;
        Check(written.Arcs.Cast<SchArc>().Select(a => (a.Center, a.Radius, a.StartAngle, a.EndAngle, a.LineWidth))
                .SequenceEqual(native.Arcs.Cast<SchArc>().Select(a => (a.Center, a.Radius, a.StartAngle, a.EndAngle, a.LineWidth))) &&
            written.EllipticalArcs.Select(a => (a.Center, a.PrimaryRadius, a.SecondaryRadius, a.StartAngle, a.EndAngle, a.LineWidth))
                .SequenceEqual(native.EllipticalArcs.Select(a => (a.Center, a.PrimaryRadius, a.SecondaryRadius, a.StartAngle, a.EndAngle, a.LineWidth))) &&
            written.Polylines.Cast<SchPolyline>().Select(p => p.LineWidth).SequenceEqual(native.Polylines.Cast<SchPolyline>().Select(p => p.LineWidth)) &&
            written.Pins.Cast<SchPin>().Select(p => (p.Designator, p.Location, p.Length, p.Orientation))
                .SequenceEqual(native.Pins.Cast<SchPin>().Select(p => (p.Designator, p.Location, p.Length, p.Orientation))),
            "batch export preserves selected native inductor arc widths and pin mapping");
        Check(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "verified checkpoint leaves no temporary library files");
    }
}
