using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HighPerfFileCopyLib
{
    public class CopyProgress
    {
        public int FilesCompleted { get; internal set; }
        public int FilesTotal { get; internal set; }
        public string CurrentFile { get; internal set; } = string.Empty;
        public double Percent => FilesTotal == 0 ? 0 : (FilesCompleted / (double)FilesTotal) * 100.0;
    }

    public class FileCopyManager
    {
        private readonly FileCopyOptions _options;
        private readonly ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
        private readonly IChecksumAlgorithm _checksumAlgo;
        private readonly WildcardMatcher[] _excludeDirectoryMatchers;
        private readonly WildcardMatcher[] _excludeFileMatchers;

        private readonly record struct FileWorkItem(string SourcePath, string RelativePath, long FileSize);

        public FileCopyManager(FileCopyOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            _excludeDirectoryMatchers = _options.ExcludeDirectoryPatterns
                .Select(static pattern => new WildcardMatcher(pattern))
                .ToArray();
            _excludeFileMatchers = _options.ExcludeFilePatterns
                .Select(static pattern => new WildcardMatcher(pattern))
                .ToArray();

            _checksumAlgo = _options.ChecksumAlgorithm?.ToUpperInvariant() switch
            {
                "SHA256" => new Sha256ChecksumAlgorithm(),
                _ => new Md5ChecksumAlgorithm()
            };
        }

        /// <summary> Pause the copy operations (between files). </summary>
        public void Pause() => _pauseEvent.Reset();
        /// <summary> Resume the copy operations. </summary>
        public void Resume() => _pauseEvent.Set();

        /// <summary>
        /// Runs the copy operation.
        /// </summary>
        /// <param name="perFileProgress">Reports progress per file.</param>
        /// <param name="overallProgress">Callback for overall progress.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        public async Task<CopyStatistics> RunAsync(
            IProgress<PerFileStats>? perFileProgress = null,
            Action<CopyProgress>? overallProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(_options.SourceDirectory))
                throw new DirectoryNotFoundException($"Source directory not found: {_options.SourceDirectory}");
            if (!Directory.Exists(_options.DestinationDirectory))
                Directory.CreateDirectory(_options.DestinationDirectory);

            var swTotal = Stopwatch.StartNew();

            var allFiles = EnumerateFiles(
                _options.SourceDirectory,
                _excludeDirectoryMatchers,
                _excludeFileMatchers,
                cancellationToken).ToArray();

            int totalCount = allFiles.Length;
            int filesDone = 0;

            var stats = new CopyStatistics
            {
                FilesTotal = totalCount
            };

            int nextIndex = -1;

            var tasks = new List<Task>();
            for (int i = 0; i < _options.DegreeOfParallelism; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int index = Interlocked.Increment(ref nextIndex);
                        if (index >= allFiles.Length)
                            break;

                        var workItem = allFiles[index];

                        _pauseEvent.Wait(cancellationToken);

                        var perFileStat = new PerFileStats
                        {
                            FilePath = workItem.SourcePath,
                            FileSize = workItem.FileSize
                        };

                        try
                        {
                            var destFile = Path.Combine(_options.DestinationDirectory, workItem.RelativePath);
                            var destDir = Path.GetDirectoryName(destFile);
                            if (!string.IsNullOrEmpty(destDir))
                                Directory.CreateDirectory(destDir);

                            // Copy the file
                            Stopwatch swCopy = Stopwatch.StartNew();
                            bool copied = false;
                            for (int attempt = 0; attempt <= _options.RetryCount && !copied; attempt++)
                            {
                                try
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    await CopyFileAsync(workItem.SourcePath, destFile, workItem.FileSize, cancellationToken).ConfigureAwait(false);
                                    copied = true;
                                }
                                catch (Exception) when (attempt < _options.RetryCount)
                                {
                                    await Task.Delay(_options.RetryDelayMs, cancellationToken).ConfigureAwait(false);
                                }
                            }
                            swCopy.Stop();
                            perFileStat.CopyTime = swCopy.Elapsed;

                            // Checksum logic if enabled
                            if (_options.ChecksumEnabled && _options.ChecksumBytes > 0)
                            {
                                Stopwatch swChk = Stopwatch.StartNew();

                                string checksum = await _checksumAlgo.ComputeAsync(workItem.SourcePath, _options.ChecksumBytes, cancellationToken).ConfigureAwait(false);
                                var chkFile = destFile + ".chk";
                                await File.WriteAllTextAsync(chkFile, checksum, cancellationToken).ConfigureAwait(false);

                                if (_options.VerificationEnabled)
                                {
                                    string destChecksum = await _checksumAlgo.ComputeAsync(destFile, _options.ChecksumBytes, cancellationToken).ConfigureAwait(false);
                                    perFileStat.VerificationPerformed = true;
                                    perFileStat.VerificationPassed = string.Equals(checksum, destChecksum, StringComparison.OrdinalIgnoreCase);
                                }
                                else
                                {
                                    perFileStat.VerificationPerformed = false;
                                    perFileStat.VerificationPassed = false;
                                }

                                swChk.Stop();
                                perFileStat.ChecksumTime = swChk.Elapsed;
                            }
                            else
                            {
                                perFileStat.ChecksumTime = null;
                                perFileStat.VerificationPerformed = false;
                                perFileStat.VerificationPassed = false;
                            }

                            perFileProgress?.Report(perFileStat);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception)
                        {
                            perFileStat.VerificationPerformed = false;
                            perFileStat.VerificationPassed = false;
                            perFileStat.CopyTime = TimeSpan.Zero;
                            perFileStat.ChecksumTime = null;
                            perFileProgress?.Report(perFileStat);
                        }
                            finally
                            {
                                stats.FileStats.Add(perFileStat);
                                Interlocked.Add(ref stats.TotalCopyTicks, perFileStat.CopyTime.Ticks);
                                Interlocked.Add(ref stats.TotalBytesCopied, perFileStat.FileSize);
                                if (perFileStat.ChecksumTime.HasValue)
                                {
                                    Interlocked.Add(ref stats.TotalChecksumTicks, perFileStat.ChecksumTime.Value.Ticks);
                                    Interlocked.Increment(ref stats.ChecksumFileCount);
                                }

                                int done = Interlocked.Increment(ref filesDone);
                                overallProgress?.Invoke(new CopyProgress
                                {
                                    FilesCompleted = done,
                                    FilesTotal = totalCount,
                                CurrentFile = perFileStat.FilePath
                            });
                        }
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            swTotal.Stop();
            stats.TotalTime = swTotal.Elapsed;

            return stats;
        }

        private async Task CopyFileAsync(string sourceFile, string destFile, long fileSize, CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    File.Copy(sourceFile, destFile, overwrite: true);

                    if (_options.CopyTimestampsAndAttributes)
                    {
                        File.SetAttributes(destFile, File.GetAttributes(sourceFile));
                        File.SetCreationTime(destFile, File.GetCreationTime(sourceFile));
                        File.SetLastAccessTime(destFile, File.GetLastAccessTime(sourceFile));
                        File.SetLastWriteTime(destFile, File.GetLastWriteTime(sourceFile));
                    }

                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            int bufferSize;
            if (fileSize < 64 * 1024) bufferSize = 64 * 1024;   // <64KB
            else if (fileSize < 1 * 1024 * 1024) bufferSize = 256 * 1024;  // <1MB
            else bufferSize = 1024 * 1024; // >=1MB

            var fileOptions = FileOptions.SequentialScan | FileOptions.Asynchronous;

            try
            {
                var sourceOptions = new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = bufferSize,
                    Options = fileOptions
                };

                var destinationOptions = new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = bufferSize,
                    Options = FileOptions.Asynchronous,
                    PreallocationSize = fileSize
                };

                using (var srcStream = new FileStream(sourceFile, sourceOptions))
                using (var dstStream = new FileStream(destFile, destinationOptions))
                {
                    await srcStream.CopyToAsync(dstStream, bufferSize, cancellationToken).ConfigureAwait(false);
                }

                if (_options.CopyTimestampsAndAttributes)
                {
                    File.SetAttributes(destFile, File.GetAttributes(sourceFile));
                    File.SetCreationTime(destFile, File.GetCreationTime(sourceFile));
                    File.SetLastAccessTime(destFile, File.GetLastAccessTime(sourceFile));
                    File.SetLastWriteTime(destFile, File.GetLastWriteTime(sourceFile));
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"[ACCESS DENIED] {sourceFile} → {destFile}: {ex.Message}");
                throw;
            }
            catch (IOException ioex)
            {
                Console.WriteLine($"[IO ERROR] {sourceFile} → {destFile}: {ioex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Unexpected error copying {sourceFile} → {destFile}: {ex.Message}");
                throw;
            }
        }

        private static IEnumerable<FileWorkItem> EnumerateFiles(
            string root,
            WildcardMatcher[] excludeDirs,
            WildcardMatcher[] excludeFilePatterns,
            CancellationToken cancellationToken)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                IEnumerable<string> subDirs;
                try { subDirs = Directory.EnumerateDirectories(dir); }
                catch { continue; }

                foreach (var sub in subDirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsExcluded(Path.GetFileName(sub), excludeDirs))
                        continue;
                    stack.Push(sub);
                }

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir); }
                catch { continue; }

                foreach (var f in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsExcluded(Path.GetFileName(f), excludeFilePatterns))
                        continue;

                    yield return new FileWorkItem(
                        f,
                        Path.GetRelativePath(root, f),
                        new FileInfo(f).Length);
                }
            }
        }

        private static bool IsExcluded(string text, WildcardMatcher[] matchers)
        {
            for (int i = 0; i < matchers.Length; i++)
            {
                if (matchers[i].IsMatch(text))
                    return true;
            }

            return false;
        }

        private sealed class WildcardMatcher
        {
            private readonly Regex _regex;

            public WildcardMatcher(string pattern)
            {
                var regexPattern = "^" + Regex.Escape(pattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";

                _regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            }

            public bool IsMatch(string text) => _regex.IsMatch(text);
        }
    }
}
