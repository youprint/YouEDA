using System.Reflection;

namespace EasyEdaAltiumGrabber;

public partial class MainWindow : System.Windows.Window
{
    public MainWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "development";
        Title = $"YouEDA {version} — EasyEDA / LCSC Component Loader";
        DataContext = new ViewModels.MainViewModel();
    }

    private void OpenSettings(object sender, System.Windows.RoutedEventArgs e)
    {
        new SettingsWindow((ViewModels.MainViewModel)DataContext) { Owner = this }.ShowDialog();
    }
}
