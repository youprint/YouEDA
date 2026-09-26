using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using EasyEdaAltiumGrabber.Controls;
using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;

namespace EasyEdaAltiumGrabber.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _partNumber = "C32346";
    private string _batchPartNumbers = "";
    private string _altiumOutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EasyEdaAltium");
    private string _kiCadOutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EasyEdaKiCad");
    private readonly string _userSymbolDirectory = BuildSymbolSearchPath();
    private readonly AppSettingsStore _settingsStore = new();
    private string _statusText = "Enter an LCSC code, then search the public EasyEDA component library.";
    private string _busy = "";
    private bool _fullImportRunning;
    private CancellationTokenSource? _searchCancellation;
    private bool _refreshCadFromServer;
    private int _familyWorkerCount = 3;
    private bool _diagnosticLogging;
    private string _diagnosticLogStatus = "No diagnostic log yet";
    private string? _lastLookupLog;
    public bool DiagnosticLogging { get => _diagnosticLogging; set { _diagnosticLogging = value; On(); On(nameof(ImportSettingsSummary)); PersistSettings(); } }
    public string DiagnosticLogStatus { get => _diagnosticLogStatus; private set { _diagnosticLogStatus = value; On(); } }
    public string? LastDiagnosticLog { get; private set; }
    public ICommand OpenDiagnosticLogsCommand { get; }
    public IReadOnlyList<int> FamilyWorkerOptions { get; } = [1, 3];
    public int FamilyWorkerCount { get => _familyWorkerCount; set { _familyWorkerCount = value == 1 ? 1 : 3; On(); On(nameof(ImportSettingsSummary)); } }
    private readonly DispatcherTimer _speedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Func<string>? _speedSnapshot;
    private string _speedText = "";
    public string SpeedText { get => _speedText; private set { _speedText = value; On(); } }
    public bool RefreshCadFromServer { get => _refreshCadFromServer; set { _refreshCadFromServer = value; On(); On(nameof(ImportSettingsSummary)); } }
    public string ImportSettingsSummary =>
        $"{(RefreshCadFromServer ? "Server refresh ON — cache bypassed" : "Reuse fresh CAD cache")} · {FamilyWorkerCount} Altium family worker(s) · Logs {(DiagnosticLogging ? "on" : "off")}";
    private string _selectedExportFormat = "Altium";
    private string _selectedTheme = ThemeService.MidnightBlueprint;
    private ComponentSearchRow? _selectedResult;
    private bool _showProgress;
    private int _progressValue;
    private int _progressMaximum = 1;

    public ObservableCollection<ComponentSearchRow> Results { get; } = [];
    public string PartNumber { get => _partNumber; set { _partNumber = value; On(); RefreshCommands(); } }
    public string BatchPartNumbers { get => _batchPartNumbers; set { _batchPartNumbers = value; On(); RefreshCommands(); } }
    public string OutputDirectory
    {
        get => SelectedExportFormat == "KiCad" ? _kiCadOutputDirectory : _altiumOutputDirectory;
        set
        {
            if (SelectedExportFormat == "KiCad") KiCadOutputDirectory = value;
            else AltiumOutputDirectory = value;
        }
    }
    public string AltiumOutputDirectory { get => _altiumOutputDirectory; set { _altiumOutputDirectory = value; On(); On(nameof(OutputDirectory)); PersistSettings(); RefreshCommands(); } }
    public string KiCadOutputDirectory { get => _kiCadOutputDirectory; set { _kiCadOutputDirectory = value; On(); On(nameof(OutputDirectory)); PersistSettings(); RefreshCommands(); } }
    public string UserSymbolDirectory => _userSymbolDirectory;
    public string SelectedExportFormat
    {
        get => _selectedExportFormat;
        set
        {
            _selectedExportFormat = value == "KiCad" ? "KiCad" : "Altium";
            On();
            On(nameof(SelectedExportTab));
            On(nameof(OutputDirectory));
            PersistSettings();
            RefreshCommands();
        }
    }
    public int SelectedExportTab
    {
        get => SelectedExportFormat == "KiCad" ? 1 : 0;
        set => SelectedExportFormat = value == 1 ? "KiCad" : "Altium";
    }
    public IReadOnlyList<string> AvailableThemes => ThemeService.Names;
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            var normalized = ThemeService.Normalize(value);
            if (_selectedTheme == normalized) return;
            _selectedTheme = normalized;
            ThemeService.Apply(_selectedTheme);
            On();
            PersistSettings();
        }
    }
    public string StatusText { get => _statusText; private set { _statusText = value; On(); } }
    public bool ShowProgress { get => _showProgress; private set { _showProgress = value; On(); } }
    public int ProgressValue { get => _progressValue; private set { _progressValue = value; On(); } }
    public int ProgressMaximum { get => _progressMaximum; private set { _progressMaximum = Math.Max(1, value); On(); } }
    public bool IsBusy => _fullImportRunning || !string.IsNullOrEmpty(_busy);
    public bool CanChangeDiagnostics => !IsBusy;
    public bool CanSearch => !IsBusy && RequestedPartNumbers().Any();
    public bool CanExport => !IsBusy && Results.Any(row => row.ParsedComponent is not null);

    public ComponentSearchRow? SelectedResult
    {
        get => _selectedResult;
        set { _selectedResult = value; On(); On(nameof(PreviewComponent)); RefreshCommands(); }
    }

    public EdaComponent? PreviewComponent => SelectedResult?.ParsedComponent;
    public ICommand SearchCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand AddToLibraryCommand { get; }
    public ICommand SaveModelCommand { get; }
    public ICommand OpenOutputCommand { get; }
    public ICommand OpenInAltiumCommand { get; }
    public ICommand AddToAltiumCommand { get; }
    public ICommand AddToKiCadCommand { get; }
    public ICommand SaveAltiumSourceCommand { get; }
    public ICommand SaveKiCadSourceCommand { get; }
    public ICommand OpenAltiumOutputCommand { get; }
    public ICommand OpenKiCadOutputCommand { get; }
    public ICommand OpenAltiumLibrariesCommand { get; }
    public ICommand ImportCsvCommand { get; }
    public ICommand RunFullImportCommand { get; }
    public MainViewModel()
    {
        _speedTimer.Tick += (_, _) => { if (_speedSnapshot is not null) SpeedText = _speedSnapshot(); };
        var settings = _settingsStore.Load();
        _diagnosticLogging = settings.DiagnosticLogging;
        OpenDiagnosticLogsCommand = new ActionCommand(() => OpenOutputDirectory(ImportDiagnostics.DefaultDirectory), () => true);
        _selectedExportFormat = settings.ExportFormat == "KiCad" ? "KiCad" : "Altium";
        _selectedTheme = ThemeService.Normalize(settings.Theme);
        ThemeService.Apply(_selectedTheme);
        if (!string.IsNullOrWhiteSpace(settings.AltiumOutputDirectory)) _altiumOutputDirectory = settings.AltiumOutputDirectory;
        else if (!string.IsNullOrWhiteSpace(settings.OutputDirectory)) _altiumOutputDirectory = settings.OutputDirectory;
        if (!string.IsNullOrWhiteSpace(settings.KiCadOutputDirectory)) _kiCadOutputDirectory = settings.KiCadOutputDirectory;
        // Begin the public LCEDA connection in the background. The first searched component
        // then does not normally pay the DNS/TLS connection cost on the UI's critical path.
        _ = LcscScraper.WarmUpAsync();
        SearchCommand = new AsyncCommand(SearchAsync, () => CanSearch);
        CancelSearchCommand = new ActionCommand(() => { _searchCancellation?.Cancel(); RefreshCommands(); }, () => _busy == "search" && _searchCancellation?.IsCancellationRequested == false);
        AddToLibraryCommand = new AsyncCommand(() => AddToLibraryAsync(SelectedExportFormat == "KiCad" ? LibraryExporter.OutputFormat.KiCad : LibraryExporter.OutputFormat.Altium, OutputDirectory), () => CanExport);
        SaveModelCommand = new AsyncCommand(() => SaveModelAsync(OutputDirectory), () => SelectedResult?.RawPayload is not null && !IsBusy);
        OpenOutputCommand = new ActionCommand(() => OpenOutputDirectory(OutputDirectory), () => !string.IsNullOrWhiteSpace(OutputDirectory));
        OpenInAltiumCommand = new ActionCommand(() => OpenLibrariesInAltium(AltiumOutputDirectory), () => !IsBusy && LibrariesExist(AltiumOutputDirectory));
        AddToAltiumCommand = new AsyncCommand(() => AddToLibraryAsync(LibraryExporter.OutputFormat.Altium, AltiumOutputDirectory), () => CanExport);
        AddToKiCadCommand = new AsyncCommand(() => AddToLibraryAsync(LibraryExporter.OutputFormat.KiCad, KiCadOutputDirectory), () => CanExport);
        SaveAltiumSourceCommand = new AsyncCommand(() => SaveModelAsync(AltiumOutputDirectory), () => SelectedResult?.RawPayload is not null && !IsBusy);
        SaveKiCadSourceCommand = new AsyncCommand(() => SaveModelAsync(KiCadOutputDirectory), () => SelectedResult?.RawPayload is not null && !IsBusy);
        OpenAltiumOutputCommand = new ActionCommand(() => OpenOutputDirectory(AltiumOutputDirectory), () => !string.IsNullOrWhiteSpace(AltiumOutputDirectory));
        OpenKiCadOutputCommand = new ActionCommand(() => OpenOutputDirectory(KiCadOutputDirectory), () => !string.IsNullOrWhiteSpace(KiCadOutputDirectory));
        OpenAltiumLibrariesCommand = new ActionCommand(() => OpenLibrariesInAltium(AltiumOutputDirectory), () => !IsBusy && LibrariesExist(AltiumOutputDirectory));
        ImportCsvCommand = new ActionCommand(ImportCsv, () => !IsBusy);
        RunFullImportCommand = new AsyncCommand(RunFullImportAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(BatchPartNumbers) && RequestedPartNumbers().Any());
    }

    private void PersistSettings() => _settingsStore.Save(new AppUserSettings(
        _selectedExportFormat, null, _altiumOutputDirectory, _kiCadOutputDirectory, _selectedTheme, _diagnosticLogging));

    private void ShowDiagnosticSession(ImportDiagnostics? diagnostics)
    {
        if (diagnostics is null) { DiagnosticLogStatus = "Logs off for this run"; return; }
        LastDiagnosticLog = diagnostics.FilePath;
        On(nameof(LastDiagnosticLog));
        DiagnosticLogStatus = diagnostics.Error ?? "Recording diagnostic log";
    }
    private void FinishDiagnosticSession(ImportDiagnostics? diagnostics)
    {
        diagnostics?.Dispose();
        if (diagnostics is not null) DiagnosticLogStatus = diagnostics.Error ?? "Diagnostic log saved";
    }

    private static string BuildSymbolSearchPath()
    {
        var paths = new List<string>();
        // This compact personal source tree contains the user's vendor/exact symbols. Resolve
        // it before the common catalog so existing artwork and designators are never replaced.
        var personalLibrary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Library");
        if (Directory.Exists(personalLibrary)) paths.Add(personalLibrary);
        paths.Add(Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib"));
        return string.Join(Path.PathSeparator, paths);
    }

    private void StartSpeedIndicator(Func<string> snapshot)
    {
        _speedSnapshot = snapshot;
        SpeedText = snapshot();
        _speedTimer.Start();
    }

    private void StopSpeedIndicator()
    {
        _speedTimer.Stop();
        if (_speedSnapshot is not null) SpeedText = _speedSnapshot();
        _speedSnapshot = null;
    }

    private async Task RunFullImportAsync()
    {
        if (IsBusy) return;
        var format = SelectedExportFormat == "KiCad" ? LibraryExporter.OutputFormat.KiCad : LibraryExporter.OutputFormat.Altium;
        var output = OutputDirectory;
        var selections = Results.ToDictionary(r => r.PartNumber, r => (r.IncludeFootprint, r.Include3D), StringComparer.OrdinalIgnoreCase);
        _fullImportRunning = true;
        RefreshCommands();
        using var diagnostics = ImportDiagnostics.Begin(DiagnosticLogging, "full-import");
        try
        {
            FamilyLibraryExportPlan.EnsureOutputIsSafe(output, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Library"));
            ImportDiagnostics.Record("workflow.start", new { format = format.ToString(), output });
            await FullImportWorkflow.RunAsync(SearchAsync, async () =>
            {
                foreach (var row in Results)
                    if (selections.TryGetValue(row.PartNumber, out var choice))
                    { row.IncludeFootprint = choice.IncludeFootprint && row.ParsedComponent!.HasFootprintData; row.Include3D = choice.Include3D && row.ParsedComponent!.ThreeDModel is not null; }
                await AddToLibraryAsync(format, output, automaticFamilyLibraries: true);
            });
        }
        catch (Exception exception) { ImportDiagnostics.Failure("workflow.failed", exception); StatusText = "Full import error: " + exception.Message; }
        finally { _fullImportRunning = false; RefreshCommands(); }
    }

    private async Task<bool> SearchAsync()
    {
        using var diagnostics = ImportDiagnostics.Begin(DiagnosticLogging, "lookup");
        ShowDiagnosticSession(diagnostics);
        _lastLookupLog = diagnostics?.FilePath;
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        _busy = "search"; RefreshCommands();
        try
        {
            Results.Clear();
            var requested = RequestedPartNumbers().ToArray();
            var refresh = RefreshCadFromServer;
            ImportDiagnostics.Record("lookup.options", new { count = requested.Length, refresh });
            ShowProgress = true;
            ProgressMaximum = requested.Length * 2;
            ProgressValue = 0;
            var timer = Stopwatch.StartNew();
            BatchLookupProgress? latest = null;
            StartSpeedIndicator(() =>
                ImportSpeedSummary.Format("CAD", latest?.Resolved ?? 0, latest?.CadFinished ?? 0, requested.Length, timer.Elapsed) +
                Environment.NewLine + ImportSpeedSummary.Format("Prices", latest?.PricesFinished ?? 0,
                    latest?.PricesFinished ?? 0, latest?.CadFinished == requested.Length ? latest.Resolved : requested.Length, timer.Elapsed));
            string detail = "Starting component downloads…";
            var report = await new BatchComponentLookup().RunAsync(requested,
                (part, log, ct) => new LcscScraper(log).GetRawComponentAsync(part, ct, forceRefresh: refresh),
                (part, raw) => new Parser().Parse(part, raw),
                (component, ct) => new JlcPcbPricingService().EnrichAsync(component, ct),
                update =>
            {
                latest = update;
                if (update.Ready is { } item)
                {
                    var metadata = ReadMetadata(item.RawPayload);
                    Results.Add(new ComponentSearchRow(item.PartNumber, metadata.Name, metadata.Description, metadata.Package, item.RawPayload, item.Component)
                    {
                        IncludeFootprint = item.Component.HasFootprintData,
                        Include3D = item.Component.ThreeDModel is not null
                    });
                }
                if (!string.IsNullOrWhiteSpace(update.Detail)) detail = update.Detail;
                ProgressValue = update.CadFinished + update.PricesFinished + (update.CadFinished - update.Resolved);
                StatusText = $"CAD {update.CadFinished}/{update.Total} ({update.ActiveCad} active, {update.Cached} cached). " +
                    $"Prices {update.PricesFinished}/{update.Resolved} ({update.ActivePrices} active). " +
                    $"Elapsed {update.Elapsed:hh\\:mm\\:ss}. {detail}";
            }, cancellation.Token);
            // Preserve CSV ordering even though arrivals are deliberately concurrent.
            var ordering = requested.Select((part, index) => (part, index)).ToDictionary(item => item.part, item => item.index);
            var ordered = Results.OrderBy(row => ordering[row.PartNumber]).ToArray();
            Results.Clear();
            foreach (var row in ordered) Results.Add(row);
            SelectedResult = Results.FirstOrDefault();
            var symbolOnly = Results.Count(row => row.ParsedComponent is { HasFootprintData: false });
            var priced = Results.Count(row => row.ParsedComponent!.Properties.ContainsKey("JLCPCB Unit Price"));
            ImportDiagnostics.Record("lookup.result", new { requested = requested.Length, resolved = Results.Count, priced,
                report.Cancelled, failed = report.Failures.Count, priceFailures = report.PriceFailures.Count });
            StatusText = (report.Cancelled ? "Search stopped. " : "Search complete. ") +
                $"Resolved {Results.Count}/{requested.Length} part(s) in {timer.Elapsed:hh\\:mm\\:ss}. Prices available: {priced}/{Results.Count}." +
                (symbolOnly == 0 ? "" : $" {symbolOnly} symbol-only (no EasyEDA footprint available).") +
                (report.Failures.Count == 0 ? "" : $" {report.Failures.Count} failed: {string.Join("; ", report.Failures.Take(3))}") +
                (report.Cancelled ? " Completed CAD results are kept. Search again to reuse fresh downloads; export now uses available prices." : " Ready to export.");
            return !report.Cancelled && Results.Count > 0;
        }
        catch (Exception exception) { ImportDiagnostics.Failure("lookup.failed", exception); StatusText = "Search error: " + exception.Message; return false; }
        finally { StopSpeedIndicator(); _searchCancellation = null; ShowProgress = false; _busy = ""; RefreshCommands(); FinishDiagnosticSession(diagnostics); }
    }

    private async Task AddToLibraryAsync(LibraryExporter.OutputFormat format, string outputDirectory,
        bool automaticFamilyLibraries = false)
    {
        using var diagnostics = ImportDiagnostics.Begin(DiagnosticLogging, "export");
        ShowDiagnosticSession(diagnostics);
        ImportDiagnostics.Record("export.options", new { format = format.ToString(), outputDirectory, FamilyWorkerCount, automaticFamilyLibraries,
            lookupLog = _lastLookupLog, parts = Results.Count, footprints = Results.Count(r => r.IncludeFootprint), models = Results.Count(r => r.Include3D) });
        _busy = "export"; RefreshCommands();
        try
        {
            var referenceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Library");
            FamilyLibraryExportPlan.EnsureOutputIsSafe(outputDirectory, referenceRoot);
            Directory.CreateDirectory(outputDirectory);
            var chosen = Results.Where(row => row.ParsedComponent is not null).ToArray();
            var files = new List<string>();
            var failures = new List<string>();
            FamilyLibraryExportPlan? familyPlan = null;
            var organization = BatchLibraryOrganizationPicker.Resolve(chosen.Length, automaticFamilyLibraries, count =>
            {
                using var prompt = ImportDiagnostics.Measure("organization.prompt");
                return BatchLibraryOrganizationPicker.Pick(count);
            });
            ImportDiagnostics.Record("export.organization", new { choice = organization?.ToString() ?? "cancelled", automatic = automaticFamilyLibraries });
            if (organization is null)
            {
                StatusText = "Library export cancelled before any files were changed.";
                return;
            }
            if (organization == BatchLibraryOrganization.FamilyLibraries)
            {
                familyPlan = FamilyLibraryExportPlan.Create(outputDirectory, chosen.Select(row => row.ParsedComponent!), referenceRoot);
                StatusText = $"Family-library run: {familyPlan.RunDirectory}";
            }
            ShowProgress = true;
            ProgressMaximum = chosen.Length;
            ProgressValue = 0;
            if (familyPlan is not null && format == LibraryExporter.OutputFormat.Altium)
            {
                await ExportFamiliesAsync(chosen, outputDirectory, familyPlan);
                return;
            }
            var deferredChoices = new List<ComponentSearchRow>();
            var batch = format is LibraryExporter.OutputFormat.Altium or LibraryExporter.OutputFormat.Both
                ? new AltiumExportBatch() : null;
            await using var modelDownloads = new ModelDownloadBatch(chosen
                .Where(row => row.Include3D && row.IncludeFootprint && row.ParsedComponent!.HasFootprintData)
                .Select(row => row.ParsedComponent!).ToArray());
            var exportWatch = Stopwatch.StartNew();
            bool awaitingChoice = false;
            StartSpeedIndicator(() =>
                (awaitingChoice ? "Waiting for symbol selection — conversion timer paused." :
                    ImportSpeedSummary.Format("Conversion", files.Count, ProgressValue, chosen.Length, exportWatch.Elapsed)) +
                Environment.NewLine + $"3D downloads: {modelDownloads.Finished}/{modelDownloads.Total} processed · {modelDownloads.Active} active (maximum 3)");

            async Task ExportRowAsync(ComponentSearchRow row, SymbolLibraryMatch? selectedSymbol)
            {
                try
                {
                    StatusText = $"Generating {row.PartNumber} ({ProgressValue + 1}/{chosen.Length}); up to 3 model downloads in parallel…";
                    var component = row.ParsedComponent!;
                    var include3d = row.Include3D;
                    var includeFootprint = row.IncludeFootprint && component.HasFootprintData;
                    var symbolDirectory = UserSymbolDirectory;
                    var extraDirectories = familyPlan is null ? null : new[] { familyPlan.FamilyDirectory(component) };
                    // The single writer is off the dispatcher, but is always awaited before
                    // the next mutation. Model workers may keep fetching while it converts.
                    files.Add(await Task.Run(() => new LibraryExporter().ExportImportPlanAsync(component, outputDirectory,
                        include3d, symbolDirectory, selectedSymbol, format: format,
                        includeFootprint: includeFootprint, additionalOutputDirectories: extraDirectories,
                        batch: batch, modelDownloads: modelDownloads)));
                }
                catch (Exception exception)
                {
                    ImportDiagnostics.Failure("part.export_failed", exception, new { part = row.PartNumber });
                    failures.Add($"{row.PartNumber}: {exception.Message}");
                }
                finally { ProgressValue++; }
            }

            async Task CheckpointAsync()
            {
                if (batch is null) return;
                StatusText = $"Saving and verifying libraries ({ProgressValue}/{chosen.Length})…";
                // A checkpoint error is fatal, not a per-component failure to skip.
                await Task.Run(() => batch.CheckpointAsync());
            }

            // A modal replacement picker must not stall all uncomplicated parts. First write
            // every part whose bundled match or EasyEDA fallback is deterministic, then ask
            // for the few genuinely ambiguous selections.
            foreach (var row in chosen)
            {
                var component = row.ParsedComponent!;
                var resolver = new UserSymbolLibraryResolver();
                var selectedSymbol = resolver.Resolve(component, UserSymbolDirectory, null);
                if (selectedSymbol is null && !resolver.MustGenerateFromEasyEda(component))
                {
                    deferredChoices.Add(row);
                    continue;
                }
                await ExportRowAsync(row, selectedSymbol);
                if ((int)ProgressValue % AltiumExportBatch.CheckpointInterval == 0) await CheckpointAsync();
            }
            await CheckpointAsync();
            foreach (var row in deferredChoices)
            {
                StatusText = $"Automatic imports complete. Choose a replacement for {row.PartNumber} ({ProgressValue + 1}/{chosen.Length})…";
                SymbolLibraryMatch? selectedSymbol;
                awaitingChoice = true;
                exportWatch.Stop();
                try { using var prompt = ImportDiagnostics.Measure("symbol.prompt", new { part = row.PartNumber }); selectedSymbol = PromptForBundledSymbol(row.ParsedComponent!); }
                finally { awaitingChoice = false; exportWatch.Start(); }
                await ExportRowAsync(row, selectedSymbol);
                if ((int)ProgressValue % AltiumExportBatch.CheckpointInterval == 0) await CheckpointAsync();
            }
            await CheckpointAsync();
            if (familyPlan is not null)
            {
                await familyPlan.WriteAuditAsync(chosen.Select(row => row.ParsedComponent!));
                familyPlan.FinalizeFamilyFileNames(format);
            }
            var formatName = format == LibraryExporter.OutputFormat.KiCad ? "KiCad" : "Altium";
            StatusText = $"Updated {formatName} libraries with {files.Count}/{chosen.Length} component(s) in {outputDirectory} ({exportWatch.Elapsed:hh\\:mm\\:ss})." +
                (familyPlan is null ? "" : $" Family libraries: {familyPlan.RunDirectory}.") +
                (failures.Count == 0 ? "" : $" Failed: {string.Join("; ", failures.Take(3))}");
            ImportDiagnostics.Record("export.result", new { requested = chosen.Length, succeeded = files.Count, failed = failures.Count, elapsedSeconds = exportWatch.Elapsed.TotalSeconds });
        }
        catch (Exception exception) { ImportDiagnostics.Failure("export.failed", exception); StatusText = "Library generation error: " + exception.Message; }
        finally { StopSpeedIndicator(); ShowProgress = false; _busy = ""; RefreshCommands(); FinishDiagnosticSession(diagnostics); }
    }

    private async Task ExportFamiliesAsync(ComponentSearchRow[] rows, string outputDirectory, FamilyLibraryExportPlan plan)
    {
        var workers = FamilyWorkerCount;
        var watch = Stopwatch.StartNew();
        var stage = "Preparing family conversion";
        var workerSummary = new FamilyConversionProgress(0, 0, 0, "").WorkerSummary(workers);
        var succeeded = 0;
        bool awaitingChoice = false;
        await using var models = new ModelDownloadBatch(rows.Where(r => r.Include3D && r.IncludeFootprint && r.ParsedComponent!.HasFootprintData)
            .Select(r => r.ParsedComponent!).ToArray());
        var session = new FamilyConversionSession(plan, UserSymbolDirectory, models, workers);
        StartSpeedIndicator(() => (awaitingChoice ? "Waiting for symbol selection — conversion timer paused." :
            ImportSpeedSummary.Format(stage, succeeded, ProgressValue, rows.Length, watch.Elapsed)) +
            Environment.NewLine + workerSummary +
            Environment.NewLine + $"3D: {models.Finished}/{models.Total} parts processed · {models.Active}/{ModelDownloadBatch.Concurrency} downloads active · {models.UniqueModels} unique models");
        var automatic = new List<FamilyConversionItem>();
        var deferred = new List<(int Order, ComponentSearchRow Row)>();
        // Resolve choices once before worker mutation. Sources remain read-only; each worker
        // independently reloads the selected native symbol when constructing its family file.
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var resolver = new UserSymbolLibraryResolver();
            StatusText = $"Preparing symbol {index + 1}/{rows.Length}: {row.PartNumber}";
            using var resolving = ImportDiagnostics.Measure("symbol.resolve", new { part = row.PartNumber });
            var match = await Task.Run(() => resolver.Resolve(row.ParsedComponent!, UserSymbolDirectory, null));
            if (match is null && !resolver.MustGenerateFromEasyEda(row.ParsedComponent!)) deferred.Add((index, row));
            else automatic.Add(Item(index, row, match));
        }
        var progress = new Progress<FamilyConversionProgress>(update =>
        {
            ProgressValue = update.Processed;
            succeeded = update.Succeeded;
            workerSummary = update.WorkerSummary(workers);
            StatusText = update.Detail;
        });
        stage = "Family conversion";
        await session.RunAsync(automatic, progress);
        // All uncomplicated families are saved before any modal question is shown.
        foreach (var (index, row) in deferred)
        {
            SymbolLibraryMatch? match;
            StatusText = $"Automatic families saved. Choose a symbol for {row.PartNumber}.";
            awaitingChoice = true;
            watch.Stop();
            try { using var prompt = ImportDiagnostics.Measure("symbol.prompt", new { part = row.PartNumber }); match = PromptForBundledSymbol(row.ParsedComponent!); }
            finally { awaitingChoice = false; watch.Start(); }
            await session.RunAsync([Item(rows.Length + index, row, match)], progress);
        }
        var successful = session.Succeeded;
        // Writes on the UI dispatcher above are queued; establish final counters explicitly.
        ProgressValue = rows.Length;
        succeeded = successful.Count;
        workerSummary = new FamilyConversionProgress(rows.Length, succeeded, 0, "").WorkerSummary(workers);
        stage = "Merging and verifying combined libraries";
        StatusText = stage;
        var mergeProgress = new Progress<string>(message => StatusText = message);
        await Task.Run(() => new AltiumFamilyMerger().MergeAsync(outputDirectory, plan, successful, mergeProgress));
        await plan.WriteAuditAsync(successful.Select(i => i.Component));
        await File.WriteAllTextAsync(Path.Combine(plan.RunDirectory, "conversion-results.json"), JsonSerializer.Serialize(new
        {
            version = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            workers, requested = rows.Length, succeeded = successful.Count,
            failed = session.Failures, elapsedSeconds = watch.Elapsed.TotalSeconds,
            combinedMerge = successful.Count == 0 ? "skipped: no successful components" : "verified",
            note = "Unrelated existing combined components retained; no CPU affinity forced."
        }, new JsonSerializerOptions { WriteIndented = true }));
        plan.FinalizeFamilyFileNames(LibraryExporter.OutputFormat.Altium);
        ImportDiagnostics.Record("export.result", new { requested = rows.Length, succeeded = successful.Count,
            failed = session.Failures.Count, workers, elapsedSeconds = watch.Elapsed.TotalSeconds, plan.RunDirectory });
        StatusText = $"Altium family conversion complete: {successful.Count}/{rows.Length} parts, {workers} worker(s), {watch.Elapsed:hh\\:mm\\:ss}. " +
            $"Combined library: {outputDirectory}. Families: {plan.RunDirectory}." +
            (session.Failures.Count == 0 ? "" : $" Failed: {string.Join("; ", session.Failures.Take(3))}");

        static FamilyConversionItem Item(int order, ComponentSearchRow row, SymbolLibraryMatch? match) =>
            new(order, row.ParsedComponent!, row.IncludeFootprint && row.ParsedComponent!.HasFootprintData, row.Include3D, match);
    }

    private async Task SaveModelAsync(string outputDirectory)
    {
        var selected = SelectedResult!;
        try
        {
            var folder = Path.Combine(outputDirectory, selected.PartNumber);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{selected.PartNumber}_easyeda.json");
            await File.WriteAllTextAsync(path, selected.RawPayload!);
            StatusText = $"Saved EasyEDA source data: {Path.GetFileName(path)}";
        }
        catch (Exception exception) { StatusText = "Save error: " + exception.Message; }
    }

    private void OpenOutputDirectory(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            Process.Start(new ProcessStartInfo { FileName = outputDirectory, UseShellExecute = true });
        }
        catch (Exception exception) { StatusText = "Folder error: " + exception.Message; }
    }

    private void OpenLibrariesInAltium(string outputDirectory)
    {
        try
        {
            var pcbLib = Path.Combine(outputDirectory, "youeda.PcbLib");
            var schLib = Path.Combine(outputDirectory, "youeda.SchLib");
            if (!File.Exists(pcbLib) || !File.Exists(schLib))
            {
                StatusText = "Generate at least one component first; youeda.PcbLib and youeda.SchLib were not found.";
                return;
            }

            // Use the Windows file association so this works with any installed Altium Designer
            // version, rather than hard-coding a version-specific executable path.
            Process.Start(new ProcessStartInfo { FileName = pcbLib, UseShellExecute = true });
            Process.Start(new ProcessStartInfo { FileName = schLib, UseShellExecute = true });
            StatusText = "Opened youeda.PcbLib and youeda.SchLib in Altium Designer.";
        }
        catch (Exception exception)
        {
            StatusText = "Unable to open Altium libraries: " + exception.Message;
        }
    }

    private void ImportCsv()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import LCSC references",
            Filter = "CSV or text files (*.csv;*.txt)|*.csv;*.txt|All files (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            BatchPartNumbers = File.ReadAllText(dialog.FileName);
            StatusText = $"Loaded {RequestedPartNumbers().Count()} LCSC reference(s) from {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception) { StatusText = "CSV import error: " + exception.Message; }
    }

    private SymbolLibraryMatch? PromptForBundledSymbol(EdaComponent component)
    {
        StatusText = $"{component.LcscPartNumber}: choose a bundled symbol or use the EasyEDA fallback.";
        var resolver = new UserSymbolLibraryResolver();
        var symbols = resolver.ListMasterSymbols(UserSymbolDirectory);
        return BundledSymbolPicker.Pick(component, symbols, resolver);
    }

    private IEnumerable<string> RequestedPartNumbers()
    {
        var combined = string.IsNullOrWhiteSpace(BatchPartNumbers) ? PartNumber : BatchPartNumbers;
        return combined.Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(value => System.Text.RegularExpressions.Regex.IsMatch(value, "^C\\d+$"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static (string Name, string Description, string Package, bool Has3D) ReadMetadata(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var result = document.RootElement.GetProperty("result");
        var name = result.TryGetProperty("title", out var title) ? title.GetString() ?? "Unnamed" : "Unnamed";
        var description = result.TryGetProperty("description", out var text) ? text.GetString() ?? "No description supplied." : "No description supplied.";
        var package = "";
        var has3D = false;
        if (result.TryGetProperty("packageDetail", out var packageDetail) && packageDetail.ValueKind == JsonValueKind.Object)
        {
            package = packageDetail.TryGetProperty("title", out var packageTitle) ? packageTitle.GetString() ?? "" : "";
            if (packageDetail.TryGetProperty("dataStr", out var dataStr) && dataStr.TryGetProperty("head", out var head) &&
                head.TryGetProperty("uuid_3d", out var id3D)) has3D = !string.IsNullOrWhiteSpace(id3D.GetString());
        }
        return (name, description, package, has3D);
    }

    private void RefreshCommands()
    {
        On(nameof(IsBusy)); On(nameof(CanSearch)); On(nameof(CanExport)); On(nameof(CanChangeDiagnostics));
        ((AsyncCommand)SearchCommand).RaiseCanExecuteChanged();
        ((ActionCommand)CancelSearchCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)AddToLibraryCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)SaveModelCommand).RaiseCanExecuteChanged();
        ((ActionCommand)OpenOutputCommand).RaiseCanExecuteChanged();
        ((ActionCommand)OpenInAltiumCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)AddToAltiumCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)AddToKiCadCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)SaveAltiumSourceCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)SaveKiCadSourceCommand).RaiseCanExecuteChanged();
        ((ActionCommand)OpenAltiumOutputCommand).RaiseCanExecuteChanged();
        ((ActionCommand)OpenKiCadOutputCommand).RaiseCanExecuteChanged();
        ((ActionCommand)OpenAltiumLibrariesCommand).RaiseCanExecuteChanged();
        ((ActionCommand)ImportCsvCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)RunFullImportCommand).RaiseCanExecuteChanged();
    }

    private static bool LibrariesExist(string outputDirectory) =>
        File.Exists(Path.Combine(outputDirectory, "youeda.PcbLib")) &&
        File.Exists(Path.Combine(outputDirectory, "youeda.SchLib"));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    private sealed class ActionCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public void Execute(object? parameter) => execute();
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) => await execute();
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class ComponentSearchRow : INotifyPropertyChanged
{
    private bool _includeFootprint;
    private bool _include3D;

    public ComponentSearchRow(string partNumber, string name, string description, string package, string rawPayload, EdaComponent parsedComponent)
        => (PartNumber, Name, Description, Package, RawPayload, ParsedComponent) = (partNumber, name, description, package, rawPayload, parsedComponent);

    public string PartNumber { get; }
    public string Name { get; }
    public string Description { get; }
    public string Package { get; }
    public string? RawPayload { get; }
    public EdaComponent? ParsedComponent { get; }
    public bool IncludeFootprint { get => _includeFootprint; set { _includeFootprint = value; On(); } }
    public bool Include3D { get => _include3D; set { _include3D = value; On(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
