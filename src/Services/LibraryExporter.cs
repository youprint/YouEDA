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
    public async Task<string> ExportImportPlanAsync(
        EdaComponent component,
        string outputDirectory,
        bool include3d = true,
        string? userSymbolDirectory = null,
        SymbolLibraryMatch? selectedSymbol = null,
        CancellationToken ct = default)
    {
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
            model = include3d
                ? await new EasyEda3dModelDownloader().TryDownloadAsync(component.ThreeDModel, partDirectory, ct)
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

        var pcbLib = await new AltiumV2Exporter().UpsertPcbLibAsync(component, outputDirectory, model);
        var resolver = new UserSymbolLibraryResolver();
        var symbolMatch = selectedSymbol ?? resolver.Resolve(component, userSymbolDirectory, null);
        string schLib;
        string symbolStatus;
        if (symbolMatch is null)
        {
            schLib = await new AltiumSchExporter().UpsertEasyEdaSymbolAsync(component, outputDirectory);
            symbolStatus = "Schematic symbol: generated from EasyEDA P pin records.";
        }
        else
        {
            var symbol = resolver.LoadSelectedComponent(symbolMatch);
            // A generic/library symbol is retained exactly as drawn, but its entry in the
            // output library is keyed by this LCSC part so different parts never overwrite it.
            symbol.Name = component.LcscPartNumber;
            symbol.LibReference = component.LcscPartNumber;
            symbol.Comment = component.Name;
            symbol.DesignItemId = component.Properties.GetValueOrDefault("Manufacturer Part") ?? component.LcscPartNumber;
            schLib = await new AltiumSchExporter().UpsertSymbolAsync(symbol, outputDirectory);
            symbolStatus = $"Schematic symbol: reused your {symbolMatch.MatchKind} from {symbolMatch.Path}.";
        }

        await File.WriteAllTextAsync(Path.Combine(partDirectory, "README.txt"),
            $"Shared native PcbLib: {pcbLib}.\nShared native SchLib: {schLib}.\n{symbolStatus}\n{modelStatus}\n", ct);
        return pcbLib;
    }
}
