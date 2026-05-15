using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartCompress.Core;

/// <summary>
/// Per-encoder rate-distortion slope k in the model `size ∝ 2^(-cqp/k)`.
/// The classic universal value is 6 ("6 CQP = 2× size"), but real encoders deviate:
/// measured AMF runs sit at ~7.0, NVENC and x264 are closer to 6.0, AV1 can be ~5.5.
/// Fitting per encoder from benchmark CQP sweeps removes ~10–15% prediction error.
/// </summary>
public static class EncoderSlope
{
    public const double DefaultK = 6.0;

    // Below this many sample groups, fall back to the default — one group can be
    // noisy enough to give a misleading slope.
    private const int MinGroupsForFit = 2;

    private static Dictionary<string, double>? _cache;
    private static readonly object _lock = new();

    /// <summary>Returns the fitted slope k for `encoder`, or `DefaultK` if uncalibrated.</summary>
    public static double For(string encoder)
    {
        EnsureLoaded();
        return _cache!.TryGetValue(encoder, out var k) ? k : DefaultK;
    }

    /// <summary>Force a re-fit on the next call. Use after a benchmark run.</summary>
    public static void Invalidate()
    {
        lock (_lock) _cache = null;
    }

    public static IReadOnlyDictionary<string, double> AllCalibrated()
    {
        EnsureLoaded();
        return _cache!;
    }

    // ── Fitting ──────────────────────────────────────────────────────────────

    private static void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_cache != null) return;
            _cache = Fit();
        }
    }

    private static Dictionary<string, double> Fit()
    {
        var entries = LoadBenchmarkEntries();
        if (entries.Count == 0) return new Dictionary<string, double>();

        // Group by (encoder, content fingerprint) so each group is one fixed clip
        // at one fixed resolution — the only variable is CQP. That's exactly what
        // we need to isolate the slope from confounders like duration and complexity.
        // Key: enc | scaleHeight | durationSec | mc rounded to 5 dp.
        var groups = new Dictionary<string, List<(int Cqp, long Size)>>();
        foreach (var e in entries)
        {
            if (e.OutputBytes is null || e.OutputBytes.Value <= 0) continue;
            if (e.MeanComplexity is null || e.MeanComplexity.Value <= 0) continue;

            string key = $"{e.Encoder}|{e.ScaleHeight}|{e.DurationSec}|{e.MeanComplexity.Value:F5}";
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = new List<(int, long)>();
            list.Add((e.Cqp, e.OutputBytes.Value));
        }

        // Per-encoder slope = median over per-group slopes.
        // Median is robust against the occasional group with weird content.
        var perEncoderSlopes = new Dictionary<string, List<double>>();
        foreach (var (groupKey, points) in groups)
        {
            if (points.Count < 3) continue;  // LSQ needs ≥3 to be meaningful
            var encoder = groupKey.Split('|')[0];
            double? slope = FitGroupSlope(points);
            if (slope is null) continue;

            if (!perEncoderSlopes.TryGetValue(encoder, out var slopes))
                perEncoderSlopes[encoder] = slopes = new List<double>();
            slopes.Add(slope.Value);
        }

        var result = new Dictionary<string, double>();
        foreach (var (encoder, slopes) in perEncoderSlopes)
        {
            if (slopes.Count < MinGroupsForFit) continue;
            slopes.Sort();
            double median = slopes[slopes.Count / 2];
            // Sanity clamp — real slopes are 4–10. Anything outside that is noise.
            if (median < 4 || median > 10) continue;
            result[encoder] = median;
        }
        return result;
    }

    // Least-squares fit of log2(size) = a - cqp/k.  Returns k = -1/slope.
    // Returns null if data is degenerate (zero variance in CQP).
    private static double? FitGroupSlope(List<(int Cqp, long Size)> points)
    {
        int n = points.Count;
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        foreach (var (cqp, size) in points)
        {
            double x = cqp;
            double y = Math.Log2(size);
            sumX  += x;
            sumY  += y;
            sumXX += x * x;
            sumXY += x * y;
        }
        double denom = n * sumXX - sumX * sumX;
        if (Math.Abs(denom) < 1e-9) return null;

        double slope = (n * sumXY - sumX * sumY) / denom;  // d(log2 size)/d(cqp)
        if (slope >= 0) return null;                       // size must decrease with cqp
        return -1.0 / slope;
    }

    // ── Benchmark cache reader (decoupled from CqpCache internals) ───────────

    private static List<CacheEntry> LoadBenchmarkEntries()
    {
        try
        {
            if (!File.Exists(AppPaths.BenchmarkCacheFile)) return new();
            var dict = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(
                File.ReadAllText(AppPaths.BenchmarkCacheFile));
            return dict?.Values.ToList() ?? new();
        }
        catch { return new(); }
    }
}
