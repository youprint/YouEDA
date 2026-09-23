using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using EasyEdaAltiumGrabber.Services;

namespace EasyEdaAltiumGrabber.Controls;

/// <summary>Small searchable picker for components stored in the bundled master SchLib.</summary>
public static class BundledSymbolPicker
{
    public static SymbolLibraryMatch? Pick(string partNumber, IEnumerable<SymbolLibraryMatch> symbols)
    {
        var choices = symbols.ToArray();
        if (choices.Length == 0) return null;
        var dialogBackground = new SolidColorBrush(Color.FromRgb(40, 40, 40));
        var listBackground = new SolidColorBrush(Color.FromRgb(48, 48, 48));
        var textForeground = new SolidColorBrush(Color.FromRgb(242, 242, 242));
        var window = new Window
        {
            Title = $"Choose bundled symbol for {partNumber}", Width = 620, Height = 190,
            Owner = Application.Current?.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize,
            Background = dialogBackground, Foreground = textForeground
        };
        var panel = new Grid { Margin = new Thickness(16) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var message = new TextBlock
        {
            Text = "Type to search your bundled library, then select the symbol:",
            Foreground = textForeground, Margin = new Thickness(0, 0, 0, 7)
        };
        var itemText = new FrameworkElementFactory(typeof(TextBlock));
        itemText.SetBinding(TextBlock.TextProperty, new Binding(nameof(SymbolLibraryMatch.ComponentName)));
        itemText.SetValue(TextBlock.ForegroundProperty, textForeground);
        var itemTemplate = new DataTemplate { VisualTree = itemText };
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, listBackground));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, textForeground));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
        var selected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(65, 101, 129))));
        itemStyle.Triggers.Add(selected);
        var picker = new ComboBox
        {
            IsEditable = true, IsTextSearchEnabled = true, ItemsSource = choices,
            ItemTemplate = itemTemplate, ItemContainerStyle = itemStyle,
            Foreground = textForeground, Background = listBackground,
            BorderBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
            MaxDropDownHeight = 280, MinWidth = 560
        };
        TextSearch.SetTextPath(picker, nameof(SymbolLibraryMatch.ComponentName));
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
