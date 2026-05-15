namespace SmartCompress.Core;

/// <summary>
/// Centralises all path decisions so the rest of the app never has to
/// know whether it's running portable (debug / USB) or installed.
///
/// Portable:  everything lives next to the exe (current debug behaviour).
/// Installed: user data goes to %LOCALAPPDATA%\SmartCompress,
///            default output goes to %USERPROFILE%\Videos\SmartCompress.
///
/// Drop a "portable.flag" file next to the exe to force portable mode
/// even when installed to Program Files.
/// </summary>
public static class AppPaths
{
    public static readonly bool IsPortable;

    static AppPaths()
    {
        var exeDir = AppContext.BaseDirectory;
        var pf     = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        bool underProgramFiles =
            (!string.IsNullOrEmpty(pf)   && exeDir.StartsWith(pf,   StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(pf86) && exeDir.StartsWith(pf86, StringComparison.OrdinalIgnoreCase));

        IsPortable = File.Exists(Path.Combine(exeDir, "portable.flag")) || !underProgramFiles;
    }

    // ── Directories ───────────────────────────────────────────────────────────

    /// <summary>Writable folder for user-specific data (cache, logs).</summary>
    public static string DataDir => IsPortable
        ? AppContext.BaseDirectory
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartCompress");

    /// <summary>Default output folder shown to the user in the wizard.</summary>
    public static string DefaultOutputDir => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "out")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "SmartCompress");

    /// <summary>Log directory.</summary>
    public static string LogDir => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "out")
        : Path.Combine(DataDir, "logs");

    // ── Files ─────────────────────────────────────────────────────────────────

    public static string CacheFile          => Path.Combine(DataDir, "cqp_cache.json");
    public static string BenchmarkCacheFile => Path.Combine(DataDir, "benchmark_cache.json");
    public static string FfmpegDir          => Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    public static string ConfigFile         => Path.Combine(AppContext.BaseDirectory, "config.json");
}
