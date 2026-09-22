using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EasyEdaAltiumGrabber.Models;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Sch;
using OriginalCircuit.Eda.Primitives;
using PinOrientation = OriginalCircuit.Eda.Enums.PinOrientation;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Creates and maintains native Altium symbols in the shared SchLib.</summary>
public sealed class AltiumSchExporter
{
    public Task<string> UpsertEasyEdaSymbolAsync(EdaComponent source, string outputDirectory)
    {
        if (source.SymbolPins.Count == 0)
            throw new InvalidDataException("EasyEDA returned no schematic P pin records.");
        return UpsertSymbolAsync(CreateEasyEdaSymbol(source), outputDirectory, source.SymbolPins.Count);
    }

    /// <summary>Adds or refreshes a symbol while retaining all other symbols in youeda.SchLib.</summary>
    public async Task<string> UpsertSymbolAsync(SchComponent symbol, string outputDirectory, int? expectedPinCount = null)
    {
        if (string.IsNullOrWhiteSpace(symbol.Name))
            throw new InvalidDataException("The selected schematic symbol has no component name.");

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "youeda.SchLib");
        var library = File.Exists(path)
            ? (SchLibrary)await AltiumLibrary.OpenSchLibAsync(path)
            : (SchLibrary)AltiumLibrary.CreateSchLib();

        // Component names are the stable LCSC keys in the output library. Re-importing a
        // component therefore replaces only that symbol and leaves every other one intact.
        library.Remove(symbol.Name);
        library.Add(symbol);
        await library.SaveAsync(path);

        var verified = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(path);
        if (verified[symbol.Name] is not SchComponent written ||
            (expectedPinCount is not null && written.Pins.Count != expectedPinCount))
            throw new InvalidDataException("The shared SchLib did not pass post-write verification.");
        return path;
    }

    public static SchComponent CreateEasyEdaSymbol(EdaComponent source)
    {
        var symbol = new SchComponent
        {
            Name = source.LcscPartNumber,
            LibReference = source.LcscPartNumber,
            Description = string.IsNullOrWhiteSpace(source.Description) ? source.Name : source.Description,
            Comment = source.Name,
            DesignatorPrefix = Prefix(source),
            DesignItemId = source.Properties.GetValueOrDefault("Manufacturer Part"),
            PartCount = 1
        };
        symbol.AddParameter(new SchParameter { Name = "Designator", Value = Prefix(source) + "?", IsVisible = true, HideName = true });
        symbol.AddParameter(new SchParameter { Name = "LCSC Part", Value = source.LcscPartNumber, IsVisible = false });
        if (source.Properties.TryGetValue("Value", out var value) && !string.IsNullOrWhiteSpace(value))
            symbol.AddParameter(new SchParameter { Name = "Value", Value = value, IsVisible = true, HideName = true });
        if (source.Properties.TryGetValue("Manufacturer", out var manufacturer) && !string.IsNullOrWhiteSpace(manufacturer))
            symbol.AddParameter(new SchParameter { Name = "Manufacturer", Value = manufacturer, IsVisible = false });

        var left = source.SymbolPins.Where(pin => pin.NameAnchor.Equals("start", StringComparison.OrdinalIgnoreCase)).ToArray();
        var right = source.SymbolPins.Except(left).ToArray();
        if (left.Length == 0 || right.Length == 0)
        {
            left = source.SymbolPins.Where((_, index) => index % 2 == 0).ToArray();
            right = source.SymbolPins.Except(left).ToArray();
        }
        var rows = Math.Max(2, Math.Max(left.Length, right.Length));
        const double halfWidth = 5.08, spacing = 2.54, pinLength = 2.54;
        var halfHeight = rows * spacing / 2;
        symbol.AddRectangle(new SchRectangle
        {
            Corner1 = new CoordPoint(Coord.FromMm(-halfWidth), Coord.FromMm(-halfHeight)),
            Corner2 = new CoordPoint(Coord.FromMm(halfWidth), Coord.FromMm(halfHeight)),
            Color = 0,
            IsFilled = false
        });
        AddPins(symbol, left, -halfWidth - pinLength, PinOrientation.Right, rows);
        AddPins(symbol, right, halfWidth + pinLength, PinOrientation.Left, rows);
        return symbol;
    }

    private static void AddPins(SchComponent symbol, EdaSymbolPin[] pins, double x, PinOrientation orientation, int rows)
    {
        for (var index = 0; index < pins.Length; index++)
        {
            var y = (rows - 1) * 2.54 / 2 - index * 2.54;
            var pin = pins[index];
            symbol.AddPin(SchPin.Create(pin.Number).WithName(string.IsNullOrWhiteSpace(pin.Name) ? pin.Number : pin.Name)
                .At(Coord.FromMm(x), Coord.FromMm(y)).Length(Coord.FromMm(2.54)).Orient(orientation).Electrical(MapElectrical(pin.ElectricalType)).Build());
        }
    }

    private static PinElectricalType MapElectrical(int type) => type switch
    {
        1 => PinElectricalType.InputOutput,
        2 => PinElectricalType.Output,
        3 => PinElectricalType.Power,
        4 => PinElectricalType.Input,
        _ => PinElectricalType.Passive
    };

    private static string Prefix(EdaComponent component) => component.Properties.TryGetValue("pre", out var prefix) && !string.IsNullOrWhiteSpace(prefix)
        ? prefix.Trim().TrimEnd('?') : "U";
}
