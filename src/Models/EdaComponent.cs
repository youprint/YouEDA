namespace EasyEdaAltiumGrabber.Models;
public sealed class EdaComponent
{
    public required string LcscPartNumber { get; init; }
    public string Name { get; set; } = "Unnamed";
    public string Description { get; set; } = "";
    public List<EdaPad> Pads { get; } = [];
    public List<EdaShape> Shapes { get; } = [];
    public List<EdaSymbolPin> SymbolPins { get; } = [];
    public List<EdaSymbolUnit> SymbolUnits { get; } = [];
    public List<string> Tags { get; } = [];
    public Dictionary<string,string> Properties { get; } = [];

    // This reference comes from packageDetail.dataStr.head.uuid_3d.  It is optional because
    // EasyEDA does not provide a model for every footprint.
    public Eda3dModel? ThreeDModel { get; set; }
}

public sealed record Eda3dModel(
    string Uuid,
    string Name,
    double Xmm,
    double Ymm,
    double Zmm,
    double RotationXDeg,
    double RotationYDeg,
    double RotationZDeg,
    double WidthMm,
    double HeightMm);
public sealed record EdaSymbolPin(string Number, string Name, int ElectricalType, int RotationDeg, string NameAnchor)
{
    public double Xmm { get; init; }
    public double Ymm { get; init; }
    public double LengthMm { get; init; }
}
public sealed class EdaSymbolUnit
{
    public List<EdaSymbolPin> Pins { get; } = [];
    public List<EdaSymbolGraphic> Graphics { get; } = [];
}
public sealed record EdaSymbolGraphic(string Kind, IReadOnlyList<(double X, double Y)> PointsMm, double StrokeMm, bool Filled);
public sealed record EdaPad(string Number, double Xmm, double Ymm, double WidthMm, double HeightMm, double RotationDeg, string Layer, bool Plated, double HoleMm)
{
    public string Shape { get; init; } = "RECT";
    public double SlotLengthMm { get; init; }
    public IReadOnlyList<(double X, double Y)> PolygonPointsMm { get; init; } = [];
}
public sealed record EdaShape(string Kind, string Layer, IReadOnlyList<(double X, double Y)> PointsMm, double StrokeMm);
