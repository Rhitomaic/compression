using System.Diagnostics;
using System.Globalization;
using FFMpegCore;

namespace SmartCompress.Core;

public static class VideoAnalyzer
{
    public static async Task<VideoInfo> GetVideoInfoAsync(string filePath)
    {
        var info = await FFProbe.AnalyseAsync(filePath);
        var video = info.VideoStreams.FirstOrDefault()
            ?? throw new InvalidOperationException("No video stream found in file.");

        int width  = video.Width;
        int height = video.Height;
        double fps = video.FrameRate;
        double durationS = info.Duration.TotalSeconds;
        long bitrateBps  = (long)info.Format.BitRate;
        int bitrateKbps  = (int)(bitrateBps / 1000);

        double bppf = (width * height * fps) > 0
            ? bitrateBps / (double)(width * height * fps)
            : 0;

        string complexity, hint;
        if (bppf < 0.05)
        {
            complexity = "Simple";
            hint       = "clean content, compresses very well";
        }
        else if (bppf < 0.15)
        {
            complexity = "Medium";
            hint       = "typical game / screen recording";
        }
        else
        {
            complexity = "Complex";
            hint       = "high motion, grain, or fine detail — harder to compress";
        }

        return new VideoInfo(
            width, height,
            Math.Round(fps, 2),
            bitrateKbps,
            Math.Round(durationS, 1),
            Math.Round(bppf, 4),
            complexity, hint
        );
    }

    // Build an adaptive complexity profile from the video's packet sizes.
    // Returns null when the container doesn't expose usable timing data.
    // width/height are required to compute absolute bits-per-pixel-per-frame,
    // which makes the profile comparable across clips of different resolution.
    public static async Task<ComplexityProfile?> GetComplexityProfileAsync(
        string filePath, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        // Try PTS first (accurate display order).  Many containers omit PTS and
        // store only DTS, so fall back to dts_time when pts_time yields nothing.
        var packets = await ReadPackets(filePath, "pts_time");
        if (packets.Count < 10)
            packets = await ReadPackets(filePath, "dts_time");

        if (packets.Count < 10) return null;

        // Sort by timestamp — ffprobe emits in decode order, not display order.
        packets.Sort((a, b) => a.ts.CompareTo(b.ts));

        // Cap to keep memory and processing time bounded for long or high-fps recordings.
        // 30 K packets covers 5 min at 100 fps; anything longer is sampled uniformly.
        const int MaxPackets = 30_000;
        if (packets.Count > MaxPackets)
        {
            var sampled = new List<(double ts, long size)>(MaxPackets);
            for (int i = 0; i < MaxPackets; i++)
                sampled.Add(packets[(int)((long)i * packets.Count / MaxPackets)]);
            packets = sampled;
        }

        return BuildAdaptiveProfile(packets, width, height);
    }

    // ── Packet reader ─────────────────────────────────────────────────────────

    private static async Task<List<(double ts, long size)>> ReadPackets(
        string filePath, string timestampField)
    {
        var ffprobePath = Path.Combine(
            GlobalFFOptions.Current.BinaryFolder,
            OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

        var psi = new ProcessStartInfo(ffprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in new[]
        {
            "-v", "quiet",
            "-select_streams", "v:0",
            "-show_entries", $"packet={timestampField},size",
            "-of", "csv=p=0",
            filePath
        })
            psi.ArgumentList.Add(a);

        using var proc     = Process.Start(psi)!;
        var stderrTask     = proc.StandardError.ReadToEndAsync();
        var packets        = new List<(double ts, long size)>();

        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
        {
            var parts = line.Split(',');
            if (parts.Length >= 2
                && double.TryParse(parts[0], NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out var ts)
                && long.TryParse(parts[1], out var sz)
                && sz > 0)
                packets.Add((ts, sz));
        }

        await proc.WaitForExitAsync();
        await stderrTask;
        return packets;
    }

    // ── Adaptive profile builder ──────────────────────────────────────────────

    private static ComplexityProfile? BuildAdaptiveProfile(
        List<(double ts, long size)> packets, int width, int height)
    {
        if (packets.Count < 10) return null;

        double startTs   = packets[0].ts;
        double totalSpan = packets[^1].ts - startTs;
        if (totalSpan <= 0) return null;

        double pixels = (double)width * height;
        if (pixels <= 0) return null;

        // Global mean bppf is only used as a relative reference for the split
        // decision (where in this clip are the "hot" sections). Segment values
        // themselves are stored as ABSOLUTE bppf so they're comparable across clips.
        double globalMeanBppf = (packets.Sum(p => (double)p.size) * 8.0)
                              / (pixels * packets.Count);
        if (globalMeanBppf <= 0) return null;

        const double BaseWindowSec  = 1.0;
        const double MinWindowSec   = 0.5;
        const float  SplitThreshold = 1.3f;

        var segments = new List<AdaptiveSegment>();

        for (double t = 0; t < totalSpan; t += BaseWindowSec)
        {
            double wEnd = Math.Min(t + BaseWindowSec, totalSpan);
            SplitWindow(packets, startTs, t, wEnd, pixels,
                        globalMeanBppf, MinWindowSec, SplitThreshold, segments);
        }

        if (segments.Count == 0) return null;
        return ComplexityProfile.FromSegments(segments.ToArray(), totalSpan);
    }

    // Recursively split a window when its bppf is above the global average × threshold.
    // Stored segment values are absolute bits-per-pixel-per-frame so they remain
    // comparable across clips of different resolution and source quality.
    private static void SplitWindow(
        List<(double ts, long size)> packets,
        double startTs,
        double winStart, double winEnd,
        double pixels,
        double globalMeanBppf,
        double minWindowSec, float splitThreshold,
        List<AdaptiveSegment> result)
    {
        double duration = winEnd - winStart;

        // Collect sizes for packets in [winStart, winEnd) relative to startTs.
        // Packets are sorted by ts so we can break early once we pass winEnd.
        var sizes = new List<double>();
        foreach (var (ts, size) in packets)
        {
            double rel = ts - startTs;
            if (rel >= winEnd)   break;   // early exit: list is sorted
            if (rel >= winStart) sizes.Add(size);
        }

        if (sizes.Count < 2 || duration <= minWindowSec)
        {
            float bppf = sizes.Count > 0
                ? (float)((sizes.Sum() * 8.0) / (pixels * sizes.Count))
                : 0f;
            result.Add(new AdaptiveSegment((float)winStart, (float)duration, bppf));
            return;
        }

        double meanBppf = (sizes.Sum() * 8.0) / (pixels * sizes.Count);

        if (meanBppf <= splitThreshold * globalMeanBppf)
        {
            result.Add(new AdaptiveSegment((float)winStart, (float)duration, (float)meanBppf));
            return;
        }

        double mid = (winStart + winEnd) / 2;
        SplitWindow(packets, startTs, winStart, mid, pixels, globalMeanBppf, minWindowSec, splitThreshold, result);
        SplitWindow(packets, startTs, mid, winEnd, pixels, globalMeanBppf, minWindowSec, splitThreshold, result);
    }
}
