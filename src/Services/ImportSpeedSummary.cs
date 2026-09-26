using System.Globalization;

namespace EasyEdaAltiumGrabber.Services;

/// <summary>Average throughput and completion estimate, independent of the UI timer.</summary>
public static class ImportSpeedSummary
{
    public static string Format(string phase, int succeeded, int processed, int total, TimeSpan elapsed)
    {
        var seconds = Math.Max(0, elapsed.TotalSeconds);
        var rate = seconds > 0 ? Math.Max(0, succeeded) * 60.0 / seconds : 0;
        var remaining = Math.Max(0, total - processed);
        string estimate;
        if (remaining == 0) estimate = "queue finished";
        else if (processed < 3 || seconds < 3) estimate = "ETA estimating…";
        else estimate = "ETA ~" + Duration(TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds / 2,
            seconds / processed * remaining)));
        return $"{phase}: {rate.ToString("0.0", CultureInfo.InvariantCulture)} parts/min avg · " +
            $"{processed}/{total} processed · elapsed {Duration(elapsed)} · {estimate}";
    }

    private static string Duration(TimeSpan elapsed) =>
        $"{(long)Math.Max(0, elapsed.TotalHours):00}:{Math.Max(0, elapsed.Minutes):00}:{Math.Max(0, elapsed.Seconds):00}";
}
