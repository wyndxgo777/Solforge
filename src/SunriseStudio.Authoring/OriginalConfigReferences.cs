using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

/** Only immutable original bytes enter this candidate index; raw changes are read on every compile. */
internal static partial class OriginalConfigReferences
{
    private const uint Magic = 0x33524353;
    private const int MaximumLinks = 2_000_000;
    internal static string Schema => $"static-isolation-references-v3/{typeof(OriginalConfigReferences).Module.ModuleVersionId}";
    /** Original-install reference candidates, table bounds, nodes, failed tags and source count. */
    internal sealed record Snapshot(IReadOnlyDictionary<uint, uint[]> Candidates,
        IReadOnlyDictionary<uint, uint> TableBounds, IReadOnlyDictionary<uint, IsolationNode> Nodes,
        IReadOnlySet<uint> Failed, int SourceCount);
    private sealed class Holder
    {
        internal readonly object Gate = new();
        internal string? Identity;
        internal Snapshot? Value;
        internal long Generation;
    }
    private static readonly ConditionalWeakTable<PackageCatalog, Holder> Memory = new();

    /** Startup can warm complete facts without making unreadable configs block view-only use. */
    internal static void Prepare(PackageCatalog catalog, CancellationToken token, Action<int, int>? progress)
    {
        token.ThrowIfCancellationRequested();
        if (!catalog.DiskCache.Enabled) return;
        try { TryCandidates(catalog, new HashSet<uint>(), new HashSet<uint>(), out _, token, progress, true); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
    }

    /** A missing source identity disables reuse; failed original reads cannot certify absence. */
    internal static bool TryCandidates(PackageCatalog catalog, IReadOnlySet<uint> owners,
        IReadOnlySet<uint> rawChanged, out IReadOnlySet<uint> candidates, CancellationToken token = default,
        Action<int, int>? progress = null, bool allowFailures = false)
    {
        var snapshot = Load(catalog, rawChanged, token, progress, allowFailures);
        candidates = snapshot is null ? new HashSet<uint>() : owners
            .SelectMany(owner => snapshot.Candidates.GetValueOrDefault(owner) ?? []).Concat(rawChanged).ToHashSet();
        return snapshot is not null;
    }

    /** Original isolation nodes for the catalog; false when no snapshot exists. */
    internal static bool TryNodes(PackageCatalog catalog, IReadOnlySet<uint> rawChanged,
        out IReadOnlyDictionary<uint, IsolationNode> nodes)
    {
        var snapshot = Load(catalog, rawChanged);
        nodes = snapshot?.Nodes ?? new Dictionary<uint, IsolationNode>();
        return snapshot is not null;
    }

    /** Original reference snapshot for the catalog, or null when it cannot be built. */
    internal static Snapshot? Open(PackageCatalog catalog, IReadOnlySet<uint> rawChanged) => Load(catalog, rawChanged);

    /** Complete snapshots survive raw-layer changes; failed originals retry after invalidation. */
    private static Snapshot? Load(PackageCatalog catalog, IReadOnlySet<uint> rawChanged,
        CancellationToken token = default, Action<int, int>? progress = null, bool allowFailures = false)
    {
        token.ThrowIfCancellationRequested();
        string? identity = catalog.CreateOriginalCacheIdentity(Schema, token: token);
        if (identity is null) { catalog.RequireCurrentOriginalPackages(token); return null; }
        var holder = Memory.GetValue(catalog, static _ => new());
        Snapshot snapshot;
        lock (holder.Gate)
        {
            if (holder.Identity == identity && holder.Value is { } ready
                && (ready.Failed.Count == 0 || holder.Generation == catalog.ReadGeneration)) snapshot = ready;
            else
            {
                uint[] configTags = catalog.Entries.Where(entry => entry.Reference == 0x80809C36)
                    .Select(entry => entry.TagHash).Order().ToArray();
                var allOwners = catalog.Entries.Where(entry => entry.Reference == 0x80806EF4)
                    .Select(entry => entry.TagHash).ToHashSet();
                var tableTags = catalog.Entries.Where(entry => entry.Reference == 0x8080966D)
                    .Select(entry => entry.TagHash).Order().ToArray();
                var nodeEntries = catalog.Entries.Where(entry => InstanceIsolationFacts.Classes.Contains(entry.Reference))
                    .ToDictionary(entry => entry.TagHash);
                Snapshot? cached = null;
                bool loaded = catalog.DiskCache.TryRead(identity, stream => cached = Read(stream, configTags, tableTags, nodeEntries, allOwners), token);
                bool reused = loaded && cached is { Failed.Count: 0 };
                snapshot = reused ? cached! : Build(catalog, allOwners, configTags.Length + tableTags.Length + nodeEntries.Count, token, progress);
                if (catalog.CreateOriginalCacheIdentity(Schema, token: token) != identity)
                    throw new IOException("The original config sources changed during isolation admission.");
                if (!reused) catalog.DiskCache.Write(identity, stream => Write(stream, configTags.Length, tableTags.Length, nodeEntries.Count, snapshot), token,
                    canCommit: () => catalog.CreateOriginalCacheIdentity(Schema) == identity);
                holder.Identity = identity; holder.Value = snapshot; holder.Generation = catalog.ReadGeneration;
            }
        }
        token.ThrowIfCancellationRequested();
        if (catalog.CreateOriginalCacheIdentity(Schema, token: token) != identity)
            throw new IOException("The original config sources changed during isolation admission.");
        progress?.Invoke(snapshot.SourceCount, snapshot.SourceCount);
        uint failed = snapshot.Failed.FirstOrDefault(tag => !rawChanged.Contains(tag));
        if (!allowFailures && failed != 0) throw new InvalidDataException($"Original source {failed:X8} could not be read; static placement scope is unknown.");
        return snapshot;
    }

    /** Unaligned byte matches remain candidates, so malformed pointers cannot evade typed refusal. */
    private static Snapshot Build(PackageCatalog catalog, HashSet<uint> owners, int total,
        CancellationToken token, Action<int, int>? progress)
    {
        var candidates = new Dictionary<uint, HashSet<uint>>();
        var failed = new HashSet<uint>();
        var tableBounds = new Dictionary<uint, uint>();
        var nodes = new Dictionary<uint, IsolationNode>();
        var highBytes = SearchValues.Create(owners.Select(owner => (byte)(owner >> 24)).Distinct().ToArray());
        int links = 0, index = 0, facts = 0;
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference is 0x80809C36 or 0x8080966D
            || InstanceIsolationFacts.Classes.Contains(entry.Reference)))
        {
            token.ThrowIfCancellationRequested();
            if ((index & 255) == 0) progress?.Invoke(index, total);
            token.ThrowIfCancellationRequested();
            index++;
            byte[] bytes;
            try { bytes = catalog.ReadOriginal(entry.TagHash); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or ArgumentException or OverflowException or KeyNotFoundException)
            { failed.Add(entry.TagHash); continue; }
            if (entry.Reference == 0x8080966D)
            {
                if (bytes.Length < 0x1C) failed.Add(entry.TagHash);
                else tableBounds.Add(entry.TagHash, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x18)));
                continue;
            }
            if (InstanceIsolationFacts.Classes.Contains(entry.Reference))
            {
                var node = InstanceIsolationFacts.Parse(entry.TagHash, entry.Reference, bytes, owners);
                facts = checked(facts + node.Placements.Length + node.Maps.Length + node.Children.Length + node.OwnerCandidates.Length);
                if (facts > MaximumLinks) throw new InvalidDataException("The isolation metadata index exceeds its limit.");
                nodes.Add(entry.TagHash, node); continue;
            }
            for (int at = 0; at <= bytes.Length - 4; at++)
            {
                int skip = bytes.AsSpan(at + 3).IndexOfAny(highBytes);
                if (skip < 0) break;
                at += skip;
                uint owner = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
                if (!owners.Contains(owner)) continue;
                if (!candidates.TryGetValue(owner, out var users)) candidates.Add(owner, users = []);
                if (users.Add(entry.TagHash) && ++links > MaximumLinks)
                    throw new InvalidDataException("The original config reference index exceeds its limit.");
            }
        }
        return new(candidates.ToDictionary(pair => pair.Key, pair => pair.Value.Order().ToArray()), tableBounds, nodes, failed, total);
    }

    private static void Write(Stream stream, int configs, int tables, int nodes, Snapshot snapshot)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic); writer.Write(configs); writer.Write(tables); writer.Write(nodes); writer.Write(snapshot.Candidates.Count);
        foreach (var (owner, tags) in snapshot.Candidates.OrderBy(pair => pair.Key))
        {
            writer.Write(owner); writer.Write(tags.Length);
            foreach (uint tag in tags) writer.Write(tag);
        }
        writer.Write(snapshot.TableBounds.Count);
        foreach (var (table, bounds) in snapshot.TableBounds.OrderBy(pair => pair.Key))
        { writer.Write(table); writer.Write(bounds); }
        WriteNodes(writer, snapshot.Nodes);
        writer.Write(snapshot.Failed.Count);
        foreach (uint tag in snapshot.Failed.Order()) writer.Write(tag);
    }

    /** Cache rows must belong to this exact installed catalog before they can certify absence. */
    private static Snapshot Read(Stream stream, uint[] configs, uint[] tables,
        IReadOnlyDictionary<uint, PackageEntry> nodeEntries, HashSet<uint> owners)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != configs.Length || reader.ReadInt32() != tables.Length
            || reader.ReadInt32() != nodeEntries.Count)
            throw new InvalidDataException("The config reference cache schema differs.");
        var configTags = configs.ToHashSet();
        var tableTags = tables.ToHashSet();
        var candidates = new Dictionary<uint, uint[]>();
        int count = Count(reader, owners.Count), links = 0;
        for (int index = 0; index < count; index++)
        {
            uint owner = reader.ReadUInt32();
            if (!owners.Contains(owner) || candidates.ContainsKey(owner))
                throw new InvalidDataException("The config cache owner is invalid.");
            int length = Count(reader, Math.Min(configs.Length, MaximumLinks - links)); links += length;
            uint[] tags = new uint[length];
            for (int row = 0; row < length; row++)
            {
                tags[row] = reader.ReadUInt32();
                if (!configTags.Contains(tags[row]) || row > 0 && tags[row] <= tags[row - 1])
                    throw new InvalidDataException("The config cache source is invalid.");
            }
            candidates.Add(owner, tags);
        }
        var tableBounds = new Dictionary<uint, uint>();
        count = Count(reader, tables.Length);
        for (int index = 0; index < count; index++)
        {
            uint table = reader.ReadUInt32(), bounds = reader.ReadUInt32();
            if (!tableTags.Contains(table) || !tableBounds.TryAdd(table, bounds))
                throw new InvalidDataException("The table bounds cache row is invalid.");
        }
        var nodes = ReadNodes(reader, nodeEntries, owners);
        var failed = new HashSet<uint>();
        count = Count(reader, configs.Length + tables.Length + nodeEntries.Count);
        for (int index = 0; index < count; index++)
        {
            uint tag = reader.ReadUInt32();
            if ((!configTags.Contains(tag) && !tableTags.Contains(tag) && !nodeEntries.ContainsKey(tag)) || !failed.Add(tag))
                throw new InvalidDataException("The config cache failure row is invalid.");
        }
        if (tables.Any(tag => !tableBounds.ContainsKey(tag) && !failed.Contains(tag)))
            throw new InvalidDataException("The table bounds cache is incomplete.");
        if (nodeEntries.Keys.Any(tag => !nodes.ContainsKey(tag) && !failed.Contains(tag)))
            throw new InvalidDataException("The isolation metadata cache is incomplete.");
        return new(candidates, tableBounds, nodes, failed, configs.Length + tables.Length + nodeEntries.Count);
    }

    private static int Count(BinaryReader reader, int maximum)
    {
        int value = reader.ReadInt32();
        return value >= 0 && value <= maximum ? value : throw new InvalidDataException("The config cache count is invalid.");
    }
}
