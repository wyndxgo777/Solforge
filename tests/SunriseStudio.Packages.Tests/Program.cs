using System.Diagnostics;
using SunriseStudio.Packages;

// Solforge Phase 05 (metadata index, search), Phase 06 (parallel block reads), Phase 07 (tag headers, DAG) and Phase 08 (delta allocator) checks.
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

    // Phase 06: parallel block reads return the serial bytes. Separate catalogs keep each path's decoded cache cold.
    uint[] tags;
    byte[][] serial;
    using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
    {
        tags = catalog.Entries.Select(x => x.TagHash).ToArray();
        serial = tags.Select(catalog.ReadOriginal).ToArray();
    }
    Check(serial.Any(x => x.Length > 0x40000 * 4), "the parallel check covers a tag spanning several blocks");
    using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
    {
        catalog.MaximumParallelDecodes = 4;
        Check(tags.Select(t => catalog.ReadOriginalParallel(t)).Zip(serial).All(x => x.First.AsSpan().SequenceEqual(x.Second)),
            "ReadOriginalParallel is byte-identical to the serial path (Phase 06)");
    }
    using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
    {
        var all = catalog.ReadOriginalAll(tags, 4);
        Check(all.Zip(serial).All(x => x.First.AsSpan().SequenceEqual(x.Second)),
            "ReadOriginalAll returns the serial bytes in input order (Phase 06)");
        Check(catalog.ReadOriginalAsync(tags[^1]).GetAwaiter().GetResult().AsSpan().SequenceEqual(serial[^1]),
            "ReadOriginalAsync returns the serial bytes (Phase 06)");
        catalog.MaximumParallelDecodes = 0;
        Check(catalog.MaximumParallelDecodes == 1 && catalog.ReadOriginalAll(tags).Zip(serial).All(x => x.First.AsSpan().SequenceEqual(x.Second)),
            "a parallel bound of 1 falls back to serial reads (Phase 06)");
    }

    // A drive that drops out: the read seam fails like ERROR_NOT_READY, and the retry reopens the file.
    using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
    {
        var notReady = new IOException("The device is not ready.", unchecked((int)0x80070015));
        int calls = 0, failing = 1;
        catalog.BlockRead = (handle, buffer, offset) =>
            ++calls <= failing ? throw notReady : RandomAccess.Read(handle, buffer, offset);
        Check(catalog.ReadOriginal(tags[0]).AsSpan().SequenceEqual(serial[0]) && calls == 2
            && catalog.ReadStatistics.FileOpens == 2, "a read that fails once with NOT_READY reopens its file and returns the right bytes");
        var budget = PackageIo.RetryBudget;
        PackageIo.RetryBudget = TimeSpan.FromMilliseconds(300);
        calls = 0; failing = int.MaxValue;
        try { catalog.ReadOriginal(tags[1]); Check(false, "a drive that stays down reports a device error"); }
        catch (PackageDeviceException error)
        {
            Check(PackageIo.IsDeviceError(error) && error.Drive.Length != 0 && error.Message.StartsWith("Drive ", StringComparison.Ordinal),
                "a drive that stays down reports a device error that names the drive");
        }
        finally { PackageIo.RetryBudget = budget; }
        calls = 0; failing = 0;
        Check(catalog.ReadOriginal(tags[1]).AsSpan().SequenceEqual(serial[1]),
            "a failed read caches nothing; the next read returns the right bytes");
    }

    // Phase 08: mock chunk append and entry-table update into a project delta; the source package is only read.
    static string Sha256(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    string sourceHash = Sha256(first);
    long sourceLength = new FileInfo(first).Length;
    DateTime sourceTime = File.GetLastWriteTimeUtc(first);
    byte[] edited = Bytes(1200, 6), added = Bytes(300_000, 7);
    var allocator = PackageBlockAllocator.Open(first);
    allocator.Replace(0, edited);
    uint addedTag = allocator.Append(0x80800001, 0x100A, added);
    string deltaPath = Path.Combine(root, "project", "deltas", "w64_test_0100_0.sfpd");
    var delta = allocator.Write(deltaPath);
    Check(addedTag == TagHeaders.Compose(0x100, 2) && delta.TargetPatch == delta.SourcePatch + 1
        && delta.Redirects.Count == 2 && delta.Redirects[0].Flags == PackageEntryOverride.Replaced
        && delta.Redirects[1].Flags == PackageEntryOverride.Appended && delta.EntryCount == 3,
        "the allocator records a replaced and an appended entry with the next patch (Phase 08)");
    Check(delta.Blocks.Count == 2 && delta.Blocks[0].Index == delta.SourceBlockCount
        && delta.Blocks.All(b => b.Patch == delta.TargetPatch && b.Flags == 0)
        && delta.Redirects.All(r => r.StartBlock >= delta.SourceBlockCount && r.StartOffset % 16 == 0),
        "new chunks go in raw blocks after the source's last block, owned by the target patch (Phase 08)");
    Check(delta.ReadEntry(0).AsSpan().SequenceEqual(edited) && delta.ReadEntry(2).AsSpan().SequenceEqual(added),
        "the delta file returns the replaced and appended bytes (Phase 08)");
    var table = TagHeaders.ParseEntryTable(0x100, "w64_test_0100_1", delta.BuildEntryTable(first));
    Check(table.Length == 3 && table[0].Size == edited.Length && table[1].Size == medium.Length
        && table[1].Reference == 0x80800002 && table[2].Size == added.Length && table[2].TypeInfo == 0x100A,
        "the updated entry table redirects entry 0, keeps entry 1 and appends entry 2 (Phase 08)");
    Check(Sha256(first) == sourceHash && new FileInfo(first).Length == sourceLength
        && File.GetLastWriteTimeUtc(first) == sourceTime && delta.Matches(first),
        "the source package is unchanged after the delta write (Phase 08)");
    using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
        Check(catalog.ReadOriginal(TagHeaders.Compose(0x100, 0)).AsSpan().SequenceEqual(small),
            "the catalog still reads the source bytes (Phase 08)");
    bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; } catch (T) { return true; }
    }
    Check(Throws<InvalidDataException>(() => PackageBlockAllocator.CheckLimits(8193, 1))
        && Throws<InvalidDataException>(() => PackageBlockAllocator.CheckLimits(1, 16385))
        && !Throws<InvalidDataException>(() => PackageBlockAllocator.CheckLimits(8192, 16384))
        && Throws<InvalidDataException>(() => PackageBlockAllocator.CheckNewPackageId(0x0D00))
        && !Throws<InvalidDataException>(() => PackageBlockAllocator.CheckNewPackageId(0x0100))
        && Throws<InvalidDataException>(() => allocator.Replace(5, edited)),
        "entry, block, package id and entry index limits are enforced (Phase 08)");
    Check(Throws<IOException>(() => allocator.Write(first)) && Sha256(first) == sourceHash,
        "the allocator refuses to write over its source (Phase 08)");
    byte[] damaged = File.ReadAllBytes(deltaPath);
    damaged[(int)delta.Blocks[delta.Blocks.Count - 1].Offset] ^= 0xFF;
    File.WriteAllBytes(deltaPath, damaged);
    Check(Throws<InvalidDataException>(() => PackageDelta.Read(deltaPath)), "a damaged delta block fails its SHA-1 (Phase 08)");
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

// Phase 07: tag hash mapping (§5.4), the entry-record parse path PackageFile uses, the dependency DAG.
Check(TagHeaders.Compose(0x100, 0) == 0x80A00000 && TagHeaders.Compose(0x0CFF, 0x1FFF) == 0x821FFFFF
    && TagHeaders.TryDecompose(0x821FFFFF, out ushort mappedPackage, out int mappedEntry)
    && mappedPackage == 0x0CFF && mappedEntry == 0x1FFF && !TagHeaders.TryDecompose(0x12345678, out _, out _),
    "tag hashes map to package id and entry index (Phase 07)");
{
    byte[] row = new byte[TagHeaders.EntryRecordSize];
    TagHeaders.WriteEntryRecord(row, 0x80809C0F, (8u << 9) | (2u << 6), 17, 0x1230, 70_000);
    var parsed = TagHeaders.ParseEntryRecord(0x101, "test", 5, row);
    Check(parsed.TagHash == TagHeaders.Compose(0x101, 5) && parsed.Reference == 0x80809C0F && parsed.Type == 8
        && parsed.SubType == 2 && parsed.Size == 70_000 && parsed.PackageId == 0x101 && parsed.EntryIndex == 5,
        "entry records round-trip reference, type, subtype and size (Phase 07)");

    // 10,000 headers: one full 8192-entry table and one of 1808, parsed through the catalog's parse path.
    var random = new Random(7);
    byte[] full = new byte[8192 * 16], rest = new byte[1808 * 16];
    foreach (byte[] table in new[] { full, rest })
        for (int i = 0; i < table.Length / 16; i++)
            TagHeaders.WriteEntryRecord(table.AsSpan(i * 16), (uint)random.Next(), (uint)random.Next(),
                random.Next(0x4000), random.Next(0x4000) * 16, random.Next(1 << 24));
    TagHeaders.ParseEntryTable(0x100, "warmup", full);
    var best = TimeSpan.MaxValue;
    int parsedCount = 0;
    for (int run = 0; run < 5; run++)
    {
        var watch = Stopwatch.StartNew();
        parsedCount = TagHeaders.ParseEntryTable(0x100, "a", full).Length + TagHeaders.ParseEntryTable(0x101, "b", rest).Length;
        watch.Stop();
        if (watch.Elapsed < best) best = watch.Elapsed;
    }
    Console.WriteLine($"Parsed {parsedCount} tag headers in {best.TotalMilliseconds:F2} ms (best of 5).");
    Check(parsedCount == 10_000 && best < TimeSpan.FromMilliseconds(100), "10,000 tag headers parse in under 100 ms (Phase 07)");

    var graph = new TagDependencyGraph();
    graph.Add(1, 2); graph.Add(2, 3); graph.Add(1, 3); graph.Add(4, 3);
    Check(!graph.TryAdd(3, 1) && !graph.TryAdd(5, 5) && graph.RejectedEdges.Count == 2 && graph.EdgeCount == 4,
        "the dependency graph rejects cycles and self references (Phase 07)");
    bool threw = false;
    try { graph.Add(3, 2); } catch (InvalidOperationException) { threw = true; }
    var order = graph.TopologicalOrder().ToList();
    Check(threw && order.Count == 4 && order.IndexOf(3) < order.IndexOf(2) && order.IndexOf(2) < order.IndexOf(1)
        && order.IndexOf(3) < order.IndexOf(4), "Add throws on a cycle; the topological order puts dependencies first (Phase 07)");
    var refs = new Dictionary<uint, uint[]> { [10] = [11, 12], [11] = [12, 10], [12] = [13], [13] = [] };
    var built = TagDependencyGraph.Build([10], tag => refs.GetValueOrDefault(tag) ?? [], maximumNodes: 3);
    Check(built.NodeCount == 3 && built.RejectedEdges.Contains((11u, 10u)) && built.Closure(10).SequenceEqual(new uint[] { 10, 11, 12 }),
        "Build walks references, rejects the back edge and stops at maximumNodes (Phase 07)");
}

if (gameRoot is null)
{
    Console.WriteLine("skipped: installed package timing and serial/parallel comparison (no --game-root).");
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

    // Phase 06 on installed (Oodle-compressed, AES-GCM) blocks: the largest multi-block tags, read cold both ways.
    uint[] sample = catalog.Entries.Where(x => x.Size > 0x40000).OrderByDescending(x => x.Size).ThenBy(x => x.TagHash)
        .Take(32).Select(x => x.TagHash).ToArray();
    using var serialCatalog = PackageCatalog.Open(gameRoot);
    using var parallelCatalog = PackageCatalog.Open(gameRoot);
    var serialTime = Stopwatch.StartNew();
    byte[][] serialBytes = sample.Select(serialCatalog.ReadOriginal).ToArray();
    serialTime.Stop();
    var parallelTime = Stopwatch.StartNew();
    byte[][] parallelBytes = sample.Select(t => parallelCatalog.ReadOriginalParallel(t)).ToArray();
    parallelTime.Stop();
    Console.WriteLine($"Installed multi-block tags: {sample.Length}, serial {serialTime.Elapsed.TotalMilliseconds:F0} ms, "
        + $"parallel {parallelTime.Elapsed.TotalMilliseconds:F0} ms ({parallelCatalog.MaximumParallelDecodes} decodes).");
    Check(sample.Length > 0 && serialBytes.Zip(parallelBytes).All(x => x.First.AsSpan().SequenceEqual(x.Second)),
        "installed parallel block reads are byte-identical to serial reads (Phase 06)");

    // Phase 07 on the installation: every entry locates by its hash, and a native-dependency DAG over entities.
    Check(catalog.Entries.All(e => catalog.TryLocate(e.TagHash, out var id, out int index) && id == e.PackageId && index == e.EntryIndex),
        $"all {catalog.Entries.Count} installed tag hashes map to their package id and entry index (Phase 07)");
    uint[] entities = catalog.Entries.Where(e => e.Reference == 0x80809C0F).Select(e => e.TagHash).Take(200).ToArray();
    int withConfigs = entities.Count(t => ComponentIndex.EntityConfigs(catalog.ReadOriginal(t)).Length > 0);
    IEnumerable<uint> NativeReferences(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry)) return [];
        try { return PackageNativeDependencies.Read(catalog, tag, entry.Reference, entry.TypeInfo, catalog.ReadOriginal(tag)); }
        catch (InvalidDataException) { return []; }
    }
    var dag = TagDependencyGraph.Build(entities, NativeReferences, maximumNodes: 5000);
    Console.WriteLine($"Entity DAG: {entities.Length} roots ({withConfigs} with configs), {dag.NodeCount} tags, "
        + $"{dag.EdgeCount} edges, {dag.RejectedEdges.Count} cycle edges rejected.");
    Check(entities.Length > 0 && withConfigs > 0 && dag.TopologicalOrder().Count == dag.NodeCount,
        "installed entity templates parse and their dependency graph is acyclic (Phase 07)");
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
