namespace SmartCompress.Core;

/// <summary>
/// One outcome from processing a single file via a media pipeline.
/// All fields populated regardless of success so the batch summary can still
/// report what was attempted.
/// </summary>
public record MediaResult(
    bool    Success,
    string  SourcePath,
    string? DestinationPath,
    long    InputBytes,
    long    OutputBytes,
    string  Notes,
    string? ErrorMessage = null);

/// <summary>
/// Settings shared by every file in a batch — picked once via the wizard,
/// applied to all files. Pipeline-specific config lives inside each pipeline
/// instance (e.g. `VideoPipeline` stores the chosen encoder + forced resolution).
/// </summary>
public sealed class BatchContext
{
    public long?        SizeLimit     { get; init; }
    public List<string> CodecPriority { get; init; } = new();
    public string       PresetLabel   { get; init; } = "";
    public string       OutputFolder  { get; init; } = "";
    public AppConfig    Config        { get; init; } = null!;
    public string       FfmpegPath    { get; init; } = "";
}

/// <summary>
/// One media type's compression strategy. The dispatcher routes each input
/// file to the first pipeline whose `CanHandle` returns true.
///
/// Pipelines own their own configuration questions (encoder, quality params, etc.)
/// via `ConfigureForBatchAsync`, called once before the batch runs.
/// </summary>
public interface IMediaPipeline
{
    /// <summary>Display name for logs and the wizard summary.</summary>
    string Name { get; }

    /// <summary>Lowercase extensions including the dot, e.g. [".mp4", ".mkv"].</summary>
    string[] SupportedExtensions { get; }

    /// <summary>Return true if this pipeline should process `filePath`.</summary>
    bool CanHandle(string filePath);

    /// <summary>
    /// Ask the user any cross-batch questions (e.g. "which encoder for all videos?").
    /// Return false to abort the whole batch (user cancelled).
    /// Called once before any file is processed, after extensions have been classified.
    /// </summary>
    Task<bool> ConfigureForBatchAsync(BatchContext ctx, List<string> filesForThisPipeline);

    /// <summary>Process one file. The pipeline picks the output path.</summary>
    Task<MediaResult> ProcessAsync(string src, BatchContext ctx, Logger log);
}
