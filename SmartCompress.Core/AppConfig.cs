using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartCompress.Core;

public enum EncoderSpeed { Fast, Slow, VerySlow }

public record Preset(
    [property: JsonPropertyName("key")]     string Key,
    [property: JsonPropertyName("id")]      string Id,
    [property: JsonPropertyName("size_mb")] double? SizeMb,
    [property: JsonPropertyName("codecs")]  List<string> Codecs,
    [property: JsonPropertyName("label")]   string Label
);

public record EncoderInfo(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("desc")]  string Desc,
    [property: JsonPropertyName("speed")] string Speed = "fast"
);

public record SsimThreshold(
    [property: JsonPropertyName("min")]   double Min,
    [property: JsonPropertyName("label")] string Label
);

public class AppConfig
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "0.0.0";

    [JsonPropertyName("presets")]
    public List<Preset> Presets { get; init; } = [];

    [JsonPropertyName("encoder_candidates")]
    public Dictionary<string, List<string>> EncoderCandidates { get; init; } = [];

    [JsonPropertyName("default_cqp")]
    public Dictionary<string, int> DefaultCqp { get; init; } = [];

    [JsonPropertyName("max_cqp")]
    public Dictionary<string, int> MaxCqp { get; init; } = [];

    [JsonPropertyName("ssim_floors")]
    public Dictionary<string, double> SsimFloors { get; init; } = [];

    [JsonPropertyName("max_cqp_by_complexity")]
    public Dictionary<string, int> MaxCqpByComplexity { get; init; } = [];

    [JsonPropertyName("min_output_height")]
    public List<List<int>> MinOutputHeight { get; init; } = [];

    [JsonPropertyName("resolution_steps")]
    public List<int> ResolutionSteps { get; init; } = [];

    [JsonPropertyName("resolution_info")]
    public Dictionary<string, string> ResolutionInfo { get; init; } = [];

    [JsonPropertyName("encoder_info")]
    public Dictionary<string, EncoderInfo> EncoderInfo { get; init; } = [];

    [JsonPropertyName("ssim_labels")]
    public List<SsimThreshold> SsimLabels { get; init; } = [];

    public static AppConfig Load(string configPath)
    {
        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<AppConfig>(json)
            ?? throw new InvalidDataException("Failed to parse config.json");
    }

    public string FormatMb(long bytes) => $"{bytes / 1_048_576.0:F2} MB";

    public int GetDefaultCqp(string encoder) =>
        DefaultCqp.TryGetValue(encoder, out var v) ? v : 28;

    public int GetMaxCqp(string encoder, string complexity = "Medium")
    {
        var baseMax = MaxCqp.TryGetValue(encoder, out var b) ? b : 45;
        var cap = MaxCqpByComplexity.TryGetValue(complexity, out var c) ? c : baseMax;
        return Math.Min(baseMax, cap);
    }

    public double GetSsimFloor(string complexity = "Medium") =>
        SsimFloors.TryGetValue(complexity, out var v) ? v : 0.93;

    public string GetSsimLabel(double score)
    {
        foreach (var entry in SsimLabels)
            if (score >= entry.Min)
                return entry.Label;
        return "Poor";
    }

    public int GetMinOutputHeight(int srcHeight)
    {
        foreach (var pair in MinOutputHeight)
            if (srcHeight > pair[0])
                return pair[1];
        return srcHeight;
    }

    public EncoderSpeed GetEncoderSpeed(string encoder)
    {
        if (!EncoderInfo.TryGetValue(encoder, out var info)) return EncoderSpeed.Fast;
        return info.Speed.ToLowerInvariant() switch
        {
            "very_slow" => EncoderSpeed.VerySlow,
            "slow"      => EncoderSpeed.Slow,
            _           => EncoderSpeed.Fast,
        };
    }

    public static string CodecFamily(string encoder)
    {
        if (encoder.Contains("264")) return "h264";
        if (encoder.Contains("265") || encoder.Contains("hevc")) return "h265";
        return "av1";
    }
}
