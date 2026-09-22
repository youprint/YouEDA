using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using EasyEdaAltiumGrabber.Controls;
using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;

namespace EasyEdaAltiumGrabber.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _partNumber = "C32346";
    private string _batchPartNumbers = "";
    private string _outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EasyEdaAltium");
    private readonly string _userSymbolDirectory = Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib");
    private string _statusText = "Enter an LCSC code, then search the public EasyEDA component library.";
    private string _busy = "";
    private ComponentSearchRow? _selectedResult;

    public ObservableCollection<ComponentSearchRow> Results { get; } = [];
    public string PartNumber { get => _partNumber; set { _partNumber = value; On(); RefreshCommands(); } }
    public string BatchPartNumbers { get => _batchPartNumbers; set { _batchPartNumbers = value; On(); RefreshCommands(); } }
    public string OutputDirectory { get => _outputDirectory; set { _outputDirectory = value; On(); RefreshCommands(); } }
    public string UserSymbolDirectory => _userSymbolDirectory;
    public string StatusText { get => _statusText; private set { _statusText = value; On(); } }
    public bool IsBusy => !string.IsNullOrEmpty(_busy);
    public bool CanSearch => !IsBusy && RequestedPartNumbers().Any();
    public bool CanExport => !IsBusy && Results.Any(row => row.ParsedComponent is not null && row.IncludeFootprint);

    public ComponentSearchRow? SelectedResult
    {
        get => _selectedResult;
        set { _selectedResult = value; On(); On(nameof(PreviewComponent)); RefreshCommands(); }
    }

    public EdaComponent? PreviewComponent => SelectedResult?.ParsedComponent;
    public ICommand SearchCommand { get; }
    public ICommand AddToLibraryCommand { get; }
    public ICommand SaveModelCommand { get; }
    public ICommand OpenOutputCommand { get; }
    public ICommand OpenInAltiumCommand { get; }
    public ICommand ImportCsvCommand { get; }

    public MainViewModel()
    {
        SearchCommand = new AsyncCommand(SearchAsync, () => CanSearch);
        AddToLibraryCommand = new AsyncCommand(AddToLibraryAsync, () => CanExport);
        SaveModelCommand = new AsyncCommand(SaveModelAsync, () => SelectedResult?.RawPayload is not null && !IsBusy);
        OpenOutputCommand = new ActionCommand(OpenOutputDirectory, () => !string.IsNullOrWhiteSpace(OutputDirectory));
        OpenInAltiumCommand = new ActionCommand(OpenLibrariesInAltium, () => !IsBusy && LibrariesExist());
        ImportCsvCommand = new ActionCommand(ImportCsv, () => !IsBusy);
    }

    private async Task SearchAsync()
    {
        _busy = "search"; RefreshCommands();
        try
        {
            Results.Clear();
            var requested = RequestedPartNumbers().ToArray();
            var failures = new List<string>();
            for (var index = 0; index < requested.Length; index++)
            {
                var partNumber = requested[index];
                try
                {
                    StatusText = $"Fetching {partNumber} ({index + 1}/{requested.Length})…";
                    var raw = await new LcscScraper(message => StatusText = $"{partNumber}: {message}").GetRawComponentAsync(partNumber);
                    var parsed = new Parser().Parse(partNumber, raw);
                    var metadata = ReadMetadata(raw);
                    Results.Add(new ComponentSearchRow(partNumber, metadata.Name, metadata.Description, metadata.Package, raw, parsed)
                    {
                        IncludeFootprint = true,
                        Include3D = parsed.ThreeDModel is not null
                    });
                }
                catch (Exception exception) { failures.Add($"{partNumber}: {exception.Message}"); }
            }
            SelectedResult = Results.FirstOrDefault();
            StatusText = $"Resolved {Results.Count}/{requested.Length} part(s)." +
                (failures.Count == 0 ? " Select options, then add the checked footprints to the output library." : $" {failures.Count} failed: {string.Join("; ", failures)}");
        }
        catch (Exception exception) { StatusText = "Search error: " + exception.Message; }
        finally { _busy = ""; RefreshCommands(); }
    }

    private async Task AddToLibraryAsync()
    {
        _busy = "export"; RefreshCommands();
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            var chosen = Results.Where(row => row.IncludeFootprint && row.ParsedComponent is not null).ToArray();
            var files = new List<string>();
            for (var index = 0; index < chosen.Length; index++)
            {
                StatusText = $"Generating {chosen[index].PartNumber} ({index + 1}/{chosen.Length})…";
                var component = chosen[index].ParsedComponent!;
                var resolver = new UserSymbolLibraryResolver();
                var matchingSymbol = resolver.Resolve(component, UserSymbolDirectory, null);
                // IC/MCU families always use authoritative EasyEDA P pin records. Other
                // component families stay in the user's symbol library and prompt on ambiguity.
                var selectedSymbol = matchingSymbol;
                if (selectedSymbol is null && !resolver.MustGenerateFromEasyEda(component))
                    selectedSymbol = PromptForBundledSymbol(component);
                else if (selectedSymbol is null)
                    StatusText = $"{component.LcscPartNumber}: generating its schematic symbol from EasyEDA pin data.";
                files.Add(await new LibraryExporter().ExportImportPlanAsync(component, OutputDirectory, chosen[index].Include3D, UserSymbolDirectory, selectedSymbol));
            }
            StatusText = $"Updated youeda.PcbLib and youeda.SchLib with {files.Count} component(s).";
        }
        catch (Exception exception) { StatusText = "Library generation error: " + exception.Message; }
        finally { _busy = ""; RefreshCommands(); }
    }

    private async Task SaveModelAsync()
    {
        var selected = SelectedResult!;
        try
        {
            var folder = Path.Combine(OutputDirectory, selected.PartNumber);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{selected.PartNumber}_easyeda.json");
            await File.WriteAllTextAsync(path, selected.RawPayload!);
            StatusText = $"Saved EasyEDA source data: {Path.GetFileName(path)}";
        }
        catch (Exception exception) { StatusText = "Save error: " + exception.Message; }
    }

    private void OpenOutputDirectory()
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            Process.Start(new ProcessStartInfo { FileName = OutputDirectory, UseShellExecute = true });
        }
        catch (Exception exception) { StatusText = "Folder error: " + exception.Message; }
    }

    private void OpenLibrariesInAltium()
    {
        try
        {
            var pcbLib = Path.Combine(OutputDirectory, "youeda.PcbLib");
            var schLib = Path.Combine(OutputDirectory, "youeda.SchLib");
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
        var symbols = new UserSymbolLibraryResolver().ListMasterSymbols(UserSymbolDirectory);
        return BundledSymbolPicker.Pick(component.LcscPartNumber, symbols);
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
        On(nameof(IsBusy)); On(nameof(CanSearch)); On(nameof(CanExport));
        ((AsyncCommand)SearchCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)AddToLibraryCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)SaveModelCommand).RaiseCanExecuteChanged();
        ((ActionCommand)OpenOutputCommand).RaiseCanExecuteChanged();
        ((ActionCommand)OpenInAltiumCommand).RaiseCanExecuteChanged();
        ((ActionCommand)ImportCsvCommand).RaiseCanExecuteChanged();
    }

    private bool LibrariesExist() =>
        File.Exists(Path.Combine(OutputDirectory, "youeda.PcbLib")) &&
        File.Exists(Path.Combine(OutputDirectory, "youeda.SchLib"));

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
