using System.Text.Json;
using Spectre.Console;

namespace SmartCompress.Core;

public record ValidationBucket(
    string Encoder,
    int?   ScaleHeight,
    int    Count,
    double MedianResidual,
    double MaxAbsResidual,
    double Rmse);

/// <summary>
/// Honest accuracy assessment: hide each clip's entries from the predictor,
/// ask the predictor to recover their CQPs from OTHER clips only, measure
/// how far off it is. This is the "would my prediction have been right?"
/// question, answered without any per-clip cheating.
/// </summary>
public static class BenchmarkValidator
{
    public static List<ValidationBucket> CrossValidate()
    {
        var entries = LoadBenchmarkEntries();
        if (entries.Count == 0) return new();

        // Group by clip — same MeanComplexity + Duration is a robust proxy for
        // "same source video" since both are content fingerprints.
        var clipGroups = entries
            .Where(e => e.MeanComplexity.HasValue && e.MeanComplexity.Value > 0)
            .GroupBy(e => $"{e.MeanComplexity!.Value:F5}|{e.DurationSec}")
            .ToList();

        var bucketResiduals = new Dictionary<string, List<double>>();

        foreach (var clip in clipGroups)
        {
            var heldOut  = clip.ToList();
            var heldOutSet = new HashSet<CacheEntry>(heldOut);

            // Remaining cache excludes everything from this clip, so the predictor
            // can't accidentally use a sibling entry as a near-perfect reference.
            var remaining = entries.Where(e => !heldOutSet.Contains(e)).ToList();

            foreach (var entry in heldOut)
            {
                if (entry.OutputBytes is null || entry.OutputBytes.Value <= 0) continue;
                if (entry.Segments is null || entry.Segments.Length == 0)     continue;

                var profile = ComplexityProfile.FromSegments(entry.Segments, entry.DurationSec);

                var pred = CqpCache.PredictFromEntries(
                    remaining, profile,
                    entry.Encoder, entry.OutputBytes, entry.ScaleHeight,
                    entry.DurationSec);

                if (pred is null) continue;

                double residual = entry.Cqp - pred.Value.Cqp;  // +ve = predictor undershot
                string bucket = $"{entry.Encoder}|{entry.ScaleHeight?.ToString() ?? "native"}";
                if (!bucketResiduals.TryGetValue(bucket, out var list))
                    bucketResiduals[bucket] = list = new List<double>();
                list.Add(residual);
            }
        }

        return bucketResiduals
            .Select(kv =>
            {
                var parts = kv.Key.Split('|');
                int? scale = parts[1] == "native" ? (int?)null : int.Parse(parts[1]);
                var rs = kv.Value;
                rs.Sort();
                double median = rs[rs.Count / 2];
                double maxAbs = rs.Max(Math.Abs);
                double rmse   = Math.Sqrt(rs.Sum(r => r * r) / rs.Count);
                return new ValidationBucket(parts[0], scale, rs.Count, median, maxAbs, rmse);
            })
            .OrderBy(b => b.Encoder)
            .ThenBy(b => b.ScaleHeight ?? int.MaxValue)
            .ToList();
    }

    public static void PrintReport(List<ValidationBucket> buckets)
    {
        if (buckets.Count == 0)
        {
            AnsiConsole.MarkupLine("  [dim]Not enough data for cross-validation report (need ≥2 clips per encoder).[/]");
            return;
        }

        AnsiConsole.MarkupLine("[bold]Predictor accuracy (hold-one-clip-out cross-validation):[/]");
        Console.WriteLine();

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey39);
        table.AddColumn("[dim]Encoder[/]");
        table.AddColumn(new TableColumn("[dim]Scale[/]").Centered());
        table.AddColumn(new TableColumn("[dim]N[/]").Centered());
        table.AddColumn(new TableColumn("[dim]Median[/]").RightAligned());
        table.AddColumn(new TableColumn("[dim]RMSE[/]").RightAligned());
        table.AddColumn(new TableColumn("[dim]Worst[/]").RightAligned());

        foreach (var b in buckets)
        {
            // Color RMSE: ≤1 great, ≤2 ok, >2 wide spread.
            string rmseColor = b.Rmse <= 1.0 ? "green"
                             : b.Rmse <= 2.0 ? "yellow" : "red";
            string medianColor = Math.Abs(b.MedianResidual) <= 0.5 ? "green"
                               : Math.Abs(b.MedianResidual) <= 1.5 ? "yellow" : "red";

            string scaleStr = b.ScaleHeight.HasValue ? $"{b.ScaleHeight}p" : "native";

            table.AddRow(
                $"[cyan]{Markup.Escape(b.Encoder)}[/]",
                $"[dim]{Markup.Escape(scaleStr)}[/]",
                b.Count.ToString(),
                $"[{medianColor}]{b.MedianResidual:+0.0;-0.0;0.0}[/]",
                $"[{rmseColor}]{b.Rmse:F2}[/]",
                $"{b.MaxAbsResidual:F0}");
        }
        AnsiConsole.Write(table);

        Console.WriteLine();
        AnsiConsole.MarkupLine("  [dim]Median = systematic bias (close to 0 is best).[/]");
        AnsiConsole.MarkupLine("  [dim]RMSE  = typical prediction error (close to 0 is best).[/]");
        AnsiConsole.MarkupLine("  [dim]Worst = largest single-prediction miss across the bucket.[/]");
    }

    // ── Storage reader (decoupled from CqpCache internals) ───────────────────

    private static List<CacheEntry> LoadBenchmarkEntries()
    {
        try
        {
            if (!File.Exists(AppPaths.BenchmarkCacheFile)) return new();
            var dict = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(
                File.ReadAllText(AppPaths.BenchmarkCacheFile));
            return dict?.Values.ToList() ?? new();
        }
        catch { return new(); }
    }
}
