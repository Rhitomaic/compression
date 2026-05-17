using FFMpegCore;
using FFMpegCore.Extensions.Downloader;
using FFMpegCore.Extensions.Downloader.Enums;
using SmartCompress;
using SmartCompress.Core;
using Spectre.Console;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding  = System.Text.Encoding.UTF8;

// ── FFmpeg setup ──────────────────────────────────────────────────────────────
var ffmpegDir = AppPaths.FfmpegDir;
Directory.CreateDirectory(ffmpegDir);

var ffmpegExe = FfmpegLocator.GetFfmpegPath();

var ffmpegOptions = new FFOptions
{
    BinaryFolder = Path.GetDirectoryName(ffmpegExe)!,
    TemporaryFilesFolder = Path.GetTempPath()
};

GlobalFFOptions.Configure(ffmpegOptions);

if (!File.Exists(ffmpegExe))
{
    AnsiConsole.MarkupLine("[cyan]FFmpeg not found.[/] Downloading [dim](one-time setup, ~75 MB)...[/]");
    await FFMpegDownloader.DownloadBinaries(
        version:  FFMpegVersions.LatestAvailable,
        binaries: FFMpegBinaries.FFMpeg | FFMpegBinaries.FFProbe,
        options:  ffmpegOptions);
    AnsiConsole.MarkupLine("[green]FFmpeg ready.[/]\n");
}

// ── Config ────────────────────────────────────────────────────────────────────
var configPath = AppPaths.ConfigFile;
if (!File.Exists(configPath))
{
    AnsiConsole.MarkupLine("[bold red][[ERROR]][/] config.json not found next to the executable.");
    return 1;
}
var config = AppConfig.Load(configPath);

// ── Banner ────────────────────────────────────────────────────────────────────
Console.WriteLine();
AnsiConsole.Write(new Rule($"[bold cyan] SmartCompress [/] [dim]v{config.Version}[/]").RuleStyle("cyan dim"));
Console.WriteLine();

// ── Main loop ─────────────────────────────────────────────────────────────────
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    AnsiConsole.MarkupLine("\n\n  [dim]Cancelling — cleaning up...[/]");
    // Kill any active ffmpeg children + delete tmp files in the session output
    // folder. Without this, Ctrl+C during an encode leaves `_sc_tmp_*.mp4`
    // orphans next to whichever output folder the user picked.
    Pipeline.CancelAll();
    Environment.Exit(0);
};

while (true)
{
    var mode = PickMode();
    if (mode == AppMode.Quit) break;

    if (mode == AppMode.Compress)
    {
        await RunSession(ffmpegExe, config);
        Console.WriteLine();
        AnsiConsole.Markup("  Run something else? [dim][[y/N]][/]: ");
        var again = Wizard.ReadLineDrained().Trim().ToLowerInvariant();
        if (again != "y") break;
        Console.WriteLine();
    }
    else
    {
        await RunBenchmark(ffmpegExe, config);
        Console.WriteLine();
        AnsiConsole.Markup("  Run something else? [dim][[y/N]][/]: ");
        var again = Wizard.ReadLineDrained().Trim().ToLowerInvariant();
        if (again != "y") break;
        Console.WriteLine();
    }
}

return 0;

// ── Mode selector ─────────────────────────────────────────────────────────────
static AppMode PickMode()
{
    AnsiConsole.MarkupLine("[bold]What do you want to do?[/]\n");
    AnsiConsole.MarkupLine("  [dim][[1]][/]  [white]Compress a video[/]");
    AnsiConsole.MarkupLine("  [dim][[2]][/]  [white]Run benchmark[/]  [dim](populate cache for better predictions)[/]");
    AnsiConsole.MarkupLine("  [dim][[3]][/]  [dim]Quit[/]");
    Console.WriteLine();

    while (true)
    {
        AnsiConsole.MarkupLine("  [dim](default: 1)[/]");
        AnsiConsole.Markup("  [dim]>[/] ");
        var raw = Wizard.ReadLineDrained().Trim();
        if (raw.Length == 0 || raw == "1") return AppMode.Compress;
        if (raw == "2") return AppMode.Benchmark;
        if (raw == "3" || raw.Equals("q", StringComparison.OrdinalIgnoreCase)) return AppMode.Quit;
        AnsiConsole.MarkupLine("  [red][[!]] Enter 1, 2, or 3.[/]\n");
    }
}

// ── Benchmark runner ──────────────────────────────────────────────────────────
static async Task RunBenchmark(string ffmpegExe, AppConfig config)
{
    var logDir = AppPaths.LogDir;
    using var log = new Logger();
    log.Init(logDir);
    log.Write("=== Benchmark session ===");

    Console.WriteLine();
    var plan = await BenchmarkWizard.Run(ffmpegExe, config);
    if (plan == null)
    {
        AnsiConsole.MarkupLine("\n  [dim]Benchmark cancelled.[/]");
        return;
    }

    Console.WriteLine();
    AnsiConsole.Write(new Rule("[bold cyan] Running benchmark [/]").RuleStyle("cyan dim"));
    Console.WriteLine();

    var summary = Benchmark.Run(ffmpegExe, plan.Clips, plan.Encoders, plan.Mode, config, log);

    Console.WriteLine();
    AnsiConsole.Write(new Rule("[bold green] Benchmark complete [/]").RuleStyle("green"));
    Console.WriteLine();
    AnsiConsole.MarkupLine($"  [dim]Encodes:[/]   [white]{summary.Succeeded} / {summary.Total}[/]" +
        (summary.Failed > 0 ? $"   [red]({summary.Failed} failed)[/]" : ""));
    AnsiConsole.MarkupLine($"  [dim]Time:[/]      [white]{summary.Elapsed:hh\\:mm\\:ss}[/]");
    AnsiConsole.MarkupLine($"  [dim]Saved to:[/]  [dim]{Markup.Escape(AppPaths.BenchmarkCacheFile)}[/]");

    log.Write($"\nBenchmark done: {summary.Succeeded}/{summary.Total} OK, {summary.Failed} failed, {summary.Elapsed:hh\\:mm\\:ss}");

    // Honest self-test: hold each clip out, ask the predictor to recover its
    // CQPs from the rest. Tells you whether the cache is actually predictive.
    Console.WriteLine();
    AnsiConsole.Write(new Rule("[dim] Cross-validation [/]").RuleStyle("grey dim"));
    Console.WriteLine();
    var report = BenchmarkValidator.CrossValidate();
    BenchmarkValidator.PrintReport(report);

    log.Write("\nCross-validation:");
    foreach (var b in report)
        log.Write($"  {b.Encoder} {b.ScaleHeight?.ToString() ?? "native"}: " +
                  $"N={b.Count} median={b.MedianResidual:+0.0;-0.0;0.0} rmse={b.Rmse:F2} worst={b.MaxAbsResidual:F0}");
}

// ── Session ───────────────────────────────────────────────────────────────────
//
// Batch-aware flow:
//   1. wizard collects path (file OR folder)
//   2. dispatcher classifies files into per-pipeline buckets
//   3. preset (size limit) asked once, applied to all
//   4. each engaged pipeline asks its own questions via ConfigureForBatchAsync
//   5. each file processed through its routed pipeline
//   6. aggregate result table summarises totals
static async Task RunSession(string ffmpegExe, AppConfig config)
{
    var logDir = AppPaths.LogDir;
    using var log = new Logger();
    log.Init(logDir);

    // Wire the VideoPipeline's wizard hook before doing anything. Core stays
    // UI-free; this delegate is how it pulls user input from the CLI layer.
    VideoPipeline.WizardPromptDelegate = (working, codecPriority, maxHeight) =>
    {
        var recommended = EncoderProber.PickRecommended(working, codecPriority);
        Wizard.Divider(); Console.WriteLine();
        var enc = Wizard.StepEncoder(working, recommended);
        if (enc is null) return (null, 0);
        Wizard.Divider(); Console.WriteLine();
        // Resolution menu is built off the largest source in the batch — the per-file
        // ladder inside CompressSmart still respects each file's actual height.
        int res = Wizard.StepResolution(maxHeight, "Medium", config);
        return (enc, res);
    };

    var dispatcher = new MediaDispatcher(new IMediaPipeline[]
    {
        new VideoPipeline(),
        // Future: new ImagePipeline(), new AudioPipeline(), ...
    });

    // ── Step 1: collect ──────────────────────────────────────────────────────
    var rawPath = Wizard.StepInput();
    var collected = BatchCollector.Collect(rawPath);
    if (collected.Files.Count == 0)
    {
        AnsiConsole.MarkupLine("\n[bold red][[ERROR]][/] Nothing found at that path.");
        return;
    }

    var plan = dispatcher.Classify(collected.Files);
    if (plan.HandledCount == 0)
    {
        AnsiConsole.MarkupLine("\n[bold red][[ERROR]][/] No supported media files in that location.");
        AnsiConsole.MarkupLine($"  [dim]Found {plan.UnhandledCount} unsupported file(s). " +
            $"Supported: {string.Join(", ", dispatcher.All.SelectMany(p => p.SupportedExtensions))}[/]");
        return;
    }

    // Show what we found.
    Console.WriteLine();
    AnsiConsole.MarkupLine($"  [bold]Found:[/]  {plan.HandledCount} file(s)  " +
        $"[dim]({Markup.Escape(config.FormatMb(collected.TotalBytes))} total)[/]" +
        (collected.WasFolder ? "  [dim](from folder)[/]" : ""));
    foreach (var (pipe, files) in plan.ByPipeline.Where(kv => kv.Value.Count > 0))
        AnsiConsole.MarkupLine($"    [cyan]{Markup.Escape(pipe.Name)}[/]: {files.Count}");
    if (plan.UnhandledCount > 0)
        AnsiConsole.MarkupLine($"    [dim]Skipping {plan.UnhandledCount} unsupported file(s)[/]");

    // Per-file detail table — probe each video in parallel for the quick stats.
    // GetVideoInfoAsync is fast (ffprobe metadata only, no packet scan), so we
    // can afford to run it for every file up front. The complexity rating from
    // info is bppf-based, doesn't need the slow ComplexityProfile pass.
    var videoFiles = plan.ByPipeline
        .Where(kv => kv.Key is VideoPipeline)
        .SelectMany(kv => kv.Value)
        .ToList();
    if (videoFiles.Count >= 2)
    {
        Console.WriteLine();
        AnsiConsole.MarkupLine("  [dim]Probing files...[/]");
        var probeTasks = videoFiles.Select(async f =>
        {
            try   { return (f, info: (VideoInfo?)await VideoAnalyzer.GetVideoInfoAsync(f), err: (string?)null); }
            catch (Exception ex) { return (f, info: null, err: ex.Message); }
        });
        var probes = await Task.WhenAll(probeTasks);

        var table = new Spectre.Console.Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn(new TableColumn("[dim]#[/]").RightAligned());
        table.AddColumn("[dim]File[/]");
        table.AddColumn(new TableColumn("[dim]Res[/]").RightAligned());
        table.AddColumn(new TableColumn("[dim]Duration[/]").RightAligned());
        table.AddColumn(new TableColumn("[dim]Size[/]").RightAligned());
        table.AddColumn(new TableColumn("[dim]Bitrate[/]").RightAligned());
        table.AddColumn("[dim]Complexity[/]");

        int idx = 0;
        foreach (var (path, info, err) in probes)
        {
            idx++;
            string sizeStr = "?";
            try { sizeStr = config.FormatMb(new FileInfo(path).Length); } catch { }
            var nameShort = Path.GetFileName(path);
            if (nameShort.Length > 42) nameShort = nameShort[..39] + "...";

            if (info == null)
            {
                table.AddRow(idx.ToString(), $"[red]{Markup.Escape(nameShort)}[/]",
                    "-", "-", sizeStr, "-",
                    $"[red]probe failed: {Markup.Escape(err ?? "unknown")}[/]");
                continue;
            }

            var cColor = info.Complexity switch
            {
                "Simple"  => "green",
                "Complex" => "red",
                _         => "yellow",
            };
            var dur = TimeSpan.FromSeconds(info.DurationS);
            string durStr = dur.TotalHours >= 1
                ? $"{(int)dur.TotalHours}h{dur.Minutes:00}m{dur.Seconds:00}s"
                : $"{(int)dur.TotalMinutes}m{dur.Seconds:00}s";
            table.AddRow(
                idx.ToString(),
                Markup.Escape(nameShort),
                $"{info.Width}x{info.Height}",
                durStr,
                sizeStr,
                $"{info.BitrateKbps:N0} kbps",
                $"[{cColor}]{Markup.Escape(info.Complexity)}[/]");
        }
        AnsiConsole.Write(table);
    }

    log.Write($"Batch input: {rawPath}");
    log.Write($"  Files: {plan.HandledCount} handled, {plan.UnhandledCount} skipped, " +
              $"{config.FormatMb(collected.TotalBytes)} total");

    // ── Step 2: preset (shared by all files) ─────────────────────────────────
    Wizard.Divider(); Console.WriteLine();
    var (sizeLimit, codecPriority, presetLabel) = Wizard.StepPreset(config);

    var outFolder = "";
    Wizard.Divider(); Console.WriteLine();
    outFolder = Wizard.StepOutputFolder();

    // Now that we know the output folder, sweep any orphan tmp files from a
    // prior hard-crash and register it so Ctrl+C can clean up mid-batch.
    Pipeline.SweepStaleTmps(outFolder);
    Pipeline.SessionTmpDir = outFolder;

    var ctx = new BatchContext
    {
        SizeLimit     = sizeLimit,
        CodecPriority = codecPriority,
        PresetLabel   = presetLabel,
        OutputFolder  = outFolder,
        Config        = config,
        FfmpegPath    = ffmpegExe,
    };

    // ── Step 3: per-pipeline config (encoder, etc.) ──────────────────────────
    foreach (var (pipe, files) in plan.ByPipeline.Where(kv => kv.Value.Count > 0))
    {
        bool configured = await pipe.ConfigureForBatchAsync(ctx, files);
        if (!configured)
        {
            AnsiConsole.MarkupLine($"\n[bold red][[ERROR]][/] Could not configure {Markup.Escape(pipe.Name)} pipeline.");
            return;
        }
    }

    // ── Step 4: confirm ──────────────────────────────────────────────────────
    Wizard.Divider(); Console.WriteLine();
    AnsiConsole.MarkupLine("[bold]Ready — here's what will happen:[/]\n");
    var confirm = new Spectre.Console.Table().Border(TableBorder.None).HideHeaders();
    confirm.AddColumn(new TableColumn("").RightAligned().Width(14));
    confirm.AddColumn("");
    confirm.AddRow("[dim]Files[/]",  $"[white]{plan.HandledCount}[/]  [dim]({Markup.Escape(config.FormatMb(collected.TotalBytes))})[/]");
    confirm.AddRow("[dim]Preset[/]", $"[white]{Markup.Escape(presetLabel.Trim())}[/]");
    if (sizeLimit.HasValue)
        confirm.AddRow("[dim]Limit (per file)[/]", $"[yellow]{Markup.Escape(config.FormatMb(sizeLimit.Value))}[/]");
    confirm.AddRow("[dim]Output[/]", $"[dim]{Markup.Escape(outFolder)}[/]");
    AnsiConsole.Write(confirm);

    Console.WriteLine();
    AnsiConsole.Markup("  Press [bold]Enter[/] to start, or [dim]Ctrl+C[/] to cancel...");
    Wizard.ReadLineDrained();
    Console.WriteLine();

    // ── Step 5: process the batch ────────────────────────────────────────────
    var batchStart = DateTime.UtcNow;
    var results = new List<MediaResult>();
    int fileIdx = 0;
    // Live ETA bookkeeping: total input bytes vs bytes already finished.
    // Recomputed from observed speed each iteration so the estimate self-corrects.
    long bytesDone   = 0;
    long bytesTotal  = collected.TotalBytes;

    foreach (var (pipe, files) in plan.ByPipeline.Where(kv => kv.Value.Count > 0))
    {
        foreach (var f in files)
        {
            fileIdx++;

            // Print a live ETA before each file once we have data to base it on.
            // First file: no estimate (nothing to learn from yet).
            if (fileIdx > 1 && bytesDone > 0)
            {
                var soFar       = DateTime.UtcNow - batchStart;
                long remaining  = Math.Max(bytesTotal - bytesDone, 0);
                double secsLeft = soFar.TotalSeconds * remaining / bytesDone;
                double mbPerMin = (bytesDone / 1_048_576.0) /
                                  Math.Max(soFar.TotalMinutes, 0.001);
                Console.WriteLine();
                AnsiConsole.MarkupLine(
                    $"  [dim]ETA remaining: ~{Markup.Escape(FormatEta(TimeSpan.FromSeconds(secsLeft)))}" +
                    $"  ({mbPerMin:F1} MB/min so far)[/]");
            }

            Console.WriteLine();
            // Note: [[...]] is Spectre's escape for literal square brackets — otherwise
            // it tries to parse `[1/5]` as a markup tag and throws at render time.
            AnsiConsole.Write(new Rule(
                $"[bold cyan] [[{fileIdx}/{plan.HandledCount}]] {Markup.Escape(Path.GetFileName(f))} [/]")
                .RuleStyle("cyan dim"));
            Console.WriteLine();
            log.Write($"\n=== [{fileIdx}/{plan.HandledCount}] {f} ===");

            try
            {
                var r = await pipe.ProcessAsync(f, ctx, log);
                results.Add(r);
                bytesDone += r.InputBytes;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"\n[red]  Exception: {Markup.Escape(ex.Message)}[/]");
                log.Write($"  EXCEPTION: {ex.Message}");
                results.Add(new MediaResult(false, f, null, 0, 0, "Exception", ex.Message));
                // Even on exception, count the file's input bytes so ETA doesn't
                // stall pretending nothing happened.
                try { bytesDone += new FileInfo(f).Length; } catch { }
            }
        }
    }

    // ── Step 6: aggregate result table ───────────────────────────────────────
    Console.WriteLine();
    AnsiConsole.Write(new Rule("[bold green] Batch complete [/]").RuleStyle("green"));
    Console.WriteLine();

    int ok = results.Count(r => r.Success);
    int fail = results.Count - ok;
    long totalIn  = results.Sum(r => r.InputBytes);
    long totalOut = results.Where(r => r.Success).Sum(r => r.OutputBytes);
    double reduction = totalIn > 0 ? (1 - (double)totalOut / totalIn) * 100 : 0;
    var elapsed = DateTime.UtcNow - batchStart;

    var summary = new Spectre.Console.Table().Border(TableBorder.None).HideHeaders();
    summary.AddColumn(new TableColumn("").RightAligned().Width(16));
    summary.AddColumn("");
    summary.AddRow("[dim]Processed[/]",
        $"[white]{ok}/{results.Count}[/]" + (fail > 0 ? $"  [red]({fail} failed)[/]" : ""));
    summary.AddRow("[dim]Input total[/]",  $"[dim]{Markup.Escape(config.FormatMb(totalIn))}[/]");
    summary.AddRow("[dim]Output total[/]",
        $"[bold white]{Markup.Escape(config.FormatMb(totalOut))}[/]  [green]({reduction:F1}% smaller)[/]");
    summary.AddRow("[dim]Time[/]",         $"[white]{elapsed:hh\\:mm\\:ss}[/]");
    summary.AddRow("[dim]Saved to[/]",     $"[dim]{Markup.Escape(outFolder)}[/]");
    AnsiConsole.Write(summary);

    if (fail > 0)
    {
        Console.WriteLine();
        AnsiConsole.MarkupLine("[bold red]Failures:[/]");
        foreach (var r in results.Where(x => !x.Success))
            AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(Path.GetFileName(r.SourcePath))}  " +
                $"[dim]— {Markup.Escape(r.ErrorMessage ?? r.Notes)}[/]");
    }

    AnsiConsole.Write(new Rule().RuleStyle("green dim"));

    log.Write("");
    log.Write($"Batch result: {ok}/{results.Count} OK, {fail} failed");
    log.Write($"  In:  {config.FormatMb(totalIn)}");
    log.Write($"  Out: {config.FormatMb(totalOut)}  ({reduction:F1}% smaller)");
    log.Write($"  Time: {elapsed:hh\\:mm\\:ss}");
}

static string FormatEta(TimeSpan t)
{
    if (t.TotalHours   >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
    if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
    return $"{(int)Math.Max(t.TotalSeconds, 0)}s";
}

enum AppMode { Compress, Benchmark, Quit }
