using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using EasyEdaAltiumGrabber;
using EasyEdaAltiumGrabber.Controls;
using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;
using EasyEdaAltiumGrabber.ViewModels;

namespace UiLayoutSmoke;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Load the application's real theme and BAML, then lay out the real MainWindow
        // at a representative 1920x1080 maximized-client area without showing it.
        var app = new App();
        app.InitializeComponent();

        var window = new MainWindow
        {
            WindowState = WindowState.Normal,
            Width = 1920,
            Height = 1040
        };
        try
        {
            // Keep UI tests and documentation captures out of the user's saved preferences.
            var captureModel = (MainViewModel)window.DataContext;
            typeof(MainViewModel).GetField("_settingsStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(captureModel, new AppSettingsStore(Path.Combine(AppContext.BaseDirectory, "ui-test-settings.json")));
            captureModel.AltiumOutputDirectory = @"C:\YouEDA\Libraries\Altium";
            captureModel.KiCadOutputDirectory = @"C:\YouEDA\Libraries\KiCad";
            // WPF only materializes template visuals after the window is shown.
            window.Show();
            // The desktop app correctly restores the user's chosen skin; visual assertions must
            // establish their own deterministic theme instead of assuming saved preferences.
            ThemeService.Apply(ThemeService.MidnightBlueprint);
            window.UpdateLayout();
            var runningVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
            Check(window.Title.StartsWith($"YouEDA {runningVersion} —", StringComparison.Ordinal),
                "window title uses the running executable version rather than a hard-coded release number");

            var tabControl = FindDescendants<TabControl>(window).SingleOrDefault(control => control.Items.Count == 2)
                ?? throw new InvalidOperationException("FAILED: exporter TabControl was not created");
            Check(tabControl.ActualHeight >= 164, "export tabs reserve enough vertical space for their controls");

            VerifyExportTab(window, tabControl, 0,
                ["Open folder", "Open in Altium", "Save source", "Add selected to Altium library"]);
            VerifyRenderedSelectedTab(window, tabControl, CaptureWindow(window, "ui-layout-altium-tab.png"));
            VerifyExportTab(window, tabControl, 1,
                ["Open folder", "Save source", "Add selected to KiCad library"]);
            VerifyRenderedSelectedTab(window, tabControl, CaptureWindow(window, "ui-layout-kicad-tab.png"));

            VerifyTheme(window, ThemeService.MidnightBlueprint, Color.FromRgb(15, 27, 43), "midnight-blueprint");
            VerifyTheme(window, ThemeService.GraphiteMint, Color.FromRgb(31, 36, 40), "graphite-mint");
            VerifyTheme(window, ThemeService.WarmStudio, Color.FromRgb(248, 247, 244), "warm-studio");
            ThemeService.Apply(ThemeService.MidnightBlueprint);
            VerifyInteractiveControlContrast(window, tabControl);
            VerifySettingsWindow(window);
            VerifyBatchProgressIndicator(window);
            CaptureVisualMatrix(window, tabControl);
            VerifyImporterPreviews(window);
            VerifyTooltips(window);
            VerifySymbolCompatibilityPicker();
        }
        finally
        {
            window.Close();
        }

        Console.WriteLine("PASS: MainWindow starts maximized and both exporter tabs lay out every action button within bounds.");
        return 0;
    }

    private static void VerifySymbolCompatibilityPicker()
    {
        var resolver = new UserSymbolLibraryResolver();
        var catalog = Path.Combine(AppContext.BaseDirectory, "Symbols", "BundledUserSymbols.SchLib");
        var searchPath = (string)typeof(MainViewModel).GetMethod("BuildSymbolSearchPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
        var choices = resolver.ListMasterSymbols(searchPath);
        Check(choices.Count == 71, "real view-model combined search path populates all 71 picker choices");
        var resistor = choices.Single(c => c.ComponentName == "Resistor");
        var wrong = choices.First(c => resolver.LoadPreviewComponent(c).Pins.Count == 3);
        var part = new EdaComponent { LcscPartNumber = "C_UI_TEST", Name = "Picker test" };
        part.SymbolPins.Add(new("1", "1", 0, 0, "")); part.SymbolPins.Add(new("2", "2", 0, 180, ""));
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var deadline = DateTime.UtcNow.AddSeconds(15);
        int stage = 0;
        Exception? failure = null;
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title.Contains("C_UI_TEST"));
            try
            {
                if (DateTime.UtcNow > deadline) throw new Exception("Symbol picker compatibility test timed out.");
                if (dialog is null) return;
                var button = FindDescendants<Button>(dialog).Single(b => b.Content?.ToString() == "Use selected symbol");
                var combo = FindDescendants<ComboBox>(dialog).Single();
                var text = string.Join('\n', FindDescendants<TextBlock>(dialog).Select(t => t.Text));
                if (stage == 0 && text.Contains("different numbered terminals"))
                {
                    Check(!button.IsEnabled, "symbol picker blocks incompatible numbered terminals and explains why");
                    combo.SelectedItem = resistor; stage = 1;
                    Check(!button.IsEnabled, "symbol choice stays disabled while the new selection is being checked");
                }
                else if (stage == 1 && text.Contains("Numbered terminals match") && button.IsEnabled)
                {
                    Check(true, "symbol picker permits the compatible native symbol after checking");
                    stage = 2; timer.Stop(); dialog.DialogResult = false;
                }
            }
            catch (Exception exception) { failure = exception; timer.Stop(); dialog?.Close(); }
        };
        timer.Start();
        try { BundledSymbolPicker.Pick(part, [wrong, resistor], resolver); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
        Check(stage == 2, "symbol picker guard completed both invalid and valid selection paths");
    }

    private static void VerifyExportTab(Window window, TabControl tabControl, int selectedIndex, string[] buttonLabels)
    {
        tabControl.SelectedIndex = selectedIndex;
        window.UpdateLayout();

        var selectedTab = (TabItem)tabControl.ItemContainerGenerator.ContainerFromIndex(selectedIndex);
        var selectedHeader = selectedTab.Header?.ToString() ?? string.Empty;
        var headerText = FindDescendants<TextBlock>(selectedTab)
            .SingleOrDefault(candidate => string.Equals(candidate.Text, selectedHeader, StringComparison.Ordinal));
        Check(headerText is not null && headerText.Foreground is SolidColorBrush { Color: { R: 255, G: 255, B: 255 } },
            $"{selectedHeader} selected-tab label has high-contrast white text");
        Check(selectedTab.Background is SolidColorBrush { Color: { R: 37, G: 99, B: 235 } },
            $"{selectedHeader} selected-tab background uses the visible accent colour");

        foreach (var label in buttonLabels)
        {
            var button = FindDescendants<Button>(tabControl)
                .SingleOrDefault(candidate => candidate.IsVisible && string.Equals(candidate.Content?.ToString(), label, StringComparison.Ordinal));
            Check(button is not null, $"{label} button is visible in export tab {selectedIndex}");
            Check(button!.ActualWidth > 0 && button.ActualHeight > 0, $"{label} button has a non-zero layout size");
            Check(FindDescendants<ContentPresenter>(button).Any(presenter =>
                    string.Equals(presenter.Content?.ToString(), label, StringComparison.Ordinal)),
                $"{label} button forwards its caption into the rendered template");

            var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Check(bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight,
                $"{label} button is fully inside the maximized window");
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject =>
        FindDescendants<T>(root).FirstOrDefault();

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }

    private static RenderTargetBitmap CaptureWindow(FrameworkElement window, string fileName)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, fileName));
        encoder.Save(stream);
        return image;
    }

    private static void VerifyRenderedSelectedTab(Window window, TabControl tabControl, RenderTargetBitmap image)
    {
        var selectedTab = (TabItem)tabControl.ItemContainerGenerator.ContainerFromIndex(tabControl.SelectedIndex);
        var bounds = selectedTab.TransformToAncestor(window)
            .TransformBounds(new Rect(0, 0, selectedTab.ActualWidth, selectedTab.ActualHeight));
        // Sample just inside the left edge, away from the header text. This verifies the final
        // raster output—not merely the WPF Background property—uses the selected-tab accent.
        var x = Math.Clamp((int)Math.Floor(bounds.Left + 5), 0, image.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Floor(bounds.Top + selectedTab.ActualHeight / 2), 0, image.PixelHeight - 1);
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        Check(pixel[2] == 37 && pixel[1] == 99 && pixel[0] == 235,
            $"rendered selected tab uses the visible cobalt accent (got RGB {pixel[2]},{pixel[1]},{pixel[0]})");
    }

    private static void VerifyTheme(Window window, string theme, Color expectedBackground, string fileSlug)
    {
        ThemeService.Apply(theme);
        window.UpdateLayout();
        Check(window.Background is SolidColorBrush { Color: var actual } && actual == expectedBackground,
            $"{theme} applies its window background resource");
        CaptureWindow(window, $"theme-{fileSlug}.png");
    }

    private static void VerifyTooltips(Window window)
    {
        var summary = FindDescendants<TextBlock>(window).Single(t => t.ToolTip?.ToString() == "Change these options with Settings…");
        var fullImport = FindDescendants<Button>(window).Single(b => b.Name == "RunFullImportButton");
        var tooltip = new ToolTip { PlacementTarget = summary, Content = summary.ToolTip };
        try
        {
            tooltip.IsOpen = true;
            foreach (var theme in ThemeService.Names)
            {
                ThemeService.Apply(theme);
                foreach (var (content, slug) in new[] { (summary.ToolTip, "summary"), (fullImport.ToolTip, "full-import") })
                {
                    tooltip.Content = content;
                    tooltip.UpdateLayout();
                    var expected = ((SolidColorBrush)Application.Current.FindResource("Panel")).Color;
                    var image = CaptureWindow(tooltip, $"tooltip-{ThemeService.Names.ToList().IndexOf(theme)}-{slug}.png");
                    var sample = new byte[4];
                    image.CopyPixels(new Int32Rect(4, 4, 1, 1), sample, 4, 0);
                    Check(sample[2] == expected.R && sample[1] == expected.G && sample[0] == expected.B,
                        $"{theme} {slug} tooltip renders the theme surface rather than Windows' pale default");
                    var text = FindDescendants<TextBlock>(tooltip).First(t => !string.IsNullOrWhiteSpace(t.Text));
                    var foreground = ((SolidColorBrush)text.Foreground).Color;
                    static double Luminance(Color c)
                    {
                        static double Linear(byte b) { var s = b / 255.0; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
                        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
                    }
                    var a = Luminance(expected); var b = Luminance(foreground);
                    Check((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5,
                        $"{theme} {slug} tooltip text is readable with at least 4.5:1 contrast");
                    Check(tooltip.ActualWidth <= 420 && text.Text == content.ToString(),
                        "tooltip retains its complete help text within a bounded width");
                    if (slug == "full-import") Check(text.TextWrapping == TextWrapping.Wrap && text.ActualHeight > 30,
                        "long full-import tooltip wraps instead of overflowing horizontally");
                }
            }
        }
        finally { tooltip.IsOpen = false; ThemeService.Apply(ThemeService.MidnightBlueprint); }
    }

    private static void VerifyInteractiveControlContrast(Window window, TabControl tabControl)
    {
        tabControl.SelectedIndex = 0;
        window.UpdateLayout();
        foreach (var label in new[] { "Save source", "Add selected to Altium library" })
        {
            var button = FindDescendants<Button>(window).Single(candidate => candidate.Content?.ToString() == label);
            Check(!button.IsEnabled && Math.Abs(button.Opacity - 1) < .001,
                $"disabled {label} stays visible rather than fading away");
            Check(button.Foreground is SolidColorBrush { Color: { R: 168, G: 180, B: 197 } },
                $"disabled {label} uses a readable muted label color");
        }
    }

    private static void VerifySettingsWindow(Window window)
    {
        var model = (MainViewModel)window.DataContext;
        Check(FindDescendants<Button>(window).Single(b => b.Name == "SettingsButton").IsVisible,
            "Settings button is visible on the main window");
        Check(!FindDescendants<CheckBox>(window).Any(c => c.Name == "DiagnosticLoggingOption"),
            "global diagnostic checkbox moved out of batch controls");
        var settings = new SettingsWindow(model) { Owner = window };
        var priorLogging = model.DiagnosticLogging;
        var priorRefresh = model.RefreshCadFromServer;
        var priorWorkers = model.FamilyWorkerCount;
        var busyField = typeof(MainViewModel).GetField("_busy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var refreshCommands = typeof(MainViewModel).GetMethod("RefreshCommands", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var priorBusy = busyField.GetValue(model);
        try
        {
        settings.Show(); settings.UpdateLayout();
        Check(!FindDescendants<ScrollViewer>(settings).Any(s => s.ScrollableHeight > 0),
            "default settings window shows all options without scrolling");
        var appearancePicker = FindDescendants<ComboBox>(settings).Single(c => c.Name == "AppearanceSelector");
        Check(appearancePicker.Background is SolidColorBrush { Color: { R: 16, G: 36, B: 62 } } &&
            appearancePicker.Foreground is SolidColorBrush { Color: { R: 229, G: 231, B: 235 } },
            "settings appearance picker retains themed readable colours");
        var workers = FindDescendants<ComboBox>(settings).Single(c => c.Name == "FamilyWorkerSelector");
        Check(workers.Items.Count == 2 && model.FamilyWorkerCount == 3, "family export defaults to three workers and offers a one-worker fallback");
        workers.SelectedItem = 1;
        Check(model.FamilyWorkerCount == 1, "family worker selector updates the export setting");
        workers.SelectedItem = 3;
        var logging = FindDescendants<CheckBox>(settings).Single(c => c.Name == "DiagnosticLoggingOption");
        logging.IsChecked = !priorLogging;
        Check(model.DiagnosticLogging == !priorLogging, "diagnostic logging checkbox updates the saved preference");
        logging.IsChecked = priorLogging;
        var refresh = FindDescendants<CheckBox>(settings).Single(c => c.Name == "RefreshCadOption");
        refresh.IsChecked = true;
        Check(model.RefreshCadFromServer && model.ImportSettingsSummary.Contains("cache bypassed"),
            "settings refresh updates model and main-window cache-bypass warning");
        refresh.IsChecked = false;
        Check(!model.RefreshCadFromServer && model.ImportSettingsSummary.Contains("Reuse fresh CAD cache"),
            "settings cache reuse updates main-window summary");
        CaptureWindow(settings, "settings-window.png");
        busyField.SetValue(model, "export"); refreshCommands.Invoke(model, null); settings.UpdateLayout();
        Check(!refresh.IsEnabled && !workers.IsEnabled && !logging.IsEnabled && appearancePicker.IsEnabled,
            "active import locks refresh, worker count and diagnostics but allows appearance changes");
        settings.Height = 430; settings.Width = 490; settings.UpdateLayout();
        var done = FindDescendants<Button>(settings).Single(b => b.Content?.ToString() == "Done");
        var bounds = done.TransformToAncestor(settings).TransformBounds(new Rect(0, 0, done.ActualWidth, done.ActualHeight));
        Check(bounds.Bottom <= settings.ActualHeight && FindDescendants<ScrollViewer>(settings).Any(s => s.ScrollableHeight > 0),
            "compact settings keeps Done visible and scrolls options");
        CaptureWindow(settings, "settings-compact.png");
        }
        finally
        {
            busyField.SetValue(model, priorBusy); refreshCommands.Invoke(model, null);
            model.DiagnosticLogging = priorLogging; model.RefreshCadFromServer = priorRefresh;
            model.FamilyWorkerCount = priorWorkers;
            settings.Close();
        }
        bool openedByButton = false;
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            var opened = Application.Current.Windows.OfType<SettingsWindow>().SingleOrDefault();
            openedByButton = opened?.DataContext == model && opened.Owner == window;
            opened?.Close();
        }));
        FindDescendants<Button>(window).Single(b => b.Name == "SettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(openedByButton, "Settings button opens an owned dialog sharing the current import settings");
    }

    private static void VerifyBatchProgressIndicator(Window window)
    {
        var model = (MainViewModel)window.DataContext;
        var fullImport = FindDescendants<Button>(window).Single(b => b.Name == "RunFullImportButton");
        var priorBatch = model.BatchPartNumbers;
        model.BatchPartNumbers = "";
        Check(!fullImport.Command.CanExecute(null), "full import is disabled before a batch list is loaded");
        model.BatchPartNumbers = "C11702\nC1002";
        Check(fullImport.Command.CanExecute(null), "full import becomes available for a loaded CSV list");
        model.BatchPartNumbers = priorBatch;
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.ProgressMaximum))!.GetSetMethod(nonPublic: true)!.Invoke(model, [12]);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.ProgressValue))!.GetSetMethod(nonPublic: true)!.Invoke(model, [5]);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.ShowProgress))!.GetSetMethod(nonPublic: true)!.Invoke(model, [true]);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.SpeedText))!.GetSetMethod(nonPublic: true)!.Invoke(model,
            [ImportSpeedSummary.Format("Family conversion", 48, 50, 351, TimeSpan.FromMinutes(2)) + Environment.NewLine +
             new FamilyConversionProgress(50, 48, 3, "", 1, 2, 0).WorkerSummary(3) + Environment.NewLine +
             "3D: 69/351 parts processed · 3/3 downloads active · 52 unique models"]);
        window.UpdateLayout();
        var progress = FindDescendant<ProgressBar>(window);
        Check(progress is { IsVisible: true } && progress.Value == 5 && progress.Maximum == 12,
            "batch progress indicator is visible and reports completed/total components");
        var speed = FindDescendants<TextBlock>(window).Single(t => t.Name == "ImportSpeedIndicator");
        Check(speed.IsVisible && speed.Text.Contains("24.0 parts/min") && speed.Text.Contains("ETA ~") && speed.ActualHeight > 0,
            "live speed readout binds throughput, elapsed time, and ETA");
        // Verify a timer tick updates the text without a new network/progress event.
        var snapshotCalls = 0;
        typeof(MainViewModel).GetMethod("StartSpeedIndicator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(model, new object[] { (Func<string>)(() => "Timer snapshot " + ++snapshotCalls) });
        var frame = new System.Windows.Threading.DispatcherFrame();
        var exitTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1250) };
        exitTimer.Tick += (_, _) => { exitTimer.Stop(); frame.Continue = false; };
        exitTimer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Check(snapshotCalls >= 2, "speed display refreshes each second even without progress events");
        typeof(MainViewModel).GetMethod("StopSpeedIndicator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(model, null);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.SpeedText))!.GetSetMethod(nonPublic: true)!.Invoke(model,
            [ImportSpeedSummary.Format("Family conversion", 48, 50, 351, TimeSpan.FromMinutes(2)) + Environment.NewLine +
             new FamilyConversionProgress(50, 48, 3, "", 1, 2, 0).WorkerSummary(3) + Environment.NewLine +
             "3D: 69/351 parts processed · 3/3 downloads active · 52 unique models"]);
        CaptureWindow(window, "batch-progress-visible.png");
        window.Width = 1280;
        window.Height = 720;
        window.UpdateLayout();
        var bounds = speed.TransformToAncestor(window).TransformBounds(new Rect(0, 0, speed.ActualWidth, speed.ActualHeight));
        Check(bounds.Bottom < window.ActualHeight && bounds.Top >= 0,
            "speed indicator remains in bounds in a compact window");
        CaptureWindow(window, "batch-speed-compact.png");
        window.Width = 1920;
        window.Height = 1040;
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.ShowProgress))!.GetSetMethod(nonPublic: true)!.Invoke(model, [false]);
    }

    private static void CaptureVisualMatrix(Window window, TabControl tabControl)
    {
        var viewports = new[] { (Width: 1280d, Height: 720d, Name: "compact"), (Width: 1920d, Height: 1040d, Name: "wide") };
        var themes = new[]
        {
            (ThemeService.MidnightBlueprint, "midnight"),
            (ThemeService.GraphiteMint, "graphite"),
            (ThemeService.WarmStudio, "warm")
        };
        foreach (var viewport in viewports)
        {
            window.Width = viewport.Width;
            window.Height = viewport.Height;
            window.UpdateLayout();
            foreach (var theme in themes)
            {
                ThemeService.Apply(theme.Item1);
                foreach (var (tabIndex, tabName) in new[] { (0, "altium"), (1, "kicad") })
                {
                    tabControl.SelectedIndex = tabIndex;
                    window.UpdateLayout();
                    CaptureWindow(window, $"qa-{theme.Item2}-{viewport.Name}-{tabName}.png");
                }
            }
        }
        ThemeService.Apply(ThemeService.MidnightBlueprint);
        window.Width = 1920;
        window.Height = 1040;
        window.UpdateLayout();
    }

    private static void VerifyImporterPreviews(Window window)
    {
        var model = (MainViewModel)window.DataContext;
        Check(FindDescendants<TabControl>(window).Count() == 1,
            "only the Altium/KiCad exporter tabs remain; viewer workspaces are removed");
        Check(typeof(MainWindow).Assembly.GetType("EasyEdaAltiumGrabber.Services.AltiumLibraryViewerLoader") is null &&
              typeof(MainWindow).Assembly.GetType("EasyEdaAltiumGrabber.Services.KiCadLibraryViewerLoader") is null &&
              typeof(MainWindow).Assembly.GetType("EasyEdaAltiumGrabber.Models.LibraryViewerItem") is null,
            "dedicated viewer loaders and model are removed from the application assembly");
        var component = new EdaComponent { LcscPartNumber = "C123", Name = "Preview fixture", FootprintName = "R0402" };
        component.SymbolPins.AddRange([new EdaSymbolPin("1", "1", 0, 180, ""), new EdaSymbolPin("2", "2", 0, 0, "")]);
        component.Pads.AddRange([new EdaPad("1", -.5, 0, .5, .5, 0, "1", false, 0),
            new EdaPad("2", .5, 0, .5, .5, 0, "1", false, 0)]);
        var prior = model.SelectedResult;
        try
        {
            model.SelectedResult = new ComponentSearchRow("C123", "Preview fixture", "", "R0402", "{}", component);
            window.UpdateLayout();
            Check(FindDescendants<SymbolPreviewControl>(window).Single().Component == component &&
                  FindDescendants<FootprintPreviewControl>(window).Single().Component == component,
                "selected-component schematic and footprint previews remain available in the importer");
            CaptureWindow(window, "importer-previews.png");
        }
        finally { model.SelectedResult = prior; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + message);
    }
}
