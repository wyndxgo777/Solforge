using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using static Checks;

/** Checks that collision initial-state masks (guards, hobs, stored variants) resolve from installed data, including dependencies on interfaces and spawn sources. */
internal static class InitialStateChecks
{
    /** Native label matching independently pins the initial masks of both owned rigs. */
    internal static void Owned(PackageCatalog catalog)
    {
        var reads = new HashSet<uint>();
        var reader = new CollisionInitialStateReader(catalog) { Reads = reads };
        var guard = reader.Read(0x80C0FA98, 0x80FE2D67);
        Check(guard.EnabledParts is { Length: 13 }
            && Disabled(guard).SequenceEqual([1, 2, 4, 5, 10, 11, 12]), $"Guard initial mask: {guard.Unresolved}");
        var hob = reader.Read(0x80B82FE3, 0x80C443FA);
        Check(hob.EnabledParts is { Length: 11 } && Disabled(hob).SequenceEqual([1, 2, 8, 10]),
            $"Hob initial mask: {hob.Unresolved}");
        var placed = reader.Read(0x80C0FA98, 0x80FE2D67, 0x80B48DDD, 0x8B0);
        Check(placed.EnabledParts is not null && Disabled(placed).SequenceEqual(Disabled(guard)),
            $"Stored berth variant mask: {placed.Unresolved}");
        Check(reads.Contains(0x815B886C) && reads.Contains(0x815B889A) && reads.Contains(0x80B48DDD),
            "Initial-state dependencies omit an interface or spawn source.");

        var loader = new CollisionSceneLoader(catalog);
        var original = loader.Load(new SceneDocument { TagHash = 0x80C0FA98 });
        Check(original.Placements.Count == 46 && original.Placements.Count(p => p.InitiallyEnabled == true) == 27,
            "Guard bodies were dropped or the displayed initial mask differs from native filters.");
        var hobScene = loader.Load(new SceneDocument { TagHash = 0x80B82FE3 });
        Check(hobScene.Placements.Count == 26 && hobScene.Placements.Count(p => p.InitiallyEnabled == true) == 22,
            "Hob bodies were dropped or the displayed initial mask differs from native filters.");

        var previous = catalog.ReadOverride;
        byte[] source = catalog.ReadOriginal(0x80B48DDD);
        byte[] changed = source.ToArray();
        int parameters = ArrayData(changed, 0x980 + 16);
        int parameterCount = checked((int)BitConverter.ToInt64(changed, 0x980 + 16));
        int value = Enumerable.Range(0, parameterCount).Select(i => parameters + 8 * i)
            .Single(row => BitConverter.ToUInt32(changed, row) == 0xB10F785D);
        BitConverter.GetBytes(0x9A3D2CCBu).CopyTo(changed, value + 4);
        try
        {
            catalog.ReadOverride = tag => tag == 0x80B48DDD ? changed : previous?.Invoke(tag);
            var other = reader.Read(0x80C0FA98, 0x80FE2D67, 0x80B48DDD, 0x8B0);
            Check(other.Enabled(10) == true && other.EnabledParts!.Count(v => v) == guard.EnabledParts!.Count(v => v) + 1,
                $"A stored variant override did not enable its own body group: {other.Unresolved}");
            byte[] invalidMap = catalog.ReadOriginal(0x815B886C).ToArray();
            BitConverter.GetBytes(999u).CopyTo(invalidMap, ArrayData(invalidMap, 16) + 4);
            catalog.ReadOverride = tag => tag == 0x815B886C ? invalidMap : previous?.Invoke(tag);
            var unresolved = reader.Read(0x80C0FA98, 0x80FE2D67);
            Check(unresolved.EnabledParts is null && unresolved.Unresolved?.Contains("method 999") == true,
                "An unknown native initializer produced a guessed mask.");
            var reopened = loader.Load(new SceneDocument { TagHash = 0x80C0FA98 });
            Check(reopened.Placements.Count == 46 && reopened.Placements.All(p => p.InitiallyEnabled is null),
                "Unresolved activation hid authored bodies or reused an old mask.");
        }
        finally { catalog.ReadOverride = previous; }
        Check(catalog.ReadOriginal(0x80B48DDD).SequenceEqual(source), "Initial-state preview changed the installed spawn.");
        Console.WriteLine("PASS collision-initial-state guard=27/46 hob=22/26 authored-variant=pass unresolved=visible rows=retained");
    }

    private static IEnumerable<int> Disabled(CollisionInitialState state) =>
        Enumerable.Range(0, state.EnabledParts!.Length).Where(i => !state.EnabledParts[i]);

    private static int ArrayData(byte[] source, int field) => checked(field + 24 + (int)BitConverter.ToInt64(source, field + 8));
}
