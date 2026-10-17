/**
 * File: The one entry point to the retail data: payload files, the compact index, the lookup cache, the class map, the
 * retail schema registry and the tiger-mcp engine. Everything heavy loads lazily, once, and degrades with a reason.
 */
using System.Collections.Concurrent;
using System.Globalization;
using SunriseStudio.Packages.TagSchemas;

namespace Solforge.Retail;

/** The retail (build 245048) schemas: a registry of their own with the retail array marker 0x80809FB8. */
public static class RetailSchemas
{
    private const string Prefix = "Solforge.Retail.Schemas.";
    private static readonly Lazy<TagSchemaRegistry> Embedded = new(() =>
    {
        var assembly = typeof(RetailSchemas).Assembly;
        var sources = new List<(string, string)>();
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            sources.Add((name[Prefix.Length..], reader.ReadToEnd()));
        }
        return TagSchemaRegistry.FromSources(sources, RetailTag.ArrayMarker);
    });

    /** The embedded retail schemas (never mixed with TagSchemaRegistry.Default, which describes 86657). */
    public static TagSchemaRegistry Registry => Embedded.Value;
}

/** Retail data access shared by the MCP tools, the editor's Retail workspace and the porting code. Thread-safe. */
public sealed class RetailSource : IDisposable
{
    private static readonly Lazy<RetailSource> SharedSource = new(() => new RetailSource(RetailOptions.FromEnvironment()));
    private readonly Lazy<(RetailIndex? Index, string? Error)> index;
    private readonly Lazy<(RetailLookupCache Cache, string? Error)> lookup;
    private readonly Lazy<Dictionary<ushort, List<string>>> folders;
    private readonly ConcurrentDictionary<uint, byte[]> cache = new();
    private long cachedBytes;
    private Task? indexTask;
    private readonly object indexGate = new();

    public RetailOptions Options { get; }
    public TigerEngine Engine { get; }
    public RetailClassMap ClassMap => RetailClassMap.Default;
    public TagSchemaRegistry Schemas => RetailSchemas.Registry;

    /** The process-wide source configured from the environment (RetailOptions.FromEnvironment). */
    public static RetailSource Shared => SharedSource.Value;

    /** progress, when set, hears about index build progress (rows parsed). */
    public Action<string>? Progress { get; set; }
    /** The last index progress line ("Indexing retail tags: 1,500,000 rows"), empty before any. */
    public string IndexProgress { get; private set; } = "";

    public RetailSource(RetailOptions options)
    {
        Options = options;
        Engine = new TigerEngine(options.EngineUrl, options.EngineTimeoutSeconds);
        index = new(LoadIndex, LazyThreadSafetyMode.ExecutionAndPublication);
        lookup = new(LoadLookup, LazyThreadSafetyMode.ExecutionAndPublication);
        folders = new(ScanFolders, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /** Whether the extraction root exists. */
    public bool RootExists => Directory.Exists(Options.Root);
    /** Whether the index has been loaded (or failed) already, so a call will not wait for it. */
    public bool IndexLoaded => index.IsValueCreated;

    /**
     * Starts loading the index on a worker (once) and waits up to wait for it; false while it is still loading. The
     * first build parses 8.6M TSV rows (about a minute); later loads read the binary cache in seconds.
     */
    public async Task<bool> WaitForIndexAsync(TimeSpan wait, CancellationToken cancel)
    {
        if (index.IsValueCreated) return true;
        Task task;
        lock (indexGate) task = indexTask ??= Task.Run(() => { _ = index.Value; });
        if (task.IsCompleted) return true;
        var finished = await Task.WhenAny(task, Task.Delay(wait, cancel)).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        return finished == task;
    }

    /** The tag index (built from the TSVs the first time, about a minute; then loaded from the cache). Null with IndexError. */
    public RetailIndex? Index => index.Value.Index;
    public string? IndexError => index.Value.Error;
    /** The lookup cache (empty when the file is missing). */
    public RetailLookupCache Lookup => lookup.Value.Cache;
    public string? LookupError => lookup.Value.Error;

    private (RetailIndex?, string?) LoadIndex()
    {
        try
        {
            if (!RootExists) return (null, $"The retail extraction root {Options.Root} does not exist (set SOLFORGE_RETAIL_ROOT).");
            return (RetailIndex.Open(Options.Root, Options.CacheFolder,
                rows =>
                {
                    IndexProgress = $"Indexing retail tags: {rows.ToString("N0", CultureInfo.InvariantCulture)} rows";
                    Progress?.Invoke(IndexProgress);
                }), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { return (null, "The retail index could not load: " + error.Message); }
    }

    private (RetailLookupCache, string?) LoadLookup()
    {
        if (Options.LookupCache is not { Length: > 0 } path) return (RetailLookupCache.Empty, "No lookup cache configured; 64-bit hashes stay unresolved.");
        if (!File.Exists(path)) return (RetailLookupCache.Empty, $"Lookup cache {path} not found; 64-bit hashes stay unresolved (start tiger-mcp -v d2_ren once to create it).");
        try { return (RetailLookupCache.Load(path), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        { return (RetailLookupCache.Empty, "The lookup cache could not load: " + error.Message); }
    }

    /** payloads\<stem> folders under Root and MediaRoot by package id (from the stem's "_<id>_<patch>" tail). */
    private Dictionary<ushort, List<string>> ScanFolders()
    {
        var result = new Dictionary<ushort, List<string>>();
        foreach (string? root in new[] { Options.Root, Options.MediaRoot })
        {
            if (root is not { Length: > 0 }) continue;
            string payloads = Path.Combine(root, "payloads");
            if (!Directory.Exists(payloads)) continue;
            foreach (string folder in Directory.EnumerateDirectories(payloads))
            {
                string[] parts = Path.GetFileName(folder).Split('_');
                if (parts.Length < 3 || !ushort.TryParse(parts[^2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort id)) continue;
                if (!result.TryGetValue(id, out var list)) result[id] = list = [];
                list.Add(folder);
            }
        }
        return result;
    }

    /** The payload file of a tag, or null when none was extracted (redacted packages, empty tags, other drives). */
    public string? PayloadPath(uint tag)
    {
        if (!RetailTag.Plausible(tag)) return null;
        string file = tag.ToString("x8", CultureInfo.InvariantCulture) + ".bin";
        // The index names the package file; its stem is the folder. Only use it when the index is already loaded.
        if (IndexLoaded && Index?.Find(tag) is { } entry)
        {
            string stem = Path.GetFileNameWithoutExtension(entry.Package);
            foreach (string? root in new[] { Options.Root, Options.MediaRoot })
                if (root is { Length: > 0 } && Path.Combine(root, "payloads", stem, file) is var path && File.Exists(path)) return path;
        }
        if (folders.Value.TryGetValue(RetailTag.Package(tag), out var candidates))
            foreach (string folder in candidates)
                if (Path.Combine(folder, file) is var path && File.Exists(path)) return path;
        return null;
    }

    /** The tag's bytes, or null when not extracted. Recent payloads stay cached (up to 256 MB). */
    public byte[]? Read(uint tag)
    {
        if (cache.TryGetValue(tag, out var hit)) return hit;
        if (PayloadPath(tag) is not { } path) return null;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        if (Interlocked.Add(ref cachedBytes, bytes.Length) > 256L << 20) { cache.Clear(); Interlocked.Exchange(ref cachedBytes, bytes.Length); }
        cache[tag] = bytes;
        return bytes;
    }

    /** The tag a WideHash names, or null when it is none or the 64-bit hash is not in the lookup cache. */
    public uint? Resolve(WideHash hash)
    {
        if (hash.IsNone) return null;
        return hash.Is32 ? hash.Hash32 : Lookup.Resolve(hash.Hash64);
    }

    /** The WideHash at offset resolved to a tag, or null. */
    public uint? Wide(ReadOnlySpan<byte> bytes, int offset) => Resolve(RetailBytes.Wide(bytes, offset));

    /** The class of a struct tag (file types 8 and 16) from the index (loads it on first use), or null. */
    public uint? ClassOf(uint tag) => Index?.Find(tag) is { IsStruct: true } entry ? entry.Reference : null;

    /** The class of a tag only when the index is already loaded (never blocks on building it). */
    public uint? ClassIfLoaded(uint tag) => IndexLoaded ? ClassOf(tag) : null;

    /** A display name for a retail class: the retail schema, the class map, or the hex. */
    public string ClassName(uint classHash) =>
        Schemas.Find(classHash)?.Name ?? ClassMap.NameOf(classHash) ?? RetailTag.Hex(classHash);

    /** The engine's name for a tag (named tags of the lookup cache), or null. */
    public string? NameOf(uint tag) => Lookup.NameOf(tag)?.Name;

    public void Dispose() => Engine.Dispose();
}
