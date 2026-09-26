using System;
using System.IO;
using System.Text.Json;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Stores small, user-specific desktop preferences outside the installed app folder.</summary>
public sealed class AppSettingsStore
{
    private const string DefaultExportFormat = "Altium";
    private readonly string _path;

    public AppSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YouEDA", "settings.json");
    }

    public AppUserSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppUserSettings(DefaultExportFormat, null);
            var settings = JsonSerializer.Deserialize<AppUserSettings>(File.ReadAllText(_path));
            return settings ?? new AppUserSettings(DefaultExportFormat, null);
        }
        catch (IOException) { return new AppUserSettings(DefaultExportFormat, null); }
        catch (JsonException) { return new AppUserSettings(DefaultExportFormat, null); }
    }

    public void Save(AppUserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, _path, true);
        }
        catch (IOException)
        {
            // Preferences must never stop part export when a profile folder is temporarily busy.
        }
        catch (UnauthorizedAccessException)
        {
            // An installed application may run under a restricted profile; retain in-memory state.
        }
    }
}

public sealed record AppUserSettings(
    string ExportFormat,
    string? OutputDirectory,
    string? AltiumOutputDirectory = null,
    string? KiCadOutputDirectory = null,
    string? Theme = null,
    bool DiagnosticLogging = false);
