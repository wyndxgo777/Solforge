namespace SunriseStudio.Packages;

/** Counters for package reads: entries and blocks read, cache hits, bytes, file opens and time. */
public sealed record PackageReadStatistics(long EntryReads, long BlockReads, long CacheHits,
    long StoredBytes, long DecodedBytes, long FileOpens, long InheritedLookups,
    TimeSpan ReadTime, TimeSpan DecodeTime);
