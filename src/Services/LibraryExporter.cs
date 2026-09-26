using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyEdaAltiumGrabber.Models;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Exports per-part source artifacts and upserts native Altium libraries.</summary>
public sealed class LibraryExporter
{
    public enum OutputFormat { Altium, KiCad, Both }

    public async Task<string> ExportImportPlanAsync(
        EdaComponent component,
        string outputDirectory,
        bool include3d = true,
        string? userSymbolDirectory = null,
        SymbolLibraryMatch? selectedSymbol = null,
        CancellationToken ct = default,
        OutputFormat format = OutputFormat.Altium,
        bool includeFootprint = true,
        IReadOnlyList<string>? additionalOutputDirectories = null,
        AltiumExportBatch? batch = null,
        ModelDownloadBatch? modelDownloads = null)
    {
        using var operation = ImportDiagnostics.Measure("part.export", new { part = component.LcscPartNumber, outputDirectory,
            format = format.ToString(), includeFootprint, include3d });
        Directory.CreateDirectory(outputDirectory);
        // Keep raw data and a downloaded STEP beside each source part. The two Altium
        // library files themselves intentionally live at the output-directory root.
        var partDirectory = Path.Combine(outputDirectory, component.LcscPartNumber);
        Directory.CreateDirectory(partDirectory);

        var planPath = Path.Combine(partDirectory, $"{component.LcscPartNumber}.altium-import-plan.json");
        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(new
        {
            schema = "altium-import-plan/v1",
            units = "mm",
            coordinateSystem = "Altium: X right, Y up",
            component
        }, options), ct);

        Downloaded3dModel? model = null;
        var modelStatus = "No EasyEDA 3D model is referenced by this footprint.";
        try
        {
            model = include3d && includeFootprint
                ? modelDownloads is not null ? await modelDownloads.GetAsync(component.LcscPartNumber)
                    : await new EasyEda3dModelDownloader().TryDownloadAsync(component.ThreeDModel, partDirectory, ct)
                : null;
            if (model is not null)
                modelStatus = $"Embedded EasyEDA STEP model: {model.Path}";
            else if (!include3d)
                modelStatus = "3D model export was deselected.";
            else if (component.ThreeDModel is not null)
                modelStatus = "EasyEDA references a 3D model, but its public STEP file was unavailable.";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
        {
            modelStatus = $"The footprint was generated without a 3D model: {exception.Message}";
        }

        var resolver = new UserSymbolLibraryResolver();
        // A user-supplied native symbol, including an inductor, is the source of truth.
        // The generated EasyEDA drawing is used only when neither an exact nor a curated
        // native match is available.
        var symbolMatch = selectedSymbol ?? resolver.Resolve(component, userSymbolDirectory, null);
        ImportDiagnostics.Record("symbol.selected", new { part = component.LcscPartNumber, source = symbolMatch?.Path ?? "EasyEDA fallback",
            symbolMatch?.ComponentName, symbolMatch?.MatchKind });
        string? pcbLib = null;
        string? schLib = null;
        string? kiCadLib = null;
        var symbolStatus = symbolMatch is null
            ? "Schematic symbol: generated from EasyEDA records."
            : $"Schematic symbol: reused your {symbolMatch.MatchKind} from {symbolMatch.Path}.";
        var targets = new[] { outputDirectory }
            .Concat(additionalOutputDirectories ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var target in targets)
        {
            // Reload the native source symbol for every destination. Altium writer objects are
            // mutable, so this prevents an upsert in one family library affecting another.
            var targetSymbol = symbolMatch is null ? null : resolver.LoadSelectedComponent(symbolMatch);
            if (targetSymbol is not null)
            {
                var compatibility = UserSymbolLibraryResolver.CheckPinCompatibility(component, targetSymbol);
                ImportDiagnostics.Record("symbol.pin_check", new { part = component.LcscPartNumber,
                    compatibility.Compatible, compatibility.Reason });
                if (!compatibility.Compatible)
                    throw new InvalidDataException("Selected native symbol rejected: " + compatibility.Reason);
            }
            if (format is OutputFormat.Altium or OutputFormat.Both)
            {
                if (batch is not null)
                {
                    await batch.UpsertAsync(component, target, targetSymbol, model, includeFootprint);
                    schLib = Path.Combine(target, "youeda.SchLib");
                    if (includeFootprint) pcbLib = Path.Combine(target, "youeda.PcbLib");
                }
                else
                {
                    if (includeFootprint)
                        pcbLib = await new AltiumV2Exporter().UpsertPcbLibAsync(component, target, model);
                    if (targetSymbol is null)
                        schLib = await new AltiumSchExporter().UpsertEasyEdaSymbolAsync(component, target, includeFootprint);
                    else
                    {
                        targetSymbol.Comment = component.Name;
                        AltiumSchExporter.PrepareSymbol(targetSymbol, component, includeFootprint);
                        schLib = await new AltiumSchExporter().UpsertSymbolAsync(targetSymbol, target,
                            legacyLcscName: component.LcscPartNumber, legacyComponentName: component.Name, attachFootprint: includeFootprint);
                    }
                }
            }
            if (format is OutputFormat.KiCad or OutputFormat.Both)
                kiCadLib = await new KiCadLibraryExporter().UpsertAsync(component, target, targetSymbol, model, ct, includeFootprint);
        }

        await File.WriteAllTextAsync(Path.Combine(partDirectory, "README.txt"),
            $"Shared native PcbLib: {pcbLib ?? "not requested"}.\nShared native SchLib: {schLib ?? "not requested"}.\nShared KiCad symbol library: {kiCadLib ?? "not requested"}.\n{symbolStatus}\n{modelStatus}\n", ct);

        // The per-LCSC folder is an import staging area only: its raw payload, plan, README,
        // and downloaded STEP are no longer needed after the shared libraries have been written.
        // Altium embeds the STEP data in the PcbLib, while KiCad copies it to youeda.3dshapes.
        Directory.Delete(partDirectory, recursive: true);
        ImportDiagnostics.Record("part.exported", new { part = component.LcscPartNumber });
        return pcbLib ?? schLib ?? kiCadLib!;
    }
}
