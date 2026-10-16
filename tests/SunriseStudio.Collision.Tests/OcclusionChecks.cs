using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using static Checks;

/** A moved Leviathan slab opens its old place and closes its new place in both tomes, in a build only. */
internal static class OcclusionChecks
{
    // A 5 by 10 by 0.5 metre slab both installed tomes block, and the fine and coarse tome of its bubble.
    private const uint Table = 0x80EDC202, FineTome = 0x80EDC2C2, CoarseTome = 0x80EDC2C3;
    private const int Row = 2112;

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(PackageCatalog catalog)
    {
        if (!catalog.TryGetEntry(Table, out _)) { Console.WriteLine("SKIP occlusion-rule table=80EDC202 absent"); return; }
        var parts = new AssetSceneLoader(catalog).LoadMesh(Table).Instances
            .Where(item => item.InstanceSourceTag == Table && item.InstanceSourceIndex == Row).ToArray();
        Check(parts.Length > 0, "The slab row has no mesh.");
        var points = parts.SelectMany(item => item.Mesh.Vertices.Select(vertex => Vector3.Transform(vertex.Position, item.Transform))).ToArray();
        Vector3 min = points.Aggregate(Vector3.Min), max = points.Aggregate(Vector3.Max), center = (min + max) * 0.5f;
        var shift = new Vector3(0, 11, 0);
        var info = SceneEdits.Describe(catalog, parts[0]);
        var edit = SceneEdits.CreateTransformEdit(catalog, parts[0], info.Position + shift, null, info.Scale)
            ?? throw new InvalidDataException("The slab move recorded nothing.");
        ObjectEditCompileOptions Options(bool build) =>
            new("occlusion-check", new HashSet<ushort>()) { Build = build, RawChangedTags = new HashSet<uint>() };
        var editor = ObjectEditCompiler.Default.Compile([edit], catalog.ReadOriginal, catalog, Options(false));
        Check(!editor.Assets.ContainsKey(FineTome) && !editor.Assets.ContainsKey(CoarseTome), "An editor compile rewrote a tome.");
        var build = ObjectEditCompiler.Default.Compile([edit], catalog.ReadOriginal, catalog, Options(true));
        Check(build.Notes.Count == 0, "The slab move left a tome unchanged: " + string.Join(" ", build.Notes));
        var rng = new Random(Row);
        Vector3 low = Vector3.Min(min, min + shift) - new Vector3(1.5f), high = Vector3.Max(max, max + shift) + new Vector3(1.5f);
        var sizes = new List<string>();
        foreach (uint tag in new[] { FineTome, CoarseTome })
        {
            Check(build.Assets.TryGetValue(tag, out var bytes) && build.Edits[0].Tags.Contains(tag), $"The build did not rewrite tome {tag:X8}.");
            var before = TomeReader.Read(catalog.ReadOriginal(tag));
            var after = TomeReader.Read(bytes!);
            Check(TomeWriter.Write(after).AsSpan().SequenceEqual(bytes), $"Tome {tag:X8} did not rebuild byte for byte.");
            Check(after.Locate(center + shift).Cell == -1, $"The slab's new place in {tag:X8} still has a cell.");
            Check(after.LocateCluster(center) >= 0, $"The slab's old place in {tag:X8} has no cluster.");
            Check(after.Clusters.Count == before.Clusters.Count, $"Tome {tag:X8} gained a cluster.");
            for (int i = 0; i < 4000; i++)
            {
                var point = before.Min + new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()) * (before.Max - before.Min);
                if (Vector3.Clamp(point, low, high) == point) continue;
                Check(after.LocateCluster(point) == before.LocateCluster(point), $"Tome {tag:X8} changed a cluster outside the edit.");
            }
            sizes.Add($"{tag:X8}+{(bytes!.Length - catalog.ReadOriginal(tag).Length) / 1024}KiB");
        }
        Console.WriteLine($"PASS occlusion-rule slab=80EDC202:{Row} editor=untouched build={string.Join(",", sizes)} open=cluster close=solid");
    }
}
