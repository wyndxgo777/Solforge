using System.Buffers.Binary;
using System.Text.Json;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

public enum DependencyIssueKind { MissingReference, InvalidNativeReference, MissingProjectFile }

/** A problem the build would ship; Category is Texture, Audio, Script, ... from AssetCategories when known. */
public sealed record DependencyIssue(DependencyIssueKind Kind, uint FromTag, uint? TargetTag, string Category, string Message);

/** An authored tag (changed or added) and the child tags its bytes reference. */
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
        var added = new Dictionary<uint, (AddedProjectAsset Asset, PackageEntry Entry)>();
        var projectPackages = new HashSet<ushort>();
        foreach (var asset in project.AddedAssets)
        {
            if (asset.Native is not { } native) continue;
            projectPackages.Add(native.PackageId);
            if (native.EntryIndex is not int index) continue;
            uint tag = TagOf(native.PackageId, index);
            added[tag] = (asset, new(tag, native.Reference, (int)((native.TypeInfo >> 9) & 127), (int)((native.TypeInfo >> 6) & 7),
                (int)Math.Min(asset.Size, int.MaxValue), $"w64_sunrise_{native.PackageId:x4}_0", native.PackageId, index)
                { TypeInfo = native.TypeInfo });
        }
        PackageEntry? Resolve(uint tag) => added.TryGetValue(tag, out var own) ? own.Entry
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
            try
            {
                foreach (uint target in PackageNativeDependencies.Read(catalog, tag, entry.Reference, entry.TypeInfo, data, Resolve))
                    references.Add(target);
            }
            catch (InvalidDataException error)
            {
                issues.Add(new(DependencyIssueKind.InvalidNativeReference, tag, null, category, error.Message));
            }
            if (AssetCategories.IsStructured(entry))
                for (int offset = 0; offset + 4 <= data.Length; offset += 4)
                {
                    uint word = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                    if (word == tag || word - TagBase >= TagSpan) continue;
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

        foreach (var change in project.Changes.OrderBy(change => change.TagHash))
        {
            uint tag = change.TagHash;
            if (!catalog.TryGetEntry(tag, out var entry))
            {
                issues.Add(new(DependencyIssueKind.MissingReference, tag, tag, "Unknown", $"Changed tag {tag:X8} is not in the installation."));
                continue;
            }
            Visit(tag, "changed", entry, () => project.ReadAsset(tag, catalog));
        }
        foreach (var (tag, (asset, entry)) in added.OrderBy(pair => pair.Key))
            Visit(tag, "added", entry, () => project.ReadAddedAsset(asset.Id, catalog));
        return new(project.Name, nodes, issues);
    }

    public static uint TagOf(ushort packageId, int entryIndex) => checked(TagBase + ((uint)packageId << 13) + (uint)entryIndex);
}
