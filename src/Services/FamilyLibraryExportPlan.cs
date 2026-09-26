using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyEdaAltiumGrabber.Models;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Creates isolated, never-overwriting family-library runs beside the selected output.</summary>
public sealed class FamilyLibraryExportPlan
{
    public const string ReferenceLibraryDirectoryName = "Library";
    private readonly Dictionary<string, ComponentFamilyClassification> _classifications;

    private FamilyLibraryExportPlan(string runDirectory, Dictionary<string, ComponentFamilyClassification> classifications)
    {
        RunDirectory = runDirectory;
        _classifications = classifications;
    }

    public string RunDirectory { get; }
    public IReadOnlyDictionary<string, ComponentFamilyClassification> Classifications => _classifications;
    public static bool RequiresOrganizationChoice(int resolvedComponentCount) => resolvedComponentCount > 5;

    public static FamilyLibraryExportPlan Create(string outputDirectory, IEnumerable<EdaComponent> components, string referenceDirectory)
    {
        EnsureOutputIsSafe(outputDirectory, referenceDirectory);
        var classifier = new ComponentFamilyClassifier();
        var classifications = components.ToDictionary(component => component.LcscPartNumber,
            component => classifier.Classify(component), StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(Path.GetFullPath(outputDirectory), "Family Libraries");
        var stem = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var run = Path.Combine(root, stem);
        for (var suffix = 2; Directory.Exists(run) || File.Exists(run); suffix++) run = Path.Combine(root, stem + "-" + suffix);
        Directory.CreateDirectory(run);
        return new FamilyLibraryExportPlan(run, classifications);
    }

    public string FamilyDirectory(EdaComponent component) => Path.Combine(RunDirectory, ClassificationFor(component).Family);

    public ComponentFamilyClassification ClassificationFor(EdaComponent component) =>
        _classifications.TryGetValue(component.LcscPartNumber, out var classification)
            ? classification
            : throw new KeyNotFoundException($"No family classification exists for {component.LcscPartNumber}.");

    public async Task WriteAuditAsync(IEnumerable<EdaComponent> components, CancellationToken cancellationToken = default)
    {
        var rows = components.Select(component => new
        {
            Part = component.LcscPartNumber,
            Classification = ClassificationFor(component),
            component.Name,
            component.Description,
            Package = component.FootprintName
        }).OrderBy(row => row.Classification.Family).ThenBy(row => row.Part, StringComparer.OrdinalIgnoreCase).ToArray();
        var csv = new StringBuilder("LCSC Part,Family,Confidence,Evidence,Name,Description,Package" + Environment.NewLine);
        foreach (var row in rows)
        {
            var values = new[] { row.Part, row.Classification.Family, row.Classification.Confidence.ToString(), row.Classification.Evidence, row.Name, row.Description, row.Package };
            csv.AppendLine(string.Join(',', values.Select(Csv)));
        }
        await File.WriteAllTextAsync(Path.Combine(RunDirectory, "family-classification.csv"), csv.ToString(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(RunDirectory, "import-summary.json"), JsonSerializer.Serialize(new
        {
            schema = "youeda-family-library-run/v1",
            createdUtc = DateTimeOffset.UtcNow,
            totalComponents = rows.Length,
            families = rows.GroupBy(row => row.Classification.Family).OrderBy(group => group.Key)
                .Select(group => new { family = group.Key, components = group.Count() }),
            referenceLibrary = "read-only; not modified"
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    /// <summary>
    /// The shared writers use their normal internal youeda.* names while a run is in progress.
    /// Once every component is written, publish the completed files using the Desktop\Library
    /// paired-family convention without touching that reference directory.
    /// </summary>
    public void FinalizeFamilyFileNames(LibraryExporter.OutputFormat format)
    {
        var kiCadFamilies = new List<(string Name, string Directory, bool Symbol, bool Footprint)>();
        foreach (var family in _classifications.Values.Select(item => item.Family).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.Combine(RunDirectory, family);
            if (!Directory.Exists(directory)) continue;
            if (format is LibraryExporter.OutputFormat.Altium or LibraryExporter.OutputFormat.Both)
            {
                Rename(directory, "youeda.SchLib", family + ".SchLib");
                Rename(directory, "youeda.PcbLib", family + ".PcbLib");
            }
            if (format is LibraryExporter.OutputFormat.KiCad or LibraryExporter.OutputFormat.Both)
            {
                // Preflight before renaming either half; this run is newly owned, never the reference library.
                if (File.Exists(Path.Combine(directory, family + ".kicad_sym")) ||
                    Directory.Exists(Path.Combine(directory, family + ".pretty")))
                    throw new IOException($"Refusing to overwrite completed family library {family}.");
                Rename(directory, "youeda.kicad_sym", family + ".kicad_sym");
                RenameDirectory(directory, "youeda.pretty", family + ".pretty");
                var symbolPath = Path.Combine(directory, family + ".kicad_sym");
                var hasSymbol = File.Exists(symbolPath);
                var hasFootprint = Directory.Exists(Path.Combine(directory, family + ".pretty"));
                if (hasSymbol)
                {
                    // Only rewrite the generated Footprint property, never metadata containing similar text.
                    var text = File.ReadAllText(symbolPath);
                    text = Regex.Replace(text, @"(?m)^(\s*\(property ""Footprint"" "")youeda:",
                        match => match.Groups[1].Value + family + ":");
                    File.WriteAllText(symbolPath, text, new UTF8Encoding(false));
                }
                var item = (Name: family, Directory: directory, Symbol: hasSymbol, Footprint: hasFootprint);
                WriteKiCadTables(directory, [item]);
                kiCadFamilies.Add(item);
            }
        }
        if (kiCadFamilies.Count > 0) WriteKiCadTables(RunDirectory, kiCadFamilies);
    }

    private static void WriteKiCadTables(string directory,
        IEnumerable<(string Name, string Directory, bool Symbol, bool Footprint)> families)
    {
        static string Quote(string value) => "\"" + value.Replace("\\", "/").Replace("\"", "\\\"") + "\"";
        foreach (var symbols in new[] { true, false })
        {
            var entries = families.Where(f => symbols ? f.Symbol : f.Footprint).Select(f =>
                $"  (lib (name {Quote(f.Name)})(type \"KiCad\")(uri {Quote(Path.GetFullPath(Path.Combine(f.Directory, f.Name + (symbols ? ".kicad_sym" : ".pretty"))))})(options \"\")(descr \"YouEDA family library\"))");
            var content = "(" + (symbols ? "sym_lib_table" : "fp_lib_table") + "\n" + string.Join("\n", entries) + "\n)\n";
            File.WriteAllText(Path.Combine(directory, symbols ? "sym-lib-table" : "fp-lib-table"), content, new UTF8Encoding(false));
        }
    }

    public static void EnsureOutputIsSafe(string outputDirectory, string referenceDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new InvalidDataException("Choose an output directory first.");
        var output = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var reference = Path.GetFullPath(referenceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (output.StartsWith(reference, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Family-library output cannot be written inside the read-only Desktop\\Library reference folder.");
    }

    private static string Csv(string? value) => '"' + (value ?? string.Empty).Replace("\"", "\"\"") + '"';

    private static void Rename(string directory, string sourceName, string destinationName)
    {
        var source = Path.Combine(directory, sourceName);
        if (!File.Exists(source)) return;
        var destination = Path.Combine(directory, destinationName);
        if (File.Exists(destination)) throw new IOException($"Refusing to overwrite existing family library {destination}.");
        File.Move(source, destination);
    }

    private static void RenameDirectory(string directory, string sourceName, string destinationName)
    {
        var source = Path.Combine(directory, sourceName);
        if (!Directory.Exists(source)) return;
        var destination = Path.Combine(directory, destinationName);
        if (Directory.Exists(destination)) throw new IOException($"Refusing to overwrite existing family footprint directory {destination}.");
        Directory.Move(source, destination);
    }
}
