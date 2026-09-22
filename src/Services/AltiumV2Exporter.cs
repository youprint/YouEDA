using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EasyEdaAltiumGrabber.Models;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Pcb;
using OriginalCircuit.Eda.Primitives;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Native PcbLib writer using the current OriginalCircuit.Altium source tree.</summary>
public sealed class AltiumV2Exporter
{
    /// <summary>Upserts one footprint into the shared, native Altium PcbLib.</summary>
    public async Task<string> UpsertPcbLibAsync(EdaComponent source, string outputDirectory, Downloaded3dModel? model = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "youeda.PcbLib");

        // A newly created V2 PcbLibrary can be internally round-tripped but some Altium releases
        // reject its default Library section. Seed from an Altium-authored library so its required
        // section metadata, layer mapping, and version information are preserved.
        PcbLibrary library;
        if (File.Exists(path))
        {
            library = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(path);
        }
        else
        {
            var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "AltiumTemplate.PcbLib");
            if (!File.Exists(templatePath))
                throw new FileNotFoundException("The bundled Altium PcbLib template is missing.", templatePath);
            library = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(templatePath);
            foreach (var existing in library.Components.Select(item => item.Name).ToArray())
                library.Remove(existing);
            library.ComponentParamsToc.Clear();
            // The Altium-authored template contains a diode STEP asset.  It is not linked to
            // our generated footprint and must not be carried into every new component library.
            library.Models.Clear();
        }

        // Re-running a part refreshes it without duplicating its footprint. Other components,
        // their parameters, and their embedded STEP data remain untouched.
        library.Remove(source.LcscPartNumber);
        var component = BuildComponent(source, library, model);

        library.Add(component.Build());
        await library.SaveAsync(path);

        // Catch writer regressions before reporting a file as generated. This validates its compound
        // structure and all serialized primitive records with the same reader implementation.
        var verified = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(path);
        if (!verified.Contains(source.LcscPartNumber) ||
            (model is not null && !verified.Models.Any(item => item.Name == model.FileName)))
            throw new InvalidDataException("The shared PcbLib did not pass post-write verification.");
        return path;
    }

    private static ComponentBuilder BuildComponent(EdaComponent source, PcbLibrary library, Downloaded3dModel? model)
    {
        var component = PcbComponent.Create(source.LcscPartNumber).WithDescription(source.Name);

        foreach (var pad in source.Pads)
        {
            component.AddPad(builder =>
            {
                builder.At(Coord.FromMm(pad.Xmm), Coord.FromMm(pad.Ymm))
                    .Size(Coord.FromMm(pad.WidthMm), Coord.FromMm(pad.HeightMm))
                    .Shape(PadShape.Rectangular)
                    .Rotation(pad.RotationDeg)
                    .WithDesignator(pad.Number);

                // A zero-hole pad must be emitted as an SMD pad; merely setting HoleSize(0)
                // leaves a partially configured pad record which Altium can reject on load.
                if (pad.Plated && pad.HoleMm > 0)
                    builder.ThroughHole(Coord.FromMm(pad.HoleMm)).Layer(74); // Multi-layer
                else
                    builder.Smd(MapCopperLayer(pad.Layer));
            });
        }

        foreach (var track in source.Shapes.Where(shape => shape.Kind == "TRACK" && shape.PointsMm.Count > 1))
        {
            for (var index = 1; index < track.PointsMm.Count; index++)
            {
                var start = track.PointsMm[index - 1];
                var end = track.PointsMm[index];
                component.AddTrack(trackBuilder => trackBuilder
                    .From(Coord.FromMm(start.X), Coord.FromMm(start.Y))
                    .To(Coord.FromMm(end.X), Coord.FromMm(end.Y))
                    .Width(Coord.FromMm(track.StrokeMm))
                    .Layer(MapEasyEdaLayer(track.Layer)));
            }
        }

        if (model is not null)
        {
            // Embed the STEP data and add the mandatory body-to-model link. The footprint is
            // already normalised about (0,0), so the EasyEDA model origin is the same point.
            var modelId = Guid.TryParseExact(model.Source.Uuid, "N", out var easyEdaId)
                ? easyEdaId.ToString("B").ToUpperInvariant()
                : Guid.NewGuid().ToString("B").ToUpperInvariant();
            // If this part is refreshed, replace only its identical EasyEDA model record.
            library.Models.RemoveAll(item => string.Equals(item.Id, modelId, StringComparison.OrdinalIgnoreCase));
            var embeddedModel = new PcbModel
            {
                Id = modelId,
                Name = model.FileName,
                IsEmbedded = true,
                ModelSource = "Undefined",
                StepData = model.StepData
            };
            embeddedModel.RecomputeChecksum();
            library.Models.Add(embeddedModel);

            var halfWidth = model.Source.WidthMm > 0 ? model.Source.WidthMm / 2 : 0.5;
            var halfHeight = model.Source.HeightMm > 0 ? model.Source.HeightMm / 2 : 0.5;
            component.AddComponentBody(body => body
                .OnLayer("MECHANICAL1")
                .WithName(model.FileName)
                // Altium treats a model-based body as a closed contour; an empty outline
                // is tolerated by readers but rejected by the native PcbLib editor.
                .Kind(0)
                .ShapeBased(false)
                .ModelId(modelId)
                .OverallHeight(Coord.FromMm(model.HeightMm))
                .AddPoint(Coord.FromMm(model.Source.Xmm - halfWidth), Coord.FromMm(model.Source.Ymm - halfHeight))
                .AddPoint(Coord.FromMm(model.Source.Xmm + halfWidth), Coord.FromMm(model.Source.Ymm - halfHeight))
                .AddPoint(Coord.FromMm(model.Source.Xmm + halfWidth), Coord.FromMm(model.Source.Ymm + halfHeight))
                .AddPoint(Coord.FromMm(model.Source.Xmm - halfWidth), Coord.FromMm(model.Source.Ymm + halfHeight))
                .At2D(Coord.FromMm(model.Source.Xmm), Coord.FromMm(model.Source.Ymm))
                .Rotation2D(0)
                .Rotation3D(model.Source.RotationXDeg, model.Source.RotationYDeg, model.Source.RotationZDeg)
                .OffsetZ(Coord.FromMm(model.Source.Zmm + model.ZOffsetMm)));
        }

        return component;
    }

    private static int MapCopperLayer(string easyEdaLayer) => easyEdaLayer == "2" ||
        easyEdaLayer.Equals("BottomLayer", StringComparison.OrdinalIgnoreCase) ? 32 : 1;

    private static int MapEasyEdaLayer(string easyEdaLayer) => easyEdaLayer switch
    {
        "1" or "TopLayer" => 1,
        "2" or "BottomLayer" => 32,
        // Binary PcbLib layer IDs are not the visible layer numbers: IDs 2..31 are
        // Mid-Layer 1..30.  Top/Bottom Overlay are 33/34 respectively.  Using 21
        // accidentally placed silkscreen on Mid-Layer 20, which appears purple.
        "3" or "TopSilkLayer" => 33,
        "4" or "BottomSilkLayer" => 34,
        _ => 33
    };
}
