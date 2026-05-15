using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartCompress.Core;

public record CacheEntry(
    [property: JsonPropertyName("cqp")]         int                Cqp,
    [property: JsonPropertyName("enc")]         string             Encoder,
    [property: JsonPropertyName("limitBytes")]  long?              LimitBytes,
    [property: JsonPropertyName("scaleHeight")] int?               ScaleHeight,
    [property: JsonPropertyName("durationSec")] int                DurationSec,
    [property: JsonPropertyName("segs")]        AdaptiveSegment[]? Segments,
    [property: JsonPropertyName("lastUsed")]    long               LastUsedUtc,
    [property: JsonPropertyName("useCount")]    int                UseCount,
    [property: JsonPropertyName("ob")]          long?              OutputBytes    = null,
    [property: JsonPropertyName("mc")]          float?             MeanComplexity = null,
    [property: JsonPropertyName("ss")]          float?             Ssim           = null
);

public static class CqpCache
{
    private const int  MaxEntries     = 500;
    private const int  StaleDays      = 60;
    private const long MaxFileSizeBytes = 2L * 1024 * 1024; // 2 MB

    private static readonly JsonSerializerOptions JsonOpts = new(); // compact (no indent)

    // ── Public API ────────────────────────────────────────────────────────────

    public static int? Lookup(string key)
    {
        // Main cache first (most likely match for a re-encode of the same file),
        // then benchmark cache as fallback.
        var main = LoadDict(AppPaths.CacheFile);
        if (main?.TryGetValue(key, out var e) == true) return e.Cqp;
        var bench = LoadDict(AppPaths.BenchmarkCacheFile);
        return bench?.TryGetValue(key, out var be) == true ? be.Cqp : null;
    }

    // targetBytes: the size limit we're trying to hit (null = quality-squeeze mode).
    // threshold:   minimum similarity to accept a reference entry.
    //
    // Prediction model:
    //   output_size ∝ MeanComplexity × Duration × 2^(-cqp/6)
    // So for any candidate reference:
    //   target_cqp = ref_cqp + 6*log2(ref_size / target_size)
    //                       + 6*log2((new_mc × new_dur) / (ref_mc × ref_dur))
    // In quality mode (no target size) the size term is dropped and the
    // complexity term uses per-frame mc only (duration is irrelevant when
    // optimising for per-frame visual quality).
    // Number of top references blended into the final prediction. Picking N=1
    // (the old behaviour) made the predictor swing whenever one quirky entry
    // happened to be the closest match. Averaging the top few smooths noise
    // and is now meaningful with a populated benchmark cache.
    private const int MultiRefTopN = 5;

    public static (int Cqp, float Similarity)? LookupSimilar(
        ComplexityProfile profile,
        string encoder, long? targetBytes, int? scaleHeight,
        double newDurationSec,
        float threshold = 0.75f,
        double? targetSsim = null)
    {
        var entries = LoadAllEntries();
        if (entries.Count == 0) return null;
        return PredictFromEntries(entries, profile, encoder, targetBytes,
            scaleHeight, newDurationSec, threshold, targetSsim);
    }

    // The core prediction logic, factored out so the cross-validator can pass
    // a held-out subset of entries to test "would the predictor have nailed this?".
    internal static (int Cqp, float Similarity)? PredictFromEntries(
        List<CacheEntry> entries,
        ComplexityProfile profile,
        string encoder, long? targetBytes, int? scaleHeight,
        double newDurationSec,
        float threshold = 0.75f,
        double? targetSsim = null)
    {
        if (entries.Count == 0) return null;

        bool   isSizeMode = targetBytes.HasValue && targetBytes.Value > 0;
        double k          = EncoderSlope.For(encoder);

        // Collect every viable reference's predicted CQP, then weighted-average
        // the top N by similarity. Single-best (the old approach) discards
        // 95% of the cache once it's populated.
        var candidates = new List<(double Cqp, float Similarity)>();

        foreach (var entry in entries)
        {
            if (entry.Encoder      != encoder)     continue;
            if (entry.DurationSec  <= 0)           continue;
            if (entry.Segments == null || entry.Segments.Length == 0) continue;
            if (!entry.MeanComplexity.HasValue || entry.MeanComplexity.Value <= 0) continue;
            if (profile.MeanComplexity <= 0f) continue;

            // Resolution matching: same scale = direct, different = math transfer.
            // Native-res entries (null scale) are skipped for cross-res because we
            // don't know the source height — would need a schema change to fix.
            double resolutionAdjust = 0.0;
            if (entry.ScaleHeight != scaleHeight)
            {
                if (entry.ScaleHeight == null || scaleHeight == null) continue;
                // size ∝ height² at same complexity/cqp; CQP shift = 2k · log2(h_new/h_ref)
                resolutionAdjust = 2.0 * k *
                    Math.Log2((double)scaleHeight.Value / entry.ScaleHeight.Value);
            }

            bool hasOutputBytes = entry.OutputBytes.HasValue && entry.OutputBytes.Value > 0;

            // Quality mode: only use quality-mode entries (limitBytes == null).
            if (!isSizeMode && entry.LimitBytes != null) continue;
            // Size mode without stored output bytes: legacy fallback, require same limit.
            if (isSizeMode && !hasOutputBytes && entry.LimitBytes != targetBytes) continue;

            var refProfile = ComplexityProfile.FromSegments(entry.Segments, entry.DurationSec);
            float sim = profile.Similarity(refProfile);
            // Mild similarity penalty when transferring across resolutions —
            // spatial complexity varies with scale in content-dependent ways.
            if (resolutionAdjust != 0.0) sim *= 0.92f;

            float minSim = (hasOutputBytes && isSizeMode) ? threshold : 0.88f;
            if (sim < minSim) continue;

            double sizeAdjust = (hasOutputBytes && isSizeMode)
                ? Math.Log2((double)entry.OutputBytes!.Value / targetBytes!.Value) * k
                : 0.0;

            double complexityAdjust;
            if (isSizeMode)
            {
                // Size mode: more weighted-complexity = bigger file at same CQP, so
                // to hit the SAME target size we need HIGHER CQP (more compression).
                double newWC = (double)profile.MeanComplexity     * newDurationSec;
                double refWC = (double)entry.MeanComplexity.Value * entry.DurationSec;
                complexityAdjust = Math.Log2(newWC / refWC) * k;
            }
            else
            {
                // Quality mode: more complex content gives LOWER SSIM at the same CQP.
                // To maintain the same quality floor, complex content needs LOWER CQP
                // (more bits, less compression). Direction is the OPPOSITE of size mode.
                // (Was a sign bug previously — predictor was pushing CQP UP for hard
                // content, then quality check failed and binary search clawed back down.)
                complexityAdjust = -Math.Log2(profile.MeanComplexity / entry.MeanComplexity.Value) * k;
            }

            // Quality-mode anchor: if we know both the target SSIM floor and the
            // reference entry's measured SSIM, shift CQP toward whatever value the
            // SSIM-vs-CQP curve predicts will hit the floor.
            //
            // Model: log2(1 - SSIM) = a + cqp/k (higher CQP → larger (1-SSIM))
            // → cqp_target = cqp_ref + k · log2((1 - target) / (1 - ref))
            // If ref SSIM > target floor: ratio < 1, log2 < 0... wait that's backwards.
            // Let me redo: 1-ref < 1-target → ratio (1-target)/(1-ref) > 1 → log2 > 0.
            // We want CQP_target > CQP_ref (ref over-quality, can compress more). Correct.
            double ssimAdjust = 0.0;
            if (!isSizeMode && targetSsim.HasValue
                && entry.Ssim.HasValue && entry.Ssim.Value > 0 && entry.Ssim.Value < 1.0
                && targetSsim.Value > 0 && targetSsim.Value < 1.0)
            {
                ssimAdjust = k * Math.Log2((1.0 - targetSsim.Value) / (1.0 - entry.Ssim.Value));
            }

            double predictedCqp = entry.Cqp + sizeAdjust + complexityAdjust + resolutionAdjust + ssimAdjust;
            candidates.Add((predictedCqp, sim));
        }

        if (candidates.Count == 0) return null;

        // Top-N weighted average. Weight = similarity^2 so the closest match
        // dominates but isn't winner-take-all.
        candidates.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        int take = Math.Min(MultiRefTopN, candidates.Count);

        double sumWeighted = 0, sumWeight = 0;
        for (int i = 0; i < take; i++)
        {
            double w = (double)candidates[i].Similarity * candidates[i].Similarity;
            sumWeighted += candidates[i].Cqp * w;
            sumWeight   += w;
        }
        double avgCqp = sumWeighted / sumWeight;

        // Self-calibration: shift by the median residual observed in past encodes
        // for this encoder. Catches systematic bias the formula can't predict
        // (e.g. an encoder consistently behaving differently from its fitted slope).
        avgCqp += PredictorCalibration.BiasFor(encoder);

        return ((int)Math.Round(avgCqp), candidates[0].Similarity);
    }

    public static void Save(string key, int cqp, long outputBytes,
        string encoder, long? limitBytes, int? scaleHeight,
        int durationSec, ComplexityProfile? profile,
        bool isBenchmark = false,
        double? ssim = null)
    {
        try
        {
            string path = isBenchmark ? AppPaths.BenchmarkCacheFile : AppPaths.CacheFile;
            var dict = LoadDict(path) ?? new Dictionary<string, CacheEntry>();

            // Main cache caps file size to avoid runaway growth. Benchmark cache is
            // explicitly populated and never pruned — its entries are valuable references.
            if (!isBenchmark)
            {
                bool isNew = !dict.ContainsKey(key);
                if (isNew && File.Exists(path) &&
                    new FileInfo(path).Length >= MaxFileSizeBytes)
                    return;
            }

            int  useCount = dict.TryGetValue(key, out var existing) ? existing.UseCount + 1 : 1;
            long lastUsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            dict[key] = new CacheEntry(cqp, encoder, limitBytes, scaleHeight,
                durationSec, profile?.Segments, lastUsed, useCount,
                outputBytes > 0 ? outputBytes : null,
                profile != null && profile.MeanComplexity > 0 ? profile.MeanComplexity : null,
                ssim is { } s && s > 0 && s < 1.0 ? (float)s : null);

            if (!isBenchmark) Prune(dict);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(dict, JsonOpts));
        }
        catch { }
    }

    public static string MakeKey(string src, double durationS,
        string encoder, long? sizeLimitBytes, int? scaleHeight, int? cqp = null)
    {
        var contentHash = SampleHash(src);
        int durSec      = (int)Math.Round(durationS);
        var limitPart   = sizeLimitBytes.HasValue ? sizeLimitBytes.Value.ToString() : "none";
        var scalePart   = scaleHeight.HasValue ? $"{scaleHeight}p" : "native";
        // CQP suffix lets benchmark CQP-sweep entries coexist for the same
        // (clip, encoder, scale, limit). Regular saves omit it so a re-encode
        // overwrites the previous best CQP for that configuration.
        var cqpPart     = cqp.HasValue ? $"_cqp{cqp.Value}" : "";
        return $"{contentHash}_{durSec}s_{encoder}_{limitPart}_{scalePart}{cqpPart}";
    }

    // ── Pruning ───────────────────────────────────────────────────────────────

    private static void Prune(Dictionary<string, CacheEntry> dict)
    {
        if (dict.Count <= MaxEntries) return;

        long cutoff = DateTimeOffset.UtcNow.AddDays(-StaleDays).ToUnixTimeSeconds();

        var stale = dict
            .Where(kv => kv.Value.UseCount <= 1 && kv.Value.LastUsedUtc < cutoff)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in stale) dict.Remove(k);

        if (dict.Count > MaxEntries)
        {
            var toRemove = dict
                .OrderBy(kv => kv.Value.LastUsedUtc)
                .Take(dict.Count - MaxEntries)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in toRemove) dict.Remove(k);
        }
    }

    // ── Content hash ─────────────────────────────────────────────────────────

    // Sample 4 × 16 KB chunks at 0 %, 25 %, 50 %, 75 % of the file.
    private static string SampleHash(string path)
    {
        const int chunkSize = 16 * 1024;
        long fileSize = new FileInfo(path).Length;

        try
        {
            using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[chunkSize];

            for (int i = 0; i < 4; i++)
            {
                long pos = fileSize * i / 4;
                pos = Math.Clamp(pos, 0, Math.Max(0, fileSize - chunkSize));
                fs.Seek(pos, SeekOrigin.Begin);
                int read = fs.Read(buf);
                hasher.AppendData(buf, 0, read);
            }

            return Convert.ToHexString(hasher.GetHashAndReset())[..16].ToLowerInvariant();
        }
        catch
        {
            return fileSize.ToString("x16");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, CacheEntry>? LoadDict(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(
                File.ReadAllText(path));
        }
        catch { return null; }
    }

    // All entries from both caches, deduped by key. Main cache takes precedence
    // if the same key exists in both (more recent user-driven encode).
    private static List<CacheEntry> LoadAllEntries()
    {
        var seen = new HashSet<string>();
        var result = new List<CacheEntry>();

        var main = LoadDict(AppPaths.CacheFile);
        if (main != null)
            foreach (var (k, v) in main)
                if (seen.Add(k)) result.Add(v);

        var bench = LoadDict(AppPaths.BenchmarkCacheFile);
        if (bench != null)
            foreach (var (k, v) in bench)
                if (seen.Add(k)) result.Add(v);

        return result;
    }
}
