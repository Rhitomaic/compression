using Spectre.Console;

namespace SmartCompress.Core;

public enum BenchmarkMode { Fast, Accurate }

public record BenchmarkClip(string Path, VideoInfo Info, ComplexityProfile Profile);

public record BenchmarkSummary(int Total, int Succeeded, int Failed, TimeSpan Elapsed);

public static class Benchmark
{
    // Resolutions to test relative to source. Native resolution is always included.
    // Anything above source is skipped (upscaling is pointless for benchmark data).
    private static readonly int[] ScaleLadder = [1080, 720, 540];

    // Size targets used by Accurate mode (mirror real preset sizes).
    private static readonly long[] AccurateSizeTargetsBytes =
    [
        10L * 1024 * 1024,   // 10 MB (Discord Free)
        25L * 1024 * 1024,   // 25 MB (Discord Nitro Basic)
        100L * 1024 * 1024,  // 100 MB (typical custom cap)
    ];

    // CQPs to sweep in Fast mode (count, not values — values are derived from
    // each encoder's default..max range per config.json so they stay sensible).
    private const int FastCqpCount = 5;

    // Pre-flight time estimate — used by the wizard's confirmation screen.
    public static int EstimateEncodes(
        List<BenchmarkClip> clips, List<EncoderEntry> encoders, BenchmarkMode mode)
    {
        int total = 0;
        foreach (var clip in clips)
        {
            var scales = ResolutionsFor(clip.Info.Height);
            foreach (var _ in encoders)
                foreach (var __ in scales)
                    total += mode == BenchmarkMode.Fast
                        ? FastCqpCount
                        : AccurateSizeTargetsBytes.Length * 3 + FastCqpCount;
            // Accurate ≈ 3 encodes per size target (binary search) + a CQP sweep
            // for quality references. Rough — actual binary search may be 2-4 passes.
        }
        return total;
    }

    // Tracks overall benchmark progress so individual encode iterations can
    // print a "12/420 • 3% • ETA 2h 15m" status line. Threaded through the
    // inner loops by reference because they share state.
    private sealed class BenchmarkProgress
    {
        public int      EstimatedTotal { get; init; }
        public DateTime Start          { get; init; }
        public int      Done           { get; set; }

        public void PrintStatus(string label)
        {
            int total   = Math.Max(EstimatedTotal, Done);
            double pct  = total > 0 ? Done * 100.0 / total : 0;
            var elapsed = DateTime.UtcNow - Start;
            string eta;
            if (Done == 0 || elapsed.TotalSeconds < 2)
            {
                eta = "calculating";
            }
            else
            {
                double secPerEncode = elapsed.TotalSeconds / Done;
                int    remaining    = Math.Max(total - Done, 0);
                eta = FormatDuration(TimeSpan.FromSeconds(secPerEncode * remaining));
            }

            // 24-cell ASCII bar so it stays aligned with monospaced output.
            const int barWidth = 24;
            int filled = (int)Math.Round(barWidth * pct / 100.0);
            filled = Math.Clamp(filled, 0, barWidth);
            string bar = new string('█', filled) + new string('░', barWidth - filled);

            AnsiConsole.MarkupLine(
                $"      [dim][[{Done}/{total}][/]  [cyan]{Markup.Escape(bar)}[/]  " +
                $"[dim]{pct,5:F1}% • ETA {Markup.Escape(eta)} • {Markup.Escape(label)}[/]");
        }

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalHours   >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
            if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
            return $"{(int)Math.Max(t.TotalSeconds, 0)}s";
        }
    }

    public static BenchmarkSummary Run(
        string ffmpegPath,
        List<BenchmarkClip> clips,
        List<EncoderEntry> encoders,
        BenchmarkMode mode,
        AppConfig config,
        Logger log)
    {
        var progress = new BenchmarkProgress
        {
            EstimatedTotal = EstimateEncodes(clips, encoders, mode),
            Start          = DateTime.UtcNow,
        };
        int succeeded = 0, failed = 0;

        foreach (var clip in clips)
        {
            var scales = ResolutionsFor(clip.Info.Height);

            AnsiConsole.Write(new Rule(
                $"[bold cyan] {Markup.Escape(Path.GetFileName(clip.Path))} [/]" +
                $"[dim] - {clip.Info.Width}x{clip.Info.Height} {clip.Info.DurationS:F0}s {Markup.Escape(clip.Info.Complexity)} [/]")
                .RuleStyle("cyan dim"));
            log.Write($"\n=== Clip: {clip.Path} ({clip.Info.Width}x{clip.Info.Height} {clip.Info.DurationS:F0}s {clip.Info.Complexity}) ===");

            foreach (var encoder in encoders)
            {
                AnsiConsole.MarkupLine($"\n  [cyan]Encoder:[/] {Markup.Escape(encoder.Name)}");
                log.Write($"\n--- Encoder: {encoder.Name} ---");

                foreach (var scale in scales)
                {
                    var scaleLabel = scale.HasValue ? $"{scale}p" : $"{clip.Info.Height}p (native)";
                    AnsiConsole.MarkupLine($"    [dim]Resolution:[/] {Markup.Escape(scaleLabel)}");
                    log.Write($"  Resolution: {scaleLabel}");

                    if (mode == BenchmarkMode.Fast)
                    {
                        RunCqpSweep(ffmpegPath, clip, encoder.Name, scale, config, log,
                            progress, ref succeeded, ref failed);
                    }
                    else
                    {
                        // Accurate mode = CQP sweep for raw quality data, PLUS size-target
                        // pipeline runs to teach the binary search/cache about real targets.
                        RunCqpSweep(ffmpegPath, clip, encoder.Name, scale, config, log,
                            progress, ref succeeded, ref failed);
                        RunSizeTargets(ffmpegPath, clip, encoder.Name, scale, config, log,
                            progress, ref succeeded, ref failed);
                    }
                }
            }
        }

        // Force EncoderSlope to re-fit from the freshly-populated cache on its next read.
        EncoderSlope.Invalidate();

        return new BenchmarkSummary(progress.Done, succeeded, failed, DateTime.UtcNow - progress.Start);
    }

    // ── CQP sweep (Fast mode core) ────────────────────────────────────────────
    //
    // Picks N CQPs spanning each encoder's useful range and does a single
    // encode + SSIM per CQP. Each saved entry teaches the formula one direct
    // (complexity → bytes at cqp) data point.

    private static void RunCqpSweep(
        string ffmpegPath, BenchmarkClip clip, string encoder, int? scale,
        AppConfig config, Logger log,
        BenchmarkProgress progress, ref int succeeded, ref int failed)
    {
        int defCqp = config.GetDefaultCqp(encoder);
        int maxCqp = config.GetMaxCqp(encoder);
        if (maxCqp <= defCqp) return;

        var cqps = new int[FastCqpCount];
        for (int i = 0; i < FastCqpCount; i++)
            cqps[i] = defCqp + (maxCqp - defCqp) * i / (FastCqpCount - 1);

        var tmpDir = Path.Combine(AppPaths.LogDir, "_benchmark_tmp");
        Directory.CreateDirectory(tmpDir);

        foreach (var cqp in cqps)
        {
            progress.Done++;
            progress.PrintStatus($"CQP {cqp}");
            var tmp = Path.Combine(tmpDir, $"_bench_{Guid.NewGuid():N}.mp4");
            try
            {
                AnsiConsole.MarkupLine($"      [dim]CQP[/] [yellow]{cqp,2}[/]");
                log.Write($"    CQP {cqp,2}");

                if (!Pipeline.RunEncode(ffmpegPath, clip.Path, tmp,
                        encoder, cqp, scale, clip.Info.DurationS))
                {
                    failed++;
                    continue;
                }

                long size = new FileInfo(tmp).Length;
                var ssim  = Pipeline.CalcSsim(ffmpegPath, clip.Path, tmp, scale, clip.Info.DurationS);

                var sizeStr = $"{size / 1_048_576.0:F2} MB";
                if (ssim.HasValue)
                    AnsiConsole.MarkupLine($"        [dim]{Markup.Escape(sizeStr)}  SSIM {ssim:F4}[/]");
                else
                    AnsiConsole.MarkupLine($"        [dim]{Markup.Escape(sizeStr)}  (no SSIM)[/]");
                log.Write($"      {sizeStr}  SSIM {ssim?.ToString("F4") ?? "-"}");

                // Save as quality-mode (no size limit). The CQP suffix in the key
                // lets all 5 sweep points coexist for this (clip,encoder,scale).
                // Pass SSIM through — it's the anchor the quality-mode predictor
                // uses to map "this CQP" → "this measured SSIM", otherwise quality
                // predictions degrade to a complexity-only heuristic.
                var key = CqpCache.MakeKey(clip.Path, clip.Info.DurationS,
                    encoder, null, scale, cqp);
                CqpCache.Save(key, cqp, size, encoder, null, scale,
                    (int)Math.Round(clip.Info.DurationS), clip.Profile,
                    isBenchmark: true,
                    ssim: ssim);

                succeeded++;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"        [red]error: {Markup.Escape(ex.Message)}[/]");
                log.Write($"      ERROR: {ex.Message}");
                failed++;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        try { if (Directory.GetFiles(tmpDir).Length == 0) Directory.Delete(tmpDir); } catch { }
    }

    // ── Size-target runs (Accurate mode addition) ─────────────────────────────
    //
    // Exercises the real CompressToTarget binary search for each preset size.
    // Each run lands ONE realistic (clip,encoder,scale,limit) → best-cqp entry,
    // which models the production path more closely than the raw CQP sweep.

    private static void RunSizeTargets(
        string ffmpegPath, BenchmarkClip clip, string encoder, int? scale,
        AppConfig config, Logger log,
        BenchmarkProgress progress, ref int succeeded, ref int failed)
    {
        var tmpDir = Path.Combine(AppPaths.LogDir, "_benchmark_tmp");
        Directory.CreateDirectory(tmpDir);

        foreach (var target in AccurateSizeTargetsBytes)
        {
            // Sanity: skip if target would be wildly oversized for the source
            // (no point benchmarking a 100 MB cap on a 5-second clip).
            double targetKbps = target * 8.0 / (clip.Info.DurationS * 1000.0);
            if (clip.Info.BitrateKbps > 0 && targetKbps >= clip.Info.BitrateKbps * 0.95)
                continue;

            progress.Done++;
            var sizeStr = $"{target / 1_048_576.0:F0} MB";
            progress.PrintStatus($"Target {sizeStr}");
            var tmp = Path.Combine(tmpDir, $"_bench_t_{Guid.NewGuid():N}.mp4");
            try
            {
                AnsiConsole.MarkupLine($"      [dim]Target[/] [yellow]{Markup.Escape(sizeStr)}[/]");
                log.Write($"    Target {sizeStr}");

                var (ok, _) = Pipeline.CompressToTarget(
                    ffmpegPath, clip.Path, tmp,
                    encoder, config.GetDefaultCqp(encoder), target,
                    scale, clip.Info.BitrateKbps, clip.Info.DurationS,
                    clip.Info.Complexity, config, log, clip.Profile,
                    isBenchmark: true);

                if (ok) succeeded++; else failed++;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"        [red]error: {Markup.Escape(ex.Message)}[/]");
                log.Write($"      ERROR: {ex.Message}");
                failed++;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        try { if (Directory.GetFiles(tmpDir).Length == 0) Directory.Delete(tmpDir); } catch { }
    }

    // Resolutions to encode at: native (null) + each ladder step strictly below source.
    private static List<int?> ResolutionsFor(int srcHeight)
    {
        var result = new List<int?> { null };
        foreach (var h in ScaleLadder)
            if (h < srcHeight) result.Add(h);
        return result;
    }
}
