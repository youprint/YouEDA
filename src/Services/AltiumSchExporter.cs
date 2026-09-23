using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
        var symbol = CreateEasyEdaSymbol(source);
        PrepareSymbol(symbol, source);
        return UpsertSymbolAsync(symbol, outputDirectory, source.SymbolPins.Count);
    }

    /// <summary>Adds or refreshes a symbol while retaining all other symbols in youeda.SchLib.</summary>
    public async Task<string> UpsertSymbolAsync(SchComponent symbol, string outputDirectory, int? expectedPinCount = null)
    {
        if (string.IsNullOrWhiteSpace(symbol.Name))
            throw new InvalidDataException("The selected schematic symbol has no component name.");

        var path = Path.Combine(outputDirectory, "youeda.SchLib");
        var library = await OpenSharedSchLibAsync(outputDirectory);

        // Component names are the stable LCSC keys in the output library. Re-importing a
        // component therefore replaces only that symbol and leaves every other one intact.
        Upsert(library, symbol);
        await library.SaveAsync(path);

        var verified = (SchLibrary)await AltiumLibrary.OpenSchLibAsync(path);
        if (verified[symbol.Name] is not SchComponent written ||
            (expectedPinCount is not null && written.Pins.Count != expectedPinCount) ||
            !written.Implementations.Any(model => model.ModelType == "PCBLIB" &&
                model.ModelName == FootprintNameForSymbol(symbol)))
            throw new InvalidDataException("The shared SchLib did not pass post-write verification.");
        return path;
    }

    /// <summary>Opens the shared library once for a single-writer bulk import.</summary>
    public async Task<SchLibrary> OpenSharedSchLibAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "youeda.SchLib");
        var library = File.Exists(path)
            ? (SchLibrary)await AltiumLibrary.OpenSchLibAsync(path)
            : (SchLibrary)AltiumLibrary.CreateSchLib();
        EnsureReadableCanvas(library);
        return library;
    }

    /// <summary>Mutates an already-open library; the caller decides when to checkpoint it.</summary>
    public void Upsert(SchLibrary library, SchComponent symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol.Name))
            throw new InvalidDataException("The selected schematic symbol has no component name.");
        EnsureReadableCanvas(library);
        AttachFootprint(symbol);
        library.Remove(symbol.Name);
        library.Add(symbol);
    }

    /// <summary>Keep the bundled artwork while following the user's Value/Comment conventions.</summary>
    public static void PrepareSymbol(SchComponent symbol, EdaComponent source)
    {
        var footprint = GetOrCreateParameter(symbol, "Footprint", -5.588, 5.08);
        footprint.Value = AltiumFootprintNaming.NameFor(source);
        footprint.IsVisible = false;
        var value = source.Properties.GetValueOrDefault("Value");
        var manufacturerPart = source.Properties.GetValueOrDefault("Manufacturer Part");
        var isDiode = symbol.DesignatorPrefix?.StartsWith('D') == true;
        var isValuePassive = symbol.DesignatorPrefix is { } prefix &&
            (prefix.StartsWith('R') || prefix.StartsWith('C') || prefix.StartsWith('L'));
        if (!string.IsNullOrWhiteSpace(value))
        {
            var valueParameter = GetOrCreateParameter(symbol, "Value", -5.588, 5.08);
            valueParameter.Value = value;
            valueParameter.IsVisible = false;
        }

        if (isDiode && !string.IsNullOrWhiteSpace(manufacturerPart))
        {
            var partParameter = GetOrCreateParameter(symbol, "Manufacturer Part", -5.588, 5.08);
            partParameter.Value = manufacturerPart;
            partParameter.IsVisible = false;
            SetComment(symbol, "=Manufacturer Part");
        }
        else if (!string.IsNullOrWhiteSpace(value))
        {
            SetComment(symbol, "=Value");
        }
        else
        {
            SetComment(symbol, source.Name);
        }

        if (isValuePassive && !string.IsNullOrWhiteSpace(value))
        {
            var designator = symbol.Parameters.OfType<SchParameter>().FirstOrDefault(p => p.Name == "Designator");
            if (designator is not null)
                designator.Location = new CoordPoint(designator.Location.X, Coord.FromMm(2.54));
            var comment = symbol.Parameters.OfType<SchParameter>().First(p => p.Name == "Comment");
            comment.Location = new CoordPoint(Coord.FromMm(0), Coord.FromMm(-2.54));
        }
    }

    private static SchParameter GetOrCreateParameter(SchComponent symbol, string name, double x, double y)
    {
        var parameter = symbol.Parameters.OfType<SchParameter>().FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (parameter is not null) return parameter;
        parameter = new SchParameter
        {
            Name = name, FontId = 1, Color = 8388608, HideName = true,
            Location = new CoordPoint(Coord.FromMm(x), Coord.FromMm(y))
        };
        symbol.AddParameter(parameter);
        return parameter;
    }

    private static void SetComment(SchComponent symbol, string expression)
    {
        symbol.Comment = expression;
        var comment = GetOrCreateParameter(symbol, "Comment", 0, -2.54);
        comment.Value = expression;
        comment.IsVisible = true;
        comment.HideName = true;
    }

    private static void AttachFootprint(SchComponent symbol)
    {
        // AltiumSharp exposes implementations read-only; its concrete list is still mutable.
        // Check the representation explicitly so a future AltiumSharp change fails visibly.
        if ((object)symbol.Implementations is not List<SchImplementation> models)
            throw new InvalidDataException("AltiumSharp changed the schematic implementation collection.");
        models.RemoveAll(model => model.ModelType?.Equals("PCBLIB", StringComparison.OrdinalIgnoreCase) == true);
        models.Add(new SchImplementation
        {
            Description = "YouEDA footprint",
            ModelName = FootprintNameForSymbol(symbol),
            ModelType = "PCBLIB",
            IsCurrent = true
        });
    }

    private static string FootprintNameForSymbol(SchComponent symbol) =>
        symbol.Parameters.OfType<SchParameter>().FirstOrDefault(parameter =>
            parameter.Name.Equals("Footprint", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(parameter.Value))?.Value ?? symbol.Name;

    private static void EnsureReadableCanvas(SchLibrary library)
    {
        // Altium displays an otherwise valid, newly created SchLib on a black canvas when
        // AreaColor is absent; the user's existing Altium library uses this light sheet color.
        library.HeaderParameters ??= new List<KeyValuePair<string, string>>
        {
            new("HEADER", "Protel for Windows - Schematic Library Editor Binary File Version 5.0"),
            new("Weight", "0"), new("MinorVersion", "3"),
            new("UniqueID", new string(Enumerable.Range(0, 8).Select(_ => (char)('A' + Random.Shared.Next(26))).ToArray())),
            new("FontIdCount", "1"), new("FontName1", "Times New Roman"), new("Size1", "10"),
            new("UseMBCS", "T"), new("IsBOC", "T"), new("SheetStyle", "9"),
            new("BorderOn", "T"), new("Display_Unit", "0")
        };
        AddHeaderIfMissing(library, "AreaColor", "16317695");
        AddHeaderIfMissing(library, "SnapGridOn", "T");
        AddHeaderIfMissing(library, "SnapGridSize", "10");
        AddHeaderIfMissing(library, "VisibleGridOn", "T");
        AddHeaderIfMissing(library, "VisibleGridSize", "10");
        AddHeaderIfMissing(library, "CustomX", "18000");
        AddHeaderIfMissing(library, "CustomY", "18000");
        AddHeaderIfMissing(library, "UseCustomSheet", "T");
    }

    private static void AddHeaderIfMissing(SchLibrary library, string name, string value)
    {
        if (!library.HeaderParameters!.Any(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
            library.HeaderParameters!.Add(new KeyValuePair<string, string>(name, value));
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
        var designator = new SchParameter { Name = "Designator", Value = Prefix(source) + "?", IsVisible = true,
            HideName = true, Color = 8388608, FontId = 1 };
        symbol.AddParameter(designator);
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
        const double halfWidth = 10.16, spacing = 2.54;
        var halfHeight = rows * spacing / 2;
        designator.Location = new CoordPoint(Coord.FromMm(-halfWidth), Coord.FromMm(halfHeight + spacing));
        symbol.AddParameter(new SchParameter { Name = "Comment", Value = source.Name, IsVisible = true,
            HideName = true, Color = 8388608, FontId = 1,
            Location = new CoordPoint(Coord.FromMm(-halfWidth), Coord.FromMm(-halfHeight - spacing)) });
        symbol.AddRectangle(new SchRectangle
        {
            Corner1 = new CoordPoint(Coord.FromMm(-halfWidth), Coord.FromMm(-halfHeight)),
            Corner2 = new CoordPoint(Coord.FromMm(halfWidth), Coord.FromMm(halfHeight)),
            Color = 128,
            LineWidth = Coord.FromMm(0.0508),
            IsFilled = false
        });
        // In Altium, the pin location is the body-side endpoint and the orientation points
        // outward. Reversing that convention puts pin numbers inside the body and names outside.
        AddPins(symbol, left, -halfWidth, PinOrientation.Left, rows);
        AddPins(symbol, right, halfWidth, PinOrientation.Right, rows);
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
