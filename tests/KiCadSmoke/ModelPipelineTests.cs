using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Pcb;

internal static class ModelPipelineTests
{
    private const string Step = "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("FAILED: " + message); Console.WriteLine("PASS: " + message); }
    private static EdaComponent Part(int i)
    {
        var part = new EdaComponent { LcscPartNumber = "C" + i, Name = "TEST" + i, Description = "Resistor", FootprintName = "R0402" };
        part.Properties["pre"] = "R?";
        part.SymbolPins.Add(new("1", "1", 0, 0, "start")); part.SymbolPins.Add(new("2", "2", 0, 180, "end"));
        part.Pads.Add(new("1", -.5, 0, .5, .5, 0, "1", false, 0));
        part.Pads.Add(new("2", .5, 0, .5, .5, 0, "1", false, 0));
        part.Shapes.Add(new("TRACK", "3", [(-.8, -.4), (-.8, .4)], .1524));
        part.ThreeDModel = new(i.ToString("x32"), "MODEL", 0, 0, 0, 0, 0, 0, 1, 1);
        return part;
    }
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "model-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await PriorityAsync(timeout.Token);
        await ReuseAsync(root, timeout.Token);
        await CacheAsync(root, timeout.Token);
        await LoggingAsync(root, timeout.Token);
        await FootprintAsync(root);
        await FamilyStatesAsync(root, timeout.Token);
        await FullWorkflowAsync(timeout.Token);
    }

    private static async Task PriorityAsync(CancellationToken ct)
    {
        var parts = Enumerable.Range(1, 18).Select(Part).ToArray();
        var release = parts.ToDictionary(p => p.ThreeDModel!.Uuid, _ => Signal());
        var started = parts.ToDictionary(p => p.ThreeDModel!.Uuid, _ => Signal());
        var order = new ConcurrentQueue<string>();
        int active = 0, peak = 0;
        await using (var batch = new ModelDownloadBatch(parts, async (model, _, stop) =>
        {
            lock (order) { active++; peak = Math.Max(peak, active); order.Enqueue(model!.Uuid); }
            started[model!.Uuid].SetResult();
            try { await release[model.Uuid].Task.WaitAsync(stop); return null; }
            finally { lock (order) active--; }
        }))
        {
            await Task.WhenAll(parts.Take(3).Select(p => started[p.ThreeDModel!.Uuid].Task)).WaitAsync(ct);
            batch.SetPriority("Resistors", ["C15", "C18"]);
            batch.SetPriority("Capacitors", ["C16"]);
            batch.SetPriority("Diodes", ["C17"]);
            release[parts[0].ThreeDModel!.Uuid].SetResult();
            await started[parts[14].ThreeDModel!.Uuid].Task.WaitAsync(ct);
            release[parts[1].ThreeDModel!.Uuid].SetResult();
            await started[parts[15].ThreeDModel!.Uuid].Task.WaitAsync(ct);
            release[parts[2].ThreeDModel!.Uuid].SetResult();
            await started[parts[16].ThreeDModel!.Uuid].Task.WaitAsync(ct);
            Check(order.Take(6).SequenceEqual(parts.Take(3).Concat(parts.Skip(14).Take(3)).Select(p => p.ThreeDModel!.Uuid)),
                "slow-response model queue prioritizes all three current family dependencies before CSV prefetch/look-ahead");
            batch.SetPriority("Capacitors", ["C14"]);
            release[parts[14].ThreeDModel!.Uuid].SetResult();
            await started[parts[13].ThreeDModel!.Uuid].Task.WaitAsync(ct);
            Check(!started[parts[17].ThreeDModel!.Uuid].Task.IsCompleted, "a newly needed family model outranks another family's look-ahead");
            batch.SetPriority("Resistors", []); batch.SetPriority("Capacitors", []); batch.SetPriority("Diodes", []);
            foreach (var signal in release.Values) signal.TrySetResult();
            await Task.WhenAll(parts.Select(p => batch.GetAsync(p.LcscPartNumber))).WaitAsync(ct);
        }
        Check(peak == 3 && active == 0 && order.Distinct().Count() == 18 && order.Count == 18,
            "reprioritization never restarts in-flight work, loses queued parts, or exceeds three downloads");
    }

    private static async Task ReuseAsync(string root, CancellationToken ct)
    {
        var a = Part(101); var b = Part(102); var c = Part(103);
        b.ThreeDModel = a.ThreeDModel! with { Xmm = 2, Zmm = .5, RotationZDeg = 90 };
        // Same public model name, different UUID: must remain separate, not guessed equivalent.
        int calls = 0;
        await using var batch = new ModelDownloadBatch([a, b, c], async (model, path, stop) =>
        {
            Interlocked.Increment(ref calls);
            var file = Path.Combine(path, "fixture.step");
            await File.WriteAllTextAsync(file, Step, stop);
            return new(model!, "fixture.step", file, Step, 0, 1);
        });
        var first = await batch.GetAsync(a.LcscPartNumber).WaitAsync(ct);
        var second = await batch.GetAsync(b.LcscPartNumber).WaitAsync(ct);
        await batch.GetAsync(c.LcscPartNumber).WaitAsync(ct);
        Check(calls == 2 && batch.UniqueModels == 2 && batch.ReusedParts == 1 && first!.Path == second!.Path,
            "repeated model UUIDs download once while distinct UUIDs remain independent");
        Check(first!.Source.Xmm == 0 && second!.Source.Xmm == 2 && second.Source.RotationZDeg == 90 && second.Source.Zmm == .5,
            "shared STEP payload retains each component's own translation, rotation, and Z offset");
    }

    private static async Task CacheAsync(string root, CancellationToken ct)
    {
        int steps = 0, objs = 0; bool missingObj = false, missingStep = false;
        using var http = new HttpClient(new Handler(request =>
        {
            bool obj = request.RequestUri!.AbsolutePath.Contains("3dmodel");
            if (obj) objs++; else steps++;
            return new HttpResponseMessage(obj && missingObj || !obj && missingStep ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = new StringContent(obj ? "v 0 0 -0.3\nv 1 1 1.2\n" : Step) };
        }));
        var cache = Path.Combine(root, "cache");
        var downloader = new EasyEda3dModelDownloader(http, new(TimeSpan.Zero), cache);
        var part = Part(500);
        await downloader.TryDownloadAsync(part.ThreeDModel, Path.Combine(root, "download1"), ct);
        var reused = await downloader.TryDownloadAsync(part.ThreeDModel! with { RotationXDeg = 180 }, Path.Combine(root, "download2"), ct);
        Check(steps == 1 && objs == 1 && reused!.Source.RotationXDeg == 180 && Math.Abs(reused.ZOffsetMm - .3) < .0001,
            "validated disk cache reuses STEP and OBJ bounds across imports without HTTP or pose changes");
        var path = Directory.GetFiles(cache, "*.json").Single();
        var corrupt = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        corrupt["Step"] = Step.Replace("DATA", "TAMPERED");
        await File.WriteAllTextAsync(path, corrupt.ToJsonString(), ct);
        await downloader.TryDownloadAsync(part.ThreeDModel, Path.Combine(root, "download3"), ct);
        Check(steps == 2 && objs == 2, "STEP checksum mismatch refreshes the cached asset");
        var expired = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        expired["SavedUtc"] = DateTimeOffset.UtcNow.AddDays(-8);
        await File.WriteAllTextAsync(path, expired.ToJsonString(), ct);
        await downloader.TryDownloadAsync(part.ThreeDModel, Path.Combine(root, "download4"), ct);
        Check(steps == 3, "expired model cache refreshes after seven days");
        missingObj = true;
        await downloader.TryDownloadAsync(Part(501).ThreeDModel, Path.Combine(root, "download5"), ct);
        missingObj = false;
        await downloader.TryDownloadAsync(Part(501).ThreeDModel, Path.Combine(root, "download6"), ct);
        Check(steps == 4 && objs == 5, "unavailable OBJ bounds retry later without downloading STEP again");
        missingStep = true;
        await downloader.TryDownloadAsync(Part(502).ThreeDModel, Path.Combine(root, "download7"), ct);
        missingStep = false;
        await downloader.TryDownloadAsync(Part(502).ThreeDModel, Path.Combine(root, "download8"), ct);
        Check(steps == 6, "a missing STEP is not negatively cached");
        var blockedCache = Path.Combine(root, "cache-not-a-directory");
        await File.WriteAllTextAsync(blockedCache, "fixture", ct);
        var withoutCache = await new EasyEda3dModelDownloader(http, new(TimeSpan.Zero), blockedCache)
            .TryDownloadAsync(Part(503).ThreeDModel, Path.Combine(root, "download9"), ct);
        Check(withoutCache is not null, "unwritable model cache does not block model export");
    }

    private static async Task LoggingAsync(string root, CancellationToken ct)
    {
        var disabledPath = Path.Combine(root, "disabled-logs");
        using (ImportDiagnostics.Begin(false, "test", disabledPath)) ImportDiagnostics.Record("not-written");
        Check(!Directory.Exists(disabledPath), "disabled logging creates no files");
        string log;
        using (var diagnostics = ImportDiagnostics.Begin(true, "test", Path.Combine(root, "logs"), TimeSpan.FromMilliseconds(20))!)
        {
            log = diagnostics.FilePath!;
            using (ImportDiagnostics.Measure("blocked.fixture", new { part = "C500" }))
            {
                await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => ImportDiagnostics.Record("parallel.fixture", new { i }), ct)));
                await Task.Delay(90, ct);
                var text = await ReadLiveAsync(log, ct);
                Check(text.Contains("heartbeat") && text.Contains("blocked.fixture") && text.Contains("elapsedMs"),
                    "live log heartbeats identify unfinished operations during a stall before completion");
            }
            ImportDiagnostics.Failure("private-error.fixture", new IOException("SECRET_RESPONSE_BODY"));
        }
        var lines = await File.ReadAllLinesAsync(log, ct);
        var records = lines.Select(l => JsonDocument.Parse(l)).ToArray();
        try
        {
            Check(records.Count(r => r.RootElement.GetProperty("name").GetString() == "parallel.fixture") == 30 &&
                records.Last().RootElement.GetProperty("name").GetString() == "session.end" &&
                !string.Join('\n', lines).Contains("SECRET_RESPONSE_BODY"),
                "concurrent diagnostic events remain valid JSON lines, end cleanly, and exclude exception response bodies");
        }
        finally { foreach (var record in records) record.Dispose(); }
        var blocked = Path.Combine(root, "logs-not-a-directory"); await File.WriteAllTextAsync(blocked, "fixture", ct);
        using var failedLog = ImportDiagnostics.Begin(true, "test", blocked)!;
        ImportDiagnostics.Record("safe-after-log-failure");
        Check(failedLog.Error is not null, "unwritable diagnostic directory reports failure without throwing into the importer");
    }

    private static async Task FootprintAsync(string root)
    {
        var output = Path.Combine(root, "footprints");
        var batch = new AltiumExportBatch();
        using var log = ImportDiagnostics.Begin(true, "reuse", Path.Combine(root, "logs"))!;
        await batch.UpsertAsync(Part(601), output, null, null, true);
        await batch.UpsertAsync(Part(602), output, null, null, true);
        var changed = Part(603);
        changed.Shapes[0] = changed.Shapes[0] with { PointsMm = [(-.9, -.4), (-.9, .4)] };
        await batch.UpsertAsync(changed, output, null, null, true);
        await batch.CheckpointAsync();
        var text = await ReadLiveAsync(log.FilePath!, CancellationToken.None);
        Check(text.Split("\"footprint.reused\"").Length - 1 == 1 && text.Split("\"footprint.built\"").Length - 1 == 2,
            "identical footprint geometry reuses the entry, but changed coordinates under the same name rebuild it");
        var pcb = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(Path.Combine(output, "youeda.PcbLib"));
        Check(Math.Abs(pcb["R0402"]!.Tracks[0].Start.X.ToMm() + .9) < .00001,
            "footprint reuse does not suppress a real geometry update");
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(handle(request));
    }
    private static async Task<string> ReadLiveAsync(string path, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task FamilyStatesAsync(string root, CancellationToken ct)
    {
        var parts = new[] { Part(701), Part(702), Part(703) };
        parts[1].Description = "Capacitor"; parts[1].Properties["pre"] = "C?";
        parts[2].Description = "Inductor"; parts[2].Properties["pre"] = "L?";
        var plan = FamilyLibraryExportPlan.Create(Path.Combine(root, "state-output"), parts, Path.Combine(root, "reference"));
        var release = Signal(); var allWaiting = Signal();
        bool sawSaving = false, sawConverting = false;
        await using var models = new ModelDownloadBatch(parts, async (_, _, stop) => { await release.Task.WaitAsync(stop); return null; });
        var session = new FamilyConversionSession(plan, null, models, 3);
        var task = session.RunAsync(parts.Select((p, i) => new FamilyConversionItem(i, p, true, true, null)).ToArray(),
            new InlineProgress<FamilyConversionProgress>(p =>
            {
                if (p.WaitingForModels == 3 && p.Converting == 0 && p.Saving == 0) allWaiting.TrySetResult();
                if (p.Saving > 0) sawSaving = true;
                if (p.Converting > 0) sawConverting = true;
            }), ct);
        await allWaiting.Task.WaitAsync(ct);
        Check(!task.IsCompleted, "three assigned family jobs are correctly reported as waiting, not busy converting");
        release.SetResult(); await task;
        Check(sawSaving && sawConverting && session.Succeeded.Count == 3,
            "family state transitions cover waiting, converting, checkpoint saving, and completion");
    }
    private static async Task FullWorkflowAsync(CancellationToken ct)
    {
        var ready = Signal(); bool exported = false;
        var run = FullImportWorkflow.RunAsync(async () => { await ready.Task.WaitAsync(ct); return true; },
            () => { exported = true; return Task.CompletedTask; });
        Check(!exported, "full import waits for joined lookup/pricing before export");
        ready.SetResult(); await run;
        Check(exported, "full import automatically continues to library generation after lookup");
        exported = false;
        await FullImportWorkflow.RunAsync(() => Task.FromResult(false), () => { exported = true; return Task.CompletedTask; });
        Check(!exported, "cancelled or empty lookup prevents automatic library export");
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
