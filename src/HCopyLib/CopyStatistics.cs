using System;
using System.Collections.Generic;
using System.Linq;

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
        public List<PerFileStats> FileStats { get; internal set; } = new List<PerFileStats>();
        public TimeSpan TotalCopyTime => TimeSpan.FromTicks(FileStats.Sum(f => f.CopyTime.Ticks));
        public TimeSpan TotalChecksumTime => TimeSpan.FromTicks(FileStats.Sum(f => (f.ChecksumTime?.Ticks) ?? 0));
        public TimeSpan AverageCopyTimePerFile => FilesTotal > 0 ? TimeSpan.FromTicks(TotalCopyTime.Ticks / FilesTotal) : TimeSpan.Zero;
        public TimeSpan AverageChecksumTimePerFile => (FilesTotal > 0 && FileStats.Any(f => f.ChecksumTime.HasValue))
            ? TimeSpan.FromTicks(TotalChecksumTime.Ticks / FilesTotal)
            : TimeSpan.Zero;
    }
}
