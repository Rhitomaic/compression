namespace SmartCompress.Core;

/// <summary>
/// Strategy decided up-front per file. Lets <c>CompressSmart</c> route to a
/// path tuned for the actual problem instead of one binary search regardless.
/// </summary>
public enum CompressionStrategy
{
    /// <summary>Squeeze for quality only — limit is absent or non-binding.</summary>
    QualityFirst,
    /// <summary>Limit is binding — walk the resolution ladder, binary-search per step.</summary>
    SizeConstrained,
}

public enum EstimatorConfidence
{
    /// <summary>No usable cache references — predictions are heuristic.</summary>
    None,
    Low,
    Medium,
    High,
}

/// <summary>
/// Up-front plan handed to <c>CompressSmart</c>. The two starting CQPs come
/// from <c>CqpCache.LookupSimilar</c>: one anchored on the quality floor, one
/// on the size limit. Their relationship determines which path to take.
/// </summary>
public record EstimatorResult(
    int                   StartCqp,
    int?                  StartScale,
    int?                  QualityCqp,
    int?                  SizeCqp,
    EstimatorConfidence   Confidence,
    CompressionStrategy   Strategy,
    string                Reason);

public static class Estimator
{
    // Limit is considered non-binding when the quality-floor CQP is at least
    // this many points HIGHER than the size-fit CQP. Higher CQP = more
    // compression, so quality-floor CQP > size-fit CQP means hitting the
    // quality floor naturally produces a file under the limit.
    private const int QualityBeatsSizeMargin = 2;

    // ...and considered way-over-budget when the size-fit CQP is much higher
    // than the quality-floor CQP — meaning we'd have to compress past the
    // quality floor to fit. At that point dropping a resolution step is
    // strictly better than burning passes at the current res.
    private const int SizeFarBelowQualityMargin = 4;

    public static EstimatorResult Plan(
        ComplexityProfile? profile,
        VideoInfo info,
        long?              sizeLimit,
        string             encoder,
        int                forcedRes,
        AppConfig          config)
    {
        int defaultCqp = config.GetDefaultCqp(encoder);
        int maxCqp     = config.GetMaxCqp(encoder, info.Complexity);
        double floor   = config.GetSsimFloor(info.Complexity);

        int? startScale = ResolveStartScale(info.Height, forcedRes, info.Complexity, config);

        // Without a complexity profile we can't query the cache. Fall back to
        // defaults and let the per-path logic figure it out from scratch.
        if (profile == null || profile.MeanComplexity <= 0f)
        {
            var strategy = sizeLimit.HasValue
                ? CompressionStrategy.SizeConstrained
                : CompressionStrategy.QualityFirst;
            return new EstimatorResult(
                StartCqp:   defaultCqp,
                StartScale: startScale,
                QualityCqp: null,
                SizeCqp:    null,
                Confidence: EstimatorConfidence.None,
                Strategy:   strategy,
                Reason:     "No complexity profile — using encoder defaults.");
        }

        // Two predictions:
        //  - Quality CQP: highest CQP that should still meet the SSIM floor.
        //  - Size CQP:    lowest CQP that should still fit the size limit.
        // The relationship between them is the whole basis for the path split.
        var qualityHit = CqpCache.LookupSimilar(
            profile, encoder, targetBytes: null,
            scaleHeight: startScale, info.DurationS, targetSsim: floor);

        (int Cqp, float Similarity)? sizeHit = null;
        if (sizeLimit.HasValue)
            sizeHit = CqpCache.LookupSimilar(
                profile, encoder, targetBytes: sizeLimit.Value,
                scaleHeight: startScale, info.DurationS);

        int?  qualityCqp = qualityHit.HasValue ? Math.Clamp(qualityHit.Value.Cqp, defaultCqp, maxCqp) : (int?)null;
        int?  sizeCqp    = sizeHit.HasValue    ? Math.Clamp(sizeHit.Value.Cqp,    defaultCqp, maxCqp) : (int?)null;
        float bestSim    = MathF.Max(
            qualityHit?.Similarity ?? 0f,
            sizeHit?.Similarity    ?? 0f);
        var   confidence = ConfidenceFromSimilarity(bestSim);

        // ── Strategy decision ───────────────────────────────────────────────
        // No limit: trivially QualityFirst.
        if (!sizeLimit.HasValue)
        {
            int startCqp = qualityCqp ?? defaultCqp;
            return new EstimatorResult(
                StartCqp:   startCqp,
                StartScale: startScale,
                QualityCqp: qualityCqp,
                SizeCqp:    null,
                Confidence: confidence,
                Strategy:   CompressionStrategy.QualityFirst,
                Reason:     qualityCqp.HasValue
                    ? $"No size limit — squeezing to quality floor (predicted CQP {qualityCqp})."
                    : "No size limit, no cache match — squeezing from default CQP.");
        }

        // Low confidence with a binding limit: don't risk QualityFirst, let the
        // binary search handle it. The limit is the hard constraint, quality
        // is the soft preference.
        if (confidence == EstimatorConfidence.None || confidence == EstimatorConfidence.Low)
        {
            int startCqp = sizeCqp ?? qualityCqp ?? defaultCqp;
            return new EstimatorResult(
                StartCqp:   startCqp,
                StartScale: startScale,
                QualityCqp: qualityCqp,
                SizeCqp:    sizeCqp,
                Confidence: confidence,
                Strategy:   CompressionStrategy.SizeConstrained,
                Reason:     "Low cache confidence — binary search to be safe.");
        }

        // Both predictions present → compare. Higher CQP = more compression.
        if (qualityCqp.HasValue && sizeCqp.HasValue)
        {
            int diff = qualityCqp.Value - sizeCqp.Value;

            if (diff >= QualityBeatsSizeMargin)
            {
                // Hitting the quality floor produces a file COMFORTABLY under
                // the limit — limit is non-binding, just squeeze for quality.
                return new EstimatorResult(
                    StartCqp:   qualityCqp.Value,
                    StartScale: startScale,
                    QualityCqp: qualityCqp,
                    SizeCqp:    sizeCqp,
                    Confidence: confidence,
                    Strategy:   CompressionStrategy.QualityFirst,
                    Reason:     $"Limit non-binding (quality CQP {qualityCqp} > size CQP {sizeCqp}+{QualityBeatsSizeMargin}).");
            }

            if (diff <= -SizeFarBelowQualityMargin)
            {
                // Size limit demands FAR more compression than quality allows —
                // we can't hit quality at this resolution. Drop one ladder
                // step up front instead of binary-searching to a fail.
                int? lowerScale = NextLowerLadderStep(startScale, info.Height, config);
                return new EstimatorResult(
                    StartCqp:   sizeCqp.Value,
                    StartScale: lowerScale ?? startScale,
                    QualityCqp: qualityCqp,
                    SizeCqp:    sizeCqp,
                    Confidence: confidence,
                    Strategy:   CompressionStrategy.SizeConstrained,
                    Reason:     lowerScale.HasValue
                        ? $"Limit forces CQP {sizeCqp} but quality wants {qualityCqp} — starting one res lower ({lowerScale}p)."
                        : $"Limit forces CQP {sizeCqp} but quality wants {qualityCqp} — already at minimum resolution.");
            }

            // Limit is binding, current res is reasonable — let the binary
            // search find the exact boundary from the size-fit prediction.
            return new EstimatorResult(
                StartCqp:   sizeCqp.Value,
                StartScale: startScale,
                QualityCqp: qualityCqp,
                SizeCqp:    sizeCqp,
                Confidence: confidence,
                Strategy:   CompressionStrategy.SizeConstrained,
                Reason:     $"Limit binding (size CQP {sizeCqp} near quality CQP {qualityCqp}).");
        }

        // One prediction missing but a limit exists — go SizeConstrained off
        // whichever number we have.
        {
            int startCqp = sizeCqp ?? qualityCqp ?? defaultCqp;
            return new EstimatorResult(
                StartCqp:   startCqp,
                StartScale: startScale,
                QualityCqp: qualityCqp,
                SizeCqp:    sizeCqp,
                Confidence: confidence,
                Strategy:   CompressionStrategy.SizeConstrained,
                Reason:     "Partial cache match — binary searching to verify.");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Initial resolution: forced override wins; otherwise native, dropping
    // one step only for Complex content above 1080p (matches the wizard's
    // recommended logic).
    private static int? ResolveStartScale(
        int srcHeight, int forcedRes, string complexity, AppConfig config)
    {
        if (forcedRes > 0)
            return forcedRes >= srcHeight ? null : forcedRes;

        if (complexity == "Complex" && srcHeight >= 1080)
        {
            int? dropTo = config.ResolutionSteps.FirstOrDefault(h => h < srcHeight);
            return dropTo == 0 ? null : dropTo;
        }
        return null;
    }

    private static int? NextLowerLadderStep(int? currentScale, int srcHeight, AppConfig config)
    {
        int effective = currentScale ?? srcHeight;
        foreach (var h in config.ResolutionSteps)
            if (h < effective) return h;
        return null;
    }

    private static EstimatorConfidence ConfidenceFromSimilarity(float sim) => sim switch
    {
        >= 0.95f => EstimatorConfidence.High,
        >= 0.88f => EstimatorConfidence.Medium,
        >  0.0f  => EstimatorConfidence.Low,
        _        => EstimatorConfidence.None,
    };
}
