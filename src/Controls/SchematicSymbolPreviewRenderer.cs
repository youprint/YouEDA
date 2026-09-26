using System;
using System.IO;
using OriginalCircuit.Altium.Models.Sch;
using OriginalCircuit.Altium.Rendering.Raster;
using OriginalCircuit.Eda.Rendering;

namespace EasyEdaAltiumGrabber.Controls;

/// <summary>Renders the same native schematic primitives that YouEDA exports.</summary>
public static class SchematicSymbolPreviewRenderer
{
    public static byte[] RenderPng(SchComponent symbol, int width = 480, int height = 320)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        using var output = new MemoryStream();
        new RasterRenderer().RenderAsync(symbol, output, new RenderOptions
        {
            Width = width,
            Height = height,
            AutoZoom = true
        }).AsTask().GetAwaiter().GetResult();
        return output.ToArray();
    }
}
