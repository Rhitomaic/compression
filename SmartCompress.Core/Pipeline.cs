using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Spectre.Console;

namespace SmartCompress.Core;

public record CompressResult(bool Success, double? Ssim, int? ScaleUsed);

public static class Pipeline
{
    // ── Cancellation support ──────────────────────────────────────────────────
    // Tracked so Ctrl+C can kill any in-flight ffmpeg children gracefully
    // instead of `Environment.Exit`-ing and leaving orphan tmp files behind.

    private static readonly ConcurrentDictionary<int, Process> ActiveProcesses = new();

    /// <summary>
    /// The output folder for the current session. Used by `CancelAll` to sweep
    /// orphaned `_sc_tmp_*.mp4` files on cancellation. Set by `Program.RunSession`.
    /// </summary>
    public static string? SessionTmpDir { get; set; }

    /// <summary>
    /// Kill any ffmpeg child processes we spawned and clean tmp files in the
    /// current session's output folder. Safe to call from the Ctrl+C handler.
    /// </summary>
    public static void CancelAll()
    {
        foreach (var proc in ActiveProcesses.Values)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { /* already dead, no permission, etc. — best effort */ }
        }
        ActiveProcesses.Clear();

        if (SessionTmpDir != null && Directory.Exists(SessionTmpDir))
        {
            try
            {
                foreach (var f in Directory.GetFiles(SessionTmpDir, "_sc_tmp_*.mp4"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }

    /// <summary>
    /// Sweep orphaned tmp files left by a previous hard-crash. Called at the
    /// start of each session once the output folder is known.
    /// </summary>
    public static void SweepStaleTmps(string outputDir)
    {
        if (!Directory.Exists(outputDir)) return;
        try
        {
            foreach (var f in Directory.GetFiles(outputDir, "_sc_tmp_*.mp4"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    // ── Single ffmpeg encode with live progress bar ───────────────────────────

    public static bool RunEncode(
        string ffmpegPath, string src, string dst,
        string encoder, int cqp,
        int? scaleHeight = null, double durationS = 0)
    {
        var args = BuildEncodeArgs(src, dst, encoder, cqp, scaleHeight);
        using var proc = StartProcess(ffmpegPath, args);

        var stderrLines = new List<string>();
        var stderrTask  = Task.Run(() =>
        {
            string? l;
            while ((l = proc.StandardError.ReadLine()) != null)
                stderrLines.Add(l);
        });

        AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns([new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new RemainingTimeColumn()])
            .Start(ctx =>
            {
                var task = ctx.AddTask("    [cyan]Encoding[/]", maxValue: 100.0);
                if (durationS <= 0) task.IsIndeterminate = true;

                string? line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    if (line[..eq] == "out_time_us" && durationS > 0 &&
                        long.TryParse(line[(eq + 1)..], out var us) && us >= 0)
                    {
                        task.Value          = Math.Min(us / (durationS * 1_000_000) * 100.0, 100.0);
                        task.IsIndeterminate = false;
                    }
                }
                task.Value           = 100.0;
                task.IsIndeterminate = false;
            });

        proc.WaitForExit();
        stderrTask.Wait();

        if (proc.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("\n[bold red]  [[ERROR]] ffmpeg failed:[/]");
            foreach (var l in stderrLines.TakeLast(20))
                AnsiConsole.MarkupLine($"  [red]{Markup.Escape(l)}[/]");
            return false;
        }
        return true;
    }

    // ── SSIM quality measurement with live progress bar ───────────────────────

    public static double? CalcSsim(
        string ffmpegPath, string src, string compressed,
        int? matchHeight = null, double durationS = 0)
    {
        string lavfi = matchHeight.HasValue
            ? $"[0:v]scale=-2:{matchHeight}:flags=lanczos[ref];[ref][1:v]ssim"
            : "[0:v][1:v]ssim";

        string[] args =
        [
            "-y", "-progress", "pipe:1", "-nostats",
            "-i", src, "-i", compressed,
            "-lavfi", lavfi,
            "-f", "null", "-"
        ];

        using var proc = StartProcess(ffmpegPath, args);

        double? ssimVal = null;
        var stderrTask = Task.Run(() =>
        {
            string? line;
            while ((line = proc.StandardError.ReadLine()) != null)
            {
                int idx = line.IndexOf("All:", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var token = line[(idx + 4)..].TrimStart().Split(' ')[0];
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        ssimVal = v;
                }
            }
        });

        AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns([new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new RemainingTimeColumn()])
            .Start(ctx =>
            {
                var task = ctx.AddTask("    [blue]Quality check[/]", maxValue: 100.0);
                if (durationS <= 0) task.IsIndeterminate = true;

                string? line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    if (line[..eq] == "out_time_us" && durationS > 0 &&
                        long.TryParse(line[(eq + 1)..], out var us) && us >= 0)
                    {
                        task.Value           = Math.Min(us / (durationS * 1_000_000) * 100.0, 100.0);
                        task.IsIndeterminate = false;
                    }
                }
                task.Value           = 100.0;
                task.IsIndeterminate = false;
            });

        proc.WaitForExit();
        stderrTask.Wait();
        return ssimVal;
    }

    // ── CQP binary search: hit a size target ─────────────────────────────────

    public static (bool Success, int? BestCqp) CompressToTarget(
        string ffmpegPath, string src, string dst,
        string encoder, int defaultCqp, long? sizeLimit,
        int? scaleHeight, int srcBitrateKbps, double durationS,
        string complexity, AppConfig config, Logger log,
        ComplexityProfile? profile = null,
        bool isBenchmark = false)
    {
        if (sizeLimit is null)
        {
            var resStr = scaleHeight.HasValue ? $" at {scaleHeight}p" : "";
            AnsiConsole.MarkupLine($"  CQP [yellow]{defaultCqp}[/]{Markup.Escape(resStr)}  [dim](no size limit)[/]");
            log.Write($"  CQP {defaultCqp}{resStr}  (no size limit)");
            Console.WriteLine();
            bool ok = RunEncode(ffmpegPath, src, dst, encoder, defaultCqp, scaleHeight, durationS);
            return (ok, ok ? defaultCqp : null);
        }

        int lo = defaultCqp;
        int hi = config.GetMaxCqp(encoder, complexity);
        string? bestPath = null;
        int?    bestCqp  = null;

        var cacheKey = CqpCache.MakeKey(src, durationS, encoder, sizeLimit, scaleHeight);

        var resStr2 = scaleHeight.HasValue ? $" at {scaleHeight}p" : "";
        AnsiConsole.MarkupLine(
            $"  Target: [bold white]{Markup.Escape(config.FormatMb(sizeLimit.Value))}[/]  " +
            $"[dim]|[/]  CQP [yellow]{lo}[/][dim]-[/][yellow]{hi}[/]{Markup.Escape(resStr2)}");
        log.Write($"  Target: {config.FormatMb(sizeLimit.Value)}  |  CQP {lo}-{hi}{resStr2}");
        Console.WriteLine();

        // Fast path: if we've compressed this exact file+settings before, re-use the CQP directly.
        int? cachedCqp = CqpCache.Lookup(cacheKey);
        if (cachedCqp.HasValue)
        {
            int cqp = Math.Clamp(cachedCqp.Value, lo, hi);
            AnsiConsole.MarkupLine($"  Pass [bold]1[/]  CQP [yellow]{cqp,2}[/]  [dim](cached)[/]");
            log.Write($"  Pass 1  CQP {cqp,2}  (cached)");

            var tmp  = TmpPath(dst, cqp);
            if (RunEncode(ffmpegPath, src, tmp, encoder, cqp, scaleHeight, durationS))
            {
                long size = new FileInfo(tmp).Length;
                if (size <= sizeLimit.Value)
                {
                    AnsiConsole.MarkupLine($"    {Markup.Escape(config.FormatMb(size))}  [green]OK fits[/]");
                    log.Write($"    {config.FormatMb(size)}  OK fits");
                    File.Move(tmp, dst, overwrite: true);
                    CqpCache.Save(cacheKey, cqp, size, encoder, sizeLimit, scaleHeight,
                        (int)Math.Round(durationS), profile, isBenchmark);
                    return (true, cqp);
                }
                // Cache stale (e.g. encoder update changed output size) — fall through to full search.
                AnsiConsole.MarkupLine($"    {Markup.Escape(config.FormatMb(size))}  [yellow]cache stale — searching[/]");
                log.Write($"    {config.FormatMb(size)}  cache stale — full search");
                File.Delete(tmp);
            }
        }

        // Profile similarity fallback: find the closest known profile for the same
        // encoder/limit/scale. If similarity is high enough, treat it like a soft cache hit.
        int? firstCqp        = null;
        int? predictedRawCqp = null;  // unclamped prediction — for residual tracking
        if (profile != null)
        {
            var similar = CqpCache.LookupSimilar(profile, encoder, sizeLimit, scaleHeight, durationS);
            if (similar.HasValue)
            {
                predictedRawCqp = similar.Value.Cqp;
                int cqp = Math.Clamp(similar.Value.Cqp, lo, hi);
                int pct = (int)(similar.Value.Similarity * 100);
                AnsiConsole.MarkupLine(
                    $"  [dim]Similar clip found ({pct}% match) — starting at CQP [yellow]{cqp}[/][/]");
                log.Write($"  Similar clip found ({pct}% match) — starting at CQP {cqp}");
                firstCqp = cqp;
            }
        }

        // Bitrate-math estimate: only kick in when similarity lookup found nothing,
        // and only when the estimate lands strictly inside the range (clamping wastes a pass).
        if (!firstCqp.HasValue && srcBitrateKbps > 0 && durationS > 0)
        {
            double targetKbps = sizeLimit.Value * 8.0 / (durationS * 1000.0);
            if (targetKbps > 0 && targetKbps < srcBitrateKbps)
            {
                double ratio = srcBitrateKbps / targetKbps;
                int est = defaultCqp + (int)Math.Round(Math.Log2(ratio) * 6.0);
                if (est > lo && est < hi)
                    firstCqp = est;
            }
        }

        long? prevSize = null;
        int?  prevCqp  = null;
        long  bestSize = 0;
        double encoderK = EncoderSlope.For(encoder);

        // Skip a planned pass if our two-point local fit predicts it will land
        // more than this fraction over the limit. 3% buffer covers prediction noise.
        const double SkipTolerance = 1.03;

        // Each (cqp, size) pair from this run. Powers the local slope fit so we
        // can predict the size of the NEXT planned CQP before encoding it.
        var passHistory = new List<(int Cqp, long Size)>();

        for (int attempt = 1; attempt <= 8; attempt++)
        {
            int cqp;
            if (attempt == 1 && firstCqp.HasValue)
            {
                cqp = firstCqp.Value;
            }
            else if (prevSize.HasValue && prevCqp.HasValue)
            {
                // Size-guided next step: log2(target/measured) × k gives the CQP delta,
                // where k is this encoder's calibrated rate-distortion slope.
                // If the file fits, delta > 0 → subtract → go lower (better quality).
                // If too big,    delta < 0 → subtract → go higher (more compression).
                double ratio = (double)sizeLimit.Value / prevSize.Value;
                int    delta = (int)Math.Round(Math.Log2(ratio) * encoderK);
                if (delta == 0) delta = ratio >= 1.0 ? 1 : -1; // always move at least 1 step
                int    est   = prevCqp.Value - delta;
                cqp = (est >= lo && est <= hi) ? est : (lo + hi) / 2;
            }
            else
            {
                cqp = (lo + hi) / 2;
            }

            // Pre-flight check: when about to go LOWER than current best (chasing better
            // quality), use the run-local slope to predict size. If we're confident it
            // will bust the cap, skip the encode entirely — same result without the wait.
            if (bestPath != null && bestCqp.HasValue && bestSize > 0 &&
                cqp < bestCqp.Value && passHistory.Count >= 1)
            {
                double localK   = LocalSlopeFromHistory(passHistory) ?? encoderK;
                double predicted = bestSize * Math.Pow(2, (bestCqp.Value - cqp) / localK);
                if (predicted > sizeLimit.Value * SkipTolerance)
                {
                    var predStr = Markup.Escape(config.FormatMb((long)predicted));
                    AnsiConsole.MarkupLine(
                        $"  Pass [bold]{attempt}[/]  CQP [yellow]{cqp,2}[/]  " +
                        $"[dim](skipped — predicted {predStr}, over cap)[/]");
                    log.Write($"  Pass {attempt}  CQP {cqp,2}  skipped (predicted {config.FormatMb((long)predicted)} > limit)");
                    break;
                }
            }

            var tmp = TmpPath(dst, cqp);

            AnsiConsole.MarkupLine($"  Pass [bold]{attempt}[/]  CQP [yellow]{cqp,2}[/]");
            log.Write($"  Pass {attempt}  CQP {cqp,2}");

            if (!RunEncode(ffmpegPath, src, tmp, encoder, cqp, scaleHeight, durationS))
                return (false, null);

            long size = new FileInfo(tmp).Length;
            bool fits = size <= sizeLimit.Value;
            var sizeStr = Markup.Escape(config.FormatMb(size));
            AnsiConsole.MarkupLine(fits
                ? $"    {sizeStr}  [green]OK fits[/]"
                : $"    {sizeStr}  [yellow]too big[/]");
            log.Write($"    {config.FormatMb(size)}  {(fits ? "OK fits" : "X  too big")}");

            prevCqp  = cqp;
            prevSize = size;
            passHistory.Add((cqp, size));

            if (fits) { if (bestPath != null && File.Exists(bestPath)) File.Delete(bestPath); bestPath = tmp; bestCqp = cqp; bestSize = size; hi = cqp - 1; }
            else       { File.Delete(tmp); lo = cqp + 1; }

            if (lo > hi) break;
        }

        if (bestPath != null && bestCqp.HasValue)
        {
            File.Move(bestPath, dst, overwrite: true);
            CqpCache.Save(cacheKey, bestCqp.Value, bestSize, encoder, sizeLimit, scaleHeight,
                (int)Math.Round(durationS), profile, isBenchmark);

            // Self-calibration feedback. Only from real user encodes — benchmark
            // runs would create a loop where bias correction skews benchmark data
            // which then feeds back into the bias. We want the bias to track
            // real-world divergence only.
            if (!isBenchmark && predictedRawCqp.HasValue)
                PredictorCalibration.RecordResidual(encoder, predictedRawCqp.Value, bestCqp.Value);

            return (true, bestCqp);
        }
        return (false, null);
    }

    // ── Quality-first path: hit the SSIM floor, minimal passes ────────────────
    //
    // Replaces the binary-search hunt-for-exact-boundary behaviour of
    // CompressNoLimit when the Estimator says the size limit is non-binding
    // (or absent). The goal here is floor compliance, not bit-precise minimum.
    //
    // Accept window: SSIM in [floor, floor + AcceptBand] = done in one pass.
    // Above the band:  one CQP+2 attempt to squeeze, accept the higher of the
    //                  two that still meets floor.
    // Below the floor: drop CQP by 2, then 4, capped at 3 total passes.

    public static (bool Success, double? Ssim) CompressQualityFirst(
        string ffmpegPath, string src, string dst,
        string encoder, int startCqp, double ssimFloor,
        int? scaleHeight, double durationS, AppConfig config, Logger log,
        ComplexityProfile? profile = null)
    {
        // User-picked medium acceptance band (2026-05-15).
        const double AcceptBand = 0.010;
        const double SqueezeBand = 0.020;
        const int    MaxPasses   = 3;

        int loBound = config.GetDefaultCqp(encoder);
        int hiBound = config.GetMaxCqp(encoder);
        int cqp     = Math.Clamp(startCqp, loBound, hiBound);

        AnsiConsole.MarkupLine(
            $"  Quality-first: SSIM floor [cyan]{ssimFloor}[/]  [dim]|[/]  starting at CQP [yellow]{cqp}[/]");
        log.Write($"  Quality-first: SSIM floor {ssimFloor}  |  starting at CQP {cqp}");
        Console.WriteLine();

        (int Cqp, string FilePath, double Ssim, long Size)? best = null;
        var cacheKey = CqpCache.MakeKey(src, durationS, encoder, null, scaleHeight);

        for (int attempt = 1; attempt <= MaxPasses; attempt++)
        {
            var tmp = TmpPath(dst, cqp);

            AnsiConsole.MarkupLine($"  Pass [bold]{attempt}[/]  CQP [yellow]{cqp,2}[/]");
            log.Write($"  Pass {attempt}  CQP {cqp,2}");

            if (!RunEncode(ffmpegPath, src, tmp, encoder, cqp, scaleHeight, durationS))
                return (false, null);

            long size = new FileInfo(tmp).Length;
            var  ssim = CalcSsim(ffmpegPath, src, tmp, scaleHeight, durationS);

            if (ssim is null)
            {
                // No SSIM available — take this result and move on. Better than
                // looping when we can't measure progress.
                AnsiConsole.MarkupLine($"    {Markup.Escape(config.FormatMb(size))}  [dim]SSIM unavailable - accepting[/]");
                log.Write($"    {config.FormatMb(size)}  SSIM unavailable - accepting");
                if (best.HasValue && File.Exists(best.Value.FilePath)) File.Delete(best.Value.FilePath);
                File.Move(tmp, dst, overwrite: true);
                return (true, null);
            }

            var ssimColor = SsimColor(ssim.Value);
            double margin = ssim.Value - ssimFloor;

            // Branch on where we landed relative to the floor.
            if (ssim.Value >= ssimFloor)
            {
                // Promote this to best (it meets floor).
                if (best.HasValue && File.Exists(best.Value.FilePath)) File.Delete(best.Value.FilePath);
                best = (cqp, tmp, ssim.Value, size);

                if (margin <= AcceptBand)
                {
                    AnsiConsole.MarkupLine(
                        $"    {Markup.Escape(config.FormatMb(size))}  SSIM [{ssimColor}]{ssim:F4}[/]  " +
                        $"[green]OK[/]  [dim](in accept band)[/]");
                    log.Write($"    {config.FormatMb(size)}  SSIM {ssim:F4}  OK (in accept band)");
                    break;
                }

                if (margin <= SqueezeBand || attempt >= MaxPasses)
                {
                    AnsiConsole.MarkupLine(
                        $"    {Markup.Escape(config.FormatMb(size))}  SSIM [{ssimColor}]{ssim:F4}[/]  [green]OK[/]");
                    log.Write($"    {config.FormatMb(size)}  SSIM {ssim:F4}  OK");
                    break;
                }

                // Room to compress more — one attempt at CQP+2.
                AnsiConsole.MarkupLine(
                    $"    {Markup.Escape(config.FormatMb(size))}  SSIM [{ssimColor}]{ssim:F4}[/]  " +
                    $"[green]OK[/]  [dim](trying CQP+2 to squeeze)[/]");
                log.Write($"    {config.FormatMb(size)}  SSIM {ssim:F4}  OK (trying CQP+2)");
                int next = Math.Min(cqp + 2, hiBound);
                if (next == cqp) break;
                cqp = next;
                continue;
            }

            // Below floor — first failure, drop by 2; second failure, drop by 4.
            AnsiConsole.MarkupLine(
                $"    {Markup.Escape(config.FormatMb(size))}  SSIM [{ssimColor}]{ssim:F4}[/]  " +
                $"[yellow]below floor[/]");
            log.Write($"    {config.FormatMb(size)}  SSIM {ssim:F4}  below floor");
            if (File.Exists(tmp)) File.Delete(tmp);

            if (attempt >= MaxPasses) break;
            int step  = attempt == 1 ? 2 : 4;
            int lower = Math.Max(cqp - step, loBound);
            if (lower == cqp) break;
            cqp = lower;
        }

        if (best.HasValue)
        {
            File.Move(best.Value.FilePath, dst, overwrite: true);
            CqpCache.Save(cacheKey, best.Value.Cqp, best.Value.Size, encoder, null, scaleHeight,
                (int)Math.Round(durationS), profile, ssim: best.Value.Ssim);
            return (true, best.Value.Ssim);
        }

        // Never met the floor within pass budget — fall back to default CQP
        // so the caller still gets a file out the door.
        AnsiConsole.MarkupLine("[dim]  Could not meet floor in pass budget — encoding at default CQP.[/]");
        log.Write("  Could not meet floor in pass budget — encoding at default CQP.");
        bool ok = RunEncode(ffmpegPath, src, dst, encoder, loBound, scaleHeight, durationS);
        return (ok, null);
    }

    // ── Full smart pipeline (resolution ladder + SSIM floor) ─────────────────

    public static CompressResult CompressSmart(
        string ffmpegPath, string src, string dst,
        string encoder, int defaultCqp, long? sizeLimit,
        int srcHeight, VideoInfo info, int forcedRes,
        AppConfig config, Logger log,
        ComplexityProfile? profile = null)
    {
        string complexity = info.Complexity;
        double floor      = config.GetSsimFloor(complexity);
        var    steps      = config.ResolutionSteps;
        double durationS  = info.DurationS;
        int    minHeight  = config.GetMinOutputHeight(srcHeight);

        // Up-front plan: which path to take and where to start.
        var plan = Estimator.Plan(profile, info, sizeLimit, encoder, forcedRes, config);
        PrintEstimatorPlan(plan, log);

        var complexityColor = complexity switch { "Simple" => "green", "Complex" => "red", _ => "yellow" };
        AnsiConsole.MarkupLine(
            $"  Quality floor:  SSIM [cyan]{floor}[/]  " +
            $"([{complexityColor}]{Markup.Escape(complexity)}[/] content)");
        log.Write($"  Quality floor:  SSIM {floor}  ({complexity} content)");

        if (sizeLimit.HasValue && forcedRes == 0)
        {
            AnsiConsole.MarkupLine(
                $"  Resolution cap: [dim]{minHeight}p minimum[/]  [dim](source: {srcHeight}p)[/]");
            log.Write($"  Resolution cap: {minHeight}p minimum  (source: {srcHeight}p)");
        }
        Console.WriteLine();

        // ── QualityFirst path ───────────────────────────────────────────────
        // Estimator says limit is non-binding (or absent). Hit the floor in
        // 1-3 passes instead of binary-searching the exact-highest CQP.
        if (plan.Strategy == CompressionStrategy.QualityFirst)
        {
            var scale = plan.StartScale;
            var label = scale is null ? $"{srcHeight}p (original)" : $"{scale}p";
            AnsiConsole.Write(new Rule($"[dim] Resolution: {Markup.Escape(label)} [/]").RuleStyle("grey dim"));
            Console.WriteLine();
            log.Write($"\n--- Resolution: {label} ---");
            var (success, ssim) = CompressQualityFirst(
                ffmpegPath, src, dst, encoder, plan.StartCqp, floor,
                scale, durationS, config, log, profile);
            return new CompressResult(success, ssim, scale);
        }

        // ── SizeConstrained path ────────────────────────────────────────────
        // Build the ladder, but start from plan.StartScale (the Estimator may
        // have already dropped one step when it knew the original res was
        // hopeless). Higher-than-start steps are skipped.
        List<int?> ladder;
        if (forcedRes == 0)
        {
            int startHeight = plan.StartScale ?? srcHeight;
            var below = steps.Where(h => h < startHeight && h >= minHeight).Select(h => (int?)h);
            ladder = (plan.StartScale.HasValue
                ? new List<int?> { plan.StartScale }
                : new List<int?> { null })
                .Concat(below)
                .Distinct()
                .ToList();
        }
        else
        {
            int? scale = forcedRes >= srcHeight ? null : (int?)forcedRes;
            ladder = [scale];
        }

        foreach (var res in ladder)
        {
            var label = res is null ? $"{srcHeight}p (original)" : $"{res}p";
            AnsiConsole.Write(new Rule($"[dim] Resolution: {Markup.Escape(label)} [/]").RuleStyle("grey dim"));
            Console.WriteLine();
            log.Write($"\n--- Resolution: {label} ---\n");

            var (success, _) = CompressToTarget(
                ffmpegPath, src, dst, encoder, defaultCqp, sizeLimit,
                res, info.BitrateKbps, durationS, complexity, config, log, profile);

            if (!success)
            {
                if (ladder.Count == 1)
                {
                    AnsiConsole.MarkupLine($"  [red]Could not hit target at {Markup.Escape(label)}.[/]");
                    AnsiConsole.MarkupLine("  [dim]Try a lower resolution or a larger size limit.[/]");
                    log.Write($"  Could not hit target at {label}.");
                }
                else if (res == ladder[^1])
                {
                    AnsiConsole.MarkupLine($"  [red]Could not hit target at {Markup.Escape(label)} (minimum resolution reached).[/]");
                    log.Write($"  Could not hit target at {label} (minimum resolution reached).");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [yellow]Could not hit target at {Markup.Escape(label)} - dropping to lower resolution...[/]");
                    log.Write($"  Could not hit target at {label} - dropping to lower resolution...");
                }
                continue;
            }

            Console.WriteLine();
            var ssim = CalcSsim(ffmpegPath, src, dst, res, durationS);

            if (ssim is null)
            {
                AnsiConsole.MarkupLine("[dim]  SSIM unavailable - accepting result.[/]");
                log.Write("  SSIM unavailable - accepting result.");
                return new CompressResult(true, null, res);
            }

            var ssimColor = SsimColor(ssim.Value);
            AnsiConsole.MarkupLine(
                $"  SSIM [{ssimColor}]{ssim:F4}[/]  [dim]—[/]  {Markup.Escape(config.GetSsimLabel(ssim.Value))}  " +
                $"[dim](floor: {floor}  [[{Markup.Escape(complexity)}]])[/]");
            log.Write($"  SSIM {ssim:F4}  -  {config.GetSsimLabel(ssim.Value)}  (floor: {floor}  [{complexity}])");

            if (ssim.Value >= floor)
                return new CompressResult(true, ssim, res);

            if (ladder.Count == 1)
            {
                AnsiConsole.MarkupLine($"  [dim]Quality below {Markup.Escape(complexity)} floor - resolution was manually set, keeping result.[/]");
                log.Write($"  Quality below {complexity} floor - but resolution was manually set, keeping result.");
                return new CompressResult(true, ssim, res);
            }

            AnsiConsole.MarkupLine(
                $"  [yellow]Quality below {Markup.Escape(complexity)} floor ({floor}) — dropping to lower resolution...[/]");
            log.Write($"  Quality below {complexity} floor ({floor}) - dropping to lower resolution...");
            if (File.Exists(dst)) File.Delete(dst);
        }

        return new CompressResult(false, null, null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Two-point slope fit on the most recent passes of THIS encode. Tracks
    // how this specific clip behaves around the operating CQP — usually
    // tighter than the encoder's general calibrated k because it captures
    // content-specific quirks.
    private static double? LocalSlopeFromHistory(List<(int Cqp, long Size)> history)
    {
        if (history.Count < 2) return null;
        var a = history[^2];
        var b = history[^1];
        if (a.Cqp == b.Cqp || a.Size <= 0 || b.Size <= 0) return null;

        // size = K · 2^(-cqp/k)  →  k = (cqp_b - cqp_a) / log2(size_a / size_b)
        double logR = Math.Log2((double)a.Size / b.Size);
        if (Math.Abs(logR) < 0.01) return null;  // too noisy / no real change
        double k = (b.Cqp - a.Cqp) / logR;
        if (k < 4 || k > 12) return null;        // sanity clamp; falls back to encoder k
        return k;
    }

    private static void PrintEstimatorPlan(EstimatorResult plan, Logger log)
    {
        var stratColor = plan.Strategy == CompressionStrategy.QualityFirst ? "green" : "yellow";
        var confColor  = plan.Confidence switch
        {
            EstimatorConfidence.High   => "green",
            EstimatorConfidence.Medium => "yellow",
            EstimatorConfidence.Low    => "darkorange3",
            _                          => "red",
        };

        AnsiConsole.MarkupLine(
            $"  [dim]Plan:[/] [{stratColor}]{plan.Strategy}[/]  " +
            $"[dim](conf:[/] [{confColor}]{plan.Confidence}[/][dim])[/]");
        AnsiConsole.MarkupLine($"  [dim]→ {Markup.Escape(plan.Reason)}[/]");

        var startStr = plan.StartScale.HasValue ? $"{plan.StartScale}p" : "native";
        var details  = $"start CQP {plan.StartCqp} @ {startStr}";
        if (plan.QualityCqp.HasValue) details += $", quality CQP {plan.QualityCqp}";
        if (plan.SizeCqp.HasValue)    details += $", size CQP {plan.SizeCqp}";
        AnsiConsole.MarkupLine($"  [dim]   {Markup.Escape(details)}[/]");

        log.Write($"  Plan: {plan.Strategy} (conf: {plan.Confidence}) — {plan.Reason}");
        log.Write($"    {details}");
    }

    private static string SsimColor(double ssim) =>
        ssim >= 0.97 ? "green" : ssim >= 0.93 ? "yellow" : ssim >= 0.87 ? "darkorange3" : "red";

    private static string TmpPath(string dst, int cqp)
    {
        // Include the destination stem so two concurrent batches writing into
        // the same folder don't stomp each other's tmp files.
        var stem = Path.GetFileNameWithoutExtension(dst);
        var dir  = Path.GetDirectoryName(dst) ?? ".";
        return Path.Combine(dir, $"_sc_tmp_{stem}_{cqp}.mp4");
    }

    private static List<string> BuildEncodeArgs(
        string src, string dst, string encoder, int cqp, int? scaleHeight)
    {
        var args = new List<string>
        {
            "-y", "-progress", "pipe:1", "-nostats", "-loglevel", "error",
            "-i", src, "-c:v", encoder,
        };

        if (encoder.Contains("nvenc"))
            args.AddRange(["-rc", "vbr", "-cq", cqp.ToString(), "-b:v", "0"]);
        else if (encoder.Contains("amf"))
            args.AddRange(["-rc", "cqp",
                "-qp_i", cqp.ToString(), "-qp_p", cqp.ToString(), "-qp_b", cqp.ToString()]);
        else if (encoder.Contains("qsv"))
            args.AddRange(["-global_quality", cqp.ToString(), "-look_ahead", "1"]);
        else
            args.AddRange(["-crf", cqp.ToString()]);

        if (scaleHeight.HasValue)
            args.AddRange(["-vf", $"scale=-2:{scaleHeight}:flags=lanczos"]);

        args.AddRange(["-c:a", "aac", "-b:a", "128k", dst]);
        return args;
    }

    private static Process StartProcess(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        ActiveProcesses[proc.Id] = proc;
        // Auto-unregister once the process exits naturally. CancelAll handles the
        // forced-kill path; this covers the success path.
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => ActiveProcesses.TryRemove(proc.Id, out _);
        return proc;
    }
}
