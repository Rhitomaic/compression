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

    public static bool TestEncoder(string ffmpegPath, string encoder)
    {
        var psi = BuildPsi(ffmpegPath,
        [
            "-y", "-f", "lavfi",
            "-i", "color=black:size=256x144:rate=30:duration=0.5",
            "-pix_fmt", "yuv420p", "-c:v", encoder, "-f", "null", "-"
        ]);
        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        proc.StandardError.ReadToEnd();
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
