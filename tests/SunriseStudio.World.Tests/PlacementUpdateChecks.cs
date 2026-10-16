using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

/** A placement update must leave the world exactly as a full decode of the same bytes. */
internal static class PlacementUpdateChecks
{
    /** Edits each supported row kind of the fixture, then compares with a new decode. */
    internal static void Synthetic(string root)
    {
        string install = Path.Combine(root, "placement-install");
        WorldFixture.Create(install);
        using var catalog = PackageCatalog.Open(install);
        var session = new Session(catalog, Path.Combine(root, "placement-project"), WorldFixture.Base, WorldFixture.Base + 4);
        var world = session.World;
        var placed = world.Scene.Markers.Single(marker => marker.SourceTag == WorldFixture.Base + 6);
        session.Edit("placement", SceneEdits.CreateTransformEdit(catalog, placed, new(4, 5, 6), Quaternion.CreateFromYawPitchRoll(.3f, 0, 0), 3, world, "Placed")!);
        var spawn = world.Scene.Markers.First(marker => marker.Layer == SceneLayer.Spawns);
        session.Edit("spawn", SceneEdits.CreateTransformEdit(catalog, spawn, spawn.Position + new Vector3(2, 0, 1), null, 1, world, "Spawn")!);
        var volume = world.Scene.Markers.Single(marker => marker.Layer == SceneLayer.Volumes);
        session.Edit("trigger", SceneEdits.CreateBoundsEdit(catalog, volume, new(-5, -6, -7), new(5, 6, 7), world, "Trigger")!);
        var light = (WorldLight)world.Objects.Single(row => row.Kind == WorldObjectKind.Light).Source;
        session.Edit("light", SceneEdits.CreateTransformEdit(catalog, light, new(20, 30, 40), Quaternion.Identity, 2, world, "Light")!);
        Check(ReferenceEquals(world.Scene.Markers.First(marker => marker.Layer == SceneLayer.Spawns), spawn)
            && spawn.Position == session.Full().Scene.Markers.First(marker => marker.Layer == SceneLayer.Spawns).Position,
            "The update replaced the spawn marker or missed its new position.");
        foreach (var key in session.Project.ObjectEdits.Select(edit => edit.Key).ToArray())
            session.Revert(key);
        session.Refused("outside field", WorldFixture.Base + 22, bytes => bytes[0x30] ^= 1);
        Console.WriteLine($"PASS world placement-update fixture steps={session.Steps} equal=all refused-outside-field=full-reload");
    }

    /** Moves a static instance and a placed object of an installed map, then compares with a new decode. */
    internal static void Owned(PackageCatalog catalog, WorldLoader loader, uint mapTag, uint missionTag, string root)
    {
        var session = new Session(catalog, Path.Combine(root, "owned-placement-project"), mapTag, missionTag, loader);
        var world = session.World;
        // An edit that isolates its instance rewrites the map container, which only a reload can take.
        MeshInstance? instance = null;
        string instanceResult = "none";
        foreach (var item in world.Scene.Instances.Where(item => item.InstanceSourceTag != 0 && item.Mesh.Native is not null
            && SceneEdits.Describe(catalog, item, world).CanTransform).DistinctBy(item => item.ObjectPlacement).Take(12))
        {
            var info = SceneEdits.Describe(catalog, item, world);
            var edit = SceneEdits.CreateTransformEdit(catalog, item, info.Position + new Vector3(1, 0, .5f), null, info.Scale * 1.25f, world, "Instance")!;
            instance = item;
            if (session.TryEdit("instance", edit, out string reason)) { instanceResult = "equal"; break; }
            instanceResult = "refused:" + reason.Replace(' ', '_');
        }
        Check(instance is not null, "The owned map has no instance that can move.");
        // A row whose collision the compiler also moves writes a collision compound as well.
        string collision = "none";
        foreach (var candidate in world.Scene.Instances.Where(item => item.InstanceSourceTag != 0 && item.Mesh.Native is not null
            && !ReferenceEquals(item.ObjectPlacement, instance!.ObjectPlacement)).DistinctBy(item => item.ObjectPlacement).Take(400))
        {
            var candidateInfo = SceneEdits.Describe(catalog, candidate, world);
            if (!candidateInfo.CanTransform) continue;
            var move = SceneEdits.CreateTransformEdit(catalog, candidate, candidateInfo.Position + Vector3.UnitX, null, candidateInfo.Scale, world, "Collision")!;
            ObjectEditCompilation trial;
            try { trial = ObjectEditCompiler.Default.Compile([move], catalog.ReadOriginal, catalog); }
            catch (InvalidDataException) { continue; }
            var extra = trial.Edits.Single().Tags.Where(tag => catalog.TryGetEntry(tag, out var entry) && entry.Type != 8).ToArray();
            if (extra.Length == 0) continue;
            collision = string.Join(",", extra.Select(tag => catalog.TryGetEntry(tag, out var entry)
                ? $"{tag:X8}/type{entry.Type}.{entry.SubType}/{entry.Reference:X8}" : $"{tag:X8}"));
            session.Edit("instance-collision", move);
            collision += ":equal";
            break;
        }
        Console.WriteLine($"PLACEMENT_UPDATE collision={collision}");
        // A placed model, a placement with effects, a spawn and a light are tried until one moves in place.
        var candidates = new (string Label, Func<SceneMarker, bool> Match)[]
        {
            ("placed-model", marker => marker.Semantic == "authored placement"
                && world.Scene.Instances.Any(item => ReferenceEquals(item.ObjectPlacement, marker.ObjectPlacement))),
            ("placed-effect", marker => marker.Semantic == "authored placement"
                && world.Scene.Markers.Any(item => item.Layer == SceneLayer.Effects && item.SourceTag == marker.SourceTag
                    && item.SourceOffset == marker.SourceOffset)),
            ("spawn", marker => marker.Layer == SceneLayer.Spawns)
        };
        var results = new List<string> { $"instance={instanceResult}" };
        foreach (var (label, match) in candidates)
        {
            string result = "none";
            foreach (var marker in world.Scene.Markers.Where(match).Take(12))
            {
                var placement = SceneEdits.Describe(catalog, marker, world);
                if (!placement.CanTransform) continue;
                var edit = SceneEdits.CreateTransformEdit(catalog, marker, placement.Position + Vector3.UnitZ, null, placement.Scale, world, label)!;
                if (session.TryEdit(label, edit, out string reason)) { result = "equal"; break; }
                result = "refused:" + reason.Replace(' ', '_');
            }
            results.Add($"{label}={result}");
        }
        var light = world.Objects.FirstOrDefault(row => row.Kind == WorldObjectKind.Light
            && SceneEdits.Describe(catalog, row.Source, world).CanTransform)?.Source;
        if (light is not null)
        {
            var lightInfo = SceneEdits.Describe(catalog, light, world);
            session.Edit("light", SceneEdits.CreateTransformEdit(catalog, light, lightInfo.Position + Vector3.UnitX, null, lightInfo.Scale, world, "Light")!);
        }
        results.Add($"light={(light is null ? "none" : "equal")}");
        results.Add($"collision={collision}");
        foreach (var key in session.Project.ObjectEdits.Select(edit => edit.Key).ToArray())
            session.Revert(key);
        Console.WriteLine($"PASS world placement-update owned map={mapTag:X8} mission={missionTag:X8} steps={session.Steps} "
            + $"{string.Join(' ', results)} update_ms_max={session.UpdateMilliseconds.DefaultIfEmpty().Max():F1} "
            + $"full_decode_ms_mean={session.FullMilliseconds.DefaultIfEmpty().Average():F0}");
    }

    private sealed class Session
    {
        private readonly PackageCatalog catalog;
        private readonly uint map, mission;
        private readonly Func<WorldLoader> loaders;
        private IReadOnlyDictionary<uint, byte[]> compiled = new Dictionary<uint, byte[]>();
        private IReadOnlyDictionary<uint, GeneratedObjectAsset> generated = new Dictionary<uint, GeneratedObjectAsset>();
        public StudioProject Project { get; }
        public WorldDocument World { get; }
        public int Steps { get; private set; }
        public List<double> UpdateMilliseconds { get; } = [];
        public List<double> FullMilliseconds { get; } = [];

        public Session(PackageCatalog catalog, string projectPath, uint map, uint mission, WorldLoader? shared = null)
        {
            this.catalog = catalog; this.map = map; this.mission = mission;
            loaders = shared is null ? () => new WorldLoader(catalog) : () => shared;
            Project = StudioProject.Create(projectPath, "Placement updates");
            Project.ObjectEditCompiler = ObjectEditCompiler.Default;
            catalog.ReadOverride = tag => generated.TryGetValue(tag, out var made) ? made.Data.ToArray()
                : compiled.TryGetValue(tag, out var bytes) ? bytes.ToArray()
                : Project.ContainsEdit(tag) ? Project.ReadAsset(tag, catalog) : null;
            World = Load();
        }

        /** Reloads the whole world and records how long that took. */
        public WorldDocument Full()
        {
            var watch = Stopwatch.StartNew();
            var world = Load();
            FullMilliseconds.Add(watch.Elapsed.TotalMilliseconds);
            return world;
        }

        /** Applies an object edit and throws if the update path refuses it. */
        public void Edit(string label, ObjectEdit edit)
        {
            if (!TryEdit(label, edit, out string reason)) throw new InvalidDataException($"{label}: the update was refused: {reason}");
        }

        /** Reverts the object edit with the given key. */
        public void Revert(string key)
        {
            if (!Mutate("revert " + key, () => Project.RevertObjectEdit(key), out string reason))
                throw new InvalidDataException($"revert {key}: the update was refused: {reason}");
        }

        /** A refused edit is removed again, so the world keeps matching the bytes. */
        public bool TryEdit(string label, ObjectEdit edit, out string reason)
        {
            if (Mutate(label, () => Check(Project.RecordObjectEdit(edit, catalog), $"{label} saved nothing."), out reason)) return true;
            Console.WriteLine($"PLACEMENT_UPDATE step={label.Replace(' ', '_')} refused={reason.Replace(' ', '_')}");
            Project.RevertObjectEdit(edit.Key);
            Publish(Project.CompileObjectEdits(catalog));
            return false;
        }

        /** A raw change outside the placement fields is refused, and a reload then matches. */
        public void Refused(string label, uint tag, Action<byte[]> change)
        {
            byte[] bytes = catalog.Read(tag);
            change(bytes);
            Project.ApplyEdit(tag, bytes, catalog);
            Check(WorldPlacementUpdate.Prepare(catalog, World, [tag], out string reason) is null && reason.Length > 0,
                $"{label}: a change outside the placement fields was accepted.");
            Project.Revert(tag);
        }

        private bool Mutate(string label, Action action, out string reason)
        {
            var before = compiled;
            var madeBefore = generated;
            action();
            Publish(Project.CompileObjectEdits(catalog));
            var changed = before.Keys.Union(compiled.Keys).Where(tag => !before.TryGetValue(tag, out var old)
                || !compiled.TryGetValue(tag, out var next) || !old.AsSpan().SequenceEqual(next))
                .Concat(madeBefore.Keys.Union(generated.Keys).Where(tag => !madeBefore.TryGetValue(tag, out var old)
                    || !generated.TryGetValue(tag, out var next) || !old.Data.AsSpan().SequenceEqual(next.Data))).ToArray();
            var watch = Stopwatch.StartNew();
            var update = WorldPlacementUpdate.Prepare(catalog, World, changed, out reason);
            if (update is null)
            {
                Console.WriteLine("PLACEMENT_UPDATE changed=" + string.Join(',', changed.Select(tag =>
                    catalog.TryGetEntry(tag, out var entry) ? $"{tag:X8}/{entry.Reference:X8}/type{entry.Type}" : $"{tag:X8}/missing")));
                return false;
            }
            update.Apply();
            UpdateMilliseconds.Add(watch.Elapsed.TotalMilliseconds);
            string difference = WorldFingerprint.Compare(WorldFingerprint.Of(World), WorldFingerprint.Of(Full()));
            Check(difference.Length == 0, $"{label}: the updated world differs from a full decode:{difference}");
            Console.WriteLine($"PLACEMENT_UPDATE step={label.Replace(' ', '_')} tags={changed.Length} instances={update.Instances.Count} "
                + $"markers={update.Markers.Count} rows={update.Rows.Count} update_ms={UpdateMilliseconds[^1]:F1} full_ms={FullMilliseconds[^1]:F0} equal=true");
            Steps++;
            return true;
        }

        /** Publishes a compile as the editor does: edited bytes, and generated entries in the overlay. */
        private void Publish(ObjectEditCompilation compilation)
        {
            compiled = compilation.Assets;
            generated = compilation.GeneratedAssets.ToDictionary(asset => asset.TagHash);
            var rows = new List<PackageOverlayEntry>();
            foreach (var (tag, bytes) in compiled)
                if (catalog.TryGetEntry(tag, out var entry)) rows.Add(new(entry with { Size = bytes.Length }));
            foreach (var asset in generated.Values)
            {
                var metadata = asset.Native;
                var entry = new PackageEntry(asset.TagHash, metadata.Reference, (int)(metadata.TypeInfo >> 9 & 127),
                    (int)(metadata.TypeInfo >> 6 & 7), asset.Data.Length, $"w64_sunrise_{metadata.PackageId:x4}_0",
                    metadata.PackageId, metadata.EntryIndex!.Value) { TypeInfo = metadata.TypeInfo };
                rows.Add(new(entry, asset.Name)
                {
                    Generated = true, GeneratedSourceTag = asset.SourceTag, InstanceOrigins = asset.InstanceOrigins,
                    PlacementOrigins = asset.PlacementOrigins, Replaces = asset.Replaces
                });
            }
            catalog.SetOverlayEntries(rows);
        }

        private WorldDocument Load() => loaders().LoadMission(map, mission, new() { IncludeLowerLods = false });
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
