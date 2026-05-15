using SmartCompress.Core;
using Spectre.Console;

namespace SmartCompress;

internal record BenchmarkPlan(
    List<BenchmarkClip>  Clips,
    List<EncoderEntry>   Encoders,
    BenchmarkMode        Mode);

internal static class BenchmarkWizard
{
    private const int MinClips = 2;

    public static async Task<BenchmarkPlan?> Run(string ffmpegPath, AppConfig config)
    {
        Intro();

        // 1) Probe encoders early — GPU-only (skip CPU encoders to respect "ONLY ON GPU SIDE").
        var gpuEncoders = ProbeGpuEncoders(ffmpegPath, config);
        if (gpuEncoders.Count == 0)
        {
            AnsiConsole.MarkupLine("\n[bold red]  No GPU encoders detected on this system.[/]");
            AnsiConsole.MarkupLine("  [dim]Benchmark mode is GPU-only to avoid hour-long CPU encodes.[/]");
            return null;
        }

        AnsiConsole.MarkupLine("\n  [dim]GPU encoders detected:[/]");
        foreach (var e in gpuEncoders)
            AnsiConsole.MarkupLine($"    [cyan]{Markup.Escape(e.Name)}[/]  [dim]{Markup.Escape(e.Desc)}[/]");

        // 2) Collect clips with variety check.
        Wizard.Divider(); Console.WriteLine();
        var clips = await CollectClips();
        if (clips == null || clips.Count < MinClips) return null;

        // 3) Pick speed/accuracy mode.
        Wizard.Divider(); Console.WriteLine();
        var mode = PickMode(clips);

        // 4) Confirmation screen.
        Wizard.Divider(); Console.WriteLine();
        if (!Confirm(clips, gpuEncoders, mode)) return null;

        return new BenchmarkPlan(clips, gpuEncoders, mode);
    }

    // ── Steps ─────────────────────────────────────────────────────────────────

    private static void Intro()
    {
        AnsiConsole.MarkupLine("[bold cyan]BENCHMARK MODE[/]");
        AnsiConsole.MarkupLine("  [dim]Encodes sample clips at various settings to build up a high-quality[/]");
        AnsiConsole.MarkupLine("  [dim]reference cache. Future compressions will hit the right CQP faster.[/]");
        AnsiConsole.MarkupLine("  [dim]GPU encoders only — CPU encoders would take hours.[/]");
        Console.WriteLine();
    }

    // Video extensions accepted in benchmark folder-walks. Mirrors
    // VideoPipeline.SupportedExtensions — duplicated rather than imported to
    // keep BenchmarkWizard from depending on the pipeline registry.
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".flv", ".wmv", ".mpg", ".mpeg"
    };

    private static async Task<List<BenchmarkClip>?> CollectClips()
    {
        AnsiConsole.MarkupLine("[bold]Add clips — one at a time, or paste a folder path.[/]");
        AnsiConsole.MarkupLine("  [dim]Minimum 2. We'll keep asking for more until variety is good.[/]");
        AnsiConsole.MarkupLine("  [dim]Drag-drop or paste a file/folder. Blank line + Enter to stop adding.[/]\n");

        var clips = new List<BenchmarkClip>();

        while (true)
        {
            AnsiConsole.Markup($"  [cyan]Clip {clips.Count + 1}[/]  [dim]>[/] ");
            var raw = Wizard.ReadLineDrained().Trim().Trim('"').Trim('\'');

            // Blank line: only allowed once min reached AND variety good.
            if (raw.Length == 0)
            {
                if (clips.Count < MinClips)
                {
                    AnsiConsole.MarkupLine($"  [red][[!]] Need at least {MinClips} clips. Add another.[/]\n");
                    continue;
                }
                var v = EvaluateVariety(clips);
                if (v.Sufficient) break;

                AnsiConsole.MarkupLine($"\n  [yellow][[!]] {Markup.Escape(v.Reason)}[/]");
                AnsiConsole.MarkupLine("  [dim]Strongly recommend adding more clips for better cache coverage.[/]");
                AnsiConsole.Markup("  [dim]Force start anyway? [[y/N]]:[/] ");
                if (Wizard.ReadLineDrained().Trim().ToLowerInvariant() == "y") break;
                Console.WriteLine();
                continue;
            }

            // Folder path: walk it, try each video file, summary-print the result.
            if (Directory.Exists(raw))
            {
                var collected = BatchCollector.Collect(raw);
                var videos = collected.Files
                    .Where(f => VideoExts.Contains(Path.GetExtension(f)))
                    .ToList();
                if (videos.Count == 0)
                {
                    AnsiConsole.MarkupLine("  [red][[!]] No video files in that folder.[/]\n");
                    continue;
                }
                AnsiConsole.MarkupLine($"  [dim]Walking {videos.Count} video file(s)...[/]");
                int added = 0, skipDup = 0, skipBad = 0;
                foreach (var f in videos)
                {
                    if (clips.Any(c => string.Equals(c.Path, f, StringComparison.OrdinalIgnoreCase)))
                    {
                        skipDup++;
                        continue;
                    }
                    if (await TryAddClipAsync(f, clips, verbose: true)) added++;
                    else skipBad++;
                }
                AnsiConsole.MarkupLine(
                    $"  [green]Added {added}[/] from folder" +
                    (skipDup > 0 ? $", [dim]{skipDup} duplicate[/]" : "") +
                    (skipBad > 0 ? $", [yellow]{skipBad} unusable[/]" : "") + "\n");
                ShowVarietyHint(clips);
                continue;
            }

            if (!File.Exists(raw))
            {
                AnsiConsole.MarkupLine("  [red][[!]] File not found.[/]\n");
                continue;
            }
            if (clips.Any(c => string.Equals(c.Path, raw, StringComparison.OrdinalIgnoreCase)))
            {
                AnsiConsole.MarkupLine("  [yellow][[!]] Already added that clip.[/]\n");
                continue;
            }

            await TryAddClipAsync(raw, clips, verbose: true);
            ShowVarietyHint(clips);
            Console.WriteLine();
        }

        return clips;
    }

    // Probe + add one clip. Returns true if added, false if it couldn't be used.
    // Quiet on success paths in folder-walk mode — the caller prints a summary.
    private static async Task<bool> TryAddClipAsync(string path, List<BenchmarkClip> clips, bool verbose)
    {
        VideoInfo info;
        try { info = await VideoAnalyzer.GetVideoInfoAsync(path); }
        catch (Exception ex)
        {
            if (verbose)
                AnsiConsole.MarkupLine($"  [red][[!]] {Markup.Escape(Path.GetFileName(path))}: can't analyse — {Markup.Escape(ex.Message)}[/]");
            return false;
        }

        ComplexityProfile? profile = null;
        try { profile = await VideoAnalyzer.GetComplexityProfileAsync(path, info.Width, info.Height); }
        catch { }

        if (profile == null)
        {
            if (verbose)
                AnsiConsole.MarkupLine($"  [yellow][[!]] {Markup.Escape(Path.GetFileName(path))}: no complexity profile — skipped.[/]");
            return false;
        }

        clips.Add(new BenchmarkClip(path, info, profile));

        var color = info.Complexity switch { "Simple" => "green", "Complex" => "red", _ => "yellow" };
        AnsiConsole.MarkupLine(
            $"    [green]+[/] {Markup.Escape(Path.GetFileName(path))}  " +
            $"[dim]{info.Width}x{info.Height}  {info.DurationS:F0}s[/]  " +
            $"[{color}]{Markup.Escape(info.Complexity)}[/]  " +
            $"[dim]mc={profile.MeanComplexity:F3}[/]");
        AnsiConsole.MarkupLine($"      {profile.ToSpectreBar()}");
        return true;
    }

    private static void ShowVarietyHint(List<BenchmarkClip> clips)
    {
        if (clips.Count < MinClips) return;
        var v = EvaluateVariety(clips);
        if (v.Sufficient)
            AnsiConsole.MarkupLine("  [green dim]Variety OK — press Enter on blank line to start, or add more.[/]");
        else
            AnsiConsole.MarkupLine($"  [yellow dim]{Markup.Escape(v.Reason)}[/]");
    }

    private static BenchmarkMode PickMode(List<BenchmarkClip> clips)
    {
        AnsiConsole.MarkupLine("[bold]Speed vs accuracy[/]");
        AnsiConsole.MarkupLine("  [dim]Accuracy difference is small in most cases — Fast is usually the right call.[/]");
        AnsiConsole.MarkupLine("  [dim]Accurate matters more when you have lots of varied content to throw at it.[/]\n");

        AnsiConsole.MarkupLine("  [dim][[1]][/]  [green]Fast[/]      [dim]- CQP sweep only. ~5 encodes per (clip × encoder × res).[/]");
        AnsiConsole.MarkupLine("  [dim][[2]][/]  [yellow]Accurate[/]  [dim]- CQP sweep + size-target binary searches. ~3x slower.[/]");
        Console.WriteLine();

        while (true)
        {
            AnsiConsole.MarkupLine("  [dim](default: 1)[/]");
            AnsiConsole.Markup("  [dim]>[/] ");
            var raw = Wizard.ReadLineDrained().Trim();
            if (raw.Length == 0 || raw == "1") return BenchmarkMode.Fast;
            if (raw == "2") return BenchmarkMode.Accurate;
            AnsiConsole.MarkupLine("  [red][[!]] Enter 1 or 2.[/]\n");
        }
    }

    private static bool Confirm(
        List<BenchmarkClip> clips, List<EncoderEntry> encoders, BenchmarkMode mode)
    {
        int total = Benchmark.EstimateEncodes(clips, encoders, mode);

        // Rough ETA: GPU encodes ~5-10s on typical content. Pick the middle.
        // SSIM adds ~30-50% per encode for Fast mode. Accurate is dominated by binary search.
        double secondsPerEncode = mode == BenchmarkMode.Fast ? 10.0 : 8.0;
        var eta = TimeSpan.FromSeconds(total * secondsPerEncode);

        AnsiConsole.MarkupLine("[bold]Ready to start:[/]\n");

        var table = new Spectre.Console.Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn(new TableColumn("").RightAligned().Width(14));
        table.AddColumn("");
        table.AddRow("[dim]Clips[/]",    $"[white]{clips.Count}[/]");
        table.AddRow("[dim]Encoders[/]", $"[cyan]{string.Join(", ", encoders.Select(e => e.Name))}[/]");
        table.AddRow("[dim]Mode[/]",     mode == BenchmarkMode.Fast
            ? "[green]Fast[/]  [dim](CQP sweep)[/]"
            : "[yellow]Accurate[/]  [dim](CQP sweep + size targets)[/]");
        table.AddRow("[dim]Encodes[/]",  $"[white]~{total}[/]");
        table.AddRow("[dim]ETA[/]",      $"[white]~{FormatEta(eta)}[/]  [dim](varies with clip length and GPU)[/]");
        AnsiConsole.Write(table);

        Console.WriteLine();
        AnsiConsole.Markup("  Press [bold]Enter[/] to start, or type [dim]n[/] to cancel: ");
        var ans = Wizard.ReadLineDrained().Trim().ToLowerInvariant();
        return ans != "n";
    }

    // ── Variety analysis ─────────────────────────────────────────────────────

    private record VarietyResult(bool Sufficient, string Reason);

    private static VarietyResult EvaluateVariety(List<BenchmarkClip> clips)
    {
        if (clips.Count < MinClips)
            return new VarietyResult(false, $"Need at least {MinClips} clips");

        // 1) Complexity classification spread — Simple/Medium/Complex.
        var classes = clips.Select(c => c.Info.Complexity).Distinct().Count();

        // 2) MeanComplexity range (absolute bppf). Wider = more useful references.
        var mcs   = clips.Select(c => (double)c.Profile.MeanComplexity).Where(m => m > 0).ToList();
        double mcRatio = mcs.Count >= 2 && mcs.Min() > 0
            ? mcs.Max() / mcs.Min()
            : 1.0;

        // 3) Duration spread — having both short and long clips populates duration-axis data.
        var durs       = clips.Select(c => c.Info.DurationS).ToList();
        double durRatio = durs.Min() > 0 ? durs.Max() / durs.Min() : 1.0;

        // Five clips is enough to ride out lower variety — we have raw volume.
        if (clips.Count >= 5) return new VarietyResult(true, "5+ clips — volume compensates");

        if (classes >= 2 && mcRatio >= 1.5)
            return new VarietyResult(true, "Good spread");

        // Targeted feedback so the user knows WHAT to add.
        if (classes < 2)
            return new VarietyResult(false,
                $"All clips classify as '{clips[0].Info.Complexity}' — add a contrasting clip");
        if (mcRatio < 1.5)
            return new VarietyResult(false,
                $"Complexity range is narrow ({mcRatio:F1}x) — add a clip with different content type");
        if (durRatio < 2.0)
            return new VarietyResult(false,
                $"Duration range is narrow ({durRatio:F1}x) — mix in a longer or shorter clip");

        return new VarietyResult(true, "OK");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<EncoderEntry> ProbeGpuEncoders(string ffmpegPath, AppConfig config)
    {
        AnsiConsole.MarkupLine("[dim]Probing encoders...[/]\n");
        var working = EncoderProber.ProbeWorkingEncoders(ffmpegPath, config);

        // GPU encoders all match `nvenc`, `amf`, or `qsv` in the encoder name.
        // CPU encoders (libx264, libx265, libsvtav1, libaom-av1) are excluded.
        return working.Where(e =>
            e.Name.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
            e.Name.Contains("amf",   StringComparison.OrdinalIgnoreCase) ||
            e.Name.Contains("qsv",   StringComparison.OrdinalIgnoreCase)
        ).ToList();
    }

    private static string FormatEta(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalSeconds}s";
    }
}
