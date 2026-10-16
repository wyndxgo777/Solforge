using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** Checks the on-disk name cache: storage, reuse across installs and invalidation. */
internal static class PersistentNameCacheChecks
{
    /** Cache hits preserve all naming surfaces and reject changed or damaged inputs. */
    internal static void Synthetic(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "sunrise-cache-checks-" + Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "install"), storage = Path.Combine(root, "cache");
        Directory.CreateDirectory(install);
        try
        {
            Copy(Path.Combine(source, "packages"), Path.Combine(install, "packages"));
            Copy(Path.Combine(source, "bin"), Path.Combine(install, "bin"));
            RendererFixture(install);
            var options = new StudioCacheOptions { DirectoryPath = storage, MaximumBytes = 4 * 1024 * 1024 };
            using var coldCatalog = PackageCatalog.Open(install, cacheOptions: options);
            var cold = StudioNameIndex.Build(coldCatalog);
            Check(!cold.CacheHit && coldCatalog.DiskCache.Statistics.Writes == 1, "Cold names were not saved.");
            Check(cold.RenderNames!.Entries.Count == 2, "Cache fixture has no renderer rows.");
            using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
            {
                var warm = StudioNameIndex.Build(catalog);
                Check(warm.CacheHit && catalog.ReadStatistics.EntryReads == 0, "Warm names reread package payloads.");
                Compare(cold, warm, catalog.Entries);
                long writes = catalog.DiskCache.Statistics.Writes;
                catalog.ReadOverride = _ => null;
                var overlay = StudioNameIndex.Build(catalog);
                Check(!overlay.CacheHit && overlay.CacheStatus == "Project overlay"
                    && catalog.DiskCache.Statistics.Writes == writes, "Project override entered the disk cache.");
                catalog.ReadOverride = null;
                var entry = catalog.Entries[0];
                catalog.SetOverlayEntries([new(entry, null)]);
                Check(!StudioNameIndex.Build(catalog).CacheHit, "Metadata overlay entered the disk cache.");
            }
            string snapshot = Directory.GetFiles(Path.Combine(storage, "data-v1"), "*.sscache").Single();
            byte[] intact = File.ReadAllBytes(snapshot);
            File.WriteAllBytes(snapshot, intact.AsSpan(0, 37).ToArray());
            using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
            {
                Check(!StudioNameIndex.Build(catalog).CacheHit && catalog.DiskCache.Statistics.Rejected == 1
                    && catalog.DiskCache.Statistics.Writes == 1, "Damaged snapshot did not rebuild atomically.");
            }
            string package = Directory.GetFiles(Path.Combine(install, "packages"), "*0100*.pkg").Single();
            var stamp = File.GetLastWriteTimeUtc(package);
            byte[] bytes = File.ReadAllBytes(package);
            byte[] before = Encoding.UTF8.GetBytes("fixture_static_model"), after = Encoding.UTF8.GetBytes("changed_static_model");
            int name = bytes.AsSpan().IndexOf(before);
            Check(name >= 0 && before.Length == after.Length, "Package rename fixture is absent.");
            after.CopyTo(bytes, name); File.WriteAllBytes(package, bytes); File.SetLastWriteTimeUtc(package, stamp);
            using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
            {
                var changed = StudioNameIndex.Build(catalog);
                Check(!changed.CacheHit && changed.Describe(0x80A00001).StartsWith("Changed", StringComparison.Ordinal),
                    "Same-size package edit with restored write time reused stale names.");
            }
            string sdk = Path.Combine(install, "bin", "x64", "Sunrise", "sdk", "lua", "activities");
            Directory.CreateDirectory(sdk);
            File.WriteAllText(Path.Combine(sdk, "cache_extra.lua"), "name = \"cache_extra\"\n");
            using (var catalog = PackageCatalog.Open(install, cacheOptions: options))
                Check(!StudioNameIndex.Build(catalog).CacheHit, "Added SDK name input did not invalidate names.");
            string keySource = Path.Combine(root, "owned-key-source.bin");
            File.WriteAllBytes(keySource, [1, 2, 3, 4]);
            using (var catalog = PackageCatalog.Open(install, keySource, options))
                Check(!StudioNameIndex.Build(catalog).CacheHit, "Key source identity was omitted.");
            stamp = File.GetLastWriteTimeUtc(keySource);
            File.WriteAllBytes(keySource, [4, 3, 2, 1]); File.SetLastWriteTimeUtc(keySource, stamp);
            using (var catalog = PackageCatalog.Open(install, keySource, options))
            {
                Check(!StudioNameIndex.Build(catalog).CacheHit, "Changed key source reused names.");
                catalog.DiskCache.Clear();
                Check(catalog.DiskCache.Statistics.StoredBytes == 0, "Clear retained cache snapshots.");
            }
            string project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "sunrise.project.json"), "{}");
            using (var catalog = PackageCatalog.Open(install, cacheOptions: options with { DirectoryPath = project }))
            {
                Check(!catalog.DiskCache.Enabled, "Cache accepted a delta project directory.");
                StudioNameIndex.Build(catalog);
                Check(!Directory.Exists(Path.Combine(project, "data-v1")), "Name payloads were written into a project.");
            }
            using (var catalog = PackageCatalog.Open(install, cacheOptions: options with { DirectoryPath = install }))
                Check(!catalog.DiskCache.Enabled, "Cache accepted the installation directory.");
            NameCacheSafetyChecks.IndexMutation(install, options);
            NameCacheSafetyChecks.Storage(install, options);
            StoreChecks(install, options);
            Console.WriteLine("name_cache cold=saved warm=zero-payload-reads fidelity=full corruption=recovered package=same-time-invalidated sdk=invalidated key=invalidated overlay=bypassed project=clean bound=pass");
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    }

    /** All public category and ownership lookups must agree after restoring a snapshot. */
    internal static void Compare(StudioNameIndex cold, StudioNameIndex warm, IReadOnlyList<PackageEntry> entries)
    {
        Check(cold.Coverage with { Stage = "" } == warm.Coverage with { Stage = "" }, "Cached name coverage differs.");
        Check(JsonSerializer.Serialize(cold.RenderNames!.Entries) == JsonSerializer.Serialize(warm.RenderNames!.Entries)
            && JsonSerializer.Serialize(cold.RenderNames.Conflicts) == JsonSerializer.Serialize(warm.RenderNames.Conflicts)
            && cold.RenderNames.UnsetRowCount == warm.RenderNames.UnsetRowCount, "Cached renderer names differ.");
        var first = cold.GameNames!; var second = warm.GameNames!;
        var firstComponents = first.Components!; var secondComponents = second.Components!;
        Check(first.MapCount == second.MapCount && first.ScenarioCount == second.ScenarioCount
            && first.Strings.ContainerCount == second.Strings.ContainerCount
            && first.Strings.StringCount == second.Strings.StringCount
            && first.Strings.AmbiguousCount == second.Strings.AmbiguousCount, "Cached localization counts differ.");
        foreach (var entry in entries)
        {
            uint tag = entry.TagHash;
            var coldCategory = AssetCategories.Classify(entry, first);
            var warmCategory = AssetCategories.Classify(entry, second);
            Check(coldCategory.Category == warmCategory.Category && coldCategory.Tags.SequenceEqual(warmCategory.Tags),
                $"Cached browser category differs: {tag:X8}.");
            Check(JsonSerializer.Serialize(cold.Lookup(entry)) == JsonSerializer.Serialize(warm.Lookup(entry)),
                $"Cached asset label differs: {tag:X8}.");
            Check(cold.GetAssetOwners(tag).SequenceEqual(warm.GetAssetOwners(tag))
                && first.Describe(entry) == second.Describe(entry)
                && firstComponents.EntityKind(tag) == secondComponents.EntityKind(tag)
                && firstComponents.PrimaryClass(tag) == secondComponents.PrimaryClass(tag)
                && firstComponents.Prompt(tag) == secondComponents.Prompt(tag)
                && firstComponents.CombatantName(tag) == secondComponents.CombatantName(tag)
                && firstComponents.Pattern(tag) == secondComponents.Pattern(tag)
                && firstComponents.SoundId(tag) == secondComponents.SoundId(tag)
                && firstComponents.ObjectType(tag) == secondComponents.ObjectType(tag)
                && firstComponents.InternalNameHash(tag) == secondComponents.InternalNameHash(tag)
                && firstComponents.EntitySound(tag) == secondComponents.EntitySound(tag)
                && firstComponents.ComponentLabel(firstComponents.PrimaryClass(tag))
                    == secondComponents.ComponentLabel(secondComponents.PrimaryClass(tag)),
                $"Cached entity categories or owners differ: {tag:X8}.");
            bool a = firstComponents.TryGetModelTag(tag, out uint modelA);
            bool b = secondComponents.TryGetModelTag(tag, out uint modelB);
            Check(a == b && modelA == modelB, "Cached typed model link differs.");
            Check(first.Scenario(tag) == second.Scenario(tag)
                && first.MapTitle(tag) == second.MapTitle(tag) && first.MapDevName(tag) == second.MapDevName(tag)
                && first.BubbleNameHash(tag) == second.BubbleNameHash(tag), "Cached world name metadata differs.");
            Check(first.Strings.Get(tag, 0x1234) == second.Strings.Get(tag, 0x1234), "Cached scoped text differs.");
            bool coldItem = cold.TryGetItem(tag, out var coldFacts), warmItem = warm.TryGetItem(tag, out var warmFacts);
            Check(coldItem == warmItem && coldFacts == warmFacts, $"Cached item facts differ: {tag:X8}.");
        }
        foreach (uint hash in new uint[] { 0x1234, 0x109CBBE3 })
            Check(first.Strings.Candidates(hash).SequenceEqual(second.Strings.Candidates(hash))
                && cold.ResolveHash(hash) == warm.ResolveHash(hash)
                && cold.HashCandidates(hash).SequenceEqual(warm.HashCandidates(hash)), "Cached hash ambiguity differs.");
        for (uint index = 0; index < 100000; index++)
            Check(cold.ItemName(index) == warm.ItemName(index) && cold.PerkName(index) == warm.PerkName(index),
                "Cached investment index differs.");
    }

    /** Separate catalogs prove that warm names need no prior process-local decoded blocks. */
    internal static void Installed(string gameRoot)
    {
        string root = Path.Combine(Path.GetTempPath(), "sunrise-cache-benchmark-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new StudioCacheOptions { DirectoryPath = root };
            var watch = Stopwatch.StartNew();
            using var coldCatalog = PackageCatalog.Open(gameRoot, cacheOptions: options);
            double coldOpen = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var cold = StudioNameIndex.Create(coldCatalog);
            long next = 0;
            cold.Load(progress: stage =>
            {
                if (watch.ElapsedMilliseconds < next) return;
                next = watch.ElapsedMilliseconds + 5000;
                Console.WriteLine($"CACHE_COLD stage={stage.Replace(' ', '_')} elapsed_ms={watch.ElapsedMilliseconds}");
            });
            double coldLoad = watch.Elapsed.TotalMilliseconds;
            Check(cold.CacheStatus == "Saved", $"Owned cache save failed: {coldCatalog.DiskCache.Statistics.LastError}");
            watch.Restart();
            using var warmCatalog = PackageCatalog.Open(gameRoot, cacheOptions: options);
            double warmOpen = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var warm = StudioNameIndex.Build(warmCatalog);
            double warmLoad = watch.Elapsed.TotalMilliseconds;
            Check(warm.CacheHit && warmCatalog.ReadStatistics.EntryReads == 0, "Owned warm load missed its complete cache.");
            watch.Restart();
            Compare(cold, warm, warmCatalog.Entries);
            Console.WriteLine($"CACHE_BENCH cold_open_ms={coldOpen:F1} cold_names_ms={coldLoad:F1} warm_open_ms={warmOpen:F1} "
                + $"warm_names_ms={warmLoad:F1} compare_ms={watch.Elapsed.TotalMilliseconds:F1} bytes={warmCatalog.DiskCache.Statistics.StoredBytes} "
                + $"tags={warm.Coverage.NamedTags} hashes={warm.Coverage.NamedHashes} components={warm.GameNames!.Components!.EntityCount} "
                + $"configs={warm.GameNames.Components.ConfigCount} read_errors={warm.Coverage.ReadErrors} payload_reads={warmCatalog.ReadStatistics.EntryReads}");
            var worldLoader = new WorldLoader(warmCatalog, warm);
            var before = warmCatalog.ReadStatistics;
            watch.Restart();
            var worlds = worldLoader.BuildCatalog();
            var indexed = warmCatalog.ReadStatistics;
            Console.WriteLine($"CACHE_WORLD phase=catalog elapsed_ms={watch.Elapsed.TotalMilliseconds:F1} "
                + $"maps={worlds.Maps.Count} missions={worlds.Missions.Count} reads={indexed.EntryReads - before.EntryReads} "
                + $"io_ms={(indexed.ReadTime - before.ReadTime).TotalMilliseconds:F1} decode_ms={(indexed.DecodeTime - before.DecodeTime).TotalMilliseconds:F1}");
            watch.Restart();
            var scene = worldLoader.LoadMission(0x80EDC19B, 0x80B48062, new() { IncludeLowerLods = false });
            var loaded = warmCatalog.ReadStatistics;
            Console.WriteLine($"CACHE_WORLD phase=scene elapsed_ms={watch.Elapsed.TotalMilliseconds:F1} "
                + $"instances={scene.Scene.Instances.Count} markers={scene.Scene.Markers.Count} "
                + $"reads={loaded.EntryReads - indexed.EntryReads} blocks={loaded.BlockReads - indexed.BlockReads} "
                + $"io_ms={(loaded.ReadTime - indexed.ReadTime).TotalMilliseconds:F1} decode_ms={(loaded.DecodeTime - indexed.DecodeTime).TotalMilliseconds:F1}");
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    }

    private static void StoreChecks(string install, StudioCacheOptions options)
    {
        using var catalog = PackageCatalog.Open(install, cacheOptions: options with { MaximumBytes = 32 * 1024 });
        var cache = catalog.DiskCache; cache.Clear();
        byte[] payload = new byte[20 * 1024]; new Random(73).NextBytes(payload);
        Check(cache.Write("first", stream => stream.Write(payload)), "Cache did not accept bounded data.");
        Check(cache.Write("second", stream => stream.Write(payload)) && cache.Statistics.StoredBytes <= 32 * 1024,
            "Cache exceeded its configured bound.");
        Check(!cache.TryRead("first", _ => { }), "Cache did not evict the oldest snapshot.");
        Check(!cache.Write("second", _ => throw new IOException("Interrupted fixture")), "Failed write was published.");
        Check(cache.TryRead("second", stream =>
        {
            byte[] actual = new byte[payload.Length]; stream.ReadExactly(actual);
            Check(actual.SequenceEqual(payload), "Failed write replaced the last complete snapshot.");
        }), "Atomic write failure discarded the preceding snapshot.");
    }

    private static void RendererFixture(string install)
    {
        const uint first = 0x80A02000;
        byte[] root = new byte[512], scope = new byte[32], map = new byte[32], definition = new byte[96], bubble = new byte[32];
        void U(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        void Q(byte[] data, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset), value);
        void Text(byte[] data, int offset, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
            U(data, offset - 12, 0x80800065); Q(data, offset - 8, bytes.Length); bytes.CopyTo(data, offset);
        }
        void Rows(int field, int header, uint type, uint target, int text, string name)
        {
            Q(root, field, 1); Q(root, field + 8, header - field - 8); U(root, header - 4, 0x80809FBD);
            Q(root, header, 1); U(root, header + 8, type); Q(root, header + 16, text - header - 16);
            U(root, header + 28, target); Text(root, text, name);
        }
        Rows(0x10, 0x70, 0x80806CB6, first + 1, 0x110, "cache_scope");
        Rows(0x20, 0xA0, 0x80806CB5, first + 2, 0x140, "cache_technique");
        U(map, 8, first + 4); Q(definition, 8, 0x40 - 8); Text(definition, 0x40, "cache_world");
        U(bubble, 24, 0x1234);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_cache_renderer_0101_0.pkg"), 0x101,
            [new(0x80806CB1, 8 << 9, root), new(0x808071F3, 8 << 9, scope),
             new(0x808071E8, 8 << 9, new byte[8]), new(0x808091DE, 8 << 9, map),
             new(0x80809962, 8 << 9, definition), new(0x80807DAE, 8 << 9, bubble)]);
    }

    private static void Copy(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string output = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(file, output);
        }
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidDataException(message); }
}
