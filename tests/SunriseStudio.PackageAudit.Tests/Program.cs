using System.Diagnostics;
using SunriseStudio.Packages;

// Solforge Phase 09: package and file-system engine audit.
// Always: PackageWriter output (new package and next patch) passes its SHA-1 digests (misc 0xF8, metadata 0x118,
// per-block record +0x0C) and damaged copies fail them; a full catalog search over scratch packages is measured.
// --game-root <path>: the full catalog search over the installation must peak under 1.5 GB.
// --stress (with --game-root; manual, hours on a full install): cold parse of every package file without the
// index, then every entry read in bounded parallel batches, with the same 1.5 GB budget.
string? gameRoot = null;
bool stress = args.Contains("--stress");
for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--game-root") gameRoot = args[i + 1];
const long MemoryBudget = 1536L * 1024 * 1024;
int failures = 0;
void Check(bool condition, string what)
{
    if (condition) Console.WriteLine("Passed: " + what);
    else { failures++; Console.Error.WriteLine("FAIL " + what); }
}

string root = Path.Combine(Path.GetTempPath(), "solforge-package-audit-" + Guid.NewGuid().ToString("N"));
string install = Path.Combine(root, "install"), packages = Path.Combine(install, "packages");
Directory.CreateDirectory(packages);
try
{
    string source = Path.Combine(packages, "w64_audit_0102_0.pkg");
    PackageWriter.WriteNew(source, 0x102,
    [
        new(0x80800001, 0x100A, Bytes(600_000, 1), "audit:large"),
        new(0x80800002, 0x100A, Bytes(4000, 2), "audit:small"),
    ]);
    var written = PackageDigests.Verify(source);
    Check(written.Valid && written.BlocksChecked >= 3 && written.BlocksSkipped == 0,
        $"WriteNew output passes misc, metadata and {written.BlocksChecked} block SHA-1 digests");

    // The patch lives beside its source so inherited records can be checked in the source file.
    string patch = Path.Combine(packages, "w64_audit_0102_1.pkg");
    PackageWriter.WritePatch(source, patch, new Dictionary<int, byte[]> { [1] = Bytes(9000, 3) },
        protectedGameRoot: Path.Combine(root, "protected"));
    var own = PackageDigests.Verify(patch);
    var inherited = PackageDigests.Verify(patch, ownBlocksOnly: false);
    Check(own.Valid && own.BlocksChecked >= 1 && own.BlocksSkipped == written.BlocksChecked,
        "WritePatch output passes its own block digests and skips inherited ones by default");
    Check(inherited.Valid && inherited.BlocksSkipped == 0 && inherited.BlocksChecked == own.BlocksChecked + own.BlocksSkipped,
        "inherited block records still match the bytes in their source generation");

    // Damaged copies: one byte each in misc, metadata and the first block's stored bytes.
    byte[] header = File.ReadAllBytes(source).AsSpan(0, 0x180).ToArray();
    long misc = BitConverter.ToUInt32(header, 0xF0), metadata = BitConverter.ToUInt32(header, 0x110);
    string Damaged(string name, long offset)
    {
        byte[] bytes = File.ReadAllBytes(source);
        bytes[offset] ^= 0x5A;
        string path = Path.Combine(root, "damaged", name, "w64_audit_0102_0.pkg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }
    Check(!PackageDigests.Verify(Damaged("misc", misc)).MiscValid, "a changed misc byte fails the 0xF8 digest");
    // Metadata +8 is the layout word, which table parsing does not read for variant 1.
    var badMetadata = PackageDigests.Verify(Damaged("metadata", metadata + 8));
    Check(!badMetadata.MetadataValid && badMetadata.MiscValid, "a changed metadata byte fails the 0x118 digest");
    long firstBlock = written.BlocksChecked > 0 ? FirstBlockOffset(source) : 0;
    var badBlock = PackageDigests.Verify(Damaged("block", firstBlock + 17));
    Check(badBlock.MiscValid && badBlock.MetadataValid && badBlock.BadBlocks.SequenceEqual(new[] { 0 }),
        "a changed stored byte fails that block's +0x0C digest");

    // A full catalog search over the scratch install: every class, type and name row.
    using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false, DirectoryPath = Path.Combine(root, "cache") });
    var (results, peak) = FullSearch(catalog);
    Console.WriteLine($"Scratch catalog search: {results} results, peak working set {peak / (1024 * 1024)} MB.");
    Check(results > 0 && peak < MemoryBudget, "a full catalog search over scratch packages stays under 1.5 GB");
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

if (gameRoot is null)
{
    Console.WriteLine("skipped: installed catalog memory budget (no --game-root).");
    Console.WriteLine("skipped: 115 GB full-install stress test (manual: verify.ps1 -Game <install> -Stress).");
}
else
{
    using (var catalog = PackageCatalog.Open(gameRoot))
    {
        var watch = Stopwatch.StartNew();
        var (results, peak) = FullSearch(catalog);
        Console.WriteLine($"Installed catalog search: {catalog.Entries.Count} entries, {results} results in "
            + $"{watch.Elapsed.TotalSeconds:F1} s, peak working set {peak / (1024 * 1024)} MB.");
        Check(peak < MemoryBudget, "a full catalog search over the installation stays under 1.5 GB");
    }
    if (!stress)
    {
        Console.WriteLine("skipped: 115 GB full-install stress test (manual: verify.ps1 -Game <install> -Stress).");
    }
    else
    {
        long bytes = Directory.EnumerateFiles(Path.Combine(gameRoot, "packages"), "*.pkg").Sum(path => new FileInfo(path).Length);
        Console.WriteLine($"Stress: {bytes / (1024.0 * 1024 * 1024):F1} GB of package files.");
        if (bytes < 100L * 1024 * 1024 * 1024)
            Console.WriteLine("Note: under 100 GB of packages, so this is not the full Season of Arrivals install.");
        var watch = Stopwatch.StartNew();
        using var cold = PackageCatalog.Open(gameRoot, cacheOptions: new() { Enabled = false });
        Console.WriteLine($"Stress: cold parse of {cold.PackageFileCount} package files, {cold.Entries.Count} entries "
            + $"in {watch.Elapsed.TotalSeconds:F1} s.");
        long readBytes = 0;
        int readFailures = 0;
        var tags = cold.Entries.Select(x => x.TagHash).ToArray();
        for (int start = 0; start < tags.Length; start += 256)
        {
            var batch = tags.AsSpan(start, Math.Min(256, tags.Length - start)).ToArray();
            try
            {
                foreach (byte[] data in cold.ReadOriginalAll(batch)) readBytes += data.Length;
            }
            catch (Exception error) when (error is InvalidDataException or IOException or AggregateException)
            {
                // A batch that fails is retried one tag at a time so the failure count is exact.
                foreach (uint tag in batch)
                    try { readBytes += cold.ReadOriginal(tag).Length; }
                    catch (Exception single) when (single is InvalidDataException or IOException) { readFailures++; }
            }
        }
        long peak = Process.GetCurrentProcess().PeakWorkingSet64;
        Console.WriteLine($"Stress: read {tags.Length} entries ({readBytes / (1024.0 * 1024 * 1024):F1} GB decoded), "
            + $"{readFailures} unreadable, {watch.Elapsed.TotalMinutes:F1} min, peak working set {peak / (1024 * 1024)} MB.");
        Check(readFailures == 0, "every installed entry reads (stress)");
        Check(peak < MemoryBudget, "the full-install stress test stays under 1.5 GB (stress)");
    }
}

if (failures != 0)
{
    Console.Error.WriteLine($"Package audit failed: {failures} problem(s).");
    return 1;
}
return 0;

// Searches every class and type the catalog indexes and every name row; returns the hit count and peak working set.
static (long Results, long PeakWorkingSet) FullSearch(PackageCatalog catalog)
{
    long results = 0;
    foreach (uint reference in catalog.Entries.Select(x => x.Reference).Distinct())
        results += catalog.FindByClass(reference).Count;
    foreach (var (type, subType) in catalog.Entries.Select(x => (x.Type, x.SubType)).Distinct())
        results += catalog.FindByType(type, subType).Count;
    results += catalog.Names.Count(pair => pair.Value.Contains("a", StringComparison.OrdinalIgnoreCase));
    results += catalog.NamedTags.Count(row => row.Name.Length > 0);
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    return (results, process.PeakWorkingSet64);
}

// The stored offset of block 0, read from the variant 1 block table PackageWriter writes.
static long FirstBlockOffset(string path)
{
    byte[] bytes = File.ReadAllBytes(path);
    long metadata = BitConverter.ToUInt32(bytes, 0x110);
    long entries = metadata + 96;
    ulong physicalEntries = BitConverter.ToUInt64(bytes, (int)entries - 16);
    long blocks = entries + (long)physicalEntries * 16 + 32;
    return BitConverter.ToUInt32(bytes, (int)blocks);
}

static byte[] Bytes(int count, int seed)
{
    byte[] bytes = new byte[count];
    new Random(seed).NextBytes(bytes);
    return bytes;
}
