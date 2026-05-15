namespace SmartCompress.Core;

/// <summary>
/// Expands a user-supplied path into a flat list of candidate files.
/// A file becomes a one-item list. A folder is walked.
/// Filtering against pipeline extensions happens later in `MediaDispatcher.Classify`,
/// so this stage just enumerates — it doesn't decide what counts as "media."
/// </summary>
public static class BatchCollector
{
    // Walk this many directory levels deep into a folder. Two is enough to cover
    // typical "Clips/2024/" or "Replays/<game>/" layouts without pulling in
    // unrelated stuff from deep project trees the user happened to point at.
    public const int MaxDepth = 2;

    public record CollectResult(
        List<string> Files,
        bool         WasFolder,
        long         TotalBytes);

    public static CollectResult Collect(string path)
    {
        if (File.Exists(path))
        {
            long size = new FileInfo(path).Length;
            return new CollectResult(new() { path }, WasFolder: false, TotalBytes: size);
        }

        if (Directory.Exists(path))
        {
            var files = new List<string>();
            Walk(path, depth: 0, files);
            // Sort by full path so batches process in predictable order — matches
            // what the user sees in their file explorer and makes log diffs sane.
            files.Sort(StringComparer.OrdinalIgnoreCase);
            long total = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            return new CollectResult(files, WasFolder: true, TotalBytes: total);
        }

        return new CollectResult(new(), WasFolder: false, TotalBytes: 0);
    }

    private static void Walk(string dir, int depth, List<string> sink)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                sink.Add(f);

            if (depth >= MaxDepth) return;

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                // Skip hidden / system dirs and common noise.
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name.StartsWith('_')) continue;
                if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("out",          StringComparison.OrdinalIgnoreCase)) continue;
                Walk(sub, depth + 1, sink);
            }
        }
        catch { /* unreadable subtree — skip silently */ }
    }
}
