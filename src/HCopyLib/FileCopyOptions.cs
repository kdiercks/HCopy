public class FileCopyOptions
{
    public string SourceDirectory { get; set; }
    public string DestinationDirectory { get; set; }
    public int DegreeOfParallelism { get; set; } = 4;
    public int RetryCount { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;
    public List<string> ExcludeDirectoryPatterns { get; set; } = new List<string>();
    public List<string> ExcludeFilePatterns { get; set; } = new List<string>();

    public bool ChecksumEnabled { get; set; } = false;  // Neu
    public int ChecksumBytes { get; set; } = 0;
    public bool VerificationEnabled { get; set; } = false;
    public string ChecksumAlgorithm { get; set; } = "MD5";

    public bool CopyTimestampsAndAttributes { get; set; } = true; // Neu: Metadaten kopieren
}
