namespace SunriseStudio.Packages;

/** Disk cache settings: enabled flag, folder and size limit. */
public sealed record StudioCacheOptions
{
    public bool Enabled { get; init; } = true;
    public string DirectoryPath { get; init; } = DefaultDirectoryPath;
    public long MaximumBytes { get; init; } = 512L * 1024 * 1024;
    public static string DefaultDirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sunrise Studio", "Cache");
}

/** Disk cache counters: hits, misses, writes, rejected entries, bytes and the last error. */
public sealed record StudioCacheStatistics(long Hits, long Misses, long Writes, long Rejected,
    long StoredBytes, string? LastError);
