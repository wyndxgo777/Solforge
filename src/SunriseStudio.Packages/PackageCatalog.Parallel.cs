using System.Runtime.ExceptionServices;

namespace SunriseStudio.Packages;

// Solforge Phase 06. Blocks decode on a bounded pool through the same CopyBlock path as serial reads
// (ReadStored, PackageKeys decryption, OodleDecoder.Decode, the decoded-block cache), so the bytes are the
// serial bytes by construction: the pool only warms the cache, and the serial assembly copies from it.
public sealed partial class PackageCatalog
{
    private int maximumParallelDecodes = Math.Clamp(Environment.ProcessorCount, 1, 8);

    /** Upper bound on concurrent block decodes per parallel read; 1 makes the parallel paths serial. */
    public int MaximumParallelDecodes
    {
        get => Volatile.Read(ref maximumParallelDecodes);
        set => Volatile.Write(ref maximumParallelDecodes, Math.Max(1, value));
    }

    /** Reads one tag with its blocks decoded in parallel; the result is byte-identical to ReadOriginal. */
    public byte[] ReadOriginalParallel(uint tagHash, CancellationToken token = default)
    {
        lock (readLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!byTag.ContainsKey(tagHash))
                throw new KeyNotFoundException($"Tag 0x{tagHash:X8} is not installed.");
        }
        var entry = byTag[tagHash];
        var package = packages[entry.PackageId];
        // Blocks decode to 256 KiB except possibly a package's last, so this spans at least the tag's blocks.
        // Warming stops at half the LRU so the first warmed blocks are still cached when assembly reaches them.
        int count = Math.Min(Math.Min(package.Blocks.Length - entry.StartBlock, CacheBlocks / 2),
            (int)Math.Min(int.MaxValue, ((long)entry.StartOffset + entry.Size + 0x3FFFF) / 0x40000));
        if (count > 1 && MaximumParallelDecodes > 1) DecodeBlocks(package, entry.StartBlock, count, token);
        return ReadOriginal(tagHash);
    }

    /** Reads one tag on the thread pool without blocking the caller. */
    public Task<byte[]> ReadOriginalAsync(uint tagHash, CancellationToken token = default) =>
        Task.Run(() => ReadOriginalParallel(tagHash, token), token);

    /** Reads many tags, at most maximumParallelism (default MaximumParallelDecodes) at once, in input order. */
    public byte[][] ReadOriginalAll(IReadOnlyList<uint> tagHashes, int maximumParallelism = 0,
        CancellationToken token = default)
    {
        var results = new byte[tagHashes.Count][];
        int degree = maximumParallelism > 0 ? maximumParallelism : MaximumParallelDecodes;
        if (degree == 1 || results.Length < 2)
        {
            for (int i = 0; i < results.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                results[i] = ReadOriginal(tagHashes[i]);
            }
            return results;
        }
        RunBounded(results.Length, degree, token, i => results[i] = ReadOriginal(tagHashes[i]));
        return results;
    }

    /** Warms the decoded cache for a block range; a reader that finds a block pending waits for it, as in serial reads. */
    private void DecodeBlocks(PackageFile package, int first, int count, CancellationToken token) =>
        RunBounded(count, Math.Min(count, MaximumParallelDecodes), token,
            i => CopyBlock(package, first + i, 0, Span<byte>.Empty));

    /** Runs body(0..count-1) on at most degree thread-pool workers; a single failure is rethrown as itself. */
    private static void RunBounded(int count, int degree, CancellationToken token, Action<int> body)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degree), CancellationToken = token };
        try
        {
            Parallel.For(0, count, options, body);
        }
        catch (AggregateException error) when (error.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
        }
    }
}
