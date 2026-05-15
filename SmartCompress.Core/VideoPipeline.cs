using Spectre.Console;

namespace SmartCompress.Core;

/// <summary>
/// Wraps the existing `Pipeline.CompressSmart` flow behind `IMediaPipeline`.
/// All real work still lives in `Pipeline` — this class just handles batch-level
/// configuration (encoder + forced resolution asked once, applied to every file)
/// and per-file output path derivation.
/// </summary>
public sealed class VideoPipeline : IMediaPipeline
{
    private static readonly string[] Exts =
    {
        ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".flv", ".wmv", ".mpg", ".mpeg"
    };

    public string   Name                => "Video";
    public string[] SupportedExtensions => Exts;

    public bool CanHandle(string filePath) =>
        Exts.Contains(Path.GetExtension(filePath).ToLowerInvariant());

    // Filled in by `ConfigureForBatchAsync`. The wizard asks these once and we
    // apply them to every video in the batch.
    private string?              _encoder;
    private int                  _forcedRes;
    private List<EncoderEntry>   _workingEncoders = new();

    /// <summary>
    /// Hook injected from the CLI layer: prompts the user to pick an encoder +
    /// forced resolution. Defined as a delegate so Core stays UI-free —
    /// the CLI wires this up at startup.
    /// </summary>
    public static Func<List<EncoderEntry>, List<string>, int, (string? encoder, int forcedRes)>?
        WizardPromptDelegate { get; set; }

    public Task<bool> ConfigureForBatchAsync(BatchContext ctx, List<string> files)
    {
        AnsiConsole.MarkupLine($"\n[dim]Probing encoders for {files.Count} video file(s)...[/]\n");
        _workingEncoders = EncoderProber.ProbeWorkingEncoders(ctx.FfmpegPath, ctx.Config);
        if (_workingEncoders.Count == 0)
        {
            AnsiConsole.MarkupLine("[bold red][[ERROR]][/] No working video encoders found.");
            return Task.FromResult(false);
        }

        if (WizardPromptDelegate == null)
        {
            AnsiConsole.MarkupLine("[bold red][[ERROR]][/] No wizard delegate registered for VideoPipeline.");
            return Task.FromResult(false);
        }

        // Use the maximum source height across the batch as the upper bound for
        // the resolution menu. Per-file resolution caps still apply inside
        // CompressSmart's ladder, but the wizard option list itself is one menu.
        int maxHeight = 0;
        foreach (var f in files)
        {
            try
            {
                var info = VideoAnalyzer.GetVideoInfoAsync(f).GetAwaiter().GetResult();
                if (info.Height > maxHeight) maxHeight = info.Height;
            }
            catch { /* skip unreadable, downstream will surface the error */ }
        }
        if (maxHeight == 0) maxHeight = 1080;  // sensible fallback

        var (enc, res) = WizardPromptDelegate(_workingEncoders, ctx.CodecPriority, maxHeight);
        if (enc == null) return Task.FromResult(false);

        _encoder   = enc;
        _forcedRes = res;
        return Task.FromResult(true);
    }

    public async Task<MediaResult> ProcessAsync(string src, BatchContext ctx, Logger log)
    {
        long inputBytes = 0;
        try { inputBytes = new FileInfo(src).Length; } catch { }

        if (_encoder == null)
            return new MediaResult(false, src, null, inputBytes, 0, "Not configured",
                ErrorMessage: "VideoPipeline.ConfigureForBatchAsync was not called or was cancelled.");

        // Skip-existing: if `<stem>_compressed.mp4` is already in the output
        // folder and newer than the source, treat this file as already done.
        // Lets interrupted batches resume cleanly instead of producing
        // `_compressed_2.mp4` duplicates.
        string baseDst = BaseOutputPath(src, ctx.OutputFolder);
        if (File.Exists(baseDst))
        {
            try
            {
                var dstInfo = new FileInfo(baseDst);
                var srcInfo = new FileInfo(src);
                if (dstInfo.LastWriteTimeUtc >= srcInfo.LastWriteTimeUtc && dstInfo.Length > 0)
                {
                    AnsiConsole.MarkupLine(
                        $"  [dim]Already compressed — skipping ([dim]{Markup.Escape(Path.GetFileName(baseDst))}[/])[/]");
                    log.Write($"  Already compressed — skipping ({baseDst})");
                    return new MediaResult(true, src, baseDst, inputBytes, dstInfo.Length,
                        Notes: "already exists (skipped)");
                }
            }
            catch { /* fall through to re-encode */ }
        }

        VideoInfo info;
        try { info = await VideoAnalyzer.GetVideoInfoAsync(src); }
        catch (Exception ex)
        {
            return new MediaResult(false, src, null, inputBytes, 0, "Probe failed",
                ErrorMessage: ex.Message);
        }

        ComplexityProfile? profile = null;
        try { profile = await VideoAnalyzer.GetComplexityProfileAsync(src, info.Width, info.Height); }
        catch { }

        string dst = DeriveOutputPath(src, ctx.OutputFolder);

        // Reuse the existing pipeline verbatim. All the smart math (predictor,
        // skip check, etc.) is already in there — VideoPipeline doesn't need
        // to duplicate any of it.
        var result = Pipeline.CompressSmart(
            ctx.FfmpegPath, src, dst,
            _encoder, ctx.Config.GetDefaultCqp(_encoder), ctx.SizeLimit,
            info.Height, info, _forcedRes, ctx.Config, log, profile);

        if (!result.Success)
            return new MediaResult(false, src, dst, inputBytes, 0,
                "Could not hit target", ErrorMessage: "Quality floor or size cap unreachable.");

        long outBytes = 0;
        try { outBytes = new FileInfo(dst).Length; } catch { }

        string notes = $"{_encoder} @ {(result.ScaleUsed?.ToString() ?? info.Height + "p")}" +
                       (result.Ssim.HasValue ? $", SSIM {result.Ssim:F4}" : "");

        return new MediaResult(true, src, dst, inputBytes, outBytes, notes);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // The canonical output path for a source. Always `<stem>_compressed.mp4`.
    // Used by the skip-existing check.
    private static string BaseOutputPath(string src, string folder) =>
        Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(src)}_compressed.mp4");

    private static string DeriveOutputPath(string src, string folder)
    {
        var path = BaseOutputPath(src, folder);
        // Disambiguate only when the base path exists but ProcessAsync chose
        // NOT to skip it (e.g. source was newer than the existing output).
        // Overwriting the stale one would be tempting, but a user re-running
        // after editing the source probably wants to keep both for comparison.
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(src);
        int n = 2;
        do
            path = Path.Combine(folder, $"{stem}_compressed_{n++}.mp4");
        while (File.Exists(path));
        return path;
    }
}
