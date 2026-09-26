using System.Windows;
using EasyEdaAltiumGrabber.ViewModels;

namespace EasyEdaAltiumGrabber;

public partial class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    private void CloseSettings(object sender, RoutedEventArgs e) => Close();
}
