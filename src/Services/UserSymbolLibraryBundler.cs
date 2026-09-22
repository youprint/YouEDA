using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Sch;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>One-time packer that consolidates the user's many native SchLib files into one native SchLib.</summary>
public sealed class UserSymbolLibraryBundler
{
    public async Task<SymbolBundleResult> BundleAsync(string sourceDirectory, string destinationPath)
    {
        if (!Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException($"Symbol source folder was not found: {sourceDirectory}");
        var files = Directory.EnumerateFiles(sourceDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".SchLib", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var merged = (SchLibrary)AltiumLibrary.CreateSchLib();
        var imported = 0;
        var renamed = 0;
        var failures = new List<string>();

        foreach (var file in files)
        {
            try
            {
                var source = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(file);
                foreach (var component in source.Components.OfType<SchComponent>())
                {
                    var originalName = component.Name;
                    // Compound-file storage names are limited to 31 characters.  Give every
                    // component a stable catalog index, ensuring storage-name uniqueness even
                    // when vendor libraries reuse long names with the same first 31 characters.
                    var uniqueName = $"{imported + 1:D4} {originalName}";
                    if (uniqueName.Length > 31) uniqueName = uniqueName[..31];
                    component.Name = uniqueName;
                    component.LibReference = uniqueName;
                    if (string.IsNullOrWhiteSpace(component.DesignItemId)) component.DesignItemId = originalName;
                    ApplyKnownPolarityLabels(component, originalName);
                    renamed++;
                    merged.Add(component);
                    imported++;
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetFileName(file)}: {exception.Message}");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("Bundle destination has no directory."));
        await merged.SaveAsync(destinationPath);
        var verified = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(destinationPath);
        if (verified.Count != imported) throw new InvalidDataException($"Bundled SchLib verification failed: expected {imported} components, reopened {verified.Count}.");
        return new SymbolBundleResult(files.Length, imported, renamed, failures);
    }

    private static void ApplyKnownPolarityLabels(SchComponent component, string originalName)
    {
        // Follow the convention used by the user's library: pin 1 = anode (A),
        // pin 2 = cathode (K). Keep the numerical mapping while showing
        // unambiguous electrical labels in generated SchLibs.
        if (!string.Equals(originalName, "Diode", StringComparison.OrdinalIgnoreCase) || component.Pins.Count != 2) return;
        foreach (var pin in component.Pins.OfType<SchPin>())
        {
            if (pin.Designator == "1") pin.Name = "A";
            else if (pin.Designator == "2") pin.Name = "K";
        }
    }

}

public sealed record SymbolBundleResult(int SourceLibraries, int Components, int RenamedDuplicates, IReadOnlyList<string> Failures);
