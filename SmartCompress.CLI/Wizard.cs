using System.Globalization;
using SmartCompress.Core;
using Spectre.Console;

namespace SmartCompress;

internal static class Wizard
{
    public static void Divider() =>
        AnsiConsole.Write(new Rule().RuleStyle("grey dim"));

    /// <summary>
    /// `Console.ReadLine` that first drains any buffered keystrokes the user
    /// typed during the previous encode. Prevents accidentally skipping an
    /// important prompt because they hit Enter while a progress bar was up.
    /// Returns the empty string if input is closed (Ctrl+Z / piped EOF).
    /// </summary>
    public static string ReadLineDrained()
    {
        if (!Console.IsInputRedirected)
        {
            try
            {
                while (Console.KeyAvailable)
                    Console.ReadKey(intercept: true);
            }
            catch { /* terminal can't peek — skip */ }
        }
        return Console.ReadLine() ?? "";
    }

    private static string Ask(string prompt, string @default = "")
    {
        var hint = @default.Length > 0 ? $" [dim](default: {Markup.Escape(@default)})[/]" : "";
        AnsiConsole.MarkupLine($"  {Markup.Escape(prompt)}{hint}:");
        AnsiConsole.Markup("  [dim]>[/] ");
        var val = ReadLineDrained().Trim().Trim('"').Trim('\'');
        return val.Length > 0 ? val : @default;
    }

    private static string CleanPath(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("& ")) raw = raw[2..];
        return raw.Trim('"').Trim('\'');
    }

    // ── Step 1: Input file or folder ─────────────────────────────────────────

    /// <summary>
    /// Accepts either a single file OR a directory. The dispatcher figures
    /// out later which collected files are actually supported media.
    /// Returns the original raw path (caller passes it through `BatchCollector`).
    /// </summary>
    public static string StepInput()
    {
        AnsiConsole.MarkupLine("[bold cyan]STEP 1[/] [bold]— Input[/]");
        AnsiConsole.MarkupLine("  [dim]Drag & drop a file OR folder into this window, or paste the path.[/]");
        AnsiConsole.MarkupLine("  [dim]Folders are scanned (up to 2 levels deep) and unsupported files are skipped.[/]\n");
        while (true)
        {
            AnsiConsole.Markup("  [dim]>[/] ");
            var path = CleanPath(ReadLineDrained());
            if (File.Exists(path) || Directory.Exists(path)) return path;
            AnsiConsole.MarkupLine("\n  [red][[!]] Can't find that file or folder — try again.[/]\n");
        }
    }

    // ── Step 2: Preset ────────────────────────────────────────────────────────

    public static (long? SizeLimit, List<string> CodecPriority, string PresetLabel) StepPreset(AppConfig config)
    {
        AnsiConsole.MarkupLine("[bold cyan]STEP 2[/] [bold]— Target preset[/]");
        AnsiConsole.MarkupLine("  [dim]What are you compressing for?[/]\n");

        var presets = config.Presets;
        foreach (var p in presets)
            AnsiConsole.MarkupLine($"  [dim][[{p.Key}]][/]  {Markup.Escape(p.Label)}");
        Console.WriteLine();

        var presetMap = presets.ToDictionary(p => p.Key);
        Preset chosen = default!;
        while (true)
        {
            AnsiConsole.Markup("  [dim]>[/] ");
            var key = ReadLineDrained().Trim();
            if (presetMap.TryGetValue(key, out chosen!)) break;
            AnsiConsole.MarkupLine($"  [red][[!]] Enter a number 1–{presets.Count}.[/]\n");
        }

        long? sizeLimit = chosen.SizeMb.HasValue ? (long)(chosen.SizeMb.Value * 1_048_576) : null;
        var codecs = new List<string>(chosen.Codecs);
        var label  = chosen.Label;

        if (chosen.Id == "custom")
        {
            Console.WriteLine();
            while (true)
            {
                AnsiConsole.MarkupLine("  [dim]Custom size limit in MB (or leave blank for none):[/]");
                AnsiConsole.Markup("  [dim]>[/] ");
                var raw = ReadLineDrained().Trim();
                if (raw.Length == 0) { sizeLimit = null; break; }
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb))
                {
                    sizeLimit = (long)(mb * 1_048_576);
                    label     = $"Custom ({raw} MB)";
                    break;
                }
                AnsiConsole.MarkupLine("  [red][[!]] Enter a number like 50[/]\n");
            }
        }

        return (sizeLimit, codecs, label);
    }

    // ── Step 3: Encoder ───────────────────────────────────────────────────────

    public static string? StepEncoder(List<EncoderEntry> working, string? recommended)
    {
        AnsiConsole.MarkupLine("[bold cyan]STEP 3[/] [bold]— Encoder[/]");
        AnsiConsole.MarkupLine("  [dim]Choose which encoder to use. Press Enter for the recommended option.[/]\n");

        if (working.Count == 0) return null;

        for (int i = 0; i < working.Count; i++)
        {
            var enc      = working[i];
            var isRec    = enc.Name == recommended;
            var labelColor = enc.Speed switch
            {
                EncoderSpeed.VerySlow => "red",
                EncoderSpeed.Slow     => "yellow",
                _                     => "green"
            };
            var slowTag = enc.Speed switch
            {
                EncoderSpeed.VerySlow => "  [bold red][[!]] very slow[/]",
                EncoderSpeed.Slow     => "  [dim yellow][[slow]][/]",
                _                     => ""
            };
            var recTag = isRec ? "  [bold green]← recommended[/]" : "";

            AnsiConsole.MarkupLine(
                $"  [dim][[{i + 1}]][/]  [{labelColor}]{Markup.Escape(enc.Label)}[/]{recTag}{slowTag}");
            AnsiConsole.MarkupLine($"       [dim]{Markup.Escape(enc.Desc)}[/]");
            Console.WriteLine();
        }

        int defaultIdx = working.FindIndex(e => e.Name == recommended);
        if (defaultIdx < 0) defaultIdx = 0;

        while (true)
        {
            AnsiConsole.MarkupLine($"  [dim](default: {defaultIdx + 1})[/]");
            AnsiConsole.Markup("  [dim]>[/] ");
            var raw = ReadLineDrained().Trim();
            if (raw.Length == 0) return working[defaultIdx].Name;

            if (int.TryParse(raw, out var idx) && idx >= 1 && idx <= working.Count)
            {
                var enc = working[idx - 1];
                if (enc.Speed == EncoderSpeed.VerySlow)
                {
                    Console.WriteLine();
                    AnsiConsole.MarkupLine("  [bold red][[!]] This encoder is extremely slow.[/]");
                    AnsiConsole.MarkupLine("      [dim]A 10-minute video can easily take several hours.[/]");
                    Console.WriteLine();
                    AnsiConsole.Markup("  Are you sure? [dim][[y/N]][/]: ");
                    if (ReadLineDrained().Trim().ToLowerInvariant() != "y")
                    { Console.WriteLine(); continue; }
                }
                return enc.Name;
            }
            AnsiConsole.MarkupLine($"  [red][[!]] Enter a number 1-{working.Count}, or press Enter for the default.[/]\n");
        }
    }

    // ── Step 4: Resolution ────────────────────────────────────────────────────

    public static int StepResolution(int srcHeight, string complexity, AppConfig config)
    {
        AnsiConsole.MarkupLine("[bold cyan]STEP 4[/] [bold]— Resolution[/]");
        AnsiConsole.MarkupLine("  [dim]Choose an output resolution, or Auto to let the tool decide.[/]\n");

        var resInfo = config.ResolutionInfo;
        var steps   = config.ResolutionSteps;

        var options = new List<int> { 0 };
        if (!steps.Contains(srcHeight)) options.Add(srcHeight);
        foreach (var h in steps)
            if (h <= srcHeight) options.Add(h);

        int recommended = (complexity == "Complex" && srcHeight >= 1080)
            ? steps.FirstOrDefault(h => h < srcHeight)
            : 0;

        for (int i = 0; i < options.Count; i++)
        {
            int res = options[i];
            string label, desc;
            if (res == 0)        { label = "Auto";                desc = resInfo.GetValueOrDefault("auto", ""); }
            else if (res == srcHeight) { label = $"{res}p (original)"; desc = resInfo.GetValueOrDefault(res.ToString(), "") ?? "Your source resolution."; }
            else                 { label = $"{res}p";             desc = resInfo.GetValueOrDefault(res.ToString(), ""); }

            var recTag = res == recommended ? "  [bold green]← recommended[/]" : "";
            AnsiConsole.MarkupLine($"  [dim][[{i + 1}]][/]  [white]{Markup.Escape(label)}[/]{recTag}");
            AnsiConsole.MarkupLine($"       [dim]{Markup.Escape(desc ?? "")}[/]");
            Console.WriteLine();
        }

        int defaultIdx = options.IndexOf(recommended);
        if (defaultIdx < 0) defaultIdx = 0;

        while (true)
        {
            AnsiConsole.MarkupLine($"  [dim](default: {defaultIdx + 1})[/]");
            AnsiConsole.Markup("  [dim]>[/] ");
            var raw = ReadLineDrained().Trim();
            if (raw.Length == 0) return options[defaultIdx];
            if (int.TryParse(raw, out var idx) && idx >= 1 && idx <= options.Count)
                return options[idx - 1];
            AnsiConsole.MarkupLine($"  [red][[!]] Enter a number 1-{options.Count}, or press Enter for the default.[/]\n");
        }
    }

    // ── Step: Output folder only (batch mode — pipelines pick filenames) ─────

    /// <summary>
    /// Asks for an output folder only. Pipelines derive per-file output names
    /// from each input. Falls back to env var or platform default if blank.
    /// </summary>
    public static string StepOutputFolder()
    {
        AnsiConsole.MarkupLine("[bold cyan]Output folder[/]");
        var scOutDir = Environment.GetEnvironmentVariable("SC_OUT_DIR");
        var defaultFolder = scOutDir is not null
            ? Path.Combine(scOutDir, "out")
            : AppPaths.DefaultOutputDir;
        var rawFolder = Ask("Folder", defaultFolder);
        var outFolder = CleanPath(rawFolder);
        Directory.CreateDirectory(outFolder);
        return outFolder;
    }

    // ── Steps 5 + 6: Output filename and folder (single-file legacy path) ────

    public static (string FileName, string Folder) StepOutput(string inputPath)
    {
        var stem        = Path.GetFileNameWithoutExtension(inputPath);
        var defaultName = $"{stem}_compressed.mp4";

        AnsiConsole.MarkupLine("[bold cyan]STEP 5[/] [bold]— Output filename[/]");
        var outName = Ask("Filename", defaultName);
        if (!outName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) outName += ".mp4";

        Divider();
        Console.WriteLine();

        AnsiConsole.MarkupLine("[bold cyan]STEP 6[/] [bold]— Output folder[/]");
        var scOutDir      = Environment.GetEnvironmentVariable("SC_OUT_DIR");
        var defaultFolder = scOutDir is not null
            ? Path.Combine(scOutDir, "out")
            : AppPaths.DefaultOutputDir;
        var rawFolder = Ask("Folder", defaultFolder);
        var outFolder = CleanPath(rawFolder);
        Directory.CreateDirectory(outFolder);

        var outPath = Path.Combine(outFolder, outName);
        while (File.Exists(outPath))
        {
            AnsiConsole.MarkupLine($"\n  [yellow][[!]] '{Markup.Escape(outName)}' already exists in that folder.[/]");
            AnsiConsole.MarkupLine("  [dim][[1]][/]  Overwrite it");
            AnsiConsole.MarkupLine("  [dim][[2]][/]  Choose a different name");
            Console.WriteLine();
            AnsiConsole.Markup("  [dim]>[/] ");
            var choice = ReadLineDrained().Trim();
            if (choice == "1") break;
            if (choice == "2")
            {
                var baseName = Path.GetFileNameWithoutExtension(outName);
                if (baseName.EndsWith("_compressed", StringComparison.Ordinal)) baseName = baseName[..^11];
                int counter   = 2;
                var suggested = $"{baseName}_compressed_{counter}.mp4";
                while (File.Exists(Path.Combine(outFolder, suggested)))
                    suggested = $"{baseName}_compressed_{++counter}.mp4";
                outName = Ask("New filename", suggested);
                if (!outName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) outName += ".mp4";
                outPath = Path.Combine(outFolder, outName);
            }
            else AnsiConsole.MarkupLine("  [red][[!]] Enter 1 or 2.[/]\n");
        }

        return (outName, outFolder);
    }
}
