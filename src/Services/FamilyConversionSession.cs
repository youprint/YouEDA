using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using EasyEdaAltiumGrabber.Models;

namespace EasyEdaAltiumGrabber.Services;

public sealed record FamilyConversionItem(int Order, EdaComponent Component, bool IncludeFootprint,
    bool Include3d, SymbolLibraryMatch? Symbol);
public sealed record FamilyConversionProgress(int Processed, int Succeeded, int ActiveFamilies, string Detail,
    int Converting = 0, int WaitingForModels = 0, int Saving = 0)
{
    public string WorkerSummary(int maximum) =>
        $"Families: {Converting} converting · {WaitingForModels} waiting for 3D · {Saving} saving ({ActiveFamilies}/{maximum} assigned)";
}

/// <summary>Exclusive ownership of each family; all workers share one model-download batch.</summary>
public sealed class FamilyConversionSession(FamilyLibraryExportPlan plan, string? symbolDirectory,
    ModelDownloadBatch models, int workerCount)
{
    private readonly object _sync = new();
    private readonly List<FamilyConversionItem> _succeeded = [];
    private readonly List<string> _failures = [];
    private enum WorkerPhase { Converting, WaitingForModel, Saving }
    private readonly Dictionary<string, WorkerPhase> _phases = new(StringComparer.OrdinalIgnoreCase);
    private int _processed;
    public IReadOnlyList<FamilyConversionItem> Succeeded { get { lock (_sync) return _succeeded.OrderBy(i => i.Order).ToArray(); } }
    public IReadOnlyList<string> Failures { get { lock (_sync) return _failures.ToArray(); } }

    public async Task RunAsync(IReadOnlyList<FamilyConversionItem> items,
        IProgress<FamilyConversionProgress>? progress = null, CancellationToken ct = default)
    {
        if (workerCount is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(workerCount));
        var families = items.GroupBy(i => plan.ClassificationFor(i.Component).Family)
            .OrderByDescending(g => g.Count()).Select(g => g.OrderBy(i => i.Order).ToArray()).ToArray();
        var errors = new ConcurrentQueue<Exception>();
        ImportDiagnostics.Record("families.schedule", new { workerCount, families = families.Select(f => new {
            family = plan.ClassificationFor(f[0].Component).Family, count = f.Length }).ToArray() });
        void Prioritize(FamilyConversionItem[] familyItems, int index) =>
            models.SetPriority(plan.ClassificationFor(familyItems[0].Component).Family,
                familyItems.Skip(index).Where(i => i.Include3d && i.IncludeFootprint).Select(i => i.Component.LcscPartNumber));
        // Register the initial consumers together, before any family can monopolize look-ahead.
        foreach (var familyItems in families.Take(workerCount)) Prioritize(familyItems, 0);
        void Report(string detail, string? family = null, WorkerPhase? phase = null)
        {
            lock (_sync)
            {
                if (family is not null)
                {
                    if (phase.HasValue) _phases[family] = phase.Value;
                    else _phases.Remove(family);
                }
                progress?.Report(new(_processed, _succeeded.Count, _phases.Count, detail,
                    _phases.Values.Count(p => p == WorkerPhase.Converting),
                    _phases.Values.Count(p => p == WorkerPhase.WaitingForModel),
                    _phases.Values.Count(p => p == WorkerPhase.Saving)));
                ImportDiagnostics.Record("family.state", new { detail, processed = _processed, succeeded = _succeeded.Count,
                    states = _phases.Select(p => new { family = p.Key, phase = p.Value.ToString() }).ToArray() });
            }
        }
        await FamilyWorkScheduler.RunAsync(families, workerCount, async familyItems =>
        {
            var family = plan.ClassificationFor(familyItems[0].Component).Family;
            var directory = plan.FamilyDirectory(familyItems[0].Component);
            var batch = new AltiumExportBatch();
            Report($"{family}: started", family, WorkerPhase.Converting);
            try
            {
                var sinceCheckpoint = 0;
                for (var index = 0; index < familyItems.Length; index++)
                {
                    var item = familyItems[index];
                    ct.ThrowIfCancellationRequested();
                    Prioritize(familyItems, index);
                    try
                    {
                        if (item.Include3d && item.IncludeFootprint)
                        {
                            var model = models.GetAsync(item.Component.LcscPartNumber);
                            if (!model.IsCompleted)
                                Report($"{family}: waiting for 3D {item.Component.LcscPartNumber}", family, WorkerPhase.WaitingForModel);
                            // The exporter reads the same settled task. Keep optional-model failure
                            // handling identical to the serial exporter; never start another request.
                            try { using var wait = ImportDiagnostics.Measure("family.wait-model", new { family, part = item.Component.LcscPartNumber }); await model.WaitAsync(ct); }
                            catch (Exception exception) when (exception is IOException or HttpRequestException) { }
                        }
                        Report($"{family}: generating {item.Component.LcscPartNumber}", family, WorkerPhase.Converting);
                        await new LibraryExporter().ExportImportPlanAsync(item.Component, directory,
                            item.Include3d, symbolDirectory, item.Symbol, ct,
                            includeFootprint: item.IncludeFootprint, batch: batch, modelDownloads: models);
                        lock (_sync) _succeeded.Add(item);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        ImportDiagnostics.Failure("family.part_failed", exception, new { family, part = item.Component.LcscPartNumber });
                        lock (_sync) _failures.Add($"{item.Component.LcscPartNumber}: {exception.Message}");
                    }
                    lock (_sync) _processed++;
                    Report($"{family}: processed {item.Component.LcscPartNumber}");
                    if (++sinceCheckpoint >= AltiumExportBatch.CheckpointInterval)
                    {
                        Report($"{family}: saving and verifying checkpoint", family, WorkerPhase.Saving);
                        await batch.CheckpointAsync();
                        sinceCheckpoint = 0;
                    }
                }
                Report($"{family}: saving final checkpoint", family, WorkerPhase.Saving);
                await batch.CheckpointAsync();
                Report($"{family}: saved and verified");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                ImportDiagnostics.Failure("family.checkpoint_failed", exception, new { family });
                // Other families can finish, but no combined merge is allowed after a failed checkpoint.
                errors.Enqueue(new IOException($"{family} checkpoint failed: {exception.Message}", exception));
            }
            finally
            {
                models.SetPriority(family, []);
                Report($"{family}: worker finished", family);
            }
        }, ct);
        if (!errors.IsEmpty) throw new AggregateException("Family output was not fully verified; combined libraries were not merged.", errors);
    }
}

/// <summary>Bounded CPU workers. A finished worker takes the next whole family, never half a family.</summary>
public static class FamilyWorkScheduler
{
    public static Task RunAsync<T>(IReadOnlyList<T> families, int concurrency, Func<T, Task> process, CancellationToken ct = default)
    {
        if (concurrency < 1 || concurrency > 3) throw new ArgumentOutOfRangeException(nameof(concurrency));
        int next = -1;
        return Task.WhenAll(Enumerable.Range(0, Math.Min(concurrency, families.Count)).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref next);
                if (index >= families.Count) return;
                await process(families[index]);
            }
        }, ct)));
    }
}
