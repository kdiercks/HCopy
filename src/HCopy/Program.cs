using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HighPerfFileCopyLib;

namespace HighPerfFileCopy.ConsoleApp
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: HCopy <sourceDir> <destDir> [options]");
                Console.WriteLine("Options:");
                Console.WriteLine("  -t <threads>             Number of parallel threads (default 4)");
                Console.WriteLine("  -r <retryCount>          Retry count on failure (default 3)");
                Console.WriteLine("  -w <retryDelayMs>        Wait milliseconds between retries (default 1000)");
                Console.WriteLine("  -exd <pattern>           Exclude directory pattern (wildcard), may use multiple");
                Console.WriteLine("  -exf <pattern>           Exclude file pattern (wildcard), may use multiple");
                Console.WriteLine("  -cbytes <n>              Number of Bytes to checksum (0 = none)");
                Console.WriteLine("  -verify                  Enable verification after copy");
                Console.WriteLine("  -alg <MD5|SHA256>        Choose checksum algorithm (default MD5)");
                return 1;
            }

            string source = args[0];
            string dest = args[1];

            var options = new FileCopyOptions
            {
                SourceDirectory = source,
                DestinationDirectory = dest
            };

            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "-t":
                        if (++i < args.Length && int.TryParse(args[i], out int t))
                            options.DegreeOfParallelism = t;
                        break;
                    case "-r":
                        if (++i < args.Length && int.TryParse(args[i], out int rc))
                            options.RetryCount = rc;
                        break;
                    case "-w":
                        if (++i < args.Length && int.TryParse(args[i], out int rd))
                            options.RetryDelayMs = rd;
                        break;
                    case "-exd":
                        if (++i < args.Length)
                            options.ExcludeDirectoryPatterns.Add(args[i]);
                        break;
                    case "-exf":
                        if (++i < args.Length)
                            options.ExcludeFilePatterns.Add(args[i]);
                        break;
                    case "-cbytes":
                        if (++i < args.Length && int.TryParse(args[i], out int cb))
                            options.ChecksumBytes = cb;
                        break;
                    case "-verify":
                        options.VerificationEnabled = true;
                        break;
                    case "-alg":
                        if (++i < args.Length)
                            options.ChecksumAlgorithm = args[i];
                        break;
                    default:
                        Console.WriteLine($"Unknown option {args[i]}");
                        break;
                }
            }

            using var cts = new CancellationTokenSource();
            var manager = new FileCopyManager(options);

            Console.WriteLine("Press 'p' to pause, 'r' to resume, 'c' to cancel.");

            Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var key = Console.ReadKey(true);
                    if (key.KeyChar == 'p')
                    {
                        Console.WriteLine("Pausing...");
                        manager.Pause();
                    }
                    else if (key.KeyChar == 'r')
                    {
                        Console.WriteLine("Resuming...");
                        manager.Resume();
                    }
                    else if (key.KeyChar == 'c')
                    {
                        Console.WriteLine("Cancelling...");
                        cts.Cancel();
                        break;
                    }
                }
            });

            var progress = new Progress<PerFileStats>(ps =>
            {
                var verificationStatus = ps.VerificationPerformed
                    ? (ps.VerificationPassed ? "Passed" : "Failed")
                    : "NotChecked";

                Console.WriteLine($"File: {ps.FilePath}");
                Console.WriteLine($"  Size: {ps.FileSize:N0} bytes");
                Console.WriteLine($"  CopyTime: {ps.CopyTime}");
                Console.WriteLine($"  ChecksumTime: {(ps.ChecksumTime?.ToString() ?? "-")}");
                Console.WriteLine($"  Verified: {verificationStatus}");

            });

            Console.WriteLine("Starting copy…");
            var overallProgressCallback = new Action<CopyProgress>(cp =>
            {
                Console.WriteLine($"Progress: {cp.FilesCompleted}/{cp.FilesTotal} ({cp.Percent:F2}%) CurrentFile: {cp.CurrentFile}");
            });

            var stats = await manager.RunAsync(perFileProgress: progress, overallProgress: overallProgressCallback, cancellationToken: cts.Token);


            Console.WriteLine();
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"Total files: {stats.FilesTotal}");
            Console.WriteLine($"Total elapsed time: {stats.TotalTime}");
            Console.WriteLine($"Total copy time (sum): {stats.TotalCopyTime}");
            Console.WriteLine($"Average copy time per file: {stats.AverageCopyTimePerFile}");
            if (options.ChecksumBytes > 0)
            {
                Console.WriteLine($"Total checksum time (sum): {stats.TotalChecksumTime}");
                Console.WriteLine($"Average checksum time per file: {stats.AverageChecksumTimePerFile}");
            }

            return 0;
        }
    }
}
