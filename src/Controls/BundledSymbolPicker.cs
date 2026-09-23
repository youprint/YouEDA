using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyEdaAltiumGrabber.Models;
using EasyEdaAltiumGrabber.Services;

namespace EasyEdaAltiumGrabber.Controls;

/// <summary>Small searchable picker for components stored in the bundled master SchLib.</summary>
public static class BundledSymbolPicker
{
    public static SymbolLibraryMatch? Pick(EdaComponent component, IEnumerable<SymbolLibraryMatch> symbols,
        UserSymbolLibraryResolver resolver)
    {
        var choices = symbols.ToArray();
        if (choices.Length == 0) return null;
        var dialogBackground = new SolidColorBrush(Color.FromRgb(40, 40, 40));
        var listBackground = new SolidColorBrush(Color.FromRgb(48, 48, 48));
        var textForeground = new SolidColorBrush(Color.FromRgb(242, 242, 242));
        var window = new Window
        {
            Title = $"Choose bundled symbol for {component.LcscPartNumber}",
            Width = 1050, Height = 620, MinWidth = 760, MinHeight = 470,
            Owner = Application.Current?.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize,
            Background = dialogBackground, Foreground = textForeground
        };
        var panel = new Grid { Margin = new Thickness(16) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
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

        var previews = new Grid { Margin = new Thickness(0, 16, 0, 16) };
        previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var fallbackCard = CreatePreviewCard("EasyEDA fallback · generated from pins",
            out var fallbackImage, out var fallbackStatus, out var fallbackDetails);
        var selectedCard = CreatePreviewCard("Selected bundled symbol",
            out var selectedImage, out var selectedStatus, out var selectedDetails);
        previews.Children.Add(fallbackCard);
        Grid.SetColumn(selectedCard, 2);
        previews.Children.Add(selectedCard);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Use EasyEDA fallback", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 135 };
        var accept = new Button { Content = "Use selected symbol", IsDefault = true, MinWidth = 140 };
        cancel.Click += (_, _) => { window.DialogResult = false; };
        accept.Click += (_, _) => { window.DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(accept);
        panel.Children.Add(message);
        Grid.SetRow(picker, 1); panel.Children.Add(picker);
        Grid.SetRow(previews, 2); panel.Children.Add(previews);
        Grid.SetRow(buttons, 3); panel.Children.Add(buttons);
        window.Content = panel;

        var closed = false;
        var selectionVersion = 0;
        window.Closed += (_, _) => closed = true;
        window.Loaded += (_, _) =>
        {
            if (component.SymbolPins.Count == 0)
                fallbackStatus.Text = "EasyEDA did not provide schematic pins.";
            else
            {
                try
                {
                    var fallback = AltiumSchExporter.CreateEasyEdaSymbol(component);
                    fallbackDetails.Text = $"{component.LcscPartNumber} · {fallback.Pins.Count} pins";
                    _ = RenderFallbackAsync(fallback);
                }
                catch (Exception exception) { fallbackStatus.Text = "Preview unavailable: " + exception.Message; }
            }
            UpdateSelected();
        };
        picker.SelectionChanged += (_, _) => { if (window.IsLoaded) UpdateSelected(); };

        async Task RenderFallbackAsync(OriginalCircuit.Altium.Models.Sch.SchComponent fallback)
        {
            try
            {
                var png = await Task.Run(() => SchematicSymbolPreviewRenderer.RenderPng(fallback));
                if (!closed) SetPreview(fallbackImage, fallbackStatus, png);
            }
            catch (Exception exception)
            {
                if (!closed) fallbackStatus.Text = "Preview unavailable: " + exception.Message;
            }
        }

        async void UpdateSelected()
        {
            var version = ++selectionVersion;
            selectedImage.Source = null;
            selectedStatus.Visibility = Visibility.Visible;
            selectedStatus.Text = "Rendering selected symbol…";
            if (picker.SelectedItem is not SymbolLibraryMatch match) return;
            try
            {
                // Arrow-key navigation can change the selection quickly; render only the last one.
                await Task.Delay(120);
                if (closed || version != selectionVersion) return;
                var symbol = resolver.LoadPreviewComponent(match);
                selectedDetails.Text = $"{match.ComponentName} · {symbol.Pins.Count} pins";
                var png = await Task.Run(() => SchematicSymbolPreviewRenderer.RenderPng(symbol));
                if (!closed && version == selectionVersion) SetPreview(selectedImage, selectedStatus, png);
            }
            catch (Exception exception)
            {
                if (!closed && version == selectionVersion)
                    selectedStatus.Text = "Preview unavailable: " + exception.Message;
            }
        }

        return window.ShowDialog() == true ? picker.SelectedItem as SymbolLibraryMatch : null;
    }

    private static Border CreatePreviewCard(string title, out Image image, out TextBlock status, out TextBlock details)
    {
        var layout = new Grid { Margin = new Thickness(10) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        var canvas = new Grid { Background = Brushes.White };
        image = new Image { Stretch = Stretch.Uniform };
        status = new TextBlock
        {
            Text = "Rendering preview…", Foreground = Brushes.DimGray,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12)
        };
        canvas.Children.Add(image);
        canvas.Children.Add(status);
        details = new TextBlock { Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        layout.Children.Add(heading);
        Grid.SetRow(canvas, 1); layout.Children.Add(canvas);
        Grid.SetRow(details, 2); layout.Children.Add(details);
        return new Border { Background = new SolidColorBrush(Color.FromRgb(48, 48, 48)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 75, 75)), BorderThickness = new Thickness(1), Child = layout };
    }

    private static void SetPreview(Image image, TextBlock status, byte[] png)
    {
        using var source = new MemoryStream(png);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = source;
        bitmap.EndInit();
        bitmap.Freeze();
        image.Source = bitmap;
        status.Visibility = Visibility.Collapsed;
    }
}
