using System.Diagnostics;
using System.Net;
using System.Net.Http;
using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;

internal static class BatchLookupTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + message);
        Console.WriteLine("PASS: " + message);
    }
    public static async Task RunAsync()
    {
        Check(ImportSpeedSummary.Format("CAD", 5, 5, 10, TimeSpan.FromSeconds(30))
            .Contains("10.0 parts/min avg · 5/10 processed · elapsed 00:00:30 · ETA ~00:00:30"),
            "speed indicator computes average throughput and remaining-time estimate");
        Check(ImportSpeedSummary.Format("CAD", 0, 0, 351, TimeSpan.FromSeconds(20)).Contains("ETA estimating") &&
              ImportSpeedSummary.Format("CAD", 3, 3, 3, TimeSpan.FromSeconds(30)).Contains("queue finished") &&
              ImportSpeedSummary.Format("CAD", 5, 5, 10, TimeSpan.FromSeconds(60)).Contains("5.0 parts/min"),
            "speed indicator handles startup, completion, and declining throughput during stalls");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var parts = Enumerable.Range(1, 8).Select(i => "C" + i).ToArray();
        var started = Signal(); var releaseCad = Signal(); var allCad = Signal(); var releasePrice = Signal();
        int active = 0, peak = 0, prices = 0, pricePeak = 0;
        var lookup = new BatchComponentLookup().RunAsync(parts, async (part, log, ct) =>
        {
            var count = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref peak, count);
            if (count == 3) started.TrySetResult();
            try { await releaseCad.Task.WaitAsync(ct); return part; }
            finally { Interlocked.Decrement(ref active); }
        }, (part, _) => new EdaComponent { LcscPartNumber = part }, async (component, ct) =>
        {
            InterlockedExtensions.Max(ref pricePeak, Interlocked.Increment(ref prices));
            try { await releasePrice.Task.WaitAsync(ct); component.Properties["price"] = "ready"; }
            finally { Interlocked.Decrement(ref prices); }
        }, update => { if (update.CadFinished == parts.Length) allCad.TrySetResult(); }, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        Check(peak == 3, "three CAD lookups overlap, never exceeding three workers");
        releaseCad.SetResult();
        await allCad.Task.WaitAsync(timeout.Token);
        Check(!lookup.IsCompleted, "all CAD continues while prices wait; completion waits for pricing");
        releasePrice.SetResult();
        var report = await lookup;
        Check(peak == 3 && pricePeak <= 2 && report.Items.Select(i => i.PartNumber).SequenceEqual(parts) &&
              report.Items.All(i => i.Component.Properties["price"] == "ready"), "bounded workers, ordered results, and settled price mutations");

        using var cancel = new CancellationTokenSource();
        var firstReady = Signal();
        var cancelRun = new BatchComponentLookup().RunAsync(parts, async (part, log, ct) =>
        {
            if (part != "C1") await Task.Delay(Timeout.Infinite, ct);
            return part;
        }, (part, _) => new EdaComponent { LcscPartNumber = part }, (_, ct) => Task.Delay(Timeout.Infinite, ct),
            update => { if (update.Ready is not null) firstReady.TrySetResult(); }, cancel.Token);
        await firstReady.Task.WaitAsync(timeout.Token);
        cancel.Cancel();
        var cancelled = await cancelRun.WaitAsync(timeout.Token);
        Check(cancelled.Cancelled && cancelled.Items.Count == 1, "stop search joins workers and preserves completed CAD");
        var failed = await new BatchComponentLookup().RunAsync(parts,
            (part, _, _) => part == "C2" ? Task.FromException<string>(new IOException("fixture")) : Task.FromResult(part),
            (part, _) => new EdaComponent { LcscPartNumber = part },
            (_, _) => Task.FromException(new IOException("price fixture")));
        Check(failed.Items.Count == 7 && failed.Failures.Count == 1 && failed.PriceFailures.Count == 7,
            "per-part CAD and optional pricing failures do not stop other parts");

        var cache = Path.Combine(Path.GetTempPath(), "YouEDA-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        const string json = "{\"success\":true,\"result\":{\"fixture\":true}}";
        var calls = 0;
        var status = HttpStatusCode.OK;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        })) { BaseAddress = new Uri("https://fixture.invalid/") };
        var scraper = new LcscScraper(httpClient: http, cacheDirectory: cache, requestGate: new(TimeSpan.Zero));
        try
        {
            await scraper.GetRawComponentAsync("C1");
            await scraper.GetRawComponentAsync("C1");
            Check(calls == 1, "fresh validated CAD cache avoids a second HTTP request");
            await scraper.GetRawComponentAsync("C1", forceRefresh: true);
            Check(calls == 2, "explicit refresh bypasses fresh CAD cache");
            File.SetLastWriteTimeUtc(Path.Combine(cache, "C1.json"), DateTime.UtcNow.AddDays(-8));
            await scraper.GetRawComponentAsync("C1");
            Check(calls == 3, "expired CAD cache requests fresh data");
            await File.WriteAllTextAsync(Path.Combine(cache, "C2.json"), "broken");
            await scraper.GetRawComponentAsync("C2");
            Check(calls == 4, "invalid cached JSON is refreshed");
            status = HttpStatusCode.Forbidden;
            Check(await scraper.GetRawComponentAsync("C1", forceRefresh: true) == json && calls == 5,
                "403 is not retried and a validated prior payload remains available");
            using var alreadyCancelled = new CancellationTokenSource();
            alreadyCancelled.Cancel();
            try { await scraper.GetRawComponentAsync("C1", alreadyCancelled.Token); throw new Exception("cancellation swallowed"); }
            catch (OperationCanceledException) { Check(calls == 5, "user cancellation does not fall back to cached data"); }
        }
        finally { Directory.Delete(cache, true); }

        // Exercise the actual retry path, not only the standalone pacing gate.
        var retryCache = Path.Combine(Path.GetTempPath(), "YouEDA-retry-test-" + Guid.NewGuid().ToString("N"));
        int retries = 0;
        using var retryHttp = new HttpClient(new Handler((_, _) =>
        {
            var attempt = Interlocked.Increment(ref retries);
            var response = new HttpResponseMessage(attempt == 1 ? HttpStatusCode.TooManyRequests :
                attempt == 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(json) };
            if (attempt == 1) response.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(120));
            if (attempt == 2) response.Headers.RetryAfter = new(DateTimeOffset.UtcNow.AddMilliseconds(120));
            return Task.FromResult(response);
        })) { BaseAddress = new Uri("https://fixture.invalid/") };
        try
        {
            var retryWatch = Stopwatch.StartNew();
            var retryScraper = new LcscScraper(httpClient: retryHttp, cacheDirectory: retryCache, requestGate: new(TimeSpan.Zero));
            await retryScraper.GetRawComponentAsync("C42", timeout.Token);
            Check(retries == 3 && retryWatch.ElapsedMilliseconds >= 210,
                "CAD 429/503 retries honor both Retry-After delay and date forms");
        }
        finally { if (Directory.Exists(retryCache)) Directory.Delete(retryCache, true); }

        var gate = new ComponentRequestGate(TimeSpan.FromMilliseconds(70));
        await gate.WaitAsync(timeout.Token);
        var watch = Stopwatch.StartNew();
        var waiting = gate.WaitAsync(timeout.Token);
        gate.BackOff(TimeSpan.FromMilliseconds(180));
        await waiting;
        Check(watch.ElapsedMilliseconds >= 160, "shared backoff extends an already waiting request's deadline");
        watch.Restart();
        await gate.WaitAsync(timeout.Token);
        Check(watch.ElapsedMilliseconds >= 55, "shared rate limit spaces request starts after cooldown");

        var downloadsStarted = Signal(); var releaseDownload = Signal(); int downloads = 0, downloadPeak = 0;
        var modelParts = parts.Select(part => new EdaComponent { LcscPartNumber = part }).ToArray();
        await using (var models = new ModelDownloadBatch(modelParts, async (_, _, ct) =>
        {
            var count = Interlocked.Increment(ref downloads);
            InterlockedExtensions.Max(ref downloadPeak, count);
            if (count == 3) downloadsStarted.TrySetResult();
            try { await releaseDownload.Task.WaitAsync(ct); return null; }
            finally { Interlocked.Decrement(ref downloads); }
        }))
        {
            await downloadsStarted.Task.WaitAsync(timeout.Token);
            releaseDownload.SetResult();
            await Task.WhenAll(parts.Select(models.GetAsync));
            Check(downloadPeak == 3 && downloads == 0, "conversion prefetch overlaps exactly three model workers");
        }
        var disposeStarted = Signal(); int disposedWorkers = 0;
        var stoppingModels = new ModelDownloadBatch(modelParts, async (_, _, ct) =>
        {
            Interlocked.Increment(ref disposedWorkers); disposeStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return null; }
            finally { Interlocked.Decrement(ref disposedWorkers); }
        });
        await disposeStarted.Task.WaitAsync(timeout.Token);
        await stoppingModels.DisposeAsync();
        Check(disposedWorkers == 0, "model batch disposal cancels and joins all in-flight requests");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int previous;
            do { previous = Volatile.Read(ref target); if (value <= previous) return; }
            while (Interlocked.CompareExchange(ref target, value, previous) != previous);
        }
    }
}
