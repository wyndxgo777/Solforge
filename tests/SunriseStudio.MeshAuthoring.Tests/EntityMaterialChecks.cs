using System.Buffers.Binary;
using System.Diagnostics;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

/** Checks entity material profiles: initial-state bit conditions, group counts and fallback candidates. */
internal static class EntityMaterialChecks
{
    /** Warm reads measure the extra initial-filter work separately from material table selection. */
    public static void Profile(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        foreach (var fixture in new[] { (Entity: 0x80B82FE3u, Config: 0x80C443F8u, Source: 0u, Offset: -1),
            (Entity: 0x80C0FA98u, Config: 0x80FEE882u, Source: 0u, Offset: -1),
            (Entity: 0x80C0FA98u, Config: 0x80FEE882u, Source: 0x80B48DDDu, Offset: 0x8B0) })
        {
            byte[] config = catalog.Read(fixture.Config);
            int resource = checked(24 + (int)BinaryPrimitives.ReadInt64LittleEndian(config.AsSpan(24)));
            var reads = new HashSet<uint>();
            EntityInitialState Read() => new EntityInitialStateReader(catalog) { Reads = reads }
                .Read(fixture.Entity, fixture.Config, fixture.Source, fixture.Offset);
            var initial = Read();
            Check(initial.Unresolved is null, "Profile fixture initial state is unresolved: " + initial.Unresolved);
            foreach (bool full in new[] { false, true })
            {
                EntityMaterialSelection Run()
                {
                    var state = full ? Read() : initial;
                    return EntityMaterialSelector.Read(config, resource, state.EnabledParts, state.Unresolved);
                }
                for (int warm = 0; warm < 10; warm++) Run();
                double[] times = new double[100];
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                uint selected = 0;
                for (int iteration = 0; iteration < times.Length; iteration++)
                {
                    long start = Stopwatch.GetTimestamp();
                    selected = Run().Materials[0];
                    times[iteration] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Array.Sort(times);
                Console.WriteLine($"entity_material_profile entity={fixture.Entity:X8} config={fixture.Config:X8} source={fixture.Source:X8} full={full} samples=100 median_ms={times[50]:F4} p95_ms={times[95]:F4} bytes_per_call={allocated / 100} read_tags={reads.Count} selected={selected:X8}");
            }
        }
    }

    /** Required and excluded bit lists select the first admitted, nonempty material. */
    public static void Synthetic()
    {
        byte[] bytes = new byte[0x600];
        const int resource = 0x20, groups = 0x380, conditions = 0x3C0, materials = 0x420, indices = 0x450;
        Put32(resource - 4, 0x808072BD);
        Array(resource + 0x2D0, groups, 0x808072C4, 3);
        Array(resource + 0x300, conditions, 0x808072C5, 5);
        Array(resource + 0x310, materials, 0x80800014, 5);
        Array(resource + 0x2F0, indices, 0x80800006, 4);
        Put16(groups, 3); Put16(groups + 2, 7); Put32(groups + 4, 1); Put16(groups + 8, 2);
        Put16(groups + 24, -1);
        Put16(conditions + 16, 2); Put16(conditions + 18, 0);
        Put16(conditions + 20, 1); Put16(conditions + 22, 4);
        Put16(conditions + 24, 1); Put16(conditions + 26, 2);
        Put16(conditions + 28, 2); Put16(conditions + 30, 2);
        for (int i = 0; i < 5; i++) Put32(materials + i * 4, (uint)(40 + i));
        Put16(indices, 0); Put16(indices + 2, 1); Put16(indices + 4, 1); Put16(indices + 6, 3);
        for (int mask = 0; mask < 32; mask++)
        {
            bool[] bits = Enumerable.Range(0, 5).Select(bit => (mask & (1 << bit)) != 0).ToArray();
            uint expected = bits[0] && bits[1] && !bits[4] ? 41u : bits[2] && !bits[1] && !bits[3] ? 42u : 43u;
            var result = EntityMaterialSelector.Read(bytes, resource, bits);
            Check(result.Materials.SequenceEqual(new[] { expected, uint.MaxValue, uint.MaxValue })
                && result.Unresolved is null, "Material bit conditions or signed group count changed.");
        }
        var fallback = EntityMaterialSelector.Read(bytes, resource, null, "Unresolved fixture state");
        Check(fallback.Materials[0] == 41 && fallback.Unresolved == "Unresolved fixture state",
            "Unknown initial state lost its explicit preview diagnostic.");
        Put32(materials + 4, uint.MaxValue);
        Check(EntityMaterialSelector.Read(bytes, resource, [true, true, false, false, false]).Materials[0] == 43,
            "An absent material prevented the later fallback candidate.");
        Put16(conditions + 18, 512);
        Reject(() => EntityMaterialSelector.Read(bytes, resource, new bool[5]));
        Console.WriteLine("entity_materials conditions=pass signed_count=pass missing_tag=pass unresolved=explicit");

        void Put16(int offset, short value) => BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset), value);
        void Put32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        void Array(int field, int data, uint type, int count)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(field), count);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(field + 8), data - 16 - field - 8);
            Put32(data - 20, 0x80809FBD);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(data - 16), count);
            Put32(data - 8, type);
        }
    }

    /** The shared scene loader must adopt supported initial materials and refresh overlay changes. */
    public static void Owned(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var loader = new AssetSceneLoader(catalog);
        var scene = loader.Load(0x80B82FE3);
        var part = scene.Instances.First(instance => instance.Mesh.TagHash == 0x80C4424F && instance.Mesh.PartIndex == 0x300);
        Check(part.Mesh.MaterialTag == 0x80C4425D && part.Mesh.Native?.MaterialTag == 0x80C4425D,
            "The Hob preview did not select its supported initial material.");
        Check(part.InitiallyVisible == true && scene.Instances.Any(instance => instance.InitiallyVisible == false),
            "The Hob preview did not retain both active and inactive authored geometry.");
        byte[] original = catalog.Read(0x80C443F8), changed = original.ToArray();
        int resource = checked(24 + (int)BinaryPrimitives.ReadInt64LittleEndian(changed.AsSpan(24)));
        var reads = new HashSet<uint>();
        var initial = new EntityInitialStateReader(catalog) { Reads = reads }.Read(0x80B82FE3, 0x80C443F8);
        var selection = EntityMaterialSelector.Read(original, resource, initial.EnabledParts, initial.Unresolved);
        Check(scene.Instances.Where(instance => instance.ModelSourceConfigTag == 0x80C443F8)
            .All(instance => instance.InitiallyVisible == (initial.Enabled(instance.Mesh.LogicalPartIndex) == true
                && instance.Mesh.MaterialTag != uint.MaxValue)), "Initial model visibility differs from its logical part and selected material.");
        var otherSelection = selection.Materials.ToArray(); otherSelection[0] = 0x80C4E690;
        var decode = typeof(AssetSceneLoader).GetMethod("EntityMesh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        MeshData[] Decode(uint[] values) => (MeshData[])decode.Invoke(loader, [0x80C4424Fu, values, 0x80C443F8u])!;
        var firstMeshes = Decode(selection.Materials); var otherMeshes = Decode(otherSelection);
        Check(!ReferenceEquals(firstMeshes, otherMeshes) && ReferenceEquals(firstMeshes, Decode(selection.Materials.ToArray()))
            && otherMeshes.First(mesh => mesh.PartIndex == 0x300).MaterialTag == 0x80C4E690,
            "Shared model/config placements did not keep their separate selected-material identities.");
        int field = resource + 0x310;
        int materials = checked(field + 24 + (int)BinaryPrimitives.ReadInt64LittleEndian(changed.AsSpan(field + 8)));
        BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(materials + 3 * 4), 0x80C4E690);
        catalog.ReadOverride = tag => tag == 0x80C443F8 ? changed : null;
        var edited = loader.Load(0x80B82FE3).Instances.First(instance => instance.Mesh.TagHash == 0x80C4424F && instance.Mesh.PartIndex == 0x300);
        Check(edited.Mesh.MaterialTag == 0x80C4E690 && !ReferenceEquals(edited.Mesh, part.Mesh),
            "A reused scene loader retained an old selected material after an overlay edit.");
        catalog.ReadOverride = null;
        var reopened = loader.Load(0x80B82FE3).Instances.First(instance => instance.Mesh.TagHash == 0x80C4424F && instance.Mesh.PartIndex == 0x300);
        Check(reopened.Mesh.MaterialTag == 0x80C4425D && original.SequenceEqual(catalog.Read(0x80C443F8)),
            "Material overlay removal did not restore the original preview.");
        uint mapTag = reads.Single(tag => catalog.Read(tag) is var value && value.Length > 32
            && BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(12)) == 0x80809683
            && BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(ArrayData(value, 16))) == 0x80804BEE);
        byte[] invalidMap = catalog.Read(mapTag).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(invalidMap.AsSpan(ArrayData(invalidMap, 16) + 4), 999);
        catalog.ReadOverride = tag => tag == mapTag ? invalidMap : null;
        var unknown = loader.Load(0x80B82FE3);
        Check(unknown.Diagnostics.Any(message => message.Contains("authored fallback") && message.Contains("method 999"))
            && unknown.MaterialWarnings.Any(warning => warning.ConfigTag == 0x80C443F8 && warning.Message.Contains("method 999"))
            && unknown.Instances.First(instance => instance.Mesh.TagHash == 0x80C4424F && instance.Mesh.PartIndex == 0x300)
                .Mesh.MaterialTag == 0x80C4E690,
            "Unknown initial state was guessed or its fallback was not reported.");
        catalog.ReadOverride = null;
        byte[] absent = original.ToArray();
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteUInt32LittleEndian(absent.AsSpan(materials + 4 * i), uint.MaxValue);
        catalog.ReadOverride = tag => tag == 0x80C443F8 ? absent : null;
        var noMaterial = loader.Load(0x80B82FE3).Instances.First(instance => instance.Mesh.TagHash == 0x80C4424F && instance.Mesh.PartIndex == 0x300);
        Check(noMaterial.InitiallyVisible == false && noMaterial.InitialVisibilityReason == "No active material",
            "Known native no-draw material retained fallback geometry by default.");
        catalog.ReadOverride = null;
        Console.WriteLine("entity_materials_owned config=80C443F8 part=300 selected=80C4425D overlay=refreshed source=unchanged");
    }

    /** Without positive admitted weight only parameter filters apply; a preview adds regions. */
    public static void Variants(string gameRoot)
    {
        const uint entity = 0x80C19B1F, config = 0x80FEE1D1, component = 0x80C19B23, preview = 0xBEF7FFEC;
        using var catalog = PackageCatalog.Open(gameRoot);
        EntityInitialState Read(uint variant = 0) => new EntityInitialStateReader(catalog).Read(entity, config, previewVariant: variant);
        var native = Read();
        Check(native.Unresolved is null && native.Enabled(44) == true && native.Enabled(57) == false
            && native.Enabled(1) == true && native.EveryVariantDisables(44) && !native.EveryVariantDisables(1)
            && native.Variants.Count == 8 && native.Variants.Contains(preview),
            "The Centurion native default did not keep the parameter filters without a variant: " + native.Unresolved);
        var chosen = Read(preview);
        Check(chosen.Unresolved is null && chosen.Enabled(44) == false && chosen.Enabled(42) == false
            && chosen.Enabled(6) == true && chosen.DisabledByEveryVariant is null,
            "The preview variant did not apply its region filters: " + chosen.Unresolved);

        var loader = new AssetSceneLoader(catalog);
        var scene = loader.Load(entity);
        var decal = scene.Instances.Single(instance => instance.Mesh.TagHash == 0x80FEE1D0 && instance.Mesh.PartIndex == 13328);
        Check(decal.InitiallyVisible == true && decal.InitialVisibilityReason?.Contains("Every variant disables") == true
            && scene.EntityVariants.Count == 8 && scene.PreviewVariant == 0
            && scene.Diagnostics.Any(message => message.Contains("no variant with positive weight"))
            && !scene.Diagnostics.Any(message => message.Contains("authored fallback")),
            "The Centurion native default lost its variant explanation.");
        var spawned = loader.Load(entity, new SceneLoadOptions { PreviewVariant = preview });
        var hidden = spawned.Instances.Single(instance => instance.Mesh.TagHash == 0x80FEE1D0 && instance.Mesh.PartIndex == 13328);
        var shown = spawned.Instances.Where(instance => instance.Mesh.TagHash == 0x80FEE1D0 && instance.Mesh.LogicalPartIndex == 6).ToArray();
        Check(hidden.InitiallyVisible == false && shown.Length != 0 && shown.All(instance => instance.InitiallyVisible == true)
            && spawned.PreviewVariant == preview
            && !spawned.Diagnostics.Any(message => message.Contains("no variant with positive weight")),
            "The preview variant did not hide its unselected decal or keep its enabled parts.");
        int nativeOff = InactiveBodies(scene), previewOff = InactiveBodies(spawned);
        Check(nativeOff != previewOff, "The preview variant did not reach the collision initial state.");
        Console.WriteLine($"entity_variant_collision entity=80C19B1F native_inactive={nativeOff} preview_inactive={previewOff}");

        // Edited rows: no labels admit every row, so the weights alone pick the outcome.
        byte[] original = catalog.Read(component);
        int header = Enumerable.Range(4, original.Length - 20).Single(at => U32(original, at - 4) == 0x80809FBD
            && U32(original, at + 8) == 0x808038A6);
        int field = Enumerable.Range(0, original.Length - 16).Single(at => I64(original, at) == I64(original, header)
            && at + 8 + I64(original, at + 8) == header);
        EntityInitialState Edited(params float[] weights)
        {
            byte[] changed = original.ToArray();
            BinaryPrimitives.WriteInt64LittleEndian(changed.AsSpan(field), weights.Length);
            BinaryPrimitives.WriteInt64LittleEndian(changed.AsSpan(header), weights.Length);
            for (int i = 0; i < weights.Length; i++)
            {
                int row = header + 16 + i * 48;
                BinaryPrimitives.WriteSingleLittleEndian(changed.AsSpan(row), weights[i]);
                changed.AsSpan(row + 16, 16).Clear();
            }
            catalog.ReadOverride = tag => tag == component ? changed : null;
            try { return Read(); }
            finally { catalog.ReadOverride = null; }
        }
        var zero = Edited(0);
        Check(zero.Unresolved is null && zero.Enabled(44) == true && zero.DisabledByEveryVariant is not null,
            "A zero-weight admitted row applied its region filters.");
        var single = Edited(1);
        Check(single.Unresolved is null && single.Enabled(44) == false && single.DisabledByEveryVariant is null,
            "The one admitted positive row was not chosen.");
        Check(Edited(1, 1).Unresolved == "Initial variant is a weighted random choice.",
            "Two admitted weighted rows were not reported as a random choice.");
        Console.WriteLine("entity_variants entity=80C19B1F native=parameters_only preview=BEF7FFEC part_13328=hidden zero_weight=native random=explicit");

        int InactiveBodies(SceneDocument document)
        {
            var collision = new CollisionSceneLoader(catalog).Load(document);
            Check(collision.Entities.All(entity => entity.InitialStateError is null),
                "The Centurion collision initial state did not resolve.");
            return collision.Placements.Count(placement => placement.InitiallyEnabled == false);
        }
        static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
        static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
    }

    /** Post-init methods, the runtime parameter and part flag 0x40 follow the native rules. */
    public static void NativeRules(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        EntityInitialState Read(uint entity, uint config, uint tag = 0, int offset = -1) =>
            new EntityInitialStateReader(catalog).Read(entity, config, tag, offset);
        bool Only(EntityInitialState state, int[] off) => state.EnabledParts is { } parts
            && Enumerable.Range(0, parts.Length).All(i => parts[i] == !off.Contains(i));

        var health = Read(0x80B581ED, 0x80B59FB1);
        Check(health.Unresolved is null && new[] { 2, 3, 5, 6, 7, 8, 10 }.All(i => health.Enabled(i) == false)
            && new[] { 0, 1, 4, 9 }.All(i => health.Enabled(i) == true),
            "The health-region method did not filter its regions: " + health.Unresolved);
        var constant = Read(0x80B8127E, 0x80BC1282);
        Check(constant.Unresolved is null && Only(constant, [2]), "The 0x80803B73 filter did not disable logical 2 only: "
            + constant.Unresolved);
        var allOn = Read(0x80B58D0E, 0x80C70010);
        Check(allOn.Unresolved is null && Only(allOn, []), "0x80B58D0E did not resolve with every part on: " + allOn.Unresolved);
        Check(Read(0x80B468C9, 0x815B9522).Unresolved == "Variant parameter needs player state.",
            "The runtime parameter kept its old message.");

        // Only the hull part 0x4D0 and its three coarser levels carry flag 0x40 in this model.
        var loader = new AssetSceneLoader(catalog) { IncludeLowerLods = true };
        var skiff = loader.Load(0x80BFAA54).Instances.Where(instance => instance.Mesh.TagHash == 0x80FC77D2).ToArray();
        var flagged = skiff.Where(instance => instance.Mesh.PartIndex is 0x4D0 or 0x4F0 or 0x510 or 0x530).ToArray();
        Check(flagged.Length == 4 && flagged.All(instance => instance.InitiallyVisible == false
                && instance.InitialVisibilityReason == MeshData.HiddenPartReason)
            && skiff.Except(flagged).All(instance => instance.InitialVisibilityReason != MeshData.HiddenPartReason),
            "The skiff hull parts with flag 0x40 are drawn, or another part took their reason.");
        var other = loader.Load(0x80C0C1EC).Instances.Single(instance => instance.Mesh.TagHash == 0x80F2F24E
            && instance.Mesh.PartIndex == 0x4A0);
        Check(other.InitiallyVisible == false && other.InitialVisibilityReason == MeshData.HiddenPartReason,
            "Part 0x4A0 of 0x80C0C1EC has flag 0x40 and is drawn.");
        Console.WriteLine("entity_native_rules health=80B581ED constant=80B8127E runtime=needs_player_state flag_0x40=hidden");
    }

    /** The spawn index groups every authored row; a spawn input hides what its variant hides. */
    public static void Spawns(string gameRoot)
    {
        const uint entity = 0x80C19B1F, config = 0x80FEE1D1, model = 0x80FEE1D0;
        using var catalog = PackageCatalog.Open(gameRoot);
        var index = AuthoredSpawnIndex.Build(catalog);
        var centurion = index.For(entity);
        Check(centurion.Count == 17 && centurion.Max(spawn => spawn.RowCount) == 639
            && centurion.Single(spawn => spawn.RowCount == 639).First == new SpawnInput(0x80B3C4D6, 0x8B0),
            "The Centurion does not have 17 spawn inputs with 639 rows in the top one.");
        Check(centurion.Zip(centurion.Skip(1)).All(pair => pair.First.First.Tag < pair.Second.First.Tag
            || pair.First.First.Tag == pair.Second.First.Tag && pair.First.First.Offset < pair.Second.First.Offset),
            "Spawn groups are not ordered by their first row.");

        var top = new EntityInitialStateReader(catalog).Read(entity, config, 0x80B3C4D6, 0x8B0);
        int[] off = [0, 2, 3, 4, 5, 7, 8, 10, 11, 16, 17, 18, 21, 22, 23, 24, .. Enumerable.Range(26, 14),
            .. Enumerable.Range(41, 9), 52, 54, .. Enumerable.Range(56, 11), .. Enumerable.Range(68, 14)];
        Check(top.Unresolved is null && off.All(i => top.Enabled(i) == false) && new[] { 1, 6, 67 }.All(i => top.Enabled(i) == true)
            && top.SelectedVariant == 0xBEF7FFEC, "The top Centurion spawn did not give its native part state: " + top.Unresolved);
        var knight = new EntityInitialStateReader(catalog).Read(0x80F586B0, 0x80F583E6, 0x815023A4, 0x8B0);
        Check(knight.Unresolved is null && new[] { 0, 1, 2, 4, 12, 13, 18 }.All(i => knight.Enabled(i) == true)
            && new[] { 5, 6, 9 }.All(i => knight.Enabled(i) == false),
            "The Knight spawn did not give its native part state: " + knight.Unresolved);

        // The asset view opens on the first authored spawn; the native default is one choice away.
        var loader = new AssetSceneLoader(catalog);
        MeshInstance Decal(SceneDocument scene) =>
            scene.Instances.Single(instance => instance.Mesh.TagHash == model && instance.Mesh.PartIndex == 13328);
        var first = loader.Load(entity, new SceneLoadOptions { AuthoredSpawns = centurion, PreviewSpawn = centurion[0].First });
        Check(Decal(first).InitiallyVisible == false && first.PreviewSpawn == centurion[0].First
            && first.SpawnChoices.Count == 17 && first.SpawnChoices.All(choice => choice.Unresolved is null),
            "The first Centurion spawn did not hide the floating decal or list its 17 inputs.");
        var native = loader.Load(entity, new SceneLoadOptions { AuthoredSpawns = centurion });
        Check(Decal(native).InitiallyVisible == true && native.PreviewSpawn is null
            && Decal(native).InitialVisibilityReason?.Contains("Every variant disables") == true,
            "Choosing the native default did not restore the old state.");
        Console.WriteLine($"authored_spawns rows={index.RowCount} entities={index.EntityCount} unread={index.UnreadTagCount} "
            + $"build_ms={index.BuildTime.TotalMilliseconds:F0} centurion_inputs={centurion.Count} "
            + $"first={centurion[0].First.Tag:X8}+{centurion[0].First.Offset:X} part_13328=hidden native=restored");
    }

    private static int ArrayData(byte[] bytes, int field) => checked(field + 24
        + (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field + 8)));

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid material conditions were accepted.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
