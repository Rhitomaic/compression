using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartCompress.Core;

public record CalibrationSample(
    [property: JsonPropertyName("p")]  int  Predicted,
    [property: JsonPropertyName("a")]  int  Actual,
    [property: JsonPropertyName("ts")] long TimestampUtc
);

public record EncoderCalibration(
    [property: JsonPropertyName("samples")] List<CalibrationSample> Samples
);

/// <summary>
/// Records (predicted, actual) CQP pairs after each real compression and
/// derives a per-encoder bias correction. Self-tuning: if predictions are
/// consistently +1 CQP too high, the correction subtracts 1 from future ones.
/// </summary>
public static class PredictorCalibration
{
    private const int MaxSamplesPerEncoder = 50;
    private const int MinSamplesForBias    = 5;

    private static readonly JsonSerializerOptions JsonOpts = new();
    private static readonly object _lock = new();

    /// <summary>Record one observation. Called after binary search converges.</summary>
    public static void RecordResidual(string encoder, int predictedCqp, int actualCqp)
    {
        try
        {
            lock (_lock)
            {
                var data = Load();
                if (!data.TryGetValue(encoder, out var cal))
                    data[encoder] = cal = new EncoderCalibration(new List<CalibrationSample>());

                cal.Samples.Add(new CalibrationSample(
                    predictedCqp, actualCqp,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

                // Bounded ring — keep most recent samples so the bias reflects
                // current encoder/ffmpeg behaviour, not ancient history.
                if (cal.Samples.Count > MaxSamplesPerEncoder)
                    cal.Samples.RemoveRange(0, cal.Samples.Count - MaxSamplesPerEncoder);

                Save(data);
            }
        }
        catch { }
    }

    /// <summary>
    /// Returns the bias correction to ADD to a fresh prediction.
    /// Positive = our predictions tend to undershoot; we should aim higher.
    /// </summary>
    public static double BiasFor(string encoder)
    {
        try
        {
            lock (_lock)
            {
                var data = Load();
                if (!data.TryGetValue(encoder, out var cal)) return 0.0;
                if (cal.Samples.Count < MinSamplesForBias)    return 0.0;

                // Median of (actual - predicted). Robust against the occasional
                // outlier where binary search hit its 8-pass cap or similar.
                var residuals = cal.Samples
                    .Select(s => (double)(s.Actual - s.Predicted))
                    .OrderBy(r => r)
                    .ToList();
                return residuals[residuals.Count / 2];
            }
        }
        catch { return 0.0; }
    }

    public static IReadOnlyDictionary<string, (double Bias, int Samples)> Snapshot()
    {
        try
        {
            lock (_lock)
            {
                var data = Load();
                return data.ToDictionary(
                    kv => kv.Key,
                    kv => (BiasFor(kv.Key), kv.Value.Samples.Count));
            }
        }
        catch { return new Dictionary<string, (double, int)>(); }
    }

    // ── Storage ──────────────────────────────────────────────────────────────

    private static string Path =>
        System.IO.Path.Combine(AppPaths.DataDir, "predictor_calibration.json");

    private static Dictionary<string, EncoderCalibration> Load()
    {
        try
        {
            if (!File.Exists(Path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, EncoderCalibration>>(
                File.ReadAllText(Path)) ?? new();
        }
        catch { return new(); }
    }

    private static void Save(Dictionary<string, EncoderCalibration> data)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(Path, JsonSerializer.Serialize(data, JsonOpts));
    }
}
