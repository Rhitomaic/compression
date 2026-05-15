using System.Text.Json.Serialization;

namespace SmartCompress.Core;

public record AdaptiveSegment(
    [property: JsonPropertyName("t")] float Start,
    [property: JsonPropertyName("d")] float Duration,
    [property: JsonPropertyName("c")] float Mean
);

public sealed class ComplexityProfile
{
    public const int BucketCount = 20;

    public float[]           Buckets        { get; }
    public AdaptiveSegment[] Segments       { get; }
    public float             P90            { get; }
    // Duration-weighted absolute mean complexity (bits per pixel per frame).
    // Multiplied by duration gives the "total bits to encode" proxy used by
    // the CQP prediction formula.
    public float             MeanComplexity { get; }

    public static ComplexityProfile Empty =>
        new(new float[BucketCount], []);

    // Legacy / from stored float[] only (no segment data)
    public ComplexityProfile(float[] buckets)
        : this(buckets, []) { }

    private ComplexityProfile(float[] buckets, AdaptiveSegment[] segments)
    {
        Buckets        = buckets;
        Segments       = segments;
        P90            = segments.Length > 0
            ? ComputeP90Segments(segments)
            : ComputeP90Buckets(buckets);
        MeanComplexity = ComputeMeanComplexity(segments);
    }

    public static ComplexityProfile FromSegments(AdaptiveSegment[] segs, double totalDuration)
    {
        var buckets = Resample(segs, totalDuration);
        return new ComplexityProfile(buckets, segs);
    }

    // ── Metrics ───────────────────────────────────────────────────────────────

    public float Similarity(ComplexityProfile other)
    {
        // Shape similarity via duration-weighted sorted CDFs (order-independent).
        // Two videos with the same mix of complexity levels match regardless of
        // when those levels occur in the timeline.
        float shapeSim = (Segments.Length > 0 && other.Segments.Length > 0)
            ? CosineSim(BuildSortedCdf(Segments), BuildSortedCdf(other.Segments))
            : CosineSim(Buckets, other.Buckets);

        // Magnitude awareness: cosine alone is scale-invariant, so two clips
        // with the same SHAPE but very different absolute complexity would
        // match perfectly. Apply a soft penalty for absolute scale mismatch
        // so references with similar bppf rank higher than wildly different ones.
        if (MeanComplexity > 0f && other.MeanComplexity > 0f)
        {
            float ratio = MathF.Min(
                MeanComplexity / other.MeanComplexity,
                other.MeanComplexity / MeanComplexity);
            shapeSim *= MathF.Pow(ratio, 0.25f);
        }
        return shapeSim;
    }

    private static float ComputeMeanComplexity(AdaptiveSegment[] segs)
    {
        float weightedSum = 0f, totalDuration = 0f;
        foreach (var s in segs) { weightedSum += s.Duration * s.Mean; totalDuration += s.Duration; }
        return totalDuration > 0 ? weightedSum / totalDuration : 0f;
    }

    // Build a 20-point empirical CDF of complexity, weighted by segment duration.
    // Bucket b holds the complexity value at the (b+0.5)/20 percentile of total duration.
    private static float[] BuildSortedCdf(AdaptiveSegment[] segs)
    {
        int   n      = BucketCount;
        var   sorted = segs.OrderBy(s => s.Mean).ToArray();
        float total  = sorted.Sum(s => s.Duration);
        if (total <= 0) return new float[n];

        var   result = new float[n];
        float cumul  = 0f;
        int   j      = 0;
        for (int b = 0; b < n; b++)
        {
            float target = (b + 0.5f) / n * total;
            while (j < sorted.Length - 1 && cumul + sorted[j].Duration < target)
            {
                cumul += sorted[j].Duration;
                j++;
            }
            result[b] = sorted[j].Mean;
        }
        return result;
    }

    private static float CosineSim(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f, magA = 0f, magB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        return (magA == 0f || magB == 0f) ? 0f
            : dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }

    public string ToSpectreBar()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var v in Buckets)
        {
            // Thresholds for absolute bits-per-pixel-per-frame:
            // Simple < 0.05, Medium 0.05-0.15, Complex > 0.15 (matches VideoAnalyzer's bppf classifier).
            string color = v < 0.05f ? "green" : v < 0.15f ? "yellow" : "red";
            char   block = v switch
            {
                < 0.02f => '▁', < 0.04f => '▂', < 0.06f => '▃', < 0.08f => '▄',
                < 0.12f => '▅', < 0.16f => '▆', < 0.20f => '▇', _       => '█',
            };
            sb.Append($"[{color}]{block}[/]");
        }
        return sb.ToString();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    // Distribute variable-resolution segments into n uniform buckets via
    // duration-weighted averaging. Segment means are already normalized.
    private static float[] Resample(AdaptiveSegment[] segs, double totalDuration)
    {
        int n = BucketCount;
        if (segs.Length == 0 || totalDuration <= 0) return new float[n];

        var sums    = new double[n];
        var weights = new double[n];
        double w    = totalDuration / n;

        foreach (var seg in segs)
        {
            double segEnd  = seg.Start + seg.Duration;
            int    bFirst  = Math.Clamp((int)(seg.Start / w), 0, n - 1);
            int    bLast   = Math.Clamp((int)(segEnd    / w), 0, n - 1);
            for (int b = bFirst; b <= bLast; b++)
            {
                double lo      = b * w;
                double hi      = lo + w;
                double overlap = Math.Min(segEnd, hi) - Math.Max((double)seg.Start, lo);
                if (overlap <= 0) continue;
                sums[b]    += seg.Mean * overlap;
                weights[b] += overlap;
            }
        }

        var result = new float[n];
        for (int b = 0; b < n; b++)
            result[b] = weights[b] > 0 ? (float)(sums[b] / weights[b]) : 0f;
        return result;
    }

    // Duration-weighted P90: complexity level exceeded by the hardest 10% of the video.
    private static float ComputeP90Segments(AdaptiveSegment[] segs)
    {
        float total  = segs.Sum(s => s.Duration);
        float target = total * 0.9f;
        float accum  = 0f;
        foreach (var s in segs.OrderBy(s => s.Mean))
        {
            accum += s.Duration;
            if (accum >= target) return s.Mean;
        }
        return segs.Max(s => s.Mean);
    }

    private static float ComputeP90Buckets(float[] b)
    {
        if (b.Length == 0) return 1f;
        var sorted = b.OrderBy(v => v).ToArray();
        return sorted[(int)(sorted.Length * 0.9f)];
    }
}
