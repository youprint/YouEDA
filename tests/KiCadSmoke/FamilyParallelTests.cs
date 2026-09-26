using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Pcb;
using OriginalCircuit.Altium.Models.Sch;

internal static class FamilyParallelTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + message);
        Console.WriteLine("PASS: " + message);
    }
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, peak = 0;
        var seen = new ConcurrentBag<int>();
        var tasks = FamilyWorkScheduler.RunAsync(Enumerable.Range(0, 8).ToArray(), 3, async family =>
        {
            var value = Interlocked.Increment(ref active);
            lock (seen) peak = Math.Max(peak, value);
            if (value == 3) ready.TrySetResult();
            await release.Task.WaitAsync(timeout.Token);
            seen.Add(family);
            Interlocked.Decrement(ref active);
        }, timeout.Token);
        await ready.Task.WaitAsync(timeout.Token);
        release.SetResult(); await tasks;
        Check(peak == 3 && seen.Order().SequenceEqual(Enumerable.Range(0, 8)), "family scheduler runs three workers and assigns every family exactly once");
        peak = 0;
        await FamilyWorkScheduler.RunAsync(Enumerable.Range(0, 4).ToArray(), 1, async _ =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref active)); await Task.Yield(); Interlocked.Decrement(ref active);
        });
        Check(peak == 1, "one-worker fallback is strictly serial");
        using var cancelled = new CancellationTokenSource();
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRun = FamilyWorkScheduler.RunAsync(Enumerable.Range(0, 8).ToArray(), 3, async _ =>
        {
            Interlocked.Increment(ref active); begun.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancelled.Token); }
            finally { Interlocked.Decrement(ref active); }
        }, cancelled.Token);
        await begun.Task; cancelled.Cancel();
        try { await cancelRun; throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
        Check(active == 0, "cancelled family scheduling joins all active workers");

        var root = Path.Combine(AppContext.BaseDirectory, "parallel-family-" + Guid.NewGuid().ToString("N"));
        var catalog = Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib");
        var resolver = new UserSymbolLibraryResolver();
        var parts = new[]
        {
            Part("C900001", "TEST_R", "Resistor", "R?", "R0402"),
            Part("C900002", "TEST_C", "Capacitor", "C?", "C0402"),
            Part("C900003", "TEST_L", "Inductor", "L?", "IND_TEST"),
            Part("C900004", "TEST_D", "Diode", "D?", "D_TEST"),
            Part("C900005", "TEST_R2", "Resistor", "R?", "SHARED_PACKAGE"),
            Part("C900006", "TEST_C2", "Capacitor", "C?", "SHARED_PACKAGE")
        };
        // All have the same UUID but different STEP payloads: merging must remap only
        // collisions, leaving unrelated models in the existing library untouched.
        const string uuid = "11111111111111111111111111111111";
        foreach (var part in parts) part.ThreeDModel = new(uuid, part.Name, 0, 0, 0, 0, 0, 0, 1, 1);
        parts[5].Pads[0] = parts[5].Pads[0] with { WidthMm = .8 };
        var existing = Part("C900000", "KEEP_EXISTING", "Resistor", "R?", "KEEP_EXISTING_FP");
        existing.ThreeDModel = new(uuid, existing.Name, 0, 0, 0, 0, 0, 0, 1, 1);
        var items = parts.Select((p, i) => new FamilyConversionItem(i, p, true, true, resolver.Resolve(p, catalog, null))).ToArray();
        async Task<Downloaded3dModel?> Model(Eda3dModel? model, string directory, CancellationToken ct)
        {
            if (model is null) return null;
            var step = "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n/* " + model.Name + " */\nENDSEC;\nEND-ISO-10303-21;";
            var path = Path.Combine(directory, model.Name + ".step");
            await File.WriteAllTextAsync(path, step, ct);
            return new(model, model.Name + ".step", path, step, 0, 1);
        }
        var signatures = new List<string>();
        foreach (var workers in new[] { 1, 3 })
        {
            var output = Path.Combine(root, "workers-" + workers);
            var plan = FamilyLibraryExportPlan.Create(output, parts, Path.Combine(root, "reference"));
            await using var models = new ModelDownloadBatch(parts, Model);
            await using var existingModels = new ModelDownloadBatch([existing], Model);
            await new LibraryExporter().ExportImportPlanAsync(existing, output, selectedSymbol: resolver.Resolve(existing, catalog, null), modelDownloads: existingModels);
            var before = await File.ReadAllBytesAsync(Path.Combine(output, "youeda.SchLib"));
            var session = new FamilyConversionSession(plan, catalog, models, workers);
            var watch = Stopwatch.StartNew();
            await session.RunAsync(items);
            var afterFamilies = await File.ReadAllBytesAsync(Path.Combine(output, "youeda.SchLib"));
            Check(before.SequenceEqual(afterFamilies),
                $"{workers} workers do not touch combined files during family conversion");
            await new AltiumFamilyMerger().MergeAsync(output, plan, session.Succeeded);
            Console.WriteLine($"OFFLINE fixture conversion+merge, {workers} workers: {watch.Elapsed.TotalSeconds:0.00}s (not a network benchmark)");
            var sch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(Path.Combine(output, "youeda.SchLib"));
            var pcb = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(Path.Combine(output, "youeda.PcbLib"));
            Check(sch.Components.Count() == 7 && sch.Contains(AltiumSchExporter.LibraryComponentName(existing)) && pcb.Contains(existing.FootprintName),
                $"{workers}-worker merge retains existing symbols/footprints and all new LCSC parts");
            Check(Math.Abs(((PcbPad)pcb["SHARED_PACKAGE"]!.Pads[0]).SizeTop.X.ToMm() - .8) < .00001,
                "duplicate footprint names resolve in source order regardless of worker completion order");
            var keepBody = ((PcbComponent)pcb[existing.FootprintName]!).ComponentBodies.OfType<PcbComponentBody>().Single();
            Check(pcb.Models.Single(m => m.Id == keepBody.ModelId).StepData.Contains("KEEP_EXISTING"),
                "model-ID collisions do not replace the STEP payload of an unrelated existing footprint");
            Check(pcb.Components.OfType<PcbComponent>().SelectMany(p => p.ComponentBodies.OfType<PcbComponentBody>()).All(body => pcb.Models.Any(m => m.Id == body.ModelId)),
                "all merged body-to-model references resolve after collision remapping");
            signatures.Add(Signature(sch, pcb));
            // Re-import the same verified files; output must remain deduplicated and equivalent.
            await new AltiumFamilyMerger().MergeAsync(output, plan, session.Succeeded);
            var reSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(Path.Combine(output, "youeda.SchLib"));
            var rePcb = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(Path.Combine(output, "youeda.PcbLib"));
            Check(reSch.Components.Count() == 7 && rePcb.Models.Count == pcb.Models.Count, "repeated merge does not duplicate symbols or STEP payloads");
            if (workers == 3)
            {
                var schPath = Path.Combine(output, "youeda.SchLib");
                var pcbPath = Path.Combine(output, "youeda.PcbLib");
                var savedSch = await File.ReadAllBytesAsync(schPath);
                var savedPcb = await File.ReadAllBytesAsync(pcbPath);
                // Permit reads/backups but deny replacement of the second destination.
                // This forces rollback after the first destination was already published.
                using (var held = new FileStream(pcbPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { await new AltiumFamilyMerger().MergeAsync(output, plan, session.Succeeded); throw new Exception("locked target ignored"); }
                    catch (AggregateException) { }
                }
                var restoredSch = await File.ReadAllBytesAsync(schPath);
                var restoredPcb = await File.ReadAllBytesAsync(pcbPath);
                Check(savedSch.SequenceEqual(restoredSch) && savedPcb.SequenceEqual(restoredPcb),
                    "second-file publish failure restores the first file and preserves both prior libraries byte-for-byte");
            }
            await plan.WriteAuditAsync(session.Succeeded.Select(i => i.Component));
            plan.FinalizeFamilyFileNames(LibraryExporter.OutputFormat.Altium);
            Check(File.Exists(Path.Combine(plan.FamilyDirectory(parts[0]), "Resistors.SchLib")), "verified family files use final family names");
        }
        Check(signatures[0] == signatures[1], "one and three workers produce matching native symbols, pin mappings, widths, footprints, and STEP poses");

        var optOutput = Path.Combine(root, "symbol-only");
        var optPlan = FamilyLibraryExportPlan.Create(optOutput, parts, Path.Combine(root, "reference"));
        await using var optModels = new ModelDownloadBatch([]);
        var optSession = new FamilyConversionSession(optPlan, catalog, optModels, 3);
        await optSession.RunAsync([
            items[0] with { IncludeFootprint = false, Symbol = new SymbolLibraryMatch(Path.Combine(root, "missing.SchLib"), "broken fixture", "missing") },
            items[4] with { IncludeFootprint = false },
            items[1] with { IncludeFootprint = false }
        ]);
        Check(optSession.Failures.Count == 1 && optSession.Succeeded.Count == 2,
            "a bad component does not block later parts in its family or other families");
        await new AltiumFamilyMerger().MergeAsync(optOutput, optPlan, optSession.Succeeded);
        var optSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(Path.Combine(optOutput, "youeda.SchLib"));
        Check(optSch.Components.Count() == 2 && !File.Exists(Path.Combine(optOutput, "youeda.PcbLib")) &&
            optSch.Components.OfType<SchComponent>().All(s => s.Implementations.Count == 0),
            "symbol-only family conversion preserves footprint opt-out and merges only successful parts");

        // A family checkpoint failure must never reach the combined merge path.
        var failOutput = Path.Combine(root, "failure");
        var failPlan = FamilyLibraryExportPlan.Create(failOutput, parts, Path.Combine(root, "reference"));
        var blockedPath = Path.Combine(failPlan.FamilyDirectory(parts[0]), "youeda.SchLib");
        Directory.CreateDirectory(blockedPath); // Blocks replacing a file, without altering any user library.
        await using var failModels = new ModelDownloadBatch([]);
        var failSession = new FamilyConversionSession(failPlan, catalog, failModels, 3);
        try { await failSession.RunAsync(items.Select(i => i with { Include3d = false }).ToArray()); throw new Exception("checkpoint failure ignored"); }
        catch (AggregateException) { }
        Check(!File.Exists(Path.Combine(failOutput, "youeda.SchLib")) &&
              File.Exists(Path.Combine(failPlan.FamilyDirectory(parts[1]), "youeda.SchLib")),
            "failed checkpoint prevents combined publication while unrelated families finish");
    }

    private static EdaComponent Part(string code, string name, string description, string prefix, string footprint)
    {
        var part = new EdaComponent { LcscPartNumber = code, Name = name, Description = description, FootprintName = footprint };
        part.Properties["pre"] = prefix;
        part.Properties["Value"] = "fixture";
        part.SymbolPins.Add(new("1", "1", 0, 0, "start")); part.SymbolPins.Add(new("2", "2", 0, 180, "end"));
        part.Pads.Add(new("1", -.5, 0, .5, .5, 0, "1", false, 0));
        part.Pads.Add(new("2", .5, 0, .5, .5, 0, "1", false, 0));
        part.Shapes.Add(new("TRACK", "3", [(-.8, -.4), (-.8, .4)], .1524));
        return part;
    }

    public static string Signature(SchLibrary sch, PcbLibrary pcb) => JsonSerializer.Serialize(new
    {
        symbols = sch.Components.OfType<SchComponent>().OrderBy(s => s.Name).Select(s => new
        {
            s.Name,
            pins = s.Pins.Cast<SchPin>().Select(p => new { p.Designator, p.Location, p.Length, p.Orientation }),
            arcs = s.Arcs.Cast<SchArc>().Select(a => new { a.Center, a.Radius, a.LineWidth, a.StartAngle, a.EndAngle }),
            ellipticalArcs = s.EllipticalArcs.Select(a => new { a.Center, a.PrimaryRadius, a.SecondaryRadius, a.LineWidth, a.StartAngle, a.EndAngle }),
            lines = s.Polylines.Cast<SchPolyline>().Select(p => new { p.LineWidth, p.Vertices }),
            parameters = s.Parameters.Select(p => new { p.Name, p.Value }),
            links = s.Implementations.Select(i => new { i.ModelName, i.ModelType })
        }),
        footprints = pcb.Components.OfType<PcbComponent>().OrderBy(p => p.Name).Select(p => new
        {
            p.Name,
            pads = p.Pads.Cast<PcbPad>().Select(pad => new { pad.Designator, pad.Location, pad.SizeTop }),
            tracks = p.Tracks.Cast<PcbTrack>().Select(t => new { t.Start, t.End, t.Width, t.Layer }),
            models = p.ComponentBodies.OfType<PcbComponentBody>().Select(body => new
            {
                model = pcb.Models.Single(m => m.Id == body.ModelId).StepData,
                body.Model3DRotX, body.Model3DRotY, body.Model3DRotZ, body.Model2DRotation
            })
        })
    });
}
