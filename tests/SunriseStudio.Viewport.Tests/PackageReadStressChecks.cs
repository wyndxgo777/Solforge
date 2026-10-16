using System.Collections.Concurrent;
using System.Security.Cryptography;
using SunriseStudio.Packages;

/** Parallel catalog reads across many packages, with cache eviction and invalidation, match serial reads. */
internal static class PackageReadStressChecks
{
    // Every 40th entry spans about 500 packages and far more blocks than the 1024-block cache holds.
    private const int Step = 40, Threads = 32;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string install, int rounds = 2)
    {
        var options = new StudioCacheOptions { Enabled = false };
        var serial = new Dictionary<uint, byte[]>();
        using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
            foreach (var entry in catalog.Entries.Where((e, i) => i % Step == 0 && e.Size is > 0 and < 8 << 20))
                serial[entry.TagHash] = SHA256.HashData(catalog.Read(entry.TagHash));
        for (int round = 0; round < rounds; round++)
        {
            using var catalog = PackageCatalog.Open(install, cacheOptions: options);
            using var stop = new CancellationTokenSource();
            int invalidations = 0;
            // Odd rounds drop the block cache and close file handles while the reads run.
            var churn = round % 2 == 1 ? Task.Run(() =>
            {
                while (!stop.IsCancellationRequested) { catalog.InvalidateReadCache(); invalidations++; Thread.Sleep(20); }
            }) : Task.CompletedTask;
            var failures = new ConcurrentBag<string>();
            int mismatches = 0;
            Parallel.ForEach(serial.Keys.OrderBy(tag => HashCode.Combine(tag, round)), new ParallelOptions { MaxDegreeOfParallelism = Threads }, tag =>
            {
                try { if (!SHA256.HashData(catalog.Read(tag)).AsSpan().SequenceEqual(serial[tag])) Interlocked.Increment(ref mismatches); }
                catch (Exception error) { failures.Add($"0x{tag:X8}: {error.GetType().Name}: {error.Message}"); }
            });
            stop.Cancel();
            churn.Wait();
            if (mismatches != 0 || !failures.IsEmpty)
                throw new InvalidDataException($"Round {round}: {mismatches} reads differ from serial reads, {failures.Count} failed. "
                    + string.Join("; ", failures.Take(3)));
            Console.WriteLine($"package_read_stress round={round} reads={serial.Count} threads={Threads} invalidations={invalidations} result=pass");
        }
    }
}
