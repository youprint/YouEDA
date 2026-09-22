using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyEdaAltiumGrabber.Models;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Downloads the public STEP payload referenced by an EasyEDA footprint's uuid_3d field.</summary>
public sealed class EasyEda3dModelDownloader
{
    // This is the same public endpoint used by the EasyEDA client and open-source importers.
    private const string StepEndpoint = "https://modules.easyeda.com/qAxj6KHrDKw4blvCG8QJPs7Y/";
    private const string ObjEndpoint = "https://modules.easyeda.com/3dmodel/";

    private static readonly HttpClient Client = CreateClient();

    public async Task<Downloaded3dModel?> TryDownloadAsync(Eda3dModel? model, string outputDirectory, CancellationToken cancellationToken)
    {
        if (model is null || string.IsNullOrWhiteSpace(model.Uuid)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, StepEndpoint + Uri.EscapeDataString(model.Uuid));
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var stepData = Encoding.UTF8.GetString(bytes);
        if (!stepData.Contains("ISO-10303-21", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EasyEDA returned a 3D payload that is not a STEP model.");

        var fileName = MakeSafeFileName(model.Name) + ".step";
        var path = Path.Combine(outputDirectory, fileName);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        var bounds = await TryGetObjBoundsAsync(model.Uuid, cancellationToken);
        return new Downloaded3dModel(model, fileName, path, stepData, bounds.ZOffsetMm, bounds.HeightMm);
    }

    private static HttpClient CreateClient()
    {
        // modules.easyeda.com is an older CDN endpoint. Pinning its connection to TLS 1.2
        // avoids an SSPI credential negotiation failure seen with newer Schannel defaults.
        var handler = new HttpClientHandler
        {
            SslProtocols = SslProtocols.Tls12,
            CheckCertificateRevocationList = false,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EasyEdaAltiumGrabber/1.0");
        client.DefaultRequestHeaders.Referrer = new Uri("https://easyeda.com/");
        return client;
    }

    // OBJ vertices are already in millimetres.  EasyEDALoader uses the lowest vertex
    // as the mounting-plane correction because that information is not in STEP metadata.
    private static async Task<ObjBounds> TryGetObjBoundsAsync(string uuid, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(ObjEndpoint + Uri.EscapeDataString(uuid), cancellationToken);
            if (!response.IsSuccessStatusCode) return default;
            var obj = await response.Content.ReadAsStringAsync(cancellationToken);
            double? minZ = null, maxZ = null;
            foreach (var line in obj.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("v ", StringComparison.OrdinalIgnoreCase)) continue;
                var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (values.Length >= 4 && double.TryParse(values[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z))
                {
                    minZ = !minZ.HasValue || z < minZ.Value ? z : minZ;
                    maxZ = !maxZ.HasValue || z > maxZ.Value ? z : maxZ;
                }
            }
            return minZ.HasValue && maxZ.HasValue ? new ObjBounds(Math.Abs(minZ.Value), Math.Max(0, maxZ.Value - minZ.Value)) : default;
        }
        catch (HttpRequestException) { return default; }
    }

    private static string MakeSafeFileName(string name)
    {
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "EasyEDA-3D-Model" : safe;
    }
}

public sealed record Downloaded3dModel(Eda3dModel Source, string FileName, string Path, string StepData, double ZOffsetMm, double HeightMm);
internal readonly record struct ObjBounds(double ZOffsetMm, double HeightMm);
