using System;
using System.Collections.Concurrent;

namespace HighPerfFileCopyLib
{
    public class PerFileStats
    {
        public string FilePath { get; set; } = "";
        public long FileSize { get; set; }
        public TimeSpan CopyTime { get; set; }
        public TimeSpan? ChecksumTime { get; set; }

        public bool VerificationPerformed { get; set; }
        public bool VerificationPassed { get; set; }
    }


    public class CopyStatistics
    {
        public int FilesTotal { get; internal set; }
        public TimeSpan TotalTime { get; internal set; }
        public ConcurrentBag<PerFileStats> FileStats { get; internal set; } = new ConcurrentBag<PerFileStats>();
        internal long TotalCopyTicks;
        internal long TotalChecksumTicks;
        internal int ChecksumFileCount;
        internal long TotalBytesCopied;

        public TimeSpan TotalCopyTime => TimeSpan.FromTicks(TotalCopyTicks);
        public TimeSpan TotalChecksumTime => TimeSpan.FromTicks(TotalChecksumTicks);
        public long TotalBytes => TotalBytesCopied;
        public TimeSpan AverageCopyTimePerFile => FilesTotal > 0 ? TimeSpan.FromTicks(TotalCopyTicks / FilesTotal) : TimeSpan.Zero;
        public TimeSpan AverageChecksumTimePerFile => ChecksumFileCount > 0
            ? TimeSpan.FromTicks(TotalChecksumTicks / ChecksumFileCount)
            : TimeSpan.Zero;
    }
}
