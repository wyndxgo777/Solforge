using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SunriseStudio.Formats;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

/** Benchmark, not a pass/fail test: exports a legacy map as a GLB scene and reports size and memory use, bailing out first if the machine lacks headroom. */
internal static class WorldSceneBenchmark
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    /** Budget estimates precede legacy expansion; both writers receive the same decoded scene. */
    internal static void Run(string game, uint tag, string output, bool preflightOnly)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        var clock = Stopwatch.StartNew();
        var scene = new AssetSceneLoader(catalog) { IncludeLowerLods = true }.LoadMap(tag);
        double load = clock.Elapsed.TotalSeconds;
        var meshes = scene.Instances.Select(instance => instance.Mesh).Distinct(ReferenceEqualityComparer.Instance).Cast<MeshData>().ToArray();
        long vertices = scene.Instances.Sum(instance => (long)instance.Mesh.Vertices.Length);
        long indices = scene.Instances.Sum(instance => (long)instance.Mesh.Indices.Length);
        long uniqueVertices = meshes.Sum(mesh => (long)mesh.Vertices.Length), uniqueIndices = meshes.Sum(mesh => (long)mesh.Indices.Length);
        long legacyBytes = checked(vertices * 32 + indices * 4), sharedBytes = checked(uniqueVertices * 48 + uniqueIndices * 4 + scene.Instances.Count * 128L);
        var materialTags = meshes.Select(mesh => mesh.MaterialTag).Where(value => value is not (0 or uint.MaxValue)).Distinct().ToArray();
        var textures = new Dictionary<uint, MaterialTextureBinding>();
        var reader = new GameMaterialReader(catalog); List<string> diagnostics = [];
        foreach (uint material in materialTags)
            try
            {
                foreach (var texture in reader.Read(material).Textures.Where(texture => texture.Stage == GameShaderStage.Pixel))
                    textures.TryAdd(texture.TextureTag, texture);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException)
            { diagnostics.Add($"{material:X8}: {error.Message}"); }
        long imageBytes = textures.Values.Where(texture => texture.ArraySize == 1 && texture.Depth == 1)
            .Sum(texture => checked((long)texture.Width * texture.Height * 4));
        long free = new DriveInfo(Path.GetPathRoot(output)!).AvailableFreeSpace;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new InvalidOperationException("Cannot read benchmark memory headroom.");
        using var process = Process.GetCurrentProcess();
        var preflight = new
        {
            Map = tag.ToString("X8"), scene.Name, LoadSeconds = load, Parts = scene.Instances.Count,
            Markers = scene.Markers.Count, MeshObjects = meshes.Length, Vertices = vertices, Indices = indices,
            UniqueVertices = uniqueVertices, UniqueIndices = uniqueIndices, Materials = materialTags.Length,
            Textures = textures.Count, TextureRgbaBytes = imageBytes, LegacyBinaryBytes = legacyBytes,
            SharedBinaryUpperEstimate = sharedBytes, LegacyExtraMemoryEstimate = legacyBytes * 5,
            AvailableDiskBytes = free, TotalRamBytes = memory.TotalPhysical, FreeRamBytes = memory.AvailablePhysical,
            process.WorkingSet64, scene.DecodeErrorCount, scene.UnsupportedCount, Diagnostics = diagnostics
        };
        File.WriteAllText(Path.Combine(output, "preflight.json"), JsonSerializer.Serialize(preflight, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"world_preflight tag={tag:X8} parts={scene.Instances.Count} meshes={meshes.Length} legacy_bytes={legacyBytes} shared_upper={sharedBytes} images_rgba={imageBytes} load_s={load:F3}");
        if (preflightOnly) return;
        if (legacyBytes > 1024L * 1024 * 1024 || legacyBytes * 3 + sharedBytes * 2 + imageBytes > free - 384L * 1024 * 1024
            || memory.AvailablePhysical < (ulong)(legacyBytes * 5 + 1024L * 1024 * 1024))
            throw new InvalidOperationException("Legacy map baseline exceeds the bounded memory or disk budget; preflight was preserved.");
        string legacy = Path.Combine(output, "legacy.glb");
        clock.Restart(); GltfInterchange.Write(legacy, scene); double before = clock.Elapsed.TotalSeconds;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        clock.Restart();
        var compact = WorldSceneExport.Export(catalog, scene, Path.Combine(output, "geometry"), new(IncludeMaterials: false));
        double after = clock.Elapsed.TotalSeconds;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        clock.Restart();
        var complete = WorldSceneExport.Export(catalog, scene, Path.Combine(output, "materials"));
        double full = clock.Elapsed.TotalSeconds;
        process.Refresh();
        var result = new
        {
            Preflight = preflight,
            Baseline = new { Format = "GLB geometry only; baked instances", Seconds = before, Bytes = new FileInfo(legacy).Length },
            SharedGeometry = new { Seconds = after, Bytes = Size(Path.Combine(output, "geometry")), compact.Manifest.Statistics },
            SharedMaterials = new { Seconds = full, Bytes = Size(Path.Combine(output, "materials")), complete.Manifest.Statistics },
            PeakWorkingSet = process.PeakWorkingSet64,
            PeakWorkingSetValid = true,
            Scope = "All decoded parts and available visibility metadata from one map root, including lower LODs; no frustum filtering. GLB baseline has no textures/material graphs."
        };
        File.WriteAllText(Path.Combine(output, "export-comparison.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"world_export_compare before_s={before:F3} geometry_s={after:F3} materials_s={full:F3} before_bytes={new FileInfo(legacy).Length} geometry_bytes={Size(Path.Combine(output, "geometry"))} materials_bytes={Size(Path.Combine(output, "materials"))}");
    }

    private static long Size(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
}
