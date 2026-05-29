using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using HighPerfFileCopyLib;

namespace HCopyCompare;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("HCopyCompare is Windows-only because it uses robocopy.");
            return 1;
        }

        if (!TryParseArguments(args, out var parsed, out var error, out var showHelp) || showHelp)
        {
            PrintUsage();
            if (!string.IsNullOrWhiteSpace(error))
                Console.Error.WriteLine(error);
            return string.IsNullOrWhiteSpace(error) ? 0 : 1;
        }

        var source = Path.GetFullPath(parsed.SourceDirectory);
        var destinationBase = Path.GetFullPath(parsed.DestinationBaseDirectory);
        var hcopyDest = Path.Combine(destinationBase, "hcopy");
        var robocopyDest = Path.Combine(destinationBase, "robocopy");

        PrepareDirectory(destinationBase);
        PrepareDirectory(hcopyDest);
        PrepareDirectory(robocopyDest);

        var inventory = CollectInventory(source, parsed.Options.ExcludeDirectoryPatterns, parsed.Options.ExcludeFilePatterns);

        var hcopyResult = await RunHCopyAsync(source, hcopyDest, parsed.Options).ConfigureAwait(false);
        var robocopyResult = await RunRobocopyAsync(source, robocopyDest, parsed.Options, inventory).ConfigureAwait(false);

        var report = new ComparisonReport
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SourceDirectory = source,
            DestinationBaseDirectory = destinationBase,
            HCopyDestination = hcopyDest,
            RobocopyDestination = robocopyDest,
            Inventory = inventory,
            HCopy = hcopyResult,
            Robocopy = robocopyResult,
            UnsupportedRobocopyOptions = GetUnsupportedRobocopyOptions(parsed.Options),
            PerformanceGainPercent = CalculateGainPercent(hcopyResult.Elapsed, robocopyResult.Elapsed),
            SpeedupFactor = CalculateSpeedupFactor(hcopyResult.Elapsed, robocopyResult.Elapsed)
        };

        PrintTextReport(report);
        await WriteJsonAsync(report, parsed.JsonPath).ConfigureAwait(false);
        await WriteCsvAsync(report, parsed.CsvPath).ConfigureAwait(false);

        return hcopyResult.Success && robocopyResult.Success ? 0 : 2;
    }

    private static async Task<ToolRunResult> RunHCopyAsync(string source, string destination, FileCopyOptions options)
    {
        var hcopyOptions = new FileCopyOptions
        {
            SourceDirectory = source,
            DestinationDirectory = destination,
            DegreeOfParallelism = options.DegreeOfParallelism,
            RetryCount = options.RetryCount,
            RetryDelayMs = options.RetryDelayMs,
            ExcludeDirectoryPatterns = new List<string>(options.ExcludeDirectoryPatterns),
            ExcludeFilePatterns = new List<string>(options.ExcludeFilePatterns),
            ChecksumEnabled = options.ChecksumEnabled,
            ChecksumBytes = options.ChecksumBytes,
            VerificationEnabled = options.VerificationEnabled,
            ChecksumAlgorithm = options.ChecksumAlgorithm,
            CopyTimestampsAndAttributes = options.CopyTimestampsAndAttributes
        };

        var commandLine = BuildHCopyCommandLine(source, destination, options);
        var manager = new FileCopyManager(hcopyOptions);

        try
        {
            var sw = Stopwatch.StartNew();
            var stats = await manager.RunAsync(perFileProgress: null, overallProgress: null).ConfigureAwait(false);
            sw.Stop();

            return new ToolRunResult
            {
                Tool = "HCopy",
                CommandLine = commandLine,
                ExitCode = 0,
                Elapsed = sw.Elapsed,
                FilesCopied = stats.FilesTotal,
                BytesCopied = stats.TotalBytes,
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new ToolRunResult
            {
                Tool = "HCopy",
                CommandLine = commandLine,
                ExitCode = 1,
                Elapsed = TimeSpan.Zero,
                FilesCopied = 0,
                BytesCopied = 0,
                Success = false,
                Error = ex.Message
            };
        }
    }

    private static async Task<ToolRunResult> RunRobocopyAsync(string source, string destination, FileCopyOptions options, Inventory inventory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "robocopy",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add(destination);
        psi.ArgumentList.Add("/E");
        psi.ArgumentList.Add("/COPY:DAT");
        psi.ArgumentList.Add("/DCOPY:DAT");
        psi.ArgumentList.Add($"/R:{Math.Max(0, options.RetryCount)}");
        var retryDelaySeconds = options.RetryDelayMs <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(options.RetryDelayMs / 1000d));
        psi.ArgumentList.Add($"/W:{retryDelaySeconds}");
        psi.ArgumentList.Add("/NFL");
        psi.ArgumentList.Add("/NDL");
        psi.ArgumentList.Add("/NJH");
        psi.ArgumentList.Add("/NJS");
        psi.ArgumentList.Add("/NP");

        if (options.DegreeOfParallelism > 1)
            psi.ArgumentList.Add($"/MT:{Math.Min(options.DegreeOfParallelism, 128)}");

        if (options.ExcludeDirectoryPatterns.Count > 0)
        {
            psi.ArgumentList.Add("/XD");
            foreach (var pattern in options.ExcludeDirectoryPatterns)
                psi.ArgumentList.Add(pattern);
        }

        if (options.ExcludeFilePatterns.Count > 0)
        {
            psi.ArgumentList.Add("/XF");
            foreach (var pattern in options.ExcludeFilePatterns)
                psi.ArgumentList.Add(pattern);
        }

        var commandLine = BuildDisplayCommandLine(psi.ArgumentList);

        try
        {
            using var process = new Process { StartInfo = psi };
            var sw = Stopwatch.StartNew();

            if (!process.Start())
                throw new InvalidOperationException("Failed to start robocopy.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            sw.Stop();

            return new ToolRunResult
            {
                Tool = "Robocopy",
                CommandLine = commandLine,
                ExitCode = process.ExitCode,
                Elapsed = sw.Elapsed,
                FilesCopied = inventory.Files,
                BytesCopied = inventory.Bytes,
                Success = process.ExitCode < 8
            };
        }
        catch (Win32Exception ex)
        {
            return new ToolRunResult
            {
                Tool = "Robocopy",
                CommandLine = commandLine,
                ExitCode = -1,
                Elapsed = TimeSpan.Zero,
                FilesCopied = inventory.Files,
                BytesCopied = inventory.Bytes,
                Success = false,
                Error = ex.Message
            };
        }
        catch (Exception ex)
        {
            return new ToolRunResult
            {
                Tool = "Robocopy",
                CommandLine = commandLine,
                ExitCode = -1,
                Elapsed = TimeSpan.Zero,
                FilesCopied = inventory.Files,
                BytesCopied = inventory.Bytes,
                Success = false,
                Error = ex.Message
            };
        }
    }

    private static Inventory CollectInventory(string root, List<string> excludeDirs, List<string> excludeFiles)
    {
        var dirMatchers = excludeDirs.Select(static p => new WildcardMatcher(p)).ToArray();
        var fileMatchers = excludeFiles.Select(static p => new WildcardMatcher(p)).ToArray();

        long files = 0;
        long bytes = 0;
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (var sub in subDirs)
            {
                if (IsExcluded(Path.GetFileName(sub), dirMatchers))
                    continue;

                stack.Push(sub);
            }

            IEnumerable<string> filePaths;
            try { filePaths = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var file in filePaths)
            {
                if (IsExcluded(Path.GetFileName(file), fileMatchers))
                    continue;

                files++;
                try { bytes += new FileInfo(file).Length; } catch { }
            }
        }

        return new Inventory(files, bytes);
    }

    private static bool IsExcluded(string text, WildcardMatcher[] matchers)
    {
        for (var i = 0; i < matchers.Length; i++)
        {
            if (matchers[i].IsMatch(text))
                return true;
        }

        return false;
    }

    private static string BuildHCopyCommandLine(string source, string destination, FileCopyOptions options)
    {
        var parts = new List<string>
        {
            Quote(source),
            Quote(destination)
        };

        if (options.DegreeOfParallelism != 4) parts.Add($"-t {options.DegreeOfParallelism}");
        if (options.RetryCount != 3) parts.Add($"-r {options.RetryCount}");
        if (options.RetryDelayMs != 1000) parts.Add($"-w {options.RetryDelayMs}");
        foreach (var p in options.ExcludeDirectoryPatterns) parts.Add($"-exd {Quote(p)}");
        foreach (var p in options.ExcludeFilePatterns) parts.Add($"-exf {Quote(p)}");
        if (options.ChecksumBytes > 0) parts.Add($"-cbytes {options.ChecksumBytes}");
        if (options.VerificationEnabled) parts.Add("-verify");
        if (!string.Equals(options.ChecksumAlgorithm, "MD5", StringComparison.OrdinalIgnoreCase)) parts.Add($"-alg {options.ChecksumAlgorithm}");

        return $"HCopy {string.Join(' ', parts)}";
    }

    private static List<string> GetUnsupportedRobocopyOptions(FileCopyOptions options)
    {
        var unsupported = new List<string>();
        if (options.ChecksumBytes > 0) unsupported.Add("-cbytes");
        if (options.VerificationEnabled) unsupported.Add("-verify");
        if (!string.Equals(options.ChecksumAlgorithm, "MD5", StringComparison.OrdinalIgnoreCase)) unsupported.Add("-alg");
        return unsupported;
    }

    private static double? CalculateGainPercent(TimeSpan hcopy, TimeSpan robocopy)
    {
        if (hcopy <= TimeSpan.Zero || robocopy <= TimeSpan.Zero)
            return null;

        return ((robocopy.TotalMilliseconds - hcopy.TotalMilliseconds) / robocopy.TotalMilliseconds) * 100.0;
    }

    private static double? CalculateSpeedupFactor(TimeSpan hcopy, TimeSpan robocopy)
    {
        if (hcopy <= TimeSpan.Zero || robocopy <= TimeSpan.Zero)
            return null;

        return robocopy.TotalMilliseconds / hcopy.TotalMilliseconds;
    }

    private static void PrepareDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);

        Directory.CreateDirectory(path);
    }

    private static void PrintTextReport(ComparisonReport report)
    {
        Console.WriteLine("=== HCopy vs Robocopy ===");
        Console.WriteLine($"Source: {report.SourceDirectory}");
        Console.WriteLine($"Destination base: {report.DestinationBaseDirectory}");
        Console.WriteLine($"Files: {report.Inventory.Files}");
        Console.WriteLine($"Bytes: {report.Inventory.Bytes:N0}");
        Console.WriteLine();
        PrintTool(report.HCopy);
        PrintTool(report.Robocopy);
        Console.WriteLine();
        Console.WriteLine("Comparison:");
        Console.WriteLine($"  Gain vs Robocopy: {FormatPercent(report.PerformanceGainPercent)}");
        Console.WriteLine($"  Speedup factor: {FormatDouble(report.SpeedupFactor)}x");
        if (report.UnsupportedRobocopyOptions.Count > 0)
            Console.WriteLine($"  Unsupported robocopy options: {string.Join(", ", report.UnsupportedRobocopyOptions)}");
    }

    private static void PrintTool(ToolRunResult result)
    {
        Console.WriteLine($"{result.Tool}:");
        Console.WriteLine($"  Success: {result.Success}");
        Console.WriteLine($"  Exit code: {result.ExitCode}");
        Console.WriteLine($"  Elapsed: {result.Elapsed}");
        Console.WriteLine($"  Files: {result.FilesCopied}");
        Console.WriteLine($"  Bytes: {result.BytesCopied:N0}");
        Console.WriteLine($"  Throughput: {FormatDouble(MiBPerSec(result.BytesCopied, result.Elapsed))} MiB/s");
        if (!string.IsNullOrWhiteSpace(result.Error))
            Console.WriteLine($"  Error: {result.Error}");
    }

    private static async Task WriteJsonAsync(ComparisonReport report, string path)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        Console.WriteLine($"JSON report: {path}");
    }

    private static async Task WriteCsvAsync(ComparisonReport report, string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Section,Tool,ExitCode,ElapsedMs,FilesCopied,BytesCopied,ThroughputMiBPerSec,Success,GainPercent,SpeedupFactor");
        AppendCsvRow(builder, "Inventory", "", "", "", report.Inventory.Files.ToString(CultureInfo.InvariantCulture), report.Inventory.Bytes.ToString(CultureInfo.InvariantCulture), "", "", "", "");
        AppendCsvRow(builder, "Result", report.HCopy.Tool, report.HCopy.ExitCode.ToString(CultureInfo.InvariantCulture), report.HCopy.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture), report.HCopy.FilesCopied.ToString(CultureInfo.InvariantCulture), report.HCopy.BytesCopied.ToString(CultureInfo.InvariantCulture), MiBPerSec(report.HCopy.BytesCopied, report.HCopy.Elapsed).ToString("F3", CultureInfo.InvariantCulture), report.HCopy.Success.ToString(), "", "");
        AppendCsvRow(builder, "Result", report.Robocopy.Tool, report.Robocopy.ExitCode.ToString(CultureInfo.InvariantCulture), report.Robocopy.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture), report.Robocopy.FilesCopied.ToString(CultureInfo.InvariantCulture), report.Robocopy.BytesCopied.ToString(CultureInfo.InvariantCulture), MiBPerSec(report.Robocopy.BytesCopied, report.Robocopy.Elapsed).ToString("F3", CultureInfo.InvariantCulture), report.Robocopy.Success.ToString(), "", "");
        AppendCsvRow(builder, "Comparison", "HCopy vs Robocopy", "", "", "", "", "", "", FormatCsvNumber(report.PerformanceGainPercent), FormatCsvNumber(report.SpeedupFactor));
        await File.WriteAllTextAsync(path, builder.ToString()).ConfigureAwait(false);
        Console.WriteLine($"CSV report: {path}");
    }

    private static void AppendCsvRow(StringBuilder builder, params string[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
                builder.Append(',');

            builder.Append(CsvEscape(values[i]));
        }

        builder.AppendLine();
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return '"' + value.Replace("\"", "\"\"") + '"';

        return value;
    }

    private static string FormatPercent(double? value) => value.HasValue ? value.Value.ToString("F2", CultureInfo.InvariantCulture) + "%" : "N/A";
    private static string FormatDouble(double? value) => value.HasValue ? value.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A";
    private static string FormatCsvNumber(double? value) => value.HasValue ? value.Value.ToString("F6", CultureInfo.InvariantCulture) : string.Empty;

    private static double MiBPerSec(long bytes, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
            return 0;

        return bytes / elapsed.TotalSeconds / 1024d / 1024d;
    }

    private static string Quote(string value)
        => value.Contains(' ') || value.Contains('\t') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private static string BuildDisplayCommandLine(IEnumerable<string> args)
        => string.Join(' ', args.Select(Quote));

    private static bool TryParseArguments(string[] args, out ParsedArguments parsed, out string? error, out bool showHelp)
    {
        parsed = default!;
        error = null;
        showHelp = false;

        if (args.Length == 0 || args.Any(a => a is "-h" or "--help" or "/?"))
        {
            showHelp = true;
            return false;
        }

        if (args.Length < 2)
        {
            error = "Missing source or destination base path.";
            return false;
        }

        var options = new FileCopyOptions
        {
            SourceDirectory = args[0],
            DestinationDirectory = Path.Combine(Path.GetFullPath(args[1]), "hcopy")
        };

        string? jsonPath = null;
        string? csvPath = null;

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-t":
                    if (++i < args.Length && int.TryParse(args[i], out var t)) options.DegreeOfParallelism = t; else return Fail(out error, "Invalid -t value.");
                    break;
                case "-r":
                    if (++i < args.Length && int.TryParse(args[i], out var rc)) options.RetryCount = rc; else return Fail(out error, "Invalid -r value.");
                    break;
                case "-w":
                    if (++i < args.Length && int.TryParse(args[i], out var rd)) options.RetryDelayMs = rd; else return Fail(out error, "Invalid -w value.");
                    break;
                case "-exd":
                    if (++i < args.Length) options.ExcludeDirectoryPatterns.Add(args[i]); else return Fail(out error, "Missing -exd pattern.");
                    break;
                case "-exf":
                    if (++i < args.Length) options.ExcludeFilePatterns.Add(args[i]); else return Fail(out error, "Missing -exf pattern.");
                    break;
                case "-cbytes":
                    if (++i < args.Length && int.TryParse(args[i], out var cb)) options.ChecksumBytes = cb; else return Fail(out error, "Invalid -cbytes value.");
                    break;
                case "-verify":
                    options.VerificationEnabled = true;
                    break;
                case "-alg":
                    if (++i < args.Length) options.ChecksumAlgorithm = args[i]; else return Fail(out error, "Missing -alg value.");
                    break;
                case "--json":
                    if (++i < args.Length) jsonPath = Path.GetFullPath(args[i]); else return Fail(out error, "Missing --json path.");
                    break;
                case "--csv":
                    if (++i < args.Length) csvPath = Path.GetFullPath(args[i]); else return Fail(out error, "Missing --csv path.");
                    break;
                default:
                    return Fail(out error, $"Unknown option {args[i]}");
            }
        }

        var destinationBase = Path.GetFullPath(args[1]);
        var baseName = Path.GetFileName(destinationBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "HCopyCompare";

        jsonPath ??= Path.Combine(Path.GetDirectoryName(destinationBase) ?? Directory.GetCurrentDirectory(), baseName + ".compare.json");
        csvPath ??= Path.Combine(Path.GetDirectoryName(destinationBase) ?? Directory.GetCurrentDirectory(), baseName + ".compare.csv");

        parsed = new ParsedArguments
        {
            SourceDirectory = Path.GetFullPath(args[0]),
            DestinationBaseDirectory = destinationBase,
            Options = options,
            JsonPath = jsonPath,
            CsvPath = csvPath
        };

        return true;

        bool Fail(out string? err, string message)
        {
            err = message;
            return false;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: HCopyCompare <sourceDir> <destBaseDir> [HCopy options] [--json <path>] [--csv <path>]");
        Console.WriteLine("HCopy options:");
        Console.WriteLine("  -t <threads>             Number of parallel threads (default 4)");
        Console.WriteLine("  -r <retryCount>          Retry count on failure (default 3)");
        Console.WriteLine("  -w <retryDelayMs>        Wait milliseconds between retries (default 1000)");
        Console.WriteLine("  -exd <pattern>           Exclude directory pattern (wildcard), may use multiple");
        Console.WriteLine("  -exf <pattern>           Exclude file pattern (wildcard), may use multiple");
        Console.WriteLine("  -cbytes <n>              Number of bytes to checksum (0 = none)");
        Console.WriteLine("  -verify                  Enable verification after copy");
        Console.WriteLine("  -alg <MD5|SHA256>        Choose checksum algorithm (default MD5)");
        Console.WriteLine("  --json <path>            Write JSON report to path");
        Console.WriteLine("  --csv <path>             Write CSV report to path");
    }

    private sealed class WildcardMatcher
    {
        private readonly System.Text.RegularExpressions.Regex _regex;

        public WildcardMatcher(string pattern)
        {
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            _regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);
        }

        public bool IsMatch(string text) => _regex.IsMatch(text);
    }

    private sealed record ParsedArguments
    {
        public string SourceDirectory { get; init; } = string.Empty;
        public string DestinationBaseDirectory { get; init; } = string.Empty;
        public FileCopyOptions Options { get; init; } = new();
        public string JsonPath { get; init; } = string.Empty;
        public string CsvPath { get; init; } = string.Empty;
    }

    private sealed record Inventory(long Files, long Bytes);

    private sealed class ComparisonReport
    {
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public string SourceDirectory { get; init; } = string.Empty;
        public string DestinationBaseDirectory { get; init; } = string.Empty;
        public string HCopyDestination { get; init; } = string.Empty;
        public string RobocopyDestination { get; init; } = string.Empty;
        public Inventory Inventory { get; init; } = new(0, 0);
        public ToolRunResult HCopy { get; init; } = new();
        public ToolRunResult Robocopy { get; init; } = new();
        public List<string> UnsupportedRobocopyOptions { get; init; } = new();
        public double? PerformanceGainPercent { get; init; }
        public double? SpeedupFactor { get; init; }
    }

    private sealed class ToolRunResult
    {
        public string Tool { get; init; } = string.Empty;
        public string CommandLine { get; init; } = string.Empty;
        public int ExitCode { get; init; }
        public TimeSpan Elapsed { get; init; }
        public long FilesCopied { get; init; }
        public long BytesCopied { get; init; }
        public bool Success { get; init; }
        public string? Error { get; init; }
    }
}
