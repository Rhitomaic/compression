namespace SmartCompress.Core;
using System.Diagnostics;

public static class FfmpegLocator
{
    public static string GetFfmpegPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(AppPaths.FfmpegDir, "ffmpeg.exe");
        }

        var systemPath = TryFindSystemFfmpeg();
        if (!string.IsNullOrWhiteSpace(systemPath))
            return systemPath;

        return Path.Combine(AppPaths.FfmpegDir, "ffmpeg");
    }

    private static string? TryFindSystemFfmpeg()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "whereis",
                Arguments = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);

            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                if (part.EndsWith("/ffmpeg"))
                    return part;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }
}