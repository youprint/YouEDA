using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EasyEdaAltiumGrabber.Controls;

public enum BatchLibraryOrganization { Normal, FamilyLibraries }

/// <summary>One-time export choice for large batches; it never appears for ordinary small imports.</summary>
public static class BatchLibraryOrganizationPicker
{
    /// <summary>Full import always groups by family; manual exports keep their existing choice.</summary>
    public static BatchLibraryOrganization? Resolve(int componentCount, bool automaticFamilyLibraries,
        Func<int, BatchLibraryOrganization?> prompt)
    {
        if (automaticFamilyLibraries) return BatchLibraryOrganization.FamilyLibraries;
        return Services.FamilyLibraryExportPlan.RequiresOrganizationChoice(componentCount)
            ? prompt(componentCount) : BatchLibraryOrganization.Normal;
    }

    public static BatchLibraryOrganization? Pick(int componentCount)
    {
        var foreground = new SolidColorBrush(Color.FromRgb(242, 242, 242));
        var window = new Window
        {
            Title = "Batch library organization",
            Width = 640,
            Height = 390,
            MinWidth = 540,
            MinHeight = 330,
            Owner = Application.Current?.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
            Foreground = foreground
        };
        var panel = new Grid { Margin = new Thickness(22) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        panel.Children.Add(new TextBlock
        {
            Text = $"You are exporting {componentCount} resolved components.",
            FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = foreground
        });
        var note = new TextBlock
        {
            Text = "Choose whether to keep the normal shared library only, or also create isolated family-based libraries. The Desktop\\Library reference folder is never modified.",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
            Margin = new Thickness(0, 10, 0, 14)
        };
        Grid.SetRow(note, 1); panel.Children.Add(note);

        var choices = new StackPanel();
        var normal = Choice("Import normally", "Keep the existing combined YouEDA library behavior.", true);
        var family = Choice("Create family-based library files", "Altium: convert families with the selected 1 or 3 workers, then merge the combined library. Each run has protected, timestamped family folders. KiCad keeps its existing sequential export.", false);
        choices.Children.Add(normal);
        choices.Children.Add(family);
        Grid.SetRow(choices, 2); panel.Children.Add(choices);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 95, Margin = new Thickness(0, 0, 8, 0) };
        var continueButton = new Button { Content = "Continue", IsDefault = true, MinWidth = 105 };
        cancel.Click += (_, _) => window.DialogResult = false;
        continueButton.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(continueButton);
        Grid.SetRow(buttons, 3); panel.Children.Add(buttons);
        window.Content = panel;

        return window.ShowDialog() == true
            ? family.IsChecked == true ? BatchLibraryOrganization.FamilyLibraries : BatchLibraryOrganization.Normal
            : null;
    }

    private static RadioButton Choice(string title, string description, bool selected)
    {
        var button = new RadioButton { GroupName = "organization", IsChecked = selected, Margin = new Thickness(0, 0, 0, 10), Foreground = Brushes.White };
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        content.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(205, 205, 205)), Margin = new Thickness(0, 3, 0, 0) });
        button.Content = content;
        return button;
    }
}
