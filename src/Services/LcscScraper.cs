using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Fetches the public EasyEDA CAD payload for an LCSC part number.</summary>
public sealed class LcscScraper(Action<string>? log = null)
{
    // EasyEDA's public library endpoint serves a browser client. Keep its web-contract details here.
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = CreateClient();
    private readonly Action<string>? _log = log;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://easyeda.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.ParseAdd("text/javascript, */*; q=0.01");
        client.DefaultRequestHeaders.Referrer = new Uri("https://easyeda.com/");
        return client;
    }

    public async Task<string> GetRawComponentAsync(string partNumber, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                partNumber, "^C\\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("Part number must look like C12345.");

        var normalizedPart = partNumber.ToUpperInvariant();
        // /svgs contains preview graphics only. /components contains dataStr and packageDetail.dataStr.
        var uri = $"api/products/{Uri.EscapeDataString(normalizedPart)}/components?version=6.4.19.5";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                _log?.Invoke($"GET {uri} (attempt {attempt}/3)");
                using var response = await Http.GetAsync(uri, ct);

                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException($"{normalizedPart} was not found by EasyEDA.");

                if (response.StatusCode == HttpStatusCode.Forbidden)
                    throw new HttpRequestException(
                        "EasyEDA denied the component request (403). Check your Internet connection or try again later; " +
                        "the app now uses the component-data endpoint and browser-compatible request headers.");

                if (response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500)
                {
                    if (attempt == 3)
                        throw new HttpRequestException($"EasyEDA returned {(int)response.StatusCode} after retries.");

                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt * 2);
                    _log?.Invoke($"Server returned {(int)response.StatusCode}; waiting {delay.TotalSeconds:0}s.");
                    await Task.Delay(delay, ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                ValidateEnvelope(json, normalizedPart);
                return json;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < 3)
            {
                _log?.Invoke("Request timed out; retrying.");
            }
        }

        throw new HttpRequestException("EasyEDA request failed after retries.");
    }

    private static void ValidateEnvelope(string json, string partNumber)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("EasyEDA returned an empty payload.");

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException($"EasyEDA reported no usable CAD data for {partNumber}.");
            if (!root.TryGetProperty("result", out var result) || result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new InvalidOperationException($"EasyEDA returned no component CAD result for {partNumber}.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("EasyEDA returned an invalid JSON response.", exception);
        }
    }
}
