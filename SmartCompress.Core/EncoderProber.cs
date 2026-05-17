using System.Diagnostics;
using Spectre.Console;

namespace SmartCompress.Core;

public static class EncoderProber
{
    public static HashSet<string> ProbeAvailableEncoders(string ffmpegPath)
    {
        var output = RunProcess(ffmpegPath, ["-encoders", "-v", "quiet"]);
        var found  = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length >= 6 && parts[0][0] == 'V')
                found.Add(parts[1]);
        }
        return found;
    }

    private static bool TestEncoder(string ffmpegPath, string encoder)
    {
        var isVaapi  = encoder.Contains("vaapi");
        var isVulkan = encoder.Contains("vulkan");
        var isHw     = isVaapi || isVulkan;

        var baseArgs = new List<string>
        {
            "-y", "-f", "lavfi",
            "-i", "color=black:size=256x144:rate=30:duration=0.5",
        };

        if (isVaapi)
            baseArgs.AddRange(["-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-filter_hw_device", "va"]);
        else if (isVulkan)
            baseArgs.AddRange(["-init_hw_device", "vulkan=vk:0", "-filter_hw_device", "vk"]);

        if (isHw)
            baseArgs.AddRange(["-vf", "format=nv12,hwupload"]);
        else
            baseArgs.AddRange(["-pix_fmt", "yuv420p"]);

        baseArgs.AddRange(["-c:v", encoder, "-f", "null", "-"]);

        var psi = BuildPsi(ffmpegPath, baseArgs.ToArray());
        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        proc.WaitForExit();
        stdoutTask.Wait();
        return proc.ExitCode == 0;
    }

    public static List<EncoderEntry> ProbeWorkingEncoders(string ffmpegPath, AppConfig config)
    {
        var available = ProbeAvailableEncoders(ffmpegPath);
        var candidates = config.EncoderCandidates
            .SelectMany(kvp => kvp.Value.Select(enc => (family: kvp.Key, enc)))
            .Where(x => available.Contains(x.enc))
            .ToList();

        var result = new List<EncoderEntry>();
        int total  = candidates.Count;
        int i      = 0;

        foreach (var (family, enc) in candidates)
        {
            i++;
            AnsiConsole.Markup($"  [dim][[{i}/{total}]][/]  [cyan]{Markup.Escape($"{enc,-16}")}[/]  ");
            bool ok = TestEncoder(ffmpegPath, enc);
            AnsiConsole.MarkupLine(ok ? "[green]OK[/]" : "[dim]failed - skipping[/]");
            if (ok)
            {
                config.EncoderInfo.TryGetValue(enc, out var info);
                result.Add(new EncoderEntry(
                    enc, family,
                    info?.Label ?? enc,
                    info?.Desc  ?? "",
                    config.GetEncoderSpeed(enc)
                ));
            }
        }
        return result;
    }

    public static string? PickRecommended(List<EncoderEntry> working, List<string> codecPriority)
    {
        foreach (var family in codecPriority)
        {
            var enc = working.FirstOrDefault(e => e.Family == family && e.Speed == EncoderSpeed.Fast);
            if (enc != null) return enc.Name;
        }
        foreach (var family in codecPriority)
        {
            var enc = working.FirstOrDefault(e => e.Family == family);
            if (enc != null) return enc.Name;
        }
        return working.FirstOrDefault()?.Name;
    }

    private static string RunProcess(string exe, string[] args)
    {
        var psi = BuildPsi(exe, args);
        using var proc = Process.Start(psi)!;
        var outputTask = proc.StandardOutput.ReadToEndAsync();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return outputTask.Result;
    }

    private static ProcessStartInfo BuildPsi(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
