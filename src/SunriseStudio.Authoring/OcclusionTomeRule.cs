using System.Numerics;
using SunriseStudio.Formats;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

/**
 * Moved occluders: every tome of every bubble that places the row opens the old place, and closes the
 * new place when it blocked the old one. Runs before InstanceCullRule, which adds generated lists.
 */
internal sealed class OcclusionTomeRule : IObjectEditRule
{
    private const uint MapRootClass = 0x808091DE, BubbleClass = 0x80807DAE, ContainerClass = 0x80808A54,
        ListClass = 0x808099D6, OwnerClass = 0x80806EF4, StaticClass = 0x808071A7, VisibilityMapClass = 0x80807320;
    // Blocked shares of installed meshes cluster near 0 and near 1; the middle splits them.
    private const double OccluderShare = 0.5;
    // World margin around the moved meshes; it covers two voxels of the coarsest tome.
    private const float Margin = 1;

    public ObjectEditLayout Layout => ObjectEditLayout.StaticInstance;

    public bool BuildOnly => true;

    /** One edited row through one placement, at its installed and its edited pose. */
    private sealed record Moved(ObjectEdit Edit, BubbleGraph.Placement Placement, Vector3[] Removed, Vector3[] Added);

    /** What one tome gets: the moved meshes of every bubble that uses it, and their neighbours. */
    private sealed class TomeWork
    {
        private readonly HashSet<(string, BubbleGraph.Placement)> keys = [];
        private readonly HashSet<uint> bubbles = [];
        internal List<Moved> Moved { get; } = [];
        internal List<IReadOnlyList<Vector3>> Kept { get; } = [];

        /** A bubble adds its neighbours once, and a row placed through one placement moves once. */
        internal void Add(uint bubble, IEnumerable<Moved> moved, IEnumerable<IReadOnlyList<Vector3>> kept)
        {
            if (!bubbles.Add(bubble)) return;
            Moved.AddRange(moved.Where(item => keys.Add((item.Edit.Key, item.Placement))));
            Kept.AddRange(kept);
        }
    }

    /** Opens and closes occlusion tomes for moved occluders in every bubble that places them. */
    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        var graph = new BubbleGraph(context);
        var meshes = new Dictionary<uint, MeshData[]>();
        var models = new Dictionary<uint, Vector3[]>();
        var tomes = new SortedDictionary<uint, TomeWork>();
        var editedRows = edits.Select(edit => (edit.Target.SourceTag, edit.Target.Index)).ToHashSet();
        foreach (var (bubble, scope) in graph.Bubbles.OrderBy(pair => pair.Key))
        {
            var moved = new List<Moved>();
            foreach (var edit in edits)
                foreach (var placement in scope.Placements.Where(placement => placement.Table == edit.Target.SourceTag))
                    if (Move(context, edit, placement, meshes, models) is { } item) moved.Add(item);
            if (moved.Count == 0) continue;
            var region = Bounds(moved.SelectMany(item => item.Removed.Concat(item.Added)));
            region = (region.Min - new Vector3(Margin), region.Max + new Vector3(Margin));
            var kept = Kept(context, bubble, region, editedRows, meshes);
            foreach (uint map in scope.Maps.Order())
            {
                var data = new NativeRows(context.ReadLayer(map));
                // The id map names its fine tome at +0x08 and its coarse tome at +0x0C.
                foreach (uint tome in new[] { data.U32(0x08), data.U32(0x0C) })
                {
                    if (!context.Catalog.TryGetEntry(tome, out _)) continue;
                    if (!tomes.TryGetValue(tome, out var work)) tomes[tome] = work = new TomeWork();
                    work.Add(bubble, moved, kept);
                }
            }
        }
        foreach (var (tag, work) in tomes) Rewrite(context, tag, work);
    }

    /** Opens and closes one tome, or notes why it stays as it is. */
    private static void Rewrite(ObjectEditContext context, uint tag, TomeWork work)
    {
        byte[] bytes = context.Current(tag);
        if (!TomeReader.IsTome(bytes)) return;
        try
        {
            var tome = TomeReader.Read(bytes);
            var edit = new TomeOcclusionEdit();
            edit.Kept.AddRange(work.Kept);
            // Opening only adds sight lines, so every old place opens; only a proved occluder closes its new place.
            foreach (var item in work.Moved)
            {
                edit.Removed.Add(item.Removed);
                if (TomeEditor.BlockedShare(tome, item.Removed, work.Kept) >= OccluderShare) edit.Added.Add(item.Added);
            }
            TomeEditor.Apply(tome, edit);
            byte[] written = TomeWriter.Write(tome);
            if (written.AsSpan().SequenceEqual(bytes)) return;
            context.ReplaceCurrent(tag, written);
            foreach (var contributor in work.Moved.Select(item => item.Edit).Distinct()) context.Produced(contributor, tag);
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException)
        {
            context.Note($"Occlusion tome {tag:X8} was left unchanged: {error.Message}");
        }
    }

    /** The row's mesh at its installed pose and at its edited pose, through one placement of its table. */
    private static Moved? Move(ObjectEditContext context, ObjectEdit edit, BubbleGraph.Placement placement,
        Dictionary<uint, MeshData[]> meshes, Dictionary<uint, Vector3[]> models)
    {
        uint table = edit.Target.SourceTag;
        int index = edit.Target.Index;
        var original = new NativeRows(context.Original(table));
        var current = new NativeRows(context.Current(table));
        int[] originalRows = original.Rows(0x40, 0x808071A3, 48), rows = current.Rows(0x40, 0x808071A3, 48);
        if ((uint)index >= originalRows.Length || rows.Length != originalRows.Length)
            throw new InvalidDataException("The edited instance row is missing.");
        if (Resource(current, index) is not { } resource || context.ClassOf(resource) != StaticClass) return null;
        var originalList = new NativeRows(context.Original(placement.List));
        var currentList = new NativeRows(context.ReadLayer(placement.List));
        int[] listRows = originalList.Rows(8, 0x808099D8, 144);
        if ((uint)placement.Index >= listRows.Length) throw new InvalidDataException("The static placement row is missing.");
        // The placement row holds its QTS at +0x10; it applies after the instance row's.
        var before = Matrix(original, originalRows[index]) * Matrix(originalList, listRows[placement.Index] + 0x10);
        var after = Matrix(current, rows[index]) * Matrix(currentList, placement.Row + 0x10);
        if (before == after) return null;
        var model = Model(context, resource, meshes, models);
        return model.Length == 0 ? null : new(edit, placement, Transform(model, before), Transform(model, after));
    }

    /** The static resource that draws one row, from the table's ranges. */
    private static uint? Resource(NativeRows table, int index)
    {
        int[] resources = table.Rows(0x58, 0x8080967D, 4);
        foreach (int range in table.Rows(0x68, 0x80807190, 8))
        {
            int count = table.U16(range), first = table.U16(range + 2), resource = table.U16(range + 4);
            if (index >= first && index < first + count && resource < resources.Length) return table.U32(resources[resource]);
        }
        return null;
    }

    /** Every part of one static resource as one triangle list, in model space. */
    private static Vector3[] Model(ObjectEditContext context, uint resource, Dictionary<uint, MeshData[]> meshes,
        Dictionary<uint, Vector3[]> models)
    {
        if (models.TryGetValue(resource, out var model)) return model;
        var scene = Loader(context, meshes, null).LoadMesh(resource);
        return models[resource] = scene.Instances.Where(instance => instance.Layer == SceneLayer.Geometry)
            .SelectMany(instance => Triangles(instance.Mesh, Matrix4x4.Identity)).ToArray();
    }

    /** Static and terrain meshes of a bubble near the region, except the edited rows, clipped to it. */
    private static List<IReadOnlyList<Vector3>> Kept(ObjectEditContext context, uint bubble, (Vector3 Min, Vector3 Max) region,
        HashSet<(uint, int)> editedRows, Dictionary<uint, MeshData[]> meshes)
    {
        var scene = Loader(context, meshes, region).LoadBubble(bubble);
        var kept = new List<IReadOnlyList<Vector3>>();
        foreach (var instance in scene.Instances)
        {
            if (instance.Layer is not (SceneLayer.Geometry or SceneLayer.Terrain)) continue;
            if (instance.InstanceSourceTag != 0 && editedRows.Contains((instance.InstanceSourceTag, instance.InstanceSourceIndex))) continue;
            var triangles = new List<Vector3>();
            var all = Triangles(instance.Mesh, instance.Transform);
            for (int i = 0; i + 2 < all.Length; i += 3)
            {
                var box = Bounds(all.AsSpan(i, 3).ToArray());
                if (box.Min.X > region.Max.X || box.Min.Y > region.Max.Y || box.Min.Z > region.Max.Z
                    || box.Max.X < region.Min.X || box.Max.Y < region.Min.Y || box.Max.Z < region.Min.Z) continue;
                triangles.AddRange(all.AsSpan(i, 3));
            }
            if (triangles.Count > 0) kept.Add(triangles);
        }
        return kept;
    }

    /** Reads the compile's own bytes, so raw deltas apply and edited rows hold their new values. */
    private static AssetSceneLoader Loader(ObjectEditContext context, Dictionary<uint, MeshData[]> meshes,
        (Vector3 Min, Vector3 Max)? region) =>
        new(context.Catalog) { Source = context.ReadLayer, Region = region, MeshCache = meshes };

    private static Vector3[] Triangles(MeshData mesh, Matrix4x4 transform) =>
        mesh.Indices.Select(index => Vector3.Transform(mesh.Vertices[index].Position, transform)).ToArray();

    private static Vector3[] Transform(Vector3[] model, Matrix4x4 transform) =>
        model.Select(point => Vector3.Transform(point, transform)).ToArray();

    /** Scale, then the normalized rotation, then the translation, as the scene decoder builds a placement. */
    private static Matrix4x4 Matrix(NativeRows data, int offset) =>
        Matrix4x4.CreateScale(data.F32(offset + 28)) * Matrix4x4.CreateFromQuaternion(data.Rotation(offset))
            * Matrix4x4.CreateTranslation(data.V3(offset + 16));

    private static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<Vector3> points)
    {
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var point in points) { min = Vector3.Min(min, point); max = Vector3.Max(max, point); }
        return (min, max);
    }

    /** Each bubble's static placements and visibility maps, by the typed map edges the scene decoder follows. */
    private sealed class BubbleGraph
    {
        /** A static placement: list, row, index and table. */
        internal sealed record Placement(uint List, int Row, int Index, uint Table);
        /** Maps and placements reachable from one bubble. */
        internal sealed class Scope
        {
            internal HashSet<uint> Maps { get; } = [];
            internal List<Placement> Placements { get; } = [];
        }

        private readonly ObjectEditContext context;
        private readonly InstanceIsolationSources sources;
        private readonly HashSet<(uint, uint)> visited = [];
        private readonly HashSet<(uint, uint)> lists = [];
        internal Dictionary<uint, Scope> Bubbles { get; } = [];

        internal BubbleGraph(ObjectEditContext context)
        {
            this.context = context;
            sources = new InstanceIsolationSources(context);
            foreach (var root in sources.OfClass(MapRootClass)) Visit(root.Tag, 0, 0);
        }

        private void Visit(uint tag, uint bubble, int depth)
        {
            if (depth > 24) throw new InvalidDataException("The static map graph exceeds its limit.");
            if (!visited.Add((tag, bubble))) return;
            var node = sources.Read(tag);
            if (!node.Valid) return;
            switch (node.Class)
            {
                case MapRootClass:
                    foreach (var child in node.Children)
                        if (child.Resolve(context.Catalog) is { } resolved) Visit(resolved, 0, depth + 1);
                    break;
                case BubbleClass:
                    Visit(node.Link, tag, depth + 1);
                    break;
                case 0x808091E0 or 0x8080925E:
                    foreach (var child in node.Children)
                        if (child.Resolve(context.Catalog) is { } resolved) Visit(resolved, bubble, depth + 1);
                    break;
                case 0x8080925B:
                    Visit(node.Link, bubble, depth + 1);
                    break;
                case ContainerClass when bubble != 0:
                    foreach (var child in node.Children) AddList(child.Tag, bubble);
                    break;
            }
        }

        private void AddList(uint tag, uint bubble)
        {
            if (!context.Catalog.TryGetEntry(tag, out var entry) || entry.Reference != ListClass || !lists.Add((tag, bubble))) return;
            var list = sources.Read(tag);
            if (!list.Valid) return;
            if (!Bubbles.TryGetValue(bubble, out var scope)) Bubbles[bubble] = scope = new Scope();
            foreach (uint map in list.Maps)
                if (context.Catalog.TryGetEntry(map, out var mapEntry) && mapEntry.Reference == VisibilityMapClass) scope.Maps.Add(map);
            foreach (var placement in list.Placements)
                if (sources.Read(placement.Owner) is { Valid: true, Class: OwnerClass } owner)
                    scope.Placements.Add(new(placement.List, placement.Row, placement.Index, owner.Link));
        }
    }
}
