using System.Security.Cryptography;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using static SunriseStudio.ProjectSystem.Tests.Check;

namespace SunriseStudio.ProjectSystem.Tests;

/**
 * Phase 19 audit: a generated project with 50 modified tags and 20 new assets. The installation's
 * package files stay byte-identical, every authored byte reads back after a reload with a fresh
 * catalog, and saving the reloaded project rewrites the same files byte for byte.
 */
internal static class IntegrityChecks
{
    private const int Tags = 60, Modified = 50, Added = 20, NativeAdded = 15;

    internal static void Run(string root, List<string> checks)
    {
        var fixture = new Fixture(Path.Combine(root, "install"), Tags, 1901);
        var baseHashes = fixture.Hashes();
        var originals = new Dictionary<uint, byte[]>();
        var expected = new Dictionary<uint, byte[]>();
        var expectedAdded = new Dictionary<string, byte[]>();
        string folder = Path.Combine(root, "project");
        var random = new Random(1902);
        using (var catalog = PackageCatalog.Open(fixture.Install))
        {
            for (int i = 0; i < Tags; i++) originals[Fixture.Tag(i)] = catalog.ReadOriginal(Fixture.Tag(i));
            var project = StudioProject.Create(folder, "Integrity fixture");
            var edits = new Dictionary<uint, byte[]>();
            for (int i = 0; i < Modified; i++)
            {
                uint tag = Fixture.Tag(i);
                var bytes = originals[tag].ToList();
                // Value edits on low bytes only, so no edit forges a tag-hash word; some tags grow or shrink.
                for (int edit = 0; edit < 1 + i % 5; edit++)
                {
                    int offset = random.Next(bytes.Count / 4) * 4 + random.Next(3);
                    bytes[offset] ^= (byte)(1 + random.Next(0x7E));
                }
                if (bytes.SequenceEqual(originals[tag])) bytes[0] ^= 1;
                if (i % 7 == 3) bytes.AddRange(Enumerable.Repeat((byte)0x11, 4 * (1 + i % 3)));
                if (i % 7 == 5) bytes.RemoveRange(bytes.Count - 8, 8);
                edits[tag] = expected[tag] = bytes.ToArray();
            }
            project.ApplyEdits(edits, catalog);

            ushort package = PackageIdAllocator.For(catalog, project).Allocate();
            var additions = new List<ProjectAssetAddition>();
            for (int i = 0; i < Added; i++)
            {
                byte[] body = new byte[16 + i * 12];
                for (int b = 0; b < body.Length; b++) body[b] = (byte)((b * 31 + i * 7) & 0x7F);
                additions.Add(new($"fixture:added-{i:D2}", body,
                    i < NativeAdded ? new NativeAssetMetadata(package, 0x80800001, 0x100A, $"fixture:added-{i:D2}", i) : null));
            }
            foreach (var (addition, asset) in additions.Zip(project.AddAssets(additions, catalog)))
                expectedAdded[asset.Id] = addition.Data;
            project.Save();
            Require(project.Changes.Count == Modified && project.AddedAssets.Count == Added, "The fixture project lost an edit or asset.");
            Require(DependencyCrawler.Crawl(project, catalog).Complete, "The fixture project has dependency issues.");
        }
        Require(fixture.Hashes().SequenceEqual(baseHashes), "Authoring the project wrote the installation's packages.");
        Require(!Directory.EnumerateFiles(folder, "*.tmp", SearchOption.AllDirectories).Any(), "A temporary file was left in the project.");
        var saved = ProjectFiles(folder);
        checks.Add($"generated project: {Modified} modified tags, {Added} new assets ({NativeAdded} native), base packages untouched");

        using (var catalog = PackageCatalog.Open(fixture.Install))
        {
            var reloaded = StudioProject.Open(folder);
            Require(reloaded.Changes.Count == Modified && reloaded.AddedAssets.Count == Added, "The reload lost manifest rows.");
            foreach (var (tag, bytes) in expected)
                Require(reloaded.ReadAsset(tag, catalog).SequenceEqual(bytes), $"Tag {tag:X8} changed across save and reload.");
            for (int i = Modified; i < Tags; i++)
                Require(reloaded.ReadAsset(Fixture.Tag(i), catalog).SequenceEqual(originals[Fixture.Tag(i)]),
                    "An unmodified tag did not read through to base.");
            foreach (var (id, bytes) in expectedAdded)
                Require(reloaded.ReadAddedAsset(id, catalog).SequenceEqual(bytes), $"Added asset {id} changed across save and reload.");
            foreach (var (tag, bytes) in originals)
                Require(catalog.ReadOriginal(tag).SequenceEqual(bytes), $"Base tag {tag:X8} changed.");
            reloaded.Save();
        }
        var resaved = ProjectFiles(folder);
        Require(resaved.Count == saved.Count && resaved.All(pair => saved.TryGetValue(pair.Key, out string? hash) && hash == pair.Value),
            "Saving the reloaded project did not rewrite identical files.");
        Require(fixture.Hashes().SequenceEqual(baseHashes), "Reloading the project wrote the installation's packages.");
        checks.Add($"save + reload is byte-identical ({saved.Count} project files) and every authored byte reads back");
    }

    /** SHA-256 of every project file outside the generated build/exports/.local folders. */
    private static SortedDictionary<string, string> ProjectFiles(string folder)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(folder, file).Replace('\', '/');
            if (ProjectWatcher.IgnoredRootFolders.Contains(relative.Split('/')[0], StringComparer.OrdinalIgnoreCase)) continue;
            result[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return result;
    }
}
