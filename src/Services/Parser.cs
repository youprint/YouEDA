using System.Globalization;
using System.IO;
using System.Text.Json;
using EasyEdaAltiumGrabber.Models;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Converts an EasyEDA footprint's tilde-delimited records into Altium millimetre primitives.</summary>
public sealed class Parser
{
    // EasyEDA library coordinates are in 10-mil increments: 1 unit = 0.254 mm.
    private const double EasyEdaUnitToMm = .254;

    public EdaComponent Parse(string partNumber, string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        var footprintData = FindFootprintData(document.RootElement)
            ?? throw new InvalidDataException("EasyEDA returned no public footprint data for this part.");
        var (originX, originY) = GetOrigin(footprintData);
        var component = new EdaComponent { LcscPartNumber = partNumber };
        PopulateSymbolMetadata(document.RootElement, component);
        var shapeRecords = FindShapeRecords(footprintData).ToArray();
        Populate3dModelMetadata(component, shapeRecords, originX, originY);

        foreach (var record in shapeRecords)
            ParseRecord(record, component, originX, originY);

        ParseSymbolPins(document.RootElement, component);

        if (component.Pads.Count == 0)
            throw new InvalidDataException("EasyEDA returned no PAD records for this footprint.");
        return component;
    }

    private static void PopulateSymbolMetadata(JsonElement root, EdaComponent component)
    {
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return;
        component.Name = Text(result, "title");
        component.Description = Text(result, "description");
        if (string.IsNullOrWhiteSpace(component.Name)) component.Name = component.LcscPartNumber;
        if (result.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            foreach (var tag in tags.EnumerateArray())
                if (tag.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tag.GetString())) component.Tags.Add(tag.GetString()!);

        // The symbol's c_para contains the original designator prefix and useful
        // component metadata (Value, Manufacturer, Supplier Part, etc.).
        if (!result.TryGetProperty("dataStr", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("head", out var head) || head.ValueKind != JsonValueKind.Object ||
            !head.TryGetProperty("c_para", out var parameters) || parameters.ValueKind != JsonValueKind.Object) return;
        foreach (var parameter in parameters.EnumerateObject())
        {
            if (parameter.Value.ValueKind == JsonValueKind.String)
                component.Properties[parameter.Name] = parameter.Value.GetString() ?? string.Empty;
        }
    }

    private static void ParseSymbolPins(JsonElement root, EdaComponent component)
    {
        // Symbol records reside in result.dataStr; packageDetail.dataStr is the footprint.
        if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("dataStr", out var data) ||
            !data.TryGetProperty("shape", out var shapes) || shapes.ValueKind != JsonValueKind.Array) return;
        foreach (var item in shapes.EnumerateArray())
        {
            var raw = item.GetString();
            if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith("P~", StringComparison.OrdinalIgnoreCase)) continue;
            var segments = raw.Split("^^", StringSplitOptions.None);
            var settings = segments[0].Split('~');
            if (settings.Length < 7) continue;
            var name = segments.Length > 3 ? segments[3].Split('~') : [];
            var anchor = name.Length > 5 ? name[5] : string.Empty;
            var pinName = name.Length > 4 ? name[4] : string.Empty;
            component.SymbolPins.Add(new EdaSymbolPin(settings[3], pinName, (int)Number(settings[2]), (int)Number(settings[6]), anchor));
        }
    }

    private static void Populate3dModelMetadata(EdaComponent component, IEnumerable<string> shapeRecords, double originX, double originY)
    {
        // EasyEDA's actual asset ID and placement live in SVGNODE, which is what the
        // EasyEDA Loader uses. head.uuid_3d identifies a related library record, not
        // necessarily the downloadable STEP asset.
        var raw = shapeRecords.FirstOrDefault(record => record.StartsWith("SVGNODE~", StringComparison.OrdinalIgnoreCase));
        if (raw is null) return;

        try
        {
            using var nodeDocument = JsonDocument.Parse(raw[(raw.IndexOf('~') + 1)..]);
            var root = nodeDocument.RootElement;
            if (!root.TryGetProperty("attrs", out var attrs) || attrs.ValueKind != JsonValueKind.Object) return;
            var uuid = Text(attrs, "uuid");
            if (string.IsNullOrWhiteSpace(uuid)) return;

            var (x, y) = Pair(Text(attrs, "c_origin"));
            var (rx, ry, rz) = Triple(Text(attrs, "c_rotation"));
            var name = Text(attrs, "title");
            component.ThreeDModel = new Eda3dModel(
                uuid,
                string.IsNullOrWhiteSpace(name) ? "EasyEDA-" + uuid : name,
                RelativeMm(x, originX), -RelativeMm(y, originY), Mm(Number(Text(attrs, "z"))),
                rx, ry, rz, Mm(Number(Text(attrs, "c_width"))), Mm(Number(Text(attrs, "c_height"))));
        }
        catch (JsonException)
        {
            // A malformed optional 3D outline must not prevent the 2D footprint export.
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static (string X, string Y) Pair(string value)
    {
        var values = value.Split(',', StringSplitOptions.TrimEntries);
        return (values.ElementAtOrDefault(0) ?? "0", values.ElementAtOrDefault(1) ?? "0");
    }

    private static (double X, double Y, double Z) Triple(string value)
    {
        var values = value.Split(',', StringSplitOptions.TrimEntries);
        return (Number(values.ElementAtOrDefault(0)), Number(values.ElementAtOrDefault(1)), Number(values.ElementAtOrDefault(2)));
    }

    // The component envelope holds both the schematic dataStr and packageDetail.dataStr.
    // Select the latter by looking for a shape array containing a PCB PAD record.
    private static JsonElement? FindFootprintData(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("dataStr", out var dataStr) && dataStr.ValueKind == JsonValueKind.Object &&
                ContainsPadRecord(dataStr))
                return dataStr;

            foreach (var property in element.EnumerateObject())
            {
                var result = FindFootprintData(property.Value);
                if (result.HasValue) return result;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var result = FindFootprintData(item);
                if (result.HasValue) return result;
            }
        }
        return null;
    }

    private static bool ContainsPadRecord(JsonElement dataStr)
    {
        if (!dataStr.TryGetProperty("shape", out var shapes) || shapes.ValueKind != JsonValueKind.Array) return false;
        foreach (var shape in shapes.EnumerateArray())
            if (shape.ValueKind == JsonValueKind.String && shape.GetString()!.StartsWith("PAD~", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static (double X, double Y) GetOrigin(JsonElement dataStr)
    {
        if (dataStr.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object)
            return (Number(head, "x"), Number(head, "y"));
        return (0, 0);
    }

    private static double Number(JsonElement objectElement, string property) =>
        objectElement.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private static IEnumerable<string> FindShapeRecords(JsonElement dataStr)
    {
        if (!dataStr.TryGetProperty("shape", out var shapes) || shapes.ValueKind != JsonValueKind.Array) yield break;
        foreach (var shape in shapes.EnumerateArray())
            if (shape.ValueKind == JsonValueKind.String)
                yield return shape.GetString()!;
    }

    private static void ParseRecord(string record, EdaComponent component, double originX, double originY)
    {
        var fields = record.Split('~');
        if (fields.Length == 0) return;
        var kind = fields[0].Trim().ToUpperInvariant();

        if (kind == "PAD" && fields.Length >= 10)
        {
            // PAD~shape~x~y~width~height~layer~net~number~holeRadius~...~rotation
            component.Pads.Add(new EdaPad(
                fields[8], RelativeMm(fields[2], originX), -RelativeMm(fields[3], originY),
                Mm(fields[4]), Mm(fields[5]), Number(fields.ElementAtOrDefault(11)), fields[6],
                Number(fields[9]) > 0, Mm(Number(fields[9]))));
            return;
        }

        if ((kind is "TRACK" or "SOLIDREGION") && fields.Length >= 5)
        {
            var points = ParsePoints(fields[4], originX, originY);
            component.Shapes.Add(new EdaShape(
                kind, fields.ElementAtOrDefault(2) ?? "TopLayer", points, Mm(Number(fields.ElementAtOrDefault(1)))));
        }
    }

    private static IReadOnlyList<(double X, double Y)> ParsePoints(string value, double originX, double originY)
    {
        var fields = value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<(double X, double Y)>();
        for (var index = 0; index + 1 < fields.Length; index += 2)
            result.Add((RelativeMm(fields[index], originX), -RelativeMm(fields[index + 1], originY)));
        return result;
    }

    private static double Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static double Mm(string value) => Number(value) * EasyEdaUnitToMm;
    private static double Mm(double value) => value * EasyEdaUnitToMm;
    private static double RelativeMm(string value, double origin) => (Number(value) - origin) * EasyEdaUnitToMm;
}
