using EasyEdaAltiumGrabber.Models;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>
/// Assigns an importable component to one safe library family.  The result is deliberately
/// deterministic: batch organization must never pause to ask the user for a category.
/// </summary>
public sealed class ComponentFamilyClassifier
{
    public ComponentFamilyClassification Classify(EdaComponent component)
    {
        var prefix = Property(component, "pre").Trim().TrimEnd('?').ToUpperInvariant();
        var evidence = string.Join(' ', component.Tags.Append(component.Name).Append(component.Description)
            .Append(component.FootprintName).Concat(component.Properties.Select(item => $"{item.Key} {item.Value}"))).ToUpperInvariant();

        ComponentFamilyClassification Match(string family, FamilyConfidence confidence, string reason) =>
            new(family, confidence, reason, component.LcscPartNumber);

        // Explicit type/category text wins over broad package guesses or designator prefixes.
        if (Has(evidence, "FERRITE", "COMMON MODE CHOKE", "CHOKE", "INDUCT")) return Match("Inductance", FamilyConfidence.High, "category/type: inductor or ferrite");
        if (Has(evidence, "RESIST")) return Match("Resistors", FamilyConfidence.High, "category/type: resistor");
        if (Has(evidence, "CAPACIT")) return Match("Capacitors", FamilyConfidence.High, "category/type: capacitor");
        if (Has(evidence, "CRYSTAL", "RESONATOR", "OSCILLATOR")) return Match("Crystal", FamilyConfidence.High, "category/type: crystal or oscillator");
        if (Has(evidence, "MOSFET", "TRANSISTOR", " BJT", " IGBT", "JFET")) return Match("Transistors", FamilyConfidence.High, "category/type: transistor or FET");
        if (Has(evidence, "TVS", "ESD", "ZENER", "SCHOTTKY", "PHOTODIODE", "LED", "DIODE")) return Match("Diodes", FamilyConfidence.High, "category/type: diode or LED");
        if (Has(evidence, "RELAY", "SWITCH", "PUSHBUTTON", "TACT", "BUTTON")) return Match("Switches_Relays", FamilyConfidence.High, "category/type: switch or relay");
        if (Has(evidence, "CONNECTOR", "HEADER", "SOCKET", "TERMINAL BLOCK", " USB", "JST", "FPC", "FFC", "SD CARD")) return Match("Connectors", FamilyConfidence.High, "category/type: connector");
        if (Has(evidence, "MICROCONTROLLER", " MCU", " MPU", "MEMORY", "EEPROM", "FLASH", "OPAMP", "OP AMP", "REGULATOR", "LDO", "BUCK", "BOOST", "CONVERTER", "ADC", "DAC", "LOGIC", "DRIVER", "INTERFACE", "ISOLATOR", " IC ")) return Match("IC", FamilyConfidence.High, "category/type: integrated circuit");

        // Designators are reliable fallback evidence when EasyEDA's category is sparse.
        return prefix switch
        {
            "R" => Match("Resistors", FamilyConfidence.Medium, "designator prefix R"),
            "C" => Match("Capacitors", FamilyConfidence.Medium, "designator prefix C"),
            "L" => Match("Inductance", FamilyConfidence.Medium, "designator prefix L"),
            "D" or "LED" => Match("Diodes", FamilyConfidence.Medium, "designator prefix D/LED"),
            "Q" => Match("Transistors", FamilyConfidence.Medium, "designator prefix Q"),
            "U" => Match("IC", FamilyConfidence.Medium, "designator prefix U"),
            "Y" or "X" => Match("Crystal", FamilyConfidence.Medium, "designator prefix Y/X"),
            "J" or "CN" or "CON" => Match("Connectors", FamilyConfidence.Medium, "designator prefix connector"),
            "SW" or "K" => Match("Switches_Relays", FamilyConfidence.Medium, "designator prefix switch/relay"),
            _ => Match("Other", FamilyConfidence.Low, "no safe family classification")
        };
    }

    private static bool Has(string evidence, params string[] values) => values.Any(value =>
        evidence.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string Property(EdaComponent component, string name) => component.Properties
        .FirstOrDefault(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
}

public enum FamilyConfidence { Low, Medium, High }

public sealed record ComponentFamilyClassification(string Family, FamilyConfidence Confidence, string Evidence, string LcscPartNumber);
