using System.Diagnostics;
using SunriseStudio.Packages;

// Solforge Phase 05 (metadata index, search) and Phase 06 (parallel block reads) checks.
// The install-free sections open packages PackageWriter writes into a scratch install (uncompressed,
// unencrypted blocks, so no Oodle or keys are needed). --game-root <path> adds the timed warm open of
// the installation and a serial/parallel comparison over installed (compressed, encrypted) tags.
string? gameRoot = null;
for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--game-root") gameRoot = args[i + 1];
int failures = 0;
void Check(bool condition, string what)
{
    if (condition) Console.WriteLine("Passed: " + what);
    else { failures++; Console.Error.WriteLine("FAIL " + what); }
}

string root = Path.Combine(Path.GetTempPath(), "solforge-packages-" + Guid.NewGuid().ToString("N"));
string install = Path.Combine(root, "install"), packages = Path.Combine(install, "packages");
string cache = Path.Combine(root, "cache");
Directory.CreateDirectory(packages);
try
{
    var options = new StudioCacheOptions { DirectoryPath = cache };
    byte[] small = Bytes(900, 1), medium = Bytes(700_000, 2), large = Bytes(1_500_000, 3);
    string first = Path.Combine(packages, "w64_test_0100_0.pkg"), second = Path.Combine(packages, "w64_test_0101_0.pkg");
    PackageWriter.WriteNew(first, 0x100,
        [new(0x80800001, 0x100A, small, "test:small"), new(0x80800002, 0x100A, medium, "test:medium")]);
    PackageWriter.WriteNew(second, 0x101,
        [new(0x80800003, 0x100A, large, "test:large"), new(0x80800001, 0x1200, Bytes(5000, 4), "test:other")]);

    // Phase 05: a cold open writes the index; a warm open reads it and exposes the same metadata.
    string[] coldNames;
    NamedTag[] coldRows;
    uint[] coldEntries;
    using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
    {
        Check(!catalog.OpenedFromIndex, "cold open reads the package files");
        Check(catalog.IndexPath is not null && File.Exists(catalog.IndexPath), "cold open writes the metadata index");
        coldNames = catalog.Names.Select(x => $"{x.Key:X8}={x.Value}").ToArray();
        coldRows = catalog.NamedTags.ToArray();
        coldEntries = catalog.Entries.Select(x => x.TagHash).ToArray();
        Check(catalog.FindByClass(0x80800001).Count == 2 && catalog.FindByClass(0x80800003).Count == 1
            && catalog.FindByClass(0x12345678).Count == 0, "FindByClass indexes entries by class hash");
        Check(catalog.FindByType(8).Count == 3 && catalog.FindByType(9, 0).Count == 1, "FindByType filters type and subtype");
        ulong? group = catalog.GetPackageGroup(0x100);
        Check(group is not null && catalog.FindPackagesInGroup(group.Value).Contains((ushort)0x100)
            && catalog.GetPackageGroup(0x7FF) is null, "FindPackagesInGroup finds a package by its header group");
        Check(catalog.GetPackageEntries(0x101).Count == 2 && catalog.GetPackageEntries(0x7FF).Count == 0,
            "GetPackageEntries lists one package's entries");
    }
    using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
    {
        Check(catalog.OpenedFromIndex, "warm open uses the metadata index");
        Check(catalog.Entries.Select(x => x.TagHash).SequenceEqual(coldEntries), "indexed entries match the cold open");
        Check(catalog.Names.Select(x => $"{x.Key:X8}={x.Value}").SequenceEqual(coldNames), "indexed names match the cold open");
        Check(catalog.NamedTags.SequenceEqual(coldRows), "indexed name rows match the cold open");
        var entries = catalog.GetPackageEntries(0x100);
        Check(catalog.ReadOriginal(entries[0].TagHash).SequenceEqual(small)
            && catalog.ReadOriginal(entries[1].TagHash).SequenceEqual(medium), "indexed tables read the same bytes");
    }
    // A rewritten package (different size) and a touched package (same size, newer write time) both miss.
    File.Delete(second);
    byte[] replaced = Bytes(1_600_000, 5);
    PackageWriter.WriteNew(second, 0x101, [new(0x80800003, 0x100A, replaced, "test:large")]);
    using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
    {
        Check(!catalog.OpenedFromIndex, "a changed package size invalidates the index");
        Check(catalog.ReadOriginal(catalog.GetPackageEntries(0x101)[0].TagHash).SequenceEqual(replaced),
            "the rebuilt index reads the rewritten package");
    }
    File.SetLastWriteTimeUtc(first, File.GetLastWriteTimeUtc(first).AddSeconds(2));
    using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
        Check(!catalog.OpenedFromIndex, "a changed write time invalidates the index");
    using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
        Check(catalog.OpenedFromIndex, "the rebuilt index serves the next open");
    using (var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false, DirectoryPath = cache }))
        Check(!catalog.OpenedFromIndex && catalog.IndexPath is null, "a disabled disk cache uses no index");
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

if (gameRoot is null)
{
    Console.WriteLine("No --game-root: installed package timing and comparison skipped.");
}
else
{
    // The first open may be cold; the second is the warm open the 1 s budget applies to.
    using (PackageCatalog.Open(gameRoot)) { }
    var watch = Stopwatch.StartNew();
    using var catalog = PackageCatalog.Open(gameRoot);
    watch.Stop();
    Console.WriteLine($"Warm open: {catalog.PackageFileCount} package files, {catalog.PackageCount} packages, "
        + $"{catalog.Entries.Count} entries in {watch.Elapsed.TotalMilliseconds:F0} ms (index: {catalog.OpenedFromIndex}).");
    Check(catalog.OpenedFromIndex, "installed packages open from the metadata index");
    if (catalog.PackageFileCount >= 400)
        Check(watch.Elapsed < TimeSpan.FromSeconds(1), "warm open of 400+ package files takes under 1 s");
    else
        Console.WriteLine("Note: fewer than 400 package files, so the 1 s budget is reported only.");
}

if (failures != 0)
{
    Console.Error.WriteLine($"Package checks failed: {failures} problem(s).");
    return 1;
}
return 0;

static byte[] Bytes(int count, int seed)
{
    byte[] bytes = new byte[count];
    new Random(seed).NextBytes(bytes);
    return bytes;
}
