using System.Windows;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Applies one complete visual-resource dictionary for the desktop shell.</summary>
public static class ThemeService
{
    public const string MidnightBlueprint = "Midnight Blueprint";
    public const string GraphiteMint = "Graphite & Mint";
    public const string WarmStudio = "Warm Studio";

    public static IReadOnlyList<string> Names { get; } = [MidnightBlueprint, GraphiteMint, WarmStudio];

    public static string Normalize(string? name) => Names.Contains(name) ? name! : MidnightBlueprint;

    public static void Apply(string? name)
    {
        var application = Application.Current;
        if (application is null) return;
        var normalized = Normalize(name);
        var fileName = normalized switch
        {
            GraphiteMint => "GraphiteMint.xaml",
            WarmStudio => "WarmStudio.xaml",
            _ => "MidnightBlueprint.xaml"
        };

        var dictionaries = application.Resources.MergedDictionaries;
        dictionaries.Clear();
        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"/YouEDA;component/Themes/{fileName}", UriKind.Relative)
        });
    }
}
