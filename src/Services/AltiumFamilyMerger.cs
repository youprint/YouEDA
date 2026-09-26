using System.IO;
using System.Text.Json;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Pcb;
using OriginalCircuit.Altium.Models.Sch;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Merge already-verified family artifacts, not a second conversion of source geometry.</summary>
public sealed class AltiumFamilyMerger
{
    public async Task MergeAsync(string outputDirectory, FamilyLibraryExportPlan plan,
        IReadOnlyList<FamilyConversionItem> successes, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (successes.Count == 0) return;
        using var operation = ImportDiagnostics.Measure("libraries.merge", new { outputDirectory, parts = successes.Count, plan.RunDirectory });
        var schExporter = new AltiumSchExporter();
        var targetSch = await schExporter.OpenSharedSchLibAsync(outputDirectory);
        PcbLibrary? targetPcb = null;
        var sources = new Dictionary<string, (SchLibrary Sch, PcbLibrary? Pcb)>(StringComparer.OrdinalIgnoreCase);
        var footprintWinners = successes.Where(i => i.IncludeFootprint).OrderBy(i => i.Order)
            .GroupBy(i => AltiumFootprintNaming.NameFor(i.Component), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        // Resolve duplicate footprint names by CSV order, not nondeterministic worker completion.
        foreach (var item in successes.OrderBy(i => i.Order))
        {
            ct.ThrowIfCancellationRequested();
            var source = item.Component;
            var directory = plan.FamilyDirectory(source);
            if (!sources.TryGetValue(directory, out var libraries))
            {
                var pcbPath = Path.Combine(directory, "youeda.PcbLib");
                libraries = ((SchLibrary)await AltiumLibrary.OpenSchLibAsync(Path.Combine(directory, "youeda.SchLib")),
                    File.Exists(pcbPath) ? (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(pcbPath) : null);
                sources.Add(directory, libraries);
            }
            progress?.Report($"Merging {source.LcscPartNumber} from {plan.ClassificationFor(source).Family}");
            var name = AltiumSchExporter.LibraryComponentName(source);
            if (libraries.Sch[name] is not SchComponent symbol) throw new InvalidDataException($"Missing family symbol: {name}");
            if (source.LcscPartNumber != name) targetSch.Remove(source.LcscPartNumber);
            if (source.Name != name) targetSch.Remove(source.Name);
            // Preserve the already-prepared symbol, including parameters, widths, and footprint choices.
            targetSch.Remove(name);
            targetSch.Add(symbol);
            if (!item.IncludeFootprint) continue;
            var footprintName = AltiumFootprintNaming.NameFor(source);
            if (!ReferenceEquals(footprintWinners[footprintName], item)) continue;
            if (libraries.Pcb?[footprintName] is not PcbComponent footprint)
                throw new InvalidDataException($"Missing family footprint: {footprintName}");
            targetPcb ??= await new AltiumV2Exporter().OpenSharedPcbLibAsync(outputDirectory);
            MergeModels(targetPcb, libraries.Pcb, footprint);
            targetPcb.Remove(source.LcscPartNumber);
            targetPcb.Remove(footprintName);
            targetPcb.Add(footprint);
        }
        AltiumSchExporter.MigrateUnsafeLibraryNames(targetSch);
        ct.ThrowIfCancellationRequested();
        progress?.Report("Verifying combined libraries before publishing…");
        ImportDiagnostics.Record("libraries.verifying");
        // Both files are staged and verified before either destination is changed. Keep the
        // previous files beside the family run so a failed replacement can be recovered.
        var stagedSch = Path.Combine(plan.RunDirectory, "combined-new.SchLib");
        var stagedPcb = Path.Combine(plan.RunDirectory, "combined-new.PcbLib");
        await targetSch.SaveAsync(stagedSch);
        var readSch = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(stagedSch);
        if (readSch.Components.Count() != targetSch.Components.Count() || targetSch.Components.OfType<SchComponent>().Any(s =>
            readSch[s.Name!] is not SchComponent written || written.Pins.Count != s.Pins.Count ||
            !written.Implementations.Select(i => (i.ModelType, i.ModelName)).SequenceEqual(s.Implementations.Select(i => (i.ModelType, i.ModelName)))))
            throw new InvalidDataException("Combined schematic verification failed.");
        if (targetPcb is not null)
        {
            await targetPcb.SaveAsync(stagedPcb);
            var readPcb = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(stagedPcb);
            if (readPcb.Components.Count() != targetPcb.Components.Count() || targetPcb.Components.Any(p => !readPcb.Contains(p.Name)) ||
                targetPcb.Models.Any(m => !readPcb.Models.Any(v => v.Id == m.Id && v.StepData == m.StepData)))
                throw new InvalidDataException("Combined footprint/model verification failed.");
        }
        ct.ThrowIfCancellationRequested();
        var paths = new List<(string Stage, string Target)> { (stagedSch, Path.Combine(outputDirectory, "youeda.SchLib")) };
        if (targetPcb is not null) paths.Add((stagedPcb, Path.Combine(outputDirectory, "youeda.PcbLib")));
        var backupDirectory = Path.Combine(plan.RunDirectory, "Previous combined libraries", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDirectory);
        ImportDiagnostics.Record("libraries.backup", new { backupDirectory });
        foreach (var (_, target) in paths)
            if (File.Exists(target)) File.Copy(target, Path.Combine(backupDirectory, Path.GetFileName(target)), overwrite: false);
        var replaced = new List<string>();
        try
        {
            foreach (var (stage, target) in paths) { File.Move(stage, target, overwrite: true); replaced.Add(target); ImportDiagnostics.Record("library.published", new { target }); }
        }
        catch (Exception publishError)
        {
            ImportDiagnostics.Failure("libraries.publish_failed", publishError, new { backupDirectory });
            var errors = new List<Exception> { publishError };
            foreach (var target in replaced)
            {
                try
                {
                    var backup = Path.Combine(backupDirectory, Path.GetFileName(target));
                    if (File.Exists(backup)) File.Copy(backup, target, overwrite: true);
                    else File.Delete(target);
                    ImportDiagnostics.Record("library.restored", new { target });
                }
                catch (Exception restoreError) { errors.Add(restoreError); }
            }
            throw new AggregateException($"Combined publish failed. Previous files are retained in {backupDirectory}.", errors);
        }
    }

    private static void MergeModels(PcbLibrary target, PcbLibrary source, PcbComponent footprint)
    {
        foreach (var group in footprint.ComponentBodies.OfType<PcbComponentBody>()
                     .Where(b => !string.IsNullOrWhiteSpace(b.ModelId)).GroupBy(b => b.ModelId!).ToArray())
        {
            var model = source.Models.FirstOrDefault(m => string.Equals(m.Id, group.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Missing STEP model {group.Key} in source family.");
            var existing = target.Models.FirstOrDefault(m => string.Equals(m.Id, model.Id, StringComparison.OrdinalIgnoreCase));
            var equivalent = target.Models.FirstOrDefault(m => m.Name == model.Name && m.StepData == model.StepData &&
                m.IsEmbedded == model.IsEmbedded && m.ModelSource == model.ModelSource && m.RotationX == model.RotationX &&
                m.RotationY == model.RotationY && m.RotationZ == model.RotationZ && m.Dz == model.Dz && m.Checksum == model.Checksum);
            if (equivalent is not null)
            {
                foreach (var body in group) body.ModelId = equivalent.Id;
                continue;
            }
            // Copy all public model metadata. Never change the family source model or an older
            // model referenced by an unrelated footprint in the combined library.
            var copy = JsonSerializer.Deserialize<PcbModel>(JsonSerializer.Serialize(model))!;
            if (existing is not null) copy.Id = Guid.NewGuid().ToString("B").ToUpperInvariant();
            target.Models.Add(copy);
            foreach (var body in group) body.ModelId = copy.Id;
        }
    }
}
