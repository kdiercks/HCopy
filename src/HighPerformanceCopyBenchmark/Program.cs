using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HighPerfFileCopyLib;

namespace HighPerfFileCopy.Benchmark
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: HighPerfFileCopy.Benchmark <sourceDir> <destDir>");
                Console.WriteLine("This will run benchmark varying thread counts from 1 to 16 and output timings.");
                return 1;
            }

            string source = args[0];
            string destBase = args[1];

            int[] threadCounts = new[] { 1, 2, 4, 8, 16 };
            int checksumBytes = 4096; // e.g., first 4 KB
            bool verification = false;

            foreach (var tc in threadCounts)
            {
                string dest = $"{destBase}_threads{tc}";
                Console.WriteLine($"--- Benchmark with {tc} threads ---");
                var options = new FileCopyOptions
                {
                    SourceDirectory = source,
                    DestinationDirectory = dest,
                    DegreeOfParallelism = tc,
                    RetryCount = 1,
                    RetryDelayMs = 500,
                    ChecksumBytes = checksumBytes,
                    VerificationEnabled = verification,
                    ChecksumAlgorithm = "MD5"
                };

                using var cts = new CancellationTokenSource();
                var manager = new FileCopyManager(options);

                var sw = Stopwatch.StartNew();
                var stats = await manager.RunAsync(perFileProgress: null, overallProgress: null, cancellationToken: cts.Token);
                sw.Stop();

                Console.WriteLine($"Threads: {tc}");
                Console.WriteLine($"  Files: {stats.FilesTotal}");
                Console.WriteLine($"  Total elapsed time: {sw.Elapsed}");
                Console.WriteLine($"  Copy time sum: {stats.TotalCopyTime}");
                Console.WriteLine($"  Checksum time sum: {stats.TotalChecksumTime}");
                Console.WriteLine($"  Avg copy time/file: {stats.AverageCopyTimePerFile}");
                Console.WriteLine($"  Avg checksum time/file: {stats.AverageChecksumTimePerFile}");
                Console.WriteLine();
            }

            Console.WriteLine("Benchmark completed.");
            return 0;
        }
    }
}
