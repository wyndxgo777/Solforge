using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** Checks that project-derived names are never persisted into the shared name cache, even when changes race with cache publication. */
internal static class NameCacheSafetyChecks
{
    /** Once an index has observed overlays, removing them cannot make its old buckets cacheable. */
    internal static void IndexMutation(string install, StudioCacheOptions options)
    {
        using var catalog = PackageCatalog.Open(install, cacheOptions: options);
        var cache = catalog.DiskCache; cache.Clear();
        var names = StudioNameIndex.Create(catalog);
        catalog.ReadOverride = tag =>
        {
            if (tag != 0x80A00007) return null;
            byte[] bytes = catalog.ReadOriginal(tag);
            Encoding.UTF8.GetBytes("Project-only name").CopyTo(bytes, 224);
            return bytes;
        };
        names.Load();
        catalog.ReadOverride = null;
        names.Load();
        Check(cache.Statistics.Writes == 0 && cache.Statistics.StoredBytes == 0,
            "Reused overlay index persisted project-derived names.");
        var clean = StudioNameIndex.Create(catalog);
        clean.Load(progress: phase =>
        {
            if (phase == "Saving name cache") clean.NameScene(new SceneDocument());
        });
        Check(cache.Statistics.Writes == 0 && cache.Statistics.StoredBytes == 0,
            "Scene labels added during cache publication bypassed its guard.");
        var revised = StudioNameIndex.Create(catalog);
        revised.Load(progress: phase =>
        {
            if (phase == "Saving name cache") catalog.ReadOverride = _ => null;
        });
        Check(cache.Statistics.Writes == 0 && cache.Statistics.StoredBytes == 0,
            "An overlay change during cache publication bypassed its guard.");
        catalog.ReadOverride = null;
        var restored = StudioNameIndex.Build(catalog);
        Check(!restored.CacheHit && restored.Describe(0x80A00000) == "Friendly activity",
            "A clean index restored project-derived text.");
        Console.WriteLine("name_cache_safety reused_overlay=not-persisted scene_callback=not-persisted overlay_callback=not-persisted");
    }

    /** Malformed valid-checksum data and rejected roots cannot escape recovery or redirect cleanup. */
    internal static void Storage(string install, StudioCacheOptions options)
    {
        using var catalog = PackageCatalog.Open(install, cacheOptions: options);
        var cache = catalog.DiskCache; cache.Clear();
        Check(cache.Write("bad-varint", stream => stream.Write(new byte[] { 128, 128, 128, 128, 128, 128 })),
            "Malformed-varint fixture was not written.");
        Check(!cache.TryRead("bad-varint", stream => new BinaryReader(stream).Read7BitEncodedInt())
            && cache.Statistics.Rejected > 0, "Malformed cached varint escaped corruption recovery.");
        Check(cache.Write("guarded-root", stream => stream.WriteByte(42)), "Guarded root fixture was not written.");
        string file = Directory.GetFiles(cache.DirectoryPath, "*.sscache").Single();
        string projectMarker = Path.Combine(options.DirectoryPath, "sunrise.project.json");
        File.WriteAllText(projectMarker, "{}");
        try
        {
            Check(!cache.TryRead("guarded-root", _ => { }) && File.Exists(file),
                "Rejected cache root still deleted its entry during cleanup.");
        }
        finally { File.Delete(projectMarker); }
        LinkChecks(cache, options.DirectoryPath, file);
        string alias = options.DirectoryPath + "-install-alias";
        bool linked = false;
        try
        {
            Directory.CreateSymbolicLink(alias, install); linked = true;
            using var aliased = PackageCatalog.Open(alias, cacheOptions: options with
            { DirectoryPath = Path.Combine(install, "cache-must-not-exist") });
            Check(!aliased.DiskCache.Enabled, "Installation alias allowed cache data into the physical installation.");
            Console.WriteLine("cache_path installation_alias=protected");
        }
        catch (Exception error) when (!linked && error is IOException or UnauthorizedAccessException)
        { Console.WriteLine("cache_path installation_alias=not-available"); }
        finally { if (linked) Directory.Delete(alias); }
        cache.Clear();
        Check(!cache.Write("refused", stream => stream.WriteByte(7), canCommit: () => false)
            && cache.Statistics.StoredBytes == 0, "Commit rejection left a published snapshot.");
        Console.WriteLine("cache_storage malformed-varint=miss invalid-root=untouched commit-guard=atomic");
    }

    /** Linked cache leaves and directories must never touch the redirected target. */
    private static void LinkChecks(StudioDiskCache cache, string root, string file)
    {
        string target = Path.Combine(root, "external-entry.bin");
        File.Copy(file, target);
        File.Delete(file);
        bool leaf = false;
        try
        {
            File.CreateSymbolicLink(file, target); leaf = true;
            DateTime stamp = File.GetLastWriteTimeUtc(target);
            byte[] bytes = File.ReadAllBytes(target);
            Check(!cache.TryRead("guarded-root", stream => stream.ReadByte())
                && File.Exists(target) && File.GetLastWriteTimeUtc(target) == stamp
                && File.ReadAllBytes(target).SequenceEqual(bytes), "Linked cache leaf touched its target.");
            Console.WriteLine("cache_path linked_leaf=untouched");
        }
        catch (Exception error) when (!leaf && error is IOException or UnauthorizedAccessException)
        { Console.WriteLine("cache_path linked_leaf=not-available"); }
        finally { if (leaf) File.Delete(file); }
        string original = cache.DirectoryPath + "-original", redirected = Path.Combine(root, "redirected");
        string boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string path in new[] { cache.DirectoryPath, original, redirected })
            Check(Path.GetFullPath(path).StartsWith(boundary, StringComparison.OrdinalIgnoreCase),
                "Cache link fixture left its temporary root.");
        Directory.CreateDirectory(redirected);
        string sentinel = Path.Combine(redirected, Path.GetFileName(file));
        File.Copy(target, sentinel);
        Directory.Move(cache.DirectoryPath, original);
        bool directory = false;
        try
        {
            Directory.CreateSymbolicLink(cache.DirectoryPath, redirected); directory = true;
            Check(!cache.TryRead("guarded-root", _ => { }) && File.Exists(sentinel),
                "Rejected directory link redirected cache cleanup.");
            Console.WriteLine("cache_path linked_directory=untouched");
        }
        catch (Exception error) when (!directory && error is IOException or UnauthorizedAccessException)
        { Console.WriteLine("cache_path linked_directory=not-available"); }
        finally
        {
            if (directory) Directory.Delete(cache.DirectoryPath);
            Directory.Move(original, cache.DirectoryPath);
        }
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidDataException(message); }
}
