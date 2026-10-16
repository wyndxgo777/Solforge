using System.Buffers.Binary;
using System.Diagnostics;
using SunriseStudio.Packages;

/** Checks that installed component and category refreshes keep previous snapshots and scan only relevant payloads. */
internal static class ComponentRefreshChecks
{
    /** Times a full component load, the cost a refresh avoids. */
    internal static void Baseline(string game)
    {
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        var watch = Stopwatch.StartNew();
        var names = new GameNames(catalog);
        double stringsMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var index = names.LoadComponents();
        Console.WriteLine($"component_baseline strings_ms={stringsMs:F1} components_ms={watch.Elapsed.TotalMilliseconds:F1} configs={index.ConfigCount} entities={index.EntityCount}");
    }

    /** The installed fixture changes only an in-memory config overlay and restores it afterward. */
    internal static void Run(string game)
    {
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        var watch = Stopwatch.StartNew();
        var names = new GameNames(catalog);
        double stringsMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var index = names.LoadComponents();
        Console.WriteLine($"component_refresh strings_ms={stringsMs:F1} components_ms={watch.Elapsed.TotalMilliseconds:F1} configs={index.ConfigCount} entities={index.EntityCount}");
        uint entity = 0, config = 0;
        byte[]? original = null;
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == 0x80809C0F))
        {
            if (index.EntityKind(entry.TagHash) != "Prop") continue;
            uint[] configs = ComponentIndex.EntityConfigs(catalog.Read(entry.TagHash));
            if (configs.Length != 1 || index.PrimaryClass(configs[0]) != ComponentRoles.ModelComponent) continue;
            entity = entry.TagHash; config = configs[0]; original = catalog.Read(config); break;
        }
        Check(original is not null, "No installed single-config Prop fixture was found.");
        byte[] edited = original!.ToArray();
        int primary = checked((int)(16 + BinaryPrimitives.ReadInt64LittleEndian(edited.AsSpan(16))));
        BinaryPrimitives.WriteUInt32LittleEndian(edited.AsSpan(primary - 4), ComponentRoles.LightComponent);
        catalog.ReadOverride = tag => tag == config ? edited.ToArray() : null;
        long reads = catalog.ReadStatistics.EntryReads;
        watch.Restart();
        var refreshed = names.RefreshComponents([config], out var affected);
        double elapsed = watch.Elapsed.TotalMilliseconds;
        long payloads = catalog.ReadStatistics.EntryReads - reads;
        Check(refreshed.Components!.EntityKind(entity) == "Light" && index.EntityKind(entity) == "Prop",
            "Installed category refresh did not preserve its previous snapshot.");
        Check(affected.Contains(entity) && affected.Contains(config) && payloads < 64,
            "Installed component refresh scanned unrelated payloads.");
        catalog.ReadOverride = null;
        var restored = refreshed.RefreshComponents([config], out _);
        Check(restored.Components!.EntityKind(entity) == "Prop" && catalog.Read(config).AsSpan().SequenceEqual(original),
            "Installed overlay revert did not restore the original category and bytes.");
        Console.WriteLine($"component_refresh entity={entity:X8} config={config:X8} affected={affected.Count} reads={payloads} elapsed_ms={elapsed:F2} edit=pass revert=pass originals=unchanged");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
