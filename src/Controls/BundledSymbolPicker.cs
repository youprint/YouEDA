using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EasyEdaAltiumGrabber.Services;

namespace EasyEdaAltiumGrabber.Controls;

/// <summary>Small searchable picker for components stored in the bundled master SchLib.</summary>
public static class BundledSymbolPicker
{
    public static SymbolLibraryMatch? Pick(string partNumber, IEnumerable<SymbolLibraryMatch> symbols)
    {
        var choices = symbols.ToArray();
        if (choices.Length == 0) return null;
        var window = new Window
        {
            Title = $"Choose bundled symbol for {partNumber}", Width = 620, Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.CanResize,
            Background = System.Windows.Media.Brushes.White
        };
        var panel = new Grid { Margin = new Thickness(16) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var message = new TextBlock { Text = "Type to search your bundled library, then select the symbol:", Margin = new Thickness(0, 0, 0, 7) };
        var picker = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, ItemsSource = choices, DisplayMemberPath = nameof(SymbolLibraryMatch.ComponentName), MinWidth = 560 };
        picker.SelectedIndex = 0;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Use EasyEDA fallback", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 135 };
        var accept = new Button { Content = "Use selected symbol", IsDefault = true, MinWidth = 140 };
        cancel.Click += (_, _) => { window.DialogResult = false; };
        accept.Click += (_, _) => { window.DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(accept);
        panel.Children.Add(message); Grid.SetRow(picker, 1); panel.Children.Add(picker); Grid.SetRow(buttons, 3); panel.Children.Add(buttons);
        window.Content = panel;
        return window.ShowDialog() == true ? picker.SelectedItem as SymbolLibraryMatch : null;
    }
}
