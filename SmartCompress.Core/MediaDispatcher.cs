namespace SmartCompress.Core;

/// <summary>
/// Routes input files to the right `IMediaPipeline`. First-match wins, so
/// register more specific pipelines before fallbacks if there's ever overlap.
/// </summary>
public sealed class MediaDispatcher
{
    private readonly List<IMediaPipeline> _pipelines;

    public MediaDispatcher(IEnumerable<IMediaPipeline> pipelines)
    {
        _pipelines = pipelines.ToList();
    }

    public IReadOnlyList<IMediaPipeline> All => _pipelines;

    public IMediaPipeline? Resolve(string filePath) =>
        _pipelines.FirstOrDefault(p => p.CanHandle(filePath));

    /// <summary>
    /// Group input files by which pipeline (if any) handles them.
    /// Files matching no pipeline go into the `Unhandled` bucket — useful for
    /// telling the user "I'm skipping these 3 files" instead of silently dropping.
    /// </summary>
    public DispatchPlan Classify(IEnumerable<string> files)
    {
        var byPipeline = _pipelines.ToDictionary(p => p, _ => new List<string>());
        var unhandled  = new List<string>();

        foreach (var f in files)
        {
            var pipe = Resolve(f);
            if (pipe != null) byPipeline[pipe].Add(f);
            else              unhandled.Add(f);
        }

        return new DispatchPlan(byPipeline, unhandled);
    }
}

public sealed record DispatchPlan(
    Dictionary<IMediaPipeline, List<string>> ByPipeline,
    List<string>                             Unhandled)
{
    public int HandledCount   => ByPipeline.Values.Sum(l => l.Count);
    public int UnhandledCount => Unhandled.Count;
    public int TotalCount     => HandledCount + UnhandledCount;
}
