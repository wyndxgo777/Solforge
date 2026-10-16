using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Map memory: the native plan only while it draws, release on close, and the get_editor_state memory breakdown. */
public sealed partial class StudioForm
{
    private WorldDocument? memoryWorld;
    // The scene a native plan was last requested for, so a failed preparation is not retried every frame.
    private SceneDocument? nativeRequestedFor;

    /**
     * Runs every frame after AdoptDocument. Below High, or with game shaders off, nothing draws the native plan, so the
     * map holds none: raising the quality prepares it, lowering it frees it. A closed or replaced map releases the
     * caches that pointed at it and compacts the heap so the RAM goes back to Windows.
     */
    private void SyncWorldMemory()
    {
        bool wanted = settings.GameShaders && settings.ViewportQuality >= ViewportQuality.High;
        workspace.NativeWorldWanted = wanted;
        var world = workspace.World;
        if (workspace.IsBusy || viewport is null) return;
        if (!ReferenceEquals(memoryWorld, world))
        {
            bool closed = memoryWorld is not null && world is null;
            bool replaced = memoryWorld is not null && world is not null && memoryWorld.Map.Tag != world.Map.Tag;
            memoryWorld = world;
            if (closed || replaced)
            {
                // Background indexes keyed on the old world keep all of its scene alive until replaced.
                if (aiEntries is { } entries && !ReferenceEquals(entries.World, world)) aiEntries = null;
                if (aiMission is { } mission && !ReferenceEquals(mission.World, world)) aiMission = null;
                aiHighlights.Clear();
                if (!ReferenceEquals(nameKeyWorld, inspectionWorld)) { nameKeys.Clear(); nameKeyWorld = null; }
                if (closed) { viewport.ReleaseFallbackCache(); workspace.TrimMemory(); }
                else StudioWorkspace.CompactHeap();
            }
        }
        if (world is null || !ReferenceEquals(viewport.Scene, world.Scene)) return;
        bool planned = workspace.NativePlan is not null && ReferenceEquals(workspace.NativePlanScene, world.Scene);
        // A load that already tried (and reported an error) is not retried; dropping below High clears the error.
        if (wanted && !planned && !viewport.NativeSceneReady && workspace.NativeError.Length == 0
            && !ReferenceEquals(nativeRequestedFor, world.Scene))
        {
            nativeRequestedFor = world.Scene;
            workspace.Run(() => workspace.PrepareNative());
        }
        else if (!wanted && (planned || viewport.NativeSceneReady || workspace.NativeError.Length > 0))
        {
            bool held = planned || viewport.NativeSceneReady;
            viewport.ReleaseNativeScene();
            workspace.ReleaseNative();
            nativeRequestedFor = null;
            if (held) StudioWorkspace.CompactHeap();
        }
    }

    /** get_editor_state memory: where the RAM goes, in MiB, measured from the live objects rather than estimated. */
    private JsonObject AiMemoryBreakdown()
    {
        static double MiB(long bytes) => Math.Round(bytes / 1048576.0, 1);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        var heaps = new JsonObject();
        string[] generations = ["gen0", "gen1", "gen2", "largeObjects", "pinnedObjects"];
        for (int index = 0; index < gc.GenerationInfo.Length && index < generations.Length; index++)
            heaps[generations[index]] = new JsonObject
            {
                ["size"] = MiB(gc.GenerationInfo[index].SizeAfterBytes),
                ["fragmented"] = MiB(gc.GenerationInfo[index].FragmentationAfterBytes)
            };
        var result = new JsonObject
        {
            ["workingSet"] = MiB(process.WorkingSet64), ["privateBytes"] = MiB(process.PrivateMemorySize64),
            ["managedHeap"] = MiB(GC.GetTotalMemory(false)), ["managedCommitted"] = MiB(gc.TotalCommittedBytes),
            ["managedFragmented"] = MiB(gc.FragmentedBytes), ["heaps"] = heaps,
            ["textureCache"] = MiB(workspace.TextureCacheBytes),
            ["packageBlockCache"] = MiB(workspace.Catalog?.CachedBlockBytes ?? 0),
            ["quality"] = settings.ViewportQuality.ToString(), ["lowMemoryMode"] = workspace.LowMemoryMode
        };
        if (workspace.ActiveScene is { } scene) result["scene"] = SceneMemory(scene);
        var plan = workspace.NativePlan;
        result["nativePlan"] = plan is null ? null : new JsonObject
        {
            ["parts"] = plan.PreparedPartCount, ["geometryGpu"] = MiB(plan.GeometryBytes),
            ["texturesGpu"] = MiB(plan.TextureBytes), ["texturesStillInRam"] = MiB(plan.Textures.Values.Sum(t => t.ByteCount))
        };
        if (viewport is { } view)
        {
            var (fallbackMeshes, fallbackBytes) = view.FallbackGeometry;
            result["viewport"] = new JsonObject
            {
                ["nativeRenderer"] = view.NativeSceneReady,
                ["fallbackMeshes"] = fallbackMeshes, ["fallbackGeometryGpu"] = MiB(fallbackBytes),
                ["fallbackTextures"] = view.FallbackTextureCount, ["fallbackTexturesGpu"] = MiB(view.FallbackTextureBytes),
                ["collisionLoaded"] = view.Collision is not null
            };
        }
        result["aiObjectIndexRows"] = aiEntries is { Rows.IsCompletedSuccessfully: true } rows ? (JsonNode)rows.Rows.Result.Length : null;
        result["worldObjects"] = workspace.World?.Objects.Count;
        return result;

        static JsonObject SceneMemory(SceneDocument scene)
        {
            var meshes = new HashSet<MeshData>(ReferenceEqualityComparer.Instance);
            var arrays = new HashSet<object>(ReferenceEqualityComparer.Instance);
            long instanceRows = 0, nativeBytes = 0, headerBytes = 0;
            foreach (var instance in scene.Instances)
            {
                meshes.Add(instance.Mesh);
                if (MemoryMarshal.TryGetArray(instance.NativeInstanceData, out var segment) && segment.Array is { } row
                    && arrays.Add(row)) instanceRows += row.Length;
            }
            var lods = new SortedDictionary<int, (int Meshes, long Bytes)>();
            long vertexBytes = 0, indexBytes = 0, sourceIndexBytes = 0;
            int vertexSize = Unsafe.SizeOf<SceneVertex>();
            foreach (var mesh in meshes)
            {
                // Stage copies share the colour entry's vertex and index arrays; count each array once.
                long own = 0;
                if (arrays.Add(mesh.Vertices)) own += (long)mesh.Vertices.Length * vertexSize;
                vertexBytes += own;
                if (arrays.Add(mesh.Indices)) { indexBytes += mesh.Indices.LongLength * 4; own += mesh.Indices.LongLength * 4; }
                if (arrays.Add(mesh.SourceVertexIndices))
                { sourceIndexBytes += mesh.SourceVertexIndices.LongLength * 4; own += mesh.SourceVertexIndices.LongLength * 4; }
                var lod = lods.GetValueOrDefault(mesh.LodLevel);
                lods[mesh.LodLevel] = (lod.Meshes + 1, lod.Bytes + own);
                if (mesh.Native is not { } native) continue;
                foreach (var stream in native.VertexStreams)
                    if (arrays.Add(stream.Data)) nativeBytes += stream.Data.Length;
                if (native.SkinWeightStream is { } skin && arrays.Add(skin.Data)) nativeBytes += skin.Data.Length;
                if (arrays.Add(native.IndexData)) nativeBytes += native.IndexData.Length;
                if (native.SourceHeader is { } header && arrays.Add(header)) headerBytes += header.Length;
            }
            var byLod = new JsonObject();
            foreach (var (level, value) in lods)
                byLod[level.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    new JsonObject { ["meshes"] = value.Meshes, ["decoded"] = MiB(value.Bytes) };
            return new JsonObject
            {
                ["instances"] = scene.Instances.Count, ["markers"] = scene.Markers.Count, ["uniqueMeshes"] = meshes.Count,
                ["decodedVertices"] = MiB(vertexBytes), ["decodedIndices"] = MiB(indexBytes),
                ["sourceVertexIndices"] = MiB(sourceIndexBytes), ["nativeBuffers"] = MiB(nativeBytes),
                ["nativeHeaders"] = MiB(headerBytes), ["instanceRows"] = MiB(instanceRows),
                ["byLod"] = byLod,
                // Object headers and fields only; a MeshInstance is about 300 bytes before its arrays.
                ["instanceObjectsEstimate"] = MiB(scene.Instances.Count * 300L + scene.Markers.Count * 400L)
            };
        }
    }
}
