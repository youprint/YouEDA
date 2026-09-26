using System.Text.Json;
using EasyEdaAltiumGrabber.Services;

internal static class CachedKiCadValidation
{
    public static async Task RunAsync(string manifest, string cache, string output, bool familyLibraries = false)
    {
        if (Directory.Exists(output)) throw new IOException("A fresh acceptance directory is required.");
        Directory.CreateDirectory(output);
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
        var codes = data.RootElement.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("part").GetString()!).ToArray();
        var components = new List<EasyEdaAltiumGrabber.Models.EdaComponent>();
        foreach (var code in codes)
            components.Add(new Parser().Parse(code, await File.ReadAllTextAsync(Path.Combine(cache, code + ".json"))));
        var familyPlan = familyLibraries ? FamilyLibraryExportPlan.Create(output, components,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Library")) : null;
        var resolver = new UserSymbolLibraryResolver();
        var roots = string.Join(Path.PathSeparator, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Library"),
            Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib"));
        var modelCache = new ModelAssetCache();
        var writer = new KiCadLibraryExporter();
        var priceCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouEDA", "PriceCache");
        int models = 0, prices = 0;
        foreach (var source in components)
        {
            var code = source.LcscPartNumber;
            // Offline acceptance preserves snapshot timestamps, including old snapshots; never makes HTTP calls.
            var pricePath = Path.Combine(priceCache, code + ".json");
            if (File.Exists(pricePath))
            {
                var snapshot = JsonSerializer.Deserialize<JlcPcbPriceSnapshot>(await File.ReadAllTextAsync(pricePath));
                if (snapshot is not null)
                { JlcPcbPricingService.ApplySnapshot(source, snapshot); prices++; }
            }
            var match = resolver.Resolve(source, roots, null);
            var symbol = match is null ? null : resolver.LoadSelectedComponent(match);
            Downloaded3dModel? model = null;
            if (source.ThreeDModel is { } pose && await modelCache.ReadAsync(pose.Uuid, default) is { } asset)
            {
                var path = Path.Combine(output, "cached-models", pose.Uuid + ".step");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path)) await File.WriteAllTextAsync(path, asset.Step);
                model = new(pose, pose.Uuid + ".step", path, asset.Step, asset.ZOffsetMm, asset.HeightMm);
                models++;
            }
            await writer.UpsertAsync(source, output, symbol, model);
            if (familyPlan is not null) await writer.UpsertAsync(source, familyPlan.FamilyDirectory(source), symbol, model);
        }
        if (familyPlan is not null)
        {
            familyPlan.FinalizeFamilyFileNames(LibraryExporter.OutputFormat.KiCad);
            await familyPlan.WriteAuditAsync(components);
        }
        Console.WriteLine($"PASS: fresh KiCad export: {codes.Length} parts; {models} cached models; {prices} cached price snapshots; {output}");
    }
}
