# HCopy

HCopy is a high-performance file copy tool for copying directories and large file trees on .NET 8.
It is designed to be close to `robocopy`-style workflows, with parallel copy support and optional checksum/verification.

## Projects

- `HCopy` - main copy CLI
- `HCopyCompare` - compares `HCopy` vs `robocopy` and writes text, JSON, and CSV reports
- `HighPerformanceCopyBenchmark` - thread-count benchmark harness
- `HCopyLib` - shared copy library

## Build

```bash
dotnet build HCopy.sln -c Release
```

## HCopy Usage

```bash
HCopy <sourceDir> <destDir> [options]
```

Options:

- `-t <threads>`: parallel workers, default `4`
- `-r <retryCount>`: retry count on failure, default `3`
- `-w <retryDelayMs>`: delay between retries in ms, default `1000`
- `-exd <pattern>`: exclude directory pattern, may repeat
- `-exf <pattern>`: exclude file pattern, may repeat
- `-cbytes <n>`: checksum first `n` bytes, `0` disables it
- `-verify`: verify copied files using checksum
- `-alg <MD5|SHA256>`: checksum algorithm, default `MD5`

Example:

```bash
HCopy C:\Data C:\Backup -t 8 -r 2 -w 500 -exf *.tmp
```

## HCopyCompare Usage

`HCopyCompare` runs the same source through `HCopy` and `robocopy /E`, then prints a side-by-side result.

```bash
HCopyCompare <sourceDir> <destBaseDir> [HCopy options] [--json <path>] [--csv <path>]
```

It creates two output folders:

- `<destBaseDir>/hcopy`
- `<destBaseDir>/robocopy`

Reports:

- text to console
- JSON report to `--json` or `<destBaseDir>.compare.json`
- CSV report to `--csv` or `<destBaseDir>.compare.csv`

Example:

```bash
HCopyCompare C:\Data C:\Benchmarks -t 8 -exf *.tmp --json C:\Benchmarks\result.json --csv C:\Benchmarks\result.csv
```

## Benchmark Tool

`HighPerformanceCopyBenchmark` runs HCopy with different thread counts.

## Notes

- The compare tool is Windows-only because it uses `robocopy`.
- `robocopy` is used in `/E` mode for comparison.
- For best throughput, test with realistic file sizes and storage media.
