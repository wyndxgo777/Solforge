using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** Runtime half of NativeSceneRenderer: uploads prototypes once and adds instance slots without new uploads. */
public sealed partial class NativeSceneRenderer
{
    private sealed record RuntimePrototype(IReadOnlyList<DrawGroup> Groups);
    private long runtimeTextureBytes, runtimeGeometryBytes;
    private readonly List<int> recordedStaticVisible = [];

    /** Prototype resources share this renderer's caches and never replace existing buffers. */
    internal object UploadRuntimePrototype(NativeScenePlan plan)
    {
        const long textureLimit = 512L * 1024 * 1024, geometryLimit = 256L * 1024 * 1024;
        long texturesNeeded = plan.Textures.Where(pair => !textures.ContainsKey(pair.Key))
            .Sum(pair => pair.Value.Subresources.Sum(part => (long)part.Data.Length));
        long geometryNeeded = plan.Groups.DistinctBy(group => group.GeometryKey)
            .Where(group => !geometry.ContainsKey(group.GeometryKey))
            .Sum(group => group.Mesh.VertexStreams.Sum(stream => (long)stream.Data.Length) + group.Mesh.IndexData.Length);
        if (texturesNeeded > textureLimit - runtimeTextureBytes || geometryNeeded > geometryLimit - runtimeGeometryBytes)
            throw new NotSupportedException("Live prototype GPU budget is full; reload the scene to release unused classes.");
        UploadTextures(plan);
        runtimeTextureBytes += texturesNeeded; runtimeGeometryBytes += geometryNeeded;
        var prepared = new List<DrawGroup>();
        foreach (var source in plan.Groups)
        {
            // Live slots have no caster list; their shadow_generate parts are not uploaded.
            if (source.RenderStage == NativeScenePlan.ShadowStage) continue;
            if (source.Material.ReadsWaterObject || source.Material.ReadsAdvancedScope || source.Kind == NativeMeshKind.Terrain
                || source.Kind == NativeMeshKind.Static && source.Parts.Any(part => part.BindingSource != NativeBindingSource.IsolatedPreview))
            { AddDiagnostic($"Live material 0x{source.Mesh.MaterialTag:X8} uses an unsupported placement binding."); continue; }
            if (UploadGroup(source, plan.Reader) is { } group) prepared.Add(group);
        }
        return new RuntimePrototype(prepared);
    }

    /** Slots append stable handles once; later generations reuse the same shared meshes and materials. */
    internal IReadOnlyList<(MeshInstance Instance, int Handle)> AddRuntimeSlot(object prototype,
        IReadOnlyDictionary<MeshInstance, MeshInstance> replacements)
    {
        var prepared = (RuntimePrototype)prototype;
        int firstGroup = groups.Count, firstHandle = handles.Count;
        foreach (var template in prepared.Groups)
        {
            var parts = template.Source.Parts.Select(part =>
            {
                var instance = replacements[part.Instance];
                return part with { Instance = instance, Constants = RuntimeConstants(instance, template.Source.Material.Palette) };
            }).ToArray();
            var source = template.Source with { Parts = parts };
            groups.Add(new(source, template.Pipeline, template.Mesh, template.Material, template.Scalars,
                template.Pass, template.Dyes) { Runtime = true });
        }
        AppendGroupHandles(firstGroup);
        return handles.Skip(firstHandle).Select((item, index) =>
            (item.Group.Source.Parts[item.Part].Instance, firstHandle + index)).ToArray();
    }

    private static Vector4[] RuntimeConstants(MeshInstance instance, NativeEntityPalette palette) =>
        instance.Mesh.Native?.Kind == NativeMeshKind.Static ? NativeAssetBindings.CreateStatic(instance).InstanceConstants
        : palette == NativeEntityPalette.None ? AssetSceneLoader.ReadNativeEntityConstants(instance)
        : NativeEntityPalettes.Build(instance, palette);

    /** Runtime visibility changes do not force another upload of authored opaque draws. */
    private bool StaticVisibilityChanged(List<int> visible, bool stagesChanged)
    {
        var source = CollectionsMarshal.AsSpan(visible);
        if (staticVisible.Length < source.Length) staticVisible = new int[source.Length];
        var perFrame = handlePerFrame;
        int count = 0;
        foreach (int handle in source) if (!perFrame[handle]) staticVisible[count++] = handle;
        var current = staticVisible.AsSpan(0, count);
        bool changed = stagesChanged || !current.SequenceEqual(CollectionsMarshal.AsSpan(recordedStaticVisible));
        if (changed) { recordedStaticVisible.Clear(); recordedStaticVisible.AddRange(current); }
        return changed;
    }

    // The static handles of the frame being checked; scratch for StaticVisibilityChanged.
    private int[] staticVisible = [];
}
