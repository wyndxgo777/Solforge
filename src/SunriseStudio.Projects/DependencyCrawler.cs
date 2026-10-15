using System.Buffers.Binary;
using System.Text.Json;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

public enum DependencyIssueKind { MissingReference, InvalidNativeReference, MissingProjectFile }

/**
 * A problem the build would ship. Category is the AssetCategories kind of the missing or invalid child
 * (Texture, Audio, Buffer, ...) when a native link names it; a bare structured-tag word that resolves
 * nowhere has no known kind and is "Unknown". MissingProjectFile carries the authored tag's own kind.
 */
public sealed record DependencyIssue(DependencyIssueKind Kind, uint FromTag, uint? TargetTag, string Category, string Message);

/** An authored tag the build ships (changed, object edit, config, added, generated) and the children its bytes reference. */
public sealed record DependencyNode(uint Tag, string Source, string Category, IReadOnlyList<uint> References);

/** The dependency manifest of a project: every authored tag, its children and what is missing. */
public sealed record DependencyManifest(string Project, IReadOnlyList<DependencyNode> Nodes, IReadOnlyList<DependencyIssue> Issues)
{
    public const string FileName = "dependencies.json";
    public bool Complete => Issues.Count == 0;

    private static readonly JsonSerializerOptions Json = new(ProjectJson.Options) { UnmappedMemberHandling = default };

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /** Writes build/dependencies.json (gitignored) and returns its path. */
    public string Write(string projectDirectory)
    {
        string path = Path.Combine(Path.GetFullPath(projectDirectory), "build", FileName);
        ProjectPaths.AtomicWrite(path, ToJson());
        return path;
    }
}

/**
 * Walks every authored tag of a project one level into its children (Solforge Phase 18): decoded
 * native references (PackageNativeDependencies: texture payloads, streamed mips, icon bitmaps, ...)
 * plus, for structured tags, every aligned 32-bit word that names an entry of a known package (an
 * installed package or one the project adds to). A child must exist in the installation or among the
 * project's added assets; base children are complete by definition, so the walk stops there.
 */
public static class DependencyCrawler
{
    private const uint TagBase = 0x80800000u;
    private const uint TagSpan = 0x2000u << 13;

    public static DependencyManifest Crawl(StudioProject project, PackageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(catalog);
        // The same tag set and bytes ProjectBuilder.Build ships: raw changes, compiled object edits,
        // config bindings, added native assets and generated object assets.
        var objects = project.CompileObjectEdits(catalog, build: true);
        var config = project.CompileConfigBindings(catalog);
        var added = new Dictionary<uint, (AddedProjectAsset Asset, PackageEntry Entry)>();
        var generated = new Dictionary<uint, (GeneratedObjectAsset Asset, PackageEntry Entry)>();
        var projectPackages = new HashSet<ushort>();
        foreach (var asset in project.AddedAssets)
        {
            if (asset.Native is not { } native) continue;
            projectPackages.Add(native.PackageId);
            if (native.EntryIndex is not int index) continue;
            uint tag = TagOf(native.PackageId, index);
            added[tag] = (asset, NativeEntry(tag, native, index, asset.Size));
        }
        foreach (var asset in objects.GeneratedAssets)
        {
            projectPackages.Add(asset.Native.PackageId);
            generated[asset.TagHash] = (asset, NativeEntry(asset.TagHash, asset.Native, asset.Native.EntryIndex!.Value, asset.Data.Length));
        }
        PackageEntry? Resolve(uint tag) => added.TryGetValue(tag, out var own) ? own.Entry
            : generated.TryGetValue(tag, out var derived) ? derived.Entry
            : catalog.TryGetEntry(tag, out var entry) ? entry : null;

        var nodes = new List<DependencyNode>();
        var issues = new List<DependencyIssue>();
        void Visit(uint tag, string source, PackageEntry entry, Func<byte[]> read)
        {
            string category = AssetCategories.CategoryOf(entry);
            byte[] data;
            try { data = read(); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or KeyNotFoundException)
            {
                issues.Add(new(DependencyIssueKind.MissingProjectFile, tag, null, category, error.Message));
                nodes.Add(new(tag, source, category, []));
                return;
            }
            var references = new SortedSet<uint>();
            uint? reported = null;
            try
            {
                foreach (uint target in PackageNativeDependencies.Read(catalog, tag, entry.Reference, entry.TypeInfo, data, Resolve))
                    references.Add(target);
            }
            catch (NativeDependencyException error)
            {
                bool absent = Resolve(error.Target) is null;
                reported = error.Target;
                issues.Add(new(absent ? DependencyIssueKind.MissingReference : DependencyIssueKind.InvalidNativeReference,
                    tag, error.Target, error.ExpectedCategory, absent
                        ? $"{tag:X8} needs {error.Target:X8} as its {error.Role} ({error.ExpectedCategory}), which neither the installation nor the project holds."
                        : error.Message));
            }
            catch (InvalidDataException error)
            {
                issues.Add(new(DependencyIssueKind.InvalidNativeReference, tag, null, category, error.Message));
            }
            if (AssetCategories.IsStructured(entry))
                for (int offset = 0; offset + 4 <= data.Length; offset += 4)
                {
                    uint word = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                    if (word == tag || word == reported || word - TagBase >= TagSpan) continue;
                    ushort package = (ushort)((word - TagBase) >> 13);
                    if (!catalog.ContainsInstalledPackage(package) && !projectPackages.Contains(package)) continue;
                    if (Resolve(word) is null)
                    {
                        issues.Add(new(DependencyIssueKind.MissingReference, tag, word, "Unknown",
                            $"{tag:X8} references {word:X8} at 0x{offset:X}, which neither the installation nor the project holds."));
                        continue;
                    }
                    references.Add(word);
                }
            nodes.Add(new(tag, source, category, references.ToArray()));
        }

        foreach (uint tag in project.Changes.Select(change => change.TagHash).Union(objects.Assets.Keys)
            .Union(config.Assets.Keys).Where(tag => !added.ContainsKey(tag)).Order())
        {
            if (!catalog.TryGetEntry(tag, out var entry))
            {
                issues.Add(new(DependencyIssueKind.MissingReference, tag, tag, "Unknown", $"Authored tag {tag:X8} is not in the installation."));
                continue;
            }
            string source = config.Assets.ContainsKey(tag) ? "config" : objects.Assets.ContainsKey(tag) ? "object edit" : "changed";
            Visit(tag, source, entry, () => config.Assets.TryGetValue(tag, out var configured) ? configured
                : objects.Assets.TryGetValue(tag, out var compiled) ? compiled.ToArray() : project.ReadAsset(tag, catalog));
        }
        foreach (var (tag, (asset, entry)) in added.OrderBy(pair => pair.Key))
            Visit(tag, "added", entry, () => config.Assets.TryGetValue(tag, out var configured)
                ? configured : project.ReadAddedAsset(asset.Id, catalog));
        foreach (var (tag, (asset, entry)) in generated.OrderBy(pair => pair.Key))
            Visit(tag, "generated", entry, () => asset.Data);
        return new(project.Name, nodes, issues);
    }

    private static PackageEntry NativeEntry(uint tag, NativeAssetMetadata native, int index, long size) =>
        new(tag, native.Reference, (int)((native.TypeInfo >> 9) & 127), (int)((native.TypeInfo >> 6) & 7),
            (int)Math.Min(size, int.MaxValue), $"w64_sunrise_{native.PackageId:x4}_0", native.PackageId, index)
        { TypeInfo = native.TypeInfo };

    public static uint TagOf(ushort packageId, int entryIndex) => checked(TagBase + ((uint)packageId << 13) + (uint)entryIndex);
}
