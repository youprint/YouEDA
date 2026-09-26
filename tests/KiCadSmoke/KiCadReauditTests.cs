using System.Text.RegularExpressions;
using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;

internal static class KiCadReauditTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }

    public static async Task RunAsync()
    {
        var source = new EdaComponent { LcscPartNumber = "C901", Name = "Grid test", Description = "Resistor" };
        source.Properties["pre"] = "R?";
        var unit = new EdaSymbolUnit(); source.SymbolUnits.Add(unit);
        unit.Pins.Add(new("1", "A", 0, 180, "") { Xmm = -5.08, Ymm = -.0635, LengthMm = 2.54 });
        unit.Pins.Add(new("2", "K", 0, 0, "") { Xmm = 5.08, Ymm = -.0635, LengthMm = 2.54 });
        unit.Graphics.Add(new("ELLIPSE", [(0, -.0635), (1.27, 1.27)], .0508, false));
        source.Pads.Add(new("1", -1, 0, 1, 1, 0, "1", true, 0));
        source.Pads.Add(new("2", 1, 0, 1, 1, 0, "1", true, 0));
        var output = Path.Combine(Path.GetTempPath(), "YouEDA-reaudit-" + Guid.NewGuid().ToString("N"));
        var writer = new KiCadLibraryExporter();
        await writer.UpsertAsync(source, output);
        var text = File.ReadAllText(Path.Combine(output, "youeda.kicad_sym"));
        Check(text.Contains("(at -5.08 0 0)") && text.Contains("(at 5.08 0 180)") &&
              text.Contains("(center 0 0) (radius 1.27)"), "common grid offset translates pins and artwork, not ellipse radius");
        Check(unit.Pins.All(p => p.Ymm == -.0635) && unit.Graphics[0].PointsMm[0].Y == -.0635,
            "grid correction never mutates source geometry");
        unit.Pins[1] = unit.Pins[1] with { Ymm = .2 };
        await writer.UpsertAsync(source, output);
        text = File.ReadAllText(Path.Combine(output, "youeda.kicad_sym"));
        Check(text.Contains("(at -5.08 -0.0635 0)") && text.Contains("(at 5.08 0.2 180)"),
            "incompatible spacing remains unchanged rather than snapping individual pins");
        unit.Pins[1] = unit.Pins[1] with { Ymm = -.0635 };
        var secondUnit = new EdaSymbolUnit(); source.SymbolUnits.Add(secondUnit);
        secondUnit.Pins.Add(new("3", "A", 0, 180, "") { Xmm = -2.54, Ymm = .3175, LengthMm = 1.27 });
        secondUnit.Pins.Add(new("4", "K", 0, 0, "") { Xmm = 2.54, Ymm = .3175, LengthMm = 1.27 });
        await writer.UpsertAsync(source, output);
        text = File.ReadAllText(Path.Combine(output, "youeda.kicad_sym"));
        Check(text.Contains("(at -2.54 0 0)") && text.Contains("(at -5.08 0 0)"), "each schematic unit gets its own rigid grid translation");

        var snapshot = new JlcPcbPriceSnapshot("https://example.test/C901", "USD", .007m, 100, 1000,
            [new(100000, .007m, "USD"), new(100, .01m, "USD")], DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        JlcPcbPricingService.ApplySnapshot(source, snapshot);
        Check(source.Properties["JLCPCB Unit Price"] == "0.01 USD" && source.Properties["JLCPCB Price Quantity"] == "1" &&
              source.Properties["JLCPCB Price Tier Quantity"] == "100" && source.Properties["JLCPCB Price Basis"].Contains("estimate"),
            "old bulk-price cache selects lowest purchase tier and explicitly labels per-piece estimate");
        Check(snapshot.UnitPrice == .007m && snapshot.PriceTiers[0].Quantity == 100000,
            "normalization does not mutate cached snapshot or original tiers");
        JlcPcbPricingService.ApplySnapshot(source, snapshot with { PriceTiers = [new(1, .02m, "USD")] });
        Check(source.Properties["JLCPCB Price Basis"] == "One-piece purchase tier" && source.Properties["JLCPCB Unit Price"] == "0.02 USD",
            "actual single-piece tier takes precedence over public bulk offer");
        JlcPcbPricingService.ApplySnapshot(source, snapshot with { PriceTiers = [], UnitPrice = null, Stock = null });
        Check(!source.Properties.ContainsKey("JLCPCB Unit Price") && !source.Properties.ContainsKey("JLCPCB Price Tiers"),
            "missing replacement price cannot leave stale tier parameters");
        const string page = """
          <script type="application/ld+json">{"@type":"Product","sku":"C901","offers":{"priceCurrency":"USD","price":0.007}}</script>
          <script id="__NEXT_DATA__">{"productCode":"C901","productPriceList":[{"ladder":100000,"usdPrice":0.007,"currencySymbol":"¥"},{"ladder":100,"usdPrice":0.01,"currencySymbol":"¥"}]}</script>
          """;
        Check(JlcPcbPricingService.ParseProductPage(page, "C901", DateTimeOffset.UtcNow) is { UnitPrice: .01m, MinimumQuantity: 100, Currency: "USD" },
            "live parser selects first tier and labels usdPrice as USD");

        var capacitor = new EdaComponent { LcscPartNumber = "C902", Name = "Capacitor", Description = "Ceramic capacitor" };
        capacitor.Properties["pre"] = "C?"; capacitor.SymbolUnits.Add(unit); capacitor.Pads.AddRange(source.Pads);
        var familyRoot = Path.Combine(output, "families");
        var plan = FamilyLibraryExportPlan.Create(familyRoot, [source, capacitor], Path.Combine(output, "reference"));
        var step = Path.Combine(output, "fixture.step");
        File.WriteAllText(step, "ISO-10303-21;\nEND-ISO-10303-21;");
        var model = new Downloaded3dModel(new("fixture", "fixture", 0, 0, 0, 0, 0, 0, 1, 1), "fixture.step", step, "", 0, 1);
        foreach (var part in new[] { source, capacitor })
            await writer.UpsertAsync(part, plan.FamilyDirectory(part), model: model);
        plan.FinalizeFamilyFileNames(LibraryExporter.OutputFormat.KiCad);
        foreach (var part in new[] { source, capacitor })
        {
            var family = plan.ClassificationFor(part).Family;
            var dir = plan.FamilyDirectory(part);
            var symbol = File.ReadAllText(Path.Combine(dir, family + ".kicad_sym"));
            Check(symbol.Contains($"(property \"Footprint\" \"{family}:{part.LcscPartNumber}\""), "family symbol links its own footprint nickname: " + family);
            foreach (var table in new[] { "sym-lib-table", "fp-lib-table" })
            {
                var content = File.ReadAllText(Path.Combine(dir, table));
                var path = Regex.Match(content, "\\(uri \"([^\"]+)\"").Groups[1].Value;
                Check(File.Exists(path) || Directory.Exists(path), "family table resolves outside any project: " + family + "/" + table);
                Check(content.Contains($"(name \"{family}\")"), "unique family nickname: " + family);
            }
            var fp = File.ReadAllText(Path.Combine(dir, family + ".pretty", part.LcscPartNumber + ".kicad_mod"));
            var modelPath = Regex.Match(fp, "\\(model \"([^\"]+)\"").Groups[1].Value;
            Check(Path.IsPathFullyQualified(modelPath) && File.Exists(modelPath), "3D model resolves independently of consuming project's directory");
        }
        var rootTable = File.ReadAllText(Path.Combine(plan.RunDirectory, "fp-lib-table"));
        Check(rootTable.Contains("\"Resistors\"") && rootTable.Contains("\"Capacitors\""), "aggregate family table installs both families together");
        var before = File.ReadAllText(Path.Combine(plan.FamilyDirectory(source), "Resistors.kicad_sym"));
        bool refused = false;
        try { plan.FinalizeFamilyFileNames(LibraryExporter.OutputFormat.KiCad); } catch (IOException) { refused = true; }
        Check(refused && before == File.ReadAllText(Path.Combine(plan.FamilyDirectory(source), "Resistors.kicad_sym")),
            "family finalization refuses to overwrite completed outputs");
        var userTable = "(fp_lib_table (lib (name \"User\")(type \"KiCad\")(uri \"C:/User.pretty\")))";
        File.WriteAllText(Path.Combine(output, "fp-lib-table"), userTable);
        await writer.UpsertAsync(source, output);
        Check(File.ReadAllText(Path.Combine(output, "fp-lib-table")) == userTable,
            "normal exports preserve existing user library tables");
        var onlyPlan = FamilyLibraryExportPlan.Create(Path.Combine(output, "symbol-only"), [source], Path.Combine(output, "reference"));
        await writer.UpsertAsync(source, onlyPlan.FamilyDirectory(source), includeFootprint: false);
        onlyPlan.FinalizeFamilyFileNames(LibraryExporter.OutputFormat.KiCad);
        var onlySymbol = File.ReadAllText(Path.Combine(onlyPlan.FamilyDirectory(source), "Resistors.kicad_sym"));
        Check(onlySymbol.Contains("(property \"Footprint\" \"\"") &&
              !Directory.Exists(Path.Combine(onlyPlan.FamilyDirectory(source), "Resistors.pretty")) &&
              !File.ReadAllText(Path.Combine(onlyPlan.RunDirectory, "fp-lib-table")).Contains("(lib "),
            "symbol-only family has no fabricated footprint or dangling table entry");
    }
}
