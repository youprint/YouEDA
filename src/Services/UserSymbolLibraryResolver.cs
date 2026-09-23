using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EasyEdaAltiumGrabber.Models;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Sch;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Locates the user's existing native Altium symbols without altering their artwork or pin mapping.</summary>
public sealed class UserSymbolLibraryResolver
{
    private readonly Dictionary<string, SchLibrary> _libraryCache = new(StringComparer.OrdinalIgnoreCase);
    public SymbolLibraryMatch? Resolve(EdaComponent component, string? symbolRoot, string? overrideFile)
    {
        if (!string.IsNullOrWhiteSpace(overrideFile))
        {
            if (!File.Exists(overrideFile)) throw new FileNotFoundException("Selected schematic symbol library was not found.", overrideFile);
            if (!string.Equals(Path.GetExtension(overrideFile), ".SchLib", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected symbol file must be an Altium .SchLib file.");
            return new SymbolLibraryMatch(overrideFile, "selected override");
        }

        if (MustGenerateFromEasyEda(component)) return null;
        var files = SymbolFiles(symbolRoot).ToArray();
        if (files.Length == 0) return null;
        if (files.Length == 1 && IsMasterLibrary(files[0])) return ResolveInMaster(component, files[0]);
        var keys = SearchKeys(component).ToArray();
        SymbolLibraryMatch? partial = null;
        foreach (var path in files)
        {
            var normalFileName = Normalize(Path.GetFileNameWithoutExtension(path));
            foreach (var key in keys)
            {
                if (normalFileName.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return new SymbolLibraryMatch(path, "automatic exact match");
                if (key.Length >= 6 && normalFileName.Contains(key, StringComparison.OrdinalIgnoreCase))
                    partial ??= new SymbolLibraryMatch(path, "automatic name match");
            }
        }
        // Exact manufacturer/name matches win; a generic passive symbol is only a safe fallback.
        return partial ?? FindFunctionalMatch(component, files);
    }

    public string CopyToOutput(SymbolLibraryMatch match, EdaComponent component, string outputFolder)
    {
        var name = Path.GetFileNameWithoutExtension(match.Path);
        var safeName = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var destination = Path.Combine(outputFolder, $"{component.LcscPartNumber}_symbol_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.SchLib");
        if (match.ComponentName is null)
        {
            File.Copy(match.Path, destination);
            return destination;
        }

        // Components are mutable (the caller changes Name/LibReference to the LCSC key),
        // so return a fresh deserialised instance rather than leaking the cached catalog item.
        var source = (SchLibrary)AltiumLibrary.OpenSchLibAsync(match.Path).GetAwaiter().GetResult();
        if (source[match.ComponentName] is not SchComponent selected)
            throw new InvalidDataException($"Bundled symbol '{match.ComponentName}' was not found.");
        var export = (SchLibrary)AltiumLibrary.CreateSchLib();
        export.Add(selected);
        export.SaveAsync(destination).GetAwaiter().GetResult();
        return destination;
    }

    /// <summary>
    /// Loads the exact bundled component selected by the matcher. The returned object is an
    /// in-memory copy of the source-library record and can safely be renamed for youeda.SchLib.
    /// </summary>
    public SchComponent LoadSelectedComponent(SymbolLibraryMatch match)
    {
        if (match.ComponentName is null)
            throw new InvalidDataException("A library-wide selection cannot be added to the shared SchLib. Select a component symbol instead.");

        // The selected component will be renamed to the LCSC key by the output writer.
        // Reloading makes each selection independent from the read-only lookup cache.
        var source = (SchLibrary)AltiumLibrary.OpenSchLibAsync(match.Path).GetAwaiter().GetResult();
        if (source[match.ComponentName] is not SchComponent selected)
            throw new InvalidDataException($"Bundled symbol '{match.ComponentName}' was not found.");
        return selected;
    }

    /// <summary>Returns a read-only catalog component for preview without reopening the master library.</summary>
    public SchComponent LoadPreviewComponent(SymbolLibraryMatch match)
    {
        if (match.ComponentName is null)
            throw new InvalidDataException("Select a component in the bundled library to preview it.");
        var library = LoadLibrary(match.Path);
        return library[match.ComponentName] as SchComponent ??
            throw new InvalidDataException($"Bundled symbol '{match.ComponentName}' was not found.");
    }

    public IReadOnlyList<SymbolLibraryMatch> ListMasterSymbols(string masterPath)
    {
        if (!File.Exists(masterPath)) return [];
        var library = LoadLibrary(masterPath);
        return library.Components.OfType<SchComponent>()
            .Select(symbol => new SymbolLibraryMatch(masterPath, "user-selected bundled symbol", symbol.Name))
            .OrderBy(match => match.ComponentName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> SearchKeys(EdaComponent component)
    {
        yield return Normalize(component.Name);
        foreach (var key in new[] { "Manufacturer Part", "name", "package" })
            if (component.Properties.TryGetValue(key, out var value)) yield return Normalize(value);
    }

    private static SymbolLibraryMatch? FindFunctionalMatch(EdaComponent component, IEnumerable<string> files)
    {
        var evidence = string.Join(' ', component.Tags.Append(component.Name).Append(component.Description)
            .Concat(component.Properties.Values)).ToUpperInvariant();
        var target = evidence.Contains("RESIST") ? "RESISTOR"
            : evidence.Contains("CAPACIT") ? "CAPACITOR"
            : evidence.Contains("INDUCT") ? "INDUCTOR"
            : evidence.Contains("FUSE") ? "FUSE"
            : null;
        if (target is null) return null;

        // Prefer a single passive symbol over specialised arrays/holders.
        var path = files.FirstOrDefault(file =>
        {
            var name = Normalize(Path.GetFileNameWithoutExtension(file));
            return name.Contains(target, StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("ARRAY", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("HOLDER", StringComparison.OrdinalIgnoreCase);
        });
        return path is null ? null : new SymbolLibraryMatch(path, $"bundled {target.ToLowerInvariant()} symbol");
    }

    private static string Normalize(string? value) => string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static IEnumerable<string> SymbolFiles(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return [];
        if (File.Exists(source)) return new[] { source };
        if (!Directory.Exists(source)) return [];
        return Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".SchLib", StringComparison.OrdinalIgnoreCase));
    }

    private SymbolLibraryMatch? ResolveInMaster(EdaComponent component, string masterPath)
    {
        var library = LoadLibrary(masterPath);
        var keys = SearchKeys(component).Where(key => key.Length >= 5).ToArray();
        foreach (var key in keys)
        {
            var exact = library.Components.OfType<SchComponent>().FirstOrDefault(symbol =>
                Normalize(symbol.DesignItemId).Equals(key, StringComparison.OrdinalIgnoreCase) ||
                Normalize(symbol.Name).EndsWith(key, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return new SymbolLibraryMatch(masterPath, "bundled exact match", exact.Name);
        }

        var target = GenericFamily(component);
        var functional = target is null ? null : library.Components.OfType<SchComponent>().FirstOrDefault(symbol =>
        {
            var text = $"{symbol.Name} {symbol.DesignItemId} {symbol.Description}".ToUpperInvariant();
            if (target == "DIODE")
                return symbol.Name.EndsWith(" Diode", StringComparison.OrdinalIgnoreCase);
            return text.Contains(target, StringComparison.OrdinalIgnoreCase) && !text.Contains("ARRAY", StringComparison.OrdinalIgnoreCase) && !text.Contains("HOLDER", StringComparison.OrdinalIgnoreCase);
        });
        if (functional is not null) return new SymbolLibraryMatch(masterPath, $"bundled {target!.ToLowerInvariant()} symbol", functional.Name);

        // Two-terminal quartz crystals have interchangeable terminals. A vendor-specific
        // catalog drawing is safe to reuse only when its numbered pins match EasyEDA exactly;
        // oscillators and four-terminal crystals still need a specific match or user choice.
        if (target is null && LibraryFamily(component) == "CRYSTAL" && component.SymbolPins.Count == 2)
        {
            var expected = component.SymbolPins.Select(pin => pin.Number).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var crystal = library.Components.OfType<SchComponent>().FirstOrDefault(symbol =>
                symbol.Name.Contains("Crystal", StringComparison.OrdinalIgnoreCase) &&
                symbol.Pins.Count == 2 &&
                symbol.Pins.Select(pin => pin.Designator ?? "").OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(expected));
            if (crystal is not null) return new SymbolLibraryMatch(masterPath, "bundled two-terminal crystal", crystal.Name);
        }

        // For other non-IC families, only auto-select when category and pin count
        // identify one unambiguous user symbol.  Ambiguous cases go to the picker.
        var family = LibraryFamily(component);
        if (family is null || component.SymbolPins.Count == 0) return null;
        var matches = library.Components.OfType<SchComponent>().Where(symbol =>
        {
            var text = $"{symbol.Name} {symbol.DesignItemId} {symbol.Description}".ToUpperInvariant();
            return text.Contains(family, StringComparison.OrdinalIgnoreCase) && symbol.Pins.Count == component.SymbolPins.Count;
        }).ToArray();
        return matches.Length == 1 ? new SymbolLibraryMatch(masterPath, $"bundled {family.ToLowerInvariant()} match", matches[0].Name) : null;
    }

    public bool MustGenerateFromEasyEda(EdaComponent component)
    {
        var tagText = string.Join(' ', component.Tags).ToUpperInvariant();
        // These families have package-specific signal sets; do not substitute a
        // superficially similar catalog entry for their EasyEDA pin definition.
        return tagText.Contains("MICROCONTROLLER") || tagText.Contains("MCU") || tagText.Contains("MPU") ||
               tagText.Contains("SOC") || tagText.Contains("ARM CORTEX") || tagText.Contains("LOGIC") ||
               tagText.Contains("MEMORY") || tagText.Contains("OPAMP") || tagText.Contains("INTERFACE") ||
               tagText.Contains("ADC") || tagText.Contains("DAC") || tagText.Contains("LEVEL TRANSLATOR") ||
               tagText.Contains("DIGITAL ISOLATOR") || tagText.Contains("POWER -");
    }

    private static string? GenericFamily(EdaComponent component)
    {
        var evidence = string.Join(' ', component.Tags.Append(component.Name).Append(component.Description)
            .Concat(component.Properties.Values)).ToUpperInvariant();
        return evidence.Contains("RESIST") ? "RESISTOR"
            : evidence.Contains("CAPACIT") ? "CAPACITOR"
            : evidence.Contains("INDUCT") ? "INDUCTOR"
            : evidence.Contains("FUSE") ? "FUSE"
            : evidence.Contains("DIODE") && !evidence.Contains("ZENER") && !evidence.Contains("TVS") &&
              !evidence.Contains("PHOTO") && !evidence.Contains("LED") && !evidence.Contains("ARRAY") ? "DIODE"
            : null;
    }

    private static string? LibraryFamily(EdaComponent component)
    {
        var evidence = string.Join(' ', component.Tags.Append(component.Name).Append(component.Description)
            .Concat(component.Properties.Values)).ToUpperInvariant();
        return evidence.Contains("CRYSTAL") || evidence.Contains("OSCILLATOR") ? "CRYSTAL"
            : evidence.Contains("CONNECTOR") ? "CONNECTOR"
            : evidence.Contains("SENSOR") ? "SENSOR"
            : evidence.Contains("TRANSISTOR") && evidence.Contains("NPN") ? "NPN"
            : evidence.Contains("TRANSISTOR") && evidence.Contains("PNP") ? "PNP"
            : evidence.Contains("TRANSISTOR") ? "BJT"
            : evidence.Contains("LED") ? "LED"
            : evidence.Contains("ZENER") ? "ZENER"
            : evidence.Contains("TVS") ? "TVS"
            : evidence.Contains("DIODE") && evidence.Contains("ARRAY") ? "DIODE ARRAY"
            : null;
    }

    private static bool IsMasterLibrary(string path) => string.Equals(Path.GetFileName(path), "BundledUserSymbols.SchLib", StringComparison.OrdinalIgnoreCase);

    private SchLibrary LoadLibrary(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (_libraryCache.TryGetValue(fullPath, out var cached)) return cached;
        var loaded = (SchLibrary)AltiumLibrary.OpenSchLibAsync(fullPath).GetAwaiter().GetResult();
        _libraryCache.Add(fullPath, loaded);
        return loaded;
    }
}

public sealed record SymbolLibraryMatch(string Path, string MatchKind, string? ComponentName = null);
