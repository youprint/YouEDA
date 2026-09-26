using System.Diagnostics;
using System.Threading.Channels;
using EasyEdaAltiumGrabber.Models;

namespace EasyEdaAltiumGrabber.Services;

public sealed record ComponentLookupItem(string PartNumber, string RawPayload, EdaComponent Component);
public sealed record BatchLookupProgress(int Total, int CadFinished, int Resolved, int ActiveCad,
    int PricesFinished, int ActivePrices, int Cached, TimeSpan Elapsed, string Detail, ComponentLookupItem? Ready);
public sealed record BatchLookupResult(IReadOnlyList<ComponentLookupItem> Items, IReadOnlyList<string> Failures,
    IReadOnlyList<string> PriceFailures, int PricesFinished, bool Cancelled);

/// <summary>
/// Three CAD workers feed two independent price workers. Await every worker before returning,
/// including cancellation, so no pending price mutation can race a subsequent library export.
/// Callbacks run on the calling synchronization context (the WPF dispatcher in the desktop app).
/// </summary>
public sealed class BatchComponentLookup
{
    public const int CadConcurrency = 3;
    public const int PriceConcurrency = 2;

    public async Task<BatchLookupResult> RunAsync(IReadOnlyList<string> parts,
        Func<string, Action<string>, CancellationToken, Task<string>> fetch,
        Func<string, string, EdaComponent> parse,
        Func<EdaComponent, CancellationToken, Task> price,
        Action<BatchLookupProgress>? progress = null, CancellationToken ct = default)
    {
        var prices = Channel.CreateUnbounded<EdaComponent>();
        var results = new Dictionary<string, ComponentLookupItem>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var priceFailures = new List<string>();
        var sync = new object();
        var watch = Stopwatch.StartNew();
        int next = -1, cadFinished = 0, activeCad = 0, priceFinished = 0, activePrices = 0, cached = 0;

        void Report(string detail = "", ComponentLookupItem? ready = null)
        {
            lock (sync)
                progress?.Invoke(new(parts.Count, cadFinished, results.Count, activeCad,
                    priceFinished, activePrices, cached, watch.Elapsed, detail, ready));
        }

        async Task CadWorker()
        {
            while (!ct.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= parts.Count) return;
                var part = parts[index];
                using var operation = ImportDiagnostics.Measure("cad.lookup", new { part });
                Interlocked.Increment(ref activeCad);
                Report($"{part}: fetching CAD");
                try
                {
                    var raw = await fetch(part, message =>
                    {
                        if (message.StartsWith("Using fresh cached", StringComparison.Ordinal)) Interlocked.Increment(ref cached);
                        Report($"{part}: {message}");
                    }, ct);
                    EdaComponent component;
                    using (ImportDiagnostics.Measure("cad.parse", new { part })) component = await Task.Run(() => parse(part, raw), ct);
                    var item = new ComponentLookupItem(part, raw, component);
                    lock (sync) results.Add(part, item);
                    Report($"{part}: CAD ready", item);
                    // This queue does not hold a CAD worker until the price lookup finishes.
                    prices.Writer.TryWrite(component);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception exception) { ImportDiagnostics.Failure("cad.failed", exception, new { part }); lock (sync) failures.Add($"{part}: {exception.Message}"); }
                finally
                {
                    Interlocked.Decrement(ref activeCad);
                    Interlocked.Increment(ref cadFinished);
                    Report();
                }
            }
        }

        async Task PriceWorker()
        {
            try
            {
                await foreach (var component in prices.Reader.ReadAllAsync(ct))
                {
                    ct.ThrowIfCancellationRequested();
                    using var operation = ImportDiagnostics.Measure("price.lookup", new { part = component.LcscPartNumber });
                    Interlocked.Increment(ref activePrices);
                    Report($"{component.LcscPartNumber}: retrieving price");
                    try { await price(component, ct); Interlocked.Increment(ref priceFinished);
                        ImportDiagnostics.Record("price.result", new { part = component.LcscPartNumber, available = component.Properties.ContainsKey("JLCPCB Unit Price") }); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception exception)
                    {
                        ImportDiagnostics.Failure("price.failed", exception, new { part = component.LcscPartNumber });
                        lock (sync) priceFailures.Add($"{component.LcscPartNumber}: {exception.Message}");
                        Interlocked.Increment(ref priceFinished);
                    }
                    finally { Interlocked.Decrement(ref activePrices); Report(); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        var priceWorkers = Enumerable.Range(0, PriceConcurrency).Select(_ => PriceWorker()).ToArray();
        try { await Task.WhenAll(Enumerable.Range(0, CadConcurrency).Select(_ => CadWorker())); }
        finally { prices.Writer.TryComplete(); await Task.WhenAll(priceWorkers); }
        return new(parts.Where(results.ContainsKey).Select(part => results[part]).ToArray(), failures,
            priceFailures, priceFinished, ct.IsCancellationRequested);
    }
}
