using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Pcb;
using OriginalCircuit.Altium.Models.Sch;

internal static class CachedFamilyValidation
{
    public static async Task RunAsync(string csv, string cache, string output)
    {
        var codes = (await File.ReadAllLinesAsync(csv)).Select(s => s.Trim())
            .Where(s => Regex.IsMatch(s, "^C[0-9]+$")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Length != 351) throw new Exception($"Expected 351 acceptance parts, got {codes.Length}");
        var catalog = Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib");
        var reference = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Library");
        var symbols = string.Join(Path.PathSeparator, reference, catalog);
        var items = new List<FamilyConversionItem>();
        foreach (var code in codes)
        {
            var raw = await File.ReadAllTextAsync(Path.Combine(cache, code + ".json"));
            var component = new Parser().Parse(code, raw);
            var match = new UserSymbolLibraryResolver().Resolve(component, symbols, null);
            items.Add(new(items.Count, component, component.HasFootprintData, false, match));
        }
        var reports = new List<object>();
        var uniqueModels = items.Where(i => i.Component.ThreeDModel is not null)
            .Select(i => i.Component.ThreeDModel!.Uuid).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Console.WriteLine($"Acceptance source: {items.Count} parts, {uniqueModels} unique model UUIDs (no downloads requested).");
        string? signature = null;
        foreach (var workers in new[] { 1, 3 })
        {
            var directory = Path.Combine(output, "workers-" + workers);
            if (Directory.Exists(directory)) throw new IOException("Acceptance output must be a fresh directory.");
            var plan = FamilyLibraryExportPlan.Create(directory, items.Select(i => i.Component), reference);
            await using var models = new ModelDownloadBatch([]); // Deliberately offline, isolates local conversion.
            var session = new FamilyConversionSession(plan, symbols, models, workers);
            var watch = Stopwatch.StartNew();
            await session.RunAsync(items);
            var conversion = watch.Elapsed.TotalSeconds;
            if (session.Failures.Count > 0 || session.Succeeded.Count != codes.Length)
                throw new Exception($"Acceptance failed: {session.Succeeded.Count}/{codes.Length}: {string.Join("; ", session.Failures)}");
            await new AltiumFamilyMerger().MergeAsync(directory, plan, session.Succeeded);
            var total = watch.Elapsed.TotalSeconds;
            var sch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(Path.Combine(directory, "youeda.SchLib"));
            var pcb = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(Path.Combine(directory, "youeda.PcbLib"));
            var writtenCodes = sch.Components.OfType<SchComponent>().SelectMany(s => s.Parameters)
                .Where(p => p.Name == "LCSC Part").Select(p => p.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = codes.Where(code => !writtenCodes.Contains(code)).ToArray();
            if (missing.Length > 0) throw new Exception("Missing LCSC references after merge: " + string.Join(',', missing));
            var current = FamilyParallelTests.Signature(sch, pcb);
            if (signature is not null && signature != current) throw new Exception("Single/parallel acceptance signatures differ");
            signature = current;
            await plan.WriteAuditAsync(items.Select(i => i.Component));
            plan.FinalizeFamilyFileNames(LibraryExporter.OutputFormat.Altium);
            reports.Add(new { workers, parts = codes.Length, uniqueModels, symbols = sch.Components.Count(), footprints = pcb.Components.Count(),
                conversionSeconds = conversion, conversionAndMergeSeconds = total, mode = "offline cached CAD, no model downloads" });
            Console.WriteLine($"PASS: cached acceptance {workers} workers; {codes.Length} LCSC references; conversion {conversion:0.00}s, with merge {total:0.00}s");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "acceptance-report.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: 351-part one/three-worker signatures match; no live requests issued.");
    }
}
